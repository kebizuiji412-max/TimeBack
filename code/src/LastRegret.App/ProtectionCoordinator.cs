using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Engine;

namespace LastRegret.App;

/// <summary>
/// 保护范围协调器：把"添加 / 移除 / 暂停 / 重新扫描 / 基线扫描"这些**业务动作**
/// 从视图模型里拿出来，视图模型只留对话框、横幅、按钮可用性与行状态。
///
/// 它负责一件视图模型原本必须自己管的事：<b>扫描作用域的生命周期</b>
/// （<c>BeginScanScope</c> / <c>CancelScan</c> / <c>EndScanScope</c> 与"正在扫描哪些根"）。
/// 之前这套 CancellationTokenSource 的配对散在视图模型里，漏掉一次配对就会出现
/// "按钮一直停在取消中"或"扫描范围没释放"这类难查的问题。
///
/// 它不认识 WPF：不弹窗、不拼文案、不碰 ObservableCollection。
/// </summary>
public sealed class ProtectionCoordinator
{
    private readonly WatchService _watch;
    private readonly IWatchedRootRepository _roots;

    /// <summary>正在扫描的根 → 它的取消源。扫描可能由后台线程结束，所以读写都加锁。</summary>
    private readonly Dictionary<long, CancellationTokenSource> _scopes = new();
    private readonly object _scopeGate = new();

    public ProtectionCoordinator(WatchService watch, IWatchedRootRepository roots)
    {
        _watch = watch;
        _roots = roots;

        // 引擎事件只在这里订阅一次（视图模型订这个协调器，不再直接订引擎），
        // 避免"换页面/重建视图模型"时重复订阅。
        _watch.TimelineChanged += () => TimelineChanged?.Invoke();
        _watch.Logged += entry => Logged?.Invoke(entry);
    }

    /// <summary>时间线有变化（引擎侧通知，转发给界面）。</summary>
    public event Action? TimelineChanged;

    /// <summary>引擎日志（转发给界面）。</summary>
    public event Action<LogEntry>? Logged;

    // ───────────────────────── 读取 ─────────────────────────

    public IReadOnlyList<WatchedRoot> ListRoots() => _roots.ListAll();

    public WatchedRoot? GetRoot(long id) => _roots.Get(id);

    public RootRuntimeState GetState(long rootId) => _watch.GetState(rootId);

    public (bool HasBaseline, int BaselineFiles, long IndexEntries, long EventCount) DescribeRootHealth(long rootId) =>
        _watch.DescribeRootHealth(rootId);

    public IReadOnlyList<WatchedRoot> FindRootsMissingBaseline() => _watch.FindRootsMissingBaseline();

    public WatchStatistics Statistics => _watch.Statistics;

    // ───────────────────────── 业务动作 ─────────────────────────

    /// <summary>登记一个受保护目录（不扫描）。返回 (是否成功, 根 Id, 给用户看的原因)。</summary>
    public (bool Ok, long RootId, string Message) RegisterRoot(string path) => _watch.RegisterRoot(path);

    /// <summary>扫描前的规模评估（只枚举、不读内容）。</summary>
    public Rescanner.ScanEstimate EstimateScanScope(long rootId) => _watch.EstimateScanScope(rootId);

    /// <summary>暂停 / 恢复保护。</summary>
    public void SetEnabled(long rootId, bool enabled)
    {
        if (enabled) _watch.EnableRoot(rootId);
        else _watch.DisableRoot(rootId);
    }

    /// <summary>移出保护范围；<paramref name="deleteHistory"/> 决定历史记录是否一起删。</summary>
    public void RemoveRoot(long rootId, bool deleteHistory) => _watch.RemoveRoot(rootId, deleteHistory);

    // ───────────────────────── 扫描作用域 ─────────────────────────

    /// <summary>该根当前是否正在扫描（界面用它决定"取消扫描"是否可用）。</summary>
    public bool IsScanning(long rootId)
    {
        lock (_scopeGate) return _scopes.ContainsKey(rootId);
    }

    /// <summary>当前是否有任何根正在扫描（状态栏用）。</summary>
    public bool IsAnyScanning
    {
        get { lock (_scopeGate) return _scopes.Count > 0; }
    }

    /// <summary>
    /// 开始一次扫描并占用它的取消源；已经在扫描时返回 false（不重复启动）。
    /// 必须与 <see cref="EndScan"/> 配对 —— 由调用方在 finally 里保证。
    /// </summary>
    public bool TryBeginScan(long rootId, out CancellationToken token)
    {
        lock (_scopeGate)
        {
            if (_scopes.ContainsKey(rootId))
            {
                token = CancellationToken.None;
                return false;
            }
            var cts = _watch.BeginScanScope(rootId);
            _scopes[rootId] = cts;
            token = cts.Token;
        }
        return true;
    }

    /// <summary>执行一次基线扫描（真实读写磁盘，调用方负责放到后台线程）。</summary>
    public (int Files, int Dirs) RunBaseline(
        long rootId,
        int estimatedTotal,
        Action<Rescanner.ScanProgress>? progress,
        CancellationToken token) =>
        _watch.RunBaseline(rootId, progress, token, estimatedTotal);

    /// <summary>结束扫描作用域（取消源与"正在扫描"标记一起释放）。</summary>
    public void EndScan(long rootId)
    {
        CancellationTokenSource? cts;
        lock (_scopeGate)
        {
            if (!_scopes.TryGetValue(rootId, out cts)) return;
            _scopes.Remove(rootId);
        }
        _watch.EndScanScope(rootId, cts);
    }

    /// <summary>请求取消正在进行的扫描（已扫描的部分会保存）。</summary>
    public void CancelScan(long rootId) => _watch.CancelScan(rootId);
}
