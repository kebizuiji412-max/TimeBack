namespace LastRegret.Core.Compare;

using LastRegret.Core.Model;
using LastRegret.Core.Util;

/// <summary>
/// 两个清单之间的差异计算。
///
/// 纯函数、无 IO、无数据库依赖 —— 因此可以大量单元测试，
/// 也是"恢复预览"与"状态对比"两个功能的共同底座。
///
/// 重命名识别策略：
///  清单对比本身只看"路径 + 内容"，无法判断 A→B 是改名还是"删 A 建 B"。
///  因此本类支持传入**事件证据**（rename/move 事件）来确认重命名；
///  没有证据时，绝不会凭空把"删除一个 + 新增一个"说成重命名
///  （遵守《记录事实，不伪造因果》）。
/// </summary>
public static class TreeComparer
{
    /// <summary>默认参与对比的最大条目数，超过则截断并明确告知（避免 UI 假死）。</summary>
    public const int DefaultMaxChanges = 200_000;

    /// <summary>重命名识别所需的证据（由事件日志提供）。</summary>
    public sealed class RenameEvidence
    {
        /// <summary>旧路径 → 新路径（大小写不敏感）。</summary>
        public Dictionary<string, string> OldToNew { get; } = new(PathUtil.Comparer);

        /// <summary>新路径 → 旧路径。</summary>
        public Dictionary<string, string> NewToOld { get; } = new(PathUtil.Comparer);

        public void Add(string oldPath, string newPath)
        {
            var o = PathUtil.NormalizeRelative(oldPath);
            var n = PathUtil.NormalizeRelative(newPath);
            if (o.Length == 0 || n.Length == 0 || PathUtil.Comparer.Equals(o, n)) return;
            OldToNew[o] = n;
            NewToOld[n] = o;
        }
    }

    /// <param name="from">基准状态（例如 22:04 的快照）。</param>
    /// <param name="to">对比状态（例如当前状态）。</param>
    /// <param name="renameEvidence">可选的重命名证据。</param>
    /// <param name="maxChanges">结果上限。</param>
    public static TreeDiff Compare(
        FileTreeManifest from,
        FileTreeManifest to,
        RenameEvidence? renameEvidence = null,
        int maxChanges = DefaultMaxChanges)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var diff = new TreeDiff { From = from, To = to };

        // 路径集合的并集（大小写不敏感去重：以 from 的原始拼写优先）
        var allPaths = new HashSet<string>(PathUtil.Comparer);
        foreach (var p in from.Entries.Keys) allPaths.Add(p);
        foreach (var p in to.Entries.Keys) allPaths.Add(p);
        diff.ComparedCount = allPaths.Count;

        // 先做纯粹的"路径对比"
        var rawChanges = new List<TreeChange>();
        foreach (var path in allPaths)
        {
            var before = from.Find(path);
            var after = to.Find(path);

            if (before is null && after is not null)
            {
                rawChanges.Add(new TreeChange
                {
                    Kind = ChangeKind.Added,
                    RelativePath = after.RelativePath,
                    KindAfter = after.Kind,
                    SizeAfter = after.Size,
                    HashAfter = after.Hash,
                    ObjectIdAfter = after.ObjectId,
                    FirstSeenUtc = after.FirstSeenUtc,
                    RootId = to.RootId,
                });
            }
            else if (before is not null && after is null)
            {
                rawChanges.Add(new TreeChange
                {
                    Kind = ChangeKind.Deleted,
                    RelativePath = before.RelativePath,
                    KindBefore = before.Kind,
                    SizeBefore = before.Size,
                    HashBefore = before.Hash,
                    ObjectIdBefore = before.ObjectId,
                    FirstSeenUtc = before.FirstSeenUtc,
                    RootId = from.RootId,
                });
            }
            else if (before is not null && after is not null)
            {
                if (before.Kind != after.Kind)
                {
                    rawChanges.Add(new TreeChange
                    {
                        Kind = ChangeKind.TypeChanged,
                        RelativePath = after.RelativePath,
                        KindBefore = before.Kind,
                        KindAfter = after.Kind,
                        SizeBefore = before.Size,
                        SizeAfter = after.Size,
                        HashBefore = before.Hash,
                        HashAfter = after.Hash,
                        ObjectIdBefore = before.ObjectId,
                        ObjectIdAfter = after.ObjectId,
                        FirstSeenUtc = after.FirstSeenUtc,
                        RootId = to.RootId,
                    });
                }
                else if (!ContentEquals(before, after))
                {
                    rawChanges.Add(new TreeChange
                    {
                        Kind = ChangeKind.Modified,
                        RelativePath = after.RelativePath,
                        KindBefore = before.Kind,
                        KindAfter = after.Kind,
                        SizeBefore = before.Size,
                        SizeAfter = after.Size,
                        HashBefore = before.Hash,
                        HashAfter = after.Hash,
                        ObjectIdBefore = before.ObjectId,
                        ObjectIdAfter = after.ObjectId,
                        FirstSeenUtc = after.FirstSeenUtc,
                        RootId = to.RootId,
                    });
                }
            }
        }

