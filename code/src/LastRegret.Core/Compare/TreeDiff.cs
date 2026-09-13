namespace LastRegret.Core.Compare;

using LastRegret.Core.Model;

/// <summary>两个状态之间某个路径的差异类别。</summary>
public enum ChangeKind
{
    /// <summary>目标状态里没有，当前有（恢复时应移除）。</summary>
    Added = 0,

    /// <summary>目标状态里有，当前没有（恢复时应写回）。</summary>
    Deleted = 1,

    /// <summary>两侧都有但内容不同（恢复时应覆盖）。</summary>
    Modified = 2,

    /// <summary>路径改变了但内容相同（仅由事件关联推断，不能由清单对比得出）。</summary>
    Renamed = 3,

    /// <summary>类型改变（文件 ↔ 目录）。</summary>
    TypeChanged = 4,
}

/// <summary>
/// ChangeKind 的展示与序列化辅助。
/// 注意：必须与 <see cref="ChangeKind"/> 位于同一命名空间（LastRegret.Core.Compare），
/// 否则 `using LastRegret.Core.Compare;` 无法引入这些扩展方法（踩坑记录 PIT）。
/// </summary>
public static class ChangeKindExtensions
{
    public static string ToChinese(this ChangeKind k) => k switch
    {
        ChangeKind.Added => "新增",
        ChangeKind.Deleted => "删除",
        ChangeKind.Modified => "修改",
        ChangeKind.Renamed => "重命名",
        ChangeKind.TypeChanged => "类型变化",
        _ => "变化",
    };

    /// <summary>UI 着色用的语义键。</summary>
    public static string ToToneKey(this ChangeKind k) => k switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Modified => "modified",
        ChangeKind.Renamed => "renamed",
        ChangeKind.TypeChanged => "typechanged",
        _ => "unknown",
    };

    public static string ToCode(this ChangeKind k) => k switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Modified => "modified",
        ChangeKind.Renamed => "renamed",
        ChangeKind.TypeChanged => "typechanged",
        _ => "unknown",
    };
}

/// <summary>清单中的一条状态（内容对象已解析，可独立于数据库直接使用）。</summary>
public sealed class ManifestEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public EntryKind Kind { get; set; }

    public long Size { get; set; }

    public string? Hash { get; set; }

    /// <summary>CAS 对象 Id；内容已被清理时为 null（此时哈希仍保留，用于对比但不显示 Diff）。</summary>
    public long? ObjectId { get; set; }

    public DateTime? MtimeUtc { get; set; }

    /// <summary>该路径首次出现的时刻（UTC）。null 表示未知（不得当作"新"处理）。</summary>
    public DateTime? FirstSeenUtc { get; set; }

    public string Name => LastRegret.Core.Util.PathUtil.NameOf(RelativePath);
}

/// <summary>某一时刻受保护范围的完整状态。</summary>
public sealed class FileTreeManifest
{
    public long RootId { get; set; }

    public DateTime TimestampUtc { get; set; }

    public DateTime TimestampLocal { get; set; }

    public long? SnapshotId { get; set; }

    /// <summary>路径 → 条目（路径比较大小写不敏感）。</summary>
    public Dictionary<string, ManifestEntry> Entries { get; } = new(LastRegret.Core.Util.PathUtil.Comparer);

    public int FileCount => Entries.Values.Count(e => e.Kind == EntryKind.File);

    public int DirectoryCount => Entries.Values.Count(e => e.Kind == EntryKind.Directory);

    public long TotalBytes => Entries.Values.Where(e => e.Kind == EntryKind.File).Sum(e => e.Size);

    public void Add(ManifestEntry entry) => Entries[entry.RelativePath] = entry;

    public ManifestEntry? Find(string relativePath) =>
        Entries.TryGetValue(relativePath, out var e) ? e : null;

    /// <summary>
    /// 取一份**冻结的副本**。
    ///
    /// 用途与原因（真实缺陷，由测试暴露）：恢复计划的冲突检测基线必须是
    /// "用户点击预览那一刻看到的当前状态"。若直接持有索引来源的清单对象，
    /// 一旦索引在预览之后被更新（例如用户在确认前又改了文件），
    /// 基线会跟着变，冲突就检测不出来 —— 结果是**静默覆盖用户的新内容**。
    /// 因此预览生成时立即复制一份只读快照。
    /// </summary>
    public FileTreeManifest Freeze()
    {
        var copy = new FileTreeManifest
        {
            RootId = RootId,
            TimestampUtc = TimestampUtc,
            TimestampLocal = TimestampLocal,
            SnapshotId = SnapshotId,
        };
        foreach (var (key, e) in Entries)
        {
            copy.Entries[key] = new ManifestEntry
            {
                RelativePath = e.RelativePath,
                Kind = e.Kind,
                Size = e.Size,
                Hash = e.Hash,
                ObjectId = e.ObjectId,
                MtimeUtc = e.MtimeUtc,
                FirstSeenUtc = e.FirstSeenUtc,
            };
        }
        return copy;
    }

