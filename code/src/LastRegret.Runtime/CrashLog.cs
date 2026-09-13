using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LastRegret.Runtime;

/// <summary>
/// 崩溃日志写入（纯文件 I/O，不涉及任何 UI）。
///
/// 放在 Runtime 而不是 WPF 壳里：它是应用运行期的基础设施，
/// 未来 CLI / MCP 宿主出问题时也需要写同一份日志。
/// 谁来决定"怎么把这件事告诉用户"（弹窗、返回错误码、写 stderr）是宿主自己的事。
///
/// ── 路径脱敏（上线前体检高风险项）──
/// 这个程序每天都在操作文件系统，异常消息里**天然带着用户的真实路径**
/// （例如"无法访问 D:\私人\项目\x.docx"）。日志本身不会上传，
/// 但用户通常会原样把日志发给开发者，而不会先逐行读一遍。
/// 所以写盘之前统一把绝对路径收敛成 "D:\…\x.docx"：保留盘符与末级名字，
/// 足够定位问题，又不会把用户的整个目录树交出去。
/// </summary>
public static class CrashLog
{
    /// <summary>崩溃日志路径（与数据目录同级，方便用户找到）。</summary>
    public static string Path() =>
        System.IO.Path.Combine(AppRuntime.ResolveLogDirectory(), "crash.log");

    /// <summary>
    /// 把文本里的 Windows 绝对路径收敛成 "盘符:\根级\二级\…\末级名字"。
    ///
    /// 保留规则（刻意保留前两级，否则用户自己也没法按日志找到出问题的位置）：
    ///   D:\Users\me\Documents\x.docx  ->  D:\Users\me\…\x.docx
    ///   D:\a.txt                      ->  D:\a.txt          （本来就浅，原样保留）
    ///   D:\a\b\c\d.txt                ->  D:\a\b\…\d.txt
    ///
    /// 分隔符兼容 "\" 与 "/"。路径段字符里**必须排除空白**：
    /// 否则 "D:\a\b\x.dat 复制到 E:\c\d\y.dat" 会被当成一整条路径匹配，
    /// 第二个路径就漏掉了 —— 这是测试实际抓到的缺陷。
    /// </summary>
    public static string RedactPaths(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        const string seg = @"[^\\/:*?""<>|\s]+";
        var pattern = @"(?<drive>[A-Za-z]):[\\/](?<rest>(?:" + seg + @"[\\/])*" + seg + @")?";

        return Regex.Replace(text, pattern, m =>
        {
            var drive = m.Groups["drive"].Value.ToUpperInvariant();
            var rest = m.Groups["rest"].Value.Replace('/', '\\').TrimEnd('\\');
            if (rest.Length == 0) return drive + ":\\…";

            var parts = rest.Split('\\');
            if (parts.Length <= 3) return drive + ":\\" + rest;     // 够浅，不脱敏

            var leaf = parts[parts.Length - 1];
            return drive + ":\\" + parts[0] + "\\" + parts[1] + "\\…\\" + leaf;
        });
    }

    /// <summary>
    /// 追加一条崩溃记录。**绝不抛异常**：连日志都写不了时只能放弃，
    /// 但绝不能再抛一次导致二次崩溃。
    /// </summary>
    public static void Write(string source, Exception ex)
    {
        try
        {
            var dir = AppRuntime.ResolveLogDirectory();
            Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, "crash.log");
            var sb = new StringBuilder();
            sb.AppendLine($"──── {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ────");
            sb.AppendLine(RedactPaths(ex.ToString()));
            sb.AppendLine();
            File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception)
        {
        }
    }
}
