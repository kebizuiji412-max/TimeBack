using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Data;
using LastRegret.Windows.Io;
using LastRegret.Windows.Processes;
using LastRegret.Windows.Watching;

namespace LastRegret.Engine;

/// <summary>运行时统计（首页/设置页如实展示）。</summary>
public sealed class WatchStatistics
{
    public long RawNotifications { get; set; }
    public long RawSuppressed { get; set; }
    public long EventsPersisted { get; set; }
    public long UnsavedEvents { get; set; }
    public int ActiveWatchers { get; set; }
    public int PendingInMerger { get; set; }
    public long OverflowCount { get; set; }
    public long RescanCount { get; set; }
    public DateTime? LastEventUtc { get; set; }
    public DateTime? LastFlushUtc { get; set; }
    public string? LastError { get; set; }
    public List<string> RecentErrors { get; } = new();

    /// <summary>合并率：被"去重/合并"吃掉的原始通知占比。这是效果最直观的指标。</summary>
    public double SuppressionRatio => RawNotifications == 0 ? 0 : (double)RawSuppressed / RawNotifications;
}

/// <summary>一个根的运行状态（UI 展示）。</summary>
public sealed class RootRuntimeState
{
    public long RootId { get; init; }
    public string RootPath { get; init; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Watching { get; set; }
    public bool Scanning { get; set; }
    public bool Paused { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastEventUtc { get; set; }
    public long EventCount { get; set; }
    public bool NeedsRescan { get; set; }

    /// <summary>基线扫描已处理的条目数（扫描期间实时更新，供界面显示进度）。</summary>
    public int ScanProgress { get; set; }

    /// <summary>扫描状态说明（例如"正在扫描磁盘…已扫描 12000 项"）。</summary>
    public string? ScanNote { get; set; }

    /// <summary>是否已经建立基线快照（未建立说明首次扫描被中断）。</summary>
    public bool HasBaseline { get; set; }
}

/// <summary>日志级别的记录（与 UI 绑定，但引擎不依赖 UI）。</summary>
public sealed class LogEntry
{
    public DateTime Utc { get; init; } = DateTime.UtcNow;
    public string Level { get; init; } = "info";
    public string Message { get; init; } = string.Empty;
    public long? RootId { get; init; }
}

/// <summary>
/// 引擎总调度：把"监听 → 合并 → 落库 → 索引 → 快照"串成一条链。
///
/// 线程模型（简单且不会出现难查的竞态）：
///  - 每个受保护根拥有一个独立的监听线程（ReadDirectoryChangesW + 重叠 I/O）；
///  - 监听线程只把原始通知塞进"待处理批次"，不做任何 IO 与数据库操作；
///  - 一个后台调度线程（<see cref="Timer"/>）负责：
///      1) 把批次交给事件合并器；
///      2) 推进合并窗口，产出"已稳定"的事件；
///      3) 落库 + 更新索引 + 记录版本；
///      4) 按需创建自动快照。
///  - 所有数据库写入都发生在调度线程上，天然串行，不需要复杂锁。
/// </summary>
public sealed class WatchService : IDisposable
{
    private readonly LastRegretDatabase _db;
    private readonly IWatchedRootRepository _roots;
    private readonly FileIndexRepository _index;
    private readonly EventRepository _events;
    private readonly IFileVersionRepository _versions;
    private readonly SnapshotService _snapshots;
    private readonly ISettingsRepository _settingsRepo;
    private readonly IContentStore _contentStore;
    private readonly FileSystemReader _reader;
    private readonly IProcessProbe _processProbe;
    private readonly ProcessRepository _processRepository;
    private readonly IClock _clock;

    private readonly object _gate = new();
    private readonly Dictionary<long, DirectoryWatcher> _watchers = new();
    private readonly Queue<(long RootId, RawFsNotification Notification)> _inbox = new();
    private readonly List<(long RootId, string Reason)> _pendingRescans = new();
    private readonly Dictionary<long, RootRuntimeState> _states = new();
    private readonly Dictionary<long, ExclusionMatcher> _matchers = new();
    private readonly Dictionary<long, DateTime> _dirtySince = new();
    private readonly Dictionary<long, int> _eventsSinceSnapshot = new();
    private readonly Dictionary<long, CancellationTokenSource> _scanCancellations = new();

    private EventMerger _merger;
    private AppSettings _settings;
    private Timer? _timer;
    private volatile bool _disposed;
    private DateTime _lastAutoSnapshotCheck = DateTime.MinValue;

    public WatchService(
        LastRegretDatabase db,
        IWatchedRootRepository roots,
        FileIndexRepository index,
        EventRepository events,
        IFileVersionRepository versions,
        SnapshotService snapshots,
        ISettingsRepository settingsRepo,
        IContentStore contentStore,
        FileSystemReader reader,
        IProcessProbe processProbe,
        ProcessRepository processRepository,
        ContentWriter contentWriter,
        IClock clock)
    {
        _db = db;
        _roots = roots;
        _index = index;
        _events = events;
        _versions = versions;
        _snapshots = snapshots;
        _settingsRepo = settingsRepo;
        _contentStore = contentStore;
        _reader = reader;
        _processProbe = processProbe;
        _processRepository = processRepository;
        ContentWriter = contentWriter;
        _clock = clock;

        _settings = settingsRepo.Load();
        _merger = BuildMerger();
        Statistics = new WatchStatistics();

        // 快照建立前先把待确认事件落库，保证"状态"与"事件"对齐
        _snapshots.FlushPendingEvents = FlushPending;
    }

    public ContentWriter ContentWriter { get; }

    public WatchStatistics Statistics { get; } = new();

    public Rescanner? Rescanner { get; set; }

    public AppSettings Settings => _settings;

    public event Action? TimelineChanged;

    public event Action<LogEntry>? Logged;

    // ─────────────────────────────────────────────────────────────────────
    // 生命周期
    // ─────────────────────────────────────────────────────────────────────

    private EventMerger BuildMerger()
    {
        var exclusions = new ExclusionMatcher(_settings);
        return new EventMerger(_reader, _index, exclusions, _settings);
    }

    public void ReloadSettings()
    {
        _settings = _settingsRepo.Load();
        _matchers.Clear();
        _merger = BuildMerger();
    }

    /// <summary>启动后台调度。已经启动则无副作用。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WatchService));
            if (_timer is not null) return;
            _timer = new Timer(_ => SafeTick(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(250));
        }
        Log("info", "后台监听已启动");
    }

    /// <summary>停止后台调度（并强制把待确认事件落库，不丢事实）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        FlushMerger(force: true);
        DrainInbox();
        Log("info", "后台监听已停止（已落库待确认事件）");
    }

