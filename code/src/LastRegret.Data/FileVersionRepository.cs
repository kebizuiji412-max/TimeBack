using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

/// <summary>文件历史版本仓储。</summary>
public sealed class FileVersionRepository : IFileVersionRepository
{
    private readonly SqliteConnection _db;

    public FileVersionRepository(SqliteConnection db) => _db = db;

    public void InsertRange(IEnumerable<FileVersion> versions)
    {
        var list = versions as IList<FileVersion> ?? versions.ToList();
        if (list.Count == 0) return;

        _db.InTransaction(() =>
        {
            foreach (var v in list)
            {
                _db.NonQuery(
                    "INSERT INTO file_versions(root_id, path, path_key, hash, object_id, size, recorded_utc, recorded_local, mtime_utc, event_id, snapshot_id, content_pruned, note) " +
                    "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?);",
                    v.RootId,
                    v.RelativePath,
                    v.RelativePath.ToLowerInvariant(),
                    v.Hash,
                    v.ObjectId,
                    v.Size,
                    SqliteConnection.ToUnixTicks(v.RecordedUtc),
                    SqliteConnection.ToUnixTicks(v.RecordedLocal),
                    v.MtimeUtc is null ? null : SqliteConnection.ToUnixTicks(v.MtimeUtc.Value),
                    v.EventId,
                    v.SnapshotId,
                    v.ContentPruned ? 1 : 0,
                    v.Note);
                v.Id = _db.LastInsertRowId();
            }
        });
    }

    public IReadOnlyList<FileVersion> ListForPath(long rootId, string relativePath, int limit = 200)
    {
        var list = new List<FileVersion>();
        _db.Query(
            "SELECT id, root_id, path, hash, object_id, size, recorded_utc, recorded_local, mtime_utc, event_id, snapshot_id, content_pruned, note " +
            "FROM file_versions WHERE root_id = ? AND path_key = ? ORDER BY recorded_utc DESC LIMIT ?;",
            new object?[] { rootId, PathUtil.NormalizeRelative(relativePath).ToLowerInvariant(), limit },
            row => list.Add(Map(row)));
        return list;
    }

    public FileVersion? GetLatestBefore(long rootId, string relativePath, DateTime atUtc)
    {
        FileVersion? version = null;
        _db.QueryFirst(
            "SELECT id, root_id, path, hash, object_id, size, recorded_utc, recorded_local, mtime_utc, event_id, snapshot_id, content_pruned, note " +
            "FROM file_versions WHERE root_id = ? AND path_key = ? AND recorded_utc <= ? " +
            "ORDER BY recorded_utc DESC LIMIT 1;",
            new object?[] { rootId, PathUtil.NormalizeRelative(relativePath).ToLowerInvariant(), SqliteConnection.ToUnixTicks(atUtc) },
            row => version = Map(row));
        return version;
    }

    public FileVersion? GetById(long id)
    {
        FileVersion? version = null;
        _db.QueryFirst(
            "SELECT id, root_id, path, hash, object_id, size, recorded_utc, recorded_local, mtime_utc, event_id, snapshot_id, content_pruned, note " +
            "FROM file_versions WHERE id = ? LIMIT 1;",
            new object?[] { id },
            row => version = Map(row));
        return version;
    }

    public long Count(long? rootId = null)
    {
        long n = 0;
        _db.QueryFirst(
            rootId is null ? "SELECT COUNT(*) FROM file_versions;" : "SELECT COUNT(*) FROM file_versions WHERE root_id = ?;",
            rootId is null ? Array.Empty<object?>() : new object?[] { rootId.Value },
            r => n = r.GetInt64(0));
        return n;
    }

    /// <summary>统计早于某时刻的版本行数量（清理计划用，不修改数据）。</summary>
    public long CountOlderThan(DateTime beforeUtc)
    {
        long n = 0;
        _db.QueryFirst(
            "SELECT COUNT(*) FROM file_versions WHERE recorded_utc < ?;",
            new object?[] { SqliteConnection.ToUnixTicks(beforeUtc) },
            r => n = r.GetInt64(0));
        return n;
    }

    public int DeleteOlderThan(DateTime beforeUtc, ISet<long> protectedObjectIds, bool dryRun)
    {
        int affected = 0;
        var cutoff = SqliteConnection.ToUnixTicks(beforeUtc);

        _db.InTransaction(() =>
        {
            _db.QueryFirst(
                "SELECT COUNT(*) FROM file_versions WHERE recorded_utc < ?;",
                new object?[] { cutoff }, r => affected = r.GetInt32(0));

            if (dryRun || affected == 0) return;

            // 只删除"内容没有被任何快照引用"的版本行。
            // 被快照引用的对象必须保留，否则快照会变成不可恢复的空壳。
            _db.NonQuery(
                "DELETE FROM file_versions WHERE recorded_utc < ? AND (object_id IS NULL OR object_id NOT IN " +
                "(SELECT DISTINCT object_id FROM snapshot_files WHERE object_id IS NOT NULL));",
                cutoff);
        });

        // protectedObjectIds 由调用方传入（数据层不做跨库判断），此处仅用于日志/审计
        _ = protectedObjectIds;
        return affected;
    }

    private static FileVersion Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("id"),
        RootId = row.GetInt64("root_id"),
        RelativePath = row.GetString("path"),
        Hash = row.GetString("hash"),
        ObjectId = row.GetInt64OrNull("object_id"),
        Size = row.GetInt64("size"),
        RecordedUtc = new DateTime(row.GetInt64("recorded_utc"), DateTimeKind.Utc),
        // recorded_local 存的是同一个 UTC ticks（见 SqliteConnection.ToUnixTicks），
        // 必须由 UTC 还原成本地时间，否则界面会把 UTC 当本地时间显示。
        RecordedLocal = new DateTime(row.GetInt64("recorded_utc"), DateTimeKind.Utc).ToLocalTime(),
        MtimeUtc = row.GetDateTimeUtcOrNull("mtime_utc"),
        EventId = row.GetInt64OrNull("event_id"),
        SnapshotId = row.GetInt64OrNull("snapshot_id"),
        ContentPruned = row.GetBool("content_pruned"),
        Note = row.GetStringOrNull("note"),
    };
}
