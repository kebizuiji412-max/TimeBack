using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

/// <summary>
/// 路径索引仓储：受保护范围"当前状态"的权威来源。
///
/// 与事件日志的分工：
///  - events 记录"发生过什么"（历史事实）；
///  - files 记录"现在是什么"（当前状态，可随时由事件重放或全量重扫重建）。
/// 因此 files 表损坏也不会丢历史，只是需要一次重扫对齐。
/// </summary>
public sealed class FileIndexRepository : IFileIndex
{
    private readonly SqliteConnection _db;

    public FileIndexRepository(SqliteConnection db) => _db = db;

    public IndexEntry? Get(long rootId, string relativePath)
    {
        var key = PathUtil.NormalizeRelative(relativePath).ToLowerInvariant();
        IndexEntry? entry = null;
        _db.QueryFirst(
            "SELECT root_id, path, kind, size, hash, object_id, mtime_utc, first_seen_utc, last_changed_utc, last_event_id, is_deleted, is_read_only " +
            "FROM files WHERE root_id = ? AND path_key = ? LIMIT 1;",
            new object?[] { rootId, key },
            row => entry = Map(row));
        return entry;
    }

    public void Upsert(long rootId, IndexEntry entry)
    {
        _db.NonQuery(
            """
            INSERT INTO files(root_id, path, path_key, kind, size, hash, object_id, mtime_utc,
                              first_seen_utc, last_changed_utc, last_event_id, is_deleted, is_read_only)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(root_id, path_key) DO UPDATE SET
                path            = excluded.path,
                kind            = excluded.kind,
                size            = excluded.size,
                hash            = excluded.hash,
                object_id       = excluded.object_id,
                mtime_utc       = excluded.mtime_utc,
                last_changed_utc= excluded.last_changed_utc,
                last_event_id   = excluded.last_event_id,
                is_deleted      = excluded.is_deleted,
                is_read_only    = excluded.is_read_only;
            """,
            rootId,
            entry.RelativePath,
            entry.RelativePath.ToLowerInvariant(),
            entry.Kind.ToCode(),
            entry.Size,
            entry.Hash,
            entry.ObjectId,
            entry.MtimeUtc is null ? null : SqliteConnection.ToUnixTicks(entry.MtimeUtc.Value),
            SqliteConnection.ToUnixTicks(entry.FirstSeenUtc),
            SqliteConnection.ToUnixTicks(entry.LastChangedUtc),
            entry.LastEventId,
            entry.IsDeleted ? 1 : 0,
            entry.IsReadOnly ? 1 : 0);
        entry.RootId = rootId;
    }

    public void UpsertMany(long rootId, IReadOnlyList<IndexEntry> entries)
    {
        if (entries.Count == 0) return;
        _db.InTransaction(() =>
        {
            foreach (var e in entries) Upsert(rootId, e);
        });
    }

    public void MarkDeleted(long rootId, string relativePath, DateTime atUtc)
    {
        var key = PathUtil.NormalizeRelative(relativePath).ToLowerInvariant();
        _db.NonQuery(
            "UPDATE files SET is_deleted = 1, last_changed_utc = ?, object_id = NULL, size = 0 WHERE root_id = ? AND path_key = ?;",
            SqliteConnection.ToUnixTicks(atUtc), rootId, key);
    }

    public int MoveSubtree(long rootId, string oldPrefix, string newPrefix, DateTime atUtc)
    {
        var oldRel = PathUtil.NormalizeRelative(oldPrefix);
        var newRel = PathUtil.NormalizeRelative(newPrefix);
        if (PathUtil.Comparer.Equals(oldRel, newRel)) return 0;

        var oldKey = oldRel.ToLowerInvariant();
        var newKey = newRel.ToLowerInvariant();

        int affected = 0;
        _db.InTransaction(() =>
        {
            // 含自身：path = 旧前缀 或 path 以 "旧前缀/" 开头
            var where = "(root_id = ? AND (path_key = ? OR path_key LIKE ? ESCAPE '\\'))";
            var args = new object?[] { rootId, oldKey, LikePrefix(oldKey) };

            // 先统计
            _db.QueryFirst($"SELECT COUNT(*) FROM files WHERE {where};", args, r => affected = r.GetInt32(0));
            if (affected == 0) return;

            // 直接做前缀替换更新（避免逐行读出再写回）。
            // 两种情形分开写清楚，不依赖 SUBSTR 的长度运算（那种写法极易差一位）：
            //   path_key == 旧前缀      → 直接改成新前缀
            //   path_key LIKE 旧前缀/%  → 新前缀 + 去掉"旧前缀/"之后的剩余部分
            // 比较必须用 path_key（path 已被本条语句改写），SUBSTR 是 1 基偏移。
            var likePattern = LikePrefix(oldKey);            // "前缀/%"
            var substrOffset = oldKey.Length + 2;            // 跳过 "旧前缀/"

            _db.NonQuery(
                "UPDATE files SET " +
                "  path      = CASE WHEN path_key = ? THEN ? ELSE ? || SUBSTR(path, ?) END, " +
                "  path_key  = CASE WHEN path_key = ? THEN ? ELSE ? || SUBSTR(path_key, ?) END, " +
                "  last_changed_utc = ? " +
                $"WHERE {where};",
                oldKey, newRel, newRel, substrOffset,
                oldKey, newKey, newKey, substrOffset,
                SqliteConnection.ToUnixTicks(atUtc),
                rootId, oldKey, likePattern);
        });
        return affected;
    }

