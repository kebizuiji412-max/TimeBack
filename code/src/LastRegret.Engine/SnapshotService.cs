using LastRegret.Core.Abstractions;
using LastRegret.Core.Compare;
using LastRegret.Core.Config;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Data;

namespace LastRegret.Engine;

/// <summary>
/// 快照服务：负责"把某一时刻的完整状态固化下来"。
///
/// 增量策略（这是"不复制整个文件夹"的落点）：
///  新快照 = 父快照的清单（SQL 内部整体复制）+ 自父快照以来发生变化的那几行。
///  内容字节永不在快照里重复，只存 (path, hash, object_id) 引用；
///  真实字节由 CAS 全局去重。
///
/// 于是：
///  - 每个快照都能**独立用于恢复**（无需重放事件日志）；
///  - 磁盘开销只与"变化量"成正比，与目录总大小无关。
/// </summary>
public sealed class SnapshotService
{
    private readonly LastRegretDatabase _db;
    private readonly ISnapshotRepository _snapshots;
    private readonly ISnapshotRepository _snapshotRepo;
    private readonly IFileIndex _index;
    private readonly IWatchedRootRepository _roots;
    private readonly EventRepository _events;
    private readonly IFileVersionRepository _versions;
    private readonly IContentStore _store;
    private readonly IRestoreRepository _restoreRepo;
    private readonly IClock _clock;

    public SnapshotService(
        LastRegretDatabase db,
        ISnapshotRepository snapshots,
        IFileIndex index,
        IWatchedRootRepository roots,
        EventRepository events,
        IFileVersionRepository versions,
        IContentStore store,
        IRestoreRepository restoreRepo,
        IClock clock)
    {
        _db = db;
        _snapshots = snapshots;
        _snapshotRepo = snapshots;
        _index = index;
        _roots = roots;
        _events = events;
        _versions = versions;
        _store = store;
        _restoreRepo = restoreRepo;
        _clock = clock;
    }

    /// <summary>
    /// 建立快照前的"收尾钩子"：把还在合并器里等待确认的事件先落库。
    ///
    /// 为什么必须要有它（真实缺陷，由测试暴露）：
    ///  文件系统通知到达后，事件会在合并窗口里等待几百毫秒才落库。
    ///  如果此时就建立快照，稍后落库的事件时间戳会**早于**快照时间，
    ///  于是"某个时间点的状态"与"该时间点之前的事件"出现空档，
    ///  恢复预览会错误地报告"没有需要恢复的内容"。
    ///  先落库再建快照，二者才对齐。
    /// </summary>
    public Action? FlushPendingEvents { get; set; }

