using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Events;
using LastRegret.Core.Util;

namespace LastRegret.Windows.Io;

/// <summary>
/// 物理文件系统读取器：采集内容、计算哈希、流式读取。
///
/// 关键约束（全部来自实测踩坑）：
///  1. 文件可能正被别的进程独占写入 → 必须用 FileShare.ReadWrite | Delete 打开；
///  2. 长路径（&gt;260 字符）→ 自动加 <c>\\?\</c> 前缀；
///  3. 大文件绝不整体读入内存 → 流式哈希 + 大小上限保护；
///  4. 读取失败必须给出**可解释的原因**（占用/权限/不存在），不许静默返回空内容。
/// </summary>
public sealed class FileSystemReader : IFileContentReader
{
    /// <summary>单次读取到内存的硬上限（防止误读超大文件导致内存爆炸）。</summary>
    public const long HardReadLimit = 512L * 1024 * 1024;

    private readonly int _hashBufferSize;

    public FileSystemReader(int hashBufferSize = 1024 * 1024)
    {
        _hashBufferSize = Math.Clamp(hashBufferSize, 4096, 8 * 1024 * 1024);
    }

    /// <summary>Windows 长路径前缀。仅在需要时添加，避免影响普通路径的可读性。</summary>
    public static string Extend(string absolutePath)
    {
        if (absolutePath.Length < 250) return absolutePath;
        if (absolutePath.StartsWith(@"\\?\", StringComparison.Ordinal)) return absolutePath;
        if (absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + absolutePath[2..];
        return @"\\?\" + absolutePath;
    }

    public ContentCapture Capture(string absolutePath, bool isDirectory)
    {
        var capture = new ContentCapture { AbsolutePath = absolutePath, IsDirectory = isDirectory };

        if (isDirectory)
        {
            capture.Size = 0;
            capture.UnavailableReason = "目录不采集内容";
            return capture;
        }

        try
        {
            var info = new FileInfo(Extend(absolutePath));
            if (!info.Exists)
            {
                capture.UnavailableReason = "文件不存在（可能在采集前已被删除或改名）";
                return capture;
            }

            capture.Size = info.Length;
            capture.MtimeUtc = info.LastWriteTimeUtc;
            capture.IsReadOnly = info.IsReadOnly;

            var hash = TryComputeHash(absolutePath, out var error);
            if (hash is null)
            {
                capture.UnavailableReason = error ?? "读取内容失败";
                return capture;
            }

            capture.Hash = hash;
            return capture;
        }
        catch (Exception ex)
        {
            capture.UnavailableReason = Describe(ex);
            return capture;
        }
    }

    public string? TryComputeHash(string absolutePath, out string? error)
    {
        error = null;
        // 最多重试 3 次：文件可能正处于"写入中"状态
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var stream = OpenRead(absolutePath);
                using var sha = SHA256.Create();
                var buffer = ArrayPool<byte>.Shared.Rent(_hashBufferSize);
                try
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = Describe(ex);
                if (attempt < 3) Thread.Sleep(40 * attempt);
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                return null;
            }
        }
        return null;
    }

    public bool TryReadAllBytes(string absolutePath, long maxBytes, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;
        try
        {
            var info = new FileInfo(Extend(absolutePath));
            if (!info.Exists)
            {
                error = "文件不存在";
                return false;
            }
            if (info.Length > maxBytes || info.Length > HardReadLimit)
            {
                error = $"文件大小 {PathUtil.FormatBytes(info.Length)} 超过读取上限 {PathUtil.FormatBytes(Math.Min(maxBytes, HardReadLimit))}";
                return false;
            }

            using var stream = OpenRead(absolutePath);
            var length = (int)info.Length;
            var buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(buffer, offset, length - offset);
                if (read <= 0) break;
                offset += read;
            }
            if (offset != length)
            {
                Array.Resize(ref buffer, offset);
            }
            bytes = buffer;
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return false;
        }
    }

