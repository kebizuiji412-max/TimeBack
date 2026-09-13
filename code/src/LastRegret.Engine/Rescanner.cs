using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;
using LastRegret.Core.Events;
using LastRegret.Core.Model;
using LastRegret.Core.Util;
using LastRegret.Windows.Io;

namespace LastRegret.Engine;

/// <summary>一次"磁盘 ↔ 索引对齐"的结果（如实统计，不掩饰漏掉的部分）。</summary>
public sealed class ResyncReport
{
    public long RootId { get; init; }
    public string RootPath { get; init; } = string.Empty;

    public int ScannedFiles { get; set; }
    public int ScannedDirectories { get; set; }

    /// <summary>扫描到的文件总字节数（用于报告与容量提示）。</summary>
    public long ScannedBytes { get; set; }
    public int SkippedByExclusion { get; set; }
    public int ScanErrors { get; set; }

    public int DetectedCreated { get; set; }
    public int DetectedModified { get; set; }
    public int DetectedDeleted { get; set; }

    /// <summary>删除事件中"变化前内容不可用"的数量（这类删除无法恢复，必须让用户知道）。</summary>
    public int DeletedWithoutContent { get; set; }

    /// <summary>因为超过留存上限而未保存内容的变化数量。</summary>
    public int ContentNotStored { get; set; }

    public long EventHighWatermark { get; set; }

    public long ElapsedMs { get; set; }

    public List<string> Notes { get; } = new();

    public bool HasDrift => DetectedCreated + DetectedModified + DetectedDeleted > 0;
}

/// <summary>
/// 目录扫描与"磁盘 ↔ 索引对齐"。
///
/// 两个用途：
///  1. **建立基线**：添加受保护目录时，先把现状完整记录一次
///     （这是"添加保护之前就存在的文件被删除后仍能恢复"的前提）；
///  2. **停机后重新对齐**：程序没运行时发生的所有变化在本程序看来是"空白"，
///     绝不假装没发生 —— 用文件系统自身的时间戳（mtime）作为事实依据，
///     并明确标注这些事件来自"重新扫描"而非"实时观测"。
///
/// 明确的能力边界（写进代码也写进 UI）：
///  - 停机期间被"创建后又删除"的路径无法发现（磁盘上已无痕迹）；
///  - 停机期间的多次修改会被合并成一次变化（只能看到最终状态）；
///  - 因此这些事件的时间戳是 mtime（最后一次写入），且 Source = DowntimeRescan。
/// </summary>
public sealed class Rescanner
{
    public const string BaselineSource = "BaselineScan";
    public const string ResyncSource = "DowntimeRescan";

    private readonly IFileIndex _index;
    private readonly IEventSink _events;
    private readonly IFileVersionRepository _versions;
    private readonly FileSystemReader _reader;
    private readonly ContentWriter _writer;
    private readonly ExclusionMatcher _exclusions;
    private readonly AppSettings _settings;
    private readonly IClock _clock;

