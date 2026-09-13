using System.Security.Cryptography;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Windows.Io;
using LastRegret.Windows.Storage;

namespace LastRegret.Engine;

/// <summary>内容落盘的诊断结果（设置页与日志用：如实展示"多少次没能保存内容"）。</summary>
public sealed class StorageDiagnostics
{
    public long ContentsStored { get; set; }
    public long ContentsDeduplicated { get; set; }
    public long ContentsSkippedTooLarge { get; set; }
    public long ContentsSkippedExcluded { get; set; }
    public long ContentsSkippedUnreadable { get; set; }

    /// <summary>因为"保护模式"而主动跳过内容的次数（只记录变化 / 智能留存的大文件）。</summary>
    public long ContentsSkippedByMode { get; set; }
    public long DiskFullEvents { get; set; }
    public long ObjectsPruned { get; set; }
    public string? LastError { get; set; }

    public long TotalAttempted => ContentsStored + ContentsSkippedTooLarge + ContentsSkippedUnreadable;

    /// <summary>去重率（越高说明"相同内容只存一份"越有效）。</summary>
    public double DedupRatio => (ContentsStored + ContentsDeduplicated) == 0
        ? 0
        : (double)ContentsDeduplicated / (ContentsStored + ContentsDeduplicated);
}

/// <summary>
/// 把"采集到的内容"落盘到 CAS，并据此构建可持久化的 <see cref="FileEvent"/>。
///
/// 关键安全约定：
///  - 任何一次内容写入失败都**不会**让整条事件丢失：事件照样记录，
///    只是 object_id 为空、并在 <see cref="FileEvent.Note"/> 中说明原因；
///  - 超过大小上限、磁盘写满、内容在读取期间被改写 —— 三种情况分别如实标注；
///  - 绝不"假装保存成功"。
/// </summary>
public sealed class ContentWriter
{
    private readonly IContentStore _store;
    private readonly AppSettings _settings;
    private readonly StorageDiagnostics _diagnostics;

    public ContentWriter(IContentStore store, AppSettings settings, StorageDiagnostics diagnostics)
    {
        _store = store;
        _settings = settings;
        _diagnostics = diagnostics;
    }

    public StorageDiagnostics Diagnostics => _diagnostics;

    public void UpdateSettings(AppSettings settings) { /* settings 字段可直接替换（只读字段除外） */ }

    /// <summary>内容对象 Id 的结果。</summary>
    public readonly record struct StoreResult(long? ObjectId, string? Hash, long? Size, string? Problem);