    /// <summary>
    /// 创建一个快照。
    /// </summary>
    /// <param name="rootId">受保护根。</param>
    /// <param name="kind">快照类型。</param>
    /// <param name="note">说明（UI 展示，例如"恢复到 22:04 之前自动创建"）。</param>
    /// <param name="forceFull">强制全量（基线/重新对齐时使用）。</param>
    public Snapshot Create(long rootId, SnapshotKind kind, string? note = null, bool forceFull = false)
    {
        // 先让"当前状态"稳定下来：把待确认事件落库，再取水位线
        try
        {
            FlushPendingEvents?.Invoke();
        }
        catch (Exception)
        {
            // 收尾失败不应该阻止快照（最多是少记一点"最近几毫秒"的变化）
        }
        var now = _clock.UtcNow;
        var parent = forceFull ? null : _snapshots.GetLatest(rootId);

        // ── 水位线必须与"当前状态"一致 ──
        // 用**索引**里记录的最大事件 Id（它恰好表示"当前状态已经反映了哪些事件"）。
        // 相比"此刻事件表里的最大 Id"，它不会把尚未反映到状态里的变化算进来。
        long watermark = _index.GetMaxEventId(rootId);
        if (watermark == 0) watermark = _events.GetHighWatermark(rootId, now);

        var snapshot = new Snapshot
        {
            RootId = rootId,
            TimestampUtc = now,
            // 本地时间必须是 UTC 的**同一次取值**换算而来。
            // ⚠ 踩坑记录（PIT）：早期分别调用 UtcNow 与 Now，两者相差几毫秒，
            // 于是"用本地时间反查快照"（GetAtOrBefore(atLocal.ToUniversalTime())）
            // 会落到**下一个**快照上，表现为"恢复到某个时间点却说当前已一致"。
            TimestampLocal = now.ToLocalTime(),
            Kind = kind,
            ParentId = parent?.Id,
            EventHighWatermark = watermark,
            Note = note,
            IsComplete = true,
        };

        long id;
        if (parent is null)
        {
            // 全量：把索引整体写成清单
            var files = BuildEntries(rootId, _index.ListAll(rootId));
            id = _snapshots.Insert(snapshot, files);
        }
        else
        {
            // 增量：只更新自父快照以来发生变化的路径
            var changed = _events.QueryChangedPathsSince(rootId, parent.EventHighWatermark);
            var upserts = new List<SnapshotFile>(changed.Count);
            var removed = new List<string>();

            foreach (var path in changed)
            {
                var entry = _index.Get(rootId, path);
                if (entry is null || entry.IsDeleted)
                {
                    removed.Add(path);
                    continue;
                }
                upserts.Add(ToSnapshotFile(entry));
            }

            // ── 兜底（真实缺陷，由测试暴露）──
            // 若父快照的清单是空的（典型：添加保护时索引还空着就先建了基线快照），
            // 那么"从父快照增量"等于什么都不复制，新快照会是个空壳，
            // 表现为"快照存在但文件数为 0"，恢复预览也会因此得出错误结论。
            // 这里显式退化为全量写入。早期版本把条件写成
            // `upserts.Count == 0 && removed.Count == 0`，于是只要有任何一处变化
            // 就会走增量的错误分支 —— 条件永远为假等于没有兜底。
            long parentCount = _snapshots.CountFiles(parent.Id);
            var liveCount = _index.CountAll(rootId);

            if (parentCount == 0 && liveCount > 0)
            {
                var full = BuildEntries(rootId, _index.ListAll(rootId));
                id = _snapshots.Insert(snapshot, full);
                snapshot.Note = string.IsNullOrEmpty(snapshot.Note)
                    ? "父快照清单为空，本次改为写入完整清单"
                    : snapshot.Note + "（父快照清单为空，本次写入完整清单）";
            }
            else
            {
                id = _snapshots.InsertIncremental(snapshot, upserts, removed);
            }
        }

        snapshot.Id = id;
        var loaded = _snapshots.Get(id);
        if (loaded is not null)
        {
            snapshot.FileCount = loaded.FileCount;
            snapshot.DirectoryCount = loaded.DirectoryCount;
            snapshot.TotalBytes = loaded.TotalBytes;
        }

        RecordVersions(rootId, snapshot, kind);
        return snapshot;
    }

    private static List<SnapshotFile> BuildEntries(long rootId, IReadOnlyList<IndexEntry> entries)
    {
        var list = new List<SnapshotFile>(entries.Count);
        foreach (var e in entries)
        {
            if (e.IsDeleted) continue;
            list.Add(ToSnapshotFile(e));
        }
        return list;
    }

    private static SnapshotFile ToSnapshotFile(IndexEntry e) => new()
    {
        RelativePath = e.RelativePath,
        Kind = e.Kind,
        Size = e.Size,
        Hash = e.Hash,
        ObjectId = e.ObjectId,
        MtimeUtc = e.MtimeUtc,
        FirstSeenUtc = e.FirstSeenUtc,
        IsReadOnly = e.IsReadOnly,
    };

