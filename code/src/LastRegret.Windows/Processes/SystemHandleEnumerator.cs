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

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>枚举全部系统句柄。</summary>
    public static bool TryCapture(out IReadOnlyList<SystemHandleEntry> entries)
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

    /// <summary>把一个句柄解析为对象名（文件句柄即设备路径）。带超时保护。</summary>
    public static string? QueryObjectName(IntPtr handle)
    {
        IntPtr buffer = IntPtr.Zero;
        string? result = null;
        try
        {
            buffer = Marshal.AllocHGlobal(16 * 1024);
            int length;
            int status = 0;

            // NtQueryObject 对管道/同步对象可能永久阻塞 → 放到独立线程并设超时
            var worker = new Thread(() =>
            {
                status = Win32.NtQueryObject(handle, Win32.ObjectNameInformation, buffer, 16 * 1024, out length);
            })
            {
                IsBackground = true,
                Name = "LastRegret.QueryObjectName",
            };
            worker.Start();

            if (!worker.Join(QueryObjectTimeoutMs)) return null;
            if (status != 0) return null;

            var us = Marshal.PtrToStructure<Win32.UNICODE_STRING>(buffer);
            if (us.Length <= 0 || us.Buffer == IntPtr.Zero) return null;
            result = Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            return result;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
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
