namespace LastRegret.Core.Abstractions;

using LastRegret.Core.Model;

/// <summary>事件持久化接收端。</summary>
public interface IEventSink
{
    /// <summary>批量写入事件（必须在单个事务内完成）。返回带数据库 Id 的事件。</summary>
    void AppendRange(IReadOnlyList<FileEvent> events);

    /// <summary>查询事件（时间线）。</summary>
    IReadOnlyList<FileEvent> Query(EventQuery query);

    /// <summary>统计某时间范围内的事件数量。</summary>
    long Count(EventQuery query);

    /// <summary>取某时刻之前（含）的最后一个事件 Id（时间线定位）。</summary>
    long GetHighWatermark(long rootId, DateTime atUtc);
}

/// <summary>事件查询条件。</summary>
public sealed class EventQuery
{
    public long? RootId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    /// <summary>精确路径（可选）。</summary>
    public string? RelativePath { get; set; }

    /// <summary>路径前缀（可选，用于"某目录下的全部变化"）。</summary>
    public string? PathPrefix { get; set; }

    /// <summary>只要这些操作类型。</summary>
    public OperationType[]? Operations { get; set; }

    /// <summary>是否包含瞬时事件（默认不含）。</summary>
    public bool IncludeTransient { get; set; }

    /// <summary>是否包含由恢复操作引起的事件（默认含）。</summary>
    public bool IncludeRestoreInduced { get; set; } = true;

    public int Limit { get; set; } = 500;

    public int Offset { get; set; }

    /// <summary>倒序（默认从新到旧，符合"最近发生了什么"的首页需求）。</summary>
    public bool Descending { get; set; } = true;
}
