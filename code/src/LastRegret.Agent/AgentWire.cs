namespace LastRegret.Agent;

/// <summary>
/// CLI 的线上形状（wire shape）。
///
/// 这些类型**只属于这个进程边界**：不放进 Core / Engine / Data / Runtime，
/// 也不让 <c>RestorePlan</c> / <c>Snapshot</c> / <c>FileEvent</c> / <c>RootRuntimeState</c>
/// 直接变成协议类型 —— 它们在这里被映射成下面这些"只带 Agent 真正需要字段"的小对象。
/// </summary>
internal static class AgentProtocol
{
    public const string Name = "timeback-agent";
    public const string Version = "1.0";
}

/// <summary>响应状态：四个取值必须能被机器明确区分（不得把失败说成成功）。</summary>
internal static class AgentStatus
{
    /// <summary>成功完成。</summary>
    public const string Success = "success";

    /// <summary>成功完成，但确实没有需要做的事（例如预览显示无需恢复）。</summary>
    public const string NoChange = "no_change";

    /// <summary>请求合法但被安全机制拒绝（例如指纹不匹配）—— 不是"失败"，也不会执行。</summary>
    public const string Rejected = "rejected";

    /// <summary>执行失败 / 请求不合法（能力未知、参数缺失、内部错误）。</summary>
    public const string Failed = "failed";
}

/// <summary>稳定错误码。第一版只覆盖真实会出现的几种，不追求穷尽。</summary>
internal static class AgentErrorCode
{
    public const string MissingCommand = "missing_command";
    public const string UnknownCommand = "unknown_command";
    public const string InvalidRequest = "invalid_request";
    public const string MissingParameter = "missing_parameter";
    public const string RootNotFound = "root_not_found";
    public const string PreviewFailed = "preview_failed";
    public const string FingerprintRequired = "fingerprint_required";
    public const string FingerprintMismatch = "fingerprint_mismatch";

    /// <summary>
    /// 计划已过期：预览之后磁盘内容又被改动过，执行时已不再等价（FINAL-WB-001）。
    /// 这是**拒绝**而不是失败 —— 什么都没做，调用方应重新预览。
    /// </summary>
    public const string PlanStale = "plan_stale";
    public const string RestoreFailed = "restore_failed";
    public const string UndoUnavailable = "undo_unavailable";
    public const string InternalError = "internal_error";
}

/// <summary>请求（stdin 的一个 JSON 对象；全部字段可选，缺什么由各 capability 校验）。</summary>
internal sealed class AgentRequest
{
    public long? RootId { get; set; }
    public string? FromUtc { get; set; }
    public string? ToUtc { get; set; }
    public string? AtUtc { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }
    public bool? IncludeTransient { get; set; }
    public List<string>? IncludePaths { get; set; }

    /// <summary>执行恢复/撤销时必须携带：来自同一次预览的指纹。</summary>
    public string? Fingerprint { get; set; }

    /// <summary>是否允许"恢复到目标时间点会导致新增文件被删掉"。**缺省 false**（不替调用方放宽）。</summary>
    public bool? AllowNewRemovals { get; set; }

    public long? OperationId { get; set; }
}

/// <summary>响应外壳：protocol / version / capability / ok / status / data / error。</summary>
internal sealed class AgentResponse
{
    public string Protocol { get; set; } = AgentProtocol.Name;
    public string Version { get; set; } = AgentProtocol.Version;
    public string? Capability { get; set; }
    public bool Ok { get; set; }
    public string Status { get; set; } = AgentStatus.Failed;
    public object? Data { get; set; }
    public AgentError? Error { get; set; }

    /// <summary>可选补充说明（例如引擎给出的计划警告、执行失败明细）。字段只增不删。</summary>
    public List<string>? Warnings { get; set; }
}

internal sealed class AgentError
{
    public string Code { get; set; } = AgentErrorCode.InternalError;
    public string Message { get; set; } = string.Empty;
}

// ───────────────────────── capability 数据 ─────────────────────────

