using System.Runtime.InteropServices;
using LastRegret.Windows.Native;

namespace LastRegret.Windows.Processes;

/// <summary>一条系统句柄记录。</summary>
public readonly record struct SystemHandleEntry(
    int ProcessId,
    IntPtr HandleValue,
    ushort ObjectTypeIndex,
    uint GrantedAccess);

/// <summary>
/// 系统句柄枚举（NtQuerySystemInformation + SystemExtendedHandleInformation）。
///
/// 用途：为"哪个进程持有该文件"提供**内核级证据**（在权限允许时）。
/// 本机实测：枚举本身可用（约 22 万个句柄，耗时约 0.2 秒），
/// 但跨进程 DuplicateHandle 几乎总被拒绝，因此该证据通常拿不到 ——
/// 调用方必须准备好降级到"关联进程"。
///
/// 性能与安全考虑：
///  - 结果按需枚举，不做后台常驻；
///  - NtQueryObject 对某些句柄会**永久阻塞**，因此调用时放在独立线程并设超时，
///    超时后放弃（绝不把主流程卡死）；
///  - 缓冲区按内核返回的需求大小分配一次并缓存，避免反复大块分配。
/// </summary>
internal static class SystemHandleEnumerator
{
    private const int MaxBufferBytes = 256 * 1024 * 1024;
    private const int QueryObjectTimeoutMs = 1200;

    /// <summary>
    /// 句柄快照的缓存有效期（毫秒）。
    ///
    /// ⚠ 真实缺陷（本轮修复，低配置性能事故的主因之一）：
    ///   一次枚举要 0.2 秒以上、十几到二十几万条记录，而调用方是**按事件**来问的
    ///   （批量修改 5000 个文件就是 5000 次）。加上每次解析对象名都新建线程，
    ///   结果就是线程数随事件数失控、CPU 被打满、几十分钟跑不完。
    ///   这里做短 TTL 缓存：同一批事件复用同一份快照。
    ///   取 1.5 秒是折中 —— 归属本来就是"尽力而为"的辅助证据，稍旧完全可以接受；
    ///   正常使用每秒只有零星几个事件，缓存几乎不影响新鲜度。
    /// </summary>
    private const int CaptureTtlMs = 1500;

    private static readonly object CaptureGate = new();
    private static IReadOnlyList<SystemHandleEntry>? _cached;
    private static long _cachedAtMs;

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>枚举全部系统句柄（带短 TTL 缓存，见 <see cref="CaptureTtlMs"/>）。</summary>
    public static bool TryCapture(out IReadOnlyList<SystemHandleEntry> entries)
    {
        lock (CaptureGate)
        {
            if (_cached is not null && Environment.TickCount64 - _cachedAtMs < CaptureTtlMs)
            {
                entries = _cached;
                return true;
            }
        }

        if (!TryCaptureFresh(out var fresh))
        {
            entries = Array.Empty<SystemHandleEntry>();
            return false;
        }

        lock (CaptureGate)
        {
            _cached = fresh;
            _cachedAtMs = Environment.TickCount64;
        }
        entries = fresh;
        return true;
    }

    private static readonly object FileIndexGate = new();
    private static IReadOnlyDictionary<int, List<SystemHandleEntry>>? _fileHandlesByPid;
    private static ushort _fileHandlesTypeIndex;
    private static long _fileHandlesAtMs;

    /// <summary>
    /// 取某个进程持有的**文件类型**句柄（用上面那份缓存快照按进程号建索引，建一次复用 1.5 秒）。
    ///
    /// 为什么需要它：旧调用方是"候选进程 × 全部系统句柄"的双层循环 —— 本机句柄表有二十多万条，
    /// 每个事件都要扫一遍。改成按进程号预先分桶之后，每次事件只需要看候选进程自己那几条句柄，
    /// 成本从"每个事件几百万次比较"降到"几十次"，而**能力一点没减**（该试的候选照样试）。
    /// </summary>
    public static IReadOnlyList<SystemHandleEntry> FileHandlesOf(int pid, ushort fileTypeIndex)
    {
        lock (FileIndexGate)
        {
            if (_fileHandlesByPid is null ||
                _fileHandlesTypeIndex != fileTypeIndex ||
                Environment.TickCount64 - _fileHandlesAtMs >= CaptureTtlMs)
            {
                if (!TryCapture(out var entries)) return Array.Empty<SystemHandleEntry>();

                var map = new Dictionary<int, List<SystemHandleEntry>>();
                foreach (var e in entries)
                {
                    if (e.ObjectTypeIndex != fileTypeIndex) continue;
                    if (!map.TryGetValue(e.ProcessId, out var list))
                    {
                        map[e.ProcessId] = list = new List<SystemHandleEntry>(4);
                    }
                    list.Add(e);
                }

                _fileHandlesByPid = map;
                _fileHandlesTypeIndex = fileTypeIndex;
                _fileHandlesAtMs = Environment.TickCount64;
            }

            return _fileHandlesByPid.TryGetValue(pid, out var found)
                ? found
                : Array.Empty<SystemHandleEntry>();
        }
    }

