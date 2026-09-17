using System.Text.Json;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

/// <summary>事件仓储：唯一的事实来源。所有写入都在事务内完成。</summary>
public sealed class EventRepository : IEventSink
{
    private const string InsertSql = """
INSERT INTO events(
    root_id, ts_utc, ts_local, op, kind, path, path_key, old_path, old_path_key,
    size_before, size_after, hash_before, hash_after, mtime_before_utc, mtime_after_utc,
    object_before, object_after, merge_count, suppressed_count, is_coalesced, is_transient,
    is_restore_induced, source, confidence, pid, process_name, attribution_json,
    affected_descendants, note, last_ts_utc)
VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);
""";

    private const string SelectColumns = """
SELECT id, root_id, ts_utc, ts_local, op, kind, path, old_path, size_before, size_after,
       hash_before, hash_after, mtime_before_utc, mtime_after_utc, object_before, object_after,
       merge_count, suppressed_count, is_coalesced, is_transient, is_restore_induced, source,
       confidence, pid, process_name, attribution_json, affected_descendants, note, last_ts_utc
FROM events
""";

    private readonly SqliteConnection _db;

    public EventRepository(SqliteConnection db) => _db = db;

    public void AppendRange(IReadOnlyList<FileEvent> events)
    {
        if (events.Count == 0) return;

        _db.InTransaction(() =>
        {
            foreach (var e in events)
            {
                _db.NonQuery(InsertSql, ToArgs(e));
                e.Id = _db.LastInsertRowId();
            }
        });
    }

    public long Append(FileEvent e)
    {
        // ⚠ BB-005：INSERT 与"取回自增 Id"必须原子。
        // 两步之间若被别的线程插入了一行，LastInsertRowId 会读到**别人的** Id
        // （例如监听线程正在写事件、而主线程在读回恢复记录 Id）。
        // 批量路径 AppendRange 本来就在事务里，这里对齐同一纪律。
        _db.InTransaction(() =>
        {
            _db.NonQuery(InsertSql, ToArgs(e));
            e.Id = _db.LastInsertRowId();
        });
        return e.Id;
    }

    private static object?[] ToArgs(FileEvent e) => new object?[]
    {
        e.RootId,
        SqliteConnection.ToUnixTicks(e.TimestampUtc),
        SqliteConnection.ToUnixTicks(e.TimestampLocal),
        e.Operation.ToCode(),
        e.Kind.ToCode(),
        e.RelativePath,
        e.RelativePath.ToLowerInvariant(),
        e.OldRelativePath,
        e.OldRelativePath?.ToLowerInvariant(),
        e.SizeBefore,
        e.SizeAfter,
        e.HashBefore,
        e.HashAfter,
        e.MtimeBeforeUtc is null ? null : SqliteConnection.ToUnixTicks(e.MtimeBeforeUtc.Value),
        e.MtimeAfterUtc is null ? null : SqliteConnection.ToUnixTicks(e.MtimeAfterUtc.Value),
        e.ObjectIdBefore,
        e.ObjectIdAfter,
        e.MergeCount,
        e.SuppressedCount,
        e.IsCoalesced ? 1 : 0,
        e.IsTransient ? 1 : 0,
        e.IsRestoreInduced ? 1 : 0,
        e.Source,
        (int)e.Confidence,
        e.AttributedPid,
        e.AttributedProcess,
        e.Attribution is null ? null : JsonSerializer.Serialize(e.Attribution, AttributionJsonOpts),
        e.AffectedDescendantCount,
        e.Note,
        SqliteConnection.ToUnixTicks(e.TimestampUtc),
    };

    private static readonly JsonSerializerOptions AttributionJsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public IReadOnlyList<FileEvent> Query(EventQuery query)
    {
        var (where, args) = BuildWhere(query);
        var order = query.Descending ? "DESC" : "ASC";
        var sql = $"{SelectColumns} WHERE {where} ORDER BY ts_utc {order}, id {order} LIMIT ? OFFSET ?;";
        var allArgs = new List<object?>(args) { query.Limit, query.Offset };

        var list = new List<FileEvent>(Math.Min(query.Limit, 1024));
        _db.Query(sql, allArgs.ToArray(), row => list.Add(Map(row)));
        return list;
    }

    public long Count(EventQuery query)
    {
        var (where, args) = BuildWhere(query);
        long count = 0;
        _db.QueryFirst($"SELECT COUNT(*) FROM events WHERE {where};", args, row => count = row.GetInt64(0));
        return count;
    }

    public long GetHighWatermark(long rootId, DateTime atUtc)
    {
        long id = 0;
        _db.QueryFirst(
            "SELECT COALESCE(MAX(id), 0) FROM events WHERE root_id = ? AND ts_utc <= ?;",
            new object?[] { rootId, SqliteConnection.ToUnixTicks(atUtc) },
            row => id = row.GetInt64(0));
        return id;
    }

