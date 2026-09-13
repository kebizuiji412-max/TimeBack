namespace LastRegret.Core.Config;

/// <summary>
/// 保护模式：决定"记录变化"之外还要不要**留存文件内容**。
///
/// 为什么必须有这个开关（实测数据，2026-09-11）：
///   对一个 39.6 万条 / 43.6GB 的真实目录做基线扫描，
///   完整留存内容的吞吐约 58 条/秒 → 全量需要约 1.9 小时，
///   而历史数据占用最高可达源目录大小（43GB）。
///   这对"我只想防手抖删错一个配置文件"的用户是完全不成比例的代价。
///
/// 三种模式的语义必须让用户一眼看懂，所以中文说明直接写在枚举上：
///   · 完整内容：能恢复内容，代价最高；
///   · 智能留存：小文件能恢复内容，大文件只记录"它变了"；
///   · 只记录变化：最省最快，但**不能恢复内容**（UI 必须如实标注）。
/// </summary>
public enum ProtectionMode
{
    /// <summary>完整留存所有文件内容（受单文件上限约束）。恢复能力最强，代价最高。</summary>
    FullContent = 0,

    /// <summary>只留存小于阈值（默认 4MB）的文件内容；更大的文件只记录变化事实。</summary>
    SmartContent = 1,

    /// <summary>只记录变化事实，不留存任何文件内容。最快、最省，但无法恢复内容。</summary>
    TrackOnly = 2,
}

public static class ProtectionModeExtensions
{
    public static string ToChinese(this ProtectionMode mode) => mode switch
    {
        ProtectionMode.FullContent => "完整内容",
        ProtectionMode.SmartContent => "智能留存",
        ProtectionMode.TrackOnly => "只记录变化",
        _ => "智能留存",
    };

    /// <summary>一句话说明（UI 直接展示，必须如实反映代价与能力）。</summary>
    public static string Describe(this ProtectionMode mode) => mode switch
    {
        ProtectionMode.FullContent =>
            "留存所有文件的历史内容。恢复能力最强；首次扫描最慢，历史占用最高（最多接近源目录大小）。",
        ProtectionMode.SmartContent =>
            "只留存小文件的历史内容（大文件只记录「它变了」）。适合代码/配置/文档目录，速度与占用都可接受。",
        ProtectionMode.TrackOnly =>
            "只记录「哪个文件在什么时候发生了什么变化」，不留存任何内容。最快最省，但**无法恢复文件内容**——只能告诉你事实。",
        _ => string.Empty,
    };

    public static string ToCode(this ProtectionMode mode) => mode switch
    {
        ProtectionMode.FullContent => "full",
        ProtectionMode.SmartContent => "smart",
        ProtectionMode.TrackOnly => "track",
        _ => "smart",
    };

    public static ProtectionMode FromCode(string? code) => code switch
    {
        "full" => ProtectionMode.FullContent,
        "track" => ProtectionMode.TrackOnly,
        _ => ProtectionMode.SmartContent,
    };
}
