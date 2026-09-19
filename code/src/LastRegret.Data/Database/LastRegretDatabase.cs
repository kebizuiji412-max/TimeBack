using LastRegret.Core.Config;
using LastRegret.Core.Util;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

/// <summary>
/// 数据库引导与生命周期管理。
///
/// 崩溃安全的做法：
///  1. WAL + synchronous=FULL（见 <see cref="SqliteConnection"/>），保证已提交事务不丢；
///  2. 启动时做 <c>integrity_check</c> 与 <c>foreign_key_check</c>，
///     发现问题**如实报告**，绝不静默继续写坏数据；
///  3. 结构升级走 <c>BEGIN IMMEDIATE</c> 事务，失败自动回滚；
///  4. 升级前自动备份（把 events.db 复制为 events.db.bak-vN），
///     用户可以在出现问题时回到升级前状态。
/// </summary>
public sealed class LastRegretDatabase : IDisposable
{
    private readonly string _dataDir;
    private bool _disposed;

    public SqliteConnection Events { get; }

    public SqliteConnection Objects { get; }

    public string EventsDbPath { get; }

    public string ObjectsDbPath { get; }

    public string DataDirectory => _dataDir;

    public string StoreRoot { get; }

    public DatabaseHealth Health { get; private set; } = new();

    private LastRegretDatabase(string dataDir, SqliteConnection events, SqliteConnection objects)
    {
        _dataDir = dataDir;
        Events = events;
        Objects = objects;
        EventsDbPath = events.Path;
        ObjectsDbPath = objects.Path;
        StoreRoot = System.IO.Path.Combine(dataDir, "store");
    }

