using System.IO.Compression;
using System.Security.Cryptography;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Windows.Storage;

/// <summary>
/// 触发一次存储错误（磁盘满 / 校验失败）。上层据此进入"只记录事实、不保存内容"的降级模式。
/// </summary>
public sealed class ContentStoreException : Exception
{
    public bool DiskFull { get; }

    public ContentStoreException(string message, bool diskFull = false, Exception? inner = null)
        : base(message, inner)
    {
        DiskFull = diskFull;
    }
}

/// <summary>
/// 内容寻址存储（Content-Addressed Storage）。
///
/// 核心保证：
///  1. **相同内容只存一份**：以 SHA-256 为主键；索引里已有且磁盘文件存在 → 直接复用。
///  2. **写入原子**：先写临时文件 → fsync → 校验哈希 → 原子改名到最终路径。
///     断电最坏情况只留下一个临时文件，绝不会有"半个对象"被当成有效内容。
///  3. **索引可重建**：磁盘上的对象文件是权威；索引损坏/丢失可重新扫描建立
///     （<see cref="RebuildIndex"/>），因此索引不会成为单点故障。
///  4. **校验**：读取时可选择做哈希校验，发现损坏立即报告而不是静默返回错内容。
/// </summary>
public sealed class ContentStore : IContentStore, IDisposable
{
    /// <summary>小于该长度的内容不压缩（压缩收益低于元数据开销）。</summary>
    public const int CompressionThreshold = 4096;

