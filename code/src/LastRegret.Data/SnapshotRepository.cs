using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

/// <summary>
/// 快照仓储。
///
/// 增量策略（对应"禁止每次变化都完整复制整个文件夹"）：
///  1. 全量写入（基线/重新对齐）使用**临时表批量装载**再一次性插入，
///     避免逐行语句调用开销；
///  2. 增量快照只写入"新增/变更/删除"的行：
///     <c>INSERT INTO snapshot_files SELECT ... FROM parent WHERE ...</c>
///     由 SQLite 自己完成清单复制（不经过托管层，速度快且原子）；
///  3. **快照始终是完整清单**：所以它不依赖事件日志即可恢复；
///     而磁盘占用的增量只体现在"与父快照相比多出的那几行"。
///  4. 内容本身永不在快照里重复：只存 (path, hash, object_id) 引用，
///     真实字节由 CAS 全局去重。
/// </summary>
public sealed class SnapshotRepository : ISnapshotRepository
{
    private const string InsertSnapshotSql = """
INSERT INTO snapshots(root_id, ts_utc, ts_local, kind, parent_id, event_high_watermark,
                      file_count, dir_count, total_bytes, is_complete, note)
VALUES (?,?,?,?,?,?,?,?,?,?,?);
""";

    private readonly SqliteConnection _db;
    private readonly LastRegretDatabase _database;

    public SnapshotRepository(LastRegretDatabase database)
    {
        _database = database;
        _db = database.Events;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 写入
    // ─────────────────────────────────────────────────────────────────────

    public long Insert(Snapshot snapshot, IEnumerable<SnapshotFile> files)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _database.EnsureTempTables();

        // ⚠ 踩坑记录（PIT）：这里必须**先把清单装进临时表**再调 CopyStageInto。
        //    早期实现漏掉了装载步骤，导致 CopyStageInto 从一个空的 _manifest_stage
        //    里复制，于是"全量快照"永远是空清单（快照存在、文件数 0）。
        //    讽刺的是正因如此才有机会发现它：恢复预览会立刻表现为"什么都能恢复"。
        var list = files as IList<SnapshotFile> ?? files.ToList();

        long id = 0;
        _db.InTransaction(() =>
        {
            id = InsertSnapshotRow(snapshot);

            _db.NonQuery("DELETE FROM _manifest_stage;");
            foreach (var f in list) StageOne(f);

            CopyStageInto(id);
            UpdateCounters(id);
        });

        snapshot.Id = id;
        ApplyCounters(snapshot);
        return id;
    }

    public long InsertIncremental(Snapshot snapshot, IEnumerable<SnapshotFile> upserts, IEnumerable<string> removedPaths)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _database.EnsureTempTables();

        var upsertList = upserts.ToList();
        var removeList = removedPaths.ToList();

        long id = 0;
        _db.InTransaction(() =>
        {
            // 父快照缺失时退化为全量写入（绝不产生"不完整的快照"）
            if (snapshot.ParentId is null)
            {
                id = InsertSnapshotRow(snapshot);
                _db.NonQuery("DELETE FROM _manifest_stage;");
                CopyStageInto(id);
                UpdateCounters(id);
                return;
            }

            long parentId = snapshot.ParentId.Value;
            long parentCount = 0;
            _db.QueryFirst("SELECT COUNT(*) FROM snapshot_files WHERE snapshot_id = ?;", new object?[] { parentId }, r => parentCount = r.GetInt64(0));
            if (parentCount == 0)
            {
                id = InsertSnapshotRow(snapshot);
                UpdateCounters(id);
                return;
            }

            id = InsertSnapshotRow(snapshot);

            // 1) 从父快照整体复制清单（SQL 内部完成，速度快）
            _db.NonQuery(
                "INSERT INTO snapshot_files(snapshot_id, path, path_key, kind, size, hash, object_id, mtime_utc, first_seen_utc, is_read_only) " +
                "SELECT ?, path, path_key, kind, size, hash, object_id, mtime_utc, first_seen_utc, is_read_only " +
                "FROM snapshot_files WHERE snapshot_id = ?;",
                id, parentId);

            // 2) 删除已消失的路径
            //    ⚠ 踩坑记录（PIT）：临时表 _manifest_stage 的 kind 等列声明为 NOT NULL，
            //    而这里只需要"路径"这一列。曾试图只插入 (path, path_key)，
            //    结果触发 NOT NULL 约束失败，整条增量快照链路直接崩掉。
            //    现在只插入 path 并显式给出 kind，过滤时按 path 列比较。
            if (removeList.Count > 0)
            {
                _db.NonQuery("DELETE FROM _manifest_stage;");
                foreach (var path in removeList)
                {
                    _db.NonQuery(
                        "INSERT INTO _manifest_stage(path, path_key, kind, size) VALUES (?,?,'file',0);",
                        path, path.ToLowerInvariant());
                }
                _db.NonQuery(
                    "DELETE FROM snapshot_files WHERE snapshot_id = ? AND path IN (SELECT path FROM _manifest_stage);",
                    id);
            }

            // 3) 变更/新增的路径
            if (upsertList.Count > 0)
            {
                _db.NonQuery("DELETE FROM _manifest_stage;");
                foreach (var f in upsertList) StageOne(f);
                CopyStageInto(id);
            }

            UpdateCounters(id);
        });