/// <summary>
/// 能力清单。**这里必须与仓库根的 agent-interface.json 完全一致**，
/// 并由测试逐项比对（声明 = 实现）——不新增、不遗漏、不改 readonly。
/// </summary>
internal static class AgentCapabilities
{
    public static readonly (string Id, bool Readonly, string Description)[] All =
    {
        ("protected-folders.read", true,
            "读取当前受保护的文件夹清单：路径、是否正在监听、是否已建立基线。"),
        ("timeline.read", true,
            "读取某个受保护文件夹的恢复点（时间点）清单及其时间与规模信息。"),
        ("changes.read", true,
            "读取某个受保护文件夹的变化记录：哪个路径在什么时间发生了什么变化。"),
        ("restore.preview", true,
            "生成只读预览：恢复到某个时间点会创建、覆盖、删除哪些路径。不写磁盘、不改历史。"),
        ("restore.execute", false,
            "执行一份已经预览并确认过的恢复计划（可含删除）。会写磁盘，执行前自动建立安全点。"),
        ("restore.undo", false,
            "撤销一次实际存在且可撤销的恢复操作，把范围放回那次恢复之前的状态。"),
    };
}

internal sealed class CapabilityInfo
{
    public string Id { get; set; } = string.Empty;
    public bool Readonly { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class CapabilitiesData
{
    public string Product { get; set; } = "timeback";
    public string ProductVersion { get; set; } = "0.2.1";
    public List<CapabilityInfo> Capabilities { get; set; } = new();
}

internal sealed class ProtectedFolderInfo
{
    public long RootId { get; set; }
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Watching { get; set; }
    public bool HasBaseline { get; set; }
    public long EventCount { get; set; }
    public string? LastEventUtc { get; set; }
}

internal sealed class ProtectedFoldersData
{
    public List<ProtectedFolderInfo> Folders { get; set; } = new();
}

internal sealed class TimelinePointInfo
{
    public long SnapshotId { get; set; }
    public string TimestampUtc { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }
    public long TotalBytes { get; set; }
    public string? Note { get; set; }
}

internal sealed class TimelineData
{
    public long RootId { get; set; }
    public List<TimelinePointInfo> Points { get; set; } = new();
}

internal sealed class ChangeInfo
{
    public long EventId { get; set; }
    public string TimestampUtc { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? OldPath { get; set; }
    public long? SizeAfter { get; set; }
    public string? HashAfter { get; set; }
}

internal sealed class ChangesData
{
    public long RootId { get; set; }
    public int Count { get; set; }
    public List<ChangeInfo> Changes { get; set; } = new();
}

/// <summary>预览输入的原样回执：执行时必须原样回传，才能保证"执行的就是被预览的那一份"。</summary>
internal sealed class PreviewInputs
{
    public long RootId { get; set; }
    public string AtUtc { get; set; } = string.Empty;
    public List<string>? IncludePaths { get; set; }
}

internal sealed class PlanData
{
    public PreviewInputs PreviewInput { get; set; } = new();

    /// <summary>计划指纹：执行时必须原样回传。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public bool HasEffect { get; set; }
    public int StepCount { get; set; }
    public int ToRestore { get; set; }
    public int ToCreateDirectories { get; set; }
    public int ToRemove { get; set; }
    public int ToRemoveDirectories { get; set; }
    public int Unavailable { get; set; }
    public int Affected { get; set; }
    public string TargetTimeUtc { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}

internal sealed class RestoreData
{
    public bool ExecutionOk { get; set; }
    public long OperationId { get; set; }
    public string OperationStatus { get; set; } = string.Empty;
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int FilesChanged { get; set; }
    public long? PreSnapshotId { get; set; }
    public long? PostSnapshotId { get; set; }
    public bool CanUndo { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class UndoPreviewData
{
    public long OperationId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string TargetTimeUtc { get; set; } = string.Empty;
    public int StepCount { get; set; }
    public int ToRestore { get; set; }
    public int ToRemove { get; set; }
    public List<string> Warnings { get; set; } = new();
}

internal sealed class UsageData
{
    public string Usage { get; set; } = string.Empty;
    public List<string> Commands { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}
