using LastRegret.Core.Compare;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;
using LastRegret.Core.Util;
using LastRegret.Engine;
using LastRegret.Runtime;

namespace LastRegret.App;

/// <summary>
/// 恢复流程协调器：UI 与"应用能力"之间的**流程层**。
///
/// 它负责的是"恢复这件事怎么走完"——生成预览、把用户的自定义选择合并成一份计划、
/// 执行、撤销、读取恢复记录；它<b>不</b>负责界面：不认识 WPF、不持有
/// <c>ObservableCollection</c>、不弹窗、不拼用户文案、不切页面。
///
/// 分工：
///   · 预览 / 执行 / 撤销 一律走第 6 刀建立的 <see cref="TimeBackApplication"/>（能力边界）；
///   · 只有 Application 未声明为 capability 的两项只读查询（删除预览、恢复记录列表）
///     与"展开目录勾选"用现有引擎实现（快照清单）。
///   · 恢复安全链完全沿用 <see cref="RestoreEngine"/>：预览指纹校验、恢复前安全点、
///     冲突跳过、差异执行、逐步记账、撤销 —— 本类一行都没有重写。
/// </summary>
public sealed class RestoreCoordinator
{
    private readonly TimeBackApplication _app;
    private readonly RestoreEngine _restore;
    private readonly SnapshotService _snapshots;

    public RestoreCoordinator(TimeBackApplication app, RestoreEngine restore, SnapshotService snapshots)
    {
        _app = app;
        _restore = restore;
        _snapshots = snapshots;
    }

    // ───────────────────────── 生成预览 ─────────────────────────

    /// <summary>capability: restore.preview。按 UTC 时间点生成预览，可选只处理勾选的路径。</summary>
    public (RestorePlan? Plan, string? Error) BuildPreview(
        long rootId, DateTime atUtc, IReadOnlyCollection<string>? includePaths = null) =>
        _app.PreviewRestore(rootId, atUtc, includePaths);

    /// <summary>生成"只删除这些路径"的预览（不是 capability，属于自定义删除的既有能力）。</summary>
    public (RestorePlan? Plan, string? Error) BuildDeletionPreview(
        long rootId, IReadOnlyCollection<string> deletionPaths) =>
        _restore.BuildDeletionPreview(rootId, deletionPaths);

