using LastRegret.Core.Abstractions;
using LastRegret.Core.Compare;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Data;

namespace LastRegret.Engine;

/// <summary>在某个时间点的"状态快照候选"（时间线滑块/恢复点选择用）。</summary>
public sealed class SnapshotPoint
{
    public long SnapshotId { get; init; }
    public DateTime TimestampUtc { get; init; }
    public DateTime TimestampLocal { get; init; }
    public SnapshotKind Kind { get; init; }
    public string KindText => Kind.ToChinese();
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public long TotalBytes { get; init; }
    public long EventHighWatermark { get; init; }
    public string? Note { get; init; }
    public long RootId { get; init; }

    /// <summary>该时间点之前（含）发生了什么事件，数量级提示。</summary>
    public int EventCountBefore { get; set; }
}

/// <summary>比较服务：把"某个时间点 vs 现在"翻译成用户能看懂的变化清单。</summary>
public sealed class CompareService
{
    private readonly SnapshotService _snapshots;
    private readonly ISnapshotRepository _snapshotRepo;
    private readonly EventRepository _events;
    private readonly IWatchedRootRepository _roots;
    private readonly IContentStore _store;
    private readonly IClock _clock;

    public CompareService(
        SnapshotService snapshots,
        ISnapshotRepository snapshotRepo,
        EventRepository events,
        IWatchedRootRepository roots,
        IContentStore store,
        IClock clock)
    {
        _snapshots = snapshots;
        _snapshotRepo = snapshotRepo;
        _events = events;
        _roots = roots;
        _store = store;
        _clock = clock;
    }

    /// <summary>列出可选的恢复点（时间线上的"节点"）。</summary>
    public IReadOnlyList<SnapshotPoint> ListPoints(long rootId, DateTime? fromUtc = null, DateTime? toUtc = null, int limit = 300)
    {
        var list = new List<SnapshotPoint>();
        // ⚠ BB-008：时间范围必须下推到 SQL。
        // 旧写法是"先取最新 limit 条，再在内存里按 from/to 过滤" ——
        // 于是查询比"最新 limit 条"更早的时间段时，目标记录根本没进那 limit 条，永远返回空。
        foreach (var s in _snapshotRepo.List(rootId, limit, fromUtc, toUtc))
        {
            list.Add(new SnapshotPoint
            {
                SnapshotId = s.Id,
                TimestampUtc = s.TimestampUtc,
                TimestampLocal = s.TimestampLocal,
                Kind = s.Kind,
                FileCount = s.FileCount,
                DirectoryCount = s.DirectoryCount,
                TotalBytes = s.TotalBytes,
                EventHighWatermark = s.EventHighWatermark,
                Note = s.Note,
                RootId = s.RootId,
            });
        }
        return list;
    }

    /// <summary>
    /// 找到用户指定时间点对应的状态（最近一个不晚于它的快照）。
    /// </summary>
    public SnapshotPoint? ResolvePoint(long rootId, DateTime atLocal)
    {
        var atUtc = atLocal.Kind == DateTimeKind.Utc ? atLocal : atLocal.ToUniversalTime();
        var snapshot = _snapshots.GetAtOrBefore(rootId, atUtc);
        return snapshot is null ? null : ToPoint(snapshot);
    }

    /// <summary>把快照转成界面用的时间点描述。</summary>
    public static SnapshotPoint ToPoint(Snapshot snapshot) => new()
    {
        SnapshotId = snapshot.Id,
        TimestampUtc = snapshot.TimestampUtc,
        TimestampLocal = snapshot.TimestampLocal,
        Kind = snapshot.Kind,
        FileCount = snapshot.FileCount,
        DirectoryCount = snapshot.DirectoryCount,
        TotalBytes = snapshot.TotalBytes,
        EventHighWatermark = snapshot.EventHighWatermark,
        Note = snapshot.Note,
        RootId = snapshot.RootId,
    };

