namespace LastRegret.Data;

/// <summary>启动自检报告：如实告诉用户历史数据是不是完好的。</summary>
public sealed class DatabaseHealth
{
    public bool EventsDbOk { get; set; }

    public bool ObjectsDbOk { get; set; }

    public string? EventsProblem { get; set; }

    public string? ObjectsProblem { get; set; }

    public int ForeignKeyProblems { get; set; }

    public bool SchemaUpgraded { get; set; }

    public int SchemaVersion { get; set; }

    public string? RecoveryNote { get; set; }

    public bool IsHealthy => EventsDbOk && ObjectsDbOk && ForeignKeyProblems == 0;

    public string Describe()
    {
        if (IsHealthy) return $"数据库完好（结构版本 {SchemaVersion}）";
        var parts = new List<string>();
        if (!EventsDbOk) parts.Add($"事件库问题：{EventsProblem}");
        if (!ObjectsDbOk) parts.Add($"内容库问题：{ObjectsProblem}");
        if (ForeignKeyProblems > 0) parts.Add($"外键不一致 {ForeignKeyProblems} 处");
        return string.Join("；", parts);
    }
}
