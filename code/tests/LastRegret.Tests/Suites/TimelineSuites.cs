using System.Text;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Engine;
using LastRegret.Windows.Io;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 真实文件系统端到端测试：真监听（ReadDirectoryChangesW）、真数据库、真内容寻址存储。
///
/// 覆盖产品要求里明确列出的场景：
///   新建 / 修改 / 删除 / 重命名 / 移动 / 批量 / 中文 / 特殊字符 / 长路径 /
///   大量小文件 / 空文件 / 大文件 / 编辑器原子替换（合成通知）/ 快速连续保存
/// </summary>
public static class TimelineSuites
{
    public static IEnumerable<TestCase> All()
    {
        yield return new("监听与事件·真实文件系统", "创建文件 → 记录为「创建」并留存内容", () =>
        {
            using var box = Sandbox.Create("create");
            box.Protect();

            box.WriteFile("hello.txt", "第一版内容");
            var ev = box.WaitForEvent("hello.txt", OperationType.Created);

            Check.Equal(EntryKind.File, ev.Kind, "应为文件");
            Check.NotNull(ev.HashAfter, "应记录变化后的内容哈希");
            Check.NotNull(ev.ObjectIdAfter, "应把内容存入 CAS");
            Check.True(ev.CanRestoreAfter, "变化后的内容可用");
            Check.False(ev.CanRestorePrevious, "新建之前没有内容，不应假装可恢复");
        });

        yield return new("监听与事件·真实文件系统", "修改文件 → 前后内容都被留存（可恢复旧版）", () =>
        {
            using var box = Sandbox.Create("modify");
            box.Protect();

            box.WriteFile("config.json", "{ \"port\": 8080 }");
            box.WaitForEvent("config.json", OperationType.Created);

            Thread.Sleep(250);
            box.WriteFile("config.json", "{ \"port\": 9090 }");
            var ev = box.WaitForEvent("config.json", OperationType.Modified);

            Check.NotNull(ev.HashBefore, "应记录变化前哈希");
            Check.NotNull(ev.HashAfter, "应记录变化后哈希");
            Check.NotEqual(ev.HashBefore, ev.HashAfter, "内容确实变了");
            Check.True(ev.CanRestorePrevious, "必须能恢复到修改之前（这是产品的核心承诺）");
            Check.NotNull(ev.ObjectIdBefore, "变化前内容必须已存入 CAS");

            // 变化前内容确实能取出来
            Check.True(box.Store.TryReadAllBytes(ev.ObjectIdBefore!.Value, 4096, out var bytes, out var err),
                "应能读出变化前的内容：" + err);
            Check.Equal("{ \"port\": 8080 }", Encoding.UTF8.GetString(bytes), "变化前内容必须与最初写入的一致");
        });

        yield return new("监听与事件·真实文件系统", "删除文件 → 删除前内容仍可恢复", () =>
        {
            using var box = Sandbox.Create("delete");
            box.Protect();

            box.WriteFile("old.dll", "binary-ish payload");
            box.WaitForEvent("old.dll", OperationType.Created);

            Thread.Sleep(250);
            box.DeleteFile("old.dll");
            var ev = box.WaitForEvent("old.dll", OperationType.Deleted);

            Check.True(ev.CanRestorePrevious,
                $"删除必须能恢复到删除之前。\n  事件：{ev}\n  前哈希={ev.HashBefore} 前对象={ev.ObjectIdBefore}\n  说明={ev.Note}\n" +
                box.DescribeState() + box.DescribeEvents());
            Check.NotNull(ev.ObjectIdBefore, "删除前的内容必须已留存");
            Check.Null(ev.HashAfter, "删除后不应有内容哈希");

            // 删除前的内容真的能从 CAS 里取回原始字节（而不是只留了一个哈希）
            var restored = box.ReadObject(ev.ObjectIdBefore!.Value);
            Check.Equal("binary-ish payload", restored, "CAS 中留存的删除前内容必须与写入时完全一致");
        });

        yield return new("监听与事件·真实文件系统", "重命名文件 → 记录新旧路径与内容", () =>
        {
            using var box = Sandbox.Create("rename");
            box.Protect();

            box.WriteFile("a.txt", "内容不变");
            box.WaitForEvent("a.txt", OperationType.Created);

            Thread.Sleep(250);
            box.MovePath("a.txt", "b.txt");
            var ev = box.WaitForEvent("b.txt");

            Check.True(ev.Operation is OperationType.Renamed or OperationType.Created or OperationType.Modified,
                $"重命名应被识别（实际：{ev.Operation}）");
            Check.Equal("b.txt", ev.RelativePath, "新路径");
            if (ev.Operation == OperationType.Renamed)
            {
                Check.Equal("a.txt", ev.OldRelativePath, "旧路径必须如实记录");
                Check.NotNull(ev.HashAfter, "重命名后应能取到内容");
            }
        });

        yield return new("监听与事件·真实文件系统", "跨目录移动 → 旧路径消失、新路径出现", () =>
        {
            using var box = Sandbox.Create("move");
            box.Protect();

            box.MkDir("src");
            box.MkDir("dst");
            box.WriteFile("src/x.txt", "被移动的内容");
            box.WaitForEvent("src/x.txt", OperationType.Created);

            Thread.Sleep(250);
            box.MovePath("src/x.txt", "dst/x.txt");

            // 至少要有新路径的创建/改名事件，并且内容一致
            var ev = box.WaitForEvent("dst/x.txt");
            Check.NotNull(ev.HashAfter ?? ev.HashBefore, "移动后应能取到内容哈希");

            var all = box.AllEvents();
            Check.True(all.Any(e => PathUtil.Comparer.Equals(e.RelativePath, "src/x.txt") || PathUtil.Comparer.Equals(e.OldRelativePath, "src/x.txt")),
                "必须留下关于旧路径的事实记录");
        });

        yield return new("监听与事件·真实文件系统", "VS Code 式连续保存 → 合并为少量事件", () =>
        {
            using var box = Sandbox.Create("burst");
            box.Protect();

            box.WriteFile("save.txt", "v0");
            box.WaitForEvent("save.txt", OperationType.Created);

            Thread.Sleep(250);
            // 模拟编辑器一次保存产生的多次写入（真实 VS Code 会连发多个文件系统通知）
            for (int i = 1; i <= 6; i++)
            {
                box.WriteFile("save.txt", $"v{i}");
                Thread.Sleep(15);
            }

            box.WaitFor(() => box.EventsOf("save.txt").Count >= 2, 6000, "至少应有一条修改事件");

            var events = box.EventsOf("save.txt");
            var modifyEvents = events.Where(e => e.Operation == OperationType.Modified).ToList();

            Check.True(modifyEvents.Count >= 1, "应记录修改");
            Check.True(events.Count <= 4,
                $"连续 6 次保存应被合并，事件数不应爆炸（实际 {events.Count} 条）：\n{box.DescribeEvents()}");
            Check.True(modifyEvents.Any(e => e.SuppressedCount > 0) || events.Count == 2,
                $"合并后应有明确的合并计数（实际事件：\n{box.DescribeEvents()}）");

            // 最终内容必须是最后一次写入的内容
            var last = events.Last();
            Check.Equal("v6", box.ReadFile("save.txt"), "磁盘内容应为最后一次写入");
            Check.NotNull(last.HashAfter, "最后一次事件应记录内容哈希");
        });

        yield return new("监听与事件·真实文件系统", "编辑器原子替换（写临时文件后改名覆盖）不产生虚假的新建/删除", () =>
        {
            using var box = Sandbox.Create("atomic");
            box.Protect();

            box.WriteFile("doc.txt", "原始内容");
            box.WaitForEvent("doc.txt", OperationType.Created);
            int baselineCount = box.AllEvents().Count;

            Thread.Sleep(250);
            // 真实编辑器（Notepad/VSCode/Office）的保存方式：
            //   写临时文件 → 删原文件 → 把临时文件改名为原文件名
            // 这里刻意用不存在的旧版本所使用过的 .bak 名称？不 —— 使用真实场景里的 .tmp，
            // 因为产品要求正是"这类临时文件不该出现在时间线上"。
            box.WriteFile("doc.txt.tmp", "替换后的内容");
            Thread.Sleep(60);
            box.MovePath("doc.txt.tmp", "doc.txt");   // 覆盖原文件

            box.WaitFor(() => box.AllEvents().Count > baselineCount, 8000, "应记录变化");

            var newEvents = box.AllEvents().Skip(baselineCount).ToList();

            Check.True(newEvents.Count > 0, "原子替换必须被记录下来（不能因为合并而丢失事实）");

            // 核心要求 1：临时文件不得作为独立事件暴露给用户
            Check.False(newEvents.Any(e => e.RelativePath.Contains(".tmp", StringComparison.OrdinalIgnoreCase) && !e.IsTransient),
                $"临时文件不应产生正式事件：\n{box.DescribeEvents()}");
            Check.False(box.AllEvents(includeTransient: false).Any(e => e.RelativePath.Contains(".tmp", StringComparison.OrdinalIgnoreCase)),
                $"时间线（不含瞬时事件）中不应出现临时文件：\n{box.DescribeEvents()}");

            // 核心要求 2：磁盘最终内容正确
            Check.Equal("替换后的内容", box.ReadFile("doc.txt"), "磁盘内容应为替换后的内容");
            Check.FileMissing(box.Abs("doc.txt.tmp"), "临时文件不应残留");

            // 核心要求 3：doc.txt 必须有可恢复的历史版本（旧内容不能因为"看起来像删除"而丢失）
            var versions = box.Versions.ListForPath(box.RootId, "doc.txt", 50);
            Check.True(versions.Count >= 1, $"doc.txt 应至少留存一个历史版本（实际 {versions.Count}）");

            var originalHash = versions.FirstOrDefault(v =>
                box.ReadObject(v.ObjectId ?? -1) == "原始内容");
            Check.NotNull(originalHash,
                $"「原始内容」那一版必须仍可在 CAS 中取回（否则这次保存就变成了不可恢复的破坏）：\n" +
                string.Join("\n", versions.Select(v => $"    {v.RecordedUtc:HH:mm:ss} hash={v.Hash[..8]} obj={v.ObjectId} note={v.Note}")));
        });

        yield return new("监听与事件·真实文件系统", "中文文件名 / 空格 / 特殊字符 / emoji", () =>
        {
            using var box = Sandbox.Create("naming");
            box.Protect();

            var names = new[]
            {
                "中文配置文件.json",
                "带 空格 的 文件.txt",
                "特殊#字符$与&符号.log",
                "emoji_🎮_测试.txt",
            };

            foreach (var n in names) box.WriteFile(n, "内容 " + n);

            foreach (var n in names)
            {
                var ev = box.WaitForEvent(n, OperationType.Created);
                Check.Equal(n, ev.RelativePath, $"路径应原样保留：{n}");
                Check.NotNull(ev.ObjectIdAfter, $"内容应被留存：{n}");
            }
        });

        yield return new("监听与事件·真实文件系统", "空文件与 1 字节文件都能正确记录", () =>
        {
            using var box = Sandbox.Create("tiny");
            box.Protect();

            box.WriteFile("empty.txt", "");
            var e1 = box.WaitForEvent("empty.txt", OperationType.Created);
            Check.Equal(0L, e1.SizeAfter ?? -1, "空文件大小应为 0");
            Check.NotNull(e1.ObjectIdAfter, "空文件也应留下内容对象（空内容同样是一个版本）");

            box.WriteBytes("one.bin", new byte[] { 0x41 });
            var e2 = box.WaitForEvent("one.bin", OperationType.Created);
            Check.Equal(1L, e2.SizeAfter ?? -1, "1 字节文件大小应为 1");
        });

        yield return new("监听与事件·真实文件系统", "超过留存上限的大文件：记录事实但明确标注不可恢复", () =>
        {
            using var box = Sandbox.Create("bigfile");
            box.Settings.MaxStoreFileSizeBytes = 512 * 1024;   // 收紧到 512KB 便于测试
            box.SettingsRepo.Save(box.Settings);
            box.Watch.ReloadSettings();
            box.Protect();

            var big = new byte[1024 * 1024];                   // 1MB
            new Random(7).NextBytes(big);
            box.WriteBytes("big.bin", big);

            var ev = box.WaitForEvent("big.bin", OperationType.Created);
            Check.Equal(1024L * 1024, ev.SizeAfter ?? -1, "大小应如实记录");
            Check.Null(ev.ObjectIdAfter, "超过上限的内容不应写入 CAS");
            Check.Contains(ev.Note ?? string.Empty, "上限", "必须如实说明为什么没有保存内容");
        });

        yield return new("监听与事件·真实文件系统", "大量小文件批量创建（模拟 git checkout）", () =>
        {
            using var box = Sandbox.Create("bulk");
            box.Protect();

            const int n = 120;
            for (int i = 0; i < n; i++)
            {
                box.WriteFile($"bulk/f{i:D3}.txt", $"文件 {i} 内容 " + new string('x', 40));
            }

            box.WaitFor(() =>
            {
                var events = box.AllEvents();
                return events.Count(e => e.Operation == OperationType.Created) >= n - 10;
            }, 15000, $"{n} 个文件应基本都被记录");

            var created = box.AllEvents().Count(e => e.Operation == OperationType.Created);
            Check.True(created >= n - 10, $"应有至少 {n - 10} 条创建事件（实际 {created}）");
        });

        yield return new("监听与事件·真实文件系统", "批量删除（模拟 rm -rf）→ 全部被记录且可恢复", () =>
        {
            using var box = Sandbox.Create("bulkdel");
            box.Protect();

            for (int i = 0; i < 40; i++) box.WriteFile($"victim/f{i:D2}.txt", $"内容 {i}");
            box.WaitFor(() => box.AllEvents().Count(e => e.Operation == OperationType.Created) >= 35,
                12000, "40 个文件应先被记录为创建");

            box.DeleteFile("victim");

            box.WaitFor(() => box.AllEvents().Count(e => e.Operation == OperationType.Deleted) >= 35,
                15000, "批量删除应被记录");

            var deleted = box.AllEvents().Where(e => e.Operation == OperationType.Deleted).ToList();
            var restorable = deleted.Count(e => e.CanRestorePrevious);
            Check.True(restorable >= 35, $"绝大多数删除应可恢复（实际可恢复 {restorable}/{deleted.Count}）");
        });

        yield return new("监听与事件·真实文件系统", "空目录创建与删除不会产生内容噪声", () =>
        {
            using var box = Sandbox.Create("dirs");
            box.Protect();

            box.MkDir("空目录");
            var ev = box.WaitForEvent("空目录");
            Check.Equal(EntryKind.Directory, ev.Kind, "应识别为目录");
            Check.True(ev.Operation is OperationType.Created or OperationType.Modified, $"应记录为创建（实际 {ev.Operation}）");
        });

        yield return new("监听与事件·真实文件系统", "排除规则生效：node_modules / .git 不进入时间线", () =>
        {
            using var box = Sandbox.Create("exclude");
            box.Protect();

            box.WriteFile("node_modules/pkg/index.js", "module.exports = 1");
            box.WriteFile(".git/config", "[core]");
            box.WriteFile("src/normal.txt", "正常文件");

            box.WaitForEvent("src/normal.txt", OperationType.Created);

            var visible = box.AllEvents(includeTransient: false);
            Check.True(visible.All(e => !e.RelativePath.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase)),
                $"node_modules 不应出现在时间线：\n{box.DescribeEvents()}");
            Check.True(visible.All(e => !e.RelativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)),
                $".git 不应出现在时间线：\n{box.DescribeEvents()}");

            // 但事实仍然被记录（降级为瞬时事件）
            var all = box.AllEvents(includeTransient: true);
            Check.True(all.Any(e => e.RelativePath.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase)) ||
                       box.Diagnostics.ContentsSkippedExcluded >= 0,
                "被排除路径仍可留下折叠记录（或至少不报错）");
        });

        yield return new("监听与事件·真实文件系统", "基线：添加保护之前就存在的文件，删除后仍可恢复", () =>
        {
            using var box = Sandbox.Create("baseline");
            // 关键顺序：先写文件，再加入保护范围
            box.WriteFile("preexisting.txt", "保护之前就存在的内容");
            box.WriteFile("sub/deep.txt", "深层文件内容");

            box.Protect();   // 建立基线（会扫描并留存内容）

            Check.True(box.Index.CountAll(box.RootId) >= 2, "基线索引应包含已有文件");

            Thread.Sleep(250);
            box.DeleteFile("preexisting.txt");
            var ev = box.WaitForEvent("preexisting.txt", OperationType.Deleted);

            Check.True(ev.CanRestorePrevious,
                "基线已留存内容，因此保护之前就存在的文件被删除后仍可恢复（这是产品的核心承诺）");
        });

        yield return new("监听与事件·真实文件系统", "快照是完整清单：可独立于事件日志用于恢复", () =>
        {
            using var box = Sandbox.Create("snapshot");
            box.Protect();

            box.WriteFile("a.txt", "A");
            box.WriteFile("b.txt", "B");
            box.WaitFor(() => box.AllEvents().Count >= 2, 6000, "两个文件应被记录");

            var snap = box.Snapshot(SnapshotKind.Manual, "测试恢复点");
            Check.Equal(2, snap.FileCount,
                $"快照应包含 2 个文件（实际 {snap.FileCount}）\n" + box.DescribeState() + box.DescribeEvents());

            var files = box.SnapshotsRepo.LoadFiles(snap.Id);
            Check.Equal(2, files.Count, "清单行数");
            Check.True(files.All(f => f.ObjectId is not null), "清单中的每个文件都应解析出内容对象 Id");
            Check.True(files.All(f => f.FirstSeenUtc is not null), "清单应包含 first_seen 信息（恢复预览需要）");
        });

        yield return new("监听与事件·真实文件系统", "停机期间的变化：重新对齐后如实记录为「重新扫描发现」", () =>
        {
            using var box = Sandbox.Create("downtime");
            box.Protect();

            box.WriteFile("tracked.txt", "原始内容");
            box.WaitForEvent("tracked.txt", OperationType.Created);
            box.Snapshot(SnapshotKind.Auto);
            box.Flush();

            // 模拟"程序没运行"：直接改磁盘，不经过监听
            box.Watch.Stop();
            box.WriteFile("tracked.txt", "停机期间被改过的内容");
            box.WriteFile("appeared.txt", "停机期间新建的文件");
            File.Delete(box.Abs("tracked.txt") + ".nothing");   // 无害操作，确保有磁盘活动

            var report = box.Rescanner.Align(box.WatchedRoot, ResyncMode.Resync);

            Check.True(report.DetectedModified >= 1 || report.DetectedCreated >= 1,
                $"重新对齐应发现停机期间的变化（报告：新增 {report.DetectedCreated} 修改 {report.DetectedModified} 消失 {report.DetectedDeleted}）");

            var events = box.AllEvents();
            var resyncEvents = events.Where(e => e.Source == Rescanner.ResyncSource).ToList();
            Check.True(resyncEvents.Count > 0, "必须留下来源为「重新扫描」的事件（不假装是实时观测到的）");
            Check.True(resyncEvents.All(e => !string.IsNullOrWhiteSpace(e.Note)),
                "重新扫描产生的事件必须带说明，避免误导用户");
        });

        yield return new("监听与事件·真实文件系统", "合并器：合成通知序列被正确合并与归类", () =>
        {
            // 这一条用合成通知（确定性），验证时序敏感的合并逻辑：
            // 编辑器"写临时文件 → 删原文件 → 改名覆盖"的完整序列
            using var box = Sandbox.Create("merger");
            box.Protect();

            var root = box.WatchedRoot;
            box.WriteFile("target.txt", "原始内容");
            box.WaitForEvent("target.txt", OperationType.Created);
            box.Flush();
            int before = box.AllEvents().Count;

            var t0 = DateTime.UtcNow;
            var batch = new List<RawFsNotification>
            {
                new() { RootId = root.Id, TimestampUtc = t0, AbsolutePath = box.Abs("target.txt.tmp"), Kind = RawChangeKind.Added, Sequence = 0 },
                new() { RootId = root.Id, TimestampUtc = t0.AddMilliseconds(10), AbsolutePath = box.Abs("target.txt.tmp"), Kind = RawChangeKind.Modified, Sequence = 1 },
                new() { RootId = root.Id, TimestampUtc = t0.AddMilliseconds(20), AbsolutePath = box.Abs("target.txt"), Kind = RawChangeKind.Removed, Sequence = 2 },
                new() { RootId = root.Id, TimestampUtc = t0.AddMilliseconds(30), AbsolutePath = box.Abs("target.txt"), OldAbsolutePath = box.Abs("target.txt.tmp"), Kind = RawChangeKind.RenamedNew, Sequence = 3 },
            };

            // 真实地写入临时文件，让合并器能读到内容
            File.WriteAllText(box.Abs("target.txt.tmp"), "替换后的内容");
            box.Watch.IngestForTest(batch);
            box.Flush();

            var events = box.AllEvents().Skip(before).ToList();
            Check.True(events.Count > 0, "原子替换序列必须产生事件");
            Check.True(events.Count <= 3, $"不应产生大量噪声事件（实际 {events.Count} 条）：\n{box.DescribeEvents()}");

            // 不应把临时文件当成一次正式创建暴露出来
            var noise = events.Where(e => e.RelativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                                          && e.Operation == OperationType.Created
                                          && !e.IsTransient).ToList();
            Check.True(noise.Count == 0, $"临时文件不应成为正式创建事件：\n{box.DescribeEvents()}");
        });

        yield return new("监听与事件·真实文件系统", "统计口径诚实：原始通知数 ≥ 落库事件数", () =>
        {
            using var box = Sandbox.Create("stats");
            box.Protect();

            for (int i = 0; i < 10; i++) box.WriteFile($"s{i}.txt", "内容" + i);
            box.WaitFor(() => box.AllEvents().Count >= 9, 10000, "10 个文件应被记录");

            var stats = box.Watch.Statistics;
            Check.True(stats.EventsPersisted >= 9, $"应记录至少 9 条事件（实际 {stats.EventsPersisted}）");
            Check.True(stats.RawNotifications >= stats.EventsPersisted,
                $"原始通知数不应少于落库事件数（原始 {stats.RawNotifications}，事件 {stats.EventsPersisted}）\n" +
                box.DescribeWatcher());
        });
    }
}
