using LastRegret.Core.Abstractions;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Engine;
using LastRegret.Runtime;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 第 10 刀后的**问题收口**回归套件。
///
/// 与其它套件的区别：这里的断言一律以**磁盘真实状态**为准，
/// 而不是"数据库里有没有那一行"。历史缺陷（幽灵行、rename 被记成新增）
/// 恰恰是"数据库自洽、磁盘不自洽"，只看数据库行是抓不到的。
/// </summary>
public static class ReliabilitySuites
{
    // ───────────────────────── 磁盘 ↔ 快照清单 一致性核对 ─────────────────────────

    /// <summary>磁盘上真实存在的文件与目录（相对路径，'/' 分隔）。</summary>
    private static (HashSet<string> Files, HashSet<string> Dirs) DiskTree(string watchDir)
    {
        var files = new HashSet<string>(PathUtil.Comparer);
        var dirs = new HashSet<string>(PathUtil.Comparer);

        foreach (var d in Directory.EnumerateDirectories(watchDir, "*", SearchOption.AllDirectories))
            dirs.Add(ToRelative(watchDir, d));
        foreach (var f in Directory.EnumerateFiles(watchDir, "*", SearchOption.AllDirectories))
            files.Add(ToRelative(watchDir, f));

        return (files, dirs);
    }

    /// <summary>某个恢复点清单里记录的文件与目录。</summary>
    private static (HashSet<string> Files, HashSet<string> Dirs) ManifestTree(Sandbox box, Snapshot snapshot)
    {
        var manifest = box.SnapshotService.LoadManifest(snapshot);
        var files = new HashSet<string>(PathUtil.Comparer);
        var dirs = new HashSet<string>(PathUtil.Comparer);

        foreach (var e in manifest.Entries.Values)
        {
            if (e.RelativePath.Length == 0) continue;      // 根自身的条目不算
            if (e.Kind == EntryKind.Directory) dirs.Add(e.RelativePath);
            else files.Add(e.RelativePath);
        }

        return (files, dirs);
    }