    /// <summary>真正做一次枚举（不带缓存）。</summary>
    private static bool TryCaptureFresh(out IReadOnlyList<SystemHandleEntry> entries)
    {
        entries = Array.Empty<SystemHandleEntry>();

        int size = 8 * 1024 * 1024;
        IntPtr buffer = IntPtr.Zero;
        int needed;

        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                int status = Win32.NtQuerySystemInformation(
                    Win32.SystemExtendedHandleInformation, buffer, size, out needed);

                if (status == 0) break;

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;

                // STATUS_INFO_LENGTH_MISMATCH：按内核要求的长度重试（留出余量）
                if ((uint)status != 0xC0000004) return false;

                size = Math.Min(MaxBufferBytes, needed + (2 * 1024 * 1024));
                if (size >= MaxBufferBytes) return false;
            }

            if (buffer == IntPtr.Zero) return false;

            long count = Marshal.ReadInt64(buffer);
            if (count <= 0 || count > 4_000_000) return false;

            int entrySize = Marshal.SizeOf<Win32.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            var list = new List<SystemHandleEntry>((int)Math.Min(count, 400_000));
            IntPtr arrayStart = IntPtr.Add(buffer, 16);

            for (long i = 0; i < count; i++)
            {
                var e = Marshal.PtrToStructure<Win32.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(
                    IntPtr.Add(arrayStart, (int)(i * entrySize)));

                int pid = (int)e.UniqueProcessId.ToInt64();
                if (pid <= 0) continue;

                list.Add(new SystemHandleEntry(pid, e.HandleValue, e.ObjectTypeIndex, e.GrantedAccess));
            }

            entries = list;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static ushort? _fileTypeIndex;

    /// <summary>
    /// 找出"文件"类型的 ObjectTypeIndex。
    ///
    /// 做法：打开一个自己知道的文件，枚举句柄找到它，读出其 ObjectTypeIndex。
    /// 这样得到的索引在本机是自校验的，不依赖硬编码的魔法数字。
    /// </summary>
    public static int FindFileTypeIndex()
    {
        if (_fileTypeIndex is not null) return _fileTypeIndex.Value;

        string? temp = null;
        try
        {
            temp = Path.Combine(Path.GetTempPath(), $"lastregret-typeprobe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "probe");

            using var fs = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var raw = fs.SafeFileHandle.DangerousGetHandle();
            int myPid = Environment.ProcessId;

            if (!TryCapture(out var entries)) return -1;

            foreach (var e in entries)
            {
                if (e.ProcessId == myPid && e.HandleValue == raw)
                {
                    _fileTypeIndex = e.ObjectTypeIndex;
                    return e.ObjectTypeIndex;
                }
            }
            return -1;
        }
        catch (Exception)
        {
            return -1;
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (IOException) { /* 探测文件残留不影响功能 */ }
            }
        }
    }

