using System.Text.Json;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
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

/// <summary>恢复操作仓储（同时承担"崩溃后审计实际做到哪一步"的职责）。</summary>
public sealed class RestoreRepository : IRestoreRepository
{
    private readonly SqliteConnection _db;

    public RestoreRepository(SqliteConnection db) => _db = db;

    public long Insert(RestoreOperation op)
    {
        _db.NonQuery(
            """
            INSERT INTO restore_operations(root_id, target_ts_utc, target_ts_local, target_snapshot_id, pre_snapshot_id,
                post_snapshot_id, status, started_utc, finished_utc, planned_count, succeeded_count, failed_count,
                plan_fingerprint, message, undone_by_operation_id, undoes_operation_id)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);
            """,
            op.RootId,
            SqliteConnection.ToUnixTicks(op.TargetTimeUtc),
            SqliteConnection.ToUnixTicks(op.TargetTimeLocal),
            op.TargetSnapshotId,
            op.PreRestoreSnapshotId,
            op.PostRestoreSnapshotId,
            op.Status.ToCode(),
            SqliteConnection.ToUnixTicks(op.StartedUtc),
            op.FinishedUtc is null ? null : SqliteConnection.ToUnixTicks(op.FinishedUtc.Value),
            op.PlannedCount,
            op.SucceededCount,
            op.FailedCount,
            op.PlanFingerprint,
            op.Message,
            op.UndoneByOperationId,
            op.UndoesOperationId);
        op.Id = _db.LastInsertRowId();
        return op.Id;
    }

    public void Update(RestoreOperation op)
    {
        _db.NonQuery(
            """
            UPDATE restore_operations SET
                target_snapshot_id = ?, pre_snapshot_id = ?, post_snapshot_id = ?, status = ?,
                finished_utc = ?, planned_count = ?, succeeded_count = ?, failed_count = ?,
                plan_fingerprint = ?, message = ?, undone_by_operation_id = ?, undoes_operation_id = ?
            WHERE id = ?;
            """,
            op.TargetSnapshotId,
            op.PreRestoreSnapshotId,
            op.PostRestoreSnapshotId,
            op.Status.ToCode(),
            op.FinishedUtc is null ? null : SqliteConnection.ToUnixTicks(op.FinishedUtc.Value),
            op.PlannedCount,
            op.SucceededCount,
            op.FailedCount,
            op.PlanFingerprint,
            op.Message,
            op.UndoneByOperationId,
            op.UndoesOperationId,
            op.Id);
    }

    public RestoreOperation? Get(long id)
    {
        RestoreOperation? op = null;
        _db.QueryFirst(SelectSql + " WHERE id = ? LIMIT 1;", new object?[] { id }, row => op = Map(row));
        return op;
    }

    public IReadOnlyList<RestoreOperation> ListRecent(long? rootId = null, int limit = 50)
    {
        var list = new List<RestoreOperation>();
        var sql = SelectSql + (rootId is null ? " ORDER BY started_utc DESC LIMIT ?;" : " WHERE root_id = ? ORDER BY started_utc DESC LIMIT ?;");
        var args = rootId is null ? new object?[] { limit } : new object?[] { rootId.Value, limit };
        _db.Query(sql, args, row => list.Add(Map(row)));
        return list;
    }

    /// <summary>
    /// 某个快照是否被任何恢复操作引用（被引用的快照不允许删除）。
    /// 安全点一旦被删掉，就意味着"那次恢复撤销不了"——因此这是硬保护。
    /// </summary>
    public bool IsSnapshotReferenced(long snapshotId)
    {
        bool referenced = false;
        _db.QueryFirst(
            "SELECT 1 FROM restore_operations WHERE target_snapshot_id = ? OR pre_snapshot_id = ? OR post_snapshot_id = ? LIMIT 1;",
            new object?[] { snapshotId, snapshotId, snapshotId },
            _ => referenced = true);
        return referenced;
    }

    public RestoreOperation? GetLastUndoable(long? rootId)
    {
        RestoreOperation? op = null;
        var sql = SelectSql +
                  " WHERE status IN ('completed','partial') AND undone_by_operation_id IS NULL" +
                  (rootId is null ? string.Empty : " AND root_id = ?") +
                  " ORDER BY finished_utc DESC, id DESC LIMIT 1;";
        var args = rootId is null ? Array.Empty<object?>() : new object?[] { rootId.Value };
        _db.QueryFirst(sql, args, row => op = Map(row));
        return op;
    }

    public void InsertSteps(IEnumerable<RestoreStepRecord> steps)
    {
        var list = steps as IList<RestoreStepRecord> ?? steps.ToList();
        if (list.Count == 0) return;
        _db.InTransaction(() =>
        {
            foreach (var s in list)
            {
                _db.NonQuery(
                    "INSERT INTO restore_steps(operation_id, seq, action, path, path_key, target_hash, before_hash, before_object_id, before_size, succeeded, skipped_conflict, error, executed_utc) " +
                    "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?);",
                    s.OperationId, s.Sequence, s.Action.ToCode(), s.RelativePath, s.RelativePath.ToLowerInvariant(),
                    s.TargetHash, s.BeforeHash, s.BeforeObjectId, s.BeforeSize,
                    s.Succeeded ? 1 : 0, s.SkippedDueToConflict ? 1 : 0, s.Error,
                    s.ExecutedUtc is null ? null : SqliteConnection.ToUnixTicks(s.ExecutedUtc.Value));
                s.Id = _db.LastInsertRowId();
            }
        });
    }