    public static FileTreeManifest FromSnapshot(Snapshot snapshot, IEnumerable<SnapshotFile> files)
    {
        var m = new FileTreeManifest
        {
            RootId = snapshot.RootId,
            TimestampUtc = snapshot.TimestampUtc,
            TimestampLocal = snapshot.TimestampLocal,
            SnapshotId = snapshot.Id,
        };
        foreach (var f in files)
        {
            m.Add(new ManifestEntry
            {
                RelativePath = f.RelativePath,
                Kind = f.Kind,
                Size = f.Size,
                Hash = f.Hash,
                ObjectId = f.ObjectId,
                MtimeUtc = f.MtimeUtc,
                FirstSeenUtc = f.FirstSeenUtc,
            });
        }
        return m;
    }
}

/// <summary>一个路径的差异详情。</summary>
public sealed class TreeChange
{
    public ChangeKind Kind { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>重命名/移动时的旧路径。</summary>
    public string? OldRelativePath { get; set; }

    public EntryKind? KindBefore { get; set; }

    public EntryKind? KindAfter { get; set; }

    public long? SizeBefore { get; set; }

    public long? SizeAfter { get; set; }

    public string? HashBefore { get; set; }

    public string? HashAfter { get; set; }

    public long? ObjectIdBefore { get; set; }

    public long? ObjectIdAfter { get; set; }

    /// <summary>该变化最近一次发生的时刻（用于排序与展示）。</summary>
    public DateTime? LastEventUtc { get; set; }

    /// <summary>该路径首次出现的时刻（UTC）。null = 未知，恢复预览据此判断"是否属于目标时刻之后的用户新增"。</summary>
    public DateTime? FirstSeenUtc { get; set; }

    /// <summary>关联进程摘要（可能来源）。</summary>
    public string? AttributionSummary { get; set; }

    /// <summary>该变化涉及的受保护根目录 Id（多根时用于分组）。</summary>
    public long RootId { get; set; }

    public string Name => LastRegret.Core.Util.PathUtil.NameOf(RelativePath);

    /// <summary>内容是否可在本地取到（用于判断"能否比较/恢复"）。</summary>
    public bool CanCompareContent =>
        Kind is ChangeKind.Modified or ChangeKind.Added or ChangeKind.Deleted
        && ((ObjectIdBefore is not null) || (ObjectIdAfter is not null));

    public bool CanRestoreContent => ObjectIdAfter is not null || Kind == ChangeKind.Deleted;
}

/// <summary>两个状态之间的完整差异集合。</summary>
public sealed class TreeDiff
{
    public FileTreeManifest From { get; set; } = new();

    public FileTreeManifest To { get; set; } = new();

    public List<TreeChange> Changes { get; } = new();

    /// <summary>有多少条目参与了对比。</summary>
    public int ComparedCount { get; set; }

    /// <summary>对比是否因为规模过大而被截断（不静默：UI 必须提示）。</summary>
    public bool Truncated { get; set; }

    public string? TruncationReason { get; set; }

    public int CountOf(ChangeKind kind) => Changes.Count(c => c.Kind == kind);

    public int AddedCount => CountOf(ChangeKind.Added);

    public int DeletedCount => CountOf(ChangeKind.Deleted);

    public int ModifiedCount => CountOf(ChangeKind.Modified);

    public int RenamedCount => CountOf(ChangeKind.Renamed);

    public int ChangedCount => Changes.Count(c => c.Kind != ChangeKind.Renamed || c.HashBefore != c.HashAfter);

    public int TotalCount => Changes.Count;

    public bool IsEmpty => Changes.Count == 0;

    /// <summary>涉及的总字节变化量（目标相对当前的净增）。</summary>
    public long NetBytes
    {
        get
        {
            long net = 0;
            foreach (var c in Changes)
            {
                net += (c.SizeAfter ?? 0) - (c.SizeBefore ?? 0);
            }
            return net;
        }
    }
}
