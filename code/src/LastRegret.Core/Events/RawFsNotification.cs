namespace LastRegret.Core.Events;

using LastRegret.Core.Model;

/// <summary>
/// 原始文件系统通知（由 ReadDirectoryChangesW 层产生）。
/// 这是"最原始的事实"，尚未经过任何合并、延迟确认或语义解释。
/// </summary>
public sealed class RawFsNotification
{
    public long RootId { get; init; }

    public DateTime TimestampUtc { get; init; }

    /// <summary>产生通知的物理路径（绝对路径）。</summary>
    public string AbsolutePath { get; init; } = string.Empty;

    /// <summary>重命名/移动时的旧物理路径。</summary>
    public string? OldAbsolutePath { get; init; }

    public RawChangeKind Kind { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>底层事件序号（同一批次内的顺序），用于调试与去重。</summary>
    public long Sequence { get; init; }

    public override string ToString() =>
        $"{TimestampUtc:HH:mm:ss.fff} {Kind} {(IsDirectory ? "[D]" : "[F]")} {AbsolutePath}" +
        (OldAbsolutePath is null ? "" : $" <- {OldAbsolutePath}");
}

/// <summary>ReadDirectoryChangesW 语义的原始变化类型（保留原始信息，不提前归类）。</summary>
public enum RawChangeKind
{
    /// <summary>FILE_ACTION_ADDED</summary>
    Added = 0,

    /// <summary>FILE_ACTION_REMOVED</summary>
    Removed = 1,

    /// <summary>FILE_ACTION_MODIFIED</summary>
    Modified = 2,

    /// <summary>FILE_ACTION_RENAMED_OLD_NAME</summary>
    RenamedOld = 3,

    /// <summary>FILE_ACTION_RENAMED_NEW_NAME</summary>
    RenamedNew = 4,

    /// <summary>无法识别的动作码。</summary>
    Other = 5,
}

/// <summary>
/// 事件合并器的输出：一条已"稳定"的、可以持久化的事件草稿。
/// 与 <see cref="FileEvent"/> 的区别：这里还没有数据库 Id，且内容对象尚未落盘。
/// </summary>
public sealed class CoalescedEvent
{
    public long RootId { get; set; }

    public OperationType Operation { get; set; }

    public EntryKind Kind { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public string? OldRelativePath { get; set; }

    /// <summary>事件时间取"第一次变化"的时刻（用户感知的起点）。</summary>
    public DateTime FirstUtc { get; set; }

    /// <summary>最后一次变化的时刻（合并窗口结束）。</summary>
    public DateTime LastUtc { get; set; }

    /// <summary>被合并掉的原始通知数量。</summary>
    public int SuppressedCount { get; set; }

    /// <summary>内容采集结果（"变化前"）。</summary>
    public ContentCapture? Before { get; set; }

    /// <summary>内容采集结果（"变化后"）。</summary>
    public ContentCapture? After { get; set; }

    public bool IsTransient { get; set; }

    public string? Note { get; set; }

    /// <summary>受影响的同族路径数量（目录移动/删除时其内部条目数）。</summary>
    public int AffectedDescendantCount { get; set; }

    public override string ToString() =>
        $"{FirstUtc:HH:mm:ss.fff}…{LastUtc:HH:mm:ss.fff} {Operation.ToChinese()} {RelativePath} (合并 {SuppressedCount} 次)";
}

/// <summary>一次内容采集的结果。</summary>
public sealed class ContentCapture
{
    /// <summary>物理路径。</summary>
    public string AbsolutePath { get; set; } = string.Empty;

    /// <summary>内容哈希（SHA-256 十六进制小写）；null = 未采到内容。</summary>
    public string? Hash { get; set; }

    public long? Size { get; set; }

    public DateTime? MtimeUtc { get; set; }

    public bool IsReadOnly { get; set; }

    /// <summary>未采到内容的原因（必须如实说明，不许假装成功）。</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>是否为目录。</summary>
    public bool IsDirectory { get; set; }

    public bool HasContent => Hash is not null;
}