    /// <summary>
    /// 打开（必要时创建）数据库。
    /// </summary>
    /// <param name="dataDir">数据目录（默认 %LOCALAPPDATA%\LastRegret\data）。</param>
    public static LastRegretDatabase Open(string dataDir)
    {
        dataDir = PathUtil.NormalizeRoot(dataDir);
        Directory.CreateDirectory(dataDir);

        var eventsPath = System.IO.Path.Combine(dataDir, "events.db");
        var objectsPath = System.IO.Path.Combine(dataDir, "objects.db");

        var eventsConn = SqliteConnection.Open(eventsPath);
        SqliteConnection? objectsConn = null;
        try
        {
            objectsConn = SqliteConnection.Open(objectsPath);
            var db = new LastRegretDatabase(dataDir, eventsConn, objectsConn);
            db.Initialize();
            return db;
        }
        catch
        {
            objectsConn?.Dispose();
            eventsConn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 初始化两个数据库。
    ///
    /// 顺序很重要（踩坑记录）：
    ///  **必须先建表再读 meta**。曾经把 `读取 schema_version` 放在建表之前，
    ///  结果全新数据库一律报 "no such table: meta" 而完全无法启动。
    ///  现在的做法是"先幂等建表（CREATE TABLE IF NOT EXISTS），再读版本号"。
    /// </summary>
    private void Initialize()
    {
        var health = new DatabaseHealth();

        // 1) 幂等建表（含 TEMP 表）。对已有数据库来说这一步只是确认结构存在。
        Events.InTransaction(() =>
        {
            Events.ExecuteBatch(Schema.EventsDbSchema);
            Events.ExecuteBatch(Schema.TempTablesSchema);
        });
        Objects.InTransaction(() => Objects.ExecuteBatch(Schema.ObjectsDbSchema));

        // 2) 读取结构版本
        int version = GetMetaInt(Events, "schema_version", 0);

        if (version == 0)
        {
            SetMeta(Events, "schema_version", Schema.CurrentVersion.ToString());
            SetMeta(Objects, "schema_version", Schema.CurrentVersion.ToString());
            SetMeta(Events, "created_utc", DateTime.UtcNow.Ticks.ToString());
            SetMeta(Events, "app_version", "0.2.1");
            health.SchemaVersion = Schema.CurrentVersion;
        }
        else if (version < Schema.CurrentVersion)
        {
            UpgradeSchema(version, health);
        }
        else
        {
            health.SchemaVersion = version;
            // 结构版本比程序更新（用户降级运行）：不擅自改动，如实报告
            if (version > Schema.CurrentVersion)
            {
                health.RecoveryNote =
                    $"历史数据库结构版本（{version}）高于当前程序（{Schema.CurrentVersion}），" +
                    "程序将以只读方式尽量读取既有数据，不会降级或改写结构。";
            }
        }

        // 启动自检
        var eventsProblem = Events.IntegrityCheck();
        health.EventsDbOk = eventsProblem is null;
        health.EventsProblem = eventsProblem;

        var objectsProblem = Objects.IntegrityCheck();
        health.ObjectsDbOk = objectsProblem is null;
        health.ObjectsProblem = objectsProblem;

        try
        {
            health.ForeignKeyProblems = Events.ForeignKeyCheck().Count;
        }
        catch (SqliteException ex)
        {
            health.ForeignKeyProblems = -1;
            health.RecoveryNote = $"外键检查失败：{ex.Message}";
        }

        Health = health;
    }

    private void UpgradeSchema(int fromVersion, DatabaseHealth health)
    {
        // 升级前备份（廉价保险）
        try
        {
            Events.Checkpoint();
            var backup = $"{EventsDbPath}.bak-v{fromVersion}";
            if (!File.Exists(backup))
            {
                File.Copy(EventsDbPath, backup, overwrite: false);
                health.RecoveryNote = $"升级前已备份数据库到 {System.IO.Path.GetFileName(backup)}";
            }
        }
        catch (Exception ex)
        {
            health.RecoveryNote = $"升级前备份失败（继续升级）：{ex.Message}";
        }

        Events.InTransaction(() =>
        {
            for (int v = fromVersion + 1; v <= Schema.CurrentVersion; v++)
            {
                if (Schema.Migrations.TryGetValue(v, out var sql) && !string.IsNullOrWhiteSpace(sql))
                {
                    Events.Execute(sql);
                }
            }
            SetMeta(Events, "schema_version", Schema.CurrentVersion.ToString());
        });

        Objects.InTransaction(() =>
        {
            Objects.ExecuteBatch(Schema.ObjectsDbSchema);
            SetMeta(Objects, "schema_version", Schema.CurrentVersion.ToString());
        });

        health.SchemaUpgraded = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // meta 表读写
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 建立一个"最小可用"的对象库（只含 meta + objects 两张表）。
    /// 用途：单独测试内容寻址存储（CAS）时不必拉起整套历史数据库。
    /// </summary>
    public static void PrepareObjectsDbOnly(SqliteConnection objectsConn)
    {
        objectsConn.InTransaction(() =>
        {
            objectsConn.ExecuteBatch(Schema.ObjectsDbSchema);
            if (GetMetaInt(objectsConn, "schema_version", 0) == 0)
            {
                SetMeta(objectsConn, "schema_version", Schema.CurrentVersion.ToString());
            }
        });
    }

    /// <summary>确保会话级临时表存在（TEMP 表随连接生命周期存在，新连接需要重建）。</summary>
    public void EnsureTempTables() => Events.ExecuteBatch(Schema.TempTablesSchema);

    public static string? GetMeta(SqliteConnection conn, string key)
    {
        string? value = null;
        conn.QueryFirst("SELECT value FROM meta WHERE key = ? LIMIT 1;", new object?[] { key }, row => value = row.GetString(0));
        return value;
    }

    public static int GetMetaInt(SqliteConnection conn, string key, int fallback)
    {
        var raw = GetMeta(conn, key);
        return int.TryParse(raw, out var v) ? v : fallback;
    }

    public static void SetMeta(SqliteConnection conn, string key, string value) =>
        conn.NonQuery("INSERT INTO meta(key, value) VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", key, value);

    // ─────────────────────────────────────────────────────────────────────
    // 维护操作
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>一致性检查（设置页"检查历史完整性"按钮）。</summary>
    public (bool Ok, string Report) CheckIntegrity()
    {
        var lines = new List<string>();
        var events = Events.IntegrityCheck();
        lines.Add(events is null ? "事件库：完好" : $"事件库：{events}");
        var objects = Objects.IntegrityCheck();
        lines.Add(objects is null ? "内容库：完好" : $"内容库：{objects}");
        var fk = Events.ForeignKeyCheck();
        lines.Add(fk.Count == 0 ? "外键引用：一致" : $"外键引用：{fk.Count} 处不一致");
        bool ok = events is null && objects is null && fk.Count == 0;
        return (ok, string.Join(Environment.NewLine, lines));
    }

    /// <summary>把数据库复制到目标路径（导出/备份）。</summary>
    public void BackupTo(string targetPath)
    {
        Events.Checkpoint();
        Objects.Checkpoint();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath) ?? ".");
        File.Copy(EventsDbPath, targetPath, overwrite: true);
    }

    /// <summary>统计数据库文件占用（含 WAL）。</summary>
    public long GetDatabaseFootprint()
    {
        long total = 0;
        foreach (var path in new[] { EventsDbPath, EventsDbPath + "-wal", EventsDbPath + "-shm", ObjectsDbPath, ObjectsDbPath + "-wal", ObjectsDbPath + "-shm" })
        {
            try
            {
                if (File.Exists(path)) total += new FileInfo(path).Length;
            }
            catch (IOException) { /* 统计失败不影响正确性 */ }
        }
        return total;
    }

    /// <summary>清空所有历史（"重置历史数据"用）。会保留受保护目录配置。</summary>
    public void ResetHistory()
    {
        Events.InTransaction(() =>
        {
            Events.Execute("DELETE FROM restore_steps;");
            Events.Execute("DELETE FROM restore_operations;");
            Events.Execute("DELETE FROM snapshot_files;");
            Events.Execute("DELETE FROM snapshots;");
            Events.Execute("DELETE FROM file_versions;");
            Events.Execute("DELETE FROM events;");
            Events.Execute("UPDATE files SET is_deleted = 1, object_id = NULL, hash = NULL;");
            Events.Execute("DELETE FROM processes;");
        });
        Objects.InTransaction(() => Objects.Execute("DELETE FROM objects;"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Objects.Dispose(); }
        finally { Events.Dispose(); }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 设置读写（键值 + JSON 主体）
    // ─────────────────────────────────────────────────────────────────────

    public AppSettings LoadSettings() => DatabaseSettings.Load(Events);

    public void SaveSettings(AppSettings settings) => DatabaseSettings.Save(Events, settings);
}