    /// <summary>
    /// 删除历史变化记录。
    ///
    /// ⚠ 这是**不可撤销**的：删掉的事件再也回不来（恢复点本身不受影响）。
    /// 所以调用方必须让用户明确确认，并把"删了多少条"如实告诉用户。
    /// 返回实际删除的条数。
    /// </summary>
    public long DeleteAll(long? rootId = null)
    {
        if (rootId is null)
        {
            var before = CountAll();
            _db.Execute("DELETE FROM events;");
            return before;
        }
        var count = Count(new EventQuery { RootId = rootId.Value });
        _db.Execute("DELETE FROM events WHERE root_id = ?;", new object?[] { rootId.Value });
        return count;
    }

    /// <summary>删除某条历史记录（用户在列表里点名要删的那一条）。</summary>
    public bool DeleteById(long eventId)
    {
        var before = CountAll();
        _db.Execute("DELETE FROM events WHERE id = ?;", new object?[] { eventId });
        return CountAll() < before;
    }

    private long CountAll()
    {
        long n = 0;
        _db.QueryFirst("SELECT COUNT(*) FROM events;", Array.Empty<object?>(), r => n = r.GetInt64(0));
        return n;
    }

    /// <summary>统计信息（首页/设置页展示）。</summary>
    public (long Total, long Transient, long Last24h) GetStatistics(long? rootId = null)
    {
        long total = 0, transient = 0, last24 = 0;
        var rootClause = rootId is null ? string.Empty : " AND root_id = ?";
        var args = rootId is null ? Array.Empty<object?>() : new object?[] { rootId.Value };

        _db.QueryFirst($"SELECT COUNT(*) FROM events WHERE 1=1{rootClause};", args, r => total = r.GetInt64(0));
        _db.QueryFirst($"SELECT COUNT(*) FROM events WHERE is_transient = 1{rootClause};", args, r => transient = r.GetInt64(0));
        _db.QueryFirst(
            $"SELECT COUNT(*) FROM events WHERE ts_utc >= ?{rootClause};",
            rootId is null
                ? new object?[] { SqliteConnection.ToUnixTicks(DateTime.UtcNow.AddHours(-24)) }
                : new object?[] { SqliteConnection.ToUnixTicks(DateTime.UtcNow.AddHours(-24)), rootId.Value },
            r => last24 = r.GetInt64(0));

        return (total, transient, last24);
    }

    /// <summary>取某路径的全部历史事件（文件详情页）。</summary>
    public IReadOnlyList<FileEvent> QueryForPath(long rootId, string relativePath, bool includeTransient, int limit = 200)
    {
        var list = new List<FileEvent>();
        _db.Query(
            $"{SelectColumns} WHERE root_id = ? AND (path_key = ? OR old_path_key = ?) " +
            (includeTransient ? string.Empty : " AND is_transient = 0 ") +
            "ORDER BY ts_utc DESC, id DESC LIMIT ?;",
            new object?[] { rootId, relativePath.ToLowerInvariant(), relativePath.ToLowerInvariant(), limit },
            row => list.Add(Map(row)));
        return list;
    }

    /// <summary>取某个事件之后（不含）的全部事件，用于快照增量构建。</summary>
    public IReadOnlyList<FileEvent> QueryAfterId(long rootId, long afterEventId, int limit = 100_000)
    {
        var list = new List<FileEvent>();
        _db.Query(
            $"{SelectColumns} WHERE root_id = ? AND id > ? ORDER BY id ASC LIMIT ?;",
            new object?[] { rootId, afterEventId, limit },
            row => list.Add(Map(row)));
        return list;
    }

    /// <summary>取时间窗口内的事件（用于重命名证据收集）。</summary>
    public IReadOnlyList<FileEvent> QueryRenameEvidence(long rootId, DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<FileEvent>();
        _db.Query(
            $"{SelectColumns} WHERE root_id = ? AND ts_utc >= ? AND ts_utc <= ? AND op IN ('renamed','moved') ORDER BY ts_utc ASC;",
            new object?[]
            {
                rootId,
                SqliteConnection.ToUnixTicks(fromUtc),
                SqliteConnection.ToUnixTicks(toUtc),
            },
            row => list.Add(Map(row)));
        return list;
    }

