using System.Runtime.InteropServices;
using System.Text;

namespace LastRegret.Windows.Sqlite;

/// <summary>SQLite 错误。</summary>
public sealed class SqliteException : Exception
{
    public int ResultCode { get; }

    public int ExtendedCode { get; }

    public SqliteException(string message, int resultCode, int extendedCode = 0)
        : base(message)
    {
        ResultCode = resultCode;
        ExtendedCode = extendedCode;
    }
}

/// <summary>
/// SQLite 查询读取器。
/// 采用回调式而非 <c>IEnumerable</c>：语句句柄在回调期间有效，回调结束后立即复位，
/// 避免"忘记释放 / 边读边写同一连接"导致的锁问题。
/// </summary>
public sealed class SqliteRow
{
    private readonly IntPtr _stmt;
    private readonly int _columnCount;
    private string[]? _names;

    internal SqliteRow(IntPtr stmt)
    {
        _stmt = stmt;
        _columnCount = NativeSqlite.ColumnCount(stmt);
    }

    public int FieldCount => _columnCount;

    private string[] Names
    {
        get
        {
            if (_names is null)
            {
                _names = new string[_columnCount];
                for (int i = 0; i < _columnCount; i++) _names[i] = NativeSqlite.ColumnName(_stmt, i);
            }
            return _names;
        }
    }

    /// <summary>按序号取列名（主要用于调试与列顺序变化检测）。</summary>
    public string NameAt(int i) => Names[i];

