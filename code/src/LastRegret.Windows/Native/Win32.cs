using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LastRegret.Windows.Native;

/// <summary>
/// Win32 与 NT 原生能力的最小封装集合。
/// 所有 P/Invoke 集中在这里，便于审计与替换，避免散落在业务代码里。
/// </summary>
internal static class Win32
{
    // ── ReadDirectoryChangesW ─────────────────────────────────────────────
    public const int FILE_LIST_DIRECTORY = 0x0001;
    public const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const int FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int FILE_SHARE_READ = 0x00000001;
    public const int FILE_SHARE_WRITE = 0x00000002;
    public const int FILE_SHARE_DELETE = 0x00000004;
    public const int OPEN_EXISTING = 3;

    public const int FILE_NOTIFY_CHANGE_FILE_NAME = 0x00000001;
    public const int FILE_NOTIFY_CHANGE_DIR_NAME = 0x00000002;
    public const int FILE_NOTIFY_CHANGE_ATTRIBUTES = 0x00000004;
    public const int FILE_NOTIFY_CHANGE_SIZE = 0x00000008;
    public const int FILE_NOTIFY_CHANGE_LAST_WRITE = 0x00000010;
    public const int FILE_NOTIFY_CHANGE_CREATION = 0x00000040;
    public const int FILE_NOTIFY_CHANGE_SECURITY = 0x00000100;

    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_NOTIFY_ENUM_DIR = 1022;   // 通知缓冲区溢出（必须触发重扫）
    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_MORE_DATA = 234;

    public const int FILE_ACTION_ADDED = 0x00000001;
    public const int FILE_ACTION_REMOVED = 0x00000002;
    public const int FILE_ACTION_MODIFIED = 0x00000003;
    public const int FILE_ACTION_RENAMED_OLD_NAME = 0x00000004;
    public const int FILE_ACTION_RENAMED_NEW_NAME = 0x00000005;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
    /// <summary>重定向点（Junction / Symbolic Link / Mount Point）。物理边界守卫用它判断"这段路径是不是绕出去了"。</summary>
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    /// <summary>
    /// 取一个已打开句柄对应的**最终物理路径**（已解析 Junction / 符号链接）。
    /// 返回的字符串带 <c>\\?\</c> 前缀（UNC 是 <c>\\?\UNC\</c>），调用方需要自行规范化。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadDirectoryChangesW(
        IntPtr hDirectory,
        IntPtr lpBuffer,
        int nBufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
        int dwNotifyFilter,
        out int lpBytesReturned,
        IntPtr lpOverlapped,
        IntPtr lpCompletionRoutine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetOverlappedResult(IntPtr hFile, IntPtr lpOverlapped, out int lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string? lpName);

    // ── 驱动器 / 设备路径 ─────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int QueryDosDeviceW(string lpDeviceName, char[] lpTargetPath, int ucchMax);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetLogicalDriveStringsW(int nBufferLength, char[] lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    // ── CreateFile2 / 只读文件引用（用于进程归属探测）────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint DUPLICATE_SAME_ACCESS = 0x00000002;

    // ── 前台窗口 ─────────────────────────────────────────────────────────
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);

    // ── ntdll 句柄枚举（用于"哪个进程持有该文件"的尽力而为探测）─────────
    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryObject(IntPtr handle, int objectInformationClass, IntPtr objectInformation, int objectInformationLength, out int returnLength);

    public const int SystemExtendedHandleInformation = 64;
    public const int ObjectNameInformation = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    // ── 磁盘空间 ─────────────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
}
