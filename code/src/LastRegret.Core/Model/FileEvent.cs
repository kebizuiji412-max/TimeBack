namespace LastRegret.Core.Model;

/// <summary>
/// 一条已持久化的文件系统变化事件（事实记录）。
///
/// 语义约定：
///  - <see cref="RelativePath"/> 为发生变化的路径（Rename/Move 时是**新**路径）。
///  - <see cref="OldRelativePath"/> 仅 Rename/Move 使用（**旧**路径）。
///  - <see cref="HashBefore"/>/<see cref="HashAfter"/> 是内容寻址存储里的内容哈希，
///    为 null 表示"该侧没有内容"或"未能取得内容"（例如删除前从未留存版本）。
///  - <see cref="Confidence"/> 表明这条记录本身有多确定；
///    <see cref="Attribution"/> 表达"附近有什么进程"，绝不表达因果。
/// </summary>
public sealed class FileEvent
{
    /// <summary>数据库自增主键。未持久化时为 0。</summary>
    public long Id { get; set; }

    /// <summary>所属受保护根目录 Id。</summary>
    public long RootId { get; set; }

    /// <summary>事件时间（UTC）。</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>事件时间（本地时区，落库时冻结，避免事后时区变化导致时间线漂移）。</summary>
    public DateTime TimestampLocal { get; set; }

    public OperationType Operation { get; set; }

    public EntryKind Kind { get; set; }

    /// <summary>变化路径（相对受保护根）。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>旧路径（仅 Rename/Move）。</summary>
    public string? OldRelativePath { get; set; }

    public long? SizeBefore { get; set; }

    public long? SizeAfter { get; set; }

    public string? HashBefore { get; set; }

    public string? HashAfter { get; set; }

    public DateTime? MtimeBeforeUtc { get; set; }

    public DateTime? MtimeAfterUtc { get; set; }

    /// <summary>内容对象 Id（CAS 中的对象，指向"变化后"的内容）。</summary>
    public long? ObjectIdAfter { get; set; }

    /// <summary>内容对象 Id（CAS 中的对象，指向"变化前"的内容）。</summary>
    public long? ObjectIdBefore { get; set; }

    /// <summary>由多少个底层原始事件合并而来（>1 表示做过去重合并）。</summary>
    public int MergeCount { get; set; } = 1;

    /// <summary>是否为合并后的代表事件（UI 默认只显示这一条）。</summary>
    public bool IsCoalesced { get; set; }

    /// <summary>被折叠的原始事件数（UI 上显示"已合并 N 次保存"）。</summary>
    public int SuppressedCount { get; set; }

    /// <summary>原始事件来源，例如 "ReadDirectoryChangesW"/"Baseline"/"Restore"。</summary>
    public string Source { get; set; } = "ReadDirectoryChangesW";

    /// <summary>关联进程（可能来源）。null 表示未取得。</summary>
    public ProcessAttribution? Attribution { get; set; }

    /// <summary>关联进程 Id（方便直接 SQL 查询）。</summary>
    public int? AttributedPid { get; set; }

    /// <summary>关联进程名，例如 "Code.exe"。</summary>
    public string? AttributedProcess { get; set; }

    /// <summary>归属置信度。</summary>
    public AttributionConfidence Confidence { get; set; } = AttributionConfidence.None;

    /// <summary>附加说明（例如"检测到编辑器原子替换"）。UI 直接展示。</summary>
    public string? Note { get; set; }

    /// <summary>是否为临时/中间文件（UI 折叠）。</summary>
    public bool IsTransient { get; set; }

    /// <summary>本次变化是否由本程序自己的恢复操作引起。</summary>
    public bool IsRestoreInduced { get; set; }

    /// <summary>
    /// 这条事件**能否**把该路径恢复到变化之前的状态。
    /// 依据非常明确：只有"变化前的内容"确实存在于 CAS 中才为 true。
    /// UI 必须如实展示（可恢复 / 不可恢复），不允许为了好看而含糊。
    /// </summary>
    public bool CanRestorePrevious => ObjectIdBefore is not null;

