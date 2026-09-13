namespace LastRegret.Core.Diff;

/// <summary>
/// 文本行差异引擎（自研，零依赖）。
///
/// 算法：
///  1. 快速通道：完全相同 → 全 Context。
///  2. 去掉公共前缀/后缀（真实场景下 99% 的修改只影响局部）。
///  3. 中段使用 LCS 动态规划求最短编辑脚本（O(n·m)，两侧行数受上限保护）。
///  4. 相邻的删除行与新增行成对做"行内差异"，指出具体改了哪一段字符。
///
/// 明确边界（不假装能力）：
///  - 只做行级 + 行内差异；不做语义化/结构化（JSON/XML）Diff。
///  - 超过 <see cref="MaxLines"/> 行时截断并明确告知。
///  - 二进制内容不进入本引擎（由调用方判断）。
/// </summary>
public static class DiffEngine
{
    /// <summary>参与 LCS 的最大行数（每侧）。超过则截断。</summary>
    public const int MaxLines = 50_000;

    /// <summary>行内高亮的最大行长度；超长行只做整行标记，避免卡顿。</summary>
    public const int MaxLineLengthForIntraHighlight = 4096;

    public static DiffResult Compute(string? oldText, string? newText)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;

        var result = new DiffResult { IsText = true };

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            result.Note = "内容完全相同";
            return result;
        }

        var oldLines = SplitLines(oldText, out var oldTruncated);
        var newLines = SplitLines(newText, out var newTruncated);
        if (oldTruncated || newTruncated)
        {
            result.Truncated = true;
            result.Note = $"文件行数超过 {MaxLines}，仅比较前 {MaxLines} 行。";
        }

        // 公共前缀 / 后缀
        int prefix = 0;
        int maxPrefix = Math.Min(oldLines.Count, newLines.Count);
        while (prefix < maxPrefix &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        int maxSuffix = Math.Min(oldLines.Count - prefix, newLines.Count - prefix);
        while (suffix < maxSuffix &&
               string.Equals(oldLines[oldLines.Count - 1 - suffix], newLines[newLines.Count - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var script = new List<DiffLine>(oldLines.Count + newLines.Count);

        for (int i = 0; i < prefix; i++)
        {
            script.Add(NewContext(oldLines[i], i + 1, i + 1));
        }

        int oldMidStart = prefix;
        int oldMidCount = oldLines.Count - prefix - suffix;
        int newMidStart = prefix;
        int newMidCount = newLines.Count - prefix - suffix;

        var operations = Lcs(oldLines, oldMidStart, oldMidCount, newLines, newMidStart, newMidCount);

        // 把操作流转换为行对象，并成对做行内差异
        int oi = oldMidStart, ni = newMidStart;
        for (int i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            if (op == '=')
            {
                script.Add(NewContext(oldLines[oi], oi + 1, ni + 1));
                oi++; ni++;
            }
            else if (op == '-')
            {
                // 收集连续删除块，与紧随其后的新增块配对
                int delStart = oi;
                int delCount = 0;
                while (i < operations.Count && operations[i] == '-') { delCount++; i++; oi++; }
                int insStart = ni;
                int insCount = 0;
                while (i < operations.Count && operations[i] == '+') { insCount++; i++; ni++; }
                i--;

                EmitBlock(script, oldLines, delStart, delCount, newLines, insStart, insCount);
            }
            else // '+'
            {
                int delStart = oi;
                int insStart = ni;
                int insCount = 0;
                while (i < operations.Count && operations[i] == '+') { insCount++; i++; ni++; }
                i--;
                EmitBlock(script, oldLines, delStart, 0, newLines, insStart, insCount);
            }
        }

        for (int i = 0; i < suffix; i++)
        {
            int oiIdx = oldLines.Count - suffix + i;
            int niIdx = newLines.Count - suffix + i;
            script.Add(NewContext(oldLines[oiIdx], oiIdx + 1, niIdx + 1));
        }

        result.Lines.AddRange(script);
        result.AddedCount = script.Count(l => l.Kind == DiffLineKind.Added);
        result.RemovedCount = script.Count(l => l.Kind == DiffLineKind.Removed);
        if (!result.Truncated && result.Lines.Count > MaxLines * 2)
        {
            result.LineLevelOnly = false;
        }

        if (result.AreIdentical && !result.Truncated)
        {
            result.Note = "内容完全相同";
        }
        return result;
    }

    private static void EmitBlock(
        List<DiffLine> script,
        IReadOnlyList<string> oldLines, int delStart, int delCount,
        IReadOnlyList<string> newLines, int insStart, int insCount)
    {
        // 成对行内差异
        int pairs = Math.Min(delCount, insCount);
        for (int i = 0; i < pairs; i++)
        {
            var (oldChunks, newChunks) = IntraLine(oldLines[delStart + i], newLines[insStart + i]);
            script.Add(new DiffLine
            {
                Kind = DiffLineKind.Removed,
                Text = oldLines[delStart + i],
                OldLineNumber = delStart + i + 1,
                OldChunks = oldChunks,
            });
            script.Add(new DiffLine
            {
                Kind = DiffLineKind.Added,
                Text = newLines[insStart + i],
                NewLineNumber = insStart + i + 1,
                NewChunks = newChunks,
            });
        }

        for (int i = pairs; i < delCount; i++)
        {
            script.Add(new DiffLine
            {
                Kind = DiffLineKind.Removed,
                Text = oldLines[delStart + i],
                OldLineNumber = delStart + i + 1,
            });
        }

        for (int i = pairs; i < insCount; i++)
        {
            script.Add(new DiffLine
            {
                Kind = DiffLineKind.Added,
                Text = newLines[insStart + i],
                NewLineNumber = insStart + i + 1,
            });
        }
    }

    /// <summary>行内差异：去掉公共前后缀，标记中间变化区间。</summary>
    private static (IReadOnlyList<DiffChunk>?, IReadOnlyList<DiffChunk>?) IntraLine(string oldLine, string newLine)
    {
        if (oldLine.Length > MaxLineLengthForIntraHighlight || newLine.Length > MaxLineLengthForIntraHighlight)
            return (null, null);

        int prefix = 0;
        int max = Math.Min(oldLine.Length, newLine.Length);
        while (prefix < max && oldLine[prefix] == newLine[prefix]) prefix++;

        // 后缀比较必须限制在"去掉公共前缀之后还剩的较短长度"内，
        // 否则前缀与后缀区间会重叠，得到的区间就不止"变化的字符"（曾经少算一位）。
        int remaining = max - prefix;
        int suffix = 0;
        while (suffix < remaining &&
               oldLine[oldLine.Length - 1 - suffix] == newLine[newLine.Length - 1 - suffix])
        {
            suffix++;
        }

        int oldMid = oldLine.Length - prefix - suffix;
        int newMid = newLine.Length - prefix - suffix;
        if (oldMid <= 0 && newMid <= 0) return (null, null);

        var a = oldMid > 0 ? new[] { new DiffChunk { Start = prefix, Length = oldMid } } : null;
        var b = newMid > 0 ? new[] { new DiffChunk { Start = prefix, Length = newMid } } : null;
        return (a, b);
    }

    /// <summary>LCS 编辑脚本，返回由 '=' / '-' / '+' 组成的序列。</summary>
    private static List<char> Lcs(
        IReadOnlyList<string> oldLines, int oldStart, int oldCount,
        IReadOnlyList<string> newLines, int newStart, int newCount)
    {
        var ops = new List<char>(oldCount + newCount);

        if (oldCount == 0)
        {
            ops.AddRange(Enumerable.Repeat('+', newCount));
            return ops;
        }
        if (newCount == 0)
        {
            ops.AddRange(Enumerable.Repeat('-', oldCount));
            return ops;
        }

        // 完整 DP 表（规模受 MaxLines 保护；50k×50k 不允许，故这里用阈值降级为"整块替换"）
        const long cellBudget = 4_000_000; // ≈ 4M ints ≈ 16MB
        if ((long)oldCount * newCount > cellBudget)
        {
            // 降级：不做细粒度 LCS，输出"全删 + 全增"（仍然正确，只是不如 LCS 好看）
            for (int i = 0; i < oldCount; i++) ops.Add('-');
            for (int i = 0; i < newCount; i++) ops.Add('+');
            return ops;
        }

        var dp = new int[oldCount + 1, newCount + 1];
        for (int i = oldCount - 1; i >= 0; i--)
        {
            for (int j = newCount - 1; j >= 0; j--)
            {
                if (string.Equals(oldLines[oldStart + i], newLines[newStart + j], StringComparison.Ordinal))
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < oldCount && y < newCount)
        {
            if (string.Equals(oldLines[oldStart + x], newLines[newStart + y], StringComparison.Ordinal))
            {
                ops.Add('=');
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add('-');
                x++;
            }
            else
            {
                ops.Add('+');
                y++;
            }
        }
        while (x < oldCount) { ops.Add('-'); x++; }
        while (y < newCount) { ops.Add('+'); y++; }
        return ops;
    }

    private static DiffLine NewContext(string text, int oldNo, int newNo) => new()
    {
        Kind = DiffLineKind.Context,
        Text = text,
        OldLineNumber = oldNo,
        NewLineNumber = newNo,
    };

    /// <summary>
    /// 按行拆分成行数组（保留空行；支持 \r\n、\n、\r）。
    /// 空文本返回**空数组**（而不是含一个空字符串的数组），
    /// 否则"空文件 ↔ 有内容"会被算成 −1/+N，凭空多出一条删除行。
    /// </summary>
    public static List<string> SplitLines(string text, out bool truncated)
    {
        truncated = false;
        var lines = new List<string>();
        if (text.Length == 0) return lines;

        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
                if (lines.Count >= MaxLines)
                {
                    truncated = true;
                    break;
                }
            }
        }
        if (!truncated && start < text.Length)
        {
            lines.Add(text[start..]);
        }
        return lines;
    }
}