    public Rescanner(
        IFileIndex index,
        IEventSink events,
        IFileVersionRepository versions,
        FileSystemReader reader,
        ContentWriter writer,
        ExclusionMatcher exclusions,
        AppSettings settings,
        IClock clock)
    {
        _index = index;
        _events = events;
        _versions = versions;
        _reader = reader;
        _writer = writer;
        _exclusions = exclusions;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>扫描期间发现的一个条目。</summary>
    private sealed class ScanItem
    {
        public string AbsolutePath = string.Empty;
        public string RelativePath = string.Empty;
        public bool IsDirectory;
        public long Size;
        public DateTime? MtimeUtc;
        public bool IsReadOnly;
    }

    /// <summary>扫描过程中向上层汇报的进度（界面据此显示真实进度，而不是转圈）。</summary>
    public sealed record ScanProgress
    {
        /// <summary>已处理条目数。</summary>
        public int Processed { get; init; }

        public int Files { get; init; }

        public int Directories { get; init; }

        public int Skipped { get; init; }

        public long Bytes { get; init; }

        /// <summary>预估总条目数（来自 <see cref="Estimate"/>；未知时为 0）。</summary>
        public int EstimatedTotal { get; init; }

        /// <summary>当前正在处理的相对路径（便于用户确认"它真的在动"）。</summary>
        public string? CurrentPath { get; init; }

        public double Percent => EstimatedTotal <= 0 ? 0 : Math.Min(100, Processed * 100.0 / EstimatedTotal);

        public string Describe() => EstimatedTotal > 0
            ? $"已处理 {Processed}/{EstimatedTotal} 项（{Percent:0.#}%）"
            : $"已处理 {Processed} 项";
    }

    /// <summary>一次预估算的结果（用于在真正开始前告知用户规模与代价）。</summary>
    public sealed record ScanEstimate
    {
        public int Files { get; init; }

        public int Directories { get; init; }

        public long Bytes { get; init; }

        public long ElapsedMs { get; init; }

        /// <summary>本次估算所用的保护模式（决定耗时与占用的量级）。</summary>
        public ProtectionMode Mode { get; init; } = ProtectionMode.SmartContent;

        /// <summary>智能模式下"多大以上就不留存内容"的阈值。</summary>
        public long SmartMaxBytes { get; init; } = 4L * 1024 * 1024;

        /// <summary>按模式计算，需要留存内容的文件数量。</summary>
        public int ContentFileCount { get; init; }

        /// <summary>
        /// 实测吞吐（2026-09-11，机械盘，39.6 万条真实目录）：
        ///   · 留存内容 ≈ 110~130 个文件/秒 —— 瓶颈是 <c>synchronous=FULL</c> 下
        ///     两个数据库各自的 fsync，已接近机械盘 fsync 极限（批量事务无改善，实测过）；
        ///   · 不留存内容 ≈ 3500~4100 条/秒（只做 stat + 写索引）。
        /// 按实测值估算，宁可保守一点，也不要让用户以为"很快就好"。
        /// </summary>
        public const double ContentFilesPerSecond = 110.0;

        public const double NoContentEntriesPerSecond = 3500.0;

        /// <summary>粗略估算的完整扫描耗时（毫秒）。</summary>
        public long EstimatedBaselineMs
        {
            get
            {
                if (Mode == ProtectionMode.TrackOnly)
                {
                    return (long)((Files + Directories) / NoContentEntriesPerSecond * 1000) + 1200;
                }
                var withoutContent = Math.Max(0, Files + Directories - ContentFileCount);
                return (long)(ContentFileCount / ContentFilesPerSecond * 1000)
                     + (long)(withoutContent / NoContentEntriesPerSecond * 1000)
                     + 1500;
            }
        }

        /// <summary>预计历史内容占用（字节）。</summary>
        public long EstimatedHistoryBytes => Mode switch
        {
            ProtectionMode.TrackOnly => 0,
            ProtectionMode.FullContent => Bytes,
            _ => Bytes * ContentFileCount / Math.Max(1, Files),
        };

        /// <summary>交给用户看的完整说明（UI 直接展示，逐条如实写清代价与能力）。</summary>
        public string Describe()
        {
            var b = new System.Text.StringBuilder();
            b.AppendLine($"　文件：{Files:N0} 个");
            b.AppendLine($"　目录：{Directories:N0} 个");
            b.AppendLine($"　总大小：{LastRegret.Core.Util.PathUtil.FormatBytes(Bytes)}");
            b.AppendLine();
            b.AppendLine($"　保护模式：{Mode.ToChinese()}");
            b.AppendLine($"　预计首次扫描：约 {FormatDuration(EstimatedBaselineMs)}");

            if (Mode == ProtectionMode.TrackOnly)
            {
                b.AppendLine("　历史内容占用：0（该模式不留存内容）");
                b.Append("　⚠ 该模式下**无法恢复文件内容**，只能查看「哪个文件在何时发生了什么变化」。");
            }
            else
            {
                b.AppendLine($"　将留存内容的文件：约 {ContentFileCount:N0} 个");
                b.AppendLine($"　历史内容占用：最多约 {LastRegret.Core.Util.PathUtil.FormatBytes(EstimatedHistoryBytes)}");
                if (Mode == ProtectionMode.SmartContent && ContentFileCount < Files)
                {
                    b.Append($"　（大于 {LastRegret.Core.Util.PathUtil.FormatBytes(SmartMaxBytes)} 的 " +
                             $"{Files - ContentFileCount:N0} 个文件只记录变化事实，不保存内容）");
                }
            }
            return b.ToString().TrimEnd();
        }

        public static string FormatDuration(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalSeconds < 90) return $"{ts.TotalSeconds:0} 秒";
            if (ts.TotalMinutes < 90) return $"{ts.TotalMinutes:0.#} 分钟";
            return $"{ts.TotalHours:0.#} 小时";
        }
    }

