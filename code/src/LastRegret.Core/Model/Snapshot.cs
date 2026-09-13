namespace LastRegret.Core.Model;

/// <summary>
/// 快照：某一时刻受保护范围的**完整状态**（由清单文件承载）。
///
/// 关键设计（同时满足"增量存储"与"可独立恢复"）：
///  - 快照本身保存一份完整清单（<see cref="SnapshotFile"/> 行集合），
///    因此它**不依赖事件日志**就能用于恢复；
///  - 但清单是"路径 + 内容哈希"的引用集合，内容本身在 CAS 中全局去重，
///    所以快照之间的增量只是"变化的那几行"，绝不复制整个目录；
///  - <see cref="SnapshotFile.ObjectId"/> 在产生快照时对哈希解析一次，
///    即使之后历史清理删掉了 events/file_versions 行，快照仍可恢复。
/// </summary>
public sealed class Snapshot
{
    public long Id { get; set; }

    public long RootId { get; set; }

    public DateTime TimestampUtc { get; set; }

    public DateTime TimestampLocal { get; set; }

    public SnapshotKind Kind { get; set; } = SnapshotKind.Auto;

    /// <summary>父快照 Id（用于增量链；可为 null）。</summary>
    public long? ParentId { get; set; }

    /// <summary>该快照对应的最大事件 Id（时间线定位用）。</summary>
    public long EventHighWatermark { get; set; }

    /// <summary>清单中文件数。</summary>
    public int FileCount { get; set; }

    /// <summary>清单中目录数。</summary>
    public int DirectoryCount { get; set; }

    /// <summary>清单总逻辑字节数。</summary>
    public long TotalBytes { get; set; }

    /// <summary>产生快照的原因说明（UI 展示），例如"恢复 22:04 之前自动创建"。</summary>
    public string? Note { get; set; }

    /// <summary>是否为完整清单（当前实现恒为 true；保留字段以便将来做纯增量格式）。</summary>
    public bool IsComplete { get; set; } = true;
}

public enum SnapshotKind
{
    /// <summary>添加保护时建立的初始基线。</summary>
    Baseline = 0,

    /// <summary>按间隔自动创建。</summary>
    Auto = 1,

    /// <summary>用户手动点击"立即创建恢复点"。</summary>
    Manual = 2,

    /// <summary>恢复操作之前自动创建的安全点。</summary>
    PreRestore = 3,

    /// <summary>恢复操作之后的状态点。</summary>
    PostRestore = 4,

    /// <summary>程序重新扫描对齐磁盘后建立。</summary>
    Resync = 5,
}

public static class SnapshotKindExtensions
{
    public static string ToChinese(this SnapshotKind k) => k switch
    {
        SnapshotKind.Baseline => "基线",
        SnapshotKind.Auto => "自动",
        SnapshotKind.Manual => "手动恢复点",
        SnapshotKind.PreRestore => "恢复前安全点",
        SnapshotKind.PostRestore => "恢复后状态",
        SnapshotKind.Resync => "重新对齐",
        _ => "快照",
    };

    public static string ToCode(this SnapshotKind k) => k switch
    {
        SnapshotKind.Baseline => "baseline",
        SnapshotKind.Auto => "auto",
        SnapshotKind.Manual => "manual",
        SnapshotKind.PreRestore => "pre_restore",
        SnapshotKind.PostRestore => "post_restore",
        SnapshotKind.Resync => "resync",
        _ => "auto",
    };

    public static SnapshotKind FromCode(string? code) => code switch
    {
        "baseline" => SnapshotKind.Baseline,
        "manual" => SnapshotKind.Manual,
        "pre_restore" => SnapshotKind.PreRestore,
        "post_restore" => SnapshotKind.PostRestore,
        "resync" => SnapshotKind.Resync,
        _ => SnapshotKind.Auto,
    };
}

/// <summary>快照清单中的一行：某个路径在快照时刻的状态。</summary>
public sealed class SnapshotFile
{
    public long SnapshotId { get; set; }

    /// <summary>相对路径（'/' 分隔）。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public EntryKind Kind { get; set; }

    public long Size { get; set; }

    /// <summary>内容哈希；目录为 null。</summary>
    public string? Hash { get; set; }