    /// <summary>哈希计算（供测试与校验复用，语义与 <see cref="TryComputeHash"/> 一致但失败即抛）。</summary>
    public static string HashFileForTest(string absolutePath)
    {
        using var stream = OpenRead(absolutePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 以"尽量不干扰其他进程"的方式打开文件。
    /// FileShare.ReadWrite | Delete 是必须的：编辑器/编译器常以独占或内存映射方式持有文件。
    /// </summary>
    public static FileStream OpenRead(string absolutePath)
    {
        return new FileStream(
            Extend(absolutePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
    }

    /// <summary>把异常翻译成用户能看懂且不夸大的原因。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException => "文件不存在",
        DirectoryNotFoundException => "所在目录不存在",
        UnauthorizedAccessException => "权限不足（无法读取）",
        IOException io when IsSharingViolation(io) => "文件正被其他进程独占使用，暂时无法读取",
        PathTooLongException => "路径过长（已尝试 \\\\?\\ 前缀仍失败）",
        IOException io => $"读取失败：{io.Message}",
        _ => $"读取失败：{ex.GetType().Name} {ex.Message}",
    };

    internal static bool IsSharingViolation(IOException io)
    {
        int code = io.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
    }

    // ─────────────────────────────────────────────────────────────────────
    // 目录扫描
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>扫描结果统计。</summary>
    public sealed class ScanStats
    {
        public int FileCount { get; set; }
        public int DirectoryCount { get; set; }
        public int SkippedCount { get; set; }
        public int ErrorCount { get; set; }
        public long TotalBytes { get; set; }
        public long ElapsedMs { get; set; }

        /// <summary>本次枚举是否做过节流（说明目录很大，耗时主要花在让出 CPU 上）。</summary>
        public bool Throttled { get; set; }

        public List<string> Errors { get; } = new();
    }

    /// <summary>
    /// 递归枚举一个受保护根下的全部条目（**流式**：枚举到一条就回调一条）。
    ///
    /// 注意：
    ///  1. 这是"建立基线"时的一次性扫描，**不是**持续的监控手段
    ///     （持续监控由 ReadDirectoryChangesW 完成，绝不做每秒全目录扫描）；
    ///  2. 必须流式处理：早期实现把全部条目先收集进列表再处理，
    ///     对 39 万文件的真实目录会产生几百 MB 的无谓内存占用；
    ///  3. <paramref name="throttleEvery"/> &gt; 0 时每处理这么多条目主动让出 CPU 片刻，
    ///     保证前台界面在长时间扫描期间仍然顺畅。
    /// </summary>
    /// <param name="rootPath">物理根路径。</param>
    /// <param name="exclusions">排除规则。</param>
    /// <param name="onEntry">对每个条目回调（物理路径、相对路径、是否目录、大小、mtime、只读）；传 null 表示只统计。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="throttleEvery">每处理多少条让出一次 CPU（0 = 不节流）。</param>
    public ScanStats Scan(
        string rootPath,
        ExclusionMatcher exclusions,
        Action<string, string, bool, long, DateTime?, bool>? onEntry = null,
        CancellationToken cancellationToken = default,
        int throttleEvery = 0)
    {
        var stats = new ScanStats();
        var sw = Stopwatch.StartNew();
        var pending = new Stack<string>();
        pending.Push(PathUtil.NormalizeRoot(rootPath));
        int sinceThrottle = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throttleEvery > 0 && ++sinceThrottle >= throttleEvery)
            {
                sinceThrottle = 0;
                stats.Throttled = true;
                // 主动让出 CPU：扫描慢一点没关系，前台界面不该被拖住
                cancellationToken.WaitHandle.WaitOne(10);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            IEnumerable<string> subdirs;
            IEnumerable<string> files;
            try
            {
                subdirs = Directory.EnumerateDirectories(Extend(dir));
                files = Directory.EnumerateFiles(Extend(dir));
            }
            catch (Exception ex)
            {
                stats.ErrorCount++;
                if (stats.Errors.Count < 100) stats.Errors.Add($"{dir} → {Describe(ex)}");
                continue;
            }

            // 子目录
            try
            {
                foreach (var sub in subdirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(sub);
                    if (exclusions.IsExcludedDirectory(name))
                    {
                        stats.SkippedCount++;
                        continue;
                    }

                    var rel = PathUtil.ToRelative(rootPath, sub);
                    if (exclusions.Check(rel).Excluded)
                    {
                        stats.SkippedCount++;
                        continue;
                    }

                    DateTime? mtime = null;
                    bool readOnly = false;
                    try
                    {
                        var di = new DirectoryInfo(Extend(sub));
                        mtime = di.LastWriteTimeUtc;
                        readOnly = di.Attributes.HasFlag(FileAttributes.ReadOnly);
                    }
                    catch (Exception ex) { stats.ErrorCount++; if (stats.Errors.Count < 100) stats.Errors.Add($"{sub} → {Describe(ex)}"); }

                    stats.DirectoryCount++;
                    onEntry?.Invoke(sub, rel, true, 0, mtime, readOnly);
                    pending.Push(sub);
                }
            }
            catch (Exception ex)
            {
                stats.ErrorCount++;
                if (stats.Errors.Count < 100) stats.Errors.Add($"{dir} → {Describe(ex)}");
            }

            // 文件
            try
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rel = PathUtil.ToRelative(rootPath, file);
                    if (exclusions.Check(rel).Excluded)
                    {
                        stats.SkippedCount++;
                        continue;
                    }

                    long size = 0;
                    DateTime? mtime = null;
                    bool readOnly = false;
                    try
                    {
                        var fi = new FileInfo(Extend(file));
                        size = fi.Length;
                        mtime = fi.LastWriteTimeUtc;
                        readOnly = fi.IsReadOnly;
                    }
                    catch (Exception ex)
                    {
                        stats.ErrorCount++;
                        if (stats.Errors.Count < 100) stats.Errors.Add($"{file} → {Describe(ex)}");
                    }

                    stats.FileCount++;
                    stats.TotalBytes += size;
                    onEntry?.Invoke(file, rel, false, size, mtime, readOnly);
                }
            }
            catch (Exception ex)
            {
                stats.ErrorCount++;
                if (stats.Errors.Count < 100) stats.Errors.Add($"{dir} → {Describe(ex)}");
            }
        }

        sw.Stop();
        stats.ElapsedMs = sw.ElapsedMilliseconds;
        return stats;
    }
}
