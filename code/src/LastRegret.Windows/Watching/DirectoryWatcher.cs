using System.Runtime.InteropServices;
using System.Text;
using LastRegret.Core.Events;
using LastRegret.Core.Util;
using LastRegret.Windows.Native;

namespace LastRegret.Windows.Watching;

/// <summary>watcher 发出的通知批次。</summary>
public sealed class WatchBatch
{
    public long RootId { get; init; }

    public string RootPath { get; init; } = string.Empty;

    public List<RawFsNotification> Notifications { get; } = new();

    /// <summary>内核通知缓冲区溢出（ERROR_NOTIFY_ENUM_DIR）：期间的变化可能丢失，必须重扫。</summary>
    public bool Overflowed { get; set; }

    /// <summary>本批是否包含 rename（用于上层快速判断）。</summary>
    public bool HasRename { get; set; }
}

/// <summary>watcher 观测到的异常（不致命，向上报告由引擎决定策略）。</summary>
public sealed class WatchError
{
    public long RootId { get; init; }

    public string RootPath { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool Fatal { get; init; }
}

/// <summary>
/// 单个受保护根目录的文件系统监听器。
///
/// 实现方式：<c>ReadDirectoryChangesW</c> + 重叠 I/O（OVERLAPPED + 事件对象）。
/// 为什么不用 FileSystemWatcher：
///  - FileSystemWatcher 在缓冲区溢出时丢事件且**不对上层暴露溢出**，
///    对本项目是致命的（会静默漏掉"项目被改坏"的关键事实）；
///  - 它把 rename 的旧名/新名拆成两个独立事件，且不提供批量语义；
///  - 需要自己控制"暂停/恢复"以便恢复操作期间不产生噪声事件。
///
/// 本实现保证：
///  1. **绝不丢事件而不告知**：一旦内核报告缓冲区溢出，立即置位 Overflowed，
///     由引擎触发全量重扫对齐（宁可多扫一次，绝不假装没发生）。
///  2. 暂停期间收到的一切通知都被丢弃并记录计数，恢复后主动重扫对齐。
///  3. 目录被删除/句柄失效时自动降级为"关闭并报告"，不会静默变成死监听。
/// </summary>
public sealed class DirectoryWatcher : IDisposable
{
    private const int NotifyFilter =
        Win32.FILE_NOTIFY_CHANGE_FILE_NAME |
        Win32.FILE_NOTIFY_CHANGE_DIR_NAME |
        Win32.FILE_NOTIFY_CHANGE_SIZE |
        Win32.FILE_NOTIFY_CHANGE_LAST_WRITE |
        Win32.FILE_NOTIFY_CHANGE_CREATION;

    private readonly long _rootId;
    private readonly string _rootPath;
    private readonly bool _watchSubtree;
    private readonly int _bufferSize;
    private readonly Action<WatchBatch> _onBatch;
    private readonly Action<WatchError> _onError;

    private Thread? _thread;
    private volatile bool _disposed;
    private volatile bool _paused;
    private long _pausedDiscarded;
    private long _overflowCount;
    private long _batchCount;
    private long _readCount;
    private readonly ManualResetEventSlim _stopped = new(true);

    public DirectoryWatcher(
        long rootId,
        string rootPath,
        bool watchSubtree,
        Action<WatchBatch> onBatch,
        Action<WatchError> onError,
        int bufferSize = 64 * 1024)
    {
        _rootId = rootId;
        _rootPath = PathUtil.NormalizeRoot(rootPath);
        _watchSubtree = watchSubtree;
        _onBatch = onBatch;
        _onError = onError;
        _bufferSize = Math.Clamp(bufferSize, 8 * 1024, 4 * 1024 * 1024);
    }

    public long RootId => _rootId;

    public string RootPath => _rootPath;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => _thread is { IsAlive: true } && !_disposed;

    public bool IsPaused => _paused;

