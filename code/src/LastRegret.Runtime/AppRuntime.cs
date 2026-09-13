using System.IO;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
using LastRegret.Data;
using LastRegret.Engine;
using LastRegret.Windows.Io;
using LastRegret.Windows.Processes;
using LastRegret.Windows.Storage;

namespace LastRegret.Runtime;

/// <summary>
/// 运行期依赖容器（手写，避免引入任何第三方 DI 包）。
/// 生命周期：与进程相同；<see cref="Dispose"/> 时按依赖顺序释放。
///
/// 这是**应用组合根**：把数据层、Windows 互操作层与引擎层拼装起来，并把启动过程中
/// 的每一个失败都变成调用方能处理的返回值/自检说明，而不是让宿主闪退。
///
/// 它不认识 WPF：本程序集（LastRegret.Runtime）目标框架是 net8.0，
/// 编译期就拿不到 PresentationFramework / PresentationCore / WindowsBase，
/// 所以任何 UI 依赖都不可能被误加进来。WPF 壳（LastRegret.App）引用它，
/// 未来的 CLI / MCP 宿主也用同样的方式引用它。
/// </summary>
public sealed class AppRuntime : IDisposable
{
    public LastRegretDatabase Database { get; private init; } = null!;
    public AppSettings Settings { get; private set; } = new();
    public ISettingsRepository SettingsRepository { get; private init; } = null!;
    public WatchedRootRepository Roots { get; private init; } = null!;
    public FileIndexRepository Index { get; private init; } = null!;
    public EventRepository Events { get; private init; } = null!;
    public SnapshotRepository SnapshotsRepo { get; private init; } = null!;
    public FileVersionRepository Versions { get; private init; } = null!;
    public RestoreRepository RestoreRepo { get; private init; } = null!;
    public ProcessRepository Processes { get; private init; } = null!;
    public ContentStore Store { get; private init; } = null!;
    public FileSystemReader Reader { get; private init; } = null!;
    public ProcessProbe Probe { get; private init; } = null!;
    public ContentWriter Writer { get; private init; } = null!;
    public SnapshotService SnapshotService { get; private init; } = null!;
    public CompareService Compare { get; private init; } = null!;
    public WatchService Watch { get; private init; } = null!;
    public RestoreEngine Restore { get; private init; } = null!;
    public MaintenanceService Maintenance { get; private init; } = null!;

    /// <summary>启动自检报告（UI 会在"设置"页原样展示）。</summary>
    public DatabaseHealth Health { get; private init; } = new();

    /// <summary>启动时发现的"上一次恢复没做完"的事实描述。</summary>
    public List<string> InterruptedOperations { get; } = new();

    /// <summary>数据目录（%LOCALAPPDATA%\LastRegret\data，不可写时退回程序目录）。</summary>
    public string DataDirectory { get; private init; } = string.Empty;

    public bool DataDirectoryIsFallback { get; private set; }

    private readonly List<string> _startupNotes = new();

    public IReadOnlyList<string> StartupNotes => _startupNotes;

    public static string ResolveLogDirectory() => Path.Combine(ResolveDataDirectory().Dir, "logs");

    /// <summary>
    /// 存储位置设置文件。**故意放在固定的 %LOCALAPPDATA%\LastRegret\ 下**，
    /// 而不是放在用户自选的数据目录里 —— 否则一旦用户把数据目录改到别的盘，
    /// 下次启动就找不到这个设置文件，等于改不回来。
    /// </summary>
    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LastRegret", "storage.json");

    /// <summary>用户指定的数据目录（为空表示用默认位置）。改这个值需要重启程序才生效。</summary>
    public static string? CustomDataDirectory { get; private set; }

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LastRegret", "data");

    private static (string Dir, bool IsFallback) ResolveDataDirectory()
    {
        // ① 用户明确指定过存储位置 → 用它（放在别的盘也可以）
        var custom = LoadCustomDataDirectory();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (TryEnsureWritable(custom)) return (custom, false);
            // 用户指定的位置不可写：不能静默退回，界面必须如实说明
            CustomDirectoryUnusable = true;
        }

        // ② 首选用户数据目录（符合"普通用户权限运行"与"数据不放在程序目录"的惯例）
        if (TryEnsureWritable(DefaultDataDirectory)) return (DefaultDataDirectory, false);

        // ③ 退回程序目录（例如权限受限的环境）；UI 会明确告知用户实际位置
        var fallback = Path.Combine(AppContext.BaseDirectory, "LastRegretData");
        if (TryEnsureWritable(fallback)) return (fallback, true);