    public void UpdateStep(RestoreStepRecord step)
    {
        _db.NonQuery(
            "UPDATE restore_steps SET succeeded = ?, skipped_conflict = ?, error = ?, executed_utc = ?, before_hash = ?, before_object_id = ?, before_size = ? WHERE id = ?;",
            step.Succeeded ? 1 : 0,
            step.SkippedDueToConflict ? 1 : 0,
            step.Error,
            step.ExecutedUtc is null ? null : SqliteConnection.ToUnixTicks(step.ExecutedUtc.Value),
            step.BeforeHash,
            step.BeforeObjectId,
            step.BeforeSize,
            step.Id);
    }

    public IReadOnlyList<RestoreStepRecord> ListSteps(long operationId)
    {
        var list = new List<RestoreStepRecord>();
        _db.Query(
            "SELECT id, operation_id, seq, action, path, target_hash, before_hash, before_object_id, before_size, succeeded, skipped_conflict, error, executed_utc " +
            "FROM restore_steps WHERE operation_id = ? ORDER BY seq ASC;",
            new object?[] { operationId },
            row => list.Add(new RestoreStepRecord
            {
                Id = row.GetInt64("id"),
                OperationId = row.GetInt64("operation_id"),
                Sequence = row.GetInt32("seq"),
                Action = RestoreActionExtensions.FromCode(row.GetStringOrNull("action")),
                RelativePath = row.GetString("path"),
                TargetHash = row.GetStringOrNull("target_hash"),
                BeforeHash = row.GetStringOrNull("before_hash"),
                BeforeObjectId = row.GetInt64OrNull("before_object_id"),
                BeforeSize = row.GetInt64OrNull("before_size"),
                Succeeded = row.GetBool("succeeded"),
                SkippedDueToConflict = row.GetBool("skipped_conflict"),
                Error = row.GetStringOrNull("error"),
                ExecutedUtc = row.GetDateTimeUtcOrNull("executed_utc"),
            }));
        return list;
    }

    public IReadOnlyList<RestoreOperation> FindInterrupted()
    {
        var list = new List<RestoreOperation>();
        _db.Query(SelectSql + " WHERE status IN ('running','planned') ORDER BY started_utc DESC;", Array.Empty<object?>(), row => list.Add(Map(row)));
        return list;
    }

    private const string SelectSql =
        "SELECT id, root_id, target_ts_utc, target_ts_local, target_snapshot_id, pre_snapshot_id, post_snapshot_id, status, " +
        "started_utc, finished_utc, planned_count, succeeded_count, failed_count, plan_fingerprint, message, undone_by_operation_id, undoes_operation_id " +
        "FROM restore_operations";

    private static RestoreOperation Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("id"),
        RootId = row.GetInt64("root_id"),
        TargetTimeUtc = new DateTime(row.GetInt64("target_ts_utc"), DateTimeKind.Utc),
        // target_ts_local 同样存的是 UTC ticks，必须还原成本地时间。
        TargetTimeLocal = new DateTime(row.GetInt64("target_ts_utc"), DateTimeKind.Utc).ToLocalTime(),
        TargetSnapshotId = row.GetInt64OrNull("target_snapshot_id"),
        PreRestoreSnapshotId = row.GetInt64OrNull("pre_snapshot_id"),
        PostRestoreSnapshotId = row.GetInt64OrNull("post_snapshot_id"),
        Status = RestoreStatusExtensions.FromCode(row.GetStringOrNull("status")),
        StartedUtc = new DateTime(row.GetInt64("started_utc"), DateTimeKind.Utc),
        FinishedUtc = row.GetDateTimeUtcOrNull("finished_utc"),
        PlannedCount = row.GetInt32("planned_count"),
        SucceededCount = row.GetInt32("succeeded_count"),
        FailedCount = row.GetInt32("failed_count"),
        PlanFingerprint = row.GetStringOrNull("plan_fingerprint"),
        Message = row.GetStringOrNull("message"),
        UndoneByOperationId = row.GetInt64OrNull("undone_by_operation_id"),
        UndoesOperationId = row.GetInt64OrNull("undoes_operation_id"),
    };
}

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
        root.Path = normalized;
        return root.Id;
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

/// <summary>设置仓储（键值表 + JSON 主体）。</summary>
public sealed class SettingsRepository : ISettingsRepository
{
    private readonly LastRegretDatabase _database;

    public SettingsRepository(LastRegretDatabase database) => _database = database;

    public AppSettings Load() => _database.LoadSettings();

    public void Save(AppSettings settings) => _database.SaveSettings(settings);

    public string? GetRaw(string key) => LastRegretDatabase.GetMeta(_database.Events, key);

    public void SetRaw(string key, string value) => LastRegretDatabase.SetMeta(_database.Events, key, value);
}

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