    /// <summary>可尝试压缩的扩展名（文本类）。二进制压缩收益不稳定，本版不压。</summary>
    private static readonly HashSet<string> CompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".json", ".xml", ".csv", ".tsv", ".md", ".markdown", ".yml", ".yaml",
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".c", ".h", ".cpp", ".hpp", ".rs",
        ".go", ".rb", ".php", ".lua", ".sh", ".ps1", ".bat", ".cmd", ".sql", ".css", ".scss",
        ".html", ".htm", ".ini", ".cfg", ".conf", ".toml", ".props", ".targets", ".xaml", ".svg",
    };

    private readonly SqliteConnection _db;
    private readonly string _storeRoot;
    private readonly string _objectsDir;
    private readonly string _tempDir;
    private readonly object _gate = new();
    private readonly bool _compressionEnabled;
    private bool _disposed;

    public ContentStore(SqliteConnection objectsDb, string storeRoot, bool compressionEnabled = true)
    {
        _db = objectsDb ?? throw new ArgumentNullException(nameof(objectsDb));
        _storeRoot = PathUtil.NormalizeRoot(storeRoot);
        _objectsDir = Path.Combine(_storeRoot, "objects");
        _tempDir = Path.Combine(_storeRoot, "tmp");
        _compressionEnabled = compressionEnabled;

        Directory.CreateDirectory(_objectsDir);
        Directory.CreateDirectory(_tempDir);
    }

    public string StoreRoot => _storeRoot;

    public string ObjectsDirectory => _objectsDir;

    // ─────────────────────────────────────────────────────────────────────
    // 写入
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>一次写入的结果。</summary>
    private readonly record struct PutResult(long Id, StoredObject Object, bool Deduplicated);

    public long Put(byte[] content, string? extensionHint, out StoredObject obj, out bool deduplicated)
    {
        ArgumentNullException.ThrowIfNull(content);
        obj = null!;
        deduplicated = false;
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        try
        {
            var result = PutCore(hash, content.LongLength, extensionHint, target =>
            {
                using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough);
                fs.Write(content, 0, content.Length);
                fs.Flush(flushToDisk: true);
            }, () => content);
            obj = result.Object;
            deduplicated = result.Deduplicated;
            return result.Id;
        }
        finally
        {
            obj ??= new StoredObject { Hash = hash };
        }
    }

    public long PutFile(string absolutePath, string? extensionHint, out StoredObject obj, out bool deduplicated)
    {
        obj = null!;
        deduplicated = false;

        // 第一步：流式算哈希（不把文件读进内存）
        using var hashStream = Io.FileSystemReader.OpenRead(absolutePath);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(hashStream)).ToLowerInvariant();

        long length;
        try
        {
            length = new FileInfo(Io.FileSystemReader.Extend(absolutePath)).Length;
        }
        catch (Exception ex)
        {
            throw new ContentStoreException($"无法读取文件大小：{Io.FileSystemReader.Describe(ex)}", false, ex);
        }

        try
        {
            _currentSourcePath = absolutePath;
            var result = PutCore(hash, length, extensionHint, target =>
            {
                // 第二步：流式复制到临时文件（复制期间内容若被改写，哈希校验会失败并重试）
                using var src = Io.FileSystemReader.OpenRead(absolutePath);
                using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.WriteThrough);
                src.CopyTo(dst, 256 * 1024);
                dst.Flush(flushToDisk: true);
            }, null);
            obj = result.Object;
            deduplicated = result.Deduplicated;
            return result.Id;
        }
        finally
        {
            _currentSourcePath = null;
            obj ??= new StoredObject { Hash = hash };
        }
    }

    private PutResult PutCore(string hash, long logicalSize, string? extensionHint, Action<string> writeRaw, Func<byte[]>? rawProvider)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            // 1) 索引命中 + 磁盘文件存在 → 直接复用（这就是"内容去重"）
            var existing = FindByHashNoLock(hash);
            if (existing is not null && File.Exists(ObjectPath(hash)))
            {
                TouchNoLock(existing.Id);
                return new PutResult(existing.Id, existing, true);
            }

            // 2) 需要真正落盘。写临时文件 → 校验 → 原子改名。
            var finalPath = ObjectPath(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            string tempPath = Path.Combine(_tempDir, $"{hash}.{Guid.NewGuid():N}.part");
            bool compress = _compressionEnabled && logicalSize >= CompressionThreshold && IsCompressible(extensionHint);
            long storedSize;

            try
            {
                if (compress)
                {
                    // ⚠ 踩坑记录（PIT）：压缩写必须用 **ZLibStream**，不能用 DeflateStream。
                    //   DeflateStream.Write 产出的是**原始 deflate**（无 zlib 头），
                    //   而 DeflateStream.Read 期望**带 zlib 包装**的数据，
                    //   于是"写进去的内容永远读不回来"（读取时报
                    //   "The archive entry was compressed using an unsupported compression method"）。
                    //   更隐蔽的是：这条路径只在"启用压缩 + 内容 ≥ 4KB + 文本类扩展名"时才走到，
                    //   平时用不到，直到测试拿真实文件走完整链路才暴露。
                    using var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough);
                    using (var zlib = new ZLibStream(fs, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        if (rawProvider is not null)
                        {
                            var bytes = rawProvider();
                            zlib.Write(bytes, 0, bytes.Length);
                        }
                        else
                        {
                            CopySourceTo(zlib);
                        }
                    }
                    fs.Flush(flushToDisk: true);
                    storedSize = fs.Length;
                }
                else
                {
                    writeRaw(tempPath);
                    storedSize = new FileInfo(tempPath).Length;
                }

                // 3) 校验：确认写出的内容与声明一致（防并发写入 / 磁盘故障）
                string actualHash = HashFile(tempPath, compress);
                if (!string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    // 源文件在读取期间被改写：诚实报告，由上层在下一轮事件里重新采集
                    TryDelete(tempPath);
                    throw new ContentStoreException(
                        $"内容在读取期间发生变化（期望 {hash[..12]}…，实际 {actualHash[..12]}…）。本次不保存该版本，等待下一次事件重试。");
                }

                // 4) 原子改名到最终路径
                try
                {
                    File.Move(tempPath, finalPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    // 另一个写者刚刚写好了同一个对象：直接用它，丢弃自己的临时文件
                    TryDelete(tempPath);
                    var raced = FindByHashNoLock(hash);
                    if (raced is not null)
                    {
                        return new PutResult(raced.Id, raced, true);
                    }
                }
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                TryDelete(tempPath);
                throw new ContentStoreException(
                    "磁盘空间不足，无法保存历史内容（已跳过的内容会在事件中明确标记为不可恢复）", diskFull: true, ex);
            }
            catch (ContentStoreException)
            {
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                throw new ContentStoreException($"保存历史内容失败：{ex.Message}", false, ex);
            }

            // 5) 写索引
            long id = 0;
            var now = DateTime.UtcNow.Ticks;
            try
            {
                // ⚠ BB-005：INSERT 与取回自增 Id 放在同一事务里，
                // 否则并发写入（监听线程与恢复线程同时存内容）时这里可能读到别人的 Id。
                _db.InTransaction(() =>
                {
                    _db.NonQuery(
                        "INSERT INTO objects(hash, logical_size, stored_size, encoding, ext_hint, created_utc, ref_write_utc) " +
                        "VALUES (?,?,?,?,?,?,?);",
                        hash, logicalSize, storedSize, compress ? "deflate" : "raw", extensionHint, now, now);
                    id = _db.LastInsertRowId();
                });
            }
            catch (SqliteException)
            {
                // 索引写入失败（例如唯一约束竞争）时回读既有行
                var raced = FindByHashNoLock(hash);
                if (raced is not null)
                {
                    return new PutResult(raced.Id, raced, true);
                }
                throw;
            }

            var created = new StoredObject
            {
                Id = id,
                Hash = hash,
                LogicalSize = logicalSize,
                StoredSize = storedSize,
                Encoding = compress ? "deflate" : "raw",
                ExtensionHint = extensionHint,
            };
            return new PutResult(id, created, false);
        }
    }

    private static bool IsCompressible(string? extensionHint) =>
        extensionHint is not null && CompressibleExtensions.Contains(extensionHint);

    /// <summary>把源文件流式复制到目标流（大文件不占内存）。</summary>
    private void CopySourceTo(Stream destination)
    {
        using var src = Io.FileSystemReader.OpenRead(_currentSourcePath!);
        src.CopyTo(destination, 256 * 1024);
    }

    /// <summary>当前正在写入的源文件路径（仅 <see cref="PutFile"/> 期间有效）。</summary>
    [ThreadStatic]
    private static string? _currentSourcePathThreadStatic;

    private string? _currentSourcePath
    {
        get => _currentSourcePathThreadStatic;
        set => _currentSourcePathThreadStatic = value;
    }

    private static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) == 112 /*ERROR_DISK_FULL*/ || (ex.HResult & 0xFFFF) == 39 /*ERROR_HANDLE_DISK_FULL*/;

    // ─────────────────────────────────────────────────────────────────────
    // 查询
    // ─────────────────────────────────────────────────────────────────────

    public StoredObject? FindByHash(string hash)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return FindByHashNoLock(hash);
        }
    }

    private StoredObject? FindByHashNoLock(string hash)
    {
        StoredObject? result = null;
        _db.QueryFirst(
            "SELECT id, hash, logical_size, stored_size, encoding, ext_hint FROM objects WHERE hash = ? LIMIT 1;",
            new object?[] { hash },
            row =>
            {
                result = new StoredObject
                {
                    Id = row.GetInt64("id"),
                    Hash = row.GetString("hash"),
                    LogicalSize = row.GetInt64("logical_size"),
                    StoredSize = row.GetInt64("stored_size"),
                    Encoding = row.GetString("encoding"),
                    ExtensionHint = row.GetStringOrNull("ext_hint"),
                };
            });
        return result;
    }

    public StoredObject? FindById(long id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            StoredObject? result = null;
            _db.QueryFirst(
                "SELECT id, hash, logical_size, stored_size, encoding, ext_hint FROM objects WHERE id = ? LIMIT 1;",
                new object?[] { id },
                row =>
                {
                    result = new StoredObject
                    {
                        Id = row.GetInt64("id"),
                        Hash = row.GetString("hash"),
                        LogicalSize = row.GetInt64("logical_size"),
                        StoredSize = row.GetInt64("stored_size"),
                        Encoding = row.GetString("encoding"),
                        ExtensionHint = row.GetStringOrNull("ext_hint"),
                    };
                });
            return result;
        }
    }

    private void TouchNoLock(long id)
    {
        try
        {
            _db.NonQuery("UPDATE objects SET ref_write_utc = ? WHERE id = ?;", DateTime.UtcNow.Ticks, id);
        }
        catch (SqliteException)
        {
            // 更新时间戳失败不影响正确性
        }
    }

    public bool Exists(long objectId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var obj = FindById(objectId);
            return obj is not null && File.Exists(ObjectPath(obj.Hash));
        }
    }

    public bool TryReadAllBytes(long objectId, long maxBytes, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;
        try
        {
            var obj = FindById(objectId);
            if (obj is null) { error = "对象索引中不存在该内容"; return false; }
            if (obj.LogicalSize > maxBytes) { error = $"内容大小 {PathUtil.FormatBytes(obj.LogicalSize)} 超过上限"; return false; }

            var path = ObjectPath(obj.Hash);
            if (!File.Exists(path)) { error = "内容文件已丢失（可能已被历史清理）"; return false; }

            if (obj.Encoding == "deflate")
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var deflate = new ZLibStream(fs, CompressionMode.Decompress);
                using var ms = new MemoryStream(obj.LogicalSize > 0 && obj.LogicalSize < int.MaxValue ? (int)obj.LogicalSize : 0);
                deflate.CopyTo(ms);
                bytes = ms.ToArray();
            }
            else
            {
                bytes = File.ReadAllBytes(path);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public string ObjectPath(string hash)
    {
        if (hash.Length < 3) throw new ArgumentException("哈希长度不足", nameof(hash));
        var prefix = hash[..2];
        return Path.Combine(_objectsDir, prefix, hash);
    }

    private static string HashFile(string path, bool compressed)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        if (!compressed)
        {
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
        using var deflate = new ZLibStream(fs, CompressionMode.Decompress);
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    // ─────────────────────────────────────────────────────────────────────
    // 还原到磁盘（恢复引擎的核心动作）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 CAS 里的对象内容写回磁盘上的目标路径（恢复引擎的核心动作）。
    ///
    /// 分三步，且**每一步的错误都如实归因**（本轮修复，FINAL-WB-002）：
    ///   ① 取对象元数据 → 写临时文件（同目录，保证同卷）；
    ///   ② 校验写出的内容哈希（防磁盘故障导致的静默损坏）；
    ///   ③ 原位替换目标。
    ///
    /// ⚠ 真实缺陷：原实现把所有失败都压成一句 <c>读取失败：{ex.Message}</c>，
    ///   于是"替换目标失败"（例如目标带加密/只读等属性导致替换不被允许）也被说成
    ///   **"读取失败：无法加密指定的文件 … text.txt.lrtmp-*"** —— 错误信息指向了错误的对象、
    ///   也指向了错误的动作，让人以为是读目标文件失败。现在按阶段分别说明。
    /// ⚠ 另一个真实缺陷：失败时**不清理临时文件**（原实现只有"校验失败"与
    ///   <c>ReplaceFile</c> 的 IOException 分支清理），会在用户目录里留下 <c>.lrtmp-*</c> 残留。
    ///   现在无论在哪一步失败都清理，绝不在用户目录里留垃圾。
    /// </summary>
    public bool TryMaterialize(long objectId, string targetAbsolutePath, out string? error)
    {
        error = null;
        string? tempPath = null;
        string? tempExtended = null;
        try
        {
            var obj = FindById(objectId);
            if (obj is null) { error = "对象索引中不存在该内容"; return false; }

            var objectPath = ObjectPath(obj.Hash);
            if (!File.Exists(objectPath)) { error = "内容文件已丢失（可能已被历史清理）"; return false; }

            var dir = Path.GetDirectoryName(targetAbsolutePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(Io.FileSystemReader.Extend(dir));

            // ① 原子写：临时文件 + 替换。同目录内保证同卷，移动才是原子的。
            tempPath = targetAbsolutePath + $".lrtmp-{Guid.NewGuid():N}";
            tempExtended = Io.FileSystemReader.Extend(tempPath);
            try
            {
                MaterializeToTemp(obj, objectPath, tempExtended);
            }
            catch (Exception ex)
            {
                error = "读取历史内容失败：" + Io.FileSystemReader.Describe(ex);
                return false;
            }

            // ② 校验写出的内容（防止磁盘故障导致的静默损坏）
            var writtenHash = HashFile(tempExtended, compressed: false);
            if (!string.Equals(writtenHash, obj.Hash, StringComparison.OrdinalIgnoreCase))
            {
                error = $"还原后校验失败（期望 {obj.Hash[..12]}…，实际 {writtenHash[..12]}…），已放弃本次写入";
                return false;
            }

            // ③ 原位替换。目标存在时用 File.Replace（保留目标文件的属性/安全描述符），
            //    否则用 File.Move（新建）。两者都会把内容原子地换过去，绝不"先删后写"。
            //
            //    替换前**兜底清掉加密标记**：即便将来有别的路径把 FILE_ATTRIBUTE_ENCRYPTED
            //    带到了临时文件上，也不能让"还原一个普通文本文件"因为它而失败（FINAL-WB-002）。
            //    清不掉也不在这里阻断 —— 真正的正确性由下面的替换结果与调用方校验负责。
            var tempAttrs = File.GetAttributes(tempExtended);
            if ((tempAttrs & FileAttributes.Encrypted) != 0)
            {
                try { File.SetAttributes(tempExtended, tempAttrs & ~FileAttributes.Encrypted); }
                catch (Exception) { /* 清不掉就原样继续，不制造新的失败点 */ }
            }

            var targetExtended = Io.FileSystemReader.Extend(targetAbsolutePath);
            try
            {
                if (File.Exists(targetExtended)) File.Replace(tempExtended, targetExtended, null);
                else File.Move(tempExtended, targetExtended, overwrite: true);
            }
            catch (Exception ex)
            {
                error = $"替换目标文件失败（{Io.FileSystemReader.Describe(ex)}）。已保留原文件，未做任何覆盖。";
                return false;
            }

            tempPath = null;    // 已成功改名，不需要再清理
            return true;
        }
        catch (Exception ex)
        {
            error = Io.FileSystemReader.Describe(ex);
            return false;
        }
        finally
        {
            // 只要临时文件还在，就说明这次写入没有成功落地 —— 一律清理，不留残留
            if (tempPath is not null) TryDelete(tempExtended ?? tempPath);
        }
    }

    /// <summary>
    /// 把对象内容（可能 deflate 压缩）写到临时文件。
    ///
    /// ⚠ 真实缺陷（本轮修复，FINAL-WB-002 —— "合法 restore 完全失败"）：
    ///   原实现对小文件走 <c>File.Copy(objectPath, tempExtended)</c>。
    ///   而 **File.Copy 会把源文件的属性一起复制到目标**，包括
    ///   <c>FILE_ATTRIBUTE_ENCRYPTED</c>（EFS 加密标记）——
    ///   内容库里的对象一旦带上这个标记（应用数据目录在 C: 且被标记为加密新文件时就会继承），
    ///   还原时那个写向目标目录的 <c>&lt;名字&gt;.lrtmp-*</c> 临时文件就会被系统要求加密，
    ///   在 EFS 不可用的机器上直接失败：**"无法加密指定的文件 … text.txt.lrtmp-*"**。
    ///   这不是"读历史内容失败"，而是"复制把加密属性带过去了"。
    ///
    ///   现在一律用**字节流复制**：只搬内容，不搬属性 ——
    ///   临时文件的属性由它所在的（用户目标）目录决定，跨卷同卷都一样安全。
    /// </summary>
    private static void MaterializeToTemp(StoredObject obj, string objectPath, string tempExtended)
    {
        using var src = new FileStream(objectPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = new FileStream(tempExtended, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.WriteThrough);

        if (obj.Encoding == "deflate")
        {
            using var deflate = new ZLibStream(src, CompressionMode.Decompress);
            deflate.CopyTo(dst, 256 * 1024);
        }
        else
        {
            src.CopyTo(dst, 256 * 1024);
        }

        dst.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* 清理失败不影响主流程 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 统计 / 维护
    // ─────────────────────────────────────────────────────────────────────

    public StoreUsage GetUsage()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var usage = new StoreUsage();
            _db.QueryFirst(
                "SELECT COUNT(*) AS n, COALESCE(SUM(logical_size),0) AS l, COALESCE(SUM(stored_size),0) AS s FROM objects;",
                Array.Empty<object?>(),
                row =>
                {
                    usage.ObjectCount = row.GetInt64("n");
                    usage.LogicalBytes = row.GetInt64("l");
                    usage.StoredBytes = row.GetInt64("s");
                });

            try
            {
                var dbPath = _db.Path;
                if (File.Exists(dbPath)) usage.DatabaseBytes = new FileInfo(dbPath).Length;
                var wal = dbPath + "-wal";
                if (File.Exists(wal)) usage.DatabaseBytes += new FileInfo(wal).Length;
            }
            catch (IOException) { /* 统计失败不致命 */ }

            return usage;
        }
    }

    public IEnumerable<StoredObject> EnumerateObjects()
    {
        var list = new List<StoredObject>();
        lock (_gate)
        {
            ThrowIfDisposed();
            _db.Query(
                "SELECT id, hash, logical_size, stored_size, encoding, ext_hint FROM objects ORDER BY id;",
                Array.Empty<object?>(),
                row => list.Add(new StoredObject
                {
                    Id = row.GetInt64("id"),
                    Hash = row.GetString("hash"),
                    LogicalSize = row.GetInt64("logical_size"),
                    StoredSize = row.GetInt64("stored_size"),
                    Encoding = row.GetString("encoding"),
                    ExtensionHint = row.GetStringOrNull("ext_hint"),
                }));
        }
        return list;
    }

    public void Delete(long objectId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var obj = FindById(objectId);
            if (obj is null) return;
            var path = ObjectPath(obj.Hash);
            TryDelete(path);
            _db.NonQuery("DELETE FROM objects WHERE id = ?;", objectId);
        }
    }

    /// <summary>
    /// 重新扫描对象目录重建索引（索引损坏/丢失时的恢复手段）。
    /// 磁盘上的对象文件是权威事实。
    /// </summary>
    public int RebuildIndex(Action<string>? progress = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            int count = 0;
            _db.InTransaction(() =>
            {
                _db.NonQuery("DELETE FROM objects;");
                foreach (var file in Directory.EnumerateFiles(_objectsDir, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (name.Length != 64 || name.Contains('.')) continue; // 只认哈希命名的对象

                    try
                    {
                        long storedSize = new FileInfo(file).Length;
                        long logicalSize;
                        string encoding;

                        // 尝试按压缩解读；解出的哈希若不等于文件名，则视为未压缩
                        string rawHash;
                        try
                        {
                            rawHash = HashFile(file, compressed: true);
                            if (string.Equals(rawHash, name, StringComparison.OrdinalIgnoreCase))
                            {
                                encoding = "deflate";
                                logicalSize = CountDecompressedLength(file);
                            }
                            else
                            {
                                throw new InvalidDataException("not deflate");
                            }
                        }
                        catch (Exception)
                        {
                            encoding = "raw";
                            logicalSize = storedSize;
                            rawHash = HashFile(file, compressed: false);
                            if (!string.Equals(rawHash, name, StringComparison.OrdinalIgnoreCase))
                            {
                                progress?.Invoke($"跳过损坏对象：{file}（内容哈希与文件名不一致）");
                                continue;
                            }
                        }

                        _db.NonQuery(
                            "INSERT OR REPLACE INTO objects(hash, logical_size, stored_size, encoding, ext_hint, created_utc, ref_write_utc) " +
                            "VALUES (?,?,?,?,NULL,?,?);",
                            name, logicalSize, storedSize, encoding, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        progress?.Invoke($"跳过无法读取的对象：{file} → {ex.Message}");
                    }
                }
            });
            return count;
        }
    }

    private static long CountDecompressedLength(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var deflate = new ZLibStream(fs, CompressionMode.Decompress);
        var buffer = new byte[256 * 1024];
        long total = 0;
        int read;
        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0) total += read;
        return total;
    }

    /// <summary>校验所有对象（抽样或全量）。返回损坏对象列表。</summary>
    public IReadOnlyList<(string Hash, string Problem)> Verify(bool full = false, int sample = 200)
    {
        var problems = new List<(string, string)>();
        var objects = EnumerateObjects().ToList();
        var toCheck = full ? objects : objects.OrderByDescending(o => o.StoredSize).Take(sample).ToList();

        foreach (var obj in toCheck)
        {
            var path = ObjectPath(obj.Hash);
            if (!File.Exists(path))
            {
                problems.Add((obj.Hash, "对象文件缺失"));
                continue;
            }
            try
            {
                var actual = HashFile(path, obj.Encoding == "deflate");
                if (!string.Equals(actual, obj.Hash, StringComparison.OrdinalIgnoreCase))
                    problems.Add((obj.Hash, $"内容损坏（实际哈希 {actual[..12]}…）"));
            }
            catch (Exception ex)
            {
                problems.Add((obj.Hash, $"无法读取：{ex.Message}"));
            }
        }
        return problems;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ContentStore));
    }

    /// <summary>删除遗留的临时文件（崩溃后可能残留）。</summary>
    public int CleanupTempFiles(TimeSpan olderThan)
    {
        int removed = 0;
        try
        {
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*.part"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        File.Delete(f);
                        removed++;
                    }
                }
                catch (IOException) { /* 仍被占用，下次再清 */ }
            }
        }
        catch (DirectoryNotFoundException) { /* 目录不存在时无事可做 */ }
        return removed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}
