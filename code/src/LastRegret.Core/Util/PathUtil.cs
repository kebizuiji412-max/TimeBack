using System.Text;

namespace LastRegret.Core.Util;

/// <summary>
/// 路径规范化工具。
///
/// 全系统只存在两种路径表示，必须严格区分，禁止混用：
///  1. <b>物理绝对路径</b>（如 <c>D:\Project\src\a.cs</c>）——只用于真正访问文件系统。
///  2. <b>相对路径</b>（如 <c>src/a.cs</c>）——数据库、快照、事件、UI 一律使用这一种。
///
/// 相对路径格式约定（Windows 大小写不敏感，但**不**做小写化，以免破坏原始文件名）：
///  - 分隔符固定为 '/'；
///  - 无前导 '/'、无尾随 '/'；
///  - 不含 '.' 与 '..' 段；
///  - 空串表示"根本身"。
/// </summary>
public static class PathUtil
{
    /// <summary>把一个物理路径规范化为绝对路径（去尾随分隔符、展开相对段）。</summary>
    public static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        // 保留 "D:\" 这类根路径，否则 Path.GetFullPath 之外的代码容易把根写成 "D:"
        if (full.Length > 3 && (full.EndsWith('\\') || full.EndsWith('/')))
        {
            full = full.TrimEnd('\\', '/');
            // "D:\" -> 长度 3，保留反斜杠
            if (full.Length == 2 && full[1] == ':') full += "\\";
        }
        return full;
    }

    /// <summary>把绝对路径转为相对根目录的相对路径（'/' 分隔）。不在根下则抛异常。</summary>
    public static string ToRelative(string root, string absolutePath)
    {
        var normRoot = NormalizeRoot(root);
        var full = Path.GetFullPath(absolutePath);

        if (string.Equals(normRoot, full, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var rootWithSep = normRoot.EndsWith('\\') ? normRoot : normRoot + "\\";
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"路径不在受保护根目录内：{absolutePath}（根：{normRoot}）", nameof(absolutePath));

        return NormalizeRelative(full[rootWithSep.Length..]);
    }

    /// <summary>尝试转为相对路径；不在根下返回 false，不抛异常。</summary>
    public static bool TryToRelative(string root, string absolutePath, out string relative)
    {
        try
        {
            relative = ToRelative(root, absolutePath);
            return true;
        }
        catch (Exception)
        {
            relative = string.Empty;
            return false;
        }
    }

    /// <summary>把相对路径规范化为统一格式（'/' 分隔、无前后斜杠、无 '.'/'..' 段）。</summary>
    public static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return string.Empty;

        var s = relative.Replace('\\', '/');
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count == 0)
                    throw new ArgumentException($"相对路径越出根目录：{relative}", nameof(relative));
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        return string.Join('/', stack);
    }

    /// <summary>相对路径 → 物理绝对路径。</summary>
    public static string ToAbsolute(string root, string relative)
    {
        var normRoot = NormalizeRoot(root);
        var rel = NormalizeRelative(relative);
        if (rel.Length == 0) return normRoot;
        var sep = normRoot.EndsWith('\\') ? string.Empty : "\\";
        return normRoot + sep + rel.Replace('/', '\\');
    }

    /// <summary>相对路径的父目录（根下第一层返回空串）。</summary>
    public static string ParentOf(string relative)
    {
        var rel = NormalizeRelative(relative);
        var idx = rel.LastIndexOf('/');
        return idx < 0 ? string.Empty : rel[..idx];
    }

    /// <summary>相对路径的最后一段（文件名）。</summary>
    public static string NameOf(string relative)
    {
        var rel = NormalizeRelative(relative);
        var idx = rel.LastIndexOf('/');
        return idx < 0 ? rel : rel[(idx + 1)..];
    }

    /// <summary>相对路径的扩展名（含点，小写）。</summary>
    public static string ExtensionOf(string relative)
    {
        var name = NameOf(relative);
        var idx = name.LastIndexOf('.');
        return idx <= 0 ? string.Empty : name[idx..].ToLowerInvariant();
    }

    /// <summary>是否为根下的直接或间接子项（相对路径比较，大小写不敏感）。</summary>
    public static bool IsUnder(string ancestorRelative, string candidateRelative)
    {
        var a = NormalizeRelative(ancestorRelative);
        var c = NormalizeRelative(candidateRelative);
        if (a.Length == 0) return c.Length > 0;
        return c.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>路径比较器：Windows 语义，大小写不敏感。</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>人类可读的字节数。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB", "PB" };
        foreach (var u in units)
        {
            v /= 1024;
            if (v < 1024) return v < 10 ? $"{v:0.##} {u}" : $"{v:0.#} {u}";
        }
        return $"{v:0.#} EB";
    }

    /// <summary>把 Windows 设备路径（\Device\HarddiskVolume3\...）尽量还原为盘符路径。</summary>
    public static string DevicePathToDosPath(string devicePath, IReadOnlyDictionary<string, string> driveDeviceMap)
    {
        foreach (var (drive, device) in driveDeviceMap)
        {
            if (devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                return drive + devicePath[device.Length..];
        }
        return devicePath;
    }

    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (char.IsControl(c)) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
