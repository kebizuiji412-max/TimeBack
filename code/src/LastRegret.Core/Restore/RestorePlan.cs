namespace LastRegret.Core.Restore;

using LastRegret.Core.Compare;
using LastRegret.Core.Model;

/// <summary>
/// 恢复计划中的一个条目。
/// 计划是"用户确认过的内容"，执行阶段必须严格按计划执行；
/// 若执行时发现磁盘状态已变（<see cref="RestoreStep.ExpectedCurrentHash"/> 不符），
/// 必须跳过并报告，绝不能静默覆盖。
/// </summary>
public sealed class RestoreStep
{
    public RestoreAction Action { get; set; }

    /// <summary>本步骤操作的主路径（相对）。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>重命名回退时涉及的次要路径。</summary>
    public string? SecondaryPath { get; set; }

    public EntryKind Kind { get; set; } = EntryKind.File;

    /// <summary>目标内容哈希（写入后应达到）。</summary>
    public string? TargetHash { get; set; }

    /// <summary>目标内容的 CAS 对象 Id。</summary>
    public long? TargetObjectId { get; set; }

    public long? TargetSize { get; set; }

    /// <summary>计划生成时磁盘上的内容哈希；执行前重新核对，可发现"预览之后又被改过"。</summary>
    public string? ExpectedCurrentHash { get; set; }

    /// <summary>目标内容是否可用（CAS 中被清理过则不可恢复）。</summary>
    public bool ContentAvailable { get; set; }

    /// <summary>人类可读说明，UI 展示。</summary>
    public string Description { get; set; } = string.Empty;

    public ChangeKind SourceChange { get; set; }

    public long RootId { get; set; }

    /// <summary>是否需要用户额外确认（例如要删除一个"当前存在但目标状态不存在"的路径）。</summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>该步骤被跳过的原因（计划阶段预判，例如内容已被清理）。</summary>
    public string? Warning { get; set; }
}

/// <summary>恢复预览：给用户看的完整事实。</summary>
public sealed class RestorePlan
{
    public long RootId { get; set; }

    public string RootPath { get; set; } = string.Empty;

    public DateTime TargetTimeUtc { get; set; }

    public DateTime TargetTimeLocal { get; set; }

    public long? TargetSnapshotId { get; set; }

    public List<RestoreStep> Steps { get; } = new();

    /// <summary>预览生成时刻的当前清单。</summary>
    public FileTreeManifest Current { get; set; } = new();

    public FileTreeManifest Target { get; set; } = new();

    /// <summary>可执行 / 不可执行 / 需确认 的分类统计。</summary>
    public int ExecutableCount => Steps.Count(s => s.ContentAvailable || s.Action is RestoreAction.RemovePath or RestoreAction.RemoveDirectory or RestoreAction.CreateDirectory);

    public int UnavailableCount => Steps.Count(s => !s.ContentAvailable && s.Action == RestoreAction.RestoreContent);

    public int ConfirmationCount => Steps.Count(s => s.RequiresConfirmation);

    public int RemoveCount => Steps.Count(s => s.Action == RestoreAction.RemovePath);

    public int RestoreCount => Steps.Count(s => s.Action == RestoreAction.RestoreContent);

    public int CreateDirectoryCount => Steps.Count(s => s.Action == RestoreAction.CreateDirectory);

    public int RemoveDirectoryCount => Steps.Count(s => s.Action == RestoreAction.RemoveDirectory);

    public long BytesToWrite => Steps.Where(s => s.Action == RestoreAction.RestoreContent).Sum(s => s.TargetSize ?? 0);

    public long BytesToRemove => Steps.Where(s => s.Action == RestoreAction.RemovePath).Sum(s => s.TargetSize ?? 0);

    public List<string> Warnings { get; } = new();

    /// <summary>预览指纹：确认时校验用户看到的就是即将执行的内容。</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    public bool IsEmpty => Steps.Count == 0;

    /// <summary>计划是否真的能改变磁盘（全为 NoChange 时为 false）。</summary>
    public bool HasEffect => Steps.Any(s => s.Action != RestoreAction.NoChange);

    /// <summary>按"影响范围"聚合的目录前缀（UI 显示"预计影响 D:\Project\..."）。</summary>
    public List<string> AffectedPrefixes(int max = 12)
    {
        var groups = new Dictionary<string, int>(LastRegret.Core.Util.PathUtil.Comparer);
        foreach (var s in Steps)
        {
            if (s.Action == RestoreAction.NoChange) continue;
            var top = TopSegments(s.RelativePath, 2);
            groups[top] = groups.TryGetValue(top, out var v) ? v + 1 : 1;
        }
        return groups.OrderByDescending(kv => kv.Value)
                     .Take(max)
                     .Select(kv => $"{kv.Key}  ({kv.Value} 项)")
                     .ToList();
    }

    private static string TopSegments(string relativePath, int count)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "(根目录)";
        return string.Join('\\', parts.Take(Math.Min(count, parts.Length)));
    }

    /// <summary>计算（或重算）指纹。指纹对步骤顺序不敏感，只取决于内容集合。</summary>
    public string ComputeFingerprint()
    {
        ulong acc = 14695981039346656037UL; // FNV offset
        ulong xor = 0;
        foreach (var s in Steps)
        {
            var key = $"{s.Action.ToCode()}|{s.RelativePath}|{s.SecondaryPath}|{s.TargetHash}|{s.TargetObjectId}|{s.Kind.ToCode()}";
            var h = Fnv1a(key);
            acc = Fnv1a(acc.ToString() + key);
            xor ^= h;
        }
        Fingerprint = $"{acc:X16}{xor:X16}";
        return Fingerprint;
    }

    private static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }
}
