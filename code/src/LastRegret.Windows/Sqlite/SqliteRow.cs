using System.Runtime.InteropServices;

namespace LastRegret.Windows.Sqlite;

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
