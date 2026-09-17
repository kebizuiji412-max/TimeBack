namespace LastRegret.Core.Abstractions;

using LastRegret.Core.Model;

/// <summary>
/// 文件路径索引：记录受保护范围内"当前有哪些路径、内容是什么"。
/// 事件处理器依赖它来判定 Created / Modified / Renamed，
/// 快照构建依赖它来增量更新清单。
/// </summary>
public interface IFileIndex
{
    /// <summary>查询某路径的当前状态；不存在返回 null。</summary>
    IndexEntry? Get(long rootId, string relativePath);

    /// <summary>更新（或插入）路径状态。</summary>
    void Upsert(long rootId, IndexEntry entry);

    /// <summary>批量更新（单个事务内完成；扫描建立基线时用它减少事务开销）。</summary>
    void UpsertMany(long rootId, IReadOnlyList<IndexEntry> entries);

    /// <summary>标记删除（软删除，保留历史可查）。</summary>
    void MarkDeleted(long rootId, string relativePath, DateTime atUtc);

    /// <summary>把一棵子树整体迁移到新前缀（目录重命名/移动）。返回受影响的条目数。</summary>
    int MoveSubtree(long rootId, string oldPrefix, string newPrefix, DateTime atUtc);

    /// <summary>按前缀批量标记删除（目录删除）。返回受影响的条目数。</summary>
    int MarkSubtreeDeleted(long rootId, string prefix, DateTime atUtc);

    /// <summary>列出根下的全部当前条目（用于构建清单）。</summary>
    IReadOnlyList<IndexEntry> ListAll(long rootId);

    /// <summary>
    /// 列出某条路径及其子树的**当前有效**条目（不含已删除项，包含该路径自身）。
    ///
    /// 快照增量构建需要它：目录级事件（新建目录 / 目录改名）只会给出父目录一个路径，
    /// 但清单里必须带上它当前的整棵子树，否则子项会缺失。
    /// </summary>
    IReadOnlyList<IndexEntry> ListUnder(long rootId, string prefix);

    /// <summary>当前索引中的条目总数。</summary>
    long CountAll(long rootId);

    /// <summary>
    /// 索引中已反映的最大事件 Id（即"当前状态"对应的水位线）。
    /// 快照用它作为增量锚点，避免"事件尚未落库就被快照越过"导致漏记变化。
    /// </summary>
    long GetMaxEventId(long rootId);
}

/// <summary>索引中的一条路径状态。</summary>
public sealed class IndexEntry
{
    public long RootId { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public EntryKind Kind { get; set; }

    public long Size { get; set; }

    public string? Hash { get; set; }

    public long? ObjectId { get; set; }

    public DateTime? MtimeUtc { get; set; }

    /// <summary>该路径首次被观察到的时刻。</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>最近一次发生变化的时刻。</summary>
    public DateTime LastChangedUtc { get; set; }

    /// <summary>最近一次变化对应的事件 Id。</summary>
    public long? LastEventId { get; set; }

    public bool IsDeleted { get; set; }

    public bool IsReadOnly { get; set; }
}
