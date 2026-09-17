using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;
using LastRegret.Data;
using LastRegret.Engine;

namespace LastRegret.Runtime;

/// <summary>
/// TimeBack 的能力门面（Application Capability Boundary）。
///
/// 它只做一件事：<b>把 TimeBack 对外真正允许调用的能力，统一映射到现有的业务服务上。</b>
/// 它本身不是第二套业务引擎 —— 不实现监听、不实现快照、不实现恢复算法、
/// 不碰 CAS、不碰 SQLite、不做冲突检测、不建安全点。
///
/// 边界（与 <c>docs/agent-protocol.md</c> 一致）：
///  · 不认识 WPF（本程序集目标框架为 net8.0，编译期就拿不到 WPF 程序集）；
///  · 不出现任何 SQL，也不出现 SqliteConnection / 表名等内部实现；
///  · 恢复一律经过 <see cref="RestoreEngine"/>，因此预览指纹校验、恢复前安全点、
///    冲突跳过、差异执行、逐步记账、撤销全部沿用既有安全链，门面不绕过任何一条；
///  · 只复用现有模型（<see cref="SnapshotPoint"/> / <see cref="FileEvent"/> /
///    <see cref="RestorePlan"/> / <see cref="RestoreOutcome"/>），不另建 DTO 体系。
///
/// 组装不在这里：依赖由 <see cref="AppRuntime"/>（组合根）提供。
/// </summary>
public sealed class TimeBackApplication
{
    private readonly WatchService _watch;
    private readonly CompareService _compare;
    private readonly EventRepository _events;
    private readonly RestoreEngine _restore;

    public TimeBackApplication(
        WatchService watch,
        CompareService compare,
        EventRepository events,
        RestoreEngine restore)
    {
        _watch = watch;
        _compare = compare;
        _events = events;
        _restore = restore;
    }

    /// <summary>从组合根取得能力门面（不在此处组装任何组件）。</summary>
    public static TimeBackApplication From(AppRuntime runtime) =>
        new(runtime.Watch, runtime.Compare, runtime.Events, runtime.Restore);

    /// <summary>
    /// capability: <c>protected-folders.read</c>（只读）
    /// 读取当前受保护的文件夹清单：路径、是否正在监听、是否已建立基线。
    /// </summary>
    public IReadOnlyList<RootRuntimeState> GetProtectedFolders() => _watch.GetStates();

    /// <summary>
    /// capability: <c>timeline.read</c>（只读）
    /// 读取某个受保护文件夹在指定时间范围内的恢复点（时间点）清单及其时间与规模信息。
    /// </summary>
    public IReadOnlyList<SnapshotPoint> GetTimeline(
        long rootId,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 300) => _compare.ListPoints(rootId, fromUtc, toUtc, limit);

    /// <summary>
    /// capability: <c>changes.read</c>（只读）
    /// 读取变化记录：哪个路径在什么时候发生了什么变化。筛选条件由 <see cref="EventQuery"/> 表达。
    /// </summary>
    public IReadOnlyList<FileEvent> GetChanges(EventQuery query) => _events.Query(query);

    /// <summary>
    /// capability: <c>restore.preview</c>（只读）
    /// 生成预览：恢复到某个时间点会创建 / 覆盖 / 删除哪些路径。
    /// <paramref name="atUtc"/> 必须是 UTC（与撤销、修复路径使用同一套精确语义）；
    /// <paramref name="includePaths"/> 为 null 表示全部差异。
    /// <b>不写磁盘、不建快照、不写内容库。</b>
    /// </summary>
    public (RestorePlan? Plan, string? Error) PreviewRestore(
        long rootId,
        DateTime atUtc,
        IReadOnlyCollection<string>? includePaths = null) =>
        _restore.BuildPreviewAt(rootId, atUtc, includePaths);

    /// <summary>
    /// capability: <c>restore.execute</c>（写入）
    /// 执行一份已经预览并确认过的恢复计划。<paramref name="confirmFingerprint"/> 必须与
    /// 预览计划的指纹一致，否则拒绝执行（原样沿用 <see cref="RestoreEngine.Execute"/> 的安全链）。
    /// </summary>
    public RestoreOutcome ExecuteRestore(
        RestorePlan plan,
        string confirmFingerprint,
        bool allowNewRemovals,
        Action<string>? log = null) =>
        _restore.Execute(plan, confirmFingerprint, allowNewRemovals, log);

    /// <summary>
    /// capability: <c>restore.undo</c>（只读的一半）
    /// 取最近一次"可以撤销"的恢复操作。撤销必须基于一个实际存在且可撤销的操作，
    /// 这个方法是外部调用方发现它的入口。
    /// </summary>
    public RestoreOperation? GetLastUndoableRestore(long? rootId = null) => _restore.GetLastUndoable(rootId);

    /// <summary>
    /// capability: <c>restore.undo</c>（只读的一半）
    /// 生成撤销预览。<b>撤销同样必须先预览再执行</b>：<see cref="UndoRestore"/> 需要
    /// 这里返回的计划指纹，因此调用方无法跳过确认步骤直接撤销。
    /// </summary>
    public (RestorePlan? Plan, string? Error) PreviewUndo(long operationId) => _restore.BuildUndoPreview(operationId);

    /// <summary>
    /// capability: <c>restore.undo</c>（写入）
    /// 撤销一次实际存在且可撤销的恢复操作：以该操作的"恢复前安全点"为目标再执行一次恢复，
    /// 因此撤销同样可预览、可追踪、可再次撤销（原样沿用 <see cref="RestoreEngine.ExecuteUndo"/>）。
    /// </summary>
    public RestoreOutcome UndoRestore(
        long operationId,
        string confirmFingerprint,
        bool allowNewRemovals,
        Action<string>? log = null) =>
        _restore.ExecuteUndo(operationId, confirmFingerprint, allowNewRemovals, log);
}
