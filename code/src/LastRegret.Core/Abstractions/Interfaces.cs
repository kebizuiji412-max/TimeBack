namespace LastRegret.Core.Abstractions;

using LastRegret.Core.Compare;
using LastRegret.Core.Config;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;

/// <summary>时间源抽象（测试可注入假时钟，避免依赖真实时间导致用例不稳定）。</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public DateTime Now => DateTime.Now;
}

/// <summary>
/// 文件内容读取与哈希（只读、无副作用）。
/// 与"存储"分离：这样合并器、扫描器、比较器可以共用同一套读取逻辑，
/// 也便于测试注入故障（模拟"文件正被写入"/"权限不足"）。
/// </summary>
public interface IFileContentReader
{
    ContentCapture Capture(string absolutePath, bool isDirectory);

    /// <summary>只算哈希，不读全文到内存（大文件用流式）。</summary>
    string? TryComputeHash(string absolutePath, out string? error);

    /// <summary>把文件内容读入内存（受大小上限保护）。</summary>
    bool TryReadAllBytes(string absolutePath, long maxBytes, out byte[] bytes, out string? error);
}

/// <summary>
/// 内容寻址存储（CAS）：相同内容只落一份。
/// </summary>
public interface IContentStore
{
    /// <summary>存储根目录（例如 %LOCALAPPDATA%\LastRegret\store）。</summary>
    string StoreRoot { get; }

    /// <summary>把一段内容写入 CAS（若已存在则直接复用），返回对象 Id。</summary>
    long Put(byte[] content, string? extensionHint, out StoredObject obj, out bool deduplicated);

    /// <summary>从文件流式写入 CAS（大文件不占内存）。</summary>
    long PutFile(string absolutePath, string? extensionHint, out StoredObject obj, out bool deduplicated);

    /// <summary>按哈希查对象元数据。</summary>
    StoredObject? FindByHash(string hash);

    /// <summary>按 Id 查对象元数据。</summary>
    StoredObject? FindById(long id);

    /// <summary>读取对象内容到内存。</summary>
    bool TryReadAllBytes(long objectId, long maxBytes, out byte[] bytes, out string? error);

    /// <summary>把对象内容还原到指定物理路径（原子写：临时文件 + 替换）。</summary>
    bool TryMaterialize(long objectId, string targetAbsolutePath, out string? error);

    /// <summary>对象文件是否真实存在（索引与磁盘一致性校验）。</summary>
    bool Exists(long objectId);

    /// <summary>统计磁盘占用。</summary>
    StoreUsage GetUsage();

    /// <summary>删除对象（历史清理用）。调用方必须确认没有任何快照仍然引用它。</summary>
    void Delete(long objectId);

    /// <summary>遍历所有对象（校验/清理用）。</summary>
    IEnumerable<StoredObject> EnumerateObjects();
}

/// <summary>存储占用统计。</summary>
public sealed class StoreUsage
{
    public long ObjectCount { get; set; }

    public long LogicalBytes { get; set; }

    public long StoredBytes { get; set; }

    public long DatabaseBytes { get; set; }

    /// <summary>去重节省的字节数（逻辑总量 - 实际占用）。</summary>
    public long SavedBytes => Math.Max(0, LogicalBytes - StoredBytes);

    public double DedupRatio => LogicalBytes <= 0 ? 0 : (double)StoredBytes / LogicalBytes;
}

/// <summary>
/// 文件路径索引：记录受保护范围内"当前有哪些路径、内容是什么"。
/// 事件处理器依赖它来判定 Created / Modified / Renamed，
/// 快照构建依赖它来增量更新清单。
/// </summary>
public interface IFileIndex
{
    /// <summary>查询某路径的当前状态；不存在返回 null。</summary>
    IndexEntry? Get(long rootId, string relativePath);

    /// <summary>更新（或插入）路径状态。</summary>
    void Upsert(long rootId, IndexEntry entry);

    /// <summary>批量更新（单个事务内完成；扫描建立基线时用它减少事务开销）。</summary>
    void UpsertMany(long rootId, IReadOnlyList<IndexEntry> entries);

    /// <summary>标记删除（软删除，保留历史可查）。</summary>
    void MarkDeleted(long rootId, string relativePath, DateTime atUtc);

    /// <summary>把一棵子树整体迁移到新前缀（目录重命名/移动）。返回受影响的条目数。</summary>
    int MoveSubtree(long rootId, string oldPrefix, string newPrefix, DateTime atUtc);

