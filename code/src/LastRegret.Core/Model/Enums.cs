namespace LastRegret.Core.Model;

/// <summary>
/// 受保护范围内被识别出的文件系统操作类型。
/// </summary>
/// <remarks>
/// 设计原则（《记录事实，不伪造因果》）：
/// 这里只描述「文件系统层面确实发生了什么」，不描述「谁出于什么目的做的」。
/// 一次编辑器保存可能同时产生 <see cref="OperationType.Modified"/> 或
/// <see cref="OperationType.Renamed"/>（原子替换），二者都是事实，不得强行归并成一种。
/// </remarks>
public enum OperationType
{
    /// <summary>文件或目录从「不存在」变为「存在」。</summary>
    Created = 0,

    /// <summary>已存在的文件内容发生变化（哈希改变）。</summary>
    Modified = 1,

    /// <summary>文件或目录从「存在」变为「不存在」。</summary>
    Deleted = 2,

    /// <summary>同一目录内改名（父目录不变）。</summary>
    Renamed = 3,

    /// <summary>跨目录移动（父目录改变）。</summary>
    Moved = 4,

    /// <summary>无法归入以上类别的变化（例如元数据变化、无法读取的瞬时状态）。</summary>
    Unknown = 5,

    /// <summary>
    /// 一次「写入后未留下稳定状态」的操作（例如编辑器临时文件被写后立刻删除）。
    /// 默认在时间线上折叠，不干扰用户判断。
    /// </summary>
    Transient = 6,
}

/// <summary>受保护条目的类型。</summary>
public enum EntryKind
{
    File = 0,
    Directory = 1,
}

public static class OperationTypeExtensions
{
    public static string ToChinese(this OperationType op) => op switch
    {
        OperationType.Created => "创建",
        OperationType.Modified => "修改",
        OperationType.Deleted => "删除",
        OperationType.Renamed => "重命名",
        OperationType.Moved => "移动",
        OperationType.Transient => "瞬时变化",
        _ => "未知变化",
    };

    /// <summary>数据库持久化用的稳定字符串（不要依赖 enum 数值，避免将来插入枚举项导致历史数据错位）。</summary>
    public static string ToCode(this OperationType op) => op switch
    {
        OperationType.Created => "created",
        OperationType.Modified => "modified",
        OperationType.Deleted => "deleted",
        OperationType.Renamed => "renamed",
        OperationType.Moved => "moved",
        OperationType.Transient => "transient",
        _ => "unknown",
    };

    public static OperationType FromCode(string? code) => code switch
    {
        "created" => OperationType.Created,
        "modified" => OperationType.Modified,
        "deleted" => OperationType.Deleted,
        "renamed" => OperationType.Renamed,
        "moved" => OperationType.Moved,
        "transient" => OperationType.Transient,
        _ => OperationType.Unknown,
    };
}

public static class EntryKindExtensions
{
    public static string ToCode(this EntryKind k) => k == EntryKind.Directory ? "dir" : "file";

    public static EntryKind FromCode(string? code) => code == "dir" ? EntryKind.Directory : EntryKind.File;

    public static string ToChinese(this EntryKind k) => k == EntryKind.Directory ? "目录" : "文件";
}