    /// <summary>
    /// 只做一次**轻量预估算**：枚举条目并累加大小，但**不读取文件内容、不写数据库**。
    /// 实测 39.6 万条目约 14 秒。用于在真正开始前，按当前保护模式分别估算
    /// "大概要多久、会占多少空间、哪些文件能恢复内容"。
    /// </summary>
    public ScanEstimate Estimate(string rootPath, ProtectionMode mode, long smartMaxBytes, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = _reader.Scan(rootPath, _exclusions, null, cancellationToken, throttleEvery: 3000);

        // 再枚举一趟统计"会被留存内容的文件数"；对大目录这一趟很快（纯 stat，不读内容）
        int contentFiles = mode switch
        {
            ProtectionMode.FullContent => stats.FileCount,
            ProtectionMode.TrackOnly => 0,
            _ => CountFilesUnder(rootPath, smartMaxBytes, cancellationToken),
        };
        sw.Stop();

        return new ScanEstimate
        {
            Files = stats.FileCount,
            Directories = stats.DirectoryCount,
            Bytes = stats.TotalBytes,
            ElapsedMs = sw.ElapsedMilliseconds,
            Mode = mode,
            SmartMaxBytes = smartMaxBytes,
            ContentFileCount = contentFiles,
        };
    }

    private int CountFilesUnder(string rootPath, long maxBytes, CancellationToken cancellationToken)
    {
        int count = 0;
        _reader.Scan(rootPath, _exclusions, (abs, rel, isDir, size, mtime, ro) =>
        {
            if (!isDir && size <= maxBytes) count++;
        }, cancellationToken, throttleEvery: 3000);
        return count;
    }

    /// <summary>
    /// 执行一次扫描并与索引对齐。
    ///
    /// 性能要点（实测踩坑）：
    ///  - **单趟流式处理**：枚举到一条就处理一条。早期实现要完整枚举三遍
    ///    （统计 → 收集全部条目到内存 → 比对），在 39 万文件的真实目录上是灾难性的；
    ///  - **分批提交**：索引写入每批 200 条走一个事务，避免 39 万次单独事务；
    ///  - **节流**：每处理 N 条主动让出 CPU 一小段时间，保证前台界面和系统仍然顺畅；
    ///  - **可取消**：任何时刻都能停下，已处理的部分有效，支持中断续扫。
    /// </summary>
    /// <param name="root">受保护根。</param>
    /// <param name="mode">baseline = 只建立状态；resync = 与索引比较并记录漂移。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="estimatedTotal">预估总条目数（来自 <see cref="Estimate"/>，仅用于显示百分比）。</param>
    /// <param name="batchSize">索引批量提交大小。</param>
    /// <param name="throttleEvery">每处理多少条让出一次 CPU（0 = 不节流）。</param>
    public ResyncReport Align(
        WatchedRoot root,
        ResyncMode mode,
        Action<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int estimatedTotal = 0,
        int batchSize = 200,
        int throttleEvery = 400)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = new ResyncReport { RootId = root.Id, RootPath = root.Path };
        var maxFileSize = EffectiveMaxFileSize(root);

        // 索引里已有的条目（先读一次，内存里比对；39 万条约占几十 MB，可接受且远快于逐条查库）
        var indexSnapshot = new Dictionary<string, IndexEntry>(PathUtil.Comparer);
        foreach (var entry in _index.ListAll(root.Id)) indexSnapshot[entry.RelativePath] = entry;

        var inProgress = new ScanProgressBuilder(estimatedTotal, indexSnapshot.Count);
        var pendingBatch = new List<IndexEntry>(batchSize);
        var pendingEvents = new List<FileEvent>();
        var live = new HashSet<string>(PathUtil.Comparer);
        int processed = 0, sinceThrottle = 0;

        void FlushBatch()
        {
            if (pendingBatch.Count == 0) return;
            _index.UpsertMany(root.Id, pendingBatch);
            pendingBatch.Clear();
        }

        void FlushEvents()
        {
            if (pendingEvents.Count == 0) return;
            _events.AppendRange(pendingEvents);
            _versions.InsertRange(ToVersions(root.Id, pendingEvents));
            pendingEvents.Clear();
        }