    /// <summary>按前缀批量标记删除（目录删除）。返回受影响的条目数。</summary>
    int MarkSubtreeDeleted(long rootId, string prefix, DateTime atUtc);

    /// <summary>列出根下的全部当前条目（用于构建清单）。</summary>
    IReadOnlyList<IndexEntry> ListAll(long rootId);

    /// <summary>当前索引中的条目总数。</summary>
    long CountAll(long rootId);

    /// <summary>
    /// 索引中已反映的最大事件 Id（即"当前状态"对应的水位线）。
    /// 快照用它作为增量锚点，避免"事件尚未落库就被快照越过"导致漏记变化。
    /// </summary>
    long GetMaxEventId(long rootId);
}

/// <summary>索引中的一条路径状态。</summary>
public sealed class IndexEntry
{
    public long RootId { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public EntryKind Kind { get; set; }

    public long Size { get; set; }

    public string? Hash { get; set; }

    public long? ObjectId { get; set; }

    public DateTime? MtimeUtc { get; set; }

    /// <summary>该路径首次被观察到的时刻。</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>最近一次发生变化的时刻。</summary>
    public DateTime LastChangedUtc { get; set; }

    /// <summary>最近一次变化对应的事件 Id。</summary>
    public long? LastEventId { get; set; }

    public bool IsDeleted { get; set; }

    public bool IsReadOnly { get; set; }
}

/// <summary>事件持久化接收端。</summary>
public interface IEventSink
{
    /// <summary>批量写入事件（必须在单个事务内完成）。返回带数据库 Id 的事件。</summary>
    void AppendRange(IReadOnlyList<FileEvent> events);

    /// <summary>查询事件（时间线）。</summary>
    IReadOnlyList<FileEvent> Query(EventQuery query);

    /// <summary>统计某时间范围内的事件数量。</summary>
    long Count(EventQuery query);

    /// <summary>取某时刻之前（含）的最后一个事件 Id（时间线定位）。</summary>
    long GetHighWatermark(long rootId, DateTime atUtc);
}

/// <summary>事件查询条件。</summary>
public sealed class EventQuery
{
    public long? RootId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    /// <summary>精确路径（可选）。</summary>
    public string? RelativePath { get; set; }

    /// <summary>路径前缀（可选，用于"某目录下的全部变化"）。</summary>
    public string? PathPrefix { get; set; }

    /// <summary>只要这些操作类型。</summary>
    public OperationType[]? Operations { get; set; }

    /// <summary>是否包含瞬时事件（默认不含）。</summary>
    public bool IncludeTransient { get; set; }

    /// <summary>是否包含由恢复操作引起的事件（默认含）。</summary>
    public bool IncludeRestoreInduced { get; set; } = true;

    public int Limit { get; set; } = 500;

    public int Offset { get; set; }

    /// <summary>倒序（默认从新到旧，符合"最近发生了什么"的首页需求）。</summary>
    public bool Descending { get; set; } = true;
}

/// <summary>受保护目录仓储。</summary>
public interface IWatchedRootRepository
{
    IReadOnlyList<WatchedRoot> ListAll();

    IReadOnlyList<WatchedRoot> ListEnabled();

    WatchedRoot? Get(long id);

    WatchedRoot? FindByPath(string absolutePath);

    long Insert(WatchedRoot root);

    void Update(WatchedRoot root);

    void Delete(long id);

    void SetEnabled(long id, bool enabled);

    void SetBaselineSnapshot(long rootId, long snapshotId);

    /// <summary>更新"最近一次事件时间"（只在更新的时间上写入，避免无意义的事务）。</summary>
    void TouchLastEvent(long rootId, DateTime atUtc);

    /// <summary>更新扫描状态（idle / scanning / error），供 UI 展示。</summary>
    void SetScanState(long rootId, string state);
}

/// <summary>快照仓储。</summary>
public interface ISnapshotRepository
{
    long Insert(Snapshot snapshot, IEnumerable<SnapshotFile> files);

    /// <summary>用"上一个快照 + 变化条目"增量写入一个新快照。</summary>
    long InsertIncremental(Snapshot snapshot, IEnumerable<SnapshotFile> upserts, IEnumerable<string> removedPaths);

    Snapshot? Get(long id);

    Snapshot? GetLatest(long rootId, SnapshotKind? kind = null);

    /// <summary>取某时刻之前（含）的最后一个快照。</summary>
    Snapshot? GetLatestAtOrBefore(long rootId, DateTime atUtc);

