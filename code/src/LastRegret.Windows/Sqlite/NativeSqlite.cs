using System.Runtime.InteropServices;
using System.Text;

namespace LastRegret.Windows.Sqlite;

/// <summary>
/// 系统 winsqlite3.dll 的原生绑定。
///
/// 为什么自研绑定而不是用 Microsoft.Data.Sqlite：
///  本机 NuGet 网络不可达（实测），而 Windows 10 1803+ 自带
///  <c>C:\Windows\System32\winsqlite3.dll</c>（本机版本 3.51.1）。
///  直接 P/Invoke 它可以做到**零第三方依赖**，且随系统更新维护。
///
/// 只暴露本项目真正需要的 API，避免表面积过大带来的维护与安全负担。
/// </summary>
internal static class NativeSqlite
{
    private const string Dll = "winsqlite3.dll";

    public const int OK = 0;
    public const int ROW = 100;
    public const int DONE = 101;
    public const int BUSY = 5;
    public const int LOCKED = 6;
    public const int CONSTRAINT = 19;
    public const int MISUSE = 21;
    public const int FULL = 13;
    public const int IOERR = 10;
    public const int CORRUPT = 11;
    public const int CANTOPEN = 14;
    public const int READONLY = 8;

    public const int SQLITE_OPEN_READONLY = 0x00000001;
    public const int SQLITE_OPEN_READWRITE = 0x00000002;
    public const int SQLITE_OPEN_CREATE = 0x00000004;
    public const int SQLITE_OPEN_URI = 0x00000040;
    public const int SQLITE_OPEN_NOMUTEX = 0x00008000;
    public const int SQLITE_OPEN_FULLMUTEX = 0x00010000;

    public const int SQLITE_DETERMINISTIC = 0x000000800;

    [DllImport(Dll, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Open(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Close(IntPtr db);

    [DllImport(Dll, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Prepare(IntPtr db, byte[] sql, int numBytes, out IntPtr stmt, out IntPtr tail);

    [DllImport(Dll, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Finalize(IntPtr stmt);

    [DllImport(Dll, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Step(IntPtr stmt);

    [DllImport(Dll, EntryPoint = "sqlite3_reset", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Reset(IntPtr stmt);

    [DllImport(Dll, EntryPoint = "sqlite3_clear_bindings", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ClearBindings(IntPtr stmt);

    [DllImport(Dll, EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ErrMsgRaw(IntPtr db);

    [DllImport(Dll, EntryPoint = "sqlite3_libversion", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr LibVersionRaw();

    [DllImport(Dll, EntryPoint = "sqlite3_extended_errcode", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ExtendedErrCode(IntPtr db);

    // ── 绑定 ──────────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BindInt64(IntPtr stmt, int index, long value);

    [DllImport(Dll, EntryPoint = "sqlite3_bind_double", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BindDouble(IntPtr stmt, int index, double value);

    [DllImport(Dll, EntryPoint = "sqlite3_bind_null", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BindNull(IntPtr stmt, int index);

    [DllImport(Dll, EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BindText(IntPtr stmt, int index, byte[] value, int numBytes, IntPtr destructor);

    [DllImport(Dll, EntryPoint = "sqlite3_bind_blob", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BindBlob(IntPtr stmt, int index, byte[] value, int numBytes, IntPtr destructor);

    [DllImport(Dll, EntryPoint = "sqlite3_bind_parameter_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BindParameterCount(IntPtr stmt);

    // ── 读取 ──────────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ColumnCount(IntPtr stmt);

    [DllImport(Dll, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ColumnType(IntPtr stmt, int index);

    [DllImport(Dll, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
    public static extern long ColumnInt64(IntPtr stmt, int index);

    [DllImport(Dll, EntryPoint = "sqlite3_column_double", CallingConvention = CallingConvention.Cdecl)]
    public static extern double ColumnDouble(IntPtr stmt, int index);

    [DllImport(Dll, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ColumnTextRaw(IntPtr stmt, int index);

    [DllImport(Dll, EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ColumnBlobRaw(IntPtr stmt, int index);

    [DllImport(Dll, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ColumnBytes(IntPtr stmt, int index);

    [DllImport(Dll, EntryPoint = "sqlite3_column_name", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ColumnNameRaw(IntPtr stmt, int index);

    // ── 其他 ──────────────────────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "sqlite3_changes", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Changes(IntPtr db);

    [DllImport(Dll, EntryPoint = "sqlite3_last_insert_rowid", CallingConvention = CallingConvention.Cdecl)]
    public static extern long LastInsertRowId(IntPtr db);

    [DllImport(Dll, EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BusyTimeout(IntPtr db, int ms);

    [DllImport(Dll, EntryPoint = "sqlite3_backup_init", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr BackupInit(IntPtr dest, byte[] destName, IntPtr source, byte[] sourceName);

    [DllImport(Dll, EntryPoint = "sqlite3_backup_step", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BackupStep(IntPtr backup, int pages);

    [DllImport(Dll, EntryPoint = "sqlite3_backup_finish", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BackupFinish(IntPtr backup);

    [DllImport(Dll, EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

    [DllImport(Dll, EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Free(IntPtr ptr);

    public static string Version()
    {
        var p = LibVersionRaw();
        return p == IntPtr.Zero ? "unknown" : Marshal.PtrToStringUTF8(p) ?? "unknown";
    }

    public static string ErrorMessage(IntPtr db)
    {
        if (db == IntPtr.Zero) return "(no db handle)";
        var p = ErrMsgRaw(db);
        return p == IntPtr.Zero ? "(unknown error)" : Marshal.PtrToStringUTF8(p) ?? "(unknown error)";
    }

    public static string? ColumnText(IntPtr stmt, int index)
    {
        var p = ColumnTextRaw(stmt, index);
        if (p == IntPtr.Zero) return null;
        int len = ColumnBytes(stmt, index);
        return len <= 0 ? string.Empty : Marshal.PtrToStringUTF8(p, len);
    }

    public static byte[]? ColumnBlob(IntPtr stmt, int index)
    {
        var p = ColumnBlobRaw(stmt, index);
        if (p == IntPtr.Zero) return null;
        int len = ColumnBytes(stmt, index);
        if (len <= 0) return Array.Empty<byte>();
        var buf = new byte[len];
        Marshal.Copy(p, buf, 0, len);
        return buf;
    }

    public static string ColumnName(IntPtr stmt, int index)
    {
        var p = ColumnNameRaw(stmt, index);
        return p == IntPtr.Zero ? $"col{index}" : Marshal.PtrToStringUTF8(p) ?? $"col{index}";
    }

    /// <summary>UTF-8 编码（SQLite 默认按 UTF-8 处理文本）。</summary>
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>带结尾 NUL 的 UTF-8（用于文件名等以 NUL 结尾的参数）。</summary>
    public static byte[] Utf8Z(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var z = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, z, 0, bytes.Length);
        return z;
    }
}