    /// <summary>
    /// 自某个事件 Id 之后**发生过变化**的路径（含旧路径）。
    /// 用于快照的增量构建：只需要把这些路径的当前状态写进新清单，
    /// 其余行由 SQL 从父快照整体复制。
    /// </summary>
    public IReadOnlyList<string> QueryChangedPathsSince(long rootId, long afterEventId, int limit = 500_000)
    {
        var paths = new HashSet<string>(PathUtil.Comparer);
        _db.Query(
            "SELECT path, old_path FROM events WHERE root_id = ? AND id > ? AND op <> 'transient' LIMIT ?;",
            new object?[] { rootId, afterEventId, limit },
            row =>
            {
                paths.Add(row.GetString("path"));
                var oldPath = row.GetStringOrNull("old_path");
                if (!string.IsNullOrEmpty(oldPath)) paths.Add(oldPath);
            });
        return paths.ToList();
    }

    private static (string Where, object?[] Args) BuildWhere(EventQuery q)
    {
        var clauses = new List<string> { "1=1" };
        var args = new List<object?>();

        if (q.RootId is not null)
        {
            clauses.Add("root_id = ?");
            args.Add(q.RootId.Value);
        }
        if (q.FromUtc is not null)
        {
            clauses.Add("ts_utc >= ?");
            args.Add(SqliteConnection.ToUnixTicks(q.FromUtc.Value));
        }
        if (q.ToUtc is not null)
        {
            clauses.Add("ts_utc <= ?");
            args.Add(SqliteConnection.ToUnixTicks(q.ToUtc.Value));
        }
        if (q.RelativePath is not null)
        {
            clauses.Add("path_key = ?");
            args.Add(q.RelativePath.ToLowerInvariant());
        }
        if (q.PathPrefix is not null)
        {
            var prefix = PathUtil.NormalizeRelative(q.PathPrefix).ToLowerInvariant();
            if (prefix.Length > 0)
            {
                clauses.Add("(path_key = ? OR path_key LIKE ? ESCAPE '\\')");
                args.Add(prefix);
                args.Add(EscapeLike(prefix) + "/%");
            }
        }
        if (q.Operations is { Length: > 0 })
        {
            var marks = string.Join(",", q.Operations.Select(_ => "?"));
            clauses.Add($"op IN ({marks})");
            foreach (var op in q.Operations) args.Add(op.ToCode());
        }
        if (!q.IncludeTransient) clauses.Add("is_transient = 0");
        if (!q.IncludeRestoreInduced) clauses.Add("is_restore_induced = 0");

        return (string.Join(" AND ", clauses), args.ToArray());
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    internal static FileEvent Map(SqliteRow row)
    {
        var e = new FileEvent
        {
            Id = row.GetInt64("id"),
            RootId = row.GetInt64("root_id"),
            TimestampUtc = new DateTime(row.GetInt64("ts_utc"), DateTimeKind.Utc),
            // ts_local 与 ts_utc 存的是同一个 UTC ticks（见 SqliteConnection.ToUnixTicks）。
            // 读取时必须由 UTC 还原成本地时间，否则界面会把 UTC 当本地时间显示，
            // 用户看到的时刻会与真实发生时刻差一个时区。
            TimestampLocal = new DateTime(row.GetInt64("ts_utc"), DateTimeKind.Utc).ToLocalTime(),
            Operation = OperationTypeExtensions.FromCode(row.GetStringOrNull("op")),
            Kind = EntryKindExtensions.FromCode(row.GetStringOrNull("kind")),
            RelativePath = row.GetString("path"),
            OldRelativePath = row.GetStringOrNull("old_path"),
            SizeBefore = row.GetInt64OrNull("size_before"),
            SizeAfter = row.GetInt64OrNull("size_after"),
            HashBefore = row.GetStringOrNull("hash_before"),
            HashAfter = row.GetStringOrNull("hash_after"),
            ObjectIdBefore = row.GetInt64OrNull("object_before"),
            ObjectIdAfter = row.GetInt64OrNull("object_after"),
            MergeCount = row.GetInt32("merge_count"),
            SuppressedCount = row.GetInt32("suppressed_count"),
            IsCoalesced = row.GetBool("is_coalesced"),
            IsTransient = row.GetBool("is_transient"),
            IsRestoreInduced = row.GetBool("is_restore_induced"),
            Source = row.GetString("source"),
            Confidence = (AttributionConfidence)row.GetInt32("confidence"),
            AttributedPid = row.GetInt32OrNull("pid"),
            AttributedProcess = row.GetStringOrNull("process_name"),
            AffectedDescendantCount = row.GetInt32("affected_descendants"),
            Note = row.GetStringOrNull("note"),
        };

        e.MtimeBeforeUtc = row.GetDateTimeUtcOrNull("mtime_before_utc");
        e.MtimeAfterUtc = row.GetDateTimeUtcOrNull("mtime_after_utc");

        var json = row.GetStringOrNull("attribution_json");
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                e.Attribution = JsonSerializer.Deserialize<ProcessAttribution>(json);
            }
            catch (JsonException)
            {
                e.Attribution = null; // 归属信息解析失败不影响事件本身的有效性
            }
        }

        return e;
    }
}
