namespace LastRegret.Core.Abstractions;

using LastRegret.Core.Model;

/// <summary>进程信息提供者（"关联进程"，绝不声称因果）。</summary>
public interface IProcessProbe
{
    /// <summary>取当前活跃进程快照（可能在后台线程缓存）。</summary>
    IReadOnlyList<ProcessRecord> Snapshot();

    /// <summary>取当前前台窗口所属进程（若有）。</summary>
    ProcessAttribution? GetForegroundProcess();

    /// <summary>
    /// 尝试通过内核句柄确定"哪个进程正持有该文件"。
    /// 本机实测：大多数进程无法打开 PROCESS_DUP_HANDLE，因此经常返回 null ——
    /// 这是**真实的能力限制**，调用方必须据此降级为"关联进程"，不得伪造结论。
    /// </summary>
    ProcessAttribution? TryFindHandleOwner(string absolutePath, IReadOnlyList<ProcessAttribution> candidates);

    /// <summary>本机实测得到的归属能力报告（UI 与文档如实展示）。</summary>
    AttributionCapability GetCapability();
}

/// <summary>归属能力报告。</summary>
public sealed class AttributionCapability
{
    public bool HandleEnumerationAvailable { get; init; }

    public bool CrossProcessDupAvailable { get; init; }

    public int ProcessesProbed { get; init; }

    public int ProcessesOpenable { get; init; }

    public string Summary { get; init; } = string.Empty;
}
