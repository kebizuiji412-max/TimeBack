using LastRegret.Core.Model;
using LastRegret.Windows.Watching;

namespace LastRegret.Engine;

/// <summary>
/// 监听器实例的管理与"实例一致性"保护。
///
/// 它管什么：创建 <see cref="DirectoryWatcher"/>（含"回调必须认得出是哪个实例"的自引用）、
/// 登记与取出、启动、停止与释放、暂停/恢复，以及"这一枪是不是当前那个实例报的"判定。
///
/// 它<b>不</b>管：数据库、索引、快照、历史版本 —— 那些是
/// <see cref="WatchEventPipeline"/> 的事。
///
/// ⚠ <b>锁的所有权仍在 <see cref="WatchService"/></b>：本组件的每个成员都必须在
/// <c>_gate</c> 内调用。这是刻意的 —— WatchService 的线程模型是"统一状态机 + 一把锁"，
/// 若这里自带一把锁，就会把同一个状态机拆成多把锁，反而引入新的竞态。
/// </summary>
internal sealed class WatcherRegistry
{
    private readonly Dictionary<long, DirectoryWatcher> _watchers = new();

    /// <summary>
    /// 全部已登记的监听器。供"有几个在监听 / 溢出累计"这类只读汇总使用
    /// —— 与拆分前一样是**不加锁**的读取，本刀不改变这个既有语义。
    /// </summary>
    public ICollection<DirectoryWatcher> All => _watchers.Values;

    /// <summary>该根是否已经有一个正在运行的监听器。</summary>
    public bool IsRunning(long rootId) => _watchers.TryGetValue(rootId, out var w) && w.IsRunning;

    /// <summary>取当前登记的监听器（供测试观察实例状态）。</summary>
    public bool TryGet(long rootId, out DirectoryWatcher? watcher) =>
        _watchers.TryGetValue(rootId, out watcher);

    /// <summary>
    /// 创建并登记一个监听器。
    ///
    /// 回调必须认得出"是我这个实例在报错"：暂停→恢复会先后存在两个监听器实例，
    /// 被停掉的那个如果迟到报错，绝不能把新实例的状态改掉（见 <see cref="IsCurrent"/>）。
    /// </summary>
    public DirectoryWatcher CreateAndRegister(
        long rootId,
        WatchedRoot root,
        Action<WatchBatch> onBatch,
        Action<DirectoryWatcher, WatchError> onError)
    {
        DirectoryWatcher? self = null;
        self = new DirectoryWatcher(
            rootId,
            root.Path,
            root.IncludeSubdirectories,
            batch => onBatch(batch),
            error => onError(self!, error));
        _watchers[rootId] = self;
        return self;
    }

    /// <summary>把监听器从登记集合里取出（停止与释放由调用方在锁外做，与拆分前一致）。</summary>
    public DirectoryWatcher? TakeOut(long rootId)
    {
        _watchers.Remove(rootId, out var watcher);
        return watcher;
    }

    /// <summary>停止并释放一个已经取出的监听器。</summary>
    public static void Shutdown(DirectoryWatcher? watcher)
    {
        watcher?.Stop(TimeSpan.FromSeconds(3));
        watcher?.Dispose();
    }

    public void Pause(long rootId)
    {
        if (_watchers.TryGetValue(rootId, out var watcher)) watcher.Pause();
    }

    public void Resume(long rootId)
    {
        if (_watchers.TryGetValue(rootId, out var watcher)) watcher.Resume();
    }

    /// <summary>
    /// 这一枪是不是"当前登记在案的那个实例"报的？
    /// 监听集合才是唯一事实来源，陈旧实例的迟到报告一律丢弃。
    /// </summary>
    public bool IsCurrent(DirectoryWatcher source, long rootId) =>
        _watchers.TryGetValue(rootId, out var current) && ReferenceEquals(current, source);

    /// <summary>取出全部监听器并清空登记（关闭时用）。</summary>
    public List<DirectoryWatcher> TakeAll()
    {
        var all = _watchers.Values.ToList();
        _watchers.Clear();
        return all;
    }
}
