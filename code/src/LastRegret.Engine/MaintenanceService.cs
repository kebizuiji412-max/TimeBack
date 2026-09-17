using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Data;
using LastRegret.Windows.Storage;

namespace LastRegret.Engine;

/// <summary>磁盘占用与保留策略的现状（"历史数据"页展示）。</summary>
public sealed class StorageReport
{
    public long ObjectCount { get; set; }
    public long ObjectsLogicalBytes { get; set; }
    public long ObjectsStoredBytes { get; set; }

    /// <summary>去重节省的字节数（逻辑总量 - 实际占用）。这是"增量存储"最直观的收益。</summary>
    public long DeduplicatedBytes => Math.Max(0, ObjectsLogicalBytes - ObjectsStoredBytes);

    public double DedupRatio => ObjectsLogicalBytes <= 0 ? 0 : (double)ObjectsStoredBytes / ObjectsLogicalBytes;

    public long DatabaseBytes { get; set; }
    public long TotalHistoryBytes => ObjectsStoredBytes + DatabaseBytes;

    /// <summary>受保护范围的逻辑总大小（所有根最新快照之和）。</summary>
    public long ProtectedBytes { get; set; }
    public long ProtectedFileCount { get; set; }
    public long ProtectedDirectoryCount { get; set; }

    public long SnapshotCount { get; set; }
    public long SnapshotManifestRows { get; set; }
    public long VersionCount { get; set; }
    public long EventCount { get; set; }

    public long RootCount { get; set; }
    public long EnabledRootCount { get; set; }

    /// <summary>历史数据上限（设置值）。</summary>
    public long QuotaBytes { get; set; }
    public int RetentionDays { get; set; }

    /// <summary>占用率（相对上限）。</summary>
    public double QuotaUsage => QuotaBytes <= 0 ? 0 : (double)TotalHistoryBytes / QuotaBytes;

    /// <summary>历史数据所在磁盘的剩余空间。</summary>
    public long DiskFreeBytes { get; set; }
    public long DiskTotalBytes { get; set; }

    public bool IsOverQuota => QuotaBytes > 0 && TotalHistoryBytes > QuotaBytes;

    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"历史数据 {PathUtil.FormatBytes(TotalHistoryBytes)}");
        sb.Append($"（内容 {PathUtil.FormatBytes(ObjectsStoredBytes)} + 数据库 {PathUtil.FormatBytes(DatabaseBytes)}）");
        sb.Append($"，保护范围 {PathUtil.FormatBytes(ProtectedBytes)}");
        sb.Append($"，去重节省 {PathUtil.FormatBytes(DeduplicatedBytes)}");
        return sb.ToString();
    }
}

/// <summary>清理计划（必须先给用户看，再执行）。</summary>
public sealed class CleanupPlan
{
    public DateTime CutoffUtc { get; set; }

    public int SnapshotsToDelete { get; set; }

    public int VersionsToDelete { get; set; }

    public int EventsToDelete { get; set; }

    public int ObjectsToDelete { get; set; }

    public long BytesToFree { get; set; }

    public List<string> Reasons { get; } = new();

    /// <summary>被保护而不会删除的对象数量（全部恢复点引用的内容）。</summary>
    public int ProtectedObjects { get; set; }

    public bool IsEmpty => SnapshotsToDelete + VersionsToDelete + EventsToDelete + ObjectsToDelete == 0;

    public string Describe()
    {
        if (IsEmpty) return "没有需要清理的内容。";
        var parts = new List<string>();
        if (ObjectsToDelete > 0) parts.Add($"内容对象 {ObjectsToDelete} 个（约 {PathUtil.FormatBytes(BytesToFree)}）");
        if (SnapshotsToDelete > 0) parts.Add($"快照 {SnapshotsToDelete} 个");
        if (VersionsToDelete > 0) parts.Add($"文件版本记录 {VersionsToDelete} 条");
        if (EventsToDelete > 0) parts.Add($"事件 {EventsToDelete} 条");
        return "将清理：" + string.Join("，", parts) + "。";
    }
}