    /// <summary>CAS 对象 Id（产生快照时解析，保证快照可独立恢复）。</summary>
    public long? ObjectId { get; set; }

    public DateTime? MtimeUtc { get; set; }

    /// <summary>属性（只读/隐藏）——第一版记录但不强制恢复。</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// 该路径**首次出现**的时刻（UTC）。
    /// 用途：恢复预览需要如实区分"目标时刻之后才出现的新路径"与"一直存在的路径"，
    /// 前者属于"新增文件"，删除它们需要用户明确确认。
    /// 由基线时间或创建事件时间推导，绝不臆测。
    /// </summary>
    public DateTime? FirstSeenUtc { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 文件历史版本
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>某个路径的一个历史内容版本。相同内容在 CAS 中只存一份。</summary>
public sealed class FileVersion
{
    public long Id { get; set; }

    public long RootId { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public long? ObjectId { get; set; }

    public long Size { get; set; }

    /// <summary>该版本成为"当前"的时刻（UTC）。</summary>
    public DateTime RecordedUtc { get; set; }

    public DateTime RecordedLocal { get; set; }

    /// <summary>磁盘上的 mtime（UTC）。</summary>
    public DateTime? MtimeUtc { get; set; }

    /// <summary>产生该版本的事件 Id。</summary>
    public long? EventId { get; set; }

    /// <summary>产生该版本的快照 Id（基线扫描时用）。</summary>
    public long? SnapshotId { get; set; }

    /// <summary>该版本是否已从 CAS 中清理（清理后无法恢复内容，但保留元数据事实）。</summary>
    public bool ContentPruned { get; set; }

    public string? Note { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 进程快照（用于"关联进程"）
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProcessRecord
{
    public int Pid { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public string? ExecutablePath { get; set; }

    public DateTime? StartTimeUtc { get; set; }

    /// <summary>该进程最近一次被观察到活动的时间。</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>当时是否有可见前台窗口。</summary>
    public bool HadForegroundWindow { get; set; }

    public string? WindowTitle { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 恢复操作
// ─────────────────────────────────────────────────────────────────────────────

public enum RestoreStatus
{
    /// <summary>已生成预览，尚未执行。</summary>
    Planned = 0,

    /// <summary>正在执行。</summary>
    Running = 1,

    /// <summary>全部成功。</summary>
    Completed = 2,

    /// <summary>部分成功（部分条目失败），可撤销。</summary>
    PartiallyCompleted = 3,

    /// <summary>失败（未产生实质变化或已回滚）。</summary>
    Failed = 4,

    /// <summary>已被用户撤销。</summary>
    Undone = 5,

    /// <summary>用户取消。</summary>
    Cancelled = 6,
}

public static class RestoreStatusExtensions
{
    public static string ToChinese(this RestoreStatus s) => s switch
    {
        RestoreStatus.Planned => "待确认",
        RestoreStatus.Running => "进行中",
        RestoreStatus.Completed => "已完成",
        RestoreStatus.PartiallyCompleted => "部分完成",
        RestoreStatus.Failed => "失败",
        RestoreStatus.Undone => "已撤销",
        RestoreStatus.Cancelled => "已取消",
        _ => "未知",
    };

    public static string ToCode(this RestoreStatus s) => s switch
    {
        RestoreStatus.Planned => "planned",
        RestoreStatus.Running => "running",
        RestoreStatus.Completed => "completed",
        RestoreStatus.PartiallyCompleted => "partial",
        RestoreStatus.Failed => "failed",
        RestoreStatus.Undone => "undone",
        RestoreStatus.Cancelled => "cancelled",
        _ => "planned",
    };

    public static RestoreStatus FromCode(string? code) => code switch
    {
        "running" => RestoreStatus.Running,
        "completed" => RestoreStatus.Completed,
        "partial" => RestoreStatus.PartiallyCompleted,
        "failed" => RestoreStatus.Failed,
        "undone" => RestoreStatus.Undone,
        "cancelled" => RestoreStatus.Cancelled,
        _ => RestoreStatus.Planned,
    };
}

/// <summary>一次恢复操作（预览 → 确认 → 执行 → 可撤销 的完整记录）。</summary>
public sealed class RestoreOperation
{
    public long Id { get; set; }

    public long RootId { get; set; }

    /// <summary>恢复目标时间点。</summary>
    public DateTime TargetTimeUtc { get; set; }

    public DateTime TargetTimeLocal { get; set; }

    /// <summary>目标快照 Id。</summary>
    public long? TargetSnapshotId { get; set; }

    /// <summary>恢复前自动创建的安全点 Id（撤销的落点）。</summary>
    public long? PreRestoreSnapshotId { get; set; }

    /// <summary>恢复完成后创建的状态点 Id。</summary>
    public long? PostRestoreSnapshotId { get; set; }

    public RestoreStatus Status { get; set; } = RestoreStatus.Planned;

    public DateTime StartedUtc { get; set; }

    public DateTime? FinishedUtc { get; set; }

    /// <summary>计划条目数 / 已成功条目数 / 失败条目数。</summary>
    public int PlannedCount { get; set; }

    public int SucceededCount { get; set; }

    public int FailedCount { get; set; }

    /// <summary>用户本次确认的预览指纹（防止确认的预览与执行内容不一致）。</summary>
    public string? PlanFingerprint { get; set; }

    public string? Message { get; set; }

    /// <summary>撤销本操作时使用的那次恢复操作 Id。</summary>
    public long? UndoneByOperationId { get; set; }

    /// <summary>本操作是否是对另一次恢复的撤销。</summary>
    public long? UndoesOperationId { get; set; }
}

/// <summary>恢复明细（逐条），也是"执行日志"——崩溃后据此判断实际做到了哪一步。</summary>
public sealed class RestoreStepRecord
{
    public long Id { get; set; }

    public long OperationId { get; set; }

    public int Sequence { get; set; }

    public RestoreAction Action { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>恢复后应达到的内容哈希（目标状态）。</summary>
    public string? TargetHash { get; set; }

    /// <summary>执行前磁盘上的哈希（撤销依据）。</summary>
    public string? BeforeHash { get; set; }

    /// <summary>执行前内容的 CAS 对象（撤销依据，必须已落盘）。</summary>
    public long? BeforeObjectId { get; set; }

    public long? BeforeSize { get; set; }

    public bool Succeeded { get; set; }

    /// <summary>当真实执行时是否跳过了覆盖（因为磁盘内容已不是预期内容）。</summary>
    public bool SkippedDueToConflict { get; set; }

    public string? Error { get; set; }

    public DateTime? ExecutedUtc { get; set; }
}

/// <summary>恢复动作类型。</summary>
public enum RestoreAction
{
    /// <summary>把目标状态里存在、当前不存在的内容写回磁盘。</summary>
    RestoreContent = 0,

    /// <summary>把当前存在、目标状态里不存在的路径删除（移入回收区而非直接抹除）。</summary>
    RemovePath = 1,

    /// <summary>内容一致，无需操作（仅为审计保留）。</summary>
    NoChange = 2,

    /// <summary>重建目录。</summary>
    CreateDirectory = 3,

    /// <summary>删除目录。</summary>
    RemoveDirectory = 4,
}

public static class RestoreActionExtensions
{
    public static string ToChinese(this RestoreAction a) => a switch
    {
        RestoreAction.RestoreContent => "恢复内容",
        RestoreAction.RemovePath => "移除",
        RestoreAction.NoChange => "无变化",
        RestoreAction.CreateDirectory => "重建目录",
        RestoreAction.RemoveDirectory => "移除目录",
        _ => "操作",
    };

    public static string ToCode(this RestoreAction a) => a switch
    {
        RestoreAction.RestoreContent => "restore",
        RestoreAction.RemovePath => "remove",
        RestoreAction.NoChange => "noop",
        RestoreAction.CreateDirectory => "mkdir",
        RestoreAction.RemoveDirectory => "rmdir",
        _ => "noop",
    };

    public static RestoreAction FromCode(string? code) => code switch
    {
        "restore" => RestoreAction.RestoreContent,
        "remove" => RestoreAction.RemovePath,
        "mkdir" => RestoreAction.CreateDirectory,
        "rmdir" => RestoreAction.RemoveDirectory,
        _ => RestoreAction.NoChange,
    };
}