    IReadOnlyList<Snapshot> List(long rootId, int limit = 200);

    IReadOnlyList<SnapshotFile> LoadFiles(long snapshotId);

    /// <summary>只取某个相对路径在所有快照中的状态（文件历史页用）。</summary>
    IReadOnlyList<(Snapshot Snapshot, SnapshotFile File)> HistoryOf(long rootId, string relativePath, int limit = 100);

    long CountFiles(long snapshotId);

    /// <summary>只更新恢复点的备注（用户自定义标签，例如"升级前"）。</summary>
    void UpdateNote(long snapshotId, string? note);

    /// <summary>删除快照及其清单（历史清理用）。</summary>
    void Delete(long snapshotId);

    /// <summary>列出所有仍然被某个快照引用的对象 Id（清理保护的依据）。</summary>
    HashSet<long> ListReferencedObjectIds(long? rootId = null);

    /// <summary>统计快照数量与清单行数（设置页展示磁盘占用）。</summary>
    (long Snapshots, long ManifestRows) GetStatistics(long? rootId = null);
}

/// <summary>文件版本仓储。</summary>
public interface IFileVersionRepository
{
    void InsertRange(IEnumerable<FileVersion> versions);

    IReadOnlyList<FileVersion> ListForPath(long rootId, string relativePath, int limit = 200);

    FileVersion? GetLatestBefore(long rootId, string relativePath, DateTime atUtc);

    FileVersion? GetById(long id);

    long Count(long? rootId = null);

    /// <summary>统计早于某时刻的版本行数量（清理计划用，只读）。</summary>
    long CountOlderThan(DateTime beforeUtc);

    /// <summary>清理条件内的版本行（内容是否删除由 ContentStore 决定）。</summary>
    int DeleteOlderThan(DateTime beforeUtc, ISet<long> protectedObjectIds, bool dryRun);
}

/// <summary>恢复操作仓储。</summary>
public interface IRestoreRepository
{
    long Insert(RestoreOperation op);

    void Update(RestoreOperation op);

    RestoreOperation? Get(long id);

    IReadOnlyList<RestoreOperation> ListRecent(long? rootId = null, int limit = 50);

    /// <summary>最近一次"可以撤销"的恢复操作。</summary>
    RestoreOperation? GetLastUndoable(long? rootId);

    /// <summary>
    /// 某个快照是否被任何恢复操作引用（被引用的快照不允许删除——
    /// 安全点一旦删掉，就意味着"那次恢复撤销不了"）。
    /// </summary>
    bool IsSnapshotReferenced(long snapshotId);

    void InsertSteps(IEnumerable<RestoreStepRecord> steps);

    void UpdateStep(RestoreStepRecord step);

    IReadOnlyList<RestoreStepRecord> ListSteps(long operationId);

    /// <summary>程序启动时把"运行中"的恢复操作标记为中断（崩溃恢复）。</summary>
    IReadOnlyList<RestoreOperation> FindInterrupted();
}

/// <summary>设置仓储。</summary>
public interface ISettingsRepository
{
    AppSettings Load();

    void Save(AppSettings settings);

    string? GetRaw(string key);

    void SetRaw(string key, string value);
}

/// <summary>进程信息提供者（"关联进程"，绝不声称因果）。</summary>
public interface IProcessProbe
{
    /// <summary>取当前活跃进程快照（可能在后台线程缓存）。</summary>
    IReadOnlyList<ProcessRecord> Snapshot();

    /// <summary>取当前前台窗口所属进程（若有）。</summary>
    ProcessAttribution? GetForegroundProcess();

    /// <summary>
    /// 尝试通过内核句柄确定"哪个进程正持有该文件"。
    /// 本机实测：大多数进程无法打开 PROCESS_DUP_HANDLE，因此经常返回 null ——
    /// 这是**真实的能力限制**，调用方必须据此降级为"关联进程"，不得伪造结论。
    /// </summary>
    ProcessAttribution? TryFindHandleOwner(string absolutePath, IReadOnlyList<ProcessAttribution> candidates);

    /// <summary>本机实测得到的归属能力报告（UI 与文档如实展示）。</summary>
    AttributionCapability GetCapability();
}

/// <summary>归属能力报告。</summary>
public sealed class AttributionCapability
{
    public bool HandleEnumerationAvailable { get; init; }

    public bool CrossProcessDupAvailable { get; init; }

    public int ProcessesProbed { get; init; }

    public int ProcessesOpenable { get; init; }

    public string Summary { get; init; } = string.Empty;
}
