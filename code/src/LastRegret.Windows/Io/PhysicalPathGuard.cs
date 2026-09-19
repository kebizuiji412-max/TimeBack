using System.Runtime.InteropServices;
using LastRegret.Windows.Native;

namespace LastRegret.Windows.Io;

/// <summary>
/// 物理路径边界守卫（P0-1）。
///
/// 为什么需要它：<c>PathUtil</c> 只能证明"字符串路径仍然位于 root 之下"，
/// 不能证明 Windows 的**最终物理路径**仍位于 root 之下。例如：
///
/// <code>
/// D:\Protected\Link  --Junction--&gt;  D:\Outside
/// 逻辑路径 Link\a.txt 字符串上完全合法，实际却会操作 D:\Outside\a.txt
/// </code>
///
/// 因此凡是**可能写磁盘**的操作，在执行前都必须经过这里，并且遵循 fail-closed：
/// <b>无法证明安全 = 拒绝，绝不猜。</b>
///
/// 边界规则：
///   · root 自身如果是用户主动选择的 Junction，允许把它解析后的最终目录当作 physical root；
///   · root **以下**的任何 Reparse Point 一律不允许恢复穿过去；
///   · 已经存在的分量既要检查属性里没有 ReparsePoint，也要取最终物理路径确认仍在 physicalRoot 内；
///   · 目标不存在（恢复要新建）时，必须一路找到最近一个**已经存在的父目录**并验证它 ——
///     绝不因为"文件还不存在"就跳过边界检查；
///   · 路径比较必须是"相等 或 位于 physicalRoot + 分隔符之下"，避免 D:\Root2 被 D:\Root 误判为在内；
///   · Windows 大小写不敏感，比较用 OrdinalIgnoreCase。
/// </summary>
public static class PhysicalPathGuard
{
    /// <summary>
    /// 校验一次"要写磁盘"的目标是否仍被物理地限制在受保护范围内。
    /// </summary>
    /// <param name="rootPath">受保护范围的根（可以是它自己的 Junction 形式）。</param>
    /// <param name="relativePath">相对路径（用 / 或 \ 分隔均可）。</param>
    /// <param name="absolutePath">成功时的绝对逻辑路径。</param>
    /// <param name="error">失败原因（可直接展示给用户）。</param>
    /// <returns>true 表示**已经证明**安全；false 表示无法证明 —— 调用方必须放弃操作。</returns>
    public static bool TryValidateMutationTarget(
        string rootPath,
        string relativePath,
        out string absolutePath,
        out string? error)
    {
        absolutePath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            error = "受保护范围为空，无法确认物理边界。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "目标路径为空，无法确认物理边界。";
            return false;
        }

        // ① physical root：允许 root 自己就是一个 Junction（那是用户主动选择的位置），
        //    把它解析后的最终目录作为边界基准。
        var physicalRoot = TryGetFinalPath(rootPath);
        if (physicalRoot is null)
        {
            error = $"无法解析受保护范围的物理位置，已拒绝操作：{rootPath}";
            return false;
        }

        absolutePath = Combine(rootPath, relativePath);

        // ② 从 root 开始逐段检查"已经存在"的分量。
        var segments = SplitRelative(relativePath);
        var current = rootPath;

        // root 自身刚刚已经解析成功，它就是"最近的存在祖先"的起点：
        // 目标（乃至目标的第一段）还不存在时，验证依据就是 root 自己。
        // 这里若从 null 开始，凡是"恢复要新建的路径"都会被误拒 —— 而那正是正常恢复的主路径。
        string? nearestExistingPhysical = physicalRoot;

        foreach (var segment in segments)
        {
            current = Combine(current, segment);
            if (!Exists(current)) break;                 // 从这里开始都是"还不存在"的部分

            if (HasReparsePoint(current))
            {
                error = $"路径中包含重定向（Junction / 符号链接），已拒绝操作：{current}";
                return false;
            }

            var physical = TryGetFinalPath(current);
            if (physical is null)
            {
                error = $"无法解析该路径的物理位置，已拒绝操作：{current}";
                return false;
            }
            if (!IsSameOrUnder(physicalRoot, physical))
            {
                error = $"该路径的物理位置位于受保护范围之外，已拒绝操作：{current} → {physical}";
                return false;
            }

            nearestExistingPhysical = physical;
        }

        // ③ 目标可以不存在，但**最近的存在祖先**必须已经通过验证。
        //    目标存在时它就是自己；不存在时它就是最近的那个父目录。
        if (nearestExistingPhysical is null)
        {
            error = $"无法确认该路径仍位于受保护范围内，已拒绝操作：{absolutePath}";
            return false;
        }

        return true;
    }

    // ───────────────────────────── 内部 ─────────────────────────────

    /// <summary>取最终物理路径（已解析重定向），失败返回 null。返回值为去掉 \\?\ 前缀后的形式。</summary>
    private static string? TryGetFinalPath(string path)
    {
        var handle = Win32.CreateFileW(
            path,
            0,                                                          // 只查询属性，不请求读写权限
            unchecked((uint)(Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE)),
            IntPtr.Zero,
            unchecked((uint)Win32.OPEN_EXISTING),
            unchecked((uint)Win32.FILE_FLAG_BACKUP_SEMANTICS),          // 允许打开目录
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;

        try
        {
            var buffer = new char[512];
            var length = Win32.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) return null;

            if (length >= buffer.Length)
            {
                buffer = new char[length + 1];
                length = Win32.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                if (length == 0 || length >= buffer.Length) return null;
            }

            return NormalizeFinalPath(new string(buffer, 0, (int)length));
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    /// <summary>去掉 Win32 最终路径的 \\?\ 前缀。</summary>
    private static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string dosPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.Ordinal)) return @"\\" + path[uncPrefix.Length..];
        if (path.StartsWith(dosPrefix, StringComparison.Ordinal)) return path[dosPrefix.Length..];
        return path;
    }

    /// <summary>属性里是否带重定向标记。读不到属性时按"有风险"处理。</summary>
    private static bool HasReparsePoint(string path)
    {
        try
        {
            var attributes = (uint)File.GetAttributes(path);
            return (attributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }
        catch (Exception)
        {
            return true;    // 读不到就不放行（fail-closed）
        }
    }

    /// <summary>candidate 是否等于 root，或位于 root 之下（大小写不敏感，且不会把 D:\Root2 误判为在 D:\Root 内）。</summary>
    private static bool IsSameOrUnder(string root, string candidate)
    {
        var a = TrimEndSeparator(root);
        var b = TrimEndSeparator(candidate);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimEndSeparator(string path)
    {
        while (path.Length > 3 &&
               (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            path = path[..^1];
        }
        return path;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Combine(string left, string right) =>
        left.EndsWith(Path.DirectorySeparatorChar) || left.EndsWith(Path.AltDirectorySeparatorChar)
            ? left + right
            : left + Path.DirectorySeparatorChar + right;

    private static List<string> SplitRelative(string relativePath)
    {
        var result = new List<string>();
        foreach (var part in relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            result.Add(part);
        }
        return result;
    }
}