    /// <summary>
    /// 计算"目标时间点"与"当前状态"的差异。
    /// <para>
    /// ⚠ 踩坑记录（PIT）：请一律使用本重载（可直接给定目标快照）。
    /// 早期版本只有"按本地时间反查快照"的入口，先把它转成本地时间、内部再转回 UTC，
    /// 这个**有损往返**在时区不一致或存在毫秒级偏差时会命中**前一个**快照，
    /// 表现为"恢复到某个时间点却说当前状态已一致，无需恢复"——
    /// 一个会导致恢复功能整体失效且极难定位的缺陷。
    /// </para>
    /// </summary>
    public TreeDiffResult? CompareWithSnapshot(long rootId, Snapshot snapshot, int maxChanges = TreeComparer.DefaultMaxChanges)
        => CompareWithSnapshot(rootId, snapshot, maxChanges, includePaths: null);

    /// <summary>
    /// 计算"目标快照"与"当前状态"的差异，可只算用户勾选的那几条路径。
    ///
    /// 为什么要支持筛选：恢复面板允许用户逐个文件勾选要恢复哪些，
    /// 只勾了 3 个文件时，"要删哪些新增文件"的判断也必须在**这 3 个文件范围内**做，
    /// 否则会出现"我只勾了 a.txt，结果它把我没勾的 b.txt 也删了"。
    /// </summary>
    public TreeDiffResult? CompareWithSnapshot(
        long rootId, Snapshot snapshot, int maxChanges, IReadOnlyCollection<string>? includePaths)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var target = _snapshots.LoadManifest(snapshot);
        var current = _snapshots.LoadCurrentManifest(rootId);

        // 重命名证据：只使用事件日志里确实存在的 rename/move 记录
        var evidence = new TreeComparer.RenameEvidence();
        foreach (var e in _events.QueryRenameEvidence(rootId, snapshot.TimestampUtc, _clock.UtcNow))
        {
            if (e.OldRelativePath is { Length: > 0 }) evidence.Add(e.OldRelativePath, e.RelativePath);
        }

        var diff = TreeComparer.Compare(target, current, evidence, maxChanges);
        if (includePaths is not null)
        {
            // ⚠ 真实缺陷（本轮修复，F-01 复现矩阵 6/6 命中）：
            //   这里原先只按**精确路径**过滤（keep.Contains(RelativePath)）。
            //   而调用方（CLI 的 preview-restore、以及任何没有预先展开目录的入口）
            //   传进来的是"用户勾选的那个目录"本身 —— 于是该目录下的所有变化
            //   （包括内部被改过的文件）会被整批剔除，计划变成空的或只剩目录条目。
            //   用户看到的就是：勾了目录、点了恢复，里面的文件**一点没变**。
            //   正确语义：勾选一个目录 = 勾选它**以及它下面的整棵子树**；
            //   勾选一个文件仍然只影响那一个路径（文件不可能有子项，行为不变）。
            var keep = new List<string>();
            foreach (var p in includePaths)
            {
                var normalized = PathUtil.NormalizeRelative(p);
                if (normalized.Length > 0) keep.Add(normalized);
            }

            diff.Changes.RemoveAll(c => !keep.Any(p =>
                PathUtil.Comparer.Equals(p, c.RelativePath) || PathUtil.IsUnder(p, c.RelativePath)));
        }
        AttachEventTimes(rootId, diff, snapshot.TimestampUtc, _clock.UtcNow);