    /// <summary>
    /// 把一个句柄解析为对象名（文件句柄即设备路径）。
    ///
    /// ⚠ 真实缺陷（本轮修复）：
    ///   旧实现**每次调用都新建一个线程**去跑 NtQueryObject，超时（1.2 秒）就把它丢掉。
    ///   NtQueryObject 对某些对象会永久阻塞，于是被丢掉的那些线程永远出不来：
    ///   批量事件下线程数一路涨到上千、结束后也不回落（实测 5000 个文件 → 峰值 1602 线程、
    ///   结束后仍残留 1584）。另外超时后调用方会释放缓冲区，而那个被丢掉的线程可能还在写它
    ///   —— 潜在的 use-after-free。
    ///
    /// 现在：**唯一的专职线程** + **有界队列** + **熔断**。
    ///   · 线程只有一个，永远不新增、也永远不会被"丢掉"；
    ///   · 队列满了就直接放弃（归属本来就只是尽力而为的证据），绝不排队堆积；
    ///   · 一旦出现超时，说明本机存在会永久阻塞的对象 → 打开熔断一段时间，
    ///     期间直接返回 null，把这台机器上"拿不到内核级证据"这件事如实降级，
    ///     而不是继续白烧 CPU 和线程。
    /// </summary>
    public static string? QueryObjectName(IntPtr handle)
    {
        // 熔断中：直接放弃，不做任何尝试
        if (Environment.TickCount64 < Volatile.Read(ref _circuitOpenUntilMs)) return null;

        var request = new QueryRequest { Handle = handle };
        EnsureQueryWorker();

        if (!QueryQueue.TryAdd(request)) return null;          // 队列满 → 放弃

        if (!request.Done.Wait(QueryObjectTimeoutMs))
        {
            // 超时：worker 可能永久卡在这次调用里。不释放任何属于它的资源，
            // 只把熔断打开，避免后续每个事件都再赔上 1.2 秒。
            Volatile.Write(ref _circuitOpenUntilMs, Environment.TickCount64 + CircuitBreakMs);
            return null;
        }

        return request.Result;
    }

    /// <summary>熔断持续时间：出现超时后这段时间内不再尝试解析对象名。</summary>
    private const int CircuitBreakMs = 30_000;

    private static readonly System.Collections.Concurrent.BlockingCollection<QueryRequest> QueryQueue =
        new(new System.Collections.Concurrent.ConcurrentQueue<QueryRequest>(), 4);

    private static readonly object WorkerGate = new();
    private static Thread? _queryWorker;
    private static long _circuitOpenUntilMs;

    private sealed class QueryRequest
    {
        public IntPtr Handle;
        public string? Result;
        public readonly ManualResetEventSlim Done = new(false);
    }

    private static void EnsureQueryWorker()
    {
        if (_queryWorker is not null) return;
        lock (WorkerGate)
        {
            if (_queryWorker is not null) return;
            var worker = new Thread(QueryWorkerLoop)
            {
                IsBackground = true,
                Name = "LastRegret.QueryObjectName",
            };
            worker.Start();
            _queryWorker = worker;
        }
    }

    private static void QueryWorkerLoop()
    {
        foreach (var request in QueryQueue.GetConsumingEnumerable())
        {
            // 缓冲区由 worker 自己分配与释放：调用方超时返回后不会再碰它，
            // 也就不存在"调用方释放、worker 还在写"的竞态。
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(16 * 1024);
                int status = Win32.NtQueryObject(request.Handle, Win32.ObjectNameInformation, buffer, 16 * 1024, out _);
                if (status == 0)
                {
                    var us = Marshal.PtrToStructure<Win32.UNICODE_STRING>(buffer);
                    if (us.Length > 0 && us.Buffer != IntPtr.Zero)
                    {
                        request.Result = Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                    }
                }
            }
            catch (Exception)
            {
                // 解析失败不影响调用方：它只是拿不到内核级证据
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                try { request.Done.Set(); } catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>
    /// 建立"设备路径 → 盘符"映射（\Device\HarddiskVolume3\... → D:\...）。
    /// 通过 QueryDosDevice 逐个盘符查询得到，不猜。
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var buffer = new char[512];
            int len = Win32.GetLogicalDriveStringsW(buffer.Length, buffer);
            if (len <= 0) return map;

            var drives = new string(buffer, 0, len).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var target = new char[1024];
            foreach (var drive in drives)
            {
                var letter = drive.TrimEnd('\\');
                int n = Win32.QueryDosDeviceW(letter, target, target.Length);
                if (n <= 0) continue;
                var device = new string(target, 0, n).TrimEnd('\0');
                if (device.Length > 0) map[device] = letter + "\\";
            }
        }
        catch (Exception)
        {
            // 映射失败时调用方应把设备路径原样展示（诚实但不好看）
        }
        return map;
    }

    /// <summary>把设备路径转成盘符路径；无法转换时原样返回。</summary>
    public static string DeviceToDos(string devicePath, IReadOnlyDictionary<string, string> map)
    {
        foreach (var (device, drive) in map)
        {
            if (devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                return drive + devicePath[device.Length..].TrimStart('\\');
        }
        return devicePath;
    }
}