    public long PausedDiscardedCount => Interlocked.Read(ref _pausedDiscarded);

    public long OverflowCount => Interlocked.Read(ref _overflowCount);

    public long BatchCount => Interlocked.Read(ref _batchCount);

    public long ReadCount => Interlocked.Read(ref _readCount);

    /// <summary>启动监听线程。失败（例如目录不存在）会通过 onError 报告并返回 false。</summary>
    public bool Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DirectoryWatcher));
        if (IsRunning) return true;

        if (!Directory.Exists(_rootPath))
        {
            _onError(new WatchError { RootId = _rootId, RootPath = _rootPath, Message = "目录不存在，无法开始监听", Fatal = true });
            return false;
        }

        _stopped.Reset();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"LastRegret.Watch[{_rootId}]",
        };
        _thread.Start();
        return true;
    }

    /// <summary>暂停监听（恢复操作期间使用）。暂停期间的通知被丢弃，恢复后应重扫对齐。</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void Stop(TimeSpan? timeout = null)
    {
        if (_thread is null) return;
        _disposed = true;
        _stopped.Wait(timeout ?? TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        IntPtr handle = IntPtr.Zero;
        IntPtr overlappedPtr = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        IntPtr evt = IntPtr.Zero;
        try
        {
            handle = Win32.CreateFileW(
                FileSystemReaderExtend(_rootPath),
                Win32.FILE_LIST_DIRECTORY,
                Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
                IntPtr.Zero,
                Win32.OPEN_EXISTING,
                Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                ReportFatal(err, "无法打开目录句柄（权限不足或目录不存在）");
                return;
            }

            buffer = Marshal.AllocHGlobal(_bufferSize);

            // OVERLAPPED 的前两个字段是 Internal/InternalHigh；偏移 8 起是 union，
            // 手动构造保证偏移正确（避免依赖结构体封送细节）。
            overlappedPtr = Marshal.AllocHGlobal(IntPtr.Size == 8 ? 32 : 20);
            for (int i = 0; i < (IntPtr.Size == 8 ? 32 : 20); i++) Marshal.WriteByte(overlappedPtr, i, 0);
            evt = Win32.CreateEventW(IntPtr.Zero, true, false, null);
            Marshal.WriteIntPtr(overlappedPtr, IntPtr.Size == 8 ? 24 : 16, evt);

            if (!ReadDirectoryChangesWAsync(handle, buffer, overlappedPtr, out int startError))
            {
                ReportFatal(startError, "ReadDirectoryChangesW 启动失败");
                return;
            }
            Interlocked.Increment(ref _readCount);

            while (!_disposed)
            {
                uint wait = Win32Wait(evt, 250);
                if (wait == 258 /*WAIT_TIMEOUT*/) continue;
                if (_disposed) break;

                if (!Win32.GetOverlappedResult(handle, overlappedPtr, out int bytes, false))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Win32.ERROR_OPERATION_ABORTED) break;
                    if (err == Win32.ERROR_NOTIFY_ENUM_DIR || err == Win32.ERROR_MORE_DATA)
                    {
                        Interlocked.Increment(ref _overflowCount);
                        var overflowBatch = new WatchBatch { RootId = _rootId, RootPath = _rootPath, Overflowed = true };
                        TryEmit(overflowBatch);
                    }
                    else
                    {
                        _onError(new WatchError
                        {
                            RootId = _rootId,
                            RootPath = _rootPath,
                            Message = $"读取目录变化失败：{new System.ComponentModel.Win32Exception(err).Message}（可能目录已被删除或卸载）",
                            Fatal = true,
                        });
                        break;
                    }
                }
                else if (bytes > 0)
                {
                    var batch = ParseBuffer(buffer, bytes);
                    if (batch.Notifications.Count > 0 || batch.Overflowed) TryEmit(batch);
                }

                if (_disposed) break;

                // 重新挂起请求。暂停期间仍然挂起（保持内核侧队列有效），只是丢弃解析结果。
                if (!ReadDirectoryChangesWAsync(handle, buffer, overlappedPtr, out int rearmError))
                {
                    ReportFatal(rearmError, "重新挂起目录监听失败");
                    break;
                }
                Interlocked.Increment(ref _readCount);
            }
        }
        catch (Exception ex)
        {
            _onError(new WatchError
            {
                RootId = _rootId,
                RootPath = _rootPath,
                Message = $"监听线程异常终止：{ex.GetType().Name} {ex.Message}",
                Fatal = true,
            });
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                Win32.CancelIoEx(handle, IntPtr.Zero);
                Win32.CloseHandle(handle);
            }
            if (evt != IntPtr.Zero) Win32.CloseHandle(evt);
            if (overlappedPtr != IntPtr.Zero) Marshal.FreeHGlobal(overlappedPtr);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            _stopped.Set();
        }
    }

    private bool ReadDirectoryChangesWAsync(IntPtr handle, IntPtr buffer, IntPtr overlapped, out int error)
    {
        error = 0;
        bool ok = Win32.ReadDirectoryChangesW(
            handle, buffer, _bufferSize, _watchSubtree, NotifyFilter,
            out _, overlapped, IntPtr.Zero);
        if (ok) return true;

        int err = Marshal.GetLastWin32Error();
        if (err == 997 /*ERROR_IO_PENDING*/) return true;
        error = err;
        return false;
    }

    private void TryEmit(WatchBatch batch)
    {
        if (_paused)
        {
            Interlocked.Add(ref _pausedDiscarded, batch.Notifications.Count);
            // 暂停期间仍记录溢出事实：恢复后必须重扫
            if (batch.Overflowed) Interlocked.Increment(ref _overflowCount);
            return;
        }

        Interlocked.Increment(ref _batchCount);
        try
        {
            _onBatch(batch);
        }
        catch (Exception ex)
        {
            _onError(new WatchError
            {
                RootId = _rootId,
                RootPath = _rootPath,
                Message = $"处理通知时异常：{ex.GetType().Name} {ex.Message}",
                Fatal = false,
            });
        }
    }

    private void ReportFatal(int errorCode, string prefix)
    {
        _onError(new WatchError
        {
            RootId = _rootId,
            RootPath = _rootPath,
            Message = $"{prefix}：{new System.ComponentModel.Win32Exception(errorCode).Message} (err={errorCode})",
            Fatal = true,
        });
    }

    /// <summary>解析 FILE_NOTIFY_INFORMATION 链，并把 rename 的旧名/新名合并为一个逻辑动作。</summary>
    private WatchBatch ParseBuffer(IntPtr buffer, int bytes)
    {
        var batch = new WatchBatch { RootId = _rootId, RootPath = _rootPath };
        int offset = 0;
        string? pendingOldName = null;
        bool pendingOldIsDir = false;

        while (offset < bytes)
        {
            int nextOffset = Marshal.ReadInt32(buffer, offset);
            int action = Marshal.ReadInt32(buffer, offset + 4);
            int nameLength = Marshal.ReadInt32(buffer, offset + 8);
            if (nameLength <= 0 || nameLength > 65535) break;

            string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 12), nameLength / 2) ?? string.Empty;
            offset += 12 + nameLength;
            if (offset % 4 != 0) offset += 4 - (offset % 4);

            if (name.Length == 0) continue;

            string fullPath;
            try
            {
                fullPath = PathUtil.ToAbsolute(_rootPath, name);
            }
            catch (ArgumentException)
            {
                // 相对路径越界（理论上不会发生）：如实报告而不是猜测
                _onError(new WatchError
                {
                    RootId = _rootId,
                    RootPath = _rootPath,
                    Message = $"收到越界路径通知：{name}",
                    Fatal = false,
                });
                continue;
            }

            bool isDirectory = Directory.Exists(fullPath);

            switch (action)
            {
                case Win32.FILE_ACTION_RENAMED_OLD_NAME:
                    pendingOldName = fullPath;
                    pendingOldIsDir = isDirectory;
                    break;

                case Win32.FILE_ACTION_RENAMED_NEW_NAME:
                    if (pendingOldName is not null)
                    {
                        batch.Notifications.Add(new RawFsNotification
                        {
                            RootId = _rootId,
                            TimestampUtc = DateTime.UtcNow,
                            AbsolutePath = fullPath,
                            OldAbsolutePath = pendingOldName,
                            Kind = RawChangeKind.RenamedNew,
                            IsDirectory = isDirectory || pendingOldIsDir,
                            Sequence = batch.Notifications.Count,
                        });
                        batch.HasRename = true;
                        pendingOldName = null;
                    }
                    else
                    {
                        // 只有新名（异常情况）：如实记为"新增"，不臆造重命名
                        batch.Notifications.Add(new RawFsNotification
                        {
                            RootId = _rootId,
                            TimestampUtc = DateTime.UtcNow,
                            AbsolutePath = fullPath,
                            Kind = RawChangeKind.Added,
                            IsDirectory = isDirectory,
                            Sequence = batch.Notifications.Count,
                        });
                    }
                    break;

                case Win32.FILE_ACTION_ADDED:
                    batch.Notifications.Add(new RawFsNotification
                    {
                        RootId = _rootId,
                        TimestampUtc = DateTime.UtcNow,
                        AbsolutePath = fullPath,
                        Kind = RawChangeKind.Added,
                        IsDirectory = isDirectory,
                        Sequence = batch.Notifications.Count,
                    });
                    break;

                case Win32.FILE_ACTION_REMOVED:
                    batch.Notifications.Add(new RawFsNotification
                    {
                        RootId = _rootId,
                        TimestampUtc = DateTime.UtcNow,
                        AbsolutePath = fullPath,
                        Kind = RawChangeKind.Removed,
                        // 已删除的目录无法再探测，Files 通知也无法区分；这里保守地按"未知"处理，
                        // 上层会用索引判断是否为目录（索引里有记录就是目录）
                        IsDirectory = isDirectory,
                        Sequence = batch.Notifications.Count,
                    });
                    break;

                case Win32.FILE_ACTION_MODIFIED:
                    batch.Notifications.Add(new RawFsNotification
                    {
                        RootId = _rootId,
                        TimestampUtc = DateTime.UtcNow,
                        AbsolutePath = fullPath,
                        Kind = RawChangeKind.Modified,
                        IsDirectory = isDirectory,
                        Sequence = batch.Notifications.Count,
                    });
                    break;

                default:
                    batch.Notifications.Add(new RawFsNotification
                    {
                        RootId = _rootId,
                        TimestampUtc = DateTime.UtcNow,
                        AbsolutePath = fullPath,
                        Kind = RawChangeKind.Other,
                        IsDirectory = isDirectory,
                        Sequence = batch.Notifications.Count,
                    });
                    break;
            }
        }

        // 只有旧名、没有新名：交由上层在配对窗口后按"删除"处理
        if (pendingOldName is not null)
        {
            batch.Notifications.Add(new RawFsNotification
            {
                RootId = _rootId,
                TimestampUtc = DateTime.UtcNow,
                AbsolutePath = pendingOldName,
                Kind = RawChangeKind.RenamedOld,
                IsDirectory = pendingOldIsDir,
                Sequence = batch.Notifications.Count,
            });
            batch.HasRename = true;
        }

        return batch;
    }

    private static string FileSystemReaderExtend(string path) => Io.FileSystemReader.Extend(path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private static uint Win32Wait(IntPtr handle, uint ms) => WaitForSingleObject(handle, ms);

    public void Dispose()
    {
        Stop();
        _stopped.Dispose();
    }
}
