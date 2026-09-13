namespace LastRegret.Core.Diff;

/// <summary>行内差异片段（用于高亮"到底改了哪几个字"）。</summary>
public sealed class DiffChunk
{
    public int Start { get; init; }

    public int Length { get; init; }

    public override string ToString() => $"[{Start},{Length})";
}

public enum DiffLineKind
{
    Context = 0,
    Added = 1,
    Removed = 2,
}

/// <summary>一行差异。</summary>
public sealed class DiffLine
{
    public DiffLineKind Kind { get; init; }

    /// <summary>行内容（不含换行符）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>在"旧"文本中的行号（1 起；新增行为 null）。</summary>
    public int? OldLineNumber { get; init; }

    /// <summary>在"新"文本中的行号（1 起；删除行为 null）。</summary>
    public int? NewLineNumber { get; init; }

    /// <summary>行内差异片段（仅对成对的删除/新增行给出）。</summary>
    public IReadOnlyList<DiffChunk>? OldChunks { get; init; }

    public IReadOnlyList<DiffChunk>? NewChunks { get; init; }

    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };

    public override string ToString() => $"{Marker}{Text}";
}

/// <summary>Diff 结果。</summary>
public sealed class DiffResult
{
    public List<DiffLine> Lines { get; } = new();

    public int AddedCount { get; set; }

    public int RemovedCount { get; set; }

    /// <summary>是否因为规模超限而只做了行级（未做行内高亮）。</summary>
    public bool LineLevelOnly { get; set; }

    /// <summary>本次比较的两侧是否为文本；false 表示调用方应按二进制处理。</summary>
    public bool IsText { get; set; }

    /// <summary>说明信息（例如"内容完全相同"、"文件过大，仅显示摘要"）。</summary>
    public string? Note { get; set; }

    /// <summary>文本行数超限被截断。</summary>
    public bool Truncated { get; set; }

    public bool AreIdentical => AddedCount == 0 && RemovedCount == 0;

    /// <summary>生成统一格式文本（用于展示 / 复制 / 测试断言）。</summary>
    public string ToUnifiedText(int context = 3)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var l in Lines)
        {
            if (l.Kind == DiffLineKind.Context && context >= 0)
            {
                sb.Append(' ').AppendLine(l.Text);
            }
            else if (l.Kind != DiffLineKind.Context)
            {
                sb.Append(l.Marker).AppendLine(l.Text);
            }
        }
        return sb.ToString();
    }
}