        snapshot.Id = id;
        ApplyCounters(snapshot);
        return id;
    }

    private long InsertSnapshotRow(Snapshot snapshot)
    {
        _db.NonQuery(
            InsertSnapshotSql,
            snapshot.RootId,
            SqliteConnection.ToUnixTicks(snapshot.TimestampUtc),
            SqliteConnection.ToUnixTicks(snapshot.TimestampLocal),
            snapshot.Kind.ToCode(),
            snapshot.ParentId,
            snapshot.EventHighWatermark,
            snapshot.FileCount,
            snapshot.DirectoryCount,
            snapshot.TotalBytes,
            snapshot.IsComplete ? 1 : 0,
            snapshot.Note);
        return _db.LastInsertRowId();
    }

    private void StageOne(SnapshotFile f)
    {
        _db.NonQuery(
            "INSERT INTO _manifest_stage(path, path_key, kind, size, hash, object_id, mtime_utc, first_seen_utc, is_read_only) " +
            "VALUES (?,?,?,?,?,?,?,?,?);",
            f.RelativePath,
            f.RelativePath.ToLowerInvariant(),
            f.Kind.ToCode(),
            f.Size,
            f.Hash,
            f.ObjectId,
            f.MtimeUtc is null ? null : SqliteConnection.ToUnixTicks(f.MtimeUtc.Value),
            f.FirstSeenUtc is null ? null : SqliteConnection.ToUnixTicks(f.FirstSeenUtc.Value),
            f.IsReadOnly ? 1 : 0);
    }

    private void CopyStageInto(long snapshotId)
    {
        _db.NonQuery(
            "INSERT OR REPLACE INTO snapshot_files(snapshot_id, path, path_key, kind, size, hash, object_id, mtime_utc, first_seen_utc, is_read_only) " +
            "SELECT ?, path, path_key, kind, size, hash, object_id, mtime_utc, first_seen_utc, is_read_only FROM _manifest_stage;",
            snapshotId);
    }

    private void UpdateCounters(long snapshotId)
    {
        _db.NonQuery(
            """
            UPDATE snapshots SET
                file_count  = (SELECT COUNT(*) FROM snapshot_files WHERE snapshot_id = ? AND kind = 'file'),
                dir_count   = (SELECT COUNT(*) FROM snapshot_files WHERE snapshot_id = ? AND kind = 'dir'),
                total_bytes = (SELECT COALESCE(SUM(size),0) FROM snapshot_files WHERE snapshot_id = ? AND kind = 'file')
            WHERE id = ?;
            """,
            snapshotId, snapshotId, snapshotId, snapshotId);
    }

    private void ApplyCounters(Snapshot snapshot)
    {
        _db.QueryFirst(
            "SELECT file_count, dir_count, total_bytes FROM snapshots WHERE id = ?;",
            new object?[] { snapshot.Id },
            row =>
            {
                snapshot.FileCount = row.GetInt32("file_count");
                snapshot.DirectoryCount = row.GetInt32("dir_count");
                snapshot.TotalBytes = row.GetInt64("total_bytes");
            });
    }

    // ─────────────────────────────────────────────────────────────────────
    // 读取
    // ─────────────────────────────────────────────────────────────────────

    public Snapshot? Get(long id)
    {
        Snapshot? snapshot = null;
        _db.QueryFirst(
            "SELECT id, root_id, ts_utc, ts_local, kind, parent_id, event_high_watermark, file_count, dir_count, total_bytes, is_complete, note " +
            "FROM snapshots WHERE id = ? LIMIT 1;",
            new object?[] { id },
            row => snapshot = Map(row));
        return snapshot;
    }

    public Snapshot? GetLatest(long rootId, SnapshotKind? kind = null)
    {
        Snapshot? snapshot = null;
        var sql = "SELECT id, root_id, ts_utc, ts_local, kind, parent_id, event_high_watermark, file_count, dir_count, total_bytes, is_complete, note " +
                  "FROM snapshots WHERE root_id = ?" +
                  (kind is null ? string.Empty : " AND kind = ?") +
                  " ORDER BY ts_utc DESC, id DESC LIMIT 1;";
        var args = kind is null ? new object?[] { rootId } : new object?[] { rootId, kind.Value.ToCode() };
        _db.QueryFirst(sql, args, row => snapshot = Map(row));
        return snapshot;
    }

    public Snapshot? GetLatestAtOrBefore(long rootId, DateTime atUtc)
    {
        Snapshot? snapshot = null;
        _db.QueryFirst(
            "SELECT id, root_id, ts_utc, ts_local, kind, parent_id, event_high_watermark, file_count, dir_count, total_bytes, is_complete, note " +
            "FROM snapshots WHERE root_id = ? AND ts_utc <= ? ORDER BY ts_utc DESC, id DESC LIMIT 1;",
            new object?[] { rootId, SqliteConnection.ToUnixTicks(atUtc) },
            row => snapshot = Map(row));
        return snapshot;
    }

    /// <summary>取"最近一个 != 指定 Id"的快照（用于撤销恢复时找安全点）。</summary>
    public Snapshot? GetLatestExcluding(long rootId, long excludeId)
    {
        Snapshot? snapshot = null;
        _db.QueryFirst(
            "SELECT id, root_id, ts_utc, ts_local, kind, parent_id, event_high_watermark, file_count, dir_count, total_bytes, is_complete, note " +
            "FROM snapshots WHERE root_id = ? AND id <> ? ORDER BY ts_utc DESC, id DESC LIMIT 1;",
            new object?[] { rootId, excludeId },
            row => snapshot = Map(row));
        return snapshot;
    }

    public IReadOnlyList<Snapshot> List(long rootId, int limit = 200)
    {
        var list = new List<Snapshot>();
        _db.Query(
            "SELECT id, root_id, ts_utc, ts_local, kind, parent_id, event_high_watermark, file_count, dir_count, total_bytes, is_complete, note " +
            "FROM snapshots WHERE root_id = ? ORDER BY ts_utc DESC, id DESC LIMIT ?;",
            new object?[] { rootId, limit },
            row => list.Add(Map(row)));
        return list;
    }

    public IReadOnlyList<SnapshotFile> LoadFiles(long snapshotId)
    {
        var list = new List<SnapshotFile>();
        _db.Query(
            "SELECT snapshot_id, path, kind, size, hash, object_id, mtime_utc, first_seen_utc, is_read_only " +
            "FROM snapshot_files WHERE snapshot_id = ?;",
            new object?[] { snapshotId },
            row => list.Add(new SnapshotFile
            {
                SnapshotId = row.GetInt64("snapshot_id"),
                RelativePath = row.GetString("path"),
                Kind = EntryKindExtensions.FromCode(row.GetStringOrNull("kind")),
                Size = row.GetInt64("size"),
                Hash = row.GetStringOrNull("hash"),
                ObjectId = row.GetInt64OrNull("object_id"),
                MtimeUtc = row.GetDateTimeUtcOrNull("mtime_utc"),
                FirstSeenUtc = row.GetDateTimeUtcOrNull("first_seen_utc"),
                IsReadOnly = row.GetBool("is_read_only"),
            }));
        return list;
    }

    public IReadOnlyList<(Snapshot Snapshot, SnapshotFile File)> HistoryOf(long rootId, string relativePath, int limit = 100)
    {
        var list = new List<(Snapshot, SnapshotFile)>();
        var key = PathUtil.NormalizeRelative(relativePath).ToLowerInvariant();
        _db.Query(
            """
            SELECT s.id AS s_id, s.root_id AS s_root, s.ts_utc AS s_ts, s.ts_local AS s_tsl, s.kind AS s_kind,
                   s.parent_id AS s_parent, s.event_high_watermark AS s_hw, s.file_count AS s_fc, s.dir_count AS s_dc,
                   s.total_bytes AS s_tb, s.is_complete AS s_ic, s.note AS s_note,
                   f.path AS f_path, f.kind AS f_kind, f.size AS f_size, f.hash AS f_hash,
                   f.object_id AS f_obj, f.mtime_utc AS f_mtime, f.first_seen_utc AS f_first, f.is_read_only AS f_ro
            FROM snapshot_files f
            JOIN snapshots s ON s.id = f.snapshot_id
            WHERE s.root_id = ? AND f.path_key = ?
            ORDER BY s.ts_utc DESC, s.id DESC
            LIMIT ?;
            """,
            new object?[] { rootId, key, limit },
            row =>
            {
                var snap = new Snapshot
                {
                    Id = row.GetInt64("s_id"),
                    RootId = row.GetInt64("s_root"),
                    TimestampUtc = new DateTime(row.GetInt64("s_ts"), DateTimeKind.Utc),
                    // ts_local 与 ts_utc 存的是同一个 UTC ticks（见 SqliteConnection.ToUnixTicks）。
                    // 读取时必须由 UTC 还原成本地时间，否则界面会把 UTC 当本地时间显示。
                    TimestampLocal = new DateTime(row.GetInt64("s_ts"), DateTimeKind.Utc).ToLocalTime(),
                    Kind = SnapshotKindExtensions.FromCode(row.GetStringOrNull("s_kind")),
                    ParentId = row.GetInt64OrNull("s_parent"),
                    EventHighWatermark = row.GetInt64("s_hw"),
                    FileCount = row.GetInt32("s_fc"),
                    DirectoryCount = row.GetInt32("s_dc"),
                    TotalBytes = row.GetInt64("s_tb"),
                    IsComplete = row.GetBool("s_ic"),
                    Note = row.GetStringOrNull("s_note"),
                };
                var file = new SnapshotFile
                {
                    SnapshotId = snap.Id,
                    RelativePath = row.GetString("f_path"),
                    Kind = EntryKindExtensions.FromCode(row.GetStringOrNull("f_kind")),
                    Size = row.GetInt64("f_size"),
                    Hash = row.GetStringOrNull("f_hash"),
                    ObjectId = row.GetInt64OrNull("f_obj"),
                    MtimeUtc = row.GetDateTimeUtcOrNull("f_mtime"),
                    FirstSeenUtc = row.GetDateTimeUtcOrNull("f_first"),
                    IsReadOnly = row.GetBool("f_ro"),
                };
                list.Add((snap, file));
            });
        return list;
    }

    public long CountFiles(long snapshotId)
    {
        long n = 0;
        _db.QueryFirst("SELECT COUNT(*) FROM snapshot_files WHERE snapshot_id = ?;", new object?[] { snapshotId }, r => n = r.GetInt64(0));
        return n;
    }

    /// <summary>只更新恢复点的备注（用户自定义标签，例如"升级前"）。</summary>
    public void UpdateNote(long snapshotId, string? note) =>
        _db.NonQuery("UPDATE snapshots SET note = ? WHERE id = ?;",
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(), snapshotId);

    public void Delete(long snapshotId)
    {
        _db.NonQuery("DELETE FROM snapshots WHERE id = ?;", snapshotId);
    }

    public HashSet<long> ListReferencedObjectIds(long? rootId = null)
    {
        var set = new HashSet<long>();
        var sql = rootId is null
            ? "SELECT DISTINCT object_id FROM snapshot_files WHERE object_id IS NOT NULL;"
            : "SELECT DISTINCT f.object_id FROM snapshot_files f JOIN snapshots s ON s.id = f.snapshot_id " +
              "WHERE f.object_id IS NOT NULL AND s.root_id = ?;";
        var args = rootId is null ? Array.Empty<object?>() : new object?[] { rootId.Value };
        _db.Query(sql, args, row =>
        {
            var id = row.GetInt64OrNull(0);
            if (id is not null) set.Add(id.Value);
        });
        return set;
    }

    /// <summary>统计快照清单占用的"逻辑体积"（用于磁盘占用展示）。</summary>
    public (long Snapshots, long ManifestRows) GetStatistics(long? rootId = null)
    {
        long snapshots = 0, rows = 0;
        var args = rootId is null ? Array.Empty<object?>() : new object?[] { rootId.Value };
        _db.QueryFirst(
            rootId is null ? "SELECT COUNT(*) FROM snapshots;" : "SELECT COUNT(*) FROM snapshots WHERE root_id = ?;",
            args, r => snapshots = r.GetInt64(0));
        _db.QueryFirst(
            rootId is null
                ? "SELECT COUNT(*) FROM snapshot_files;"
                : "SELECT COUNT(*) FROM snapshot_files f JOIN snapshots s ON s.id = f.snapshot_id WHERE s.root_id = ?;",
            args, r => rows = r.GetInt64(0));
        return (snapshots, rows);
    }

    private static Snapshot Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("id"),
        RootId = row.GetInt64("root_id"),
        TimestampUtc = new DateTime(row.GetInt64("ts_utc"), DateTimeKind.Utc),
        TimestampLocal = new DateTime(row.GetInt64("ts_utc"), DateTimeKind.Utc).ToLocalTime(),
        Kind = SnapshotKindExtensions.FromCode(row.GetStringOrNull("kind")),
        ParentId = row.GetInt64OrNull("parent_id"),
        EventHighWatermark = row.GetInt64("event_high_watermark"),
        FileCount = row.GetInt32("file_count"),
        DirectoryCount = row.GetInt32("dir_count"),
        TotalBytes = row.GetInt64("total_bytes"),
        IsComplete = row.GetBool("is_complete"),
        Note = row.GetStringOrNull("note"),
    };
}