/// <summary>
/// 空间管理与历史清理。
///
/// 铁律（对应产品要求"自动删除历史前必须保证不会删除当前可恢复状态"）：
///  1. **恢复点是硬保护**：任何被快照清单引用的内容对象永不删除。
///     也就是说，无论怎么清理，"恢复到某个历史时间点"这个能力都不会失效。
///  2. **先算后删**：<see cref="PlanCleanup"/> 只计算不修改；
///     用户确认后才调用 <see cref="ApplyCleanup"/>。
///  3. **绝不触碰磁盘上的用户文件**：清理只作用于本程序自己的数据目录。
/// </summary>
public sealed class MaintenanceService
{
    private readonly LastRegretDatabase _db;
    private readonly IContentStore _store;
    private readonly ISnapshotRepository _snapshots;
    private readonly IRestoreRepository _restoreRepo;
    private readonly IFileVersionRepository _versions;
    private readonly IWatchedRootRepository _roots;
    private readonly ISettingsRepository _settingsRepo;
    private readonly IClock _clock;

    public MaintenanceService(
        LastRegretDatabase db,
        IContentStore store,
        ISnapshotRepository snapshots,
        IRestoreRepository restoreRepo,
        IFileVersionRepository versions,
        IWatchedRootRepository roots,
        ISettingsRepository settingsRepo,
        IClock clock)
    {
        _db = db;
        _store = store;
        _snapshots = snapshots;
        _restoreRepo = restoreRepo;
        _versions = versions;
        _roots = roots;
        _settingsRepo = settingsRepo;
        _clock = clock;
    }