    public int IndexOf(string name)
    {
        for (int i = 0; i < _columnCount; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public bool IsNull(int i) => NativeSqlite.ColumnType(_stmt, i) == 5; // SQLITE_NULL

    public long GetInt64(int i)
    {
        if (IsNull(i)) throw new InvalidOperationException($"列 {NameAt(i)} 为 NULL，不能当作整数读取");
        return NativeSqlite.ColumnInt64(_stmt, i);
    }

    public long? GetInt64OrNull(int i) => IsNull(i) ? null : NativeSqlite.ColumnInt64(_stmt, i);

    public int GetInt32(int i) => (int)GetInt64(i);

    public int? GetInt32OrNull(int i) => IsNull(i) ? null : (int)NativeSqlite.ColumnInt64(_stmt, i);

    public double GetDouble(int i) => NativeSqlite.ColumnDouble(_stmt, i);

    public string GetString(int i) => NativeSqlite.ColumnText(_stmt, i) ?? string.Empty;

    public string? GetStringOrNull(int i) => IsNull(i) ? null : NativeSqlite.ColumnText(_stmt, i);

    public byte[]? GetBlob(int i) => NativeSqlite.ColumnBlob(_stmt, i);

    public bool GetBool(int i) => GetInt64(i) != 0;

    public bool? GetBoolOrNull(int i) => IsNull(i) ? null : GetInt64(i) != 0;

    public DateTime GetDateTimeUtc(int i) => new DateTime(GetInt64(i), DateTimeKind.Utc);

    public DateTime? GetDateTimeUtcOrNull(int i) => IsNull(i) ? null : new DateTime(GetInt64(i), DateTimeKind.Utc);

    /// <summary>按列名取值（列缺失抛异常，避免默默读到错误的列）。</summary>
    public long GetInt64(string name) => GetInt64(Require(name));

    public long? GetInt64OrNull(string name) => GetInt64OrNull(Require(name));

    public int GetInt32(string name) => GetInt32(Require(name));

    public int? GetInt32OrNull(string name) => GetInt32OrNull(Require(name));

    public double GetDouble(string name) => GetDouble(Require(name));

    public string GetString(string name) => GetString(Require(name));

    public string? GetStringOrNull(string name) => GetStringOrNull(Require(name));

    public byte[]? GetBlob(string name) => GetBlob(Require(name));

    public DateTime GetDateTimeUtc(string name) => GetDateTimeUtc(Require(name));

    public DateTime? GetDateTimeUtcOrNull(string name) => GetDateTimeUtcOrNull(Require(name));

    public bool GetBool(string name) => GetBool(Require(name));

    public bool? GetBoolOrNull(string name) => GetBoolOrNull(Require(name));

    /// <summary>列是否为 NULL（按列名）。</summary>
    public bool IsNull(string name) => IsNull(Require(name));

    private int Require(string name)
    {
        var i = IndexOf(name);
        if (i < 0) throw new InvalidOperationException($"查询结果中不存在列 '{name}'（实际列：{string.Join(", ", Names)}）");
        return i;
    }
}

/// <summary>
/// 托管的 SQLite 连接。
///
/// 线程模型：<b>单连接不并发</b>。所有公开操作都在内部锁内完成。
/// 本项目的事件写入与查询都是短事务（毫秒级），单连接串行足够，
/// 也彻底避免了"多写者 → SQLITE_BUSY"这一类难调的问题。
/// </summary>
public sealed class SqliteConnection : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _db;
    private readonly string _path;
    private readonly Dictionary<string, IntPtr> _stmtCache = new(StringComparer.Ordinal);
    private bool _disposed;
    private int _busyRetries = 8;

    public string Path => _path;

    public string LibraryVersion { get; } = NativeSqlite.Version();

    private SqliteConnection(IntPtr db, string path)
    {
        _db = db;
        _path = path;
    }

    /// <summary>打开（或创建）数据库。</summary>
    /// <param name="path">物理路径。</param>
    /// <param name="readOnly">只读打开。</param>
    /// <param name="applyPragmas">是否应用本项目推荐的 PRAGMA（事务安全优先）。</param>
    public static SqliteConnection Open(string path, bool readOnly = false, bool applyPragmas = true)
    {
        var flags = NativeSqlite.SQLITE_OPEN_URI | NativeSqlite.SQLITE_OPEN_FULLMUTEX;
        flags |= readOnly ? NativeSqlite.SQLITE_OPEN_READONLY : NativeSqlite.SQLITE_OPEN_READWRITE | NativeSqlite.SQLITE_OPEN_CREATE;

        var rc = NativeSqlite.Open(NativeSqlite.Utf8Z(path), out var db, flags, IntPtr.Zero);
        if (rc != NativeSqlite.OK || db == IntPtr.Zero)
        {
            throw new SqliteException($"无法打开数据库 {path}：{NativeSqlite.ErrorMessage(db)} (rc={rc})", rc);
        }

        var conn = new SqliteConnection(db, path);
        NativeSqlite.BusyTimeout(db, 5000);
        if (applyPragmas && !readOnly)
        {
            conn.ApplyDefaultPragmas();
        }
        else if (applyPragmas)
        {
            // 只读连接只要 busy_timeout（已在上面设置）
        }
        return conn;
    }

    /// <summary>
    /// 崩溃安全相关的 PRAGMA 组合。
    ///
    /// 权衡说明（必须写清楚，避免后人误改）：
    ///  - journal_mode=WAL：崩溃后可用 WAL 恢复，读写不互相阻塞；
    ///  - synchronous=FULL：每次事务提交都 fsync。对"历史数据库"来说
    ///    宁可慢一点也**绝不能**出现"事务已提交但断电后丢失/损坏"；
    ///  - foreign_keys=ON：保证引用完整性；
    ///  - wal_autocheckpoint 保守设置，避免 WAL 无限增长。
    /// </summary>
    private void ApplyDefaultPragmas()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("PRAGMA foreign_keys=ON;");
        Execute("PRAGMA temp_store=MEMORY;");
        Execute("PRAGMA cache_size=-16000;");       // ≈16MB
        Execute("PRAGMA wal_autocheckpoint=1000;");
        Execute("PRAGMA busy_timeout=5000;");
    }

    public void Execute(string sql, params object?[] args) => NonQuery(sql, args);

    /// <summary>
    /// 执行**多条** SQL 语句组成的脚本（DDL / 迁移脚本）。
    ///
    /// 为什么必须单独有这个方法（踩坑记录）：
    ///  <c>sqlite3_prepare_v2</c> 一次只编译**第一条**语句，
    ///  剩余部分通过 tail 指针返回。早期实现忽略 tail、只用 sqlite3_exec 的思路，
    ///  结果整个 Schema（十多个 CREATE TABLE）只建出了第一张表，
    ///  程序随后到处报 "no such table"，而且报错点离真正的原因非常远。
    ///
    /// 本实现循环编译执行，直到 SQL 文本被消耗完。参数只对第一条语句生效（脚本里不需要参数）。
    /// </summary>
    public int ExecuteBatch(string sql, params object?[] args)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            int totalChanges = 0;
            var remaining = Encoding.UTF8.GetBytes(sql);
            int offset = 0;

