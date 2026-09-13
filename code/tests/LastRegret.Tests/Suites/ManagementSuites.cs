using LastRegret.Core.Config;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Engine;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 受保护目录的"实时性"与恢复点的"生命周期管理"测试。
///
/// 这一组对应用户实际反馈的问题：
///   ① 添加目录时界面"未响应"——因为基线扫描跑在 UI 线程上；
///   ② 创建恢复点无法自定义删除；
///   ③ 撤销不可用（根因是没有基线、没有事件、也没有恢复记录）。
/// 这些都是最容易伤到真实使用体验的地方，必须有测试锁住。
/// </summary>
public static class ManagementSuites
{
    public static IEnumerable<TestCase> All()
    {
        yield return new("受保护目录管理", "登记目录必须立刻返回（扫描放到后台）", () =>
        {
            using var box = Sandbox.Create("register-fast");
            box.Watch.Logged += e => { if (e.Level is "error" or "warn") box.WatchLog.Add(e.Message); };

            // 先放一批文件，模拟"已经有很多内容的目录"
            for (int i = 0; i < 60; i++) box.WriteFile($"pre/f{i:D2}.txt", $"内容 {i} " + new string('y', 200));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ok, rootId, message) = box.Watch.RegisterRoot(box.WatchDir);
            sw.Stop();

            Check.True(ok, "登记应成功：" + message);
            box.AdoptRoot(rootId);

            // 登记只做"写一条记录 + 开监听"，绝不能把整目录扫描算进去
            Check.True(sw.ElapsedMilliseconds < 2500,
                $"登记必须立刻返回（实际耗时 {sw.ElapsedMilliseconds} ms）。" +
                "如果这里变慢，说明又把扫描塞回了同步路径，界面会再次卡死。");

            // 登记后监听必须已经在跑：扫描期间发生的变化也不能漏
            Check.True(box.Watch.GetState(rootId).Watching, "登记后应立即开始监听");

            // 然后才建立基线
            var (files, dirs) = box.Watch.RunBaseline(rootId);
            Check.True(files >= 60, $"基线扫描应覆盖已有文件（实际 {files} 个文件）");

            var health = box.Watch.DescribeRootHealth(rootId);
            Check.True(health.HasBaseline, "基线快照应已建立");
            Check.True(health.BaselineFiles >= 60, $"基线快照应记录至少 60 个文件（实际 {health.BaselineFiles}）");
            Check.True(health.IndexEntries >= 60, $"索引应有至少 60 个条目（实际 {health.IndexEntries}）");
        });

        yield return new("受保护目录管理", "扫描期间写入的文件不会丢失（监听先于扫描启动）", () =>
        {
            using var box = Sandbox.Create("register-race");
            box.Watch.Logged += e => { if (e.Level is "error" or "warn") box.WatchLog.Add(e.Message); };

            var (ok, rootId, _) = box.Watch.RegisterRoot(box.WatchDir);
            Check.True(ok, "登记应成功");
            box.AdoptRoot(rootId);
            box.Watch.Start();

            // 监听已启动、基线还没跑：此时写入的内容必须被实时监听到
            Thread.Sleep(150);
            box.WriteFile("during-scan.txt", "扫描期间写入的内容");

            box.Watch.RunBaseline(rootId);

            box.WaitFor(() => box.AllEvents().Any(e =>
                    PathUtil.Comparer.Equals(e.RelativePath, "during-scan.txt")),
                8000, "扫描期间写入的文件必须被记录");

            var ev = box.AllEvents().First(e => PathUtil.Comparer.Equals(e.RelativePath, "during-scan.txt"));
            Check.NotNull(ev.HashAfter, "应记录到内容哈希");
        });

