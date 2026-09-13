namespace LastRegret.Core.Abstractions;

/// <summary>时间源抽象（测试可注入假时钟，避免依赖真实时间导致用例不稳定）。</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public DateTime Now => DateTime.Now;
}
