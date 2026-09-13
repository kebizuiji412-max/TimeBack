namespace LastRegret.Windows.Sqlite;

/// <summary>
/// 数据库结构定义与迁移。
///
/// 设计要点（对应产品要求"索引 / 事务 / 崩溃恢复 / 并发 / 一致性"）：
///  1. **拆库**：事件与历史元数据放 <c>events.db</c>，内容对象索引放 <c>objects.db</c>。
///     好处：内容库可以独立备份/清理/校验，事件库保持小体积（查询快）。
///  2. **时间统一用 INTEGER（UTC ticks）**：可排序、可索引、无时区歧义，
///     同时单独存一份"落库时的本地时间"避免事后时区变化导致时间线漂移。
///  3. **外键全部显式声明**，并配合 <c>PRAGMA foreign_keys=ON</c> 保证引用完整性。
///  4. **写入全部走事务**；WAL + synchronous=FULL 保证断电后不出现半写状态。
///  5. **每个查询路径都有对应索引**，绝不做全表扫描。
/// </summary>
public static class Schema
{
    /// <summary>当前结构版本。每次改动结构必须 +1 并在 <see cref="Migrations"/> 中登记。</summary>
    public const int CurrentVersion = 1;

    // ═════════════════════════════════════════════════════════════════════
    // events.db
    // ═════════════════════════════════════════════════════════════════════