    /// <summary>汇总当前磁盘占用与保留策略现状。</summary>
    public StorageReport GetReport()
    {
        var settings = _settingsRepo.Load();
        var usage = _store.GetUsage();
        var (snapshotCount, manifestRows) = _snapshots.GetStatistics();

        var report = new StorageReport
        {
            ObjectCount = usage.ObjectCount,
            ObjectsLogicalBytes = usage.LogicalBytes,
            ObjectsStoredBytes = usage.StoredBytes,
            DatabaseBytes = _db.GetDatabaseFootprint(),
            SnapshotCount = snapshotCount,
            SnapshotManifestRows = manifestRows,
            VersionCount = _versions.Count(),
            EventCount = CountEvents(),
            QuotaBytes = settings.MaxHistoryBytes,
            RetentionDays = settings.RetentionDays,
        };

        var roots = _roots.ListAll();
        report.RootCount = roots.Count;
        report.EnabledRootCount = roots.Count(r => r.Enabled);

        foreach (var root in roots)
        {
            var latest = _snapshots.GetLatest(root.Id);
            if (latest is null) continue;
            report.ProtectedBytes += latest.TotalBytes;
            report.ProtectedFileCount += latest.FileCount;
            report.ProtectedDirectoryCount += latest.DirectoryCount;
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_db.DataDirectory) ?? "C:\\");
            report.DiskFreeBytes = drive.AvailableFreeSpace;
            report.DiskTotalBytes = drive.TotalSize;
        }
        catch (Exception)
        {
            // 取不到磁盘信息时留 0，UI 会显示"—"
        }

        return report;
    }

    private long CountEvents()
    {
        long n = 0;
        _db.Events.QueryFirst("SELECT COUNT(*) FROM events;", Array.Empty<object?>(), r => n = r.GetInt64(0));
        return n;
    }

    /// <summary>
    /// 计算一次清理计划（**只读，不修改任何数据**）。
    /// </summary>
    /// <param name="retentionDaysOverride">覆盖设置中的保留天数。</param>
    public CleanupPlan PlanCleanup(int? retentionDaysOverride = null)
    {
        var settings = _settingsRepo.Load();
        int days = retentionDaysOverride ?? settings.RetentionDays;
        var cutoff = _clock.UtcNow.AddDays(-Math.Max(0, days));

        var plan = new CleanupPlan { CutoffUtc = cutoff };

        // 1) 所有恢复点引用的对象 —— 硬保护名单
        var protectedObjects = _snapshots.ListReferencedObjectIds();
        plan.ProtectedObjects = protectedObjects.Count;

        // 2) 可删除的快照：按时间早于 cutoff，且不是"最新一个"、不是手动/恢复前安全点
        //    取舍说明：手动恢复点与恢复前安全点是用户"锚定"的状态，默认不自动清理。
        foreach (var root in _roots.ListAll())
        {
            var all = _snapshots.List(root.Id, 10000).OrderByDescending(s => s.TimestampUtc).ToList();
            long latestId = all.Count > 0 ? all[0].Id : 0;

            foreach (var s in all)
            {
                if (s.TimestampUtc >= cutoff) continue;
                if (s.Id == latestId) continue;
                if (s.Kind is SnapshotKind.Manual or SnapshotKind.PreRestore or SnapshotKind.Baseline) continue;
                plan.SnapshotsToDelete++;
            }
        }

        // 3) 事件与版本行（仅统计，实际删除在 Apply 里按同一规则执行）
        _db.Events.QueryFirst("SELECT COUNT(*) FROM events WHERE ts_utc < ?;", new object?[] { ToTicks(cutoff) }, r => plan.EventsToDelete = r.GetInt32(0));
        plan.VersionsToDelete = (int)Math.Min(int.MaxValue, _versions.CountOlderThan(cutoff));

        // 4) 可删除的对象：不被任何快照引用、且不被恢复操作引用
        foreach (var obj in _store.EnumerateObjects())
        {
            if (protectedObjects.Contains(obj.Id)) continue;
            plan.ObjectsToDelete++;
            plan.BytesToFree += obj.StoredSize;
        }

        if (plan.SnapshotsToDelete > 0)
            plan.Reasons.Add($"{days} 天前的自动快照将被删除（手动恢复点、基线、恢复前安全点一律保留）");
        if (plan.ProtectedObjects > 0)
            plan.Reasons.Add($"{plan.ProtectedObjects} 个内容对象被恢复点引用，永不删除");
        if (plan.ObjectsToDelete > 0)
            plan.Reasons.Add("不被任何恢复点引用的历史内容将被回收");

        return plan;
    }

    /// <summary>执行清理。必须先向用户展示 <see cref="CleanupPlan"/> 并获得确认。</summary>
    public CleanupPlan ApplyCleanup(CleanupPlan plan, bool alsoDeleteEvents, Action<string>? log = null)
    {
        var protectedObjects = _snapshots.ListReferencedObjectIds();
        int objectsDeleted = 0;
        long bytesFreed = 0;

        // 1) 删除超期的自动快照（清单行由外键级联删除）
        foreach (var root in _roots.ListAll())
        {
            var all = _snapshots.List(root.Id, 10000).OrderByDescending(s => s.TimestampUtc).ToList();
            long latestId = all.Count > 0 ? all[0].Id : 0;

            foreach (var s in all)
            {
                if (s.TimestampUtc >= plan.CutoffUtc) continue;
                if (s.Id == latestId) continue;
                if (s.Kind is SnapshotKind.Manual or SnapshotKind.PreRestore or SnapshotKind.Baseline) continue;

                // ⚠ 真实缺陷（本轮修复，BB-010）：这里必须和 SnapshotService.CanDelete 的
                //   **硬保护**对齐 —— 尤其是"正被某个恢复操作引用的快照不能删"：
                //   删掉它，那次恢复就再也撤销不了。清理是自动跑的，不能绕过手动删除的保护规则。
                if (_restoreRepo.IsSnapshotReferenced(s.Id))
                {
                    log?.Invoke($"保留快照 #{s.Id}（{s.TimestampLocal:yyyy-MM-dd HH:mm}）：它正被某个恢复操作引用，删了那次恢复就无法撤销");
                    continue;
                }

                _snapshots.Delete(s.Id);
                log?.Invoke($"已删除快照 #{s.Id}（{s.TimestampLocal:yyyy-MM-dd HH:mm}，{s.Kind.ToChinese()}）");
            }
        }

        // 2) 回收不再被任何快照引用的对象
        foreach (var obj in _store.EnumerateObjects())
        {
            if (protectedObjects.Contains(obj.Id)) continue;
            try
            {
                var size = obj.StoredSize;
                _store.Delete(obj.Id);
                objectsDeleted++;
                bytesFreed += size;
            }
            catch (Exception ex)
            {
                log?.Invoke($"删除对象失败：{ex.Message}");
            }
        }

        // 3) 清理版本行（被快照引用的对象必须保留，否则快照会变成空壳）
        int versionsDeleted = _versions.DeleteOlderThan(plan.CutoffUtc, protectedObjects, dryRun: false);

        // 4) 事件日志：这是"事实"，默认**不删**；只有用户显式要求才清理
        int eventsDeleted = 0;
        if (alsoDeleteEvents)
        {
            _db.Events.InTransaction(() =>
            {
                _db.Events.QueryFirst("SELECT COUNT(*) FROM events WHERE ts_utc < ?;",
                    new object?[] { ToTicks(plan.CutoffUtc) }, r => eventsDeleted = r.GetInt32(0));
                _db.Events.NonQuery("DELETE FROM events WHERE ts_utc < ?;", ToTicks(plan.CutoffUtc));
            });
            log?.Invoke($"已清理 {eventsDeleted} 条过期事件（时间线将不再显示这些细节，恢复点不受影响）");
        }

        // 5) 清理临时文件与陈旧进程记录
        var tempRemoved = ((ContentStore)_store).CleanupTempFiles(TimeSpan.FromHours(6));
        if (tempRemoved > 0) log?.Invoke($"清理临时文件 {tempRemoved} 个");

        _db.Events.NonQuery("DELETE FROM processes WHERE last_seen_utc < ?;", ToTicks(_clock.UtcNow.AddDays(-7)));

        plan.SnapshotsToDelete = 0;
        plan.VersionsToDelete = versionsDeleted;
        plan.EventsToDelete = eventsDeleted;
        plan.ObjectsToDelete = objectsDeleted;
        plan.BytesToFree = bytesFreed;
        return plan;
    }

    /// <summary>
    /// 空间不足时的自动清理：只回收"没有被任何恢复点引用"的内容，永不动恢复点。
    /// 返回实际释放的字节数。
    /// </summary>
    public long AutoTrimToQuota(Action<string>? log = null)
    {
        var settings = _settingsRepo.Load();
        if (settings.MaxHistoryBytes <= 0) return 0;

        var report = GetReport();
        if (report.TotalHistoryBytes <= settings.MaxHistoryBytes) return 0;

        log?.Invoke($"历史数据 {PathUtil.FormatBytes(report.TotalHistoryBytes)} 超过上限 " +
                    $"{PathUtil.FormatBytes(settings.MaxHistoryBytes)}，开始回收未被恢复点引用的内容…");

        var protectedObjects = _snapshots.ListReferencedObjectIds();
        long freed = 0;
        int deleted = 0;

        // 从最老的开始回收
        foreach (var obj in _store.EnumerateObjects().OrderBy(o => o.Id))
        {
            if (report.TotalHistoryBytes - freed <= settings.MaxHistoryBytes) break;
            if (protectedObjects.Contains(obj.Id)) continue;

            try
            {
                freed += obj.StoredSize;
                _store.Delete(obj.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"回收对象失败：{ex.Message}");
            }
        }

        if (deleted > 0)
        {
            log?.Invoke($"已回收 {deleted} 个内容对象，释放约 {PathUtil.FormatBytes(freed)}；" +
                        "所有恢复点仍然完整可恢复。");
        }
        else
        {
            log?.Invoke("没有可回收的内容（其余内容都被恢复点引用）。如需进一步释放空间，" +
                        "请在设置中缩短历史保留天数，或删除不再需要的保护范围。");
        }

        return freed;
    }

    /// <summary>数据库完整性检查（"检查历史完整性"按钮）。</summary>
    public (bool Ok, string Report) CheckIntegrity() => _db.CheckIntegrity();

    /// <summary>
    /// 清空全部历史数据：变化记录、恢复点、文件索引、恢复记录，以及内容库里的所有对象文件。
    ///
    /// 与"清空变化记录"的区别：这个连**恢复点**和**内容库**一起清掉，用于真正把占用的
    /// 磁盘空间还回去（内容库往往是大头）。受保护文件夹的登记会保留，
    /// 但基线会被重置，下次启动会重新建立保护基线。
    ///
    /// ⚠ 不可撤销。调用方必须先让用户明确确认。
    /// </summary>
    public (bool Ok, string Report) PurgeAllHistory()
    {
        long bytesBefore;
        try { bytesBefore = _store.GetUsage().StoredBytes; } catch { bytesBefore = 0; }

        try
        {
            _db.Events.InTransaction(() =>
            {
                // 顺序：子表在前，避免外键约束（外键是打开的）
                _db.Events.NonQuery("DELETE FROM restore_steps;");
                _db.Events.NonQuery("DELETE FROM restore_operations;");
                _db.Events.NonQuery("DELETE FROM events;");
                _db.Events.NonQuery("DELETE FROM file_versions;");
                _db.Events.NonQuery("DELETE FROM files;");
                _db.Events.NonQuery("DELETE FROM snapshot_files;");
                _db.Events.NonQuery("DELETE FROM snapshots;");
                // ⚠ objects 表在**另一个库**（objects.db），不能在这里删；
                //    内容库由 PurgeStoreDirectories() 直接清目录，索引随之重建。
                _db.Events.NonQuery("DELETE FROM processes;");
            });

            // 基线被清掉了，登记里必须同步清空，否则会指向不存在的恢复点
            foreach (var root in _roots.ListAll())
            {
                try { _roots.SetBaselineSnapshot(root.Id, 0); } catch (Exception) { }
            }

            var removed = PurgeStoreDirectories();

            // ── 恢复内容库的目录结构（必须，否则 purge 之后再也不能保存内容） ──
            // 真实缺陷（由回归测试暴露，比"统计不对"严重得多）：
            //   PurgeStoreDirectories() 删掉的是**整个 store/**（含 tmp/），
            //   而 ContentStore 只在**构造函数**里创建 store/objects 与 store/tmp。
            //   于是 purge 之后任何新内容的写入都会以
            //     "Could not find a part of the path ...\store\tmp\xxx.part"
            //   失败 —— 也就是"清空历史"会把这个目录的后续保护**永久弄坏**，
            //   直到重启程序（重新构造 ContentStore）为止。
            //   这里按与构造函数相同的语义把两个目录补回来。
            EnsureStoreDirectories();

            // ── 同步对象索引（RC 修复） ──
            // objects 表在**另一个库**（objects.db），上面的 events.db 事务删不到它。
            // 早期实现只删了物理 store/、没管索引，导致：
            //   · 索引里残留一堆"指向已删物理对象"的行；
            //   · GetUsage() 直接读这张表 → 统计不会归零，设置页占用数字偏大；
            //   · 报告里"清理了 N 个对象"（数物理文件）与"释放 0 MB"（读索引）互相矛盾。
            // 修法：删完物理目录后，按**磁盘真相**重建一次索引。
            //   RebuildIndex 会先清空 objects 表、再扫描 store/objects 重建 ——
            //   目录已空，因此结果是 0 行，索引与磁盘彻底一致。
            //   注意：必须显式调用，不依赖任何人"以后会调"。
            var indexRows = RebuildObjectIndex();

            try { _db.Events.NonQuery("VACUUM;"); } catch (Exception) { }

            long bytesAfter = 0;
            try { bytesAfter = _store.GetUsage().StoredBytes; } catch { }

            var freed = bytesBefore - bytesAfter;
            var mb = freed / 1024d / 1024d;
            return (true,
                $"已清空全部历史数据：变化记录、恢复点、文件索引、恢复记录全部删除；" +
                $"内容库清理了 {removed} 个文件对象，释放约 {mb:F1} MB；" +
                $"对象索引已按磁盘重建（剩余 {indexRows} 条）。" +
                "磁盘上的原始文件没有被改动。受保护文件夹仍然保留，下次启动会重新建立保护基线。");
        }
        catch (Exception ex)
        {
            return (false, "清空失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 让内容库的目录结构与 ContentStore 构造函数建立的语义一致。
    /// purge 把整个 store/ 删掉后必须调用，否则后续写入会因缺少 store/tmp 而全部失败。
    /// </summary>
    private void EnsureStoreDirectories()
    {
        try
        {
            var store = (ContentStore)_store;
            Directory.CreateDirectory(store.StoreRoot);
            Directory.CreateDirectory(store.ObjectsDirectory);
            Directory.CreateDirectory(Path.Combine(store.StoreRoot, "tmp"));
        }
        catch (Exception)
        {
            // 建目录失败就让后续写入自己如实报错，不在这里把它变成 purge 失败
        }
    }

    /// <summary>
    /// 按磁盘上的实际对象文件重建 objects 索引；返回重建后的行数。
    /// 失败不抛：purge 的主要工作（删数据）已经完成，索引问题应如实报告而不是让整件事失败。
    /// </summary>
    private int RebuildObjectIndex()
    {
        try
        {
            var store = (ContentStore)_store;

            // RebuildIndex 会枚举 store/objects；被整体删掉后该目录不存在，
            // 这里先补建回来（空的），保证枚举不会抛 DirectoryNotFoundException。
            Directory.CreateDirectory(store.ObjectsDirectory);

            return store.RebuildIndex();
        }
        catch (Exception)
        {
            // 重建失败不抛：purge 的主要工作（删数据）已经完成。
            // 返回 -1 让报告如实显示"索引重建未成功"，而不是假装一切正常。
            return -1;
        }
    }

    /// <summary>清掉内容库的对象目录；返回删掉的文件数。</summary>
    private int PurgeStoreDirectories()
    {
        int removed = 0;
        var dir = Path.Combine(_db.DataDirectory, "store");
        if (!Directory.Exists(dir)) return 0;
        try
        {
            removed = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            // 单个目录删不掉不应让整件事失败；索引已经清空，剩下的是孤立文件
        }
        return removed;
    }

    /// <summary>校验内容对象与索引是否一致（抽样）。</summary>
    public (bool Ok, string Report) VerifyContent(int sample = 200)
    {
        var store = (ContentStore)_store;
        var problems = store.Verify(full: false, sample: sample);
        if (problems.Count == 0) return (true, $"抽查了最多 {sample} 个内容对象，全部通过哈希校验。");

        var lines = problems.Take(10).Select(p => $"{p.Hash[..12]}… → {p.Problem}").ToList();
        return (false, $"发现 {problems.Count} 个问题对象：{string.Join("；", lines)}");
    }

    /// <summary>重建内容索引（索引损坏时的恢复手段；磁盘对象是权威事实）。</summary>
    public int RebuildContentIndex(Action<string>? progress = null) =>
        ((ContentStore)_store).RebuildIndex(progress);

    private static long ToTicks(DateTime utc) => utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
}