    /// <summary>
    /// 把一次内容采集落到 CAS。
    /// </summary>
    /// <param name="capture">采集结果（可能本身就没有内容）。</param>
    /// <param name="relativePath">相对路径（决定压缩策略）。</param>
    /// <param name="maxBytes">单文件留存上限。</param>
    public StoreResult Store(ContentCapture? capture, string relativePath, long maxBytes)
    {
        if (capture is null) return new StoreResult(null, null, null, null);

        // 目录：没有内容可存
        if (capture.IsDirectory) return new StoreResult(null, null, capture.Size, null);

        if (capture.Hash is null)
        {
            // 内容本来就不可用（例如删除前未留存）：交给事件如实说明
            return new StoreResult(null, null, capture.Size, capture.UnavailableReason);
        }

        long size = capture.Size ?? 0;

        // ── 保护模式：决定要不要留存内容 ──
        // 之所以要有这一层：对一个 39.6 万条 / 43.6GB 的目录完整留存内容，
        // 实测首次扫描要接近两小时、占用可接近源目录大小。
        // 很多用户只想防"手抖改错一个配置"，不该被迫付这个代价。
        switch (_settings.Protection)
        {
            case ProtectionMode.TrackOnly:
                _diagnostics.ContentsSkippedByMode++;
                return new StoreResult(
                    null, capture.Hash, size,
                    "当前为「只记录变化」模式：不保存历史内容，因此这个变化**无法恢复内容**（可在设置里改为「智能留存」或「完整内容」）。");

            case ProtectionMode.SmartContent when size > _settings.SmartContentMaxBytes:
                _diagnostics.ContentsSkippedByMode++;
                return new StoreResult(
                    null, capture.Hash, size,
                    $"当前为「智能留存」模式：只保存小于 {PathUtil.FormatBytes(_settings.SmartContentMaxBytes)} 的文件内容，" +
                    $"该文件为 {PathUtil.FormatBytes(size)}，仅记录变化事实，**无法恢复内容**。");
        }

        if (size > maxBytes)
        {
            _diagnostics.ContentsSkippedTooLarge++;
            return new StoreResult(
                null, capture.Hash, size,
                $"文件大小 {PathUtil.FormatBytes(size)} 超过单文件留存上限 {PathUtil.FormatBytes(maxBytes)}，仅记录变化事实，不保存历史内容。");
        }

        // 先查索引：内容相同则直接复用（这就是去重的收益点）
        var existing = _store.FindByHash(capture.Hash);
        if (existing is not null && _store.Exists(existing.Id))
        {
            _diagnostics.ContentsDeduplicated++;
            return new StoreResult(existing.Id, capture.Hash, size, null);
        }

        if (capture.AbsolutePath.Length == 0)
        {
            _diagnostics.ContentsSkippedUnreadable++;
            return new StoreResult(null, capture.Hash, size, "缺少物理路径，无法保存内容。");
        }

        var extension = PathUtil.ExtensionOf(relativePath);
        long? objectId = null;
        string? problem = null;

        // 最多重试 2 次：文件可能在写入中（哈希校验失败会抛 ContentStoreException）
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                objectId = _store.PutFile(capture.AbsolutePath, extension, out var obj, out var dedup);
                if (dedup) _diagnostics.ContentsDeduplicated++;
                else _diagnostics.ContentsStored++;

                // PutFile 之后哈希以实际内容为准（防止我们记录的哈希与对象不一致）
                if (!string.Equals(obj.Hash, capture.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    return new StoreResult(obj.Id, obj.Hash, obj.LogicalSize,
                        "文件在采集与保存之间被再次修改，已按最新内容保存。");
                }
                return new StoreResult(obj.Id, capture.Hash, size, null);
            }
            catch (ContentStoreException ex)
            {
                problem = ex.Message;
                _diagnostics.LastError = ex.Message;
                if (ex.DiskFull)
                {
                    _diagnostics.DiskFullEvents++;
                    break;
                }
                if (attempt == 2) break;
                Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                _diagnostics.LastError = ex.Message;
                break;
            }
        }

        _diagnostics.ContentsSkippedUnreadable++;
        return new StoreResult(null, capture.Hash, size, problem ?? "保存历史内容失败。");
    }

    /// <summary>把内存内容直接写入 CAS（恢复引擎的安全点使用）。</summary>
    public long? StoreBytes(byte[] content, string relativePath, out string? error)
    {
        error = null;
        try
        {
            var id = _store.Put(content, PathUtil.ExtensionOf(relativePath), out _, out var dedup);
            if (dedup) _diagnostics.ContentsDeduplicated++;
            else _diagnostics.ContentsStored++;
            return id;
        }
        catch (Exception ex)
        {
            _diagnostics.LastError = ex.Message;
            error = ex.Message;
            return null;
        }
    }

    /// <summary>把磁盘上的文件直接保存为对象（安全点使用）。</summary>
    public long? StoreExistingFile(string absolutePath, string relativePath, long maxBytes, out string? hash, out string? error)
    {
        hash = null;
        error = null;
        try
        {
            var info = new FileInfo(FileSystemReader.Extend(absolutePath));
            if (!info.Exists) { error = "文件不存在"; return null; }
            if (info.Length > maxBytes)
            {
                _diagnostics.ContentsSkippedTooLarge++;
                error = $"超过单文件留存上限 {PathUtil.FormatBytes(maxBytes)}";
                return null;
            }

            var id = _store.PutFile(absolutePath, PathUtil.ExtensionOf(relativePath), out var obj, out var dedup);
            hash = obj.Hash;
            if (dedup) _diagnostics.ContentsDeduplicated++;
            else _diagnostics.ContentsStored++;
            return id;
        }
        catch (Exception ex)
        {
            _diagnostics.LastError = ex.Message;
            error = ex.Message;
            return null;
        }
    }

    /// <summary>读取对象内容（比较/Diff 用）。</summary>
    public bool TryRead(long objectId, long maxBytes, out byte[] bytes, out string? error) =>
        _store.TryReadAllBytes(objectId, maxBytes, out bytes, out error);

    /// <summary>把对象还原到指定物理路径。</summary>
    public bool TryMaterialize(long objectId, string absolutePath, out string? error) =>
        _store.TryMaterialize(objectId, absolutePath, out error);

    public static string HashBytes(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