    public int MarkSubtreeDeleted(long rootId, string prefix, DateTime atUtc)
    {
        var rel = PathUtil.NormalizeRelative(prefix);
        var key = rel.ToLowerInvariant();
        int affected = 0;
        _db.InTransaction(() =>
        {
            var where = "(root_id = ? AND (path_key = ? OR path_key LIKE ? ESCAPE '\\'))";
            var args = new object?[] { rootId, key, LikePrefix(key) };
            _db.QueryFirst($"SELECT COUNT(*) FROM files WHERE {where};", args, r => affected = r.GetInt32(0));
            _db.NonQuery(
                $"UPDATE files SET is_deleted = 1, last_changed_utc = ?, object_id = NULL, size = 0 WHERE {where};",
                SqliteConnection.ToUnixTicks(atUtc), rootId, key, LikePrefix(key));
        });
        return affected;
    }

    public IReadOnlyList<IndexEntry> ListAll(long rootId)
    {
        var list = new List<IndexEntry>();
        _db.Query(
            "SELECT root_id, path, kind, size, hash, object_id, mtime_utc, first_seen_utc, last_changed_utc, last_event_id, is_deleted, is_read_only " +
            "FROM files WHERE root_id = ? AND is_deleted = 0;",
            new object?[] { rootId },
            row => list.Add(Map(row)));
        return list;
    }

    public IReadOnlyList<IndexEntry> ListLive(long rootId) => ListAll(rootId);

    public long CountAll(long rootId)
    {
        long n = 0;
        _db.QueryFirst("SELECT COUNT(*) FROM files WHERE root_id = ? AND is_deleted = 0;", new object?[] { rootId }, r => n = r.GetInt64(0));
        return n;
    }

    /// <summary>列出某个路径前缀下的全部存活条目（用于统计目录操作影响面）。</summary>
    public IReadOnlyList<IndexEntry> ListUnder(long rootId, string prefix)
    {
        var key = PathUtil.NormalizeRelative(prefix).ToLowerInvariant();
        if (key.Length == 0) return ListAll(rootId);

        var list = new List<IndexEntry>();
        _db.Query(
            "SELECT root_id, path, kind, size, hash, object_id, mtime_utc, first_seen_utc, last_changed_utc, last_event_id, is_deleted, is_read_only " +
            "FROM files WHERE root_id = ? AND is_deleted = 0 AND (path_key = ? OR path_key LIKE ? ESCAPE '\\');",
            new object?[] { rootId, key, LikePrefix(key) },
            row => list.Add(Map(row)));
        return list;
    }

    /// <summary>最大的 last_event_id（用于对齐快照水位）。</summary>
    public long GetMaxEventId(long rootId)
    {
        long id = 0;
        _db.QueryFirst(
            "SELECT COALESCE(MAX(COALESCE(last_event_id, 0)), 0) FROM files WHERE root_id = ?;",
            new object?[] { rootId }, r => id = r.GetInt64(0));
        return id;
    }

    /// <summary>清空某个根的索引（重扫前调用）。</summary>
    public void ClearRoot(long rootId) =>
        _db.NonQuery("DELETE FROM files WHERE root_id = ?;", rootId);

    private static string LikePrefix(string lowerKey) =>
        lowerKey.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "/%";

    private static IndexEntry Map(SqliteRow row) => new()
    {
        RootId = row.GetInt64("root_id"),
        RelativePath = row.GetString("path"),
        Kind = Core.Model.EntryKindExtensions.FromCode(row.GetStringOrNull("kind")),
        Size = row.GetInt64("size"),
        Hash = row.GetStringOrNull("hash"),
        ObjectId = row.GetInt64OrNull("object_id"),
        MtimeUtc = row.GetDateTimeUtcOrNull("mtime_utc"),
        FirstSeenUtc = new DateTime(row.GetInt64("first_seen_utc"), DateTimeKind.Utc),
        LastChangedUtc = new DateTime(row.GetInt64("last_changed_utc"), DateTimeKind.Utc),
        LastEventId = row.GetInt64OrNull("last_event_id"),
        IsDeleted = row.GetBool("is_deleted"),
        IsReadOnly = row.GetBool("is_read_only"),
    };
}