        // ④ 最后的兜底：临时目录（至少保证程序能启动并说明情况）
        var temp = Path.Combine(Path.GetTempPath(), "LastRegret-data");
        Directory.CreateDirectory(temp);
        return (temp, true);
    }

    /// <summary>用户指定的位置这次不可写（界面据此提示，而不是悄悄换地方）。</summary>
    public static bool CustomDirectoryUnusable { get; private set; }

    private static string? LoadCustomDataDirectory()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return null;
            var text = File.ReadAllText(SettingsFilePath);
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"dataDirectory\"\\s*:\\s*\"([^\"]*)\"");
            if (!m.Success) return null;
            var value = m.Groups[1].Value.Replace("\\\\", "\\");
            CustomDataDirectory = value;
            return value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 设置数据目录。写的是固定位置的小配置文件，**下次启动生效**。
    /// 之所以不热切换：数据库与内容库都已经打开并持有句柄，中途换目录
    /// 要么得停掉监听、要么得在旧目录留半份数据 —— 都不是好结果。
    /// </summary>
    public static void SetCustomDataDirectory(string? dir)
    {
        try
        {
            var path = SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = "{\n  \"dataDirectory\": \"" +
                       (dir ?? string.Empty).Replace("\\", "\\\\") + "\"\n}\n";
            File.WriteAllText(path, json);
        }
        catch (Exception)
        {
            // 写不进去就让上层用提示告诉用户，不抛异常打断界面
        }
    }

    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static AppRuntime Create()
    {
        var (dataDir, isFallback) = ResolveDataDirectory();
        Directory.CreateDirectory(dataDir);

        var db = LastRegretDatabase.Open(dataDir);
        var settingsRepo = new SettingsRepository(db);
        var settings = settingsRepo.Load();

        var roots = new WatchedRootRepository(db.Events);
        var index = new FileIndexRepository(db.Events);
        var events = new EventRepository(db.Events);
        var snapshotsRepo = new SnapshotRepository(db);
        var versions = new FileVersionRepository(db.Events);
        var restoreRepo = new RestoreRepository(db.Events);
        var processes = new ProcessRepository(db.Events);

        var store = new ContentStore(db.Objects, Path.Combine(dataDir, "store"), settings.EnableCompression);
        var reader = new FileSystemReader();
        var probe = new ProcessProbe();
        var diagnostics = new StorageDiagnostics();
        var writer = new ContentWriter(store, settings, diagnostics);
        var clock = SystemClock.Instance;

        var snapshotService = new SnapshotService(db, snapshotsRepo, index, roots, events, versions, store, restoreRepo, clock);
        var compare = new CompareService(snapshotService, snapshotsRepo, events, roots, store, clock);
        var exclusions = new Core.Events.ExclusionMatcher(settings);

        var watch = new WatchService(db, roots, index, events, versions, snapshotService, settingsRepo,
            store, reader, probe, processes, writer, clock);

        var rescanner = new Rescanner(index, events, versions, reader, writer, exclusions, settings, clock);
        watch.Rescanner = rescanner;

        var restore = new RestoreEngine(db, roots, index, snapshotsRepo, restoreRepo, versions,
            snapshotService, compare, store, writer, reader, clock, watch, settings);

        var maintenance = new MaintenanceService(db, store, snapshotsRepo, restoreRepo, versions, roots, settingsRepo, clock);

        var runtime = new AppRuntime
        {
            Database = db,
            Settings = settings,
            SettingsRepository = settingsRepo,
            Roots = roots,
            Index = index,
            Events = events,
            SnapshotsRepo = snapshotsRepo,
            Versions = versions,
            RestoreRepo = restoreRepo,
            Processes = processes,
            Store = store,
            Reader = reader,
            Probe = probe,
            Writer = writer,
            SnapshotService = snapshotService,
            Compare = compare,
            Watch = watch,
            Restore = restore,
            Maintenance = maintenance,
            Health = db.Health,
            DataDirectory = dataDir,
        };

        runtime.DataDirectoryIsFallback = isFallback;

        // ── 启动自检结果如实汇报 ──
        runtime._startupNotes.Add($"数据目录：{dataDir}" + (isFallback ? "（用户数据目录不可写，已退回此位置）" : string.Empty));
        runtime._startupNotes.Add(db.Health.Describe());
        if (db.Health.SchemaUpgraded) runtime._startupNotes.Add("数据库结构已升级。");
        if (db.Health.RecoveryNote is not null) runtime._startupNotes.Add(db.Health.RecoveryNote);

        var capability = probe.GetCapability();
        runtime._startupNotes.Add("进程归属能力：" + capability.Summary);

        // 上次没做完的恢复
        runtime.InterruptedOperations.AddRange(restore.DetectInterruptedOperations());

        // 清理崩溃残留的临时对象文件
        var removed = store.CleanupTempFiles(TimeSpan.FromHours(6));
        if (removed > 0) runtime._startupNotes.Add($"已清理 {removed} 个上次运行遗留的临时文件。");

        // 启动监听（不影响窗口显示）
        watch.Start();
        watch.StartWatchingAll();

        // 自愈：找出"启用了保护但没有基线"的根（典型成因是首次扫描时用户强杀了进程，
        // 导致监听从未真正开始、时间线永远空白）。这里不静默处理，而是明确告知并
        // 由界面提供一键补齐，避免用户对着空白时间线猜原因。
        try
        {
            foreach (var root in watch.FindRootsMissingBaseline())
            {
                var health = watch.DescribeRootHealth(root.Id);
                runtime._startupNotes.Add(
                    $"⚠ 保护目录「{root.Path}」尚未建立基线（索引 {health.IndexEntries} 项、事件 {health.EventCount} 条）：" +
                    "首次扫描可能被中断过。请在设置页点击「重新扫描补齐」，否则这段时间的变化不会被记录。");
            }
        }
        catch (Exception ex)
        {
            runtime._startupNotes.Add($"检查基线状态失败：{ex.Message}");
        }

        // 超过容量上限时回收"未被恢复点引用"的内容
        try
        {
            var freed = maintenance.AutoTrimToQuota(msg => runtime._startupNotes.Add(msg));
            if (freed > 0) runtime._startupNotes.Add($"空间管理：已释放 {Core.Util.PathUtil.FormatBytes(freed)}。");
        }
        catch (Exception ex)
        {
            runtime._startupNotes.Add($"空间管理检查失败：{ex.Message}");
        }

        return runtime;
    }

    /// <summary>重新加载设置并应用到各组件（保存设置后调用）。</summary>
    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        SettingsRepository.Save(settings);
        Watch.ReloadSettings();
    }

    public void Dispose()
    {
        try { Watch.Dispose(); }
        catch (Exception) { /* 关闭阶段异常不应中断退出 */ }

        try { Store.Dispose(); }
        catch (Exception) { }

        try { Database.Dispose(); }
        catch (Exception) { }
    }
}