        yield return new("受保护目录管理", "中断的扫描会被识别为「缺基线」，并可重新补齐", () =>
        {
            using var box = Sandbox.Create("missing-baseline");
            box.Watch.Logged += e => { if (e.Level is "error" or "warn") box.WatchLog.Add(e.Message); };

            // 模拟用户强杀进程留下的状态：登记了、有索引，但基线快照没建立
            box.WriteFile("exists.txt", "已存在的文件");
            var (ok, rootId, _) = box.Watch.RegisterRoot(box.WatchDir);
            Check.True(ok, "登记应成功");
            box.AdoptRoot(rootId);
            box.Watch.Start();

            var missing = box.Watch.FindRootsMissingBaseline();
            Check.True(missing.Any(r => r.Id == rootId),
                "应识别出该目录缺少基线（否则用户会对着空白时间线猜原因）");

            var before = box.Watch.DescribeRootHealth(rootId);
            Check.False(before.HasBaseline, "补齐之前应报告没有基线");

            box.Watch.RunBaseline(rootId);

            var after = box.Watch.DescribeRootHealth(rootId);
            Check.True(after.HasBaseline, "补齐之后应有基线");
            Check.True(after.BaselineFiles >= 1, $"基线应包含已有文件（实际 {after.BaselineFiles}）");
            Check.False(box.Watch.FindRootsMissingBaseline().Any(r => r.Id == rootId), "补齐后不应再报告缺基线");
        });

        yield return new("受保护目录管理", "移出保护范围：可以只移出保留历史，也可以连历史一起删", () =>
        {
            using var box = Sandbox.Create("remove-root");
            box.Watch.Logged += e => { if (e.Level is "error" or "warn") box.WatchLog.Add(e.Message); };

            box.WriteFile("a.txt", "内容");
            var (ok, rootId, _) = box.Watch.RegisterRoot(box.WatchDir);
            Check.True(ok, "登记应成功");
            box.AdoptRoot(rootId);
            box.Watch.Start();
            box.Watch.RunBaseline(rootId);

            box.WriteFile("b.txt", "后加的文件");
            box.WaitForEvent("b.txt", OperationType.Created);
            box.Flush();
            Check.True(box.AllEvents().Count >= 1, "移出之前应当有历史记录");

            // ① 只移出：历史保留
            box.Watch.RemoveRoot(rootId, deleteHistory: false);
            Check.NotNull(box.Roots.Get(rootId), "「只移出」时登记应保留（否则用户以为被删了）");
            Check.False(box.Roots.Get(rootId)!.Enabled, "「只移出」应当把保护停掉");
            Check.True(box.AllEvents().Count >= 1, "「只移出」不得删除历史记录");
            Check.FileExists(box.Abs("a.txt"), "文件本身当然不能被删");

            // ② 连历史一起删
            box.Watch.RemoveRoot(rootId, deleteHistory: true);
            Check.True(box.Roots.Get(rootId) is null, "「连历史一起删」应把登记彻底删除");
            Check.Equal(0, box.AllEvents().Count, "「连历史一起删」应清掉历史记录");
            Check.FileExists(box.Abs("a.txt"), "删历史不得动磁盘上的文件");
            Check.FileExists(box.Abs("b.txt"), "删历史不得动磁盘上的文件");
        });

        yield return new("恢复点管理", "空壳基线快照会被自动清理（避免误导恢复预览）", () =>
        {
            using var box = Sandbox.Create("stale-baseline");
            box.Watch.Logged += e => { if (e.Level is "error" or "warn") box.WatchLog.Add(e.Message); };

            var (ok, rootId, _) = box.Watch.RegisterRoot(box.WatchDir);
            Check.True(ok, "登记应成功");
            box.AdoptRoot(rootId);

            // 制造一个"空壳基线"（文件数 0），模拟扫描被中断留下的残留
            var stale = box.SnapshotService.Create(rootId, SnapshotKind.Baseline, "伪造的空壳基线", forceFull: true);
            Check.Equal(0, stale.FileCount, "伪造的基线应是空的");

            box.WriteFile("real.txt", "真实内容");
            box.Watch.RunBaseline(rootId);

            var all = box.SnapshotService.List(rootId, 50).ToList();
            Check.True(all.Where(s => s.Kind == SnapshotKind.Baseline).All(s => s.FileCount > 0),
                "不应再存在空壳基线：\n" + string.Join("\n",
                    all.Select(s => $"  #{s.Id} {s.Kind} 文件={s.FileCount}")));
            Check.False(all.Any(s => s.Id == stale.Id), "伪造的空壳基线应已被清理");
        });