    /// <summary>开始监听所有启用的受保护根。</summary>
    public void StartWatchingAll()
    {
        foreach (var root in _roots.ListEnabled()) StartWatching(root.Id);
    }

    public void StartWatching(long rootId)
    {
        var root = _roots.Get(rootId);
        if (root is null || !root.Enabled)
        {
            Log("warn", "根目录不存在或已停用，无法启动监听", rootId);
            return;
        }

        DirectoryWatcher? watcher = null;
        lock (_gate)
        {
            if (_watchers.TryGetValue(rootId, out watcher) && watcher.IsRunning)
            {
                State(root).Watching = true;
                return;
            }

            // 回调必须认得出"是我这个监听器在报错"：暂停→恢复会先后存在两个监听器实例，
            // 被停掉的那个如果迟到报错，绝不能把新监听器的状态改掉（见 OnWatchError）。
            DirectoryWatcher? self = null;
            self = new DirectoryWatcher(
                rootId,
                root.Path,
                root.IncludeSubdirectories,
                batch => OnBatch(batch),
                error => OnWatchError(self!, error));
            watcher = self;
            _watchers[rootId] = self;
        }

        bool ok = watcher.Start();
        var state = State(root);
        state.Watching = ok;
        state.Enabled = true;

        if (!ok)
        {
            state.LastError = "无法开始监听（目录可能不存在或无权限）";
            Log("error", state.LastError, rootId);
            return;
        }

        // 文件系统通知缓冲区溢出后，Windows 无法补齐丢失的通知 → 主动重新对齐
        if (_merger.RescanRequested)
        {
            _merger.ClearRescanRequest();
            RequestRescan(rootId, "通知缓冲区曾溢出");
        }

        Log("info", $"开始监听 {root.Path}", rootId);
    }

    public void StopWatching(long rootId)
    {
        DirectoryWatcher? watcher = null;
        lock (_gate)
        {
            if (_watchers.Remove(rootId, out watcher)) { /* 取出后关闭 */ }
        }
        watcher?.Stop(TimeSpan.FromSeconds(3));
        watcher?.Dispose();

        var root = _roots.Get(rootId);
        if (root is not null && _states.TryGetValue(rootId, out var state)) state.Watching = false;
        Log("info", $"已停止监听 {root?.Path ?? rootId.ToString()}", rootId);
        TimelineChanged?.Invoke();
    }

    /// <summary>暂停某个根的记录（恢复操作期间使用；暂停期间的通知被丢弃）。</summary>
    public void Pause(long rootId)
    {
        lock (_gate)
        {
            if (_watchers.TryGetValue(rootId, out var watcher)) watcher.Pause();
        }
        if (_states.TryGetValue(rootId, out var state)) state.Paused = true;
    }

    /// <summary>恢复记录。恢复后建议调用 <see cref="RequestRescan"/> 对齐（暂停期间的通知已丢弃）。</summary>
    public void Resume(long rootId, bool rescan = true)
    {
        lock (_gate)
        {
            if (_watchers.TryGetValue(rootId, out var watcher)) watcher.Resume();
        }
        if (_states.TryGetValue(rootId, out var state)) state.Paused = false;
        if (rescan) RequestRescan(rootId, "恢复记录后重新对齐");
    }

    /// <summary>请求一次"磁盘 ↔ 索引"重新对齐（在调度线程上执行）。</summary>
    public void RequestRescan(long rootId, string reason)
    {
        lock (_gate) _pendingRescans.Add((rootId, reason));
    }

    /// <summary>
    /// 把一批"合成的"原始通知直接交给合并器（仅供自动化测试使用）。
    ///
    /// 为什么需要它：真实文件系统通知的到达顺序与时序无法 100% 复现
    /// （编辑器原子替换、连续保存的时序尤其如此）。
    /// 用合成通知可以**确定性地**验证合并逻辑本身是否正确。
    /// 生产代码路径不调用此方法。
    /// </summary>
    public IReadOnlyList<CoalescedEvent> IngestForTest(IReadOnlyList<RawFsNotification> notifications)
    {
        if (notifications.Count == 0) return Array.Empty<CoalescedEvent>();

        var rootPaths = new Dictionary<long, string>();
        foreach (var group in notifications.GroupBy(n => n.RootId))
        {
            var root = _roots.Get(group.Key);
            if (root is not null) rootPaths[group.Key] = root.Path;
        }

        return _merger.Ingest(rootPaths, notifications);
    }

    /// <summary>
    /// 立刻把待确认的事件落库（不等合并窗口自然到期）。
    /// 用途：程序退出前、恢复操作前后、自动化测试。
    ///
    /// ⚠ 踩坑记录（PIT）：早期实现写成 `DrainInbox(); ProcessInboxNow();`
    ///  —— 第一步把收件箱取空并**丢弃了返回值**，于是"立刻落库"这条路径
    ///  实际上会**静默丢掉所有刚收到的通知**（只在调度线程尚未跑过一轮时暴露）。
    ///  凡是"取出即处理"的队列，必须把取出的元素真正交给下一步。
    /// </summary>
    public void FlushPending()
    {
        var notifications = DrainInbox();
        ProcessNotifications(notifications);
        FlushMerger(force: true);
        SyncMergerStatistics();
    }