        return new TreeDiffResult
        {
            Snapshot = snapshot,
            Target = target,
            Current = current,
            Diff = diff,
            ResolvedLocalTime = snapshot.TimestampLocal,
        };
    }

    /// <summary>
    /// 比较两个快照之间的差异（"把 from 的内容恢复到 to"）。
    ///
    /// 用途：恢复面板允许把"恢复到"那一端选成另一个时间点，
    /// 此时目标状态是 to 快照的内容，而不是当前磁盘状态。
    /// </summary>
    public TreeDiffResult? CompareBetweenSnapshots(long rootId, Snapshot from, Snapshot to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var fromManifest = _snapshots.LoadManifest(from);
        var toManifest = _snapshots.LoadManifest(to);

        var earliest = from.TimestampUtc <= to.TimestampUtc ? from.TimestampUtc : to.TimestampUtc;
        var latest = from.TimestampUtc <= to.TimestampUtc ? to.TimestampUtc : from.TimestampUtc;

        var evidence = new TreeComparer.RenameEvidence();
        foreach (var e in _events.QueryRenameEvidence(rootId, earliest, latest.AddSeconds(1)))
        {
            if (e.OldRelativePath is { Length: > 0 }) evidence.Add(e.OldRelativePath, e.RelativePath);
        }

        var diff = TreeComparer.Compare(toManifest, fromManifest, evidence, TreeComparer.DefaultMaxChanges);

        return new TreeDiffResult
        {
            Snapshot = from,
            Target = toManifest,
            Current = fromManifest,
            Diff = diff,
            ResolvedLocalTime = from.TimestampLocal,
        };
    }

    /// <summary>
    /// 计算某个时间点与当前状态的差异（按**UTC** 指定）。
    /// </summary>
    public TreeDiffResult? CompareWithCurrent(long rootId, DateTime atLocal, int maxChanges = TreeComparer.DefaultMaxChanges)
    {
        var atUtc = atLocal.Kind == DateTimeKind.Utc ? atLocal : atLocal.ToUniversalTime();
        var snapshot = _snapshots.GetAtOrBefore(rootId, atUtc);
        return snapshot is null ? null : CompareWithSnapshot(rootId, snapshot, maxChanges);
    }

    private void AttachEventTimes(long rootId, TreeDiff diff, DateTime fromUtc, DateTime toUtc)
    {
        if (diff.Changes.Count == 0) return;

        // 为每条变化补上"最近一次发生时间"与"关联进程摘要"（来自事件日志的事实）
        var byPath = new Dictionary<string, FileEvent>(PathUtil.Comparer);
        foreach (var e in _events.Query(new EventQuery
        {
            RootId = rootId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            IncludeTransient = false,
            Limit = 20000,
            Descending = false,
        }))
        {
            byPath[e.RelativePath] = e;
            if (e.OldRelativePath is { Length: > 0 }) byPath.TryAdd(e.OldRelativePath, e);
        }

        foreach (var change in diff.Changes)
        {
            if (byPath.TryGetValue(change.RelativePath, out var e))
            {
                change.LastEventUtc = e.TimestampUtc;
                change.AttributionSummary = DescribeAttribution(e);
            }
        }
    }

    public static string? DescribeAttribution(FileEvent e)
    {
        if (e.AttributedProcess is null) return null;
        var friendly = Windows.Processes.ProcessProbe.DescribeProcess(e.AttributedProcess);
        var confidence = e.Confidence switch
        {
            AttributionConfidence.Handler => "确认持有文件句柄",
            AttributionConfidence.Likely => "该时刻的前台进程",
            AttributionConfidence.Nearby => "该时刻附近活跃",
            _ => "关联",
        };
        return $"{friendly}（{confidence}）";
    }

    // ─────────────────────────────────────────────────────────────────────
    // 文件级 Diff
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>两个内容对象之间的差异（文本 → 行级/行内 Diff；二进制 → 只给元数据）。</summary>
    public FileDiffResult CompareContent(long? objectIdA, long? objectIdB, string relativePath, long maxBytes = 64L * 1024 * 1024)
    {
        var result = new FileDiffResult { RelativePath = relativePath };

        result.AvailableA = ReadContent(objectIdA, maxBytes, out var bytesA, out var errorA, out var sizeA);
        result.AvailableB = ReadContent(objectIdB, maxBytes, out var bytesB, out var errorB, out var sizeB);
        result.SizeA = sizeA;
        result.SizeB = sizeB;
        result.ErrorA = errorA;
        result.ErrorB = errorB;

        if (bytesA is null && bytesB is null)
        {
            result.Note = "两侧内容都无法读取，无法比较。";
            return result;
        }

        bytesA ??= Array.Empty<byte>();
        bytesB ??= Array.Empty<byte>();

        var classA = Core.Diff.TextClassifier.Classify(bytesA.AsSpan(0, Math.Min(bytesA.Length, Core.Diff.TextClassifier.ProbeSize)), relativePath, bytesA.LongLength);
        var classB = Core.Diff.TextClassifier.Classify(bytesB.AsSpan(0, Math.Min(bytesB.Length, Core.Diff.TextClassifier.ProbeSize)), relativePath, bytesB.LongLength);

        result.IsTextA = classA.IsText;
        result.IsTextB = classB.IsText;
        result.EncodingA = classA.EncodingName;
        result.EncodingB = classB.EncodingName;

        if (bytesA.Length == bytesB.Length && bytesA.AsSpan().SequenceEqual(bytesB))
        {
            result.IsBinary = !classA.IsText;
            result.Note = "两侧内容完全相同。";
            result.Diff = new Core.Diff.DiffResult { IsText = classA.IsText };
            return result;
        }

        if (classA.IsText && classB.IsText)
        {
            var textA = Core.Diff.TextClassifier.Decode(bytesA, classA);
            var textB = Core.Diff.TextClassifier.Decode(bytesB, classB);
            result.Diff = Core.Diff.DiffEngine.Compute(textA, textB);
            result.IsText = true;
            if (classA.HasBom != classB.HasBom)
            {
                result.Note = "注意：两侧的 BOM 状态不同（编码格式被改动过）。";
            }
            return result;
        }

        // 二进制：绝不做文本 Diff，只给可核对的元数据
        result.IsBinary = true;
        result.Note = "二进制内容不做文本比较，仅提供大小与哈希核对。";
        return result;
    }

    private bool ReadContent(long? objectId, long maxBytes, out byte[]? bytes, out string? error, out long? size)
    {
        bytes = null;
        error = null;
        size = null;
        if (objectId is null) { error = "该侧没有留存内容"; return false; }

        var obj = _store.FindById(objectId.Value);
        if (obj is null) { error = "内容对象不存在（可能已被历史清理）"; return false; }
        size = obj.LogicalSize;

        if (!_store.TryReadAllBytes(objectId.Value, maxBytes, out var data, out var err))
        {
            error = err;
            return false;
        }
        bytes = data;
        return true;
    }
}

