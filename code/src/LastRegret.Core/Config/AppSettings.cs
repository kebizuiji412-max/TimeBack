using System.Text;
using LastRegret.Core.Model;

namespace LastRegret.Core.Config;

/// <summary>
/// 全局设置（持久化在 settings 表，键值对形式）。
/// 所有影响"保护范围/磁盘占用/安全边界"的参数都必须在这里显式可见，
/// 不允许埋在代码常量里让用户无法控制。
/// </summary>
public sealed class AppSettings
{
    // ── 事件合并与延迟确认 ───────────────────────────────────────────────
    /// <summary>同一路径的修改事件合并窗口（毫秒）。VS Code 一次保存常产生多个事件。</summary>
    public int MergeWindowMs { get; set; } = 1500;

    /// <summary>事件到达后延迟多久才去读取内容（毫秒），用于躲开"写入中"的瞬时状态。</summary>
    public int SettleDelayMs { get; set; } = 400;

    /// <summary>重命名/移动的配对窗口（毫秒）：删除 + 创建配对时间隔阈值。</summary>
    public int RenamePairWindowMs { get; set; } = 800;

    /// <summary>延迟确认时最多等待多久（毫秒）；超过则强制落库并标注"可能仍不稳定"。</summary>
    public int MaxConfirmDelayMs { get; set; } = 8000;

    /// <summary>单个文件在合并窗口内连续变化多少次后，中止继续等待（防止持续写入的文件永远不落库）。</summary>
    public int MaxMergeExtensions { get; set; } = 8;

    // ── 快照 ─────────────────────────────────────────────────────────────
    /// <summary>自动快照间隔（分钟）。0 = 只在手动/恢复前创建。</summary>
    public int AutoSnapshotIntervalMinutes { get; set; } = 10;

    /// <summary>自动快照的最小事件数门槛（少于该数量则跳过，避免产生大量空快照）。</summary>
    public int AutoSnapshotMinEvents { get; set; } = 5;

    // ── 存储 ─────────────────────────────────────────────────────────────
    /// <summary>历史数据占用上限（字节）。超过后按策略清理。</summary>
    public long MaxHistoryBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>历史保留天数。0 = 不按时间清理。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>单文件留存内容的最大大小（字节）；超过则只记录事件不保存内容。</summary>
    public long MaxStoreFileSizeBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// 保护模式。这是"保护一个 43GB / 39 万文件的目录到底要付多少代价"的总开关。
    ///
    /// 实测（2026-09-11，D:\TestData 39.6 万条 / 43.6GB，机械盘）：
    ///   · 完整内容模式：首次扫描约 58 条/秒 → 全量约 1.9 小时，历史占用最高 ≈ 源目录大小；
    ///   · 只记录变化模式：只做 stat，不读文件内容 → 快几十倍，但**无法恢复内容**。
    /// 因此必须让用户显式选择，而不是替他决定。
    /// </summary>
    public ProtectionMode Protection { get; set; } = ProtectionMode.SmartContent;

    /// <summary>
    /// 智能模式下留存内容的大小上限（字节）。超过这个大小的文件只记录变化事实。
    /// 默认 4MB：绝大多数"改坏了的配置/代码/文档"都在这个范围内，
    /// 而大文件（数据库、镜像、视频）留存内容的收益低、代价高。
    /// </summary>
    public long SmartContentMaxBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>是否对可压缩内容启用 deflate 存储。</summary>
    public bool EnableCompression { get; set; } = true;

    // ── 排除规则 ─────────────────────────────────────────────────────────
    /// <summary>全局排除的文件名/通配（大小写不敏感）。</summary>
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "*.tmp", "*.temp", "*.swp", "*.swx", "*.swo", "*~",
        "~$*", ".~lock.*", "*.crdownload", "*.part", "*.partial",
        "*.lock", "Thumbs.db", "desktop.ini", ".DS_Store",
        "*.lnk.tmp", "*.bak.tmp",
    };

    /// <summary>全局排除的目录名。</summary>
    public List<string> ExcludeDirectoryNames { get; set; } = new()
    {
        ".git", "node_modules", ".vs", ".idea", "$RECYCLE.BIN",
        "System Volume Information", "__pycache__", ".venv", "venv",
        "obj", "bin",
    };

    /// <summary>是否记录被排除路径的事件（记为折叠项）。默认 true：事实要留下，只是不显眼。</summary>
    public bool RecordExcludedAsTransient { get; set; } = true;

    // ── 进程关联 ─────────────────────────────────────────────────────────
    /// <summary>是否启用关联进程探测。</summary>
    public bool EnableProcessAttribution { get; set; } = true;

    /// <summary>关联进程的时间窗口（毫秒）。</summary>
    public int AttributionWindowMs { get; set; } = 3000;

    // ── 界面 ─────────────────────────────────────────────────────────────
    /// <summary>是否在时间线默认聚合折叠瞬时事件。</summary>
    public bool CollapseTransientInTimeline { get; set; } = true;

    // ── 后台服务 ─────────────────────────────────────────────────────────
    // 注意：托盘常驻与"随 Windows 启动"目前都**没有实现**。
    // 曾经这里有一个 StartWithWindows 配置 + 设置页勾选框，但只存值、没有任何注册表/启动项
    // 写入逻辑 —— 那等于给用户一个假开关，比没有更糟，所以连同 UI 一起删除了。
    // 将来要真正实现时，在这里加回配置，并同时补上实际的启动项注册与自检。

    // ── 版本 ─────────────────────────────────────────────────────────────
    public int SchemaVersion { get; set; } = 1;

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, JsonOpts);

    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try
        {
            var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            return s ?? new AppSettings();
        }
        catch (System.Text.Json.JsonException)
        {
            // 设置损坏不能导致程序无法启动：回退默认值，由调用方记录告警
            return new AppSettings();
        }
    }

    public static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>用于 UI 展示的排除规则摘要。</summary>
    public string DescribeExclusions()
    {
        var sb = new StringBuilder();
        sb.Append("文件模式：").Append(string.Join(", ", ExcludePatterns));
        sb.Append("　目录：").Append(string.Join(", ", ExcludeDirectoryNames));
        return sb.ToString();
    }
}