            while (offset < remaining.Length)
            {
                // 跳过空白与空语句
                while (offset < remaining.Length && (remaining[offset] == (byte)' ' || remaining[offset] == (byte)'\n' ||
                                                      remaining[offset] == (byte)'\r' || remaining[offset] == (byte)'\t' ||
                                                      remaining[offset] == (byte)';'))
                {
                    offset++;
                }
                if (offset >= remaining.Length) break;

                var tail = new byte[remaining.Length - offset + 1];
                Array.Copy(remaining, offset, tail, 0, remaining.Length - offset);

                var rc = NativeSqlite.Prepare(_db, tail, tail.Length - 1, out var stmt, out var tailPtr);
                if (rc != NativeSqlite.OK || stmt == IntPtr.Zero)
                {
                    var preview = Encoding.UTF8.GetString(remaining, offset, Math.Min(160, remaining.Length - offset));
                    throw new SqliteException(
                        $"{NativeSqlite.ErrorMessage(_db)} (rc={rc})\n语句片段：{preview}\nDB: {_path}", rc, NativeSqlite.ExtendedErrCode(_db));
                }

                IntPtr[] pins = Array.Empty<IntPtr>();
                try
                {
                    if (offset == 0 && args.Length > 0) BindAll(stmt, args, out pins);
                    int stepRc = StepToCompletion(stmt);
                    if (stepRc != NativeSqlite.DONE && stepRc != NativeSqlite.ROW)
                    {
                        var preview = Encoding.UTF8.GetString(remaining, offset, Math.Min(160, remaining.Length - offset));
                        throw new SqliteException(
                            $"{NativeSqlite.ErrorMessage(_db)} (rc={stepRc})\n语句片段：{preview}\nDB: {_path}", stepRc);
                    }
                    totalChanges += NativeSqlite.Changes(_db);
                }
                finally
                {
                    FreePins(pins);
                    NativeSqlite.Finalize(stmt);
                }

                if (tailPtr == IntPtr.Zero) break;

                // tail 指向"已编译部分的末尾"：换算回原缓冲区的偏移。
                // 必须自己 pin 住数组，否则取到的地址在 GC 压缩后可能失效。
                int consumed;
                unsafe
                {
                    fixed (byte* p = tail)
                    {
                        consumed = (int)((long)tailPtr - (long)p);
                    }
                }
                if (consumed <= 0 || consumed > tail.Length) break;
                offset += consumed;
            }