    public const string EventsDbSchema = """
-- ── 元信息 ───────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS meta (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL
);

-- ── 受保护目录 ───────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS watched_roots (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    path                  TEXT    NOT NULL,           -- 物理绝对路径（规范化，无尾随分隔符）
    path_key              TEXT    NOT NULL,           -- 小写形式，供大小写不敏感唯一性约束
    label                 TEXT,
    enabled               INTEGER NOT NULL DEFAULT 1,
    include_subdirs       INTEGER NOT NULL DEFAULT 1,
    created_utc           INTEGER NOT NULL,
    last_event_utc        INTEGER,
    baseline_snapshot_id  INTEGER REFERENCES snapshots(id) ON DELETE SET NULL,
    excludes_json         TEXT    NOT NULL DEFAULT '[]',
    max_file_size_bytes   INTEGER NOT NULL DEFAULT 67108864,
    last_scan_utc         INTEGER,
    scan_state            TEXT    NOT NULL DEFAULT 'idle'   -- idle|scanning|error
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_watched_roots_path ON watched_roots(path_key);

-- ── 路径索引（受保护范围的"当前状态"）───────────────────────────────────
-- 这是整个系统的状态中枢：事件处理器据此判定 Created/Modified/Renamed，
-- 快照据此增量构建，恢复预览据此与目标清单对比。
CREATE TABLE IF NOT EXISTS files (
    root_id           INTEGER NOT NULL REFERENCES watched_roots(id) ON DELETE CASCADE,
    path              TEXT    NOT NULL,               -- 相对路径，'/' 分隔
    path_key          TEXT    NOT NULL,               -- 小写，供大小写不敏感比较
    kind              TEXT    NOT NULL DEFAULT 'file',-- file|dir
    size              INTEGER NOT NULL DEFAULT 0,
    hash              TEXT,
    object_id         INTEGER,
    mtime_utc         INTEGER,
    first_seen_utc    INTEGER NOT NULL,
    last_changed_utc  INTEGER NOT NULL,
    last_event_id     INTEGER,
    is_deleted        INTEGER NOT NULL DEFAULT 0,
    is_read_only      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (root_id, path_key)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS ix_files_live      ON files(root_id, is_deleted, kind);
CREATE INDEX IF NOT EXISTS ix_files_hash      ON files(hash);
CREATE INDEX IF NOT EXISTS ix_files_changed   ON files(root_id, last_changed_utc);

-- ── 事件日志（事实记录）──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS events (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    root_id                 INTEGER NOT NULL REFERENCES watched_roots(id) ON DELETE CASCADE,
    ts_utc                  INTEGER NOT NULL,          -- 首次发生时刻（UTC ticks）
    ts_local                INTEGER NOT NULL,          -- 同刻的本地时间，冻结
    op                      TEXT    NOT NULL,          -- created|modified|deleted|renamed|moved|transient|unknown
    kind                    TEXT    NOT NULL DEFAULT 'file',
    path                    TEXT    NOT NULL,
    path_key                TEXT    NOT NULL,
    old_path                TEXT,
    old_path_key            TEXT,
    size_before             INTEGER,
    size_after              INTEGER,
    hash_before             TEXT,
    hash_after              TEXT,
    mtime_before_utc        INTEGER,
    mtime_after_utc         INTEGER,
    object_before           INTEGER,
    object_after            INTEGER,
    merge_count             INTEGER NOT NULL DEFAULT 1,
    suppressed_count        INTEGER NOT NULL DEFAULT 0,
    is_coalesced            INTEGER NOT NULL DEFAULT 0,
    is_transient            INTEGER NOT NULL DEFAULT 0,
    is_restore_induced      INTEGER NOT NULL DEFAULT 0,
    source                  TEXT    NOT NULL DEFAULT 'ReadDirectoryChangesW',
    confidence              INTEGER NOT NULL DEFAULT 0,
    pid                     INTEGER,
    process_name            TEXT,
    attribution_json        TEXT,
    affected_descendants    INTEGER NOT NULL DEFAULT 0,
    note                    TEXT,
    last_ts_utc             INTEGER                -- 合并窗口结束时刻
);
CREATE INDEX IF NOT EXISTS ix_events_time      ON events(root_id, ts_utc DESC);
CREATE INDEX IF NOT EXISTS ix_events_path_time ON events(root_id, path_key, ts_utc DESC);
CREATE INDEX IF NOT EXISTS ix_events_op_time   ON events(root_id, op, ts_utc DESC);
CREATE INDEX IF NOT EXISTS ix_events_pid       ON events(pid) WHERE pid IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_events_visible   ON events(root_id, is_transient, ts_utc DESC);

-- ── 文件历史版本 ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS file_versions (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    root_id        INTEGER NOT NULL REFERENCES watched_roots(id) ON DELETE CASCADE,
    path           TEXT    NOT NULL,
    path_key       TEXT    NOT NULL,
    hash           TEXT    NOT NULL,
    object_id      INTEGER,
    size           INTEGER NOT NULL,
    recorded_utc   INTEGER NOT NULL,
    recorded_local INTEGER NOT NULL,
    mtime_utc      INTEGER,
    event_id       INTEGER REFERENCES events(id) ON DELETE SET NULL,
    snapshot_id    INTEGER REFERENCES snapshots(id) ON DELETE SET NULL,
    content_pruned INTEGER NOT NULL DEFAULT 0,
    note           TEXT
);
CREATE INDEX IF NOT EXISTS ix_versions_path ON file_versions(root_id, path_key, recorded_utc DESC);
CREATE INDEX IF NOT EXISTS ix_versions_hash ON file_versions(hash);
CREATE INDEX IF NOT EXISTS ix_versions_time ON file_versions(recorded_utc);
CREATE INDEX IF NOT EXISTS ix_versions_obj  ON file_versions(object_id);

-- ── 快照（恢复点）────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS snapshots (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    root_id             INTEGER NOT NULL REFERENCES watched_roots(id) ON DELETE CASCADE,
    ts_utc              INTEGER NOT NULL,
    ts_local            INTEGER NOT NULL,
    kind                TEXT    NOT NULL DEFAULT 'auto',
    parent_id           INTEGER REFERENCES snapshots(id) ON DELETE SET NULL,
    event_high_watermark INTEGER NOT NULL DEFAULT 0,
    file_count          INTEGER NOT NULL DEFAULT 0,
    dir_count           INTEGER NOT NULL DEFAULT 0,
    total_bytes         INTEGER NOT NULL DEFAULT 0,
    is_complete         INTEGER NOT NULL DEFAULT 1,
    note                TEXT
);
CREATE INDEX IF NOT EXISTS ix_snapshots_root_time ON snapshots(root_id, ts_utc DESC);
CREATE INDEX IF NOT EXISTS ix_snapshots_kind      ON snapshots(root_id, kind, ts_utc DESC);

-- ── 快照清单（某时刻的完整状态）──────────────────────────────────────────
CREATE TABLE IF NOT EXISTS snapshot_files (
    snapshot_id   INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
    path          TEXT    NOT NULL,
    path_key      TEXT    NOT NULL,
    kind          TEXT    NOT NULL DEFAULT 'file',
    size          INTEGER NOT NULL DEFAULT 0,
    hash          TEXT,
    object_id     INTEGER,
    mtime_utc     INTEGER,
    first_seen_utc INTEGER,
    is_read_only  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (snapshot_id, path_key)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS ix_snapfiles_path   ON snapshot_files(path_key, snapshot_id);
CREATE INDEX IF NOT EXISTS ix_snapfiles_object ON snapshot_files(object_id) WHERE object_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_snapfiles_hash   ON snapshot_files(hash) WHERE hash IS NOT NULL;

-- ── 恢复操作 ─────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS restore_operations (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    root_id               INTEGER NOT NULL REFERENCES watched_roots(id) ON DELETE CASCADE,
    target_ts_utc         INTEGER NOT NULL,
    target_ts_local       INTEGER NOT NULL,
    target_snapshot_id    INTEGER REFERENCES snapshots(id) ON DELETE SET NULL,
    pre_snapshot_id       INTEGER REFERENCES snapshots(id) ON DELETE SET NULL,
    post_snapshot_id      INTEGER REFERENCES snapshots(id) ON DELETE SET NULL,
    status                TEXT    NOT NULL DEFAULT 'planned',
    started_utc           INTEGER NOT NULL,
    finished_utc          INTEGER,
    planned_count         INTEGER NOT NULL DEFAULT 0,
    succeeded_count       INTEGER NOT NULL DEFAULT 0,
    failed_count          INTEGER NOT NULL DEFAULT 0,
    plan_fingerprint      TEXT,
    message               TEXT,
    undone_by_operation_id INTEGER,
    undoes_operation_id   INTEGER
);
CREATE INDEX IF NOT EXISTS ix_restore_root_time ON restore_operations(root_id, started_utc DESC);
CREATE INDEX IF NOT EXISTS ix_restore_status    ON restore_operations(status);

-- ── 恢复明细（同时是执行日志，崩溃后据此判断实际做到哪一步）─────────────
CREATE TABLE IF NOT EXISTS restore_steps (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    operation_id      INTEGER NOT NULL REFERENCES restore_operations(id) ON DELETE CASCADE,
    seq               INTEGER NOT NULL,
    action            TEXT    NOT NULL,
    path              TEXT    NOT NULL,
    path_key          TEXT    NOT NULL,
    target_hash       TEXT,
    before_hash       TEXT,
    before_object_id  INTEGER,
    before_size       INTEGER,
    succeeded         INTEGER NOT NULL DEFAULT 0,
    skipped_conflict  INTEGER NOT NULL DEFAULT 0,
    error             TEXT,
    executed_utc      INTEGER
);
CREATE INDEX IF NOT EXISTS ix_restore_steps_op ON restore_steps(operation_id, seq);

-- ── 关联进程（绝不是"元凶名单"，只是"附近出现过什么"）───────────────────
CREATE TABLE IF NOT EXISTS processes (
    pid             INTEGER PRIMARY KEY,
    name            TEXT    NOT NULL,
    exe_path        TEXT,
    start_utc       INTEGER,
    last_seen_utc   INTEGER NOT NULL,
    had_foreground  INTEGER NOT NULL DEFAULT 0,
    window_title    TEXT
);
CREATE INDEX IF NOT EXISTS ix_processes_seen ON processes(last_seen_utc DESC);
CREATE INDEX IF NOT EXISTS ix_processes_name ON processes(name);

-- ── 设置（键值）────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
""";

