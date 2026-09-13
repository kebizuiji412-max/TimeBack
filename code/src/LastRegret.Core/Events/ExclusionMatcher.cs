using System.Text.RegularExpressions;

namespace LastRegret.Core.Events;

using LastRegret.Core.Config;
using LastRegret.Core.Util;

/// <summary>排除规则的匹配结果（带可解释的原因，便于 UI 如实说明"为什么不记录")。</summary>
public sealed class ExclusionVerdict
{
    public bool Excluded { get; init; }

    public string Reason { get; init; } = string.Empty;

    /// <summary>命中的模式。</summary>
    public string? Pattern { get; init; }

    public static readonly ExclusionVerdict NotExcluded = new() { Excluded = false };
}

/// <summary>
/// 排除规则匹配器（临时文件 / 已知噪声目录）。
///
/// 设计立场：这些路径**不是不记录**，而是降级为"瞬时事件"在 UI 中折叠。
/// 事实必须留下，只是不该干扰用户判断"到底发生了什么"。
/// </summary>
public sealed class ExclusionMatcher
{
    private readonly List<(Regex Regex, string Pattern)> _fileRegexes = new();
    private readonly HashSet<string> _directoryNames;

    public ExclusionMatcher(AppSettings settings)
    {
        _directoryNames = new HashSet<string>(settings.ExcludeDirectoryNames, StringComparer.OrdinalIgnoreCase);
        foreach (var p in settings.ExcludePatterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var rx = "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            _fileRegexes.Add((new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), p));
        }
    }

    public ExclusionVerdict Check(string relativePath)
    {
        var rel = PathUtil.NormalizeRelative(relativePath);
        if (rel.Length == 0) return ExclusionVerdict.NotExcluded;

        var parts = rel.Split('/');

        // 目录名命中（任意层级，根下第一个也可能是被排除目录）
        if (_directoryNames.Count > 0)
        {
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (_directoryNames.Contains(parts[i]))
                    return new ExclusionVerdict { Excluded = true, Reason = $"位于排除目录 {parts[i]} 内", Pattern = parts[i] };
            }
        }

        var name = parts[^1];
        foreach (var (regex, pattern) in _fileRegexes)
        {
            if (regex.IsMatch(name))
                return new ExclusionVerdict { Excluded = true, Reason = $"文件名匹配排除模式 {pattern}", Pattern = pattern };
        }

        // Office / 编辑器的原子替换临时文件名（形如 ~WRD1234.tmp、.goutputstream-XXXX）
        if (name.StartsWith(".goutputstream-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("~WRL", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("~WRD", StringComparison.OrdinalIgnoreCase))
        {
            return new ExclusionVerdict { Excluded = true, Reason = "编辑器/Office 的临时文件", Pattern = "atomic-temp" };
        }

        return ExclusionVerdict.NotExcluded;
    }

    /// <summary>目录名是否被排除（用于扫描时整棵剪枝）。</summary>
    public bool IsExcludedDirectory(string name) => _directoryNames.Contains(name);
}
