using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Data;
using LastRegret.Engine;
using LastRegret.Runtime;

namespace LastRegret.App;

/// <summary>
/// 历史 / 时间线 / 文件详情协调器：把"要查哪些数据"从视图模型里拿出来。
///
/// 边界（与提示词 §十三/§十四 一致）：
///   · 它<b>只返回引擎的模型</b>（<see cref="FileEvent"/> / <see cref="FileVersion"/> /
///     <see cref="FileDiffResult"/>），<b>不碰</b> ObservableCollection，也不认识行对象；
///   · 行怎么造、列表怎么刷、选中哪一行，仍然由视图模型决定；
///   · 它不建任何新的数据层——变化记录走第 6 刀的 <c>changes.read</c> 能力，
///     版本与差异用现有仓储/引擎。
/// </summary>
public sealed class HistoryCoordinator
{
    private readonly TimeBackApplication _app;
    private readonly EventRepository _events;
    private readonly IFileVersionRepository _versions;
    private readonly CompareService _compare;

    public HistoryCoordinator(
        TimeBackApplication app,
        EventRepository events,
        IFileVersionRepository versions,
        CompareService compare)
    {
        _app = app;
        _events = events;
        _versions = versions;
        _compare = compare;
    }

    /// <summary>capability: <c>changes.read</c>。按条件读取变化记录。</summary>
    public IReadOnlyList<FileEvent> QueryEvents(EventQuery query) => _app.GetChanges(query);

    /// <summary>变化记录的总量统计（文件页展示用）。</summary>
    public (long Total, long Transient, long Last24h) GetEventStatistics(long? rootId) =>
        _events.GetStatistics(rootId);

    /// <summary>某个路径的全部历史版本（文件详情页）。</summary>
    public IReadOnlyList<FileVersion> ListVersions(long rootId, string relativePath, int limit = 200) =>
        _versions.ListForPath(rootId, relativePath, limit);

    /// <summary>两个版本之间的内容差异（文件详情页）。</summary>
    public FileDiffResult CompareContent(long? objectIdA, long? objectIdB, string relativePath) =>
        _compare.CompareContent(objectIdA, objectIdB, relativePath);
}