        yield return new("恢复点管理", "最新恢复点不允许删除", () =>
        {
            using var box = Sandbox.Create("delete-latest");
            box.Protect();
            box.Snapshot(SnapshotKind.Manual, "第一个恢复点");
            Thread.Sleep(60);
            var latest = box.Snapshot(SnapshotKind.Manual, "第二个恢复点");

            var check = box.SnapshotService.CanDelete(latest);
            Check.False(check.Allowed, "最新的恢复点必须拒绝删除（否则时间线会失去当前状态的锚点）");
            Check.Contains(check.Reason, "最新", "拒绝原因要说清楚");

            Check.Throws<InvalidOperationException>(() => box.SnapshotService.DeleteChecked(latest.Id),
                "DeleteChecked 必须抛出带原因的异常");
            Check.NotNull(box.SnapshotService.Get(latest.Id), "被拒绝后该恢复点必须仍然存在");
        });

        yield return new("恢复点管理", "被恢复操作引用的恢复点不允许删除", () =>
        {
            using var box = Sandbox.Create("delete-referenced");
            box.Protect();

            box.WriteFile("a.txt", "v1");
            box.WaitForIndex("a.txt");
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "手动点");

            Thread.Sleep(250);
            box.WriteFile("a.txt", "v2");
            box.WaitForIndex("a.txt");
            box.Flush();

            var outcome = box.RestoreTo(point.TimestampLocal);
            Check.True(outcome.Ok, "恢复应成功：" + outcome.Message);
            Check.NotNull(outcome.PreSnapshotId, "恢复前安全点应存在");

            var safety = box.SnapshotService.Get(outcome.PreSnapshotId!.Value);
            Check.NotNull(safety, "安全点应可读取");

            var check = box.SnapshotService.CanDelete(safety!);
            Check.False(check.Allowed, "被恢复操作引用的安全点必须拒绝删除");
            Check.Contains(check.Reason, "撤销", "拒绝原因应点明「删了就无法撤销」");
        });

        yield return new("恢复点管理", "可以删除中间的手动恢复点（自定义删除）", () =>
        {
            using var box = Sandbox.Create("delete-middle");
            box.Protect();

            // 注意：三次写入之间必须留出大于合并窗口的间隔，否则它们会被合并成一次修改，
            // 三个恢复点就会是同一个状态——那样测的就不是"删除中间点"而是"合并逻辑"了。
            box.WriteFile("x.txt", "1");
            box.WaitForIndex("x.txt");
            box.Flush();
            var first = box.Snapshot(SnapshotKind.Manual, "第一个");

            Thread.Sleep(400);
            box.WriteFile("x.txt", "22");
            box.WaitForIndex("x.txt");
            box.Flush();
            var middle = box.Snapshot(SnapshotKind.Manual, "中间这个");

            Thread.Sleep(400);
            box.WriteFile("x.txt", "333");
            box.WaitForIndex("x.txt");
            box.Flush();
            var last = box.Snapshot(SnapshotKind.Manual, "最新的");

            // 三个恢复点确实记录了三个不同状态（否则下面的断言没有意义）
            Check.NotEqual(first.TotalBytes, middle.TotalBytes,
                "第一个与中间的恢复点内容应不同（总字节数相同说明写入被合并了）");

            var check = box.SnapshotService.CanDelete(middle);
            Check.True(check.Allowed, "中间的手动恢复点应允许删除（原因：" + check.Reason + "）");

            box.SnapshotService.DeleteChecked(middle.Id);
            Check.Null(box.SnapshotService.Get(middle.Id), "该恢复点应已被删除");

            var remaining = box.SnapshotService.List(box.RootId, 50).Select(s => s.Id).ToList();
            Check.True(remaining.Contains(first.Id), "其他恢复点不应受影响（第一个仍在）");
            Check.True(remaining.Contains(last.Id), "其他恢复点不应受影响（最新的仍在）");
            Check.Equal(0L, box.SnapshotsRepo.CountFiles(middle.Id), "清单行应被级联删除");
        });

        yield return new("恢复点管理", "批量清理只动旧的自动恢复点，手动与安全点一律保留", () =>
        {
            using var box = Sandbox.Create("cleanup-points");
            box.Protect();

            box.WriteFile("c.txt", "内容");
            box.WaitForIndex("c.txt");
            box.Flush();

            var manual = box.Snapshot(SnapshotKind.Manual, "手动点");
            var auto1 = box.Snapshot(SnapshotKind.Auto, "自动点 1");
            Thread.Sleep(60);
            var auto2 = box.Snapshot(SnapshotKind.Auto, "自动点 2");
            var latest = box.Snapshot(SnapshotKind.Manual, "最后的手动点（最新）");

            // 截止时间设为"现在"，即所有旧的自动点都在清理范围内
            var deleted = box.SnapshotService.DeleteAutoOlderThan(box.RootId, DateTime.UtcNow, out var skipped);

            var remainingIds = box.SnapshotService.List(box.RootId, 50).Select(s => s.Id).ToList();
            Check.True(remainingIds.Contains(manual.Id), "手动恢复点必须保留");
            Check.True(remainingIds.Contains(latest.Id), "最新恢复点必须保留");
            Check.False(remainingIds.Contains(auto1.Id) && remainingIds.Contains(auto2.Id),
                "旧的自动恢复点应至少被删除一个（实际删除 " + deleted + " 个）");
            Check.True(deleted >= 1, $"应删除至少 1 个自动恢复点（实际 {deleted}）");
            _ = skipped;
        });

        yield return new("恢复点管理", "恢复点备注可自定义修改与清空", () =>
        {
            using var box = Sandbox.Create("point-note");
            box.Protect();
            var point = box.Snapshot(SnapshotKind.Manual, "系统说明");

            box.SnapshotService.Rename(point.Id, "  升级前  ");
            var renamed = box.SnapshotService.Get(point.Id);
            Check.NotNull(renamed, "恢复点应仍存在");
            Check.Equal("升级前", renamed!.Note, "备注应被写入并去掉首尾空格");

            box.SnapshotService.Rename(point.Id, "   ");
            var cleared = box.SnapshotService.Get(point.Id);
            Check.Null(cleared!.Note, "空白备注应被清空为 null");

            // 备注长度应被限制，避免异常长的字符串破坏列表布局
            box.SnapshotService.Rename(point.Id, new string('长', 500));
            var limited = box.SnapshotService.Get(point.Id);
            Check.True(limited!.Note!.Length <= 200, $"备注应被截断到 200 字符以内（实际 {limited.Note.Length}）");
        });

        yield return new("恢复点管理", "恢复点里能看出「是否能撤销」与「为什么不能」", () =>
        {
            using var box = Sandbox.Create("undo-affordance");
            box.Protect();

            box.WriteFile("u.txt", "v1");
            box.WaitForIndex("u.txt");
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "改之前");

            // 恢复之前：没有任何恢复记录
            Check.False(box.Restore.ListOperations(box.RootId, 20).Any(), "还没有恢复记录");
            Check.Null(box.Restore.GetLastUndoable(box.RootId), "此时不应有可撤销的操作");

            Thread.Sleep(250);
            box.WriteFile("u.txt", "v2");
            box.WaitForIndex("u.txt");
            box.Flush();

            var outcome = box.RestoreTo(point.TimestampLocal);
            Check.True(outcome.Ok, "恢复应成功：" + outcome.Message);

            // 恢复之后：必须出现一条"可撤销"的记录，且带安全点
            var last = box.Restore.GetLastUndoable(box.RootId);
            Check.NotNull(last, "恢复成功后必须能找到可撤销的操作（否则界面的撤销按钮永远点不动）");
            Check.NotNull(last!.PreRestoreSnapshotId, "可撤销的前提是有恢复前安全点");
            Check.True(last.Status is RestoreStatus.Completed or RestoreStatus.PartiallyCompleted,
                $"状态应为完成/部分完成（实际 {last.Status}）");

            var (undoPlan, undoError) = box.Restore.BuildUndoPreview(last.Id);
            Check.NotNull(undoPlan, "应能生成撤销预览：" + undoError);
        });

        yield return new("保护模式", "扫描未完成时建立的恢复点会被识别为「内容不完整」", () =>
        {
            using var box = Sandbox.Create("suspect-point");
            box.Protect();

            // 复现用户实际遇到的情况：目录里有很多文件，但在基线扫描完成前
            // 就点了一次「创建恢复点」——于是得到一个内容严重不完整的恢复点。
            for (int i = 0; i < 30; i++) box.WriteFile($"many/f{i:D2}.txt", "内容 " + i);
            box.Watch.RunBaseline(box.RootId);      // 补齐基线（此时索引里已有 30+ 个文件）

            // 手工插入一个"只含 1 个文件"的恢复点，模拟当时的不完整状态
            var tiny = box.SnapshotService.Create(box.RootId, SnapshotKind.Manual, "扫描未完成时创建的", forceFull: true);
            Check.True(tiny.FileCount >= 30, "正常快照应包含全部文件（这是对照）");

            // 直接构造不完整快照：只放一行
            var broken = box.SnapshotService.Create(box.RootId, SnapshotKind.Manual, "伪造的不完整快照", forceFull: true);
            box.Db.Events.NonQuery("DELETE FROM snapshot_files WHERE snapshot_id = ? AND path NOT LIKE 'many/f00.txt';", broken.Id);

            var reloaded = box.SnapshotService.Get(broken.Id)!;
            var health = box.SnapshotService.CheckHealth(reloaded);
            Check.True(health.IsSuspect,
                $"内容严重不完整的恢复点必须被识别出来（文件数 {reloaded.FileCount}，索引 {box.Index.CountAll(box.RootId)}）");
            Check.NotNull(health.Reason, "必须给出原因，界面要如实告知用户");

            // 完整快照不应被误报
            var goodHealth = box.SnapshotService.CheckHealth(box.SnapshotService.Get(tiny.Id)!);
            Check.False(goodHealth.IsSuspect, "内容完整的恢复点不应被误报为可疑：" + goodHealth.Reason);
        });

        yield return new("保护模式", "「只记录变化」：能记录变化，但明确不可恢复内容", () =>
        {
            using var box = Sandbox.Create("mode-track");
            box.Settings.Protection = ProtectionMode.TrackOnly;
            box.SettingsRepo.Save(box.Settings);
            box.Watch.ReloadSettings();
            box.Protect();

            box.WriteFile("t.txt", "内容 A");
            var ev = box.WaitForEvent("t.txt", OperationType.Created);

            // 事实要记录：路径、时间、大小、哈希都要有
            Check.Equal("t.txt", ev.RelativePath, "应记录路径");
            Check.NotNull(ev.HashAfter, "应记录内容哈希（这样才知道「内容变了」）");
            Check.Equal((long)System.Text.Encoding.UTF8.GetByteCount("内容 A"), ev.SizeAfter ?? -1, "应记录大小");

            // 但不留存内容，且必须如实说明
            Check.Null(ev.ObjectIdAfter, "「只记录变化」模式不应保存内容对象");
            Check.True(ev.CanRestoreAfter == false, "应明确报告：变化后的内容不可用");
            Check.Contains(ev.Note ?? string.Empty, "只记录变化",
                $"必须如实说明为什么没有保存内容：{ev.Note}");
            Check.Equal(0L, box.Store.GetUsage().ObjectCount, "内容库里不应有任何对象");

            // 修改与删除同样只记事实
            Thread.Sleep(300);
            box.WriteFile("t.txt", "内容 B");
            box.WaitForIndex("t.txt");
            var mod = box.EventsOf("t.txt").Last();
            Check.Null(mod.ObjectIdAfter, "修改也不留存内容");
            Check.NotNull(mod.HashAfter, "但必须记录新内容的哈希");

            Thread.Sleep(300);
            box.DeleteFile("t.txt");
            var del = box.WaitForEvent("t.txt", OperationType.Deleted);
            Check.False(del.CanRestorePrevious,
                "「只记录变化」模式下的删除**无法恢复**，必须如实报告而不是假装可恢复");
        });

        yield return new("保护模式", "「智能留存」：小文件留存内容、大文件只记事实", () =>
        {
            using var box = Sandbox.Create("mode-smart");
            box.Settings.Protection = ProtectionMode.SmartContent;
            box.Settings.SmartContentMaxBytes = 4096;      // 阈值收紧到 4KB 便于测试
            box.SettingsRepo.Save(box.Settings);
            box.Watch.ReloadSettings();
            box.Protect();

            // 小文件：应留存内容
            box.WriteFile("small.txt", "小文件的内容");
            var small = box.WaitForEvent("small.txt", OperationType.Created);
            Check.NotNull(small.ObjectIdAfter, "小文件应留存内容");
            Check.True(small.CanRestoreAfter, "小文件应可恢复");
            var readBack = box.ReadObject(small.ObjectIdAfter!.Value);
            Check.Equal("小文件的内容", readBack, "留存的内容必须与写入一致");

            // 大文件：只记事实
            var big = new byte[64 * 1024];
            new Random(11).NextBytes(big);
            box.WriteBytes("big.bin", big);
            var bigEv = box.WaitForEvent("big.bin", OperationType.Created);
            Check.Null(bigEv.ObjectIdAfter, "超过阈值的文件不应留存内容");
            Check.NotNull(bigEv.HashAfter, "但仍必须记录哈希");
            Check.Contains(bigEv.Note ?? string.Empty, "智能留存",
                $"必须如实说明是模式导致的跳过：{bigEv.Note}");

            // 统计口径要能看出"有多少是按模式跳过的"
            Check.True(box.Diagnostics.ContentsSkippedByMode >= 1,
                "应统计被模式跳过的数量（用户据此判断要不要换模式）");
        });

        yield return new("保护模式", "「完整内容」：所有文件都留存内容", () =>
        {
            using var box = Sandbox.Create("mode-full");
            box.Settings.Protection = ProtectionMode.FullContent;
            box.Settings.SmartContentMaxBytes = 1024;      // 阈值故意设很小，验证它被忽略
            box.SettingsRepo.Save(box.Settings);
            box.Watch.ReloadSettings();
            box.Protect();

            var big = new byte[32 * 1024];
            new Random(3).NextBytes(big);
            box.WriteBytes("big.bin", big);
            var ev = box.WaitForEvent("big.bin", OperationType.Created);

            Check.NotNull(ev.ObjectIdAfter, "「完整内容」模式应留存大文件内容（不受智能阈值影响）");
            Check.True(ev.CanRestoreAfter, "应可恢复");
            Check.Equal(0L, box.Diagnostics.ContentsSkippedByMode, "不应有按模式跳过的内容");

            // 内容真能还原
            Thread.Sleep(300);
            box.WriteBytes("big.bin", new byte[] { 1, 2, 3 });
            box.WaitForIndex("big.bin");
            box.Flush();
            var restore = box.RestoreTo(DateTime.Now);
            Check.True(restore.Ok || restore.Message.Contains("无需恢复"), "恢复流程应能正常执行：" + restore.Message);
        });

        yield return new("保护模式", "三种模式的耗时估算与能力说明必须一致（不能夸大）", () =>
        {
            using var box = Sandbox.Create("mode-estimate");
            box.Protect();

            for (int i = 0; i < 40; i++) box.WriteFile($"e/f{i:D2}.txt", new string('z', 300));

            var full = box.Rescanner.Estimate(box.WatchDir, ProtectionMode.FullContent, 4 * 1024 * 1024);
            var smart = box.Rescanner.Estimate(box.WatchDir, ProtectionMode.SmartContent, 4 * 1024 * 1024);
            var track = box.Rescanner.Estimate(box.WatchDir, ProtectionMode.TrackOnly, 4 * 1024 * 1024);

            Check.True(full.Files >= 40, $"应统计到文件（实际 {full.Files}）");

            // 只记录变化必须是最快的，且占用为 0
            Check.True(track.EstimatedBaselineMs <= full.EstimatedBaselineMs,
                $"「只记录变化」的估算耗时应不高于「完整内容」（{track.EstimatedBaselineMs} vs {full.EstimatedBaselineMs}）");
            Check.Equal(0L, track.EstimatedHistoryBytes, "「只记录变化」的历史占用应为 0");
            Check.True(full.EstimatedHistoryBytes >= smart.EstimatedHistoryBytes,
                "「完整内容」的占用估算应不低于「智能留存」");

            // 说明文字必须写清能力边界
            Check.Contains(full.Describe(), "完整内容", "完整模式的说明应写明模式名");
            Check.Contains(track.Describe(), "无法恢复文件内容",
                "「只记录变化」的说明必须明确写出无法恢复内容（不得含糊）");
            Check.Contains(smart.Describe(), "智能留存", "智能模式的说明应写明模式名");
        });
    }
}