/// <summary>状态对比结果（含时间点信息，UI 直接绑定）。</summary>
public sealed class TreeDiffResult
{
    public Snapshot Snapshot { get; init; } = new();
    public FileTreeManifest Target { get; init; } = new();
    public FileTreeManifest Current { get; init; } = new();
    public TreeDiff Diff { get; init; } = new();
    public DateTime ResolvedLocalTime { get; init; }

    /// <summary>该时间点是否早于最早的历史（用于 UI 如实提示"再往前没有记录了"）。</summary>
    public bool TargetIsApproximate => Snapshot.TimestampLocal != ResolvedLocalTime;
}

/// <summary>文件对比结果。</summary>
public sealed class FileDiffResult
{
    public string RelativePath { get; init; } = string.Empty;

    public bool IsText { get; set; }
    public bool IsBinary { get; set; }
    public bool IsTextA { get; set; }
    public bool IsTextB { get; set; }
    public string? EncodingA { get; set; }
    public string? EncodingB { get; set; }

    public bool AvailableA { get; set; }
    public bool AvailableB { get; set; }
    public long? SizeA { get; set; }
    public long? SizeB { get; set; }
    public string? ErrorA { get; set; }
    public string? ErrorB { get; set; }

    public Core.Diff.DiffResult? Diff { get; set; }

    public string? Note { get; set; }

    public bool HasDiffContent => Diff is not null && Diff.Lines.Count > 0;
}
