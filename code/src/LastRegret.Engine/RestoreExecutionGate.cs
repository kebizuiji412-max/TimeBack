namespace LastRegret.Engine;

/// <summary>
/// 恢复事务串行闸（P0-2）。
///
/// 为什么需要它：恢复在执行期间会暂停监听、创建安全点、逐个改写磁盘、再重新对齐索引。
/// 两个恢复同时跑（GUI 里连点两次、GUI 与 Agent 同时调用）会互相踩：
/// 各自的"执行前基线"、安全点、索引重对齐全部错位。
///
/// 两层闸：
///   · 进程内：<see cref="SemaphoreSlim"/>（同一进程里的并发调用）
///   · 进程间：命名 Mutex（GUI 与 Agent CLI 同时运行）
///
/// 命名用 <c>Local\</c> 而不是 <c>Global\</c>：恢复是"当前登录会话内的同一个数据目录"这件事，
/// 用 Local 命名空间就够了，也避免普通用户权限带来的创建失败。
///
/// 语义是**非阻塞**：已经有恢复在跑时**不排队**，直接拒绝并如实告诉用户，
/// 而不是让他盯着界面等几十秒。
/// </summary>
public static class RestoreExecutionGate
{
    private const string MutexName = @"Local\TimeBack.Restore.Transaction.v1";

    private static readonly SemaphoreSlim InProcess = new(1, 1);

    /// <summary>
    /// 一次"已取得恢复事务"的租约。必须 Dispose，且**必须在取得它的线程上** Dispose
    /// （命名 Mutex 的归属是线程级的）。
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private readonly int _ownerThreadId;
        private Mutex? _mutex;
        private bool _holdsMutex;
        private bool _holdsSemaphore;
        private bool _disposed;

        internal Lease(Mutex? mutex, bool holdsMutex, bool holdsSemaphore)
        {
            _mutex = mutex;
            _holdsMutex = holdsMutex;
            _holdsSemaphore = holdsSemaphore;
            _ownerThreadId = Environment.CurrentManagedThreadId;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 任何异常路径都不能把锁永久卡死：每一步各自兜住异常，继续释放后面的资源。
            if (_holdsMutex && _mutex is not null)
            {
                try
                {
                    if (Environment.CurrentManagedThreadId == _ownerThreadId)
                    {
                        _mutex.ReleaseMutex();
                    }
                    // 换了线程就只能靠 Dispose 让内核回收 —— 不能在这里抛出去，
                    // 否则 SemaphoreSlim 就永远放不掉了。
                }
                catch (Exception) { /* 释放失败不能阻断后续释放 */ }
                finally { _holdsMutex = false; }
            }

            try { _mutex?.Dispose(); }
            catch (Exception) { /* 同上 */ }
            finally { _mutex = null; }

            if (_holdsSemaphore)
            {
                _holdsSemaphore = false;
                try { InProcess.Release(); }
                catch (Exception) { /* 同上 */ }
            }
        }
    }

    /// <summary>
    /// 尝试取得恢复事务。返回 null 表示"当前已有另一个恢复正在执行"。
    /// </summary>
    public static Lease? TryEnter()
    {
        // 进程内闸：不排队，拿不到就直接拒绝。
        if (!InProcess.Wait(0)) return null;

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name: MutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者异常死亡 —— Windows 已经把 ownership 交给当前线程，
                // 因此这里**视为本次已经取得**，继续执行。
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                InProcess.Release();
                return null;
            }

            return new Lease(mutex, holdsMutex: true, holdsSemaphore: true);
        }
        catch (Exception)
        {
            try { mutex?.Dispose(); } catch (Exception) { /* 忽略 */ }
            InProcess.Release();
            return null;
        }
    }
}