    // ═════════════════════════════════════════════════════════════════════
    // objects.db
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>会话级临时表（不落盘、不产生 WAL）。用于快照清单的批量写入，性能提升显著。</summary>
    public const string TempTablesSchema = """
CREATE TEMP TABLE IF NOT EXISTS _manifest_stage (
    path           TEXT NOT NULL,
    path_key       TEXT NOT NULL,
    kind           TEXT NOT NULL,
    size           INTEGER NOT NULL,
    hash           TEXT,
    object_id      INTEGER,
    mtime_utc      INTEGER,
    first_seen_utc INTEGER,
    is_read_only   INTEGER NOT NULL DEFAULT 0
);
""";

    public const string ObjectsDbSchema = """
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- 内容寻址存储的对象索引。相同内容（相同 SHA-256）只存一份。
CREATE TABLE IF NOT EXISTS objects (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    hash           TEXT    NOT NULL,
    logical_size   INTEGER NOT NULL,
    stored_size    INTEGER NOT NULL,
    encoding       TEXT    NOT NULL DEFAULT 'raw',   -- raw|deflate
    ext_hint       TEXT,
    created_utc    INTEGER NOT NULL,
    ref_write_utc  INTEGER NOT NULL                  -- 最近一次被引用的时间（清理排序用）
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_objects_hash ON objects(hash);
CREATE INDEX IF NOT EXISTS ix_objects_created ON objects(created_utc);
""";

    /// <summary>结构版本迁移脚本：key = 目标版本，value = 从 (key-1) 升级到 key 的 SQL。</summary>
    public static readonly IReadOnlyDictionary<int, string> Migrations = new Dictionary<int, string>
    {
        // 版本 1 是初始结构，由上面的 EventsDbSchema / ObjectsDbSchema 建立
        [1] = string.Empty,
    };
}
