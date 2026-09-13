using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

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