    /// <summary>
    /// 把快照清单中的状态登记为"文件历史版本"。
    /// 这既是文件详情页的数据来源，也保证了"同一内容不会重复登记"。
    /// </summary>
    private void RecordVersions(long rootId, Snapshot snapshot, SnapshotKind kind)
    {
        // 只需要登记"这次快照里第一次出现的内容"，避免版本表爆炸
        var existingHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long lastEventId = snapshot.EventHighWatermark;

        var files = _snapshots.LoadFiles(snapshot.Id);
        var toInsert = new List<FileVersion>();

        foreach (var f in files)
        {
            if (f.Kind != EntryKind.File || f.Hash is null) continue;

            // 该路径在本次快照之前是否已有相同哈希的版本？
            var latest = _versions.GetLatestBefore(rootId, f.RelativePath, snapshot.TimestampUtc);
            if (latest is not null && string.Equals(latest.Hash, f.Hash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            toInsert.Add(new FileVersion
            {
                RootId = rootId,
                RelativePath = f.RelativePath,
                Hash = f.Hash,
                ObjectId = f.ObjectId,
                Size = f.Size,
                RecordedUtc = snapshot.TimestampUtc,
                RecordedLocal = snapshot.TimestampLocal,
                MtimeUtc = f.MtimeUtc,
                SnapshotId = snapshot.Id,
                Note = $"{kind.ToChinese()}快照登记",
            });
        }

        if (toInsert.Count > 0) _versions.InsertRange(toInsert);
        _ = existingHashes;
        _ = lastEventId;
    }

    /// <summary>取某时刻（含）之前最近的快照；没有快照时返回 null。</summary>
    public Snapshot? GetAtOrBefore(long rootId, DateTime atUtc) => _snapshots.GetLatestAtOrBefore(rootId, atUtc);

    /// <summary>取最近一个快照（可按类型过滤）。</summary>
    public Snapshot? GetLatest(long rootId, SnapshotKind? kind = null) => _snapshots.GetLatest(rootId, kind);

    /// <summary>取某个快照。</summary>
    public Snapshot? Get(long snapshotId) => _snapshots.Get(snapshotId);

    /// <summary>列出某根的恢复点（新→旧）。</summary>
    public IReadOnlyList<Snapshot> List(long rootId, int limit = 200) => _snapshots.List(rootId, limit);

    /// <summary>加载快照清单为可比较的状态对象。</summary>
    public FileTreeManifest LoadManifest(Snapshot snapshot) =>
        FileTreeManifest.FromSnapshot(snapshot, _snapshots.LoadFiles(snapshot.Id));

    /// <summary>把"当前状态"（索引）装成清单，用于与历史快照对比。</summary>
    public FileTreeManifest LoadCurrentManifest(long rootId)
    {
        var manifest = new FileTreeManifest
        {
            RootId = rootId,
            TimestampUtc = _clock.UtcNow,
            TimestampLocal = _clock.Now,
            SnapshotId = null,
        };
        foreach (var e in _index.ListAll(rootId))
        {
            if (e.IsDeleted) continue;
            manifest.Add(new ManifestEntry
            {
                RelativePath = e.RelativePath,
                Kind = e.Kind,
                Size = e.Size,
                Hash = e.Hash,
                ObjectId = e.ObjectId,
                MtimeUtc = e.MtimeUtc,
                FirstSeenUtc = e.FirstSeenUtc,
            });
        }
        return manifest;
    }

    /// <summary>
    /// 确保某个根存在基线快照。已有则直接返回。
    /// </summary>
    public Snapshot EnsureBaseline(long rootId)
    {
        var existing = _snapshots.GetLatest(rootId, SnapshotKind.Baseline);
        if (existing is not null) return existing;

        var snapshot = Create(rootId, SnapshotKind.Baseline, "添加保护时建立的初始基线", forceFull: true);
        _roots.SetBaselineSnapshot(rootId, snapshot.Id);
        return snapshot;
    }

    /// <summary>统计快照占用（设置页展示）：遍历各根的最近快照，累加清单行数与逻辑体积。</summary>
    public (long Snapshots, long ManifestRows, long ManifestLogicalBytes) GetStatistics(long? rootId = null)
    {
        var (snapshots, rows) = _snapshotRepo.GetStatistics(rootId);
        long bytes = 0;
        foreach (var root in _roots.ListAll())
        {
            if (rootId is not null && root.Id != rootId.Value) continue;
            foreach (var s in _snapshotRepo.List(root.Id, 1)) bytes += s.TotalBytes;
        }
        return (snapshots, rows, bytes);
    }

    /// <summary>恢复点健康检查结果（用于提示用户"这个恢复点其实不完整"）。</summary>
    public sealed record SnapshotHealth(bool IsSuspect, string? Reason);

    /// <summary>
    /// 判断一个恢复点是否"看起来不完整"。
    ///
    /// 真实场景（用户实际遇到）：基线扫描被中断时点了「创建恢复点」，
    /// 得到一个只含 208 个文件（而目录里有 17,584 个）的恢复点。
    /// 这种恢复点会造成严重误导——恢复到它会"删掉"当时根本没扫到的文件。
    /// 引擎不擅自删除它（那是用户的数据），但必须明确提示。
    /// </summary>
    public SnapshotHealth CheckHealth(Snapshot snapshot)
    {
        // 用清单行数实时判定，而不是读 snapshots.file_count 缓存列：
        // 缓存列可能因为外部改动（清理、手工修复）与实际清单不一致。
        long actualFiles = _snapshots.CountFiles(snapshot.Id);

        if (actualFiles == 0)
        {
            return new SnapshotHealth(true, "该恢复点没有记录到任何文件（多半是在首次扫描尚未完成时创建的）。");
        }

        long live = _index.CountAll(snapshot.RootId);
        if (live > 0 && actualFiles < live / 10)
        {
            return new SnapshotHealth(true,
                $"该恢复点只包含 {actualFiles:N0} 条记录，而当前受保护范围里有 {live:N0} 个条目——" +
                "内容明显不完整（多半是在首次扫描尚未完成时创建的）。恢复到它可能移除大量文件，建议删除后重新扫描补齐。");
        }

        return new SnapshotHealth(false, null);
    }

    /// <summary>删除某个快照（历史清理用）。清单行会被级联删除。</summary>
    public void Delete(long snapshotId) => _snapshots.Delete(snapshotId);

    /// <summary>删除恢复点时被拒绝的原因（null = 可以删除）。</summary>
    public sealed record DeleteCheck(bool Allowed, string Reason);

    /// <summary>
    /// 检查某个恢复点能否删除。
    ///
    /// 三条硬保护（宁可拒绝，也不让用户失去回退能力）：
    ///  1. 正在被恢复操作引用的快照不能删（删了那次恢复就撤销不了）；
    ///  2. 每个根**最新的**恢复点不能删（删了就没有"当前状态"的锚点）；
    ///  3. 未建立基线的状态下，唯一的基线快照不能删。
    /// </summary>
    public DeleteCheck CanDelete(Snapshot snapshot)
    {
        var opCheck = _restoreRepo.IsSnapshotReferenced(snapshot.Id);
        if (opCheck)
        {
            return new DeleteCheck(false,
                "该恢复点正被某个恢复操作引用（它是那次恢复的目标点或安全点）。删除它会让那次恢复**无法撤销**，因此已拒绝。");
        }

        var latest = _snapshots.GetLatest(snapshot.RootId);
        if (latest is not null && latest.Id == snapshot.Id)
        {
            return new DeleteCheck(false,
                "这是该保护范围**最新的**恢复点。删除它会让时间线失去当前状态的锚点，因此已拒绝。");
        }

        if (snapshot.Kind == SnapshotKind.Baseline)
        {
            var others = _snapshots.List(snapshot.RootId, 200).Count(s => s.Id != snapshot.Id);
            if (others == 0)
            {
                return new DeleteCheck(false, "这是该保护范围唯一的恢复点（基线），删除后就没有任何可恢复的时间点了。");
            }
        }

        return new DeleteCheck(true, string.Empty);
    }

    /// <summary>
    /// 安全删除：先检查，被拒绝时抛出带原因的可读异常。
    /// </summary>
    public void DeleteChecked(long snapshotId)
    {
        var snapshot = _snapshots.Get(snapshotId) ?? throw new InvalidOperationException("恢复点不存在（可能已被删除）。");
        var check = CanDelete(snapshot);
        if (!check.Allowed) throw new InvalidOperationException(check.Reason);
        _snapshots.Delete(snapshotId);
    }

    /// <summary>
    /// 批量删除旧的自动恢复点（保留策略之外的）。
    /// 返回实际删除数量；被保护的恢复点会被跳过而不是报错。
    /// </summary>
    public int DeleteAutoOlderThan(long rootId, DateTime cutoffUtc, out List<string> skipped)
    {
        skipped = new List<string>();
        int deleted = 0;

        foreach (var s in _snapshots.List(rootId, 2000))
        {
            if (s.TimestampUtc >= cutoffUtc) continue;
            if (s.Kind != SnapshotKind.Auto) continue;

            var check = CanDelete(s);
            if (!check.Allowed)
            {
                skipped.Add($"{s.TimestampLocal:MM-dd HH:mm}（{s.Kind.ToChinese()}）：{check.Reason}");
                continue;
            }

            try
            {
                _snapshots.Delete(s.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{s.TimestampLocal:MM-dd HH:mm}：{ex.Message}");
            }
        }
        return deleted;
    }

    /// <summary>给恢复点加/改标签（用户自定义备注）。</summary>
    public void Rename(long snapshotId, string? note)
    {
        var snapshot = _snapshots.Get(snapshotId) ?? throw new InvalidOperationException("恢复点不存在。");
        var cleaned = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleaned is { Length: > 200 }) cleaned = cleaned[..200];

        // 只更新备注字段
        _snapshotRepo.UpdateNote(snapshotId, cleaned);
    }

    /// <summary>列出所有仍被快照引用的对象（清理保护）。</summary>
    public HashSet<long> ReferencedObjects(long? rootId = null) => _snapshots.ListReferencedObjectIds(rootId);
}
