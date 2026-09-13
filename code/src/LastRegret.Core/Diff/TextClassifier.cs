using System.Text;
using LastRegret.Core.Util;

namespace LastRegret.Core.Diff;

/// <summary>内容分类结果。</summary>
public sealed class ContentClassification
{
    public bool IsText { get; init; }

    /// <summary>推断的编码名（仅用于展示，例如 "UTF-8 (BOM)" / "UTF-8" / "GBK(假定)"）。</summary>
    public string EncodingName { get; init; } = "unknown";

    /// <summary>是否存在 BOM。</summary>
    public bool HasBom { get; init; }

    /// <summary>简单启发式语言/类型标记（json/xml/markdown/code/text/binary）。</summary>
    public string Category { get; init; } = "binary";

    /// <summary>为什么判定为二进制（用于 UI 如实说明）。</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// 文本/二进制判定与编码嗅探。
///
/// 为什么需要它：产品要求"文本文件优先提供 Diff，二进制文件绝不强行做文本 Diff"。
/// 判定必须有确定规则且可解释，不能靠猜。
/// </summary>
public static class TextClassifier
{
    /// <summary>用于判定的最大探测字节数。</summary>
    public const int ProbeSize = 64 * 1024;

    /// <summary>已知的二进制扩展名（快速通道）。</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".lib", ".obj", ".pdb", ".bin", ".dat", ".db", ".sqlite", ".sqlite3",
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".tar", ".cab", ".iso",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".psd", ".heic",
        ".mp3", ".mp4", ".avi", ".mkv", ".mov", ".wav", ".flac", ".ogg", ".webm", ".wmv",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".class", ".jar", ".pyc", ".pyo", ".wasm", ".o", ".a", ".node", ".pack",
        ".gguf", ".safetensors", ".onnx", ".pt", ".pth", ".ckpt",
        ".unity3d", ".assets", ".bundle", ".pak", ".blk",
    };

