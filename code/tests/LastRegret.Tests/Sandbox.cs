using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Data;
using LastRegret.Engine;
using LastRegret.Windows.Io;
using LastRegret.Windows.Processes;
using LastRegret.Windows.Storage;

namespace LastRegret.Tests;

/// <summary>
/// 测试沙箱：一个完全真实的运行环境（真数据库、真 CAS、真监听、真恢复），
/// 只是所有数据都在临时目录里，测试结束即清理。
///
/// 关键设计：<b>不用假对象</b>。假对象能通过的测试，在真机上照样会坏。
/// 本套件唯一"加速"的地方是把合并窗口/稳定等待调小，让事件更快稳定下来。
/// </summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public string DataDir { get; }
    public string WatchDir { get; }

    public LastRegretDatabase Db { get; }
    public AppSettings Settings { get; }
    public SettingsRepository SettingsRepo { get; }
    public WatchedRootRepository Roots { get; }
    public FileIndexRepository Index { get; }
    public EventRepository Events { get; }
    public SnapshotRepository SnapshotsRepo { get; }
    public FileVersionRepository Versions { get; }
    public RestoreRepository RestoreRepo { get; }
    public ContentStore Store { get; }
    public FileSystemReader Reader { get; }
    public ProcessProbe Probe { get; }
    public StorageDiagnostics Diagnostics { get; }
    public ContentWriter Writer { get; }
    public SnapshotService SnapshotService { get; }
    public CompareService Compare { get; }
    public WatchService Watch { get; }
    public Rescanner Rescanner { get; }
    public RestoreEngine Restore { get; }
    public MaintenanceService Maintenance { get; }

    public long RootId { get; private set; }
    public WatchedRoot WatchedRoot { get; private set; } = new();

    public static Sandbox Create(string name, bool enableProcessAttribution = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "lastregret-tests", $"{name}-{Guid.NewGuid():N}"[..(name.Length + 12)]);
        var watch = Path.Combine(root, "watched");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(watch);
        Directory.CreateDirectory(data);
        return new Sandbox(root, watch, data, enableProcessAttribution);
    }

    private Sandbox(string root, string watchDir, string dataDir, bool enableProcessAttribution = false)
    {
        Root = root;
        WatchDir = watchDir;
        DataDir = dataDir;

        Db = LastRegretDatabase.Open(dataDir);

        // 测试用加速参数：真实默认值是 1500/400/800，这里调小让事件更快稳定
        Settings = new AppSettings
        {
            MergeWindowMs = 220,
            SettleDelayMs = 120,
            RenamePairWindowMs = 400,
            MaxConfirmDelayMs = 1500,
            MaxMergeExtensions = 4,
            AutoSnapshotIntervalMinutes = 0,   // 测试里不自动快照，改为显式创建
            EnableCompression = true,
            EnableProcessAttribution = enableProcessAttribution,  // 默认关（枚举全系统句柄很慢）；F-02 用例会显式打开
            MaxStoreFileSizeBytes = 8 * 1024 * 1024,
        };

        SettingsRepo = new SettingsRepository(Db);
        SettingsRepo.Save(Settings);

        Roots = new WatchedRootRepository(Db.Events);
        Index = new FileIndexRepository(Db.Events);
        Events = new EventRepository(Db.Events);
        SnapshotsRepo = new SnapshotRepository(Db);
        Versions = new FileVersionRepository(Db.Events);
        RestoreRepo = new RestoreRepository(Db.Events);
        Store = new ContentStore(Db.Objects, Path.Combine(dataDir, "store"), Settings.EnableCompression);
        Reader = new FileSystemReader();
        Probe = new ProcessProbe();
        Diagnostics = new StorageDiagnostics();
        Writer = new ContentWriter(Store, Settings, Diagnostics);

        var clock = SystemClock.Instance;
        SnapshotService = new SnapshotService(Db, SnapshotsRepo, Index, Roots, Events, Versions, Store, RestoreRepo, clock);
        Compare = new CompareService(SnapshotService, SnapshotsRepo, Events, Roots, Store, clock);

        var exclusions = new ExclusionMatcher(Settings);
        Watch = new WatchService(Db, Roots, Index, Events, Versions, SnapshotService, SettingsRepo,
            Store, Reader, Probe, new ProcessRepository(Db.Events), Writer, clock);
        Rescanner = new Rescanner(Index, Events, Versions, Reader, Writer, exclusions, Settings, clock);
        Watch.Rescanner = Rescanner;

        Restore = new RestoreEngine(Db, Roots, Index, SnapshotsRepo, RestoreRepo, Versions,
            SnapshotService, Compare, Store, Writer, Reader, clock, Watch, Settings);

        Maintenance = new MaintenanceService(Db, Store, SnapshotsRepo, RestoreRepo, Versions, Roots, SettingsRepo, clock);
    }

    /// <summary>把已登记的根接管为"当前测试目标"（供只做第一步登记的用例使用）。</summary>
    public void AdoptRoot(long rootId)
    {
        RootId = rootId;
        WatchedRoot = Roots.Get(rootId) ?? throw new InvalidOperationException("根目录写入后读不回来");
    }

    /// <summary><see cref="AdoptRoot"/> 的便捷重载。</summary>
    public void AdoptRoot((bool Ok, long RootId, string Message) result)
    {
        if (!result.Ok) throw new InvalidOperationException("登记失败：" + result.Message);
        AdoptRoot(result.RootId);
    }

    /// <summary>
    /// 只做第一步：登记目录 + 立即开始监听（**不**建立基线）。
    /// 用于测试"立即返回、扫描放到后台"这条关键行为。
    /// </summary>
    public void RegisterOnly()
    {
        Watch.Logged += entry =>
        {
            if (entry.Level is "error" or "warn") WatchLog.Add($"[{entry.Level}] {entry.Message}");
        };

        var (ok, rootId, message) = Watch.RegisterRoot(WatchDir);
        if (!ok) throw new InvalidOperationException("无法登记测试保护范围：" + message);
        RootId = rootId;
        WatchedRoot = Roots.Get(rootId) ?? throw new InvalidOperationException("根目录写入后读不回来");
        Watch.Start();
    }

    /// <summary>第二步：同步建立基线（生产界面把这一步放到后台线程并显示进度）。</summary>
    public (int Files, int Dirs) RunBaseline() => Watch.RunBaseline(RootId);

    /// <summary>
    /// 把监听目录加入保护范围（登记 → 监听 → 建立基线），与界面走**同一条**两步流程：
    /// <see cref="WatchService.RegisterRoot"/> 立即返回，<see cref="WatchService.RunBaseline"/>
    /// 再建立基线。生产界面把第二步放到后台线程，测试同步执行以便断言。
    /// </summary>
    public void Protect()
    {
        RegisterOnly();
        RunBaseline();

        // 给监听线程一点时间真正挂起（ReadDirectoryChangesW 是异步的）
        Thread.Sleep(150);

        var state = Watch.GetState(RootId);
        if (!state.Watching)
        {
            throw new InvalidOperationException(
                $"监听未启动：{state.LastError}\n引擎日志：\n  {string.Join("\n  ", WatchLog)}");
        }
    }

    /// <summary>引擎在测试期间输出的错误/告警（失败时一并打印，便于定位）。</summary>
    public List<string> WatchLog { get; } = new();

    /// <summary>监听器的原始计数（用于排查"为什么没有事件"）。</summary>
    public string DescribeWatcher()
    {
        var i = Watch.InspectWatchersForTest(RootId);
        return i is null
            ? "  （没有监听器实例）"
            : $"  监听运行中={i.Running} 已暂停={i.Paused} 读取请求数={i.ReadCount} 已收批次={i.BatchCount} " +
              $"溢出={i.Overflow} 暂停期丢弃={i.PausedDiscarded}\n" +
              $"  原始通知={Watch.Statistics.RawNotifications} 抑制={Watch.Statistics.RawSuppressed} " +
              $"落库事件={Watch.Statistics.EventsPersisted} 待确认={Watch.Statistics.PendingInMerger}\n" +
              $"  引擎日志：{(WatchLog.Count == 0 ? "（无）" : string.Join(" | ", WatchLog))}";
    }

    public string Abs(string relative) => Path.Combine(WatchDir, relative.Replace('/', '\\'));

    public void WriteFile(string relative, string content)
    {
        var path = Abs(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void WriteBytes(string relative, byte[] content)
    {
        var path = Abs(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public void DeleteFile(string relative)
    {
        var path = Abs(relative);
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public void MkDir(string relative) => Directory.CreateDirectory(Abs(relative));

    public void MovePath(string from, string to)
    {
        var src = Abs(from);
        var dst = Abs(to);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        // overwrite: true —— 编辑器/Office 的原子保存就是"改名覆盖已有文件"，
        // 用 File.Move(src, dst)（不允许覆盖）会直接抛异常，无法复现真实场景。
        if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        else Directory.Move(src, dst);
    }

    public string ReadFile(string relative) => File.ReadAllText(Abs(relative));

    /// <summary>强制把待确认的事件落库（模拟调度线程的推进）。</summary>
    public void Flush()
    {
        Watch.FlushPending();
    }

    /// <summary>
    /// 等待某个条件成立（轮询 + 主动刷写合并器），超时抛异常。
    /// 这是测试与真实文件系统事件竞争的唯一可靠方式。
    /// </summary>
    public bool WaitFor(Func<bool> condition, int timeoutMs = 6000, string? describe = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Flush();
            if (condition()) return true;
            Thread.Sleep(40);
        }
        Flush();
        if (condition()) return true;
        throw new TimeoutException(
            $"等待超时（{timeoutMs} ms）：{describe ?? "条件未满足"}\n" +
            $"{DescribeWatcher()}\n" +
            $"已记录事件：\n{DescribeEvents()}");
    }

    /// <summary>等待指定路径出现某个操作的事件。</summary>
    public FileEvent WaitForEvent(string relativePath, OperationType? op = null, int timeoutMs = 6000)
    {
        FileEvent? found = null;
        WaitFor(() =>
        {
            found = Events.Query(new EventQuery
            {
                RootId = RootId,
                RelativePath = relativePath,
                Operations = op is null ? null : new[] { op.Value },
                IncludeTransient = true,
                Limit = 5,
                Descending = true,
            }).FirstOrDefault();
            return found is not null;
        }, timeoutMs, $"路径 {relativePath} 的 {(op?.ToChinese() ?? "任意")} 事件");

        return found!;
    }

    /// <summary>列出某路径的全部事件（旧→新）。</summary>
    public List<FileEvent> EventsOf(string relativePath) =>
        Events.Query(new EventQuery
        {
            RootId = RootId,
            RelativePath = relativePath,
            IncludeTransient = true,
            Limit = 200,
            Descending = false,
        }).ToList();

    public List<FileEvent> AllEvents(bool includeTransient = true) =>
        Events.Query(new EventQuery
        {
            RootId = RootId,
            IncludeTransient = includeTransient,
            Limit = 5000,
            Descending = false,
        }).ToList();

    public string DescribeEvents()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in AllEvents())
        {
            sb.AppendLine("    " + e.ToString() +
                          $" | 前={Short(e.HashBefore)} 后={Short(e.HashAfter)}" +
                          $" obj前={e.ObjectIdBefore} obj后={e.ObjectIdAfter}" +
                          (e.Note is null ? "" : $" | {e.Note}"));
        }
        return sb.Length == 0 ? "    （无）" : sb.ToString().TrimEnd();
    }

    private static string Short(string? hash) => hash is null ? "—" : hash[..Math.Min(8, hash.Length)];

    /// <summary>创建一个快照（模拟自动/手动恢复点）。</summary>
    public Snapshot Snapshot(SnapshotKind kind = SnapshotKind.Manual, string? note = null) =>
        SnapshotService.Create(RootId, kind, note);

    /// <summary>生成恢复预览。</summary>
    public (LastRegret.Core.Restore.RestorePlan? Plan, string? Error) Preview(DateTime atLocal) =>
        Restore.BuildPreview(RootId, atLocal);

    /// <summary>诊断用：把一次恢复预览的全部依据打印出来。</summary>
    public string DescribePreview(long rootId, DateTime atLocal, LastRegret.Core.Restore.RestorePlan? plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  请求时间点：{atLocal:yyyy-MM-dd HH:mm:ss.fff}（Kind={atLocal.Kind}）");
        sb.AppendLine(DescribeSnapshots());

        var snap = SnapshotsRepo.GetLatestAtOrBefore(rootId, atLocal.Kind == DateTimeKind.Utc ? atLocal : atLocal.ToUniversalTime());
        sb.AppendLine($"  命中的快照：{(snap is null ? "(无)" : $"#{snap.Id} {snap.Kind} utc={snap.TimestampUtc:HH:mm:ss.fff} 文件={snap.FileCount}")}");

        if (snap is not null)
        {
            sb.AppendLine("  快照清单：");
            foreach (var f in SnapshotsRepo.LoadFiles(snap.Id))
                sb.AppendLine($"    {f.RelativePath} hash={(f.Hash is null ? "—" : f.Hash[..8])} obj={f.ObjectId}");
        }

        sb.AppendLine("  当前索引：");
        foreach (var e in Index.ListAll(rootId))
            sb.AppendLine($"    {e.RelativePath} hash={(e.Hash is null ? "—" : e.Hash[..8])} obj={e.ObjectId} 删除={e.IsDeleted}");

        if (plan is not null)
        {
            sb.AppendLine($"  计划：步骤 {plan.Steps.Count}，HasEffect={plan.HasEffect}，需确认={plan.ConfirmationCount}");
            foreach (var s in plan.Steps)
                sb.AppendLine($"    {s.Action} {s.RelativePath} 目标={Short(s.TargetHash)} 期望当前={Short(s.ExpectedCurrentHash)} 可用={s.ContentAvailable} 警告={s.Warning}");
            sb.AppendLine("  警告：" + string.Join(" | ", plan.Warnings));
        }

        return sb.ToString();

        static string Short(string? h) => h is null ? "(null)" : (h.Length >= 8 ? h[..8] : h);
    }

    /// <summary>诊断用：索引、事件、快照的当前状态。</summary>
    public string DescribeState()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  索引条目 {Index.CountAll(RootId)} 个：");
        foreach (var e in Index.ListAll(RootId))
            sb.AppendLine($"    {(e.IsDeleted ? "[已删]" : "[存在]")} {e.RelativePath} size={e.Size} hash={(e.Hash is null ? "—" : e.Hash[..8])} obj={e.ObjectId} firstSeen={e.FirstSeenUtc:HH:mm:ss.fff}");
        sb.AppendLine($"  事件 {AllEvents().Count} 条（含瞬时）");
        sb.AppendLine($"  快照 {SnapshotsRepo.List(RootId, 20).Count} 个");
        sb.AppendLine($"  内容对象 {Store.GetUsage().ObjectCount} 个");
        return sb.ToString();
    }

    /// <summary>
    /// 等待"索引"真正反映出磁盘上的内容。
    ///
    /// 为什么需要它：文件系统通知由内核异步投递，写完文件后立刻查询，
    /// 索引可能还停留在上一版。产品在这种情况下是**安全**的（宁可晚一点记录，
    /// 也绝不错记内容），但测试需要确定性，所以在改变状态后显式对齐。
    /// </summary>
    public void WaitForIndex(string relativePath, int timeoutMs = 5000)
    {
        var abs = Abs(relativePath);
        if (!File.Exists(abs)) return;

        var diskHash = FileSystemReader.HashFileForTest(abs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Flush();
            var entry = Index.Get(RootId, relativePath);
            if (entry is not null && string.Equals(entry.Hash, diskHash, StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(40);
        }

        throw new TimeoutException(
            $"索引未能在 {timeoutMs} ms 内追上磁盘内容：{relativePath}\n" +
            $"  磁盘哈希={diskHash}\n" +
            DescribeState() + DescribeEvents());
    }

    /// <summary>
    /// 等待"索引"真正反映出磁盘上的**删除**。
    ///
    /// 为什么需要单独一个：<see cref="WaitForIndex"/> 在文件已不存在时会直接返回
    /// （它的语义是"等索引追上磁盘内容"），所以删完立刻建预览会看到"当前状态与目标一致"，
    /// 计划里一步都没有 —— 测试会以极具误导性的方式失败。
    /// 文件系统删除通知同样是内核异步投递的，必须显式对齐。
    /// </summary>
    public void WaitForGone(string relativePath, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Flush();
            var entry = Index.Get(RootId, relativePath);
            if (entry is null || entry.IsDeleted) return;
            Thread.Sleep(40);
        }

        throw new TimeoutException(
            $"索引未能在 {timeoutMs} ms 内反映删除：{relativePath}\n" +
            $"  该路径当前仍存在于索引中\n" +
            DescribeState() + DescribeEvents());
    }

    /// <summary>诊断用：直接读取 CAS 中某对象的内容。</summary>
    public string? ReadObject(long objectId)
    {
        if (!Store.TryReadAllBytes(objectId, 1024 * 1024, out var bytes, out _)) return null;
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>诊断用：列出全部快照及其时间戳（排查时间点解析问题）。</summary>
    public string DescribeSnapshots()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("  快照列表：");
        foreach (var s in SnapshotsRepo.List(RootId, 50))
        {
            sb.AppendLine($"    #{s.Id} {s.Kind.ToChinese()} utc={s.TimestampUtc:HH:mm:ss.fff} " +
                          $"local={s.TimestampLocal:HH:mm:ss.fff} kind={s.Kind} 文件={s.FileCount} " +
                          $"父={s.ParentId} 水位={s.EventHighWatermark}");
        }
        sb.AppendLine($"  现在：utc={DateTime.UtcNow:HH:mm:ss.fff} local={DateTime.Now:HH:mm:ss.fff}");
        return sb.ToString();
    }

    /// <summary>执行恢复（走与界面完全相同的路径，包括指纹校验）。</summary>
    public RestoreOutcome RestoreTo(DateTime atLocal, bool allowNewRemovals = true, string? fingerprintOverride = null)
    {
        var (plan, error) = Preview(atLocal);
        if (plan is null) throw new InvalidOperationException("无法生成恢复预览：" + error);
        return Restore.Execute(plan, fingerprintOverride ?? plan.Fingerprint, allowNewRemovals);
    }

    /// <summary>执行恢复并把过程日志一并返回（排查恢复细节用）。</summary>
    public (RestoreOutcome Outcome, List<string> Log) RestoreToWithLog(DateTime atLocal, bool allowNewRemovals = true)
    {
        var (plan, error) = Preview(atLocal);
        if (plan is null) throw new InvalidOperationException("无法生成恢复预览：" + error);

        var log = new List<string>();
        var outcome = Restore.Execute(plan, plan.Fingerprint, allowNewRemovals, log.Add);
        return (outcome, log);
    }

    public void Dispose()
    {
        try { Watch.Dispose(); } catch (Exception) { }
        try { Store.Dispose(); } catch (Exception) { }
        try { Db.Dispose(); } catch (Exception) { }

        // 清理临时目录（失败不影响测试结论，但会打印出来）
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // 文件可能仍被句柄占用：留给系统临时目录清理
        }
    }
}
