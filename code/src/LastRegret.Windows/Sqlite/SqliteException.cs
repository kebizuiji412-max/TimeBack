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