    /// <summary>已知的文本扩展名（快速通道，避免对代码文件做无谓嗅探）。</summary>
    private static readonly Dictionary<string, string> TextCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "json", [".jsonc"] = "json", [".json5"] = "json",
        [".xml"] = "xml", [".xaml"] = "xml", [".csproj"] = "xml", [".props"] = "xml", [".targets"] = "xml",
        [".config"] = "xml", [".svg"] = "xml", [".resx"] = "xml", [".plist"] = "xml",
        [".md"] = "markdown", [".markdown"] = "markdown", [".mdx"] = "markdown",
        [".cs"] = "code", [".fs"] = "code", [".vb"] = "code", [".java"] = "code", [".kt"] = "code",
        [".c"] = "code", [".h"] = "code", [".cpp"] = "code", [".hpp"] = "code", [".cc"] = "code",
        [".rs"] = "code", [".go"] = "code", [".py"] = "code", [".rb"] = "code", [".php"] = "code",
        [".js"] = "code", [".jsx"] = "code", [".ts"] = "code", [".tsx"] = "code", [".mjs"] = "code", [".cjs"] = "code",
        [".vue"] = "code", [".svelte"] = "code", [".lua"] = "code", [".sh"] = "code", [".ps1"] = "code",
        [".psm1"] = "code", [".psd1"] = "code", [".bat"] = "code", [".cmd"] = "code", [".sql"] = "code",
        [".css"] = "code", [".scss"] = "code", [".less"] = "code", [".html"] = "code", [".htm"] = "code",
        [".yml"] = "yaml", [".yaml"] = "yaml", [".toml"] = "toml", [".ini"] = "ini", [".cfg"] = "ini",
        [".conf"] = "ini", [".properties"] = "ini", [".env"] = "ini", [".editorconfig"] = "ini",
        [".txt"] = "text", [".log"] = "text", [".csv"] = "text", [".tsv"] = "text", [".gitignore"] = "text",
        [".gitattributes"] = "text", [".npmrc"] = "text", [".dockerignore"] = "text", [".patch"] = "text",
        [".diff"] = "text", [".sln"] = "text", [".gradle"] = "code", [".lock"] = "text",
    };

    /// <summary>根据文件头字节（可含路径线索）判定内容类型。</summary>
    public static ContentClassification Classify(ReadOnlySpan<byte> head, string? relativePath, long totalSize)
    {
        var ext = relativePath is null ? string.Empty : PathUtil.ExtensionOf(relativePath);

        // BOM 优先：BOM 是最强的证据
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            return new ContentClassification { IsText = true, HasBom = true, EncodingName = "UTF-8 (BOM)", Category = CategoryOf(ext) };
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            return new ContentClassification { IsText = true, HasBom = true, EncodingName = "UTF-16 LE (BOM)", Category = CategoryOf(ext) };
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            return new ContentClassification { IsText = true, HasBom = true, EncodingName = "UTF-16 BE (BOM)", Category = CategoryOf(ext) };

        // 空文件：按文本处理（Diff 结果是"空 ↔ 空/有内容"，完全可解释）
        if (totalSize == 0)
            return new ContentClassification { IsText = true, EncodingName = "empty", Category = CategoryOf(ext) };

        if (BinaryExtensions.Contains(ext))
            return new ContentClassification { IsText = false, EncodingName = "binary", Category = "binary", Reason = $"扩展名 {ext} 属于已知二进制类型" };

        var probe = head.Length > ProbeSize ? head[..ProbeSize] : head;

        // 二进制签名（魔术字节）
        if (IsKnownBinarySignature(probe, out var sig))
            return new ContentClassification { IsText = false, EncodingName = "binary", Category = "binary", Reason = $"文件头匹配二进制签名：{sig}" };

        // NUL 字节：强二进制信号
        int nulCount = 0;
        foreach (var b in probe) if (b == 0) nulCount++;
        if (nulCount > 0)
        {
            return new ContentClassification
            {
                IsText = false,
                EncodingName = "binary",
                Category = "binary",
                Reason = $"内容含 {nulCount} 个 NUL 字节（判定为二进制）",
            };
        }

        // UTF-8 合法性
        bool validUtf8 = IsValidUtf8(probe, out double replacementRatio);

        // 控制字符比例（允许 \t \n \r \f）
        int control = 0;
        foreach (var b in probe)
        {
            if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D && b != 0x0C) control++;
        }
        double controlRatio = probe.Length == 0 ? 0 : (double)control / probe.Length;

        if (controlRatio > 0.10)
        {
            return new ContentClassification
            {
                IsText = false,
                EncodingName = "binary",
                Category = "binary",
                Reason = $"控制字符占比 {controlRatio:P1}，超过 10% 阈值",
            };
        }

        if (validUtf8)
        {
            return new ContentClassification { IsText = true, EncodingName = "UTF-8", Category = CategoryOf(ext) };
        }

        // 非 UTF-8 但控制字符很少（典型的中文 GBK/GB18030 文本文件）
        return new ContentClassification
        {
            IsText = true,
            EncodingName = "GBK/ANSI(假定)",
            Category = CategoryOf(ext),
            Reason = "非 UTF-8 编码，但控制字符很少；按本地 ANSI 文本处理，可能显示为乱码",
        };
    }

    private static string CategoryOf(string ext) =>
        TextCategories.TryGetValue(ext, out var c) ? c : (ext.Length == 0 ? "text" : "text");

    private static bool IsKnownBinarySignature(ReadOnlySpan<byte> b, out string signature)
    {
        signature = string.Empty;
        if (b.Length >= 2 && b[0] == 0x4D && b[1] == 0x5A) { signature = "MZ (PE 可执行)"; return true; }
        if (b.Length >= 4 && b[0] == 0x7F && b[1] == 0x45 && b[2] == 0x4C && b[3] == 0x46) { signature = "ELF"; return true; }
        if (b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && (b[2] == 0x03 || b[2] == 0x05 || b[2] == 0x07)) { signature = "ZIP/DOCX/JAR"; return true; }
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) { signature = "PNG"; return true; }
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) { signature = "JPEG"; return true; }
        if (b.Length >= 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) { signature = "PDF"; return true; }
        if (b.Length >= 4 && b[0] == 0x53 && b[1] == 0x51 && b[2] == 0x4C && b[3] == 0x69) { signature = "SQLite"; return true; }
        if (b.Length >= 6 && b[0] == 0x37 && b[1] == 0x7A && b[2] == 0xBC && b[3] == 0xAF) { signature = "7z"; return true; }
        if (b.Length >= 4 && b[0] == 0x1F && b[1] == 0x8B) { signature = "gzip"; return true; }
        if (b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF) { signature = "OLE (旧 Office)"; return true; }
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46) { signature = "RIFF"; return true; }
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) { signature = "ICO"; return true; }
        if (b.Length >= 4 && b[0] == 0x42 && b[1] == 0x4D) { signature = "BMP"; return true; }
        return false;
    }

    /// <summary>严格 UTF-8 校验（按字节逐步解析，不依赖已注册的 EncodingProvider）。</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> data, out double invalidRatio)
    {
        int i = 0;
        int invalid = 0;
        int total = 0;
        while (i < data.Length)
        {
            total++;
            byte c = data[i];
            if (c < 0x80) { i++; continue; }

            int extra;
            int min;
            int cp;
            if ((c & 0xE0) == 0xC0) { extra = 1; min = 0x80; cp = c & 0x1F; }
            else if ((c & 0xF0) == 0xE0) { extra = 2; min = 0x800; cp = c & 0x0F; }
            else if ((c & 0xF8) == 0xF0) { extra = 3; min = 0x10000; cp = c & 0x07; }
            else { invalid++; i++; continue; }

            if (i + extra >= data.Length) { invalid++; break; }

            bool ok = true;
            for (int k = 1; k <= extra; k++)
            {
                byte cc = data[i + k];
                if ((cc & 0xC0) != 0x80) { ok = false; break; }
                cp = (cp << 6) | (cc & 0x3F);
            }
            if (!ok || cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
            {
                invalid++;
                i++;
                continue;
            }
            i += extra + 1;
        }
        invalidRatio = total == 0 ? 0 : (double)invalid / total;
        return invalid == 0;
    }

    /// <summary>用最强证据解码文本：BOM → UTF-8 → ANSI 回退。绝不静默产生乱码字符。</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, ContentClassification classification)
    {
        if (classification.HasBom)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes[3..]);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes[2..]);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (classification.EncodingName.StartsWith("UTF-8", StringComparison.Ordinal))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);
        }

        // ANSI / GBK：.NET Core 默认不带代码页编码器；用 Latin-1 逐字节映射保证
        // "不丢字节、不抛异常"，字符可能显示为乱码但不会伪装成正确的中文。
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