            return totalChanges;
        }
    }

    /// <summary>执行非查询语句，返回受影响行数。</summary>
    public int NonQuery(string sql, params object?[] args)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var stmt = GetStatement(sql);
            IntPtr[] pins = Array.Empty<IntPtr>();
            try
            {
                BindAll(stmt, args, out pins);
                int rc = StepToCompletion(stmt);
                if (rc != NativeSqlite.DONE && rc != NativeSqlite.ROW)
                    throw Error(rc, sql);
                return NativeSqlite.Changes(_db);
            }
            finally
            {
                FreePins(pins);
                NativeSqlite.Reset(stmt);
                NativeSqlite.ClearBindings(stmt);
            }
        }
    }

    /// <summary>执行查询并逐行回调。回调抛出 <see cref="StopIteration"/> 可提前结束（用于只取首行）。</summary>
    public int Query(string sql, object?[] args, Action<SqliteRow> onRow)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var stmt = GetStatement(sql);
            IntPtr[] pins = Array.Empty<IntPtr>();
            int rows = 0;
            var row = new SqliteRow(stmt);
            try
            {
                BindAll(stmt, args, out pins);
                while (true)
                {
                    int rc = NativeSqlite.Step(stmt);
                    if (rc == NativeSqlite.ROW)
                    {
                        try
                        {
                            onRow(row);
                        }
                        catch (StopIteration)
                        {
                            break;
                        }
                        rows++;
                        continue;
                    }
                    if (rc == NativeSqlite.DONE) break;
                    if (rc == NativeSqlite.BUSY || rc == NativeSqlite.LOCKED)
                    {
                        if (RetryBusy(stmt)) continue;
                    }
                    throw Error(rc, sql);
                }
                return rows;
            }
            finally
            {
                FreePins(pins);
                NativeSqlite.Reset(stmt);
                NativeSqlite.ClearBindings(stmt);
            }
        }
    }

    /// <summary>提前结束查询的内部信号（仅供 <see cref="QueryFirst"/> 使用）。</summary>
    internal sealed class StopIteration : Exception
    {
    }

    /// <summary>查询首行；无结果返回 false。只读取一行，不做全表扫描。</summary>
    public bool QueryFirst(string sql, object?[] args, Action<SqliteRow> onRow)
    {
        bool any = false;
        Query(sql, args, row =>
        {
            any = true;
            onRow(row);
            throw new StopIteration();
        });
        return any;
    }

    public T? Scalar<T>(string sql, params object?[] args)
    {
        T? result = default;
        bool any = false;
        Query(sql, args, row =>
        {
            if (any) return;
            any = true;
            if (row.IsNull(0)) return;
            var t = typeof(T);
            object value = t switch
            {
                _ when t == typeof(long) => row.GetInt64(0),
                _ when t == typeof(int) => row.GetInt32(0),
                _ when t == typeof(string) => row.GetString(0),
                _ when t == typeof(double) => row.GetDouble(0),
                _ when t == typeof(bool) => row.GetBool(0),
                _ when t == typeof(DateTime) => row.GetDateTimeUtc(0),
                _ => throw new NotSupportedException($"不支持的类型 {t.Name}"),
            };
            result = (T)value;
        });
        return result;
    }

    public long LastInsertRowId() => NativeSqlite.LastInsertRowId(_db);

    /// <summary>在事务中执行（失败自动回滚；提交失败会重试）。</summary>
    public void InTransaction(Action action) => InTransaction<object?>(() =>
    {
        action();
        return null;
    });

    public T InTransaction<T>(Func<T> action)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            bool ownTransaction = !InTransactionInternal;
            if (ownTransaction) Execute("BEGIN IMMEDIATE;");
            InTransactionInternal = true;
            try
            {
                var result = action();
                if (ownTransaction)
                {
                    CommitWithRetry();
                    InTransactionInternal = false;
                }
                return result;
            }
            catch
            {
                if (ownTransaction)
                {
                    try { Execute("ROLLBACK;"); }
                    catch (SqliteException) { /* 回滚失败：原始异常更重要 */ }
                    InTransactionInternal = false;
                }
                throw;
            }
        }
    }

    /// <summary>当前是否已处于事务内（嵌套调用时由外层负责提交）。</summary>
    public bool InTransactionInternal { get; private set; }

    private void CommitWithRetry()
    {
        const int maxAttempts = 6;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Execute("COMMIT;");
                return;
            }
            catch (SqliteException ex) when ((ex.ResultCode == NativeSqlite.BUSY || ex.ResultCode == NativeSqlite.LOCKED) && attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 语句缓存与绑定
    // ─────────────────────────────────────────────────────────────────────

    private IntPtr GetStatement(string sql)
    {
        if (_stmtCache.TryGetValue(sql, out var cached)) return cached;

        var rc = NativeSqlite.Prepare(_db, NativeSqlite.Utf8Z(sql), -1, out var stmt, out _);
        if (rc != NativeSqlite.OK || stmt == IntPtr.Zero)
            throw Error(rc, sql);

        // 语句数量有限（本项目 SQL 都是代码内常量），缓存不会无限增长
        _stmtCache[sql] = stmt;
        return stmt;
    }

    private static void BindAll(IntPtr stmt, object?[] args, out IntPtr[] pins)
    {
        pins = Array.Empty<IntPtr>();
        if (args.Length == 0) return;

        int expected = NativeSqlite.BindParameterCount(stmt);
        if (expected != args.Length)
        {
            throw new SqliteException(
                $"参数个数不匹配：SQL 需要 {expected} 个，实际传入 {args.Length} 个。", NativeSqlite.MISUSE);
        }

        var pinned = new List<GCHandle>(args.Length);
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                int idx = i + 1;
                var value = args[i];
                int rc;
                if (value is null || value is DBNull)
                {
                    rc = NativeSqlite.BindNull(stmt, idx);
                }
                else
                {
                    switch (value)
                    {
                        case string s:
                        {
                            var bytes = Encoding.UTF8.GetBytes(s);
                            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                            pinned.Add(handle);
                            rc = NativeSqlite.BindText(stmt, idx, bytes, bytes.Length, new IntPtr(-1)); // SQLITE_TRANSIENT
                            break;
                        }
                        case byte[] blob:
                        {
                            var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
                            pinned.Add(handle);
                            rc = NativeSqlite.BindBlob(stmt, idx, blob, blob.Length, new IntPtr(-1));
                            break;
                        }
                        case bool b:
                            rc = NativeSqlite.BindInt64(stmt, idx, b ? 1 : 0);
                            break;
                        case int i32:
                            rc = NativeSqlite.BindInt64(stmt, idx, i32);
                            break;
                        case long i64:
                            rc = NativeSqlite.BindInt64(stmt, idx, i64);
                            break;
                        case short i16:
                            rc = NativeSqlite.BindInt64(stmt, idx, i16);
                            break;
                        case byte u8:
                            rc = NativeSqlite.BindInt64(stmt, idx, u8);
                            break;
                        case double d:
                            rc = NativeSqlite.BindDouble(stmt, idx, d);
                            break;
                        case float f:
                            rc = NativeSqlite.BindDouble(stmt, idx, f);
                            break;
                        case DateTime dt:
                            rc = NativeSqlite.BindInt64(stmt, idx, ToUnixTicks(dt));
                            break;
                        case Enum e:
                            rc = NativeSqlite.BindInt64(stmt, idx, Convert.ToInt64(e));
                            break;
                        default:
                            throw new SqliteException($"不支持的参数类型：{value.GetType().FullName}", NativeSqlite.MISUSE);
                    }
                }

                if (rc != NativeSqlite.OK)
                    throw new SqliteException($"绑定第 {idx} 个参数失败（rc={rc}）", rc);
            }
            pins = pinned.Select(GCHandle.ToIntPtr).ToArray();
        }
        catch
        {
            foreach (var h in pinned) if (h.IsAllocated) h.Free();
            throw;
        }
    }

    private static void FreePins(IntPtr[] pins)
    {
        foreach (var p in pins)
        {
            if (p != IntPtr.Zero) GCHandle.FromIntPtr(p).Free();
        }
    }

    /// <summary>时间统一以"UTC ticks"存整数：可排序、可索引、无时区歧义。</summary>
    public static long ToUnixTicks(DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
        return utc.Ticks;
    }

    private bool RetryBusy(IntPtr stmt)
    {
        if (_busyRetries <= 0) return false;
        NativeSqlite.Reset(stmt);
        Thread.Sleep(25);
        return true;
    }

    private int StepToCompletion(IntPtr stmt)
    {
        int attempts = 0;
        while (true)
        {
            int rc = NativeSqlite.Step(stmt);
            if (rc == NativeSqlite.BUSY || rc == NativeSqlite.LOCKED)
            {
                if (++attempts > _busyRetries) return rc;
                NativeSqlite.Reset(stmt);
                Thread.Sleep(25 * attempts);
                continue;
            }
            return rc;
        }
    }

    private SqliteException Error(int rc, string sql)
    {
        var msg = NativeSqlite.ErrorMessage(_db);
        var ext = NativeSqlite.ExtendedErrCode(_db);
        var preview = sql.Length > 300 ? sql[..300] + "…" : sql;
        return new SqliteException($"{msg} (rc={rc}, ext={ext})\nSQL: {preview}\nDB: {_path}", rc, ext);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SqliteConnection));
    }

    /// <summary>强制 WAL 落盘（备份或升级前调用）。</summary>
    public void Checkpoint()
    {
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>完整性检查。返回 null 表示完好。</summary>
    public string? IntegrityCheck()
    {
        string? problem = null;
        Query("PRAGMA integrity_check;", Array.Empty<object?>(), row =>
        {
            var s = row.GetString(0);
            if (!string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase) && problem is null) problem = s;
        });
        return problem;
    }

    /// <summary>外键检查。返回问题描述列表（空 = 好）。</summary>
    public IReadOnlyList<string> ForeignKeyCheck()
    {
        var list = new List<string>();
        Query("PRAGMA foreign_key_check;", Array.Empty<object?>(), row =>
        {
            var parts = new List<string>(row.FieldCount);
            for (int i = 0; i < row.FieldCount; i++) parts.Add(row.IsNull(i) ? "null" : row.GetString(i));
            list.Add(string.Join(" | ", parts));
        });
        return list;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var stmt in _stmtCache.Values)
            {
                if (stmt != IntPtr.Zero) NativeSqlite.Finalize(stmt);
            }
            _stmtCache.Clear();
            if (_db != IntPtr.Zero)
            {
                var rc = NativeSqlite.Close(_db);
                _db = IntPtr.Zero;
                if (rc != NativeSqlite.OK)
                {
                    // 关闭失败不应掩盖业务逻辑；记录到调试输出即可
                    System.Diagnostics.Debug.WriteLine($"sqlite3_close_v2 rc={rc}");
                }
            }
        }
    }
}
