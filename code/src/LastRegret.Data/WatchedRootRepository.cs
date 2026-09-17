using System.Text.Json;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

/// <summary>受保护目录仓储。</summary>
public sealed class WatchedRootRepository : IWatchedRootRepository
{
    private readonly SqliteConnection _db;

    public WatchedRootRepository(SqliteConnection db) => _db = db;

    public IReadOnlyList<WatchedRoot> ListAll()
    {
        var list = new List<WatchedRoot>();
        _db.Query(SelectSql + " ORDER BY id;", Array.Empty<object?>(), row => list.Add(Map(row)));
        return list;
    }

    public IReadOnlyList<WatchedRoot> ListEnabled()
    {
        var list = new List<WatchedRoot>();
        _db.Query(SelectSql + " WHERE enabled = 1 ORDER BY id;", Array.Empty<object?>(), row => list.Add(Map(row)));
        return list;
    }

    public WatchedRoot? Get(long id)
    {
        WatchedRoot? root = null;
        _db.QueryFirst(SelectSql + " WHERE id = ? LIMIT 1;", new object?[] { id }, row => root = Map(row));
        return root;
    }

    public WatchedRoot? FindByPath(string absolutePath)
    {
        var key = PathUtil.NormalizeRoot(absolutePath).ToLowerInvariant();
        WatchedRoot? root = null;
        _db.QueryFirst(SelectSql + " WHERE path_key = ? LIMIT 1;", new object?[] { key }, row => root = Map(row));
        return root;
    }

    public long Insert(WatchedRoot root)
    {
        var normalized = PathUtil.NormalizeRoot(root.Path);
        // ⚠ BB-005：INSERT 与取回自增 Id 必须原子（同一连接上别的线程可能正在插入）。
        _db.InTransaction(() =>
        {
            InsertCore(root, normalized);
        });
        root.Path = normalized;
        return root.Id;
    }

    private void InsertCore(WatchedRoot root, string normalized)
    {
        _db.NonQuery(
            "INSERT INTO watched_roots(path, path_key, label, enabled, include_subdirs, created_utc, last_event_utc, baseline_snapshot_id, excludes_json, max_file_size_bytes, scan_state) " +
            "VALUES (?,?,?,?,?,?,?,?,?,?,?);",
            normalized,
            normalized.ToLowerInvariant(),
            root.Label,
            root.Enabled ? 1 : 0,
            root.IncludeSubdirectories ? 1 : 0,
            SqliteConnection.ToUnixTicks(root.CreatedUtc == default ? DateTime.UtcNow : root.CreatedUtc),
            root.LastEventUtc is null ? null : SqliteConnection.ToUnixTicks(root.LastEventUtc.Value),
            root.BaselineSnapshotId,
            JsonSerializer.Serialize(root.Excludes),
            root.MaxFileSizeBytes,
            "idle");
        root.Id = _db.LastInsertRowId();
    }

    public void Update(WatchedRoot root)
    {
        var normalized = PathUtil.NormalizeRoot(root.Path);
        _db.NonQuery(
            "UPDATE watched_roots SET path = ?, path_key = ?, label = ?, enabled = ?, include_subdirs = ?, " +
            "last_event_utc = ?, baseline_snapshot_id = ?, excludes_json = ?, max_file_size_bytes = ? WHERE id = ?;",
            normalized,
            normalized.ToLowerInvariant(),
            root.Label,
            root.Enabled ? 1 : 0,
            root.IncludeSubdirectories ? 1 : 0,
            root.LastEventUtc is null ? null : SqliteConnection.ToUnixTicks(root.LastEventUtc.Value),
            root.BaselineSnapshotId,
            JsonSerializer.Serialize(root.Excludes),
            root.MaxFileSizeBytes,
            root.Id);
    }

    public void Delete(long id) => _db.NonQuery("DELETE FROM watched_roots WHERE id = ?;", id);

    public void SetEnabled(long id, bool enabled) =>
        _db.NonQuery("UPDATE watched_roots SET enabled = ? WHERE id = ?;", enabled ? 1 : 0, id);

    public void SetBaselineSnapshot(long rootId, long snapshotId) =>
        _db.NonQuery("UPDATE watched_roots SET baseline_snapshot_id = ? WHERE id = ?;", snapshotId, rootId);

    public void SetScanState(long rootId, string state) =>
        _db.NonQuery("UPDATE watched_roots SET scan_state = ? WHERE id = ?;", state, rootId);

    public void TouchLastEvent(long rootId, DateTime atUtc) =>
        _db.NonQuery("UPDATE watched_roots SET last_event_utc = ? WHERE id = ? AND (last_event_utc IS NULL OR last_event_utc < ?);",
            SqliteConnection.ToUnixTicks(atUtc), rootId, SqliteConnection.ToUnixTicks(atUtc));

    private const string SelectSql =
        "SELECT id, path, label, enabled, include_subdirs, created_utc, last_event_utc, baseline_snapshot_id, excludes_json, max_file_size_bytes " +
        "FROM watched_roots";

    private static WatchedRoot Map(SqliteRow row)
    {
        var excludes = new List<string>();
        var json = row.GetStringOrNull("excludes_json");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                excludes = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (JsonException)
            {
                excludes = new List<string>();
            }
        }

        return new WatchedRoot
        {
            Id = row.GetInt64("id"),
            Path = row.GetString("path"),
            Label = row.GetStringOrNull("label"),
            Enabled = row.GetBool("enabled"),
            IncludeSubdirectories = row.GetBool("include_subdirs"),
            CreatedUtc = new DateTime(row.GetInt64("created_utc"), DateTimeKind.Utc),
            LastEventUtc = row.GetDateTimeUtcOrNull("last_event_utc"),
            BaselineSnapshotId = row.GetInt64OrNull("baseline_snapshot_id"),
            Excludes = excludes,
            MaxFileSizeBytes = row.GetInt64("max_file_size_bytes"),
        };
    }
}
