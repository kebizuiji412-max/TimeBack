namespace LastRegret.Core.Abstractions;

using LastRegret.Core.Config;
using LastRegret.Core.Model;

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

    /// <summary>
    /// 列出某个根的恢复点（按时间倒序，最多 limit 条）。
    ///
    /// <paramref name="fromUtc"/> / <paramref name="toUtc"/> 必须**下推到 SQL**：
    /// 先取最新 limit 条再在内存里过滤，会让"查较早的时间段"永远查不到东西
    /// （目标记录根本没进前面那 limit 条）。
    /// </summary>
    IReadOnlyList<Snapshot> List(long rootId, int limit = 200, DateTime? fromUtc = null, DateTime? toUtc = null);

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

    /// <summary>
    /// 批量找出"需要新增历史版本"的快照文件——**一次查询完成**，
    /// 用于替代"对快照里的每个文件各调用一次 <see cref="GetLatestBefore"/> 的 N+1 查询"。
    ///
    /// 判定与逐条查询 + hash 比较完全等价：该路径在 <paramref name="atUtc"/>（含该时刻）
    /// 之前**最新**的那条历史版本，若不存在、或 hash 与快照中的 hash 不同，则该文件需要登记。
    /// </summary>
    IReadOnlyList<SnapshotFile> FindFilesNeedingVersion(long rootId, long snapshotId, DateTime atUtc);

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