    private static string ToRelative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>
    /// 只轮询、**绝不调用 Flush** 的等待：用于构造"通知只躺在收件箱里"的状态。
    /// （Sandbox.WaitFor 每轮都会 Flush，会把待测的那批数据提前处理掉。）
    /// </summary>
    private static void WaitWithoutFlushing(Func<bool> condition, int timeoutMs, string because)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            Thread.Sleep(30);
        }
        throw new AssertFailedException("等待超时：" + because);
    }

    /// <summary>仓库 code\ 目录（源码结构断言用）。</summary>
    private static string RepoRootForTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LastRegret.sln")))
        {
            dir = dir.Parent;
        }
        Check.NotNull(dir, "应能从测试输出目录向上找到 LastRegret.sln");
        return dir!.FullName;
    }

    /// <summary>把集合差集打印成人能读懂的样子。</summary>
    private static string Diff(string what, IEnumerable<string> extra, IEnumerable<string> missing)
    {
        var e = extra.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var m = missing.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (e.Count == 0 && m.Count == 0) return $"{what}：一致";
        return $"{what}：\n    清单里多出（磁盘上没有）：{(e.Count == 0 ? "无" : string.Join("、", e))}" +
               $"\n    清单里缺少（磁盘上有）：{(m.Count == 0 ? "无" : string.Join("、", m))}";
    }

    /// <summary>
    /// **核心不变量**：最新恢复点的清单必须与磁盘当前状态逐项一致。
    /// 这是"幽灵行"类缺陷的判定依据（数据库里自洽不算数）。
    /// </summary>
    private static void AssertManifestMatchesDisk(Sandbox box, Snapshot snapshot, string because)
    {
        var (diskFiles, diskDirs) = DiskTree(box.WatchDir);
        var (snapFiles, snapDirs) = ManifestTree(box, snapshot);

        var fileDiff = Diff("文件", snapFiles.Except(diskFiles), diskFiles.Except(snapFiles));
        var dirDiff = Diff("目录", snapDirs.Except(diskDirs), diskDirs.Except(snapDirs));

        Check.True(fileDiff == "文件：一致", because + "\n  " + fileDiff);
        Check.True(dirDiff == "目录：一致", because + "\n  " + dirDiff);
    }

    /// <summary>对某个根的最新恢复点做一致性核对。</summary>
    private static void AssertLatestManifestMatchesDisk(Sandbox box, string because)
    {
        var latest = box.SnapshotService.GetLatest(box.RootId);
        Check.NotNull(latest, "应当存在恢复点（" + because + "）");
        AssertManifestMatchesDisk(box, latest!, because);
    }

    // ───────────────────────── 事件断言小工具 ─────────────────────────

    private static FileEvent WaitForRename(Sandbox box, string newPath, int timeoutMs = 6000)
    {
        FileEvent? found = null;
        box.WaitFor(() =>
        {
            found = box.EventsOf(newPath)
                .LastOrDefault(e => e.Operation is OperationType.Renamed or OperationType.Moved);
            return found is not null;
        }, timeoutMs, $"路径 {newPath} 的重命名事件");

        return found!;
    }

    private static void AssertLive(Sandbox box, string path, string because)
    {
        var entry = box.Index.Get(box.RootId, path);
        Check.NotNull(entry, because + $"：索引里应当有 {path}");
        Check.False(entry!.IsDeleted, because + $"：{path} 应当是「存在」状态");
    }

    /// <summary>
    /// 等索引真正反映出期望的"存在/已删除"状态。
    ///
    /// 为什么必须等：`WatchEventPipeline.Persist` 先 `AppendRange` 落事件行，
    /// 之后才 `UpdateIndex`（顺序是刻意的：索引要带上事件 Id 作为水位线）。
    /// 所以"事件查得到"并不等于"索引已更新" —— 直接断言索引会偶发抢跑。
    /// </summary>
    private static void WaitIndex(Sandbox box, string path, bool live, int timeoutMs = 6000)
    {
        box.WaitFor(() =>
        {
            var e = box.Index.Get(box.RootId, path);
            return live ? e is { IsDeleted: false } : e is null || e.IsDeleted;
        }, timeoutMs, $"索引反映 {path} 的{(live ? "存在" : "删除")}状态");
    }

    private static void AssertGone(Sandbox box, string path, string because)
    {
        var entry = box.Index.Get(box.RootId, path);
        Check.True(entry is null || entry.IsDeleted, because + $"：{path} 应当已被标记删除（实际：{(entry is null ? "缺失" : "仍存在")}）");
    }

    /// <summary>
    /// 确定性地构造"目录被整体移除，而事件层只报得出父目录一条"的形态。
    ///
    /// 为什么要这样构造：用 `Directory.Delete(recursive:true)` 时 Windows 会**逐个子项**
    /// 各报一条 REMOVED，于是即使实现只按精确路径删快照行，子项也恰好被各自的变更事件
    /// 覆盖掉 —— 幽灵行被掩盖。而真实世界里"只报父目录"的形态确实存在
    /// （回收站删除、某些文件系统/驱动、事件被合并或去重之后）。
    /// 这里暂停监听后手工投递**唯一那条**父目录事件，把这条路径钉死。
    /// </summary>
    private static void RemoveDirectoryReportingParentOnly(Sandbox box, string relative)
    {
        box.Watch.Pause(box.RootId);
        Directory.Delete(box.Abs(relative), recursive: true);

        box.Watch.IngestForTest(new[]
        {
            new RawFsNotification
            {
                RootId = box.RootId,
                TimestampUtc = DateTime.UtcNow,
                AbsolutePath = box.Abs(relative),
                Kind = RawChangeKind.Removed,
                IsDirectory = true,
                Sequence = 0,
            },
        });
        box.Flush();
        WaitIndex(box, relative, live: false);
    }

    /// <summary>同上，但用于"目录改名只报一条事件"的形态（旧目录整棵消失）。</summary>
    private static void RenameDirectoryReportingParentOnly(Sandbox box, string from, string to)
    {
        box.Watch.Pause(box.RootId);
        Directory.Move(box.Abs(from), box.Abs(to));

        var now = DateTime.UtcNow;
        box.Watch.IngestForTest(new[]
        {
            new RawFsNotification
            {
                RootId = box.RootId,
                TimestampUtc = now,
                AbsolutePath = box.Abs(to),
                OldAbsolutePath = box.Abs(from),
                Kind = RawChangeKind.RenamedNew,
                IsDirectory = true,
                Sequence = 0,
            },
        });
        box.Flush();
        WaitIndex(box, to, live: true);
    }

    // ───────────────────────── 用例 ─────────────────────────

    public static IEnumerable<TestCase> All()
    {
        // ═══════════════ P1-4：非干净退出 → 启动自动对账 ═══════════════

        // ═══════════════ 收口·重扫垃圾点 ═══════════════
        // 真实缺陷：ProcessPendingRescans 原先**无条件**创建 SnapshotKind.Resync 点，
        // 于是"脏退出启动但磁盘其实没变"也会留下一个恢复点。实测累积到 41 个，
        // 把用户真正要找的历史时间点挤到列表很后面。
        // 判据：只修"没有变化也创建快照"，不动 Resync 类型本身，也不清理历史点。

        yield return new("收口·重扫垃圾点", "无漂移的重新对齐不得创建恢复点", () =>
        {
            using var box = Sandbox.Create("rel-resync-nodrift");
            box.WriteFile("seed.txt", "seed");
            box.Protect();
            box.WaitForIndex("seed.txt");
            box.Flush();

            var beforeCount = box.SnapshotsRepo.List(box.RootId, 500, null, null).Count;
            var beforeRescans = box.Watch.Statistics.RescanCount;

            box.Watch.RequestRescan(box.RootId, "测试：无漂移");
            // RequestRescan 只把请求入队、不置位任何标志，所以"等 Scanning/NeedsRescan"
            // 会在请求还没被处理时立刻返回（假通过）。用引擎自己的 rescan 计数器证明它真的跑完。
            Check.True(box.WaitFor(
                () => box.Watch.Statistics.RescanCount > beforeRescans,
                20000, "重新对齐执行"), "重新对齐必须真的执行完");
            Check.True(box.WaitFor(() =>
            {
                var st = box.Watch.GetStates().First(s => s.RootId == box.RootId);
                return !st.Scanning && !st.NeedsRescan;
            }, 20000, "重新对齐完成"), "重新对齐应在超时前完成");

            Thread.Sleep(500);   // 若真建了快照，给落库留出时间
            var after = box.SnapshotsRepo.List(box.RootId, 500, null, null);
            Check.Equal(beforeCount, after.Count,
                "磁盘与记录一致时不得新增任何恢复点（原实现无条件建 Resync 点）");
            Check.Equal(0, after.Count(s => s.Kind == SnapshotKind.Resync), "更不该新增 Resync 点");
        });

        yield return new("收口·重扫垃圾点", "连续三次无漂移重新对齐：恢复点总数必须一个都不加", () =>
        {
            using var box = Sandbox.Create("rel-resync-nodrift3");
            box.WriteFile("seed.txt", "seed");
            box.Protect();
            box.WaitForIndex("seed.txt");
            box.Flush();

            var beforeCount = box.SnapshotsRepo.List(box.RootId, 500, null, null).Count;
            for (var i = 0; i < 3; i++)
            {
                var mark = box.Watch.Statistics.RescanCount;
                box.Watch.RequestRescan(box.RootId, "测试：无漂移 " + i);
                Check.True(box.WaitFor(
                    () => box.Watch.Statistics.RescanCount > mark,
                    20000, "第 " + i + " 次重新对齐执行"), "第 " + i + " 次重新对齐必须真的执行完");
                var done = box.WaitFor(() =>
                {
                    var st = box.Watch.GetStates().First(s => s.RootId == box.RootId);
                    return !st.Scanning && !st.NeedsRescan;
                }, 20000, "第 " + i + " 次重新对齐完成");
                Check.True(done, "第 " + i + " 次重新对齐应在超时前完成");
            }

            Thread.Sleep(500);
            var afterCount = box.SnapshotsRepo.List(box.RootId, 500, null, null).Count;
            Check.Equal(beforeCount, afterCount, "三次无漂移重新对齐之后恢复点数必须完全不变");
        });

        yield return new("收口·重扫垃圾点", "有漂移的重新对齐：恰好新增 1 个 Resync 点，清单等于修正后的磁盘", () =>
        {
            using var box = Sandbox.Create("rel-resync-drift");
            box.WriteFile("seed.txt", "seed");
            box.Protect();
            box.WaitForIndex("seed.txt");
            box.Flush();

            var beforeCount = box.SnapshotsRepo.List(box.RootId, 500, null, null).Count;

            // 让监听漏掉这次修改 —— 这正是"进程死亡期间的磁盘变化"的等价情形
            box.Watch.Pause(box.RootId);
            File.WriteAllText(box.Abs("seed.txt"), "changed-while-dead");
            box.Watch.RequestRescan(box.RootId, "测试：有漂移");

            // 暂停状态下标志位一开始就满足，等标志位会立刻返回（假通过）。
            // 直接等结果：出现新的状态点。
            Check.True(box.WaitFor(
                () => box.SnapshotsRepo.List(box.RootId, 500, null, null).Count > beforeCount,
                20000, "有漂移的重新对齐产生新的状态点"), "有漂移的重新对齐必须产生新的状态点");
            Check.True(box.WaitFor(() =>
            {
                var st = box.Watch.GetStates().First(s => s.RootId == box.RootId);
                return !st.Scanning;
            }, 20000, "扫描结束"), "扫描应当结束");
            box.Watch.Resume(box.RootId, rescan: false);

            var after = box.SnapshotsRepo.List(box.RootId, 500, null, null);
            Check.Equal(beforeCount + 1, after.Count, "有真实漂移时必须新增恰好 1 个恢复点");
            var newest = after[0];
            Check.Equal(SnapshotKind.Resync, newest.Kind, "新增的必须是 Resync（重新对齐）点");
            AssertManifestMatchesDisk(box, newest, "重新对齐后的清单必须等于修正后的磁盘状态");
        });

        yield return new("收口·脏退出", "干净退出后再次启动：不得自动补扫；脏退出则必须自动补扫并把 index 追平磁盘", () =>
        {
            var stamp = Guid.NewGuid().ToString("N")[..10];
            var dataDir = Path.Combine(Path.GetTempPath(), "lastregret-session-data", stamp);
            // 受保护目录必须**放在数据目录之外**（产品会拒绝把自身历史数据目录纳入保护范围）
            var watchDir = Path.Combine(Path.GetTempPath(), "lastregret-session-watched", stamp);
            Directory.CreateDirectory(watchDir);
            File.WriteAllText(Path.Combine(watchDir, "a.txt"), "初始内容");
            var previous = AppRuntime.CustomDataDirectory;
            try
            {
                AppRuntime.SetCustomDataDirectory(dataDir);
                long rootId;

                // ① 第一次正常启动：登记 + 基线 + **干净退出**
                using (var rt = AppRuntime.Create())
                {
                    var (ok, id, message) = rt.Watch.RegisterRoot(watchDir);
                    Check.True(ok, "登记目录：" + message);
                    rootId = id;
                    rt.Watch.RunBaseline(rootId);
                    Check.False(rt.PreviousShutdownWasUnclean, "全新数据目录不该被判成脏退出");
                }

                // ② 干净退出后再次启动：不得自动补扫
                using (var rt = AppRuntime.Create())
                {
                    Check.False(rt.PreviousShutdownWasUnclean,
                        "上次是干净退出，不该触发自动补扫（否则每次启动都白扫一遍）");
                    Check.Equal(0, (int)rt.Watch.Statistics.RescanCount, "干净启动不应产生重新对齐");
                    _ = rootId;
                }

                // ③ 模拟"上次被强杀"：把会话标记改成 open=1 + 一个**已死**的进程号
                //    （不调用内部 API 伪装，只是把持久状态改成强杀会留下的样子）
                using (var db = LastRegret.Data.LastRegretDatabase.Open(dataDir))
                {
                    var repo = new LastRegret.Data.SettingsRepository(db);
                    repo.SetRaw("session.open", "1");
                    repo.SetRaw("session.owner_pid", "999999");   // 不存在的进程
                }

                // 磁盘上出现"进程没运行时"的变化（强杀期间没被记录的那种）
                File.WriteAllText(Path.Combine(watchDir, "a.txt"), "强杀期间改的内容");

                // ④ 再次启动：必须自动对账，并把 index 追平磁盘
                using (var rt = AppRuntime.Create())
                {
                    Check.True(rt.PreviousShutdownWasUnclean, "检测到上次非正常退出 → 必须标记为脏");
                    Check.True(rt.StartupNotes.Any(n => n.Contains("上次没有正常退出")),
                        "启动说明里必须如实告诉用户正在重新检查");

                    var deadline = Environment.TickCount64 + 20000;
                    var entry = rt.Index.Get(rootId, "a.txt");
                    while (Environment.TickCount64 < deadline)
                    {
                        entry = rt.Index.Get(rootId, "a.txt");
                        var disk = LastRegret.Windows.Io.FileSystemReader.HashFileForTest(Path.Combine(watchDir, "a.txt"));
                        if (entry is { IsDeleted: false } && string.Equals(entry.Hash, disk, StringComparison.OrdinalIgnoreCase)) break;
                        Thread.Sleep(100);
                    }

                    var diskHash = LastRegret.Windows.Io.FileSystemReader.HashFileForTest(Path.Combine(watchDir, "a.txt"));
                    Check.NotNull(entry, "对账后 index 里必须有这个路径");
                    Check.Equal(diskHash, entry!.Hash ?? string.Empty,
                        "对账完成后 index 的内容哈希必须等于磁盘（这就是 P1-4 的全部要求）");
                    Check.True(rt.Watch.Statistics.RescanCount > 0, "应当真的跑过一次重新对齐");
                }

                // ⑤ 补扫完成后正常退出 → 标记必须回到干净；再启动不得再扫
                using (var rt = AppRuntime.Create())
                {
                    Check.False(rt.PreviousShutdownWasUnclean, "补扫后干净退出 → 下次启动不该再判脏");
                }
            }
            finally
            {
                AppRuntime.SetCustomDataDirectory(previous);
                try { Directory.Delete(dataDir, recursive: true); } catch { }
                try { Directory.Delete(watchDir, recursive: true); } catch { }
            }
        });

        // ═══════════════ P1-6：高频修改的历史粒度 ═══════════════

        yield return new("收口·历史粒度", "每轮 200ms 稳定间隔必须留下独立历史点（FINAL-WB-003 原始场景）", () =>
        {
            using var box = Sandbox.Create("rel-history-granularity");

            // 用**生产默认**的合并参数（测试默认值比生产更宽松，会掩盖这个缺陷）
            box.Settings.MergeWindowMs = 1500;
            box.Settings.SettleDelayMs = 200;
            box.Settings.MaxConfirmDelayMs = 8000;
            box.Settings.MaxMergeExtensions = 8;

            box.WriteFile("hot.txt", "round-start");
            box.Protect();
            box.Flush();

            const int rounds = 40;          // 4 秒写入 + 8 秒间隔 ≈ 12 秒
            for (var i = 0; i < rounds; i++)
            {
                box.WriteFile("hot.txt", $"R{i}-A");
                Thread.Sleep(50);
                box.WriteFile("hot.txt", $"R{i}-B");
                Thread.Sleep(50);
                box.WriteFile("hot.txt", $"R{i}-C");
                Thread.Sleep(200);          // 轮间稳定间隔（> SettleDelayMs）
            }

            var deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < deadline)
            {
                box.Flush();
                if (box.EventsOf("hot.txt").Count >= rounds) break;
                Thread.Sleep(100);
            }

            var events = box.EventsOf("hot.txt");
            var final = box.Index.Get(box.RootId, "hot.txt")?.Hash;
            Console.WriteLine($"      轮数={rounds} 写入={rounds * 3} 历史事件={events.Count}");

            Check.True(events.Count >= rounds / 2,
                $"{rounds} 轮、每轮间隔 200ms，至少要留下 {rounds / 2} 条独立历史，实际只有 {events.Count} 条" +
                "（旧实现会一路吞并到 MaxConfirmDelayMs，只剩个位数）");
            Check.True(final is not null &&
                       events.Any(e => string.Equals(e.HashAfter, final, StringComparison.OrdinalIgnoreCase)),
                "最终内容必须出现在历史里");
            Check.FileContent(box.Abs("hot.txt"), $"R{rounds - 1}-C", "磁盘最终内容必须正确");
        });

        yield return new("收口·历史粒度", "强制落库之后 120ms 内再修改：不得被去重吞掉", () =>
        {
            using var box = Sandbox.Create("rel-history-after-flush");
            box.Settings.SettleDelayMs = 200;

            box.WriteFile("f.txt", "v1");
            box.Protect();
            box.Flush();

            box.WriteFile("f.txt", "v2");
            box.WaitForIndex("f.txt");            // v2 已经进了索引
            var h2 = box.Index.Get(box.RootId, "f.txt")!.Hash;
            box.Flush();                          // 强制落库（保存时间点/恢复前/退出都会走这条路）

            box.WriteFile("f.txt", "v3");         // 紧接着再改（< 120ms 去重窗口）
            box.WaitForIndex("f.txt");            // 修复前：通知被吞 → 索引永远追不上 → 这里超时
            var h3 = box.Index.Get(box.RootId, "f.txt")!.Hash;

            Check.NotEqual(h2, h3, "v2 与 v3 必须是两个不同版本");
            var hashes = box.EventsOf("f.txt").Select(e => e.HashAfter).Where(h => h is not null).ToList();
            Check.True(hashes.Any(h => string.Equals(h, h2, StringComparison.OrdinalIgnoreCase)),
                "强制落库前的内容必须在历史里");
            Check.True(hashes.Any(h => string.Equals(h, h3, StringComparison.OrdinalIgnoreCase)),
                "强制落库之后紧接着的改动也必须在历史里（不得被当成上一条的重复通知丢掉）");
        });

        yield return new("收口·历史粒度", "保存时间点之后 120ms 内再修改：时间点可信 + 新改动仍在历史里", () =>
        {
            using var box = Sandbox.Create("rel-history-after-snapshot");
            box.Settings.SettleDelayMs = 200;

            box.WriteFile("g.txt", "s1");
            box.Protect();
            box.Flush();

            box.WriteFile("g.txt", "s2");
            box.WaitForIndex("g.txt");
            var h2 = box.Index.Get(box.RootId, "g.txt")!.Hash;

            var snap = box.Snapshot(SnapshotKind.Manual, "用户手动保存的时间点");

            box.WriteFile("g.txt", "s3");         // 保存之后紧接着再改
            box.WaitForIndex("g.txt");
            var h3 = box.Index.Get(box.RootId, "g.txt")!.Hash;

            // 时间点必须记录"保存那一刻"的内容，不能被后面这次改动污染
            var manifest = box.SnapshotService.LoadManifest(snap);
            Check.True(manifest.Entries.TryGetValue("g.txt", out var entry), "时间点清单里应当有 g.txt");
            Check.Equal(h2, entry!.Hash ?? string.Empty, "保存的时间点必须记录当时的内容（s2）");

            var hashes = box.EventsOf("g.txt").Select(e => e.HashAfter).Where(h => h is not null).ToList();
            Check.True(hashes.Any(h => string.Equals(h, h3, StringComparison.OrdinalIgnoreCase)),
                "保存时间点之后紧接着的改动必须在历史里");
            Check.FileContent(box.Abs("g.txt"), "s3", "磁盘内容必须是最后写入的 s3");
        });

        // ═══════════════ P1-1：还原时不得把内容库文件的属性（含加密标记）带到用户目录 ═══════════════

        yield return new("收口·还原属性", "还原小文件（raw 存储）不得复制源对象属性，且失败不留 .lrtmp-* 垃圾", () =>
        {
            using var box = Sandbox.Create("rel-materialize-attrs");
            box.WriteFile("src.txt", "内容库里的这一版");     // < 4096 字节 → 按 raw 存储
            box.Protect();
            box.WaitForIndex("src.txt");

            var entry = box.Index.Get(box.RootId, "src.txt");
            Check.NotNull(entry?.ObjectId, "应当已经有内容对象");
            var objectPath = box.Store.ObjectPath(entry!.Hash!);
            Check.True(File.Exists(objectPath), "内容对象文件应当存在：" + objectPath);

            // 给内容对象加上属性（EFS 加密标记在真实机器上就落在同一处；
            // 本机 EFS 不可用，用同样会被 File.Copy 一起搬走的属性来证明"不再传播"）
            var attrs = File.GetAttributes(objectPath);
            File.SetAttributes(objectPath, attrs | FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            Check.True((File.GetAttributes(objectPath) & FileAttributes.ReadOnly) != 0,
                "探针前提：内容对象应当带上了属性");

            try
            {
                var targetDir = Path.Combine(box.Root, "materialize-out");
                Directory.CreateDirectory(targetDir);
                var target = Path.Combine(targetDir, "restored.txt");

                var ok = box.Store.TryMaterialize(entry.ObjectId!.Value, target, out var error);
                Check.True(ok, "还原应当成功：" + error);
                Check.FileContent(target, "内容库里的这一版", "还原出来的内容必须与对象一致");

                var targetAttrs = File.GetAttributes(target);
                Check.False((targetAttrs & FileAttributes.ReadOnly) != 0,
                    "还原出来的文件不得继承内容库对象的只读属性（属性传播就是 EFS 失败的机制）");
                Check.False((targetAttrs & FileAttributes.Hidden) != 0, "不得继承隐藏属性");
                Check.False((targetAttrs & FileAttributes.System) != 0, "不得继承系统属性");

                // 目标目录里不得留下临时文件
                var leftovers = Directory.GetFiles(targetDir, "*.lrtmp-*");
                Check.Equal(0, leftovers.Length, "成功路径不得留下临时文件");

                // ── 强制失败路径：目标是"已存在的目录" → 替换必然失败 → 临时文件必须被清理 ──
                var blocked = Path.Combine(targetDir, "blocked");
                Directory.CreateDirectory(blocked);
                var failOk = box.Store.TryMaterialize(entry.ObjectId!.Value, blocked, out var failError);
                Check.False(failOk, "目标是已存在目录时应当失败");
                Check.NotNull(failError, "失败必须给出原因");
                var afterFail = Directory.GetFiles(targetDir, "*.lrtmp-*");
                Check.Equal(0, afterFail.Length,
                    "失败路径也必须清理临时文件，不能在用户目录留下垃圾：" + string.Join(",", afterFail.Select(Path.GetFileName)));
            }
            finally
            {
                try { File.SetAttributes(objectPath, attrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)); }
                catch (Exception) { }
            }
        });

        // ═══════════════ F-01：勾选目录的复现矩阵（引擎层 = CLI 真实调用路径）═══════════════
        //
        // 为什么在引擎层测：界面把用户勾选的目录交给 RestoreCoordinator.ExpandDirectoryPicks
        // 预先展开成子路径，而 **CLI 直接把 includePaths 交给引擎**。两条路都最终落到
        // RestoreEngine.BuildPreviewAt(rootId, atUtc, includePaths)。所以："传一个目录路径进去"
        // 在这里的行为就是产品真实行为。

        yield return new("收口·勾选目录", "矩阵1：普通目录 + 内部文件被修改（传目录路径）", () =>
        {
            using var box = Sandbox.Create("rel-f01-s1");
            box.WriteFile("folder/inner.txt", "A");
            box.WriteFile("other.txt", "无关");
            box.Protect();
            box.Flush();
            var t1 = box.Snapshot(SnapshotKind.Manual, "T1：inner=A");

            box.WriteFile("folder/inner.txt", "B");
            box.WaitForIndex("folder/inner.txt");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t1.TimestampUtc, new[] { "folder" });
            Check.Null(error, "预览不该报错");
            Check.NotNull(plan, "应当生成计划");
            Check.True(plan!.Steps.Any(s => s.RelativePath == "folder/inner.txt"),
                "勾选目录必须覆盖它内部的文件（否则就是 F-01 的症状）。实际步骤：" +
                string.Join(" | ", plan.Steps.Select(s => s.Action + " " + s.RelativePath)));

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);
            Check.FileContent(box.Abs("folder/inner.txt"), "A", "内部文件必须恢复到目标版本");
            Check.FileContent(box.Abs("other.txt"), "无关", "未勾选的路径不得被改动");
        });

        yield return new("收口·勾选目录", "矩阵2：目录内多个文件与子目录（传目录路径）", () =>
        {
            using var box = Sandbox.Create("rel-f01-s2");
            box.WriteFile("folder/a.txt", "A1");
            box.WriteFile("folder/b.txt", "B1");
            box.WriteFile("folder/sub/c.txt", "C1");
            box.Protect();
            box.Flush();
            var t1 = box.Snapshot(SnapshotKind.Manual, "T1");

            box.WriteFile("folder/a.txt", "A2"); box.WaitForIndex("folder/a.txt");
            box.WriteFile("folder/b.txt", "B2"); box.WaitForIndex("folder/b.txt");
            box.WriteFile("folder/sub/c.txt", "C2"); box.WaitForIndex("folder/sub/c.txt");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t1.TimestampUtc, new[] { "folder" });
            Check.Null(error, "预览不该报错");
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);

            Check.FileContent(box.Abs("folder/a.txt"), "A1", "a.txt 必须恢复");
            Check.FileContent(box.Abs("folder/b.txt"), "B1", "b.txt 必须恢复");
            Check.FileContent(box.Abs("folder/sub/c.txt"), "C1", "深层子文件必须恢复");
        });

        yield return new("收口·勾选目录", "矩阵3：目标时刻之后新增的文件（传目录路径）", () =>
        {
            using var box = Sandbox.Create("rel-f01-s3");
            box.WriteFile("folder/a.txt", "A1");
            box.Protect();
            box.Flush();
            var t1 = box.Snapshot(SnapshotKind.Manual, "T1");

            box.WriteFile("folder/new.txt", "目标时刻之后新增");
            box.WaitForIndex("folder/new.txt");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t1.TimestampUtc, new[] { "folder" });
            Check.Null(error, "预览不该报错");
            Check.True(plan!.Steps.Any(s => s.RelativePath == "folder/new.txt"),
                "勾选目录时，目录里「目标时刻之后新增」的文件也应当进入计划（按删除确认规则处理）");
            var removal = plan.Steps.First(s => s.RelativePath == "folder/new.txt");
            Check.True(removal.RequiresConfirmation,
                "删除「目标时刻之后新增」的路径必须需要用户确认");

            // 未确认时应当被拒绝
            var refused = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: false);
            Check.False(refused.Ok, "未确认删除新增文件时应当被拒绝");
            Check.FileContent(box.Abs("folder/new.txt"), "目标时刻之后新增", "被拒绝时不得动它");

            var accepted = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(accepted.Ok, "确认后应当成功：" + accepted.Message);
            Check.FileMissing(box.Abs("folder/new.txt"), "确认后新增文件应当被移除");
            Check.FileContent(box.Abs("folder/a.txt"), "A1", "原有文件保持目标版本");
        });

        yield return new("收口·勾选目录", "矩阵4：目录被删除后又重新创建（传目录路径）", () =>
        {
            using var box = Sandbox.Create("rel-f01-s4");
            box.WriteFile("folder/inner.txt", "sub content");
            box.Protect();
            box.Flush();
            var t1 = box.Snapshot(SnapshotKind.Manual, "T1：sub content");

            // 删除整个目录（索引看到），再重新创建同名目录并改内容
            box.DeleteFile("folder");
            box.WaitFor(() => box.Index.Get(box.RootId, "folder") is { IsDeleted: true }, 6000, "目录被标记删除");
            box.Flush();
            Thread.Sleep(300);
            box.WriteFile("folder/inner.txt", "new inner");
            box.WaitForIndex("folder/inner.txt");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t1.TimestampUtc, new[] { "folder" });
            Check.Null(error, "预览不该报错");
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);
            Check.FileContent(box.Abs("folder/inner.txt"), "sub content",
                "重建目录里的文件必须被恢复成目标版本（这正是白盒报告的场景）");
        });

        yield return new("收口·勾选目录", "矩阵5：目录被改名后勾选原名（传目录路径）", () =>
        {
            using var box = Sandbox.Create("rel-f01-s5");
            box.WriteFile("A/inner.txt", "原名下的内容");
            box.Protect();
            box.Flush();
            var t1 = box.Snapshot(SnapshotKind.Manual, "T1：A/inner.txt");

            box.MovePath("A", "B");
            box.WaitFor(() => box.Index.Get(box.RootId, "B/inner.txt") is { IsDeleted: false }, 8000, "改名后索引跟上");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t1.TimestampUtc, new[] { "A" });
            Check.Null(error, "预览不该报错");
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);

            Check.FileContent(box.Abs("A/inner.txt"), "原名下的内容", "勾选原名目录后，原名下的文件必须回来");
        });

        yield return new("收口·勾选目录", "矩阵7：目录自身无变化、只有内部文件变化（传目录路径）", () =>
        {
            using var box = Sandbox.Create("rel-f01-s7");
            box.WriteFile("folder/inner.txt", "A");
            box.Protect();
            box.Flush();
            var t1 = box.Snapshot(SnapshotKind.Manual, "T1");

            box.WriteFile("folder/inner.txt", "B");
            box.WaitForIndex("folder/inner.txt");
            box.Flush();

            // 目录节点自身在两个时间点都存在（NoChange），变化只在内部文件上
            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t1.TimestampUtc, new[] { "folder" });
            Check.Null(error, "预览不该报错");
            Check.True(plan!.HasEffect,
                "目录自身没有变化，但内部文件变了 —— 勾选目录必须仍然有实际动作（不能被 NoChange 过滤掉）");
            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);
            Check.FileContent(box.Abs("folder/inner.txt"), "A", "内部文件必须恢复到目标版本");
        });

        // ═══════════════ BB-015：安全点覆盖校验 ═══════════════

        yield return new("收口·安全点覆盖", "要覆盖的当前文件不在安全点里 → 必须拒绝（否则 undo 回不到执行前）", () =>
        {
            using var box = Sandbox.Create("rel-safety-missing");
            box.WriteFile("x.txt", "v1");
            box.Protect();
            box.Flush();
            var t0 = box.Snapshot(SnapshotKind.Manual, "T0：x.txt = v1");

            // 让索引认为 x.txt 已经不存在（快照链里也就不会有它）
            box.DeleteFile("x.txt");
            box.WaitFor(() => box.Index.Get(box.RootId, "x.txt") is { IsDeleted: true }, 6000, "x.txt 被标记删除");
            box.Flush();

            // 暂停监听后，磁盘上重新出现一个同名文件 —— 索引与安全点都不知道它
            box.Watch.Pause(box.RootId);
            box.WriteFile("x.txt", "未经记录的内容");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t0.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            Check.NotNull(plan, "应当生成恢复计划");
            Check.True(plan!.HasEffect, "目标时刻有 x.txt，当前索引里没有 → 应当有内容要恢复");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);

            Check.False(outcome.Ok,
                "必须拒绝：x.txt 当前确实是一个文件、这次会覆盖它，而安全点证明不了它的当前内容（BB-015）");
            Check.Contains(outcome.Message ?? "", "安全点", "拒绝原因必须说清楚是安全点没有覆盖它");
            Check.FileContent(box.Abs("x.txt"), "未经记录的内容",
                "被拒绝时磁盘必须保持原样 —— 绝不能把用户这份没被记录的内容冲掉");
        });

        yield return new("收口·安全点覆盖", "要恢复的路径当前不存在 → 不得因为安全点缺项而误拒绝", () =>
        {
            using var box = Sandbox.Create("rel-safety-absent");
            box.WriteFile("x.txt", "v1");
            box.Protect();
            box.Flush();
            var t0 = box.Snapshot(SnapshotKind.Manual, "T0：x.txt = v1");

            box.DeleteFile("x.txt");
            box.WaitFor(() => box.Index.Get(box.RootId, "x.txt") is { IsDeleted: true }, 6000, "x.txt 被标记删除");
            box.Flush();

            // 磁盘上确实没有这个文件（情况 A）→ 没有任何内容会丢，必须放行
            Check.FileMissing(box.Abs("x.txt"), "这一条用例的前提是磁盘上不存在该文件");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t0.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);

            Check.True(outcome.Ok, "路径当前不存在时不该因为安全点缺项而拒绝：" + outcome.Message);
            Check.FileContent(box.Abs("x.txt"), "v1", "文件应当被恢复回来");
        });

        yield return new("收口·安全点覆盖", "安全点齐全时：覆盖 → 成功 → undo 逐字节回到执行前", () =>
        {
            using var box = Sandbox.Create("rel-safety-ok");
            box.WriteFile("x.txt", "v1");
            box.WriteFile("keep.txt", "无关内容");
            box.Protect();
            box.Flush();
            var t0 = box.Snapshot(SnapshotKind.Manual, "T0：x.txt = v1");

            box.WriteFile("x.txt", "v2");
            box.WaitForIndex("x.txt");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, t0.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "安全点齐全时应当成功：" + outcome.Message);
            Check.FileContent(box.Abs("x.txt"), "v1", "应当恢复到目标版本");

            // undo：必须逐字节回到"v2"这个执行前状态
            var (undoPlan, undoError) = box.Restore.BuildUndoPreview(outcome.OperationId);
            Check.Null(undoError, "撤销预览不该报错");
            Check.NotNull(undoPlan, "应当生成撤销计划");
            var undone = box.Restore.ExecuteUndo(outcome.OperationId, undoPlan!.Fingerprint, allowNewRemovals: true);
            Check.True(undone.Ok, "撤销应当成功：" + undone.Message);
            Check.FileContent(box.Abs("x.txt"), "v2", "撤销必须回到执行前的内容（逐字节一致）");
            Check.FileContent(box.Abs("keep.txt"), "无关内容", "无关文件不受影响");
        });

        // ═══════════════ BB-009：重扫事件的内容对象引用 ═══════════════

        yield return new("收口·重扫内容引用", "重扫记录的变化必须带上真实内容对象（否则版本被误标「不可恢复」）", () =>
        {
            using var box = Sandbox.Create("rel-rescan-obj");
            box.WriteFile("a.txt", "第一版");
            box.Protect();
            box.Flush();

            var before = box.Index.Get(box.RootId, "a.txt");
            Check.NotNull(before?.ObjectId, "第一版的内容应当已经进了内容库");

            // 模拟"程序没运行期间文件被改过"：暂停监听后改内容，再走重扫
            box.Watch.Pause(box.RootId);
            box.WriteFile("a.txt", "第二版（离线期间改的）");
            box.Rescanner.Align(box.WatchedRoot, ResyncMode.Resync);

            var ev = box.EventsOf("a.txt").LastOrDefault(e => e.Operation == OperationType.Modified);
            Check.NotNull(ev, "重扫应当记录一条「修改」事件");

            // ① 事件必须带上"变化后"的内容对象
            Check.NotNull(ev!.ObjectIdAfter,
                "重扫事件必须带上内容对象引用（BB-009：形参收了却从没写进事件）");
            Check.True(box.Store.Exists(ev.ObjectIdAfter!.Value),
                "这个内容对象必须真的在内容库里（不能是伪造的编号）");

            // ② "变化前"的对象也要有（那是上一次记录的那一份）
            Check.NotNull(ev.ObjectIdBefore, "修改事件的「变化前」对象应当是上一版内容对象");

            // ③ 版本行必须能证明"可恢复"：MainViewModel 用 v.ObjectId + Store.Exists 判定
            var versions = box.Versions.ListForPath(box.RootId, "a.txt");
            Check.True(versions.Count > 0, "应当有历史版本行");
            var recoverable = versions.Where(v => v.ObjectId is not null && box.Store.Exists(v.ObjectId.Value)).ToList();
            Check.True(recoverable.Count > 0,
                "重扫产生的版本必须至少有一条是「内容在库、可恢复」，实际版本行：" +
                string.Join(" | ", versions.Select(v => $"{v.Hash?[..Math.Min(8, v.Hash?.Length ?? 0)]} obj={v.ObjectId}")));
            Check.True(recoverable.Any(v => string.Equals(v.Hash, ev.HashAfter, StringComparison.OrdinalIgnoreCase)),
                "新版本（重扫记下的那一份）必须在可恢复之列");
        });

        yield return new("收口·重扫内容引用", "没有留存内容时必须是 null，绝不能伪造对象编号", () =>
        {
            using var box = Sandbox.Create("rel-rescan-nocontent");
            box.WriteFile("big.bin", "0123456789");
            box.Protect();
            box.Flush();

            // 智能留存：把**这个根**的单文件留存上限压到 4 字节 → 这次变化只记事实、不留内容
            // （上限优先取根自己的设置，其次才是全局设置）
            box.WatchedRoot.MaxFileSizeBytes = 4;

            box.Watch.Pause(box.RootId);
            box.WriteFile("big.bin", "9876543210");
            var report = box.Rescanner.Align(box.WatchedRoot, ResyncMode.Resync);
            Check.True(report.ContentNotStored >= 1, "这次重扫应当如实报告「内容未保存」");

            var ev = box.EventsOf("big.bin").LastOrDefault(e => e.Operation == OperationType.Modified);
            Check.NotNull(ev, "重扫应当记录一条「修改」事件");
            Check.Null(ev!.ObjectIdAfter, "没有留存内容时必须是 null —— 绝不能写成 0 或任何伪造值");
            Check.NotNull(ev.HashAfter, "哈希仍然应当记录（只记事实）");
        });

        // ═══════════════ BB-008：时间线的时间范围过滤 ═══════════════

        yield return new("收口·时间线范围", "时间范围必须下推到查询：比「最新 limit 条」更早的时间段仍然查得到", () =>
        {
            using var box = Sandbox.Create("rel-timeline-range");
            box.WriteFile("seed.txt", "seed");
            box.Protect();

            // 造出明显超过 limit 的恢复点（每个时间点写一个**不同**的文件：
            // 同一路径在高频改写时会被 120 ms 原始通知去重窗口吃掉，那是另一件事）
            const int total = 40;
            const int limit = 10;
            var times = new List<DateTime>(total);
            for (var i = 0; i < total; i++)
            {
                box.WriteFile($"f{i:D2}.txt", "内容 " + i);
                box.WaitForIndex($"f{i:D2}.txt");
                box.Flush();
                times.Add(box.Snapshot(SnapshotKind.Manual, "T" + i).TimestampUtc);
                Thread.Sleep(5);
            }

            string Describe(IReadOnlyList<SnapshotPoint> points) =>
                string.Join(",", points.Select(p => "T" + times.IndexOf(p.TimestampUtc)));

            // ① 最近范围（落在最新 limit 条之内）
            var recent = box.Compare.ListPoints(box.RootId, times[35], null, limit);
            Check.Equal(5, recent.Count, "最近范围应当返回 5 个（实际：" + Describe(recent) + "）");

            // ② 中间范围
            var middle = box.Compare.ListPoints(box.RootId, times[15], times[19], limit);
            Check.Equal(5, middle.Count, "中间范围应当返回 5 个（实际：" + Describe(middle) + "）");

            // ③ **关键**：比"最新 limit 条"更早的范围 —— 旧实现（先 LIMIT 再内存过滤）会返回空
            var older = box.Compare.ListPoints(box.RootId, times[0], times[4], limit);
            Check.Equal(5, older.Count,
                "目标范围在全局最新 " + limit + " 条之外时，仍然必须正确返回（BB-008）——实际：" + Describe(older));
            Check.Equal(times[4].ToString("o"), older[0].TimestampUtc.ToString("o"), "应当按时间倒序返回（最新的在前）");
            Check.Equal(times[0].ToString("o"), older[^1].TimestampUtc.ToString("o"), "最早的应当在最后");

            // ④ 只有 from
            var fromOnly = box.Compare.ListPoints(box.RootId, times[30], null, 100);
            Check.Equal(10, fromOnly.Count, "from only：应当返回 T30..T39");

            // ⑤ 只有 to（注意：Protect() 建立的**基线快照**早于 T0，也会落在这个范围里）
            var toOnly = box.Compare.ListPoints(box.RootId, null, times[9], 100);
            Check.Equal(11, toOnly.Count, "to only：应当返回 基线 + T0..T9（实际：" + Describe(toOnly) + "）");
            Check.Equal(times[9].ToString("o"), toOnly[0].TimestampUtc.ToString("o"), "最新的应当是 T9");
            Check.Equal(SnapshotKind.Baseline, toOnly[^1].Kind, "最早的那条应当是基线快照");
            Check.True(toOnly.All(p => p.TimestampUtc <= times[9]), "返回结果不得超出 to 边界");

            // ⑥ from + to
            var both = box.Compare.ListPoints(box.RootId, times[10], times[29], 100);
            Check.Equal(20, both.Count, "from + to：应当返回 T10..T29");

            // ⑦ 空结果（范围之外）
            var empty = box.Compare.ListPoints(box.RootId, times[39].AddMinutes(1), null, 100);
            Check.Equal(0, empty.Count, "范围之外应当是空结果，而不是报错或返回别的东西");

            // ⑧ limit 仍然生效（即使范围内有更多）
            var limited = box.Compare.ListPoints(box.RootId, null, null, 3);
            Check.Equal(3, limited.Count, "limit 必须继续生效");
            Check.Equal(times[39].ToString("o"), limited[0].TimestampUtc.ToString("o"), "limit 生效时也应取最新的那些");
        });

        yield return new("收口·时间线范围", "恢复页的时间点列表不得被「最近 N 天」截断（老恢复点必须可达）", () =>
        {
            var vm = File.ReadAllText(
                Path.Combine(RepoRootForTests(), "src", "LastRegret.App", "MainViewModel.cs"));

            // 真实缺陷（P1-2A）：这里曾经用 _timelineDays（默认 1 天）过滤恢复点，
            // 于是超过一天的恢复点在界面上永远够不到 —— 数据库里有、CLI 的 timeline
            // 列得出、指纹也算得出来，用户却在界面上选不到。
            // 恢复点不是日志：只要它还存在，就必须有办法在界面上选到它。
            var start = vm.IndexOf("public void ReloadRestoreChoices()", StringComparison.Ordinal);
            Check.True(start > 0, "应能在 MainViewModel.cs 里找到 ReloadRestoreChoices");

            // 方法体里不得再出现 _timelineDays —— 断言只看代码，
            // 注释里为了交代缺陷来源而提到这个字段名不算违规。
            var window = vm.Substring(start, Math.Min(4000, vm.Length - start));
            var codeOnly = string.Join("\n", window.Split('\n').Select(line =>
            {
                var cut = line.IndexOf("//", StringComparison.Ordinal);
                return cut >= 0 ? line.Substring(0, cut) : line;
            }));

            Check.False(codeOnly.Contains("_timelineDays"),
                "恢复页的时间点列表不得再按「最近 N 天」过滤（老恢复点会变得不可达）");
            Check.Contains(codeOnly, "ListTimePoints", "时间点列表必须来自引擎的时间线查询");
            Check.Contains(codeOnly, "new DateTime(2000, 1, 1",
                "应当以固定哨兵表示「无下界」，而不是当前时间窗");
        });

        // ═══════════════ 第三组：UI 反馈（F-03 / F-06 / F-07 / F-08）═══════════════

        yield return new("收口·界面反馈", "提示横幅必须真的被绑定（InfoBanner 不得写完就消失）", () =>
        {
            var appDir = Path.Combine(RepoRootForTests(), "src", "LastRegret.App");
            var xaml = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml"));

            // 原型缺陷：MainViewModel 有十余处写 InfoBanner（标记删除结果、恢复集合为空的原因、
            // "所选内容已经是目标状态"等），但 XAML 从未绑定它 —— 用户点了按钮却"什么都没发生"。
            Check.Contains(xaml, "{Binding InfoBanner}", "InfoBanner 必须被绑定到界面，否则所有提示都是静默的");
            Check.Contains(xaml, "{Binding HasInfoBanner,", "提示横幅还需要可见性绑定");
        });

        yield return new("收口·界面反馈", "无变化时必须给出明确结论（而不是静默什么都不做）", () =>
        {
            var appDir = Path.Combine(RepoRootForTests(), "src", "LastRegret.App");
            var coordinator = File.ReadAllText(Path.Combine(appDir, "RestoreCoordinator.cs"));
            var vm = File.ReadAllText(Path.Combine(appDir, "MainViewModel.cs"));

            Check.Contains(coordinator, "所选内容已经是目标状态，无需恢复。",
                "无变化时必须明确告诉用户「无需恢复」");
            Check.Contains(vm, "InfoBanner = error", "这条结论必须被展现给用户（走提示横幅）");
        });

        yield return new("收口·界面反馈", "扫描进度不得出现「已处理 34000 / 13600（100%）」这种错数", () =>
        {
            var appDir = Path.Combine(RepoRootForTests(), "src", "LastRegret.App");
            var vm = File.ReadAllText(Path.Combine(appDir, "MainViewModel.cs"));

            // 只有在"总量不小于已处理数"时才允许显示百分比；否则退回"已处理 X 项"。
            Check.Contains(vm, "p.EstimatedTotal > 0 && p.EstimatedTotal >= p.Processed",
                "显示百分比前必须确认总量是可信的（不得出现 34000/13600（100%））");
        });

        yield return new("收口·界面反馈", "跨时间点勾选必须被说清楚：弹窗列出实际文件、摘要标出来源时间点数", () =>
        {
            var appDir = Path.Combine(RepoRootForTests(), "src", "LastRegret.App");
            var vm = File.ReadAllText(Path.Combine(appDir, "MainViewModel.cs"));

            // 确认弹窗必须列出**实际要恢复的文件**（按来源时间点分组），而不是只给一个计数
            Check.Contains(vm, "picksByTime", "确认弹窗必须按来源时间点分组列出实际文件");
            Check.Contains(vm, "，…还有 {paths.Count - 6} 个", "文件太多时要如实交代还有多少");
            // 勾选数与计划步数不一致时必须解释清楚
            Check.Contains(vm, "项与目标状态一致，无需改动", "必须解释「勾了 3 个为什么只说恢复 1 个」");
            // 底部摘要必须标出来自多个时间点
            Check.Contains(vm, "（来自 {timePoints} 个时间点）", "跨时间点勾选必须在摘要里说明");
        });

        yield return new("收口·界面反馈", "滚动一律交给滚轮：界面不得出现任何可见滚动条（下拉与多行文本框同样）", () =>
        {
            var appDir = Path.Combine(RepoRootForTests(), "src", "LastRegret.App");
            var app = File.ReadAllText(Path.Combine(appDir, "App.xaml"));
            var window = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml"));

            // 用户要求：不要看见任何上下滑块，统一用鼠标滚轮。
            // Hidden 只影响外观 —— 滚轮与方向键照常滚动，这是纯风格统一，不损失任何功能。
            foreach (var (name, text) in new (string, string)[] { ("App.xaml", app), ("MainWindow.xaml", window) })
            {
                Check.False(text.Contains("ScrollBarVisibility=\"Auto\""),
                    name + " 里不得再出现 Auto 滚动条（会在右侧画出滑块）");
                Check.False(text.Contains("ScrollBarVisibility=\"Visible\""),
                    name + " 里不得出现强制可见的滚动条");
            }

            // 时间点 / 目标时间点下拉：Popup 里那个 ScrollViewer 必须是 Hidden。
            // 原来这里是 Auto —— 恢复点一多，下拉右侧就冒出长滑块。
            Check.Contains(app, "<ScrollViewer VerticalScrollBarVisibility=\"Hidden\">",
                "下拉列表的滚动条必须隐藏");
            // 兜底 setter：没显式套 ToolList 的列表也不得冒出滚动条
            Check.Contains(app, "Property=\"ScrollViewer.VerticalScrollBarVisibility\" Value=\"Hidden\"",
                "列表样式（含隐式兜底）必须保持滚动条隐藏");
        });

        yield return new("收口·界面反馈", "时间点身份（1/2）：同一分钟里的多个时间点，数据上必须可区分", () =>
        {
            using var box = Sandbox.Create("rel-point-identity");
            box.WriteFile("seed.txt", "seed");
            box.Protect();

            // 两个恢复点，间隔极短 —— 界面上"友好时间"精确到分钟，很可能长得一模一样。
            // 所以界面必须还有 id / 备注可用，否则用户无法确认自己选的是哪一个。
            box.WriteFile("a.txt", "A");
            box.WaitForIndex("a.txt");
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "A");
            box.WriteFile("b.txt", "B");
            box.WaitForIndex("b.txt");
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "B");

            var points = box.Compare.ListPoints(box.RootId, null, null, 10);
            var a = points.FirstOrDefault(p => p.Note == "A");
            var b = points.FirstOrDefault(p => p.Note == "B");
            Check.NotNull(a, "应当能按备注找到第一个恢复点（备注本身就是身份）");
            Check.NotNull(b, "应当能按备注找到第二个恢复点");
            Check.True(a!.SnapshotId != b!.SnapshotId, "两个恢复点的 snapshot id 必须不同");
            Check.True(a.SnapshotId > 0 && b.SnapshotId > 0,
                "id 必须是真实编号 —— 界面上的「#id」兜底就靠它");
            // 时间戳相同不重要，重要的是"还有别的东西可以区分"：id 有、备注有
            Check.Equal("A", a.Note, "备注必须原样带出来（界面要显示它）");
            Check.Equal("B", b.Note, "备注必须原样带出来（界面要显示它）");
        });

        yield return new("收口·界面反馈", "时间点身份（2/2）：下拉必须同时给时间与身份，不得回退成只显示时间", () =>
        {
            var appDir = Path.Combine(RepoRootForTests(), "src", "LastRegret.App");
            var window = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml"));
            var models = File.ReadAllText(Path.Combine(appDir, "DisplayModels.cs"));
            var vm = File.ReadAllText(Path.Combine(appDir, "MainViewModel.cs"));

            // 身份三件套：备注/来源 → 时间 · 身份 → 弱化的 #id 兜底
            Check.Contains(models, "public string IdentityLabel", "必须能从备注（或来源）得出身份");
            Check.Contains(models, "public string IdentifiedTime", "必须提供「时间 · 身份」组合文本");
            Check.Contains(models, "public string IdSuffix", "必须有编号兜底（备注相同或为空时靠它）");
            Check.Contains(models, "Point.SnapshotId", "编号必须来自真实的 snapshot id");
            Check.Contains(models, "!string.IsNullOrWhiteSpace(Note) ? Note! : UserKindText",
                "备注为空时必须回落到来源文本，不允许身份为空");

            // 两个时间点下拉（时间点 / 目标时间点）都要用它，且不得再只绑时间
            Check.False(window.Contains("{Binding FriendlyTime}"),
                "时间点下拉不得再只显示时间 —— 同一分钟里的两个时间点会无法区分");
            // 用前缀统计，避免漏掉 ", Mode=OneWay}" 这种显式模式写法
            // （上一版断言卡在字面 "{Binding IdentifiedTime}" 上，加了 Mode 之后就统计不到了）
            var identified = window.Split("{Binding IdentifiedTime").Length - 1;
            Check.True(identified >= 2, "两个下拉都要显示身份（实际出现 " + identified + " 次）");
            var ids = window.Split("{Binding IdSuffix").Length - 1;
            Check.True(ids >= 2, "两个下拉都要有编号兜底（实际出现 " + ids + " 次）");

            // 工具栏末端的确认文本、恢复集合的来源标签同样不能退化
            Check.Contains(vm, "_selectedTimePointChoice?.IdentifiedTime",
                "下拉旁边的确认文本必须带身份");
            Check.Contains(vm, "SnapshotLabel = snapRow.IdentifiedTime",
                "恢复集合的来源标签必须带身份");
        });

        yield return new("收口·界面反馈", "身份展示绑定必须显式 OneWay（默认模式会让 MainWindow 启动即崩）", () =>
        {
            var appDir = Path.Combine(RepoRootForTests(), "src", "LastRegret.App");
            var window = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml"));

            // 真实崩溃记录：ComboBox 还没选中任何项时，ItemTemplate 仍会以 null DataContext
            // （即 MS.Internal.NamedObject）求值一次。此时绑定若走默认模式做写回，
            // WPF 会抛「无法对只读属性进行 TwoWay 绑定」，MainWindow 直接创建失败 ——
            // 界面根本起不来。所以只读身份展示绑定必须一条不漏地显式 OneWay。
            // （只断言"源码里有 IdentifiedTime"是不够的，它挡不住这次启动崩溃。）
            var exprs = System.Text.RegularExpressions.Regex.Matches(window, @"\{Binding\s+[^}]*\}");
            Check.True(exprs.Count > 0, "应当能从 MainWindow.xaml 里解析出绑定表达式");

            var identityNames = new[] { "IdentifiedTime", "IdSuffix", "IdentityLabel" };
            var offenders = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in exprs)
            {
                var text = m.Value;
                if (!identityNames.Any(n => text.Contains(n, StringComparison.Ordinal))) continue;
                if (!text.Contains("Mode=OneWay", StringComparison.Ordinal)) offenders.Add(text);
            }

            Check.Equal(0, offenders.Count,
                "身份展示绑定必须显式 Mode=OneWay，缺失的：" + string.Join(" | ", offenders));

            // 两个时间点下拉的无障碍名称必须反映当前选中的身份，而不只是时间
            Check.Contains(window,
                "AutomationProperties.Name=\"{Binding SelectedTimePointChoice.IdentifiedTime, Mode=OneWay}\"",
                "「时间点」下拉的无障碍名称必须绑定完整身份且为 OneWay");
            Check.Contains(window,
                "AutomationProperties.Name=\"{Binding SelectedTargetPoint.IdentifiedTime, Mode=OneWay}\"",
                "「目标时间点」下拉的无障碍名称必须绑定完整身份且为 OneWay");
        });

        // ═══════════════ F-02：句柄归属探测的资源失控 ═══════════════

        yield return new("收口·归属探测", "结构断言：句柄快照必须带短 TTL 缓存，且解析对象名不得每次新建线程", () =>
        {
            var dir = Path.Combine(RepoRootForTests(), "src", "LastRegret.Windows", "Processes");
            var enumerator = File.ReadAllText(Path.Combine(dir, "SystemHandleEnumerator.cs"));
            var probe = File.ReadAllText(Path.Combine(dir, "ProcessProbe.cs"));

            // ① 快照必须缓存（否则每个事件一次全系统枚举，0.2 秒 × 事件数）
            Check.Contains(enumerator, "CaptureTtlMs", "句柄快照必须有短 TTL 缓存");
            Check.Contains(enumerator, "TryCaptureFresh", "缓存命中时不得真的再去枚举");

            // ② 解析对象名不得每次新建线程（旧实现的线程泄漏根源）
            Check.False(enumerator.Contains("worker.Join("),
                "不得再出现「每次调用新建线程 + Join 超时后丢弃」的写法（线程会永久泄漏）");
            var newThreadCount = enumerator.Split("new Thread(").Length - 1;
            Check.Equal(1, newThreadCount, "解析对象名只允许有**唯一一个**专职线程");

            // ③ 必须有熔断与有界队列
            Check.Contains(enumerator, "CircuitBreakMs", "超时后必须有熔断，避免每个事件都赔上超时时间");
            Check.Contains(enumerator, "QueryQueue", "解析请求必须走有界队列");

            // ④ 能力自检为"不可用"时不得再做注定失败的枚举
            Check.Contains(probe, "HandleEnumerationAvailable", "必须先看能力自检结论");
            Check.Contains(probe, "maxNameQueries", "每次调用必须有尝试次数上限");
        });

        yield return new("收口·归属探测", "开启归属探测时：批量事件必须全部落库，且线程数不得失控", () =>
        {
            // 这条用例用**真实的归属探测**（默认开启）跑一批事件：
            // 既要证明修复没有靠"关掉归属"换性能（事件一条都不能少），
            // 又要守住"线程不随事件数失控"这条底线。
            using var box = Sandbox.Create("rel-attribution", enableProcessAttribution: true);
            box.Protect();

            const int files = 150;
            for (var i = 0; i < files; i++) box.WriteFile($"attr/{i}.txt", "内容 " + i);

            box.WaitFor(() =>
            {
                var paths = box.AllEvents().Select(e => e.RelativePath).ToHashSet(PathUtil.Comparer);
                for (var i = 0; i < files; i++)
                {
                    if (!paths.Contains($"attr/{i}.txt")) return false;
                }
                return true;
            }, 120_000, $"{files} 个文件的事件必须全部落库（开启归属探测时也不得丢）");

            var threads = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
            Console.WriteLine($"      开启归属探测、处理 {files} 个事件之后：进程内线程 = {threads}");
            Check.True(threads < 150,
                $"线程数不得随事件数失控（处理 {files} 个事件后仍有 {threads} 个线程）");
        });

        // ═══════════════ BB-007 / BB-013 / BB-010 / BB-005 ═══════════════

        yield return new("收口·恢复记账", "所有步骤都因冲突被跳过：FilesChanged 必须为 0，且不得提供撤销入口", () =>
        {
            using var box = Sandbox.Create("rel-skip-undo");
            box.WriteFile("a.txt", "第一版");
            box.Protect();
            box.Flush();
            var target = box.Snapshot(SnapshotKind.Manual, "T0");

            box.WriteFile("a.txt", "第二版");
            box.WaitForIndex("a.txt");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, target.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            Check.NotNull(plan, "应当生成恢复计划");
            Check.True(plan!.HasEffect, "应当有内容需要恢复");

            // 预览之后再改一次：执行时磁盘内容与"预览时刻"不一致 → 每一步都会被冲突保护跳过
            box.Watch.Pause(box.RootId);
            box.WriteFile("a.txt", "预览之后又改了");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);

            // 计划里唯一的步骤也过期了 → 整单拒绝（预检在建安全点之前就拦下）：
            // 没有任何步骤被执行，也就谈不上"跳过"。
            Check.True(outcome.Rejected, "计划全部过期时必须被拒绝：" + outcome.Message);
            Check.Equal(0, outcome.Skipped, "拒绝时不产生被跳过的步骤记录");
            Check.Equal(0, outcome.FilesChanged,
                "被跳过的步骤没有改动磁盘，不得计入 FilesChanged（BB-007：跳过被当成真改动）");
            Check.False(outcome.CanUndo, "磁盘一点没变，不该打开撤销入口");
            Check.Equal(0L, outcome.OperationId, "拒绝时不得创建恢复记录");
            Check.FileContent(box.Abs("a.txt"), "预览之后又改了", "被跳过的文件必须原样保留，不能被覆盖");
        });

        yield return new("收口·清理保护", "自动清理不得删除「被恢复操作引用」的快照（否则那次恢复失去回退依据）", () =>
        {
            using var box = Sandbox.Create("rel-cleanup-ref");
            box.WriteFile("a.txt", "第一版");
            box.Protect();
            box.Flush();

            // 造一个 Auto 快照当恢复目标（Auto 是清理的合法候选，Manual/Baseline 本来就不会被删）
            box.WriteFile("a.txt", "第二版");
            box.WaitForIndex("a.txt");
            box.Flush();
            var target = box.Snapshot(SnapshotKind.Auto, "作为恢复目标的自动快照");

            // 间隔要大于合并器的"原始通知去重窗口"（120 ms）：同一路径的同一动作
            // 在窗口内会被当作重复通知丢掉；两次写之间又强制 flush 过（快照前会 flush），
            // 于是第二条变化会真的丢失。真实用户不会这样操作，这里刻意留出间隔。
            Thread.Sleep(250);
            box.WriteFile("a.txt", "第三版");
            box.WaitForIndex("a.txt");
            box.Flush();
            box.Snapshot(SnapshotKind.Auto, "更新的自动快照（让上面那个不再是最新的）");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, target.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);
            Check.True(box.RestoreRepo.IsSnapshotReferenced(target.Id),
                "这一次恢复应当引用了目标快照 #" + target.Id);

            // 清理：把截止时间放到未来，让所有候选都快照都落入删除范围
            var cleanup = box.Maintenance.PlanCleanup(retentionDaysOverride: 0);
            box.Maintenance.ApplyCleanup(cleanup, alsoDeleteEvents: false,
                log: m => Console.WriteLine("      " + m));

            Check.NotNull(box.SnapshotService.Get(target.Id),
                "被恢复操作引用的快照不得被自动清理删除（BB-010：清理绕过了 CanDelete 的硬保护）");
            Check.True(box.RestoreRepo.IsSnapshotReferenced(target.Id), "引用关系必须仍然成立");
        });

        yield return new("收口·恢复记录时间", "恢复记录里的目标时间必须是真实时间，不能是 0001-01-01", () =>
        {
            using var box = Sandbox.Create("rel-target-time");
            box.WriteFile("a.txt", "第一版");
            box.Protect();
            box.Flush();
            var target = box.Snapshot(SnapshotKind.Manual, "T0");

            box.WriteFile("a.txt", "第二版");
            box.WaitForIndex("a.txt");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, target.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);

            var op = box.RestoreRepo.Get(outcome.OperationId);
            Check.NotNull(op, "应当能读到恢复记录");
            Check.True(op!.TargetTimeUtc.Year > 2000,
                "恢复记录里的目标时间必须是真实时间，实际：" + op.TargetTimeUtc.ToString("o"));
            Check.Equal(target.TimestampUtc.ToString("o"), op.TargetTimeUtc.ToString("o"),
                "恢复记录的目标时间必须等于目标快照的时间");

            // WPF 协调器侧（测试工程不引用 WPF 工程，只能做源码级结构断言）：
            // 合并计划必须把目标时间/目标快照写入计划，否则记录里就是默认值。
            var path = Path.Combine(RepoRootForTests(), "src", "LastRegret.App", "RestoreCoordinator.cs");
            var src = File.ReadAllText(path);
            var start = src.IndexOf("BuildMergedPlan(", StringComparison.Ordinal);
            Check.True(start >= 0, "应能找到 BuildMergedPlan");
            var body = src.Substring(start, Math.Min(6000, src.Length - start));
            Check.Contains(body, "merged.TargetTimeUtc", "合并计划必须写入 TargetTimeUtc（否则记录里是 0001-01-01）");
            Check.Contains(body, "merged.TargetTimeLocal", "合并计划必须写入 TargetTimeLocal");
            Check.Contains(body, "merged.TargetSnapshotId", "合并计划必须写入 TargetSnapshotId");
        });

        yield return new("收口·写入原子", "并发写入时返回的自增 Id 必须对应自己那一行（INSERT 与取回 Id 原子）", () =>
        {
            using var box = Sandbox.Create("rel-rowid");
            box.Protect();

            const int threads = 4, perThread = 25;
            var written = new System.Collections.Concurrent.ConcurrentBag<(string Path, long Id)>();

            var workers = new List<Thread>();
            for (var t = 0; t < threads; t++)
            {
                var tid = t;
                var th = new Thread(() =>
                {
                    for (var i = 0; i < perThread; i++)
                    {
                        var rel = $"evt/t{tid}-{i}.txt";
                        var ev = new FileEvent
                        {
                            RootId = box.RootId,
                            RelativePath = rel,
                            Operation = OperationType.Modified,
                            Kind = EntryKind.File,
                            TimestampUtc = DateTime.UtcNow,
                            TimestampLocal = DateTime.Now,
                        };
                        var id = box.Events.Append(ev);
                        written.Add((rel, id));
                    }
                })
                { IsBackground = true, Name = $"test-insert-{t}" };
                workers.Add(th);
                th.Start();
            }
            foreach (var th in workers) th.Join(30000);

            Check.Equal(threads * perThread, written.Count, "所有线程都必须写完");
            foreach (var (rel, id) in written)
            {
                var row = box.Events.Query(new EventQuery
                {
                    RootId = box.RootId,
                    RelativePath = rel,
                    IncludeTransient = true,
                    Limit = 5,
                }).FirstOrDefault();

                Check.NotNull(row, $"应当能按路径查到自己写入的事件：{rel}");
                Check.Equal(id, row!.Id, $"{rel} 的返回 Id 必须就是这一行的 Id（BB-005：跨线程 rowid 串号）");
            }
        });

        // ═══════════════ A4 / BB-006 / BB-011：事件处理的串行纪律 ═══════════════

        yield return new("收口·事件串行", "Stop 之前刚收到的最后一批通知必须被落库（不得静默丢弃）", () =>
        {
            // 确定性构造：**不启动调度线程**（不调 Watch.Start()），
            // 于是通知只会进 _inbox、没有任何人取走 —— 这一批只能由 Stop 自己负责落库。
            // 这也是 BB-006 的真实形态：Stop 里 `DrainInbox();` 把通知取出来又丢掉。
            using var box = Sandbox.Create("rel-stop-last");
            var (ok, rootId, message) = box.Watch.RegisterRoot(box.WatchDir);
            Check.True(ok, "登记目录：" + message);
            box.AdoptRoot(rootId);
            box.Watch.RunBaseline(rootId);
            Thread.Sleep(150);

            box.WriteFile("last.txt", "最后一批内容");
            WaitWithoutFlushing(
                () => (box.Watch.InspectWatchersForTest(rootId)?.BatchCount ?? 0) > 0,
                8000, "监听器把通知放进了收件箱（此时没有任何线程会取走它）");

            box.Watch.Stop();

            var events = box.EventsOf("last.txt");
            Check.True(events.Count > 0,
                "Stop 必须把收件箱里剩下的通知交给下一步处理并落库（BB-006：取出即丢弃）");
            Check.NotNull(box.Index.Get(rootId, "last.txt"), "索引里也应当有 last.txt");
        });

        yield return new("收口·事件串行", "并发 FlushPending 与调度线程：不得抛异常、不得丢事件", () =>
        {
            using var box = Sandbox.Create("rel-concurrent-flush");
            box.Protect();

            // 一个线程持续调 FlushPending（界面/恢复流程就是这样调的），
            // 同时调度线程在跑 Tick —— 两者都会走"取收件箱 → 合并 → 落库 → 更新索引"。
            Exception? failure = null;
            var stop = 0;
            var hammer = new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref stop) == 0) box.Flush();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            })
            { IsBackground = true, Name = "test-flush-hammer" };
            hammer.Start();

            const int files = 120;
            for (var i = 0; i < files; i++) box.WriteFile($"burst/{i}.txt", "内容 " + i);

            try
            {
                box.WaitFor(() =>
                {
                    var paths = box.AllEvents().Select(e => e.RelativePath).ToHashSet(PathUtil.Comparer);
                    for (var i = 0; i < files; i++)
                    {
                        if (!paths.Contains($"burst/{i}.txt")) return false;
                    }
                    return true;
                }, 30000, $"{files} 个文件的事件必须全部落库（并发下不得丢）");
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                hammer.Join(5000);
            }

            Check.Null(failure, "并发调用 FlushPending 不得抛异常（合并器内部集合被并发改写）");
        });

        yield return new("收口·事件串行", "结构断言：处理链路必须由同一把闸门串行（而不是到处加锁）", () =>
        {
            var path = Path.Combine(RepoRootForTests(), "src", "LastRegret.Engine", "WatchService.cs");
            var src = File.ReadAllText(path);

            Check.Contains(src, "_processGate", "必须有专门的事件处理闸门");

            // 四个入口都必须落在闸门内
            foreach (var (name, marker) in new[]
            {
                ("FlushPending", "public void FlushPending()"),
                ("Stop", "public void Stop()"),
                ("ReloadSettings", "public void ReloadSettings()"),
            })
            {
                var start = src.IndexOf(marker, StringComparison.Ordinal);
                Check.True(start >= 0, $"应能找到 {name}");
                var body = src.Substring(start, Math.Min(1400, src.Length - start));
                Check.True(body.Contains("_processGate") || body.Contains("FlushPending()"),
                    $"{name} 必须走处理闸门（否则会与调度线程并发改写合并器状态）");
            }

            // Tick 的处理段也必须在闸门内
            var tick = src.IndexOf("private void Tick()", StringComparison.Ordinal);
            Check.True(tick >= 0, "应能找到 Tick");
            var tickBody = src.Substring(tick, Math.Min(2000, src.Length - tick));
            Check.True(tickBody.Contains("lock (_processGate)"), "Tick 的处理段必须在处理闸门内");

            // Stop 不得再出现"取出即丢弃"
            var stopStart = src.IndexOf("public void Stop()", StringComparison.Ordinal);
            var stopBody = src.Substring(stopStart, Math.Min(900, src.Length - stopStart));
            Check.False(stopBody.Contains("DrainInbox();\n        Log") || stopBody.Contains("DrainInbox();\r\n        Log"),
                "Stop 不得把 DrainInbox() 的返回值丢掉（BB-006）");
        });

        // ═══════════════ A3（BB-003）：文件 ↔ 目录 的类型变化 ═══════════════

        yield return new("收口·类型变化", "文件 → 目录：恢复之后该路径必须变回「文件」且内容等于目标版本", () =>
        {
            using var box = Sandbox.Create("rel-type-f2d");
            box.WriteFile("A", "目标版本的内容");
            box.WriteFile("other.txt", "无关文件");
            box.Protect();
            box.Flush();
            var target = box.Snapshot(SnapshotKind.Manual, "T0：A 是文件");

            // 变成目录：A/x = "xxx"。
            // 先让"删除 A"单独落成一条事实，再创建新形态 —— 否则合并器会把
            // 同一个路径上的"删除 + 创建"并成一条瞬时事件，索引就学不到新类型。
            box.DeleteFile("A");
            box.Flush();
            Thread.Sleep(400);
            box.Flush();

            box.WriteFile("A/x", "xxx");
            box.WaitFor(() => Directory.Exists(box.Abs("A")) &&
                               box.Index.Get(box.RootId, "A/x") is { IsDeleted: false },
                8000, "磁盘上 A 已是目录且索引看到了 A/x");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, target.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            Check.NotNull(plan, "应当生成恢复计划");
            Check.True(plan!.HasEffect, "类型变化应当是有实际动作的恢复");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);

            // ── 磁盘级：最终形态必须等于目标状态 ──
            Check.False(Directory.Exists(box.Abs("A")), "A 必须是文件，不能还是目录（恢复报告：" + outcome.Message + "）");
            Check.FileContent(box.Abs("A"), "目标版本的内容", "A 必须恢复成目标版本的内容");
            Check.FileMissing(box.Abs("A/x"), "A/x 属于目标时刻之后的新增内容，恢复后不该还在");
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);
        });

        yield return new("收口·类型变化", "目录 → 文件：恢复之后该路径必须变回「目录」且子树完整", () =>
        {
            using var box = Sandbox.Create("rel-type-d2f");
            box.WriteFile("A/x", "xxx");
            box.WriteFile("A/sub/y.txt", "yyy");
            box.WriteFile("other.txt", "无关文件");
            box.Protect();
            box.Flush();
            var target = box.Snapshot(SnapshotKind.Manual, "T0：A 是目录");

            // 变成文件（同样先让"删除目录"单独落成一条事实）
            box.DeleteFile("A");
            box.Flush();
            Thread.Sleep(400);
            box.Flush();

            box.WriteFile("A", "现在是文件了");
            box.WaitFor(() =>
                box.Index.Get(box.RootId, "A") is { IsDeleted: false, Kind: EntryKind.File },
                8000, "索引里 A 已经变成文件");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, target.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            Check.NotNull(plan, "应当生成恢复计划");
            Check.True(plan!.HasEffect, "类型变化应当是有实际动作的恢复");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);

            // ── 磁盘级：最终形态必须等于目标状态 ──
            Check.False(File.Exists(box.Abs("A")), "A 必须是目录，不能被当成普通文件留着（恢复报告：" + outcome.Message + "）");
            Check.DirectoryExists(box.Abs("A"), "A 必须恢复成目录");
            Check.FileContent(box.Abs("A/x"), "xxx", "子文件必须回来");
            Check.FileContent(box.Abs("A/sub/y.txt"), "yyy", "深层子文件必须回来");
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);
        });

        yield return new("收口·类型变化", "类型变化恢复后：新快照清单必须与磁盘一致", () =>
        {
            using var box = Sandbox.Create("rel-type-snap");
            box.WriteFile("A", "目标版本");
            box.Protect();
            box.Flush();
            var target = box.Snapshot(SnapshotKind.Manual, "T0");

            box.DeleteFile("A");
            box.Flush();
            Thread.Sleep(400);
            box.Flush();

            box.WriteFile("A/x", "xxx");
            box.WaitFor(() => Directory.Exists(box.Abs("A")) &&
                               box.Index.Get(box.RootId, "A/x") is { IsDeleted: false },
                8000, "磁盘上 A 已是目录且索引看到了 A/x");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, target.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);

            box.Flush();
            var after = box.Snapshot(SnapshotKind.Manual, "恢复之后");
            AssertManifestMatchesDisk(box, after, "类型变化恢复后的清单必须与磁盘一致");
        });

        // ═══════════════ A2（BB-002）：真实 rename 被误录成 Created ═══════════════

        yield return new("收口·rename 同批", "文件 A.txt → B.txt 必须记成「重命名」，不得记成「新增」", () =>
        {
            using var box = Sandbox.Create("rel-rename-file");
            box.WriteFile("A.txt", "重命名前的内容");
            box.WriteFile("keep.txt", "无关文件");
            box.Protect();

            box.MovePath("A.txt", "B.txt");

            var ev = WaitForRename(box, "B.txt");
            Check.Equal(OperationType.Renamed, ev.Operation, "磁盘上是改名，历史里必须是「重命名」");
            Check.Equal("A.txt", ev.OldRelativePath ?? string.Empty, "重命名事件必须带上旧路径");
            WaitIndex(box, "B.txt", live: true);
            WaitIndex(box, "A.txt", live: false);

            // 不得额外产生一条"新增 B.txt"
            var created = box.EventsOf("B.txt").Count(e => e.Operation == OperationType.Created);
            Check.Equal(0, created, "同批 rename 不得退化成 Created（这正是 BB-002）");

            // 磁盘与索引必须同时反映"旧路径没了、新路径在"
            Check.FileMissing(box.Abs("A.txt"), "磁盘上 A.txt 应当已经不存在");
            Check.FileContent(box.Abs("B.txt"), "重命名前的内容", "改名不该丢内容");
            AssertGone(box, "A.txt", "旧路径必须被标记删除");
            AssertLive(box, "B.txt", "新路径必须存在");
        });

        yield return new("收口·rename 同批", "目录 D → E（含子文件）：旧子树消失、新子树完整", () =>
        {
            using var box = Sandbox.Create("rel-rename-dir");
            box.WriteFile("D/a.txt", "A 内容");
            box.WriteFile("D/sub/c.txt", "C 内容");
            box.MkDir("D/empty");
            box.Protect();

            box.MovePath("D", "E");

            var ev = WaitForRename(box, "E");
            Check.Equal(OperationType.Renamed, ev.Operation, "目录改名必须记成「重命名」");
            Check.Equal("D", ev.OldRelativePath ?? string.Empty, "目录重命名事件必须带上旧路径");
            Check.Equal(EntryKind.Directory, ev.Kind, "该事件的类型必须是目录");

            // 索引整体搬迁（MoveSubtree 的依据就是事件里的 OldRelativePath）
            WaitIndex(box, "E/sub/c.txt", live: true);
            WaitIndex(box, "D/a.txt", live: false);

            // 磁盘：整棵子树跟着走，内容不变
            Check.DirectoryMissing(box.Abs("D"), "磁盘上旧目录应当已经不存在");
            Check.FileContent(box.Abs("E/a.txt"), "A 内容", "子文件内容必须跟着目录走");
            Check.FileContent(box.Abs("E/sub/c.txt"), "C 内容", "深层子文件内容必须跟着目录走");
            Check.DirectoryExists(box.Abs("E/empty"), "空子目录也要跟着走");

            // 索引：旧子树整体消失、新子树整体存在（MoveSubtree 的依据是 OldRelativePath）
            AssertGone(box, "D", "旧目录");
            AssertGone(box, "D/a.txt", "旧目录下的文件");
            AssertGone(box, "D/sub/c.txt", "旧目录下的深层文件");
            AssertLive(box, "E", "新目录");
            AssertLive(box, "E/a.txt", "新目录下的文件");
            AssertLive(box, "E/sub/c.txt", "新目录下的深层文件");
        });

        yield return new("收口·rename 同批", "跨目录 move（dir1/x.txt → dir2/x.txt）：旧路径消失、新路径出现且内容不丢", () =>
        {
            using var box = Sandbox.Create("rel-move-across");
            box.WriteFile("dir1/x.txt", "移动的内容");
            box.WriteFile("dir2/other.txt", "另一个");
            box.Protect();
            var hashBefore = box.Index.Get(box.RootId, "dir1/x.txt")!.Hash;
            Check.NotNull(hashBefore, "移动前索引里应当有内容哈希");

            box.MovePath("dir1/x.txt", "dir2/x.txt");

            // 跨目录移动时 Windows 报的是 REMOVED(old) + ADDED(new)，**不是** RENAMED 对，
            // 所以这里核对的是"事实对不对称"，而不是操作名：
            // 旧路径必须消失、新路径必须出现、内容必须没丢（同一内容对象）。
            WaitIndex(box, "dir2/x.txt", live: true);
            WaitIndex(box, "dir1/x.txt", live: false);

            Check.FileMissing(box.Abs("dir1/x.txt"), "磁盘上旧位置应当已经不存在");
            Check.FileContent(box.Abs("dir2/x.txt"), "移动的内容", "移动不该丢内容");

            var moved = box.Index.Get(box.RootId, "dir2/x.txt")!;
            Check.Equal(hashBefore, moved.Hash ?? string.Empty, "移动后新位置的内容哈希必须与移动前一致");
            Check.NotNull(moved.ObjectId, "新位置必须指向内容对象（可恢复）");

            AssertGone(box, "dir1/x.txt", "旧位置必须被标记删除");
            AssertLive(box, "dir2/x.txt", "新位置必须存在");
            Check.FileContent(box.Abs("dir2/other.txt"), "另一个", "无关文件不得受影响");
        });

        yield return new("收口·rename 同批", "watcher 已配好 OldAbsolutePath 的同批通知：不得退化成 Created", () =>
        {
            // 与上面三个"真实文件系统"用例互补：这里直接构造 watcher 在同一次缓冲区里
            // 配好的那条通知（带 OldAbsolutePath），钉住本次修复的那条代码路径。
            using var box = Sandbox.Create("rel-rename-paired");
            box.WriteFile("P.txt", "配对前");
            box.Protect();
            box.Watch.Pause(box.RootId);          // 避免真实通知与构造通知重复记录

            // 磁盘上真的改名（磁盘级核对仍然成立）
            box.MovePath("P.txt", "Q.txt");

            var notifications = new[]
            {
                new RawFsNotification
                {
                    RootId = box.RootId,
                    TimestampUtc = DateTime.UtcNow,
                    AbsolutePath = box.Abs("Q.txt"),
                    OldAbsolutePath = box.Abs("P.txt"),      // ← watcher 已经配好的一对
                    Kind = RawChangeKind.RenamedNew,
                    IsDirectory = false,
                    Sequence = 0,
                },
            };

            box.Watch.IngestForTest(notifications);
            box.Flush();

            var ev = box.EventsOf("Q.txt").LastOrDefault();
            Check.NotNull(ev, "应当记录了 Q.txt 的事件");
            Check.Equal(OperationType.Renamed, ev!.Operation, "带 OldAbsolutePath 的同批 rename 必须记成重命名");
            Check.Equal("P.txt", ev.OldRelativePath ?? string.Empty, "旧路径必须取自 OldAbsolutePath");
            WaitIndex(box, "Q.txt", live: true);
            WaitIndex(box, "P.txt", live: false);
            AssertGone(box, "P.txt", "旧路径必须被标记删除");
            AssertLive(box, "Q.txt", "新路径必须存在");
        });

        yield return new("收口·rename 分批", "OLD 与 NEW 分批到达：兜底配对仍然成立（行为不得退化）", () =>
        {
            using var box = Sandbox.Create("rel-rename-split");
            box.WriteFile("S.txt", "分批前");
            box.Protect();
            box.Watch.Pause(box.RootId);

            box.MovePath("S.txt", "T.txt");

            var now = DateTime.UtcNow;
            // 第一批：只有旧名（跨批到达的典型形态）
            box.Watch.IngestForTest(new[]
            {
                new RawFsNotification
                {
                    RootId = box.RootId, TimestampUtc = now,
                    AbsolutePath = box.Abs("S.txt"),
                    Kind = RawChangeKind.RenamedOld,
                    IsDirectory = false, Sequence = 0,
                },
            });
            // 第二批：新名，但没有 OldAbsolutePath（watcher 没能在同一批里配对）
            box.Watch.IngestForTest(new[]
            {
                new RawFsNotification
                {
                    RootId = box.RootId, TimestampUtc = now.AddMilliseconds(20),
                    AbsolutePath = box.Abs("T.txt"),
                    Kind = RawChangeKind.RenamedNew,
                    IsDirectory = false, Sequence = 0,
                },
            });
            box.Flush();

            var ev = box.EventsOf("T.txt").LastOrDefault();
            Check.NotNull(ev, "应当记录了 T.txt 的事件");
            Check.Equal(OperationType.Renamed, ev!.Operation, "分批 rename 也必须记成重命名（_renameOld 兜底不得失效）");
            Check.Equal("S.txt", ev.OldRelativePath ?? string.Empty, "旧路径来自兜底配对");
            WaitIndex(box, "S.txt", live: false);
            AssertGone(box, "S.txt", "旧路径必须被标记删除");
        });

        yield return new("收口·rename 后续", "rename 之后立刻修改内容：最终记录必须是新路径上的修改", () =>
        {
            using var box = Sandbox.Create("rel-rename-modify");
            box.WriteFile("M.txt", "第一版");
            box.Protect();

            box.MovePath("M.txt", "N.txt");
            WaitForRename(box, "N.txt");

            box.WriteFile("N.txt", "改名之后又改了内容");
            box.WaitForIndex("N.txt");

            Check.FileContent(box.Abs("N.txt"), "改名之后又改了内容", "磁盘内容");
            AssertGone(box, "M.txt", "旧路径必须被标记删除");
            AssertLive(box, "N.txt", "新路径必须存在");
            var live = box.Index.Get(box.RootId, "N.txt")!;

            var events = box.EventsOf("N.txt");
            Check.True(events.Any(e => e.Operation == OperationType.Modified),
                "应当有 N.txt 的「修改」事件，实际：" + string.Join(",", events.Select(e => e.Operation)));
            Check.False(events.Any(e => e.Operation == OperationType.Created),
                "rename + modify 不得退化成「新增」");
            Check.NotNull(live.Hash, "索引里必须留下当前内容的哈希");
        });

        yield return new("收口·rename 后续", "rename 之后删除新路径：两条路径都不该留下「存在」状态", () =>
        {
            using var box = Sandbox.Create("rel-rename-delete");
            box.WriteFile("X.txt", "将被删除的内容");
            box.Protect();

            box.MovePath("X.txt", "Y.txt");
            WaitForRename(box, "Y.txt");

            box.DeleteFile("Y.txt");
            box.WaitFor(() => box.Index.Get(box.RootId, "Y.txt") is { IsDeleted: true },
                6000, "Y.txt 被标记删除");

            Check.FileMissing(box.Abs("Y.txt"), "磁盘上不该还有 Y.txt");
            AssertGone(box, "X.txt", "旧路径");
            AssertGone(box, "Y.txt", "新路径");
        });

        // ═══════════════ A1（BB-001）：增量快照幽灵行 ═══════════════

        yield return new("收口·快照一致", "目录整棵删除后：新快照不得留下任何幽灵行（清单 vs 磁盘逐项一致）", () =>
        {
            using var box = Sandbox.Create("rel-ghost-del");
            box.WriteFile("D/a.txt", "A 内容");
            box.WriteFile("D/b.txt", "B 内容");
            box.WriteFile("D/sub/c.txt", "C 内容");
            box.MkDir("D/sub/empty");
            box.WriteFile("keep.txt", "无关文件");
            box.Protect();
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "删除之前");

            // 整棵目录删除，且**事件层只有父目录一条**（幽灵行最容易被掩盖的形态）
            RemoveDirectoryReportingParentOnly(box, "D");

            var after = box.Snapshot(SnapshotKind.Manual, "删除之后");
            AssertManifestMatchesDisk(box, after, "目录整棵删除后的快照清单必须与磁盘一致");

            // 直说结论：这些路径不得出现在新快照里
            var (snapFiles, snapDirs) = ManifestTree(box, after);
            foreach (var ghost in new[] { "D/a.txt", "D/b.txt", "D/sub/c.txt" })
            {
                Check.False(snapFiles.Contains(ghost), $"幽灵行：{ghost} 已经从磁盘消失，不该留在快照清单里");
            }
            foreach (var ghost in new[] { "D", "D/sub", "D/sub/empty" })
            {
                Check.False(snapDirs.Contains(ghost), $"幽灵目录：{ghost} 已经从磁盘消失，不该留在快照清单里");
            }
            Check.True(snapFiles.Contains("keep.txt"), "无关文件必须仍在清单里");
        });

        yield return new("收口·快照一致", "目录改名后：旧子树不得残留、新子树必须完整（清单 vs 磁盘逐项一致）", () =>
        {
            using var box = Sandbox.Create("rel-ghost-rename");
            box.WriteFile("D/a.txt", "A 内容");
            box.WriteFile("D/sub/c.txt", "C 内容");
            box.WriteFile("keep.txt", "无关文件");
            box.Protect();
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "改名之前");

            // 目录改名，同样只报父目录一条事件
            RenameDirectoryReportingParentOnly(box, "D", "E");
            box.Flush();

            var after = box.Snapshot(SnapshotKind.Manual, "改名之后");
            AssertManifestMatchesDisk(box, after, "目录改名后的快照清单必须与磁盘一致");

            var (snapFiles, snapDirs) = ManifestTree(box, after);
            Check.False(snapFiles.Contains("D/a.txt"), "旧路径下的文件不得残留");
            Check.False(snapDirs.Contains("D"), "旧目录不得残留");
            Check.True(snapFiles.Contains("E/a.txt"), "新路径下的文件必须在清单里");
            Check.True(snapFiles.Contains("E/sub/c.txt"), "新路径下的深层文件必须在清单里");
            Check.True(snapDirs.Contains("E/sub"), "新路径下的子目录必须在清单里");
            Check.True(snapFiles.Contains("keep.txt"), "无关文件不得被误删（前缀删除不能误伤）");
        });

        yield return new("收口·快照一致", "连续快照：被删除的子树不得沿快照链传播", () =>
        {
            using var box = Sandbox.Create("rel-ghost-chain");
            box.WriteFile("D/a.txt", "A");
            box.WriteFile("D/sub/c.txt", "C");
            box.WriteFile("other/keep.txt", "K");
            box.Protect();
            box.Flush();
            var s0 = box.Snapshot(SnapshotKind.Manual, "T0");

            box.DeleteFile("D/sub/c.txt");
            WaitIndex(box, "D/sub/c.txt", live: false);
            box.Flush();
            var s1 = box.Snapshot(SnapshotKind.Manual, "T1 删掉一个子文件");
            AssertManifestMatchesDisk(box, s1, "T1");

            box.DeleteFile("D");
            WaitIndex(box, "D", live: false);
            box.Flush();
            var s2 = box.Snapshot(SnapshotKind.Manual, "T2 删掉整个目录");
            AssertManifestMatchesDisk(box, s2, "T2");

            // 第三个快照不能把前两个快照里的旧行继续背下来
            var (files2, dirs2) = ManifestTree(box, s2);
            Check.False(files2.Contains("D/a.txt"), "T2 不得残留 D/a.txt");
            Check.False(files2.Contains("D/sub/c.txt"), "T2 不得残留 D/sub/c.txt");
            Check.False(dirs2.Contains("D"), "T2 不得残留目录 D");

            // T0 的历史仍然必须完整（不能为了修幽灵行把历史抹掉）
            var (files0, _) = ManifestTree(box, s0);
            Check.True(files0.Contains("D/a.txt") && files0.Contains("D/sub/c.txt"),
                "T0 的历史清单必须保持不变（历史不能被后续修改污染）");
        });

        yield return new("收口·快照一致", "删除目录后恢复到「当前时间点」应当判定为无需恢复（不得把幽灵文件当成待恢复内容）", () =>
        {
            using var box = Sandbox.Create("rel-ghost-noeff");
            box.WriteFile("D/a.txt", "A");
            box.WriteFile("D/sub/c.txt", "C");
            box.Protect();
            box.Flush();
            box.Snapshot(SnapshotKind.Manual, "删除之前");

            RemoveDirectoryReportingParentOnly(box, "D");
            var after = box.Snapshot(SnapshotKind.Manual, "删除之后");

            // 目标 = 删除后的时间点，当前磁盘也正好是那个状态 ⇒ 必须"无需恢复"
            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, after.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            Check.NotNull(plan, "应当生成计划");
            Check.False(plan!.HasEffect,
                "删除后的时间点与当前磁盘一致，不该有任何待恢复内容（幽灵行会让这里变成 true）");
        });

        yield return new("收口·快照一致", "恢复到「删除之前」的时间点：整棵目录树必须真的回来（含深层文件与空目录）", () =>
        {
            using var box = Sandbox.Create("rel-ghost-restore");
            box.WriteFile("D/a.txt", "A 内容");
            box.WriteFile("D/b.txt", "B 内容");
            box.WriteFile("D/sub/c.txt", "C 内容");
            box.MkDir("D/sub/empty");
            box.Protect();
            box.Flush();
            var before = box.Snapshot(SnapshotKind.Manual, "删除之前");

            box.DeleteFile("D");
            WaitIndex(box, "D", live: false);
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, before.TimestampUtc, includePaths: null);
            Check.Null(error, "预览不该报错");
            Check.NotNull(plan, "应当生成恢复计划");
            Check.True(plan!.HasEffect, "删掉的整棵目录树应当是可恢复的");
            Check.Equal(0, plan.UnavailableCount, "这些文件的内容都留存过，不该有不可恢复项");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);

            // 磁盘级核对
            Check.FileContent(box.Abs("D/a.txt"), "A 内容", "D/a.txt 必须回来");
            Check.FileContent(box.Abs("D/b.txt"), "B 内容", "D/b.txt 必须回来");
            Check.FileContent(box.Abs("D/sub/c.txt"), "C 内容", "深层文件必须回来");
            Check.DirectoryExists(box.Abs("D/sub/empty"), "空目录也必须回来");

            // 恢复之后新建的快照同样要与磁盘一致
            box.Flush();
            var restored = box.Snapshot(SnapshotKind.Manual, "恢复之后");
            AssertManifestMatchesDisk(box, restored, "恢复之后的快照清单必须与磁盘一致");
        });

    }
}