        // 用事件证据把 "删 A + 增 B" 合并为 "重命名 A→B"
        if (renameEvidence is not null && renameEvidence.NewToOld.Count > 0)
        {
            MergeRenames(rawChanges, renameEvidence);
        }

        rawChanges.Sort(ChangeOrder);

        if (rawChanges.Count > maxChanges)
        {
            diff.Truncated = true;
            diff.TruncationReason = $"变化条目过多（{rawChanges.Count}），仅展示前 {maxChanges} 条。";
            rawChanges.RemoveRange(maxChanges, rawChanges.Count - maxChanges);
        }

        diff.Changes.AddRange(rawChanges);
        return diff;
    }

    /// <summary>内容是否相同：优先比较哈希；哈希缺失时退化为大小 + mtime 比较（并在结果中标注不确定）。</summary>
    private static bool ContentEquals(ManifestEntry a, ManifestEntry b)
    {
        if (a.Kind == EntryKind.Directory && b.Kind == EntryKind.Directory) return true;

        if (a.Hash is not null && b.Hash is not null)
            return string.Equals(a.Hash, b.Hash, StringComparison.OrdinalIgnoreCase);

        // 无哈希（例如超大文件未留存内容）：只能用大小 + mtime 近似
        if (a.Size != b.Size) return false;
        return a.MtimeUtc == b.MtimeUtc;
    }

    private static void MergeRenames(List<TreeChange> changes, RenameEvidence evidence)
    {
        var addedByPath = new Dictionary<string, TreeChange>(PathUtil.Comparer);
        var deletedByPath = new Dictionary<string, TreeChange>(PathUtil.Comparer);
        foreach (var c in changes)
        {
            if (c.Kind == ChangeKind.Added) addedByPath[c.RelativePath] = c;
            else if (c.Kind == ChangeKind.Deleted) deletedByPath[c.RelativePath] = c;
        }

        if (addedByPath.Count == 0 || deletedByPath.Count == 0) return;

        var consumed = new HashSet<TreeChange>();
        foreach (var (newPath, added) in addedByPath)
        {
            if (!evidence.NewToOld.TryGetValue(newPath, out var oldPath)) continue;
            if (!deletedByPath.TryGetValue(oldPath, out var deleted)) continue;
            // 内容必须一致（否则是"改名 + 改内容"，本版按改名 + 修改两条事实分别呈现更安全）
            if (!HashesCompatible(deleted.HashBefore, added.HashAfter)) continue;

            consumed.Add(added);
            consumed.Add(deleted);
            changes.Add(new TreeChange
            {
                Kind = ChangeKind.Renamed,
                RelativePath = added.RelativePath,
                OldRelativePath = deleted.RelativePath,
                KindBefore = deleted.KindBefore,
                KindAfter = added.KindAfter,
                SizeBefore = deleted.SizeBefore,
                SizeAfter = added.SizeAfter,
                HashBefore = deleted.HashBefore,
                HashAfter = added.HashAfter,
                ObjectIdBefore = deleted.ObjectIdBefore,
                ObjectIdAfter = added.ObjectIdAfter,
                RootId = added.RootId,
            });
        }

        if (consumed.Count > 0) changes.RemoveAll(consumed.Contains);
    }

    private static bool HashesCompatible(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>展览顺序：先显示"删除/修改"这类用户最关心的，再按路径排序。</summary>
    private static int ChangeOrder(TreeChange x, TreeChange y)
    {
        var k = Rank(x.Kind).CompareTo(Rank(y.Kind));
        if (k != 0) return k;
        return string.Compare(x.RelativePath, y.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(ChangeKind k) => k switch
    {
        ChangeKind.Deleted => 0,
        ChangeKind.Modified => 1,
        ChangeKind.Renamed => 2,
        ChangeKind.TypeChanged => 3,
        ChangeKind.Added => 4,
        _ => 5,
    };
}