    /// <summary>把一批通知交给合并器并落库（按根分组，保证根路径可解析）。</summary>
    private void ProcessNotifications(List<(long RootId, RawFsNotification Notification)> notifications)
    {
        if (notifications.Count == 0) return;

        var rootPaths = new Dictionary<long, string>();
        foreach (var group in notifications.GroupBy(n => n.RootId))
        {
            var root = _roots.Get(group.Key);
            if (root is null) continue;
            rootPaths[group.Key] = root.Path;

            var batch = group.Select(g => g.Notification).ToList();
            try
            {
                PersistCoalesced(_merger.Ingest(rootPaths, batch));
            }
            catch (Exception ex)
            {
                Log("error", $"处理通知失败：{ex.Message}", group.Key);
            }
        }

        SyncMergerStatistics();
    }

    /// <summary>把合并器的累计计数同步到对外统计（"合并率"是给用户看的真实效果）。</summary>
    private void SyncMergerStatistics()
    {
        Statistics.RawNotifications = _merger.RawNotificationsSeen;
        Statistics.RawSuppressed = _merger.RawNotificationsSuppressed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 通知接收（监听线程）
    // ─────────────────────────────────────────────────────────────────────

    private void OnBatch(WatchBatch batch)
    {
        lock (_gate)
        {
            if (batch.Overflowed)
            {
                Statistics.OverflowCount++;
                _pendingRescans.Add((batch.RootId, "内核通知缓冲区溢出（期间的变化可能缺失）"));
            }
            foreach (var n in batch.Notifications) _inbox.Enqueue((batch.RootId, n));
        }
    }

    private void OnWatchError(DirectoryWatcher source, WatchError error)
    {
        // 只有"当前登记在案、并且就是它报的错"才允许改运行时状态。
        //
        // 为什么必须校验（真实缺陷，"恢复保护后顶部显示 0 个在监听"）：
        //   暂停→恢复会在极短时间内先后存在两个监听器实例。被停掉的那个线程
        //   收尾时仍可能报一次致命错误；那一枪是冲着自己报的，却会把**新**监听器
        //   刚设好的 Watching=true 打回 false。结果就是：日志里明明写着"开始监听"，
        //   顶部却长期显示"（0 个在监听）"，而且再也不会自愈。
        //   监听集合（_watchers）才是唯一事实来源，陈旧实例的迟到报告一律丢弃。
        bool stillCurrent;
        lock (_gate)
        {
            stillCurrent = _watchers.TryGetValue(error.RootId, out var current)
                           && ReferenceEquals(current, source);
        }

        if (!stillCurrent) return;

        Statistics.LastError = error.Message;
        lock (_gate)
        {
            if (Statistics.RecentErrors.Count < 50) Statistics.RecentErrors.Add(error.Message);
        }

        if (_states.TryGetValue(error.RootId, out var state))
        {
            state.LastError = error.Message;
            if (error.Fatal) state.Watching = false;
        }

        Log(error.Fatal ? "error" : "warn", error.Message, error.RootId);
        if (error.Fatal) _pendingRescans.Add((error.RootId, "监听中断，需要重新对齐"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 调度循环（数据库线程）
    // ─────────────────────────────────────────────────────────────────────

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Statistics.LastError = $"{ex.GetType().Name}: {ex.Message}";
            lock (_gate)
            {
                if (Statistics.RecentErrors.Count < 50) Statistics.RecentErrors.Add(Statistics.LastError);
            }
            Log("error", $"调度循环异常：{ex.Message}");
        }
    }

    private void Tick()
    {
        if (_disposed) return;
        var now = _clock.UtcNow;

        ProcessPendingRescans();

        var notifications = DrainInbox();
        if (notifications.Count > 0) ProcessNotifications(notifications);

        // 推进合并窗口（延迟确认）
        var settled = _merger.Advance(now);
        PersistCoalesced(settled);

        SyncMergerStatistics();

        Statistics.PendingInMerger = _merger.PendingCount;
        Statistics.ActiveWatchers = _watchers.Values.Count(w => w.IsRunning);
        Statistics.OverflowCount = Math.Max(Statistics.OverflowCount, _watchers.Values.Sum(w => w.OverflowCount));

        MaybeAutoSnapshot(now);
        MaybeSaveProcessSnapshot(now);
    }

    private void ProcessPendingRescans()
    {
        List<(long RootId, string Reason)> pending;
        lock (_gate)
        {
            if (_pendingRescans.Count == 0) return;
            pending = _pendingRescans.ToList();
            _pendingRescans.Clear();
        }

        foreach (var (rootId, reason) in pending)
        {
            if (Rescanner is null) return;
            var root = _roots.Get(rootId);
            if (root is null) continue;

            var state = State(root);
            if (state.Scanning) continue;

            state.Scanning = true;
            state.NeedsRescan = true;
            try
            {
                Log("info", $"重新对齐受保护范围：{reason}", rootId);
                var report = Rescanner.Align(root, ResyncMode.Resync);
                Statistics.RescanCount++;

                if (report.HasDrift)
                {
                    Log("info",
                        $"重新对齐完成：新增 {report.DetectedCreated}、修改 {report.DetectedModified}、消失 {report.DetectedDeleted}" +
                        (report.DeletedWithoutContent > 0 ? $"（其中 {report.DeletedWithoutContent} 个删除无可恢复内容）" : string.Empty),
                        rootId);
                }

                // 重新对齐之后必须刷新快照，否则"时间线"与"快照链"会错位
                _snapshots.Create(rootId, SnapshotKind.Resync,
                    $"重新对齐后建立的状态点（{reason}）");
                _eventsSinceSnapshot[rootId] = 0;
                state.NeedsRescan = false;
                TimelineChanged?.Invoke();
            }
            catch (Exception ex)
            {
                state.LastError = $"重新对齐失败：{ex.Message}";
                Log("error", state.LastError, rootId);
            }
            finally
            {
                state.Scanning = false;
            }
        }
    }

    private List<(long RootId, RawFsNotification Notification)> DrainInbox()
    {
        lock (_gate)
        {
            if (_inbox.Count == 0) return new List<(long, RawFsNotification)>();
            var list = new List<(long, RawFsNotification)>(_inbox.Count);
            while (_inbox.Count > 0) list.Add(_inbox.Dequeue());
            Statistics.UnsavedEvents = list.Count;
            return list;
        }
    }

    private void FlushMerger(bool force)
    {
        try
        {
            var produced = force ? _merger.Flush(_clock.UtcNow) : _merger.Advance(_clock.UtcNow);
            PersistCoalesced(produced);
        }
        catch (Exception ex)
        {
            Log("error", $"落库待确认事件失败：{ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 事件落库 + 索引维护
    // ─────────────────────────────────────────────────────────────────────

    private void PersistCoalesced(IReadOnlyList<CoalescedEvent> produced)
    {
        if (produced.Count == 0) return;

        var rootCache = new Dictionary<long, WatchedRoot?>();
        var events = new List<FileEvent>(produced.Count);
        var newVersions = new List<FileVersion>();

        foreach (var c in produced)
        {
            if (!rootCache.TryGetValue(c.RootId, out var root))
            {
                root = _roots.Get(c.RootId);
                rootCache[c.RootId] = root;
            }
            if (root is null) continue;

            var maxSize = root.MaxFileSizeBytes > 0 ? root.MaxFileSizeBytes : _settings.MaxStoreFileSizeBytes;

            var beforeResult = ContentWriter.Store(c.Before, c.RelativePath, maxSize);
            var afterResult = ContentWriter.Store(c.After, c.RelativePath, maxSize);

            var ev = new FileEvent
            {
                RootId = c.RootId,
                TimestampUtc = c.FirstUtc,
                TimestampLocal = c.FirstUtc.ToLocalTime(),
                Operation = c.Operation,
                Kind = c.Kind,
                RelativePath = c.RelativePath,
                OldRelativePath = c.OldRelativePath,
                SizeBefore = c.Before?.Size ?? beforeResult.Size,
                SizeAfter = c.After?.Size ?? afterResult.Size,
                HashBefore = beforeResult.Hash ?? c.Before?.Hash,
                HashAfter = afterResult.Hash ?? c.After?.Hash,
                ObjectIdBefore = beforeResult.ObjectId,
                ObjectIdAfter = afterResult.ObjectId,
                MtimeBeforeUtc = c.Before?.MtimeUtc,
                MtimeAfterUtc = c.After?.MtimeUtc,
                SuppressedCount = c.SuppressedCount,
                MergeCount = 1,
                IsCoalesced = c.SuppressedCount > 0,
                IsTransient = c.IsTransient,
                Source = "ReadDirectoryChangesW",
                Note = ComposeNote(c, beforeResult, afterResult),
            };

            if (c.Kind == EntryKind.Directory && ev.Operation is OperationType.Deleted or OperationType.Renamed or OperationType.Moved)
            {
                ev.AffectedDescendantCount = CountDescendants(c);
            }

            if (_settings.EnableProcessAttribution && !c.IsTransient)
            {
                ApplyAttribution(ev, root);
            }

            events.Add(ev);
        }

        if (events.Count == 0) return;

        _events.AppendRange(events);

        // 事件已入库（带 Id），此时再更新索引与版本
        foreach (var ev in events)
        {
            UpdateIndex(ev);
            AddVersionIfNeeded(ev, newVersions);
            _dirtySince[ev.RootId] = ev.TimestampUtc;
            _eventsSinceSnapshot[ev.RootId] = _eventsSinceSnapshot.TryGetValue(ev.RootId, out var n) ? n + 1 : 1;

            if (_states.TryGetValue(ev.RootId, out var state))
            {
                state.LastEventUtc = ev.TimestampUtc;
                state.EventCount++;
            }
            _roots.TouchLastEvent(ev.RootId, ev.TimestampUtc);
        }

        if (newVersions.Count > 0) _versions.InsertRange(newVersions);

        Statistics.EventsPersisted += events.Count;
        Statistics.UnsavedEvents = _merger.PendingCount;
        Statistics.LastEventUtc = events[^1].TimestampUtc;
        Statistics.LastFlushUtc = _clock.UtcNow;

        TimelineChanged?.Invoke();
    }

    private string? ComposeNote(CoalescedEvent c, ContentWriter.StoreResult before, ContentWriter.StoreResult after)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(c.Note)) parts.Add(c.Note);

        if (c.Operation == OperationType.Deleted && c.Kind == EntryKind.File && before.ObjectId is null && !c.IsTransient)
        {
            parts.Add(before.Problem ?? "变化前的内容未留存，此删除无法恢复");
        }

        if (after.Problem is not null && c.Operation != OperationType.Deleted)
        {
            parts.Add(after.Problem);
        }

        if (c.SuppressedCount > 0)
        {
            parts.Add($"已合并 {c.SuppressedCount} 次重复通知");
        }

        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private int CountDescendants(CoalescedEvent c)
    {
        // 该操作影响面：目录自身的子项数量（用于 UI 提示"影响 N 个子项"）
        var target = c.Operation == OperationType.Deleted ? c.RelativePath : (c.OldRelativePath ?? c.RelativePath);
        try
        {
            return _index.ListUnder(c.RootId, target).Count(e => !PathUtil.Comparer.Equals(e.RelativePath, target));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void ApplyAttribution(FileEvent ev, WatchedRoot root)
    {
        try
        {
            var candidates = ((ProcessProbe)_processProbe).FindNearbyCandidates(ev.TimestampUtc, _settings.AttributionWindowMs);
            if (candidates.Count == 0) return;

            // 先试内核级证据（大多数情况下拿不到，这正是要如实标注的原因）
            ProcessAttribution? best = null;
            if (ev.Kind == EntryKind.File && ev.Operation is OperationType.Modified or OperationType.Created)
            {
                var absolute = PathUtil.ToAbsolute(root.Path, ev.RelativePath);
                best = _processProbe.TryFindHandleOwner(absolute, candidates);
            }

            best ??= candidates[0];
            ev.Attribution = best;
            ev.AttributedPid = best.Pid;
            ev.AttributedProcess = best.ProcessName;
            ev.Confidence = best.Confidence;
        }
        catch (Exception)
        {
            // 归属失败不能影响事件本身的可靠性
        }
    }

    private void UpdateIndex(FileEvent ev)
    {
        var now = ev.TimestampUtc;

        switch (ev.Operation)
        {
            case OperationType.Created:
            case OperationType.Modified:
            {
                var existing = _index.Get(ev.RootId, ev.RelativePath);

                // ── 关键修正（真实缺陷，由测试暴露）──
                // 若这一次没能采到"变化后"的内容（文件刚好在此期间消失/被锁），
                // 绝不能把索引里已知的 hash/object_id 覆盖成 NULL：
                // 那会让"这个文件的当前版本"凭空消失，随后它的删除就变成"无法恢复"。
                // 规则：没有新内容时保留旧内容引用，只更新时间戳。
                bool hasNewContent = ev.HashAfter is not null || ev.SizeAfter is not null;
                bool keepExistingContent = existing is { IsDeleted: false } && !hasNewContent;

                _index.Upsert(ev.RootId, new IndexEntry
                {
                    RootId = ev.RootId,
                    RelativePath = ev.RelativePath,
                    Kind = ev.Kind,
                    Size = keepExistingContent ? existing!.Size : ev.SizeAfter ?? 0,
                    Hash = keepExistingContent ? existing!.Hash : ev.HashAfter,
                    ObjectId = keepExistingContent ? existing!.ObjectId : ev.ObjectIdAfter,
                    MtimeUtc = ev.MtimeAfterUtc ?? existing?.MtimeUtc ?? now,
                    FirstSeenUtc = existing?.FirstSeenUtc ?? now,
                    LastChangedUtc = now,
                    LastEventId = ev.Id,
                });
                break;
            }

            case OperationType.Deleted:
            {
                if (ev.Kind == EntryKind.Directory)
                {
                    _index.MarkSubtreeDeleted(ev.RootId, ev.RelativePath, now);
                }
                else
                {
                    _index.MarkDeleted(ev.RootId, ev.RelativePath, now);
                }
                break;
            }

            case OperationType.Renamed:
            case OperationType.Moved:
            {
                var oldPath = ev.OldRelativePath;
                if (string.IsNullOrEmpty(oldPath))
                {
                    // 没有旧路径的"重命名"只能按同路径更新处理
                    goto case OperationType.Modified;
                }

                if (ev.Kind == EntryKind.Directory)
                {
                    _index.MoveSubtree(ev.RootId, oldPath, ev.RelativePath, now);
                }
                else
                {
                    var before = _index.Get(ev.RootId, oldPath);
                    _index.MarkDeleted(ev.RootId, oldPath, now);
                    _index.Upsert(ev.RootId, new IndexEntry
                    {
                        RootId = ev.RootId,
                        RelativePath = ev.RelativePath,
                        Kind = ev.Kind,
                        Size = ev.SizeAfter ?? before?.Size ?? 0,
                        Hash = ev.HashAfter ?? before?.Hash,
                        ObjectId = ev.ObjectIdAfter ?? before?.ObjectId,
                        MtimeUtc = ev.MtimeAfterUtc ?? before?.MtimeUtc ?? now,
                        FirstSeenUtc = before?.FirstSeenUtc ?? now,
                        LastChangedUtc = now,
                        LastEventId = ev.Id,
                    });
                }
                break;
            }

            case OperationType.Transient:
                // 瞬时事件不改变"当前状态"（文件已经不在了），只留事实
                break;
        }
    }

    private void AddVersionIfNeeded(FileEvent ev, List<FileVersion> sink)
    {
        if (ev.Kind != EntryKind.File) return;

        // "变化后"的内容成为新版本
        if (ev.Operation is OperationType.Created or OperationType.Modified or OperationType.Renamed or OperationType.Moved)
        {
            if (ev.HashAfter is null) return;
            sink.Add(new FileVersion
            {
                RootId = ev.RootId,
                RelativePath = ev.RelativePath,
                Hash = ev.HashAfter,
                ObjectId = ev.ObjectIdAfter,
                Size = ev.SizeAfter ?? 0,
                RecordedUtc = ev.TimestampUtc,
                RecordedLocal = ev.TimestampLocal,
                MtimeUtc = ev.MtimeAfterUtc,
                EventId = ev.Id,
                Note = ev.Operation.ToChinese(),
            });
        }

        // "变化前"的内容也登记一次（这是"恢复被删除文件"的依据）
        if (ev.Operation == OperationType.Deleted && ev.HashBefore is not null)
        {
            sink.Add(new FileVersion
            {
                RootId = ev.RootId,
                RelativePath = ev.RelativePath,
                Hash = ev.HashBefore,
                ObjectId = ev.ObjectIdBefore,
                Size = ev.SizeBefore ?? 0,
                RecordedUtc = ev.TimestampUtc,
                RecordedLocal = ev.TimestampLocal,
                MtimeUtc = ev.MtimeBeforeUtc,
                EventId = ev.Id,
                Note = "删除前的内容",
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 自动快照
    // ─────────────────────────────────────────────────────────────────────

    private void MaybeAutoSnapshot(DateTime now)
    {
        if (_settings.AutoSnapshotIntervalMinutes <= 0) return;
        if ((now - _lastAutoSnapshotCheck).TotalSeconds < 20) return;
        _lastAutoSnapshotCheck = now;

        foreach (var root in _roots.ListEnabled())
        {
            int pending = _eventsSinceSnapshot.TryGetValue(root.Id, out var n) ? n : 0;
            if (pending < Math.Max(1, _settings.AutoSnapshotMinEvents)) continue;

            var latest = _snapshots.GetAtOrBefore(root.Id, now);
            if (latest is not null &&
                (now - latest.TimestampUtc).TotalMinutes < _settings.AutoSnapshotIntervalMinutes)
            {
                continue;
            }

            try
            {
                _snapshots.Create(root.Id, SnapshotKind.Auto, $"自动恢复点（{pending} 条变化）");
                _eventsSinceSnapshot[root.Id] = 0;
                TimelineChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log("error", $"创建自动快照失败：{ex.Message}", root.Id);
            }
        }
    }

    private void MaybeSaveProcessSnapshot(DateTime now)
    {
        if (!_settings.EnableProcessAttribution) return;
        if ((now - _lastProcessSnapshot).TotalSeconds < 30) return;
        _lastProcessSnapshot = now;

        try
        {
            var foreground = _processProbe.GetForegroundProcess();
            var records = _processProbe.Snapshot().Select(p => new ProcessRecord
            {
                Pid = p.Pid,
                ProcessName = p.ProcessName,
                ExecutablePath = p.ExecutablePath,
                StartTimeUtc = p.StartTimeUtc,
                LastSeenUtc = now,
                HadForegroundWindow = foreground is not null && foreground.Pid == p.Pid,
                WindowTitle = foreground is not null && foreground.Pid == p.Pid ? foreground.WindowTitle : null,
            }).ToList();

            _processRepository.UpsertRange(records);
            _processRepository.Prune(now.AddDays(-3));
        }
        catch (Exception)
        {
            // 进程快照失败不影响核心功能
        }
    }

    private DateTime _lastProcessSnapshot = DateTime.MinValue;

    // ─────────────────────────────────────────────────────────────────────
    // 受保护目录管理
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 添加一个受保护目录（**同步**完成注册 + 后台启动基线扫描 + 开始监听）。
    ///
    /// ⚠ 只适合小目录或自动化测试。生产界面必须调用 <see cref="AddRootAsync"/>：
    ///   首次基线扫描要逐个读取文件算哈希并留存内容，对大目录（实测一个有 1.7 万个文件的
    ///   目录）会持续数十秒到数分钟；若在 UI 线程上执行，界面会被彻底堵死并显示"未响应"，
    ///   用户只能强杀进程 —— 强杀后基线不完整、监听从未启动、时间线永远空白。
    ///   这是一个真实发生过的产品缺陷（见 PIT-076）。
    /// </summary>
    public (bool Ok, long RootId, string Message) AddRoot(string path, string? label = null, Action<Rescanner.ScanProgress>? progress = null)
    {
        var registered = RegisterRoot(path, label);
        if (!registered.Ok) return registered;

        var root = _roots.Get(registered.RootId);
        if (root is null) return (false, registered.RootId, "目录已加入，但读取配置失败");

        try
        {
            var report = RunBaselineCore(root, progress, CancellationToken.None);
            RefreshBaselineSnapshot(root);
            return (true, registered.RootId,
                $"已开始保护（基线快照包含 {report.Files} 个文件 / {report.Dirs} 个目录）");
        }
        catch (Exception ex)
        {
            Log("error", $"建立基线失败：{ex.Message}", root.Id);
            StartWatching(root.Id);
            return (true, registered.RootId,
                $"目录已加入保护，但建立基线时出错：{ex.Message}（可稍后点「重新扫描补齐」；实时监听已在运行）");
        }
    }

    /// <summary>
    /// 第一步：登记受保护目录并**立即开始实时监听**，不做基线扫描。
    /// 立刻返回，因此可以在 UI 线程上安全调用。
    /// </summary>
    public (bool Ok, long RootId, string Message) RegisterRoot(string path, string? label = null)
    {
        string normalized;
        try
        {
            normalized = PathUtil.NormalizeRoot(path);
        }
        catch (Exception ex)
        {
            return (false, 0, $"路径无效：{ex.Message}");
        }

        if (!Directory.Exists(FileSystemReader.Extend(normalized)))
            return (false, 0, "目录不存在或无法访问");

        var existing = _roots.FindByPath(normalized);
        if (existing is not null)
        {
            if (!existing.Enabled)
            {
                _roots.SetEnabled(existing.Id, true);
                StartWatching(existing.Id);
                return (true, existing.Id, "该目录此前被暂停保护，已重新启用");
            }
            return (false, existing.Id, "该目录已经在保护范围内");
        }

        // 不允许把程序自己的数据目录加进保护范围（否则恢复会破坏历史库本身）
        if (IsInsideStore(normalized))
            return (false, 0, "不能把本程序自己的历史数据目录加入保护范围");

        var root = new WatchedRoot
        {
            Path = normalized,
            Label = label,
            Enabled = true,
            CreatedUtc = _clock.UtcNow,
            IncludeSubdirectories = true,
            MaxFileSizeBytes = _settings.MaxStoreFileSizeBytes,
        };
        var id = _roots.Insert(root);

        State(root);
        _matchers[id] = new ExclusionMatcher(_settings);

        // 先开监听：基线扫描期间发生的变化也不会漏掉
        // （扫描是"从磁盘读现状"，与实时监听并行不冲突）
        StartWatching(id);
        return (true, id, "目录已加入保护范围，正在建立基线…");
    }

    /// <summary>
    /// 第二步：在后台建立基线（扫描 + 留存内容 + 基线快照）。
    /// 由调用方放到后台线程执行，并在完成后回到 UI 线程刷新。
    /// </summary>
    /// <param name="rootId">受保护根。</param>
    /// <param name="progress">进度回调（含真实进度与当前路径）。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="estimatedTotal">预估总条目数（用于显示百分比；0 = 未知）。</param>
    public (int Files, int Dirs) RunBaseline(
        long rootId,
        Action<Rescanner.ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int estimatedTotal = 0)
    {
        var root = _roots.Get(rootId) ?? throw new InvalidOperationException("受保护目录不存在");
        var report = RunBaselineCore(root, progress, cancellationToken, estimatedTotal);
        RefreshBaselineSnapshot(root, cancellationToken);
        return report;
    }

    /// <summary>
    /// 只做轻量预估算（不读文件内容、不写数据库），用于在真正开始前告知用户规模与代价。
    /// 估算结果按**当前保护模式**分别计算耗时与占用。
    /// </summary>
    public Rescanner.ScanEstimate EstimateScanScope(long rootId, CancellationToken cancellationToken = default)
    {
        var root = _roots.Get(rootId) ?? throw new InvalidOperationException("受保护目录不存在");
        if (Rescanner is null) return new Rescanner.ScanEstimate();
        return Rescanner.Estimate(root.Path, _settings.Protection, _settings.SmartContentMaxBytes, cancellationToken);
    }

    /// <summary>基线扫描的核心：把磁盘现状写进索引并留存内容。</summary>
    private (int Files, int Dirs) RunBaselineCore(
        WatchedRoot root,
        Action<Rescanner.ScanProgress>? progress,
        CancellationToken cancellationToken,
        int estimatedTotal = 0)
    {
        if (Rescanner is null) return (0, 0);

        var state = State(root);
        state.Scanning = true;
        state.ScanProgress = 0;
        state.ScanNote = "正在扫描磁盘…";

        try
        {
            Log("info", $"正在建立基线（首次扫描 {root.Path}）…", root.Id);
            if (estimatedTotal > 0)
            {
                Log("info", $"预计需要处理约 {estimatedTotal} 个条目（大目录的首次扫描可能要几十分钟，期间可随时取消）", root.Id);
            }

            var lastLogged = 0;
            var report = Rescanner.Align(root, ResyncMode.Baseline, p =>
            {
                // 扫描可能持续很久：把进度写进运行状态，界面据此显示真实进度条
                state.ScanProgress = p.Processed;
                state.ScanNote = $"正在扫描磁盘…{p.Describe()}";

                // 每 2000 项才写一次日志，避免日志被刷屏
                if (p.Processed - lastLogged >= 2000)
                {
                    lastLogged = p.Processed;
                    Log("info", $"基线扫描进行中：{p.Describe()}（文件 {p.Files}、目录 {p.Directories}）", root.Id);
                }

                progress?.Invoke(p);
            }, cancellationToken, estimatedTotal);

            Log("info",
                $"基线扫描完成：文件 {report.ScannedFiles} 个、目录 {report.ScannedDirectories} 个，" +
                $"跳过排除项 {report.SkippedByExclusion} 个，耗时 {report.ElapsedMs} ms" +
                (report.ScanErrors > 0 ? $"，{report.ScanErrors} 处读取失败" : string.Empty),
                root.Id);

            return (report.ScannedFiles, report.ScannedDirectories);
        }
        catch (OperationCanceledException)
        {
            Log("warn", "基线扫描被取消：已扫描的部分仍然有效，可稍后重新扫描补齐", root.Id);
            throw;
        }
        finally
        {
            state.Scanning = false;
            state.ScanNote = null;
        }
    }

    /// <summary>扫描完成后建立基线快照（必须在索引就绪之后）。</summary>
    public Snapshot RefreshBaselineSnapshot(WatchedRoot root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 顺手清理：之前被中断的扫描可能留下一个空壳基线快照（文件数为 0），
        // 它会让"恢复到某时间点"得出错误结论，所以在建立新基线前先删掉。
        foreach (var stale in _snapshots.List(root.Id, 200)
                     .Where(s => s.Kind == SnapshotKind.Baseline && s.FileCount == 0))
        {
            try
            {
                _snapshots.Delete(stale.Id);
                Log("warn", $"已清理一个不完整的基线快照（#{stale.Id}，文件数 0）", root.Id);
            }
            catch (Exception ex)
            {
                Log("warn", $"清理不完整基线快照失败：{ex.Message}", root.Id);
            }
        }
        var baseline = _snapshots.Create(root.Id, SnapshotKind.Baseline, "添加保护时建立的初始基线", forceFull: true);
        _roots.SetBaselineSnapshot(root.Id, baseline.Id);
        _eventsSinceSnapshot[root.Id] = 0;

        Log("info", $"基线已建立（快照 #{baseline.Id}：{baseline.FileCount} 个文件 / {baseline.DirectoryCount} 个目录）", root.Id);
        TimelineChanged?.Invoke();
        return baseline;
    }

    /// <summary>取消某个根正在进行的基线扫描。</summary>
    public void CancelScan(long rootId)
    {
        lock (_gate)
        {
            if (_scanCancellations.TryGetValue(rootId, out var cts) && !cts.IsCancellationRequested)
            {
                cts.Cancel();
                Log("warn", "已请求取消基线扫描", rootId);
            }
        }
    }

    /// <summary>登记一个扫描取消源（由界面在启动后台扫描前创建）。</summary>
    public CancellationTokenSource BeginScanScope(long rootId)
    {
        lock (_gate)
        {
            var cts = new CancellationTokenSource();
            _scanCancellations[rootId] = cts;
            return cts;
        }
    }

    public void EndScanScope(long rootId, CancellationTokenSource cts)
    {
        lock (_gate)
        {
            _scanCancellations.Remove(rootId);
            cts.Dispose();
        }
    }

    private bool IsInsideStore(string path)
    {
        var store = PathUtil.NormalizeRoot(_contentStore.StoreRoot);
        var data = PathUtil.NormalizeRoot(_db.DataDirectory);
        return path.StartsWith(store, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(data, StringComparison.OrdinalIgnoreCase)
            || store.StartsWith(path, StringComparison.OrdinalIgnoreCase)
            || data.StartsWith(path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>暂停保护（停止监听但保留全部历史）。</summary>
    public void DisableRoot(long rootId)
    {
        _roots.SetEnabled(rootId, false);
        StopWatching(rootId);
        if (_states.TryGetValue(rootId, out var state)) state.Enabled = false;
        Log("info", "已暂停保护（历史记录保留）", rootId);
    }

    public void EnableRoot(long rootId)
    {
        _roots.SetEnabled(rootId, true);
        var root = _roots.Get(rootId);
        if (root is not null)
        {
            _roots.Update(root);
            StartWatching(rootId);
            RequestRescan(rootId, "重新启用保护");
        }
        if (_states.TryGetValue(rootId, out var state)) state.Enabled = true;
    }

    /// <summary>
    /// 删除保护范围：必须先由调用方确认。
    /// <paramref name="deleteHistory"/> = true 时同时清除该根的事件与快照（级联）。
    /// 注意：磁盘上的文件**永远不会**被本操作触碰。
    /// </summary>
    public void RemoveRoot(long rootId, bool deleteHistory)
    {
        StopWatching(rootId);

        if (deleteHistory)
        {
            _roots.Delete(rootId);
            Log("info", "已删除保护范围及其历史记录（磁盘文件未做任何改动）", rootId);
        }
        else
        {
            _roots.SetEnabled(rootId, false);
            Log("info", "已移出保护范围，历史记录保留", rootId);
        }

        lock (_gate)
        {
            _states.Remove(rootId);
            _matchers.Remove(rootId);
            _eventsSinceSnapshot.Remove(rootId);
        }
        TimelineChanged?.Invoke();
    }

    public IReadOnlyList<RootRuntimeState> GetStates() => _states.Values.OrderBy(s => s.RootId).ToList();

    public RootRuntimeState GetState(long rootId) => State(_roots.Get(rootId) ?? new WatchedRoot { Id = rootId });

    private RootRuntimeState State(WatchedRoot root)
    {
        if (_states.TryGetValue(root.Id, out var state)) return state;

        long count = 0;
        try
        {
            var (_, _, last24) = _events.GetStatistics(root.Id);
            count = last24;
        }
        catch (Exception)
        {
            count = 0;
        }

        state = new RootRuntimeState
        {
            RootId = root.Id,
            RootPath = root.Path,
            Enabled = root.Enabled,
            EventCount = count,
            HasBaseline = root.BaselineSnapshotId is not null,
        };
        _states[root.Id] = state;
        return state;
    }

    /// <summary>
    /// 找出"启用了保护、但没有基线快照"的根。
    ///
    /// 这正是用户强杀进程留下的状态：首次扫描被中断 → 基线快照没能建立 →
    /// 时间线永远空白、也无法恢复到任何时间点。启动时应当自动补齐，而不是让用户
    /// 自己猜"为什么什么都没记录"。
    ///
    /// ⚠ 判据只能是"有没有基线快照"，**不能**加内容条件。
    /// 空文件夹的基线快照合法地是 0 文件，加 <c>FileCount == 0</c> 会让它每次都
    /// 被报成"上次准备没有做完"（真实缺陷，Release 黑盒压测发现）。
    /// </summary>
    public IReadOnlyList<WatchedRoot> FindRootsMissingBaseline()
    {
        var list = new List<WatchedRoot>();
        foreach (var root in _roots.ListAll())
        {
            if (!root.Enabled) continue;
            var baseline = _snapshots.GetLatest(root.Id, SnapshotKind.Baseline);
            if (baseline is null || root.BaselineSnapshotId is null)
            {
                list.Add(root);
            }
        }
        return list;
    }

    /// <summary>
    /// 某个根的实际情况摘要（界面用它显示"是否有基线 / 索引条目数 / 事件数"，
    /// 让用户一眼看出"到底在不在记录"）。
    ///
    /// ⚠ 判断"有没有基线"只看**登记里的 baseline_snapshot_id 与那条快照是否存在**，
    /// 绝不能用 <c>FileCount &gt; 0</c> 之类的内容条件。
    /// 真实缺陷（Release 黑盒压测发现）：空文件夹的基线快照合法地是 0 文件 / 0 目录，
    /// 加了内容条件后它永远被判成"没有基线"，于是同一个文件夹同时出现
    /// 「● 正在保护」「还没准备好」「上次准备没有做完」三种说法，
    /// 而且「现在补扫完」按钮常驻、点了也没用（补扫一百次仍然是 0 个文件）。
    /// </summary>
    public (bool HasBaseline, int BaselineFiles, long IndexEntries, long EventCount) DescribeRootHealth(long rootId)
    {
        var baseline = _snapshots.GetLatest(rootId, SnapshotKind.Baseline);
        var root = _roots.Get(rootId);
        long events = 0;
        try
        {
            var (_, _, _) = _events.GetStatistics(rootId);
            events = _events.Count(new EventQuery { RootId = rootId, IncludeTransient = true, Limit = 1 });
        }
        catch (Exception)
        {
            events = 0;
        }

        return (
            root?.BaselineSnapshotId is not null && baseline is not null,
            baseline?.FileCount ?? 0,
            _index.CountAll(rootId),
            events);
    }

    /// <summary>某个根当前累计的"未进快照"事件数。</summary>
    public int PendingSnapshotEvents(long rootId) =>
        _eventsSinceSnapshot.TryGetValue(rootId, out var n) ? n : 0;

    /// <summary>监听器内部计数快照（诊断用；生产界面在"运行日志"页展示统计）。</summary>
    public sealed record WatcherInspection(
        bool Running, bool Paused, long ReadCount, long BatchCount, long Overflow, long PausedDiscarded);

    public WatcherInspection? InspectWatchersForTest(long rootId)
    {
        lock (_gate)
        {
            if (!_watchers.TryGetValue(rootId, out var w)) return null;
            return new WatcherInspection(w.IsRunning, w.IsPaused, w.ReadCount, w.BatchCount, w.OverflowCount, w.PausedDiscardedCount);
        }
    }

    private void Log(string level, string message, long? rootId = null)
    {
        var entry = new LogEntry { Level = level, Message = message, RootId = rootId };
        lock (_gate)
        {
            if (level is "error" or "warn")
            {
                if (Statistics.RecentErrors.Count < 50) Statistics.RecentErrors.Add(message);
            }
        }
        Logged?.Invoke(entry);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Stop(); }
        catch (Exception) { /* 关闭期间异常不应掩盖其他问题 */ }

        List<DirectoryWatcher> watchers;
        lock (_gate)
        {
            watchers = _watchers.Values.ToList();
            _watchers.Clear();
        }
        foreach (var w in watchers)
        {
            try { w.Dispose(); } catch (Exception) { }
        }
    }
}