    /// <summary>这条事件是否能用来"重做"变化之后的状态。</summary>
    public bool CanRestoreAfter => ObjectIdAfter is not null;

    /// <summary>展示用的一句话摘要。</summary>
    public string Summary
    {
        get
        {
            var s = $"{Operation.ToChinese()} {RelativePath}";
            if (OldRelativePath is { Length: > 0 }) s += $"（原 {OldRelativePath}）";
            if (SuppressedCount > 0) s += $" · 已合并 {SuppressedCount} 次";
            if (AffectedDescendantCount > 0) s += $" · 影响 {AffectedDescendantCount} 个子项";
            return s;
        }
    }

    /// <summary>受影响的同族路径数量（目录操作使用）。</summary>
    public int AffectedDescendantCount { get; set; }

    public override string ToString() =>
        $"[{TimestampLocal:HH:mm:ss.fff}] {Operation.ToChinese()} {RelativePath}" +
        (OldRelativePath is null ? "" : $" (原 {OldRelativePath})");
}

/// <summary>归属置信度。绝不夸大因果判断能力。</summary>
public enum AttributionConfidence
{
    /// <summary>没有可用信息。</summary>
    None = 0,

    /// <summary>只知道时间窗口内附近有哪些进程，无法区分具体是哪一个。</summary>
    Nearby = 1,

    /// <summary>时间窗口 + 进程类型启发式（编辑器/终端/资源管理器）高度吻合。</summary>
    Likely = 2,

    /// <summary>拿到了内核级证据（文件句柄持有者）。当前环境通常不可得。</summary>
    Handler = 3,
}

/// <summary>
/// 关联进程信息。这是"可能来源"，不是"确定的元凶"。
/// UI 必须按 <see cref="Confidence"/> 如实措辞。
/// </summary>
public sealed class ProcessAttribution
{
    public int Pid { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    /// <summary>进程可执行文件完整路径（可能为 null：权限不足）。</summary>
    public string? ExecutablePath { get; set; }

    public DateTime? StartTimeUtc { get; set; }

    /// <summary>窗口标题（仅前台窗口进程可得）。</summary>
    public string? WindowTitle { get; set; }

    public AttributionConfidence Confidence { get; set; } = AttributionConfidence.None;

    /// <summary>判定依据说明，例如"事件时刻的前台窗口进程"/"活跃进程集合"。UI 展示。</summary>
    public string Basis { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrEmpty(ProcessName) ? $"PID {Pid}" : ProcessName;
}

/// <summary>CAS 中的一个内容对象。</summary>
public sealed class StoredObject
{
    public long Id { get; set; }

    /// <summary>内容哈希（SHA-256 十六进制小写）。</summary>
    public string Hash { get; set; } = string.Empty;

    public long LogicalSize { get; set; }

    public long StoredSize { get; set; }

    /// <summary>存储编码：raw / deflate。相同内容只存一份，编码方式随首次写入固定。</summary>
    public string Encoding { get; set; } = "raw";

    public string? ExtensionHint { get; set; }
}

/// <summary>受保护根目录。</summary>
public sealed class WatchedRoot
{
    public long Id { get; set; }

    /// <summary>物理绝对路径。</summary>
    public string Path { get; set; } = string.Empty;

    public string? Label { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>最近一次事件时间（UTC）。</summary>
    public DateTime? LastEventUtc { get; set; }

    /// <summary>基线快照 Id。</summary>
    public long? BaselineSnapshotId { get; set; }

    /// <summary>是否递归包含子目录。</summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>被用户排除的子路径（相对路径前缀）。</summary>
    public List<string> Excludes { get; set; } = new();

    /// <summary>单文件最大留存大小（字节），超过则只记录事件不保存内容。</summary>
    public long MaxFileSizeBytes { get; set; } = 64L * 1024 * 1024;

    public string DisplayName
    {
        get
        {
            var trimmed = Path.TrimEnd('\\', '/');
            var name = trimmed.Length == 0 ? Path : System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }
}