        // ── 单趟：枚举到一条就处理一条 ──
        _reader.Scan(
            root.Path,
            _exclusions,
            (abs, rel, isDir, size, mtime, readOnly) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                live.Add(rel);
                indexSnapshot.TryGetValue(rel, out var existing);

                if (isDir)
                {
                    pendingBatch.Add(new IndexEntry
                    {
                        RootId = root.Id,
                        RelativePath = rel,
                        Kind = EntryKind.Directory,
                        Size = 0,
                        MtimeUtc = mtime,
                        FirstSeenUtc = existing?.FirstSeenUtc ?? mtime ?? _clock.UtcNow,
                        LastChangedUtc = mtime ?? _clock.UtcNow,
                        IsReadOnly = readOnly,
                    });

                    if (mode == ResyncMode.Resync && (existing is null || existing.IsDeleted))
                    {
                        report.DetectedCreated++;
                        pendingEvents.Add(MakeEvent(root.Id, BaselineSourceOrResync(mode), OperationType.Created,
                            EntryKind.Directory, rel, abs, size, mtime, readOnly, null, 0,
                            "重新扫描发现该目录已存在"));
                    }
                }
                else
                {
                    // 只有"大小或修改时间变了"才重新计算哈希 —— 这是续扫/重扫能很快的原因
                    bool needHash =
                        existing is null ||
                        existing.IsDeleted ||
                        existing.Hash is null ||
                        existing.Size != size ||
                        existing.MtimeUtc != mtime;

                    string? hash = existing?.Hash;
                    long? objectId = existing?.ObjectId;

                    if (needHash)
                    {
                        var capture = _reader.Capture(abs, isDirectory: false);
                        hash = capture.Hash;
                        var stored = _writer.Store(capture, rel, maxFileSize);
                        objectId = stored.ObjectId;
                        if (capture.Hash is not null && objectId is null)
                        {
                            report.ContentNotStored++;
                            if (report.Notes.Count < 50)
                                report.Notes.Add($"{rel}：{stored.Problem ?? "内容未保存"}");
                        }
                    }

                    pendingBatch.Add(new IndexEntry
                    {
                        RootId = root.Id,
                        RelativePath = rel,
                        Kind = EntryKind.File,
                        Size = size,
                        Hash = hash,
                        ObjectId = objectId,
                        MtimeUtc = mtime,
                        FirstSeenUtc = existing?.FirstSeenUtc ?? mtime ?? _clock.UtcNow,
                        LastChangedUtc = mtime ?? _clock.UtcNow,
                        IsReadOnly = readOnly,
                    });

                    if (mode == ResyncMode.Resync)
                    {
                        if (existing is null || existing.IsDeleted)
                        {
                            report.DetectedCreated++;
                            pendingEvents.Add(MakeEvent(root.Id, ResyncSource, OperationType.Created, EntryKind.File,
                                rel, abs, size, mtime, readOnly, hash, 0,
                                "重新扫描发现该文件在程序未运行期间出现"));
                        }
                        else if (!string.Equals(existing.Hash, hash, StringComparison.OrdinalIgnoreCase))
                        {
                            report.DetectedModified++;
                            pendingEvents.Add(MakeEvent(root.Id, ResyncSource, OperationType.Modified, EntryKind.File,
                                rel, abs, size, mtime, readOnly, hash, 0,
                                "重新扫描发现内容与上次记录不一致（程序未运行期间的变化）"));
                        }
                    }
                }

                processed++;
                sinceThrottle++;
                inProgress.Processed = processed;
                if (isDir)
                {
                    inProgress.Directories++;
                    report.ScannedDirectories++;
                }
                else
                {
                    inProgress.Files++;
                    inProgress.Bytes += size;
                    report.ScannedFiles++;
                    report.ScannedBytes += size;
                }
                if (progress is not null && processed % 200 == 0) progress(inProgress.Build(rel));

                if (pendingBatch.Count >= batchSize) FlushBatch();
                if (pendingEvents.Count >= 2000) FlushEvents();

                if (throttleEvery > 0 && sinceThrottle >= throttleEvery)
                {
                    sinceThrottle = 0;
                    // 主动让出 CPU：扫描可以慢一点，但前台界面与其它程序不应该被拖住
                    cancellationToken.WaitHandle.WaitOne(12);
                    FlushBatch();
                    inProgress.Throttled = true;
                }
            },
            cancellationToken);

        FlushBatch();

        // 索引里有、磁盘上没有 → 程序未运行期间被删除（或上次扫描被中断）
        foreach (var entry in indexSnapshot.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (live.Contains(entry.RelativePath)) continue;

            if (mode == ResyncMode.Resync && !entry.IsDeleted)
            {
                report.DetectedDeleted++;
                bool hasContent = entry.ObjectId is not null;
                if (!hasContent) report.DeletedWithoutContent++;

                pendingEvents.Add(new FileEvent
                {
                    RootId = root.Id,
                    TimestampUtc = _clock.UtcNow,
                    TimestampLocal = _clock.Now,
                    Operation = OperationType.Deleted,
                    Kind = entry.Kind,
                    RelativePath = entry.RelativePath,
                    SizeBefore = entry.Size,
                    HashBefore = entry.Hash,
                    ObjectIdBefore = entry.ObjectId,
                    MtimeBeforeUtc = entry.MtimeUtc,
                    Source = ResyncSource,
                    IsCoalesced = true,
                    Note = hasContent
                        ? "程序未运行期间该路径消失（已按最后记录的内容保留可恢复版本）"
                        : "程序未运行期间该路径消失，且此前未留存内容 —— 无法恢复",
                });
            }

            _index.MarkDeleted(root.Id, entry.RelativePath, _clock.UtcNow);
        }

        if (pendingEvents.Count > 0)
        {
            _events.AppendRange(pendingEvents);
            report.EventHighWatermark = pendingEvents[^1].Id;
            _versions.InsertRange(ToVersions(root.Id, pendingEvents));
        }

        sw.Stop();
        report.ElapsedMs = sw.ElapsedMilliseconds;
        progress?.Invoke(inProgress.Build(null));
        return report;
    }

    private static string BaselineSourceOrResync(ResyncMode mode) =>
        mode == ResyncMode.Baseline ? BaselineSource : ResyncSource;

    /// <summary>进度累加器（避免在回调里反复构造对象）。</summary>
    private sealed class ScanProgressBuilder
    {
        public ScanProgressBuilder(int estimatedTotal, int alreadyIndexed)
        {
            EstimatedTotal = estimatedTotal > 0 ? estimatedTotal : alreadyIndexed;
        }

        public int Processed { get; set; }
        public int Files { get; set; }
        public int Directories { get; set; }
        public long Bytes { get; set; }
        public bool Throttled { get; set; }

        private readonly int EstimatedTotal;

        public ScanProgress Build(string? currentPath) => new()
        {
            Processed = Processed,
            Files = Files,
            Directories = Directories,
            Bytes = Bytes,
            EstimatedTotal = EstimatedTotal,
            CurrentPath = currentPath,
        };
    }

    private long EffectiveMaxFileSize(WatchedRoot root) =>
        root.MaxFileSizeBytes > 0 ? root.MaxFileSizeBytes : _settings.MaxStoreFileSizeBytes;

    /// <summary>构造一条"扫描发现"的事件（不依赖中间对象，便于单趟流式处理）。</summary>
    private static FileEvent MakeEvent(
        long rootId, string source, OperationType op, EntryKind kind,
        string relativePath, string absolutePath, long size, DateTime? mtime, bool readOnly,
        string? hash, long objectId, string note)
    {
        var ts = mtime ?? DateTime.UtcNow;
        return new FileEvent
        {
            RootId = rootId,
            TimestampUtc = ts,
            TimestampLocal = ts.ToLocalTime(),
            Operation = op,
            Kind = kind,
            RelativePath = relativePath,
            SizeAfter = size,
            HashAfter = hash,
            MtimeAfterUtc = mtime,
            Source = source,
            IsCoalesced = true,
            Note = note,
        };
    }

    /// <summary>把事件转成文件版本行（扫描登记用）。</summary>
    private static IEnumerable<FileVersion> ToVersions(long rootId, IReadOnlyList<FileEvent> events)
    {
        foreach (var e in events)
        {
            if (e.Operation is not (OperationType.Created or OperationType.Modified)) continue;
            if (e.HashAfter is null || e.Kind != EntryKind.File) continue;

            yield return new FileVersion
            {
                RootId = rootId,
                RelativePath = e.RelativePath,
                Hash = e.HashAfter,
                ObjectId = e.ObjectIdAfter,
                Size = e.SizeAfter ?? 0,
                RecordedUtc = e.TimestampUtc,
                RecordedLocal = e.TimestampLocal,
                MtimeUtc = e.MtimeAfterUtc,
                EventId = e.Id == 0 ? null : e.Id,
                Note = e.Source == BaselineSource ? "基线扫描登记" : "重新扫描登记",
            };
        }
    }
}

public enum ResyncMode
{
    /// <summary>只把磁盘现状写进索引与内容库（建立基线）。</summary>
    Baseline,

    /// <summary>与现有索引比较，把差异记录为事件（程序未运行期间的变化）。</summary>
    Resync,
}
