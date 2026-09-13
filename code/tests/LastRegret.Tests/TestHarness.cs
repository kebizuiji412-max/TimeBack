namespace LastRegret.Tests;

/// <summary>断言失败（测试自己的异常类型，便于与"被测代码抛异常"区分）。</summary>
public sealed class AssertFailedException : Exception
{
    public AssertFailedException(string message) : base(message) { }
}

/// <summary>
/// 极简断言库。
///
/// 为什么不用 xUnit/NUnit：本机 NuGet 网络不可达（实测），
/// 任何第三方测试框架都无法还原。自研 80 行断言库足以覆盖本项目需要，
/// 且测试本身可以完全离线运行 —— 这比"用不了的好框架"更有价值。
/// </summary>
public static class Check
{
    public static void True(bool condition, string because)
    {
        if (!condition) throw new AssertFailedException($"期望为真：{because}");
    }

    public static void False(bool condition, string because)
    {
        if (condition) throw new AssertFailedException($"期望为假：{because}");
    }

    public static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertFailedException($"{because}\n  期望：{Describe(expected)}\n  实际：{Describe(actual)}");
    }

    public static void NotEqual<T>(T unexpected, T actual, string because)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new AssertFailedException($"{because}\n  不应等于：{Describe(unexpected)}");
    }

    public static void Contains(string haystack, string needle, string because)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertFailedException($"{because}\n  未在文本中找到：{needle}\n  文本片段：{Trim(haystack)}");
    }

    public static void NotNull(object? value, string because)
    {
        if (value is null) throw new AssertFailedException($"期望非 null：{because}");
    }

    public static T NotNull<T>(T? value, string because) where T : class
    {
        if (value is null) throw new AssertFailedException($"期望非 null：{because}");
        return value;
    }

    public static void Null(object? value, string because)
    {
        if (value is not null) throw new AssertFailedException($"期望为 null：{because}（实际：{Describe(value)}）");
    }

    public static void Throws<TException>(Action action, string because) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertFailedException($"{because}\n  期望异常 {typeof(TException).Name}，实际 {ex.GetType().Name}: {ex.Message}");
        }
        throw new AssertFailedException($"{because}\n  期望抛出 {typeof(TException).Name}，但没有异常");
    }

    public static void FileExists(string path, string because)
    {
        if (!File.Exists(path)) throw new AssertFailedException($"{because}\n  文件不存在：{path}");
    }

    public static void FileMissing(string path, string because)
    {
        if (File.Exists(path)) throw new AssertFailedException($"{because}\n  文件仍然存在：{path}");
    }

    public static void DirectoryExists(string path, string because)
    {
        if (!Directory.Exists(path)) throw new AssertFailedException($"{because}\n  目录不存在：{path}");
    }

    public static void DirectoryMissing(string path, string because)
    {
        if (Directory.Exists(path)) throw new AssertFailedException($"{because}\n  目录仍然存在：{path}");
    }

    public static void FileContent(string path, string expected, string because)
    {
        FileExists(path, because);
        var actual = File.ReadAllText(path);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new AssertFailedException($"{because}\n  期望内容：{Trim(expected)}\n  实际内容：{Trim(actual)}");
    }

    public static void FileHash(string path, string expectedHash, string because)
    {
        FileExists(path, because);
        var actual = LastRegret.Windows.Io.FileSystemReader.HashFileForTest(path);
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new AssertFailedException($"{because}\n  期望哈希：{expectedHash}\n  实际哈希：{actual}");
    }

    private static string Describe(object? value) => value switch
    {
        null => "(null)",
        string s => $"\"{Trim(s)}\"",
        _ => value.ToString() ?? "(null)",
    };

    private static string Trim(string? s)
    {
        if (s is null) return "(null)";
        var t = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return t.Length > 400 ? t[..400] + "…" : t;
    }
}

/// <summary>一个测试用例。</summary>
public sealed record TestCase(string Suite, string Name, Action Body)
{
    public string FullName => $"{Suite} › {Name}";
}

/// <summary>测试结果。</summary>
public sealed record TestResult(string FullName, bool Passed, string? Error, long ElapsedMs);
