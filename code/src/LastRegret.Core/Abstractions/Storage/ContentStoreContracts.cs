namespace LastRegret.Core.Abstractions;

using LastRegret.Core.Events;
using LastRegret.Core.Model;

/// <summary>
/// 文件内容读取与哈希（只读、无副作用）。
/// 与"存储"分离：这样合并器、扫描器、比较器可以共用同一套读取逻辑，
/// 也便于测试注入故障（模拟"文件正被写入"/"权限不足"）。
/// </summary>
public interface IFileContentReader
{
    ContentCapture Capture(string absolutePath, bool isDirectory);

    /// <summary>只算哈希，不读全文到内存（大文件用流式）。</summary>
    string? TryComputeHash(string absolutePath, out string? error);

    /// <summary>把文件内容读入内存（受大小上限保护）。</summary>
    bool TryReadAllBytes(string absolutePath, long maxBytes, out byte[] bytes, out string? error);
}

/// <summary>
/// 内容寻址存储（CAS）：相同内容只落一份。
/// </summary>
public interface IContentStore
{
    /// <summary>存储根目录（例如 %LOCALAPPDATA%\LastRegret\store）。</summary>
    string StoreRoot { get; }

    /// <summary>把一段内容写入 CAS（若已存在则直接复用），返回对象 Id。</summary>
    long Put(byte[] content, string? extensionHint, out StoredObject obj, out bool deduplicated);

    /// <summary>从文件流式写入 CAS（大文件不占内存）。</summary>
    long PutFile(string absolutePath, string? extensionHint, out StoredObject obj, out bool deduplicated);

    /// <summary>按哈希查对象元数据。</summary>
    StoredObject? FindByHash(string hash);

    /// <summary>按 Id 查对象元数据。</summary>
    StoredObject? FindById(long id);

    /// <summary>读取对象内容到内存。</summary>
    bool TryReadAllBytes(long objectId, long maxBytes, out byte[] bytes, out string? error);

    /// <summary>把对象内容还原到指定物理路径（原子写：临时文件 + 替换）。</summary>
    bool TryMaterialize(long objectId, string targetAbsolutePath, out string? error);

    /// <summary>对象文件是否真实存在（索引与磁盘一致性校验）。</summary>
    bool Exists(long objectId);

    /// <summary>统计磁盘占用。</summary>
    StoreUsage GetUsage();

    /// <summary>删除对象（历史清理用）。调用方必须确认没有任何快照仍然引用它。</summary>
    void Delete(long objectId);

    /// <summary>遍历所有对象（校验/清理用）。</summary>
    IEnumerable<StoredObject> EnumerateObjects();
}

/// <summary>存储占用统计。</summary>
public sealed class StoreUsage
{
    public long ObjectCount { get; set; }

    public long LogicalBytes { get; set; }

    public long StoredBytes { get; set; }

    public long DatabaseBytes { get; set; }

    /// <summary>去重节省的字节数（逻辑总量 - 实际占用）。</summary>
    public long SavedBytes => Math.Max(0, LogicalBytes - StoredBytes);

    public double DedupRatio => LogicalBytes <= 0 ? 0 : (double)StoredBytes / LogicalBytes;
}