    /// <summary>
    /// 把用户的"恢复集合"合并成**一份可执行的恢复计划**。
    ///
    /// 自定义恢复的关键就在这里：集合里的文件可能来自不同时间点，
    /// 所以按来源快照分组、各生成一份只含这些路径的计划，再把所有步骤合并成一个计划
    /// —— 引擎只消费 <c>plan.Steps</c> 与 <c>plan.Current</c>，因此不需要改动恢复引擎。
    ///
    /// <paramref name="picks"/> 是 (来源快照 Id, 相对路径)；勾选目录时展开成整棵子树，
    /// 因为引擎的 includePaths 是精确匹配，只传目录路径得不到里面的文件。
    ///
    /// 返回的 <c>Dropped</c> / <c>Unavailable</c> / <c>LatestTarget</c> 是给界面组文案用的**事实**，
    /// 文案本身由调用方决定。
    /// </summary>
    public (RestorePlan? Plan, string? Error, int Dropped, int Unavailable, DateTime? LatestTarget) BuildMergedPlan(
        long rootId,
        IReadOnlyList<(long SnapshotId, string RelativePath)> picks,
        IReadOnlyCollection<string> deletionPaths)
    {
        var merged = new RestorePlan { RootId = rootId };
        var seenPaths = new HashSet<string>(PathUtil.Comparer);
        var dropped = 0;
        var unavailable = 0;
        DateTime? latestTarget = null;
        long? latestTargetSnapshotId = null;

        // ① 要恢复的：按来源时间点分组
        foreach (var group in picks.GroupBy(p => p.SnapshotId))
        {
            var snapshot = _snapshots.Get(group.Key);
            if (snapshot is null) continue;

            var paths = ExpandDirectoryPicks(snapshot, group.Select(p => p.RelativePath).ToList());
            var (groupPlan, error) = _app.PreviewRestore(rootId, snapshot.TimestampUtc, paths);
            if (groupPlan is null)
            {
                return (null, error ?? "无法生成恢复计划。", dropped, unavailable, latestTarget);
            }

            // 冲突检测基线取第一次预览时的当前状态；所有分组共用同一份快照
            if (merged.Current.Entries.Count == 0) merged.Current = groupPlan.Current;

            foreach (var step in groupPlan.Steps)
            {
                // 同一路径被勾了两次（来自不同时间点）时，只保留第一次
                if (!seenPaths.Add(step.RelativePath)) { dropped++; continue; }
                merged.Steps.Add(step);
            }

            unavailable += groupPlan.UnavailableCount;
            if (latestTarget is null || snapshot.TimestampLocal > latestTarget)
            {
                latestTarget = snapshot.TimestampLocal;
                latestTargetSnapshotId = snapshot.Id;
            }
        }

        // ② 要删除的：并入同一个计划。恢复与删除的目标路径不应重叠，重叠时以删除为准。
        if (deletionPaths.Count > 0)
        {
            var (delPlan, delError) = BuildDeletionPreview(rootId, deletionPaths);
            if (delPlan is null)
            {
                return (null, delError ?? "无法生成删除计划。", dropped, unavailable, latestTarget);
            }
            if (merged.Current.Entries.Count == 0) merged.Current = delPlan.Current;

            foreach (var step in delPlan.Steps)
            {
                if (!seenPaths.Add(step.RelativePath)) continue;   // 同一路径已按恢复处理
                merged.Steps.Add(step);
            }
            merged.Warnings.AddRange(delPlan.Warnings);
        }

        if (!merged.HasEffect)
        {
            // 措辞按"用户看得懂的结论"写：他刚点完确认，需要知道"为什么什么都没发生"。
            return (null, "所选内容已经是目标状态，无需恢复。", dropped, unavailable, latestTarget);
        }

        merged.ComputeFingerprint();

        // ── 恢复记录里的"目标时间点"必须填上 ──
        // ⚠ 真实缺陷（本轮修复，BB-013）：合并计划原先只填 Steps/Current，
        //   TargetTimeUtc 保持默认值 = DateTime.MinValue，于是恢复记录里出现
        //   **0001-01-01**，时间线/记录页显示成乱码一样的时间。
        //   取**最新**的那个勾选来源，与确认弹窗里显示的 latestTarget 保持一致。
        if (latestTarget is not null)
        {
            merged.TargetTimeUtc = latestTarget.Value.ToUniversalTime();
            merged.TargetTimeLocal = latestTarget.Value;
            merged.TargetSnapshotId = latestTargetSnapshotId;
        }

        return (merged, null, dropped, unavailable, latestTarget);
    }

    // ───────────────────────── 执行与撤销 ─────────────────────────

    /// <summary>capability: restore.execute。指纹必须来自同一份预览，否则引擎会拒绝。</summary>
    public RestoreOutcome Execute(RestorePlan plan, string fingerprint, bool allowNewRemovals = true) =>
        _app.ExecuteRestore(plan, fingerprint, allowNewRemovals);

    /// <summary>capability: restore.undo（只读半）：最近一次可撤销的恢复操作。</summary>
    public RestoreOperation? GetLastUndoable(long? rootId) => _app.GetLastUndoableRestore(rootId);

    /// <summary>capability: restore.undo（只读半）：撤销预览（执行撤销需要它的指纹）。</summary>
    public (RestorePlan? Plan, string? Error) PreviewUndo(long operationId) => _app.PreviewUndo(operationId);

