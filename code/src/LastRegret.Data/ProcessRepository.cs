using LastRegret.Core.Model;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

/// <summary>关联进程仓储（如实记录"附近出现过什么进程"，不做因果推断）。</summary>
public sealed class ProcessRepository
{
    private readonly SqliteConnection _db;

    public ProcessRepository(SqliteConnection db) => _db = db;

    public void UpsertRange(IEnumerable<ProcessRecord> records)
    {
        var list = records as IList<ProcessRecord> ?? records.ToList();
        if (list.Count == 0) return;
        _db.InTransaction(() =>
        {
            foreach (var p in list)
            {
                _db.NonQuery(
                    """
                    INSERT INTO processes(pid, name, exe_path, start_utc, last_seen_utc, had_foreground, window_title)
                    VALUES (?,?,?,?,?,?,?)
                    ON CONFLICT(pid) DO UPDATE SET
                        name           = excluded.name,
                        exe_path       = COALESCE(excluded.exe_path, processes.exe_path),
                        start_utc      = COALESCE(excluded.start_utc, processes.start_utc),
                        last_seen_utc  = excluded.last_seen_utc,
                        had_foreground = MAX(excluded.had_foreground, processes.had_foreground),
                        window_title   = COALESCE(excluded.window_title, processes.window_title);
                    """,
                    p.Pid, p.ProcessName, p.ExecutablePath,
                    p.StartTimeUtc is null ? null : SqliteConnection.ToUnixTicks(p.StartTimeUtc.Value),
                    SqliteConnection.ToUnixTicks(p.LastSeenUtc),
                    p.HadForegroundWindow ? 1 : 0,
                    p.WindowTitle);
            }
        });
    }

    public IReadOnlyList<ProcessRecord> ListRecent(int limit = 100)
    {
        var list = new List<ProcessRecord>();
        _db.Query(
            "SELECT pid, name, exe_path, start_utc, last_seen_utc, had_foreground, window_title FROM processes ORDER BY last_seen_utc DESC LIMIT ?;",
            new object?[] { limit },
            row => list.Add(new ProcessRecord
            {
                Pid = row.GetInt32("pid"),
                ProcessName = row.GetString("name"),
                ExecutablePath = row.GetStringOrNull("exe_path"),
                StartTimeUtc = row.GetDateTimeUtcOrNull("start_utc"),
                LastSeenUtc = new DateTime(row.GetInt64("last_seen_utc"), DateTimeKind.Utc),
                HadForegroundWindow = row.GetBool("had_foreground"),
                WindowTitle = row.GetStringOrNull("window_title"),
            }));
        return list;
    }

    /// <summary>清理长期未见的进程记录（它们已经不可能解释新的事件）。</summary>
    public int Prune(DateTime olderThanUtc) =>
        _db.NonQuery("DELETE FROM processes WHERE last_seen_utc < ?;", SqliteConnection.ToUnixTicks(olderThanUtc));
}