    /// <summary>capability: restore.undo（写入半）。</summary>
    public RestoreOutcome ExecuteUndo(long operationId, string fingerprint, bool allowNewRemovals = true) =>
        _app.UndoRestore(operationId, fingerprint, allowNewRemovals);

    // ───────────────────────── 恢复记录（只读） ─────────────────────────

    /// <summary>列出恢复记录（界面"恢复记录"列表用）。</summary>
    public IReadOnlyList<RestoreOperation> ListOperations(long? rootId, int limit) =>
        _restore.ListOperations(rootId, limit);

    /// <summary>列出一次恢复的每一步（排查用）。</summary>
    public IReadOnlyList<RestoreStepRecord> ListSteps(long operationId) => _restore.ListSteps(operationId);

    // ───────────────────────── 恢复页要用的读取 ─────────────────────────

    /// <summary>手动保存一个恢复点（"记下现在的状态"）。</summary>
    public Snapshot CreateSnapshot(long rootId, SnapshotKind kind, string? note) =>
        _snapshots.Create(rootId, kind, note);

    /// <summary>capability: timeline.read。列出可作为"恢复到哪个时间点"的恢复点（引擎已按时间倒序返回）。</summary>
    public IReadOnlyList<SnapshotPoint> ListTimePoints(long rootId, DateTime fromUtc, int limit = 300) =>
        _app.GetTimeline(rootId, fromUtc, null, limit);

    /// <summary>
    /// 一个恢复点的健康与可删性。引擎负责判断，界面只负责如实展示。
    /// 返回 null 表示这个恢复点已经查不到了（列表该刷新）。
    /// </summary>
    public (Snapshot Snapshot, bool IsSuspect, string? SuspectReason, bool CanDelete, string? DeleteBlockReason)?
        DescribePoint(long snapshotId)
    {
        var snapshot = _snapshots.Get(snapshotId);
        if (snapshot is null) return null;

        var health = _snapshots.CheckHealth(snapshot);
        var check = _snapshots.CanDelete(snapshot);
        return (snapshot, health.IsSuspect, health.Reason, check.Allowed, check.Reason);
    }

    /// <summary>读取某个时间点的清单条目（界面用它展开目录、以及显示"那个时刻的文件结构"）。</summary>
    public IReadOnlyList<ManifestEntry>? LoadManifestEntries(long snapshotId)
    {
        var snapshot = _snapshots.Get(snapshotId);
        if (snapshot is null) return null;
        return _snapshots.LoadManifest(snapshot).Entries.Values.ToList();
    }

    // ───────────────────────── 内部 ─────────────────────────

    /// <summary>
    /// 把"勾选了目录"展开成整棵子树。展开依据是该时间点的清单本身
    /// —— 也就是"恢复到它当时的完整样子"，而不是猜。勾选文件时原样返回。
    /// </summary>
    private List<string> ExpandDirectoryPicks(Snapshot snapshot, List<string> picked)
    {
        FileTreeManifest manifest;
        try
        {
            manifest = _snapshots.LoadManifest(snapshot);
        }
        catch (Exception)
        {
            return picked;                     // 读不到清单就按原样提交，由引擎给出明确结论
        }
        if (manifest.Entries.Count == 0) return picked;

        // 只看路径，不关心内容，所以用精确比较的集合开销更小
        var isDirectory = new HashSet<string>(PathUtil.Comparer);
        foreach (var e in manifest.Entries.Values)
        {
            if (e.Kind == EntryKind.Directory) isDirectory.Add(e.RelativePath);
        }

        var result = new List<string>(picked);
        var seen = new HashSet<string>(picked, PathUtil.Comparer);

        foreach (var path in picked)
        {
            if (!isDirectory.Contains(path)) continue;      // 是文件 → 原样保留
            var prefix = path + "/";
            foreach (var e in manifest.Entries.Values)
            {
                if (e.RelativePath.Length == 0) continue;
                if (!e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(e.RelativePath)) result.Add(e.RelativePath);
            }
        }

        return result;
    }
}
