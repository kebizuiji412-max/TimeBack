namespace LastRegret.Core.Restore;

using LastRegret.Core.Compare;
using LastRegret.Core.Model;
using LastRegret.Core.Util;

/// <summary>
/// 内容解析：把内容哈希解析为本地可用的对象 Id。
/// 由数据层实现（查询 CAS 索引）；预览阶段用它判断"这条能不能真的恢复"。
/// </summary>
public interface IContentResolver
{
    /// <summary>哈希 → 对象 Id；不可用返回 null。</summary>
    long? ResolveObjectId(string hash);

    /// <summary>对象是否仍然存在于磁盘（防止索引与磁盘不一致）。</summary>
    bool IsObjectAvailable(long objectId);
}

/// <summary>安全守卫：恢复引擎必须遵守的边界。</summary>
public static class RestoreGuard
{
    /// <summary>本程序的私有目录名（绝不允许被恢复操作触碰）。</summary>
    public const string AppStorageDirName = "LastRegret";

    private static readonly string[] SystemCriticalPrefixes =
    {
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData\Microsoft",
    };

    /// <summary>
    /// 判断某条恢复动作是否落在"系统关键目录"内。
    /// 第一版**不禁止**用户保护这些目录（用户可能确实需要），但预览必须给出强提醒。
    /// </summary>
    public static bool IsSystemCriticalPath(string absolutePath, out string matchedPrefix)
    {
        foreach (var p in SystemCriticalPrefixes)
        {
            if (absolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                matchedPrefix = p;
                return true;
            }
        }
        matchedPrefix = string.Empty;
        return false;
    }

    /// <summary>判断路径是否位于程序自身的历史仓库内（必须硬性禁止）。</summary>
    public static bool IsAppStoragePath(string absolutePath, string storageRoot)
    {
        if (string.IsNullOrEmpty(storageRoot)) return false;
        var a = PathUtil.NormalizeRoot(absolutePath);
        var s = PathUtil.NormalizeRoot(storageRoot);
        return string.Equals(a, s, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(s.EndsWith('\\') ? s : s + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 恢复计划生成器：把"状态差异"翻译成"用户可确认的恢复步骤清单"。
///
/// 三条硬规则：
///  1. **只做差异**：绝不整目录覆盖。计划里的每一步都能对应用户看到的一条差异。
///  2. **不确定就提示**：要删除"目标时刻之后才出现的新路径"、或要覆盖"预览后又被改过的文件"，
///     一律标记 <see cref="RestoreStep.RequiresConfirmation"/>，绝不静默执行。
///  3. **内容不可用就说不可用**：CAS 中已清理的内容，步骤仍列出但标记为不可恢复，
///     绝不假装能恢复（《可恢复就明确写可恢复》）。
/// </summary>
public sealed class RestorePlanner
{
    private readonly IContentResolver _content;

    public RestorePlanner(IContentResolver content) => _content = content;

    /// <summary>
    /// 生成恢复计划。
    /// </summary>
    /// <param name="diff">目标状态 vs 当前状态（From = 目标时刻，To = 当前）。</param>
    /// <param name="target">目标时刻的状态清单。</param>
    /// <param name="current">当前状态清单。</param>
    /// <param name="options">可选策略。</param>
    public RestorePlan Plan(TreeDiff diff, FileTreeManifest target, FileTreeManifest current, RestoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(current);
        options ??= new RestoreOptions();

        var plan = new RestorePlan
        {
            RootId = target.RootId,
            RootPath = options.RootPath ?? string.Empty,
            TargetTimeUtc = target.TimestampUtc,
            TargetTimeLocal = target.TimestampLocal,
            TargetSnapshotId = target.SnapshotId,
            Current = current.Freeze(),
            Target = target,
        };

        foreach (var change in diff.Changes)
        {
            switch (change.Kind)
            {
                case ChangeKind.Deleted:
                    AddRestore(plan, change, current, options);
                    break;

                case ChangeKind.Modified:
                    AddRestore(plan, change, current, options);
                    break;

                case ChangeKind.TypeChanged:
                    // 类型变化：先移除现有实体，再按目标类型重建
                    if (change.KindAfter == EntryKind.Directory)
                    {
                        plan.Steps.Add(RemoveStep(change, "移除同名文件，改为目录", options,
                            expectedCurrentHash: current.Find(change.RelativePath)?.Hash));
                        plan.Steps.Add(DirectoryStep(change, options));
                    }
                    else
                    {
                        plan.Steps.Add(RemoveStep(change, "移除同名目录，改为文件", options,
                            kindOverride: EntryKind.Directory,
                            expectedCurrentHash: current.Find(change.RelativePath)?.Hash));
                        AddRestore(plan, change, current, options);
                    }
                    break;

                case ChangeKind.Renamed:
                    // 目标里 A 存在、B 不存在 → 移除 B，恢复 A
                    if (change.OldRelativePath is { Length: > 0 } oldPath)
                    {
                        var oldEntry = target.Find(oldPath);
                        if (oldEntry is not null)
                        {
                            var restoreChange = new TreeChange
                            {
                                Kind = ChangeKind.Deleted,
                                RelativePath = oldPath,
                                KindAfter = oldEntry.Kind,
                                SizeAfter = oldEntry.Size,
                                HashAfter = oldEntry.Hash,
                                ObjectIdAfter = oldEntry.ObjectId,
                                FirstSeenUtc = oldEntry.FirstSeenUtc,
                                RootId = change.RootId,
                            };
                            AddRestore(plan, restoreChange, current, options);
                        }
                    }
                    plan.Steps.Add(RemoveStep(change, "移除重命名后的路径（目标状态中不存在）", options,
                        kindOverride: change.KindAfter ?? EntryKind.File,
                        expectedCurrentHash: current.Find(change.RelativePath)?.Hash));
                    break;

                case ChangeKind.Added:
                    // 当前存在、目标不存在 → 需要移除（删除是敏感操作，需确认）
                    plan.Steps.Add(RemoveStep(change, "当前存在但目标状态中不存在", options,
                        expectedCurrentHash: current.Find(change.RelativePath)?.Hash));
                    break;
            }
        }

        // 目标状态中缺少的目录（当前存在且为空）→ 底部向上尝试删除
        foreach (var change in diff.Changes.Where(c => c.Kind == ChangeKind.Added && c.KindAfter == EntryKind.Directory))
        {
            plan.Steps.Add(new RestoreStep
            {
                Action = RestoreAction.RemoveDirectory,
                RelativePath = change.RelativePath,
                Kind = EntryKind.Directory,
                ContentAvailable = true,
                SourceChange = change.Kind,
                RootId = change.RootId,
                Description = "目录在目标状态中不存在（仅在为空时移除，非空则保留并提示）",
                RequiresConfirmation = false,
            });
        }

        // 需要在目标状态中重建的目录
        foreach (var change in diff.Changes.Where(c => c.Kind == ChangeKind.Deleted && c.KindAfter == EntryKind.Directory))
        {
            plan.Steps.Add(new RestoreStep
            {
                Action = RestoreAction.CreateDirectory,
                RelativePath = change.RelativePath,
                Kind = EntryKind.Directory,
                ContentAvailable = true,
                SourceChange = change.Kind,
                RootId = change.RootId,
                Description = "重建目录",
            });
        }

        OrderSteps(plan);

        // 统计与警告
        if (plan.UnavailableCount > 0)
        {
            plan.Warnings.Add($"有 {plan.UnavailableCount} 个文件的历史内容已被清理，无法恢复；这些条目会明确标记为「不可恢复」。");
        }
        if (diff.Truncated)
        {
            plan.Warnings.Add("状态对比条目过多被截断，本计划只覆盖已展示的差异。");
        }
        if (plan.ConfirmationCount > 0)
        {
            plan.Warnings.Add($"有 {plan.ConfirmationCount} 个条目属于敏感删除（目标时刻之后新增的路径），需要逐项确认。");
        }
        if (!string.IsNullOrEmpty(options.RootPath) && RestoreGuard.IsSystemCriticalPath(options.RootPath, out var sysPrefix))
        {
            plan.Warnings.Add($"受保护范围位于系统关键目录（{sysPrefix}）内，恢复前请务必确认预览内容。");
        }

        plan.ComputeFingerprint();
        return plan;
    }

    private void AddRestore(RestorePlan plan, TreeChange change, FileTreeManifest current, RestoreOptions options)
    {
        // ── 目录必须单独处理（真实缺陷，由"误删整个文件夹"场景暴露）──
        // 目录**没有内容哈希**（HashBefore 恒为 null），所以如果直接往下走到
        // "targetHash is null → NoChange" 那条分支，被删掉的目录会被判成
        // "缺少内容哈希，无法恢复"并**永远不被重建** ——
        // 表现为"空目录删掉后怎么点恢复都不回来"。
        // 目录的正确动作是 CreateDirectory（引擎的 ExecuteStep 本来就支持，
        // 只是 Planner 从来没为目录生成过这一步）。
        // 判定目标种类时优先看"目标时刻"这一侧：KindBefore 属于目标状态。
        // ⚠ 只处理 Deleted 与 TypeChanged：
        //   Added 的含义是"当前有、目标没有"（要移除），目标侧不可能是目录，
        //   若把它也算进来会把"要删掉的目录"误判成"要重建的目录"。
        var targetKind = change.KindBefore ?? change.KindAfter;
        if (targetKind == EntryKind.Directory
            && change.Kind is ChangeKind.Deleted or ChangeKind.TypeChanged)
        {
            plan.Steps.Add(DirectoryStep(change, options));
            return;
        }

        // ── 字段方向说明（极易搞反，务必看清）──
        // TreeComparer.Compare(from = 目标时刻, to = 当前状态) 生成 TreeChange，因此：
        //    change.HashBefore / ObjectIdBefore  → 属于 **目标状态**（想恢复到的那个版本）
        //    change.HashAfter  / ObjectIdAfter   → 属于 **当前状态**（此刻磁盘上的内容）
        // 于是：
        //    · 要写回的内容哈希 = HashBefore（目标版本）
        //    · 冲突检测的期望值 = HashAfter（预览时刻磁盘上的内容）
        // 早期实现把两者弄反，导致每一次恢复都被判成"文件在预览之后又被改过"而全部跳过。
        string? targetHash = change.HashBefore ?? change.HashAfter;
        long? objectId = change.ObjectIdBefore ?? change.ObjectIdAfter;

        if (targetHash is null)
        {
            plan.Steps.Add(new RestoreStep
            {
                Action = RestoreAction.NoChange,
                RelativePath = change.RelativePath,
                Kind = change.KindAfter ?? change.KindBefore ?? EntryKind.File,
                ContentAvailable = false,
                SourceChange = change.Kind,
                RootId = change.RootId,
                Description = "缺少内容哈希，无法恢复",
                Warning = "该条目没有留存内容哈希（执行期间被跳过或读取失败），无法恢复。",
            });
            return;
        }

        objectId ??= _content.ResolveObjectId(targetHash);
        bool available = objectId is not null && _content.IsObjectAvailable(objectId.Value);
        bool exceededLimit = (change.SizeAfter ?? change.SizeBefore ?? 0) > options.MaxRestorableFileSizeBytes;

        var step = new RestoreStep
        {
            Action = RestoreAction.RestoreContent,
            RelativePath = change.RelativePath,
            Kind = change.KindAfter ?? change.KindBefore ?? EntryKind.File,
            TargetHash = targetHash,
            TargetObjectId = objectId,
            TargetSize = change.SizeBefore ?? change.SizeAfter,
            // 冲突检测的期望值 = 预览时刻磁盘上的内容（"当前状态"这一侧 = HashAfter）。
            // 绝不能取 HashBefore —— 那是目标版本，会让每一步恢复都被误判为
            // "预览后被改过"从而跳过（真实缺陷，由测试暴露）。
            ExpectedCurrentHash = change.HashAfter,
            ContentAvailable = available && !exceededLimit,
            SourceChange = change.Kind,
            RootId = change.RootId,
            Description = change.Kind == ChangeKind.Deleted ? "恢复被删除的内容" : "恢复为旧版本内容",
        };

        if (!available)
        {
            step.Warning = objectId is null
                ? "历史内容未留存（可能是添加保护之前就存在的文件，或已按策略清理）。"
                : "历史内容已被清理，无法恢复。";
        }
        else if (exceededLimit)
        {
            step.Warning = $"文件大小 {PathUtil.FormatBytes(step.TargetSize ?? 0)} 超过单文件恢复上限。";
        }

        plan.Steps.Add(step);
    }

    /// <summary>
    /// 生成"移除某个路径"的步骤。
    ///
    /// <paramref name="expectedCurrentHash"/> 必须是**预览时刻磁盘上该路径的内容哈希**
    /// （也就是"当前清单"里的值），用于执行时做冲突检测。
    /// ⚠ 踩坑记录（PIT）：早期实现用 <c>TreeChange.HashAfter</c> 当期望值，
    /// 而它在"修改"类变化里表示的是**目标**版本哈希，于是每一次恢复都会被判定为
    /// "文件在预览之后又被修改过"从而跳过 —— 恢复功能整体失效。
    /// 期望值只能来自"当前状态"这一侧，不能来自目标状态那一侧。
    /// </summary>
    private static RestoreStep RemoveStep(
        TreeChange change, string description, RestoreOptions options,
        EntryKind? kindOverride = null, string? expectedCurrentHash = null)
    {
        var kind = kindOverride ?? change.KindAfter ?? change.KindBefore ?? EntryKind.File;

        // 是否是"目标时刻之后才出现的新增内容"？
        // 依据：该路径首次出现的时刻（由基线时间或创建事件推导），不做任何猜测。
        bool isNewSinceTarget =
            change.FirstSeenUtc is { } firstSeen &&
            firstSeen > options.TargetTimeUtc &&
            change.Kind == ChangeKind.Added;

        return new RestoreStep
        {
            Action = kind == EntryKind.Directory ? RestoreAction.RemoveDirectory : RestoreAction.RemovePath,
            RelativePath = change.RelativePath,
            Kind = kind,
            TargetHash = null,
            TargetSize = change.SizeAfter,
            ExpectedCurrentHash = expectedCurrentHash,
            ContentAvailable = true,
            SourceChange = change.Kind,
            RootId = change.RootId,
            Description = description,
            // 删除用户新增内容 = 敏感操作，必须逐项确认
            RequiresConfirmation = isNewSinceTarget && !options.AllowRemoveNewPathsWithoutConfirmation,
        };
    }

    private static RestoreStep DirectoryStep(TreeChange change, RestoreOptions options) => new()
    {
        Action = RestoreAction.CreateDirectory,
        RelativePath = change.RelativePath,
        Kind = EntryKind.Directory,
        ContentAvailable = true,
        SourceChange = change.Kind,
        RootId = change.RootId,
        Description = "重建目录",
    };

    /// <summary>执行顺序：先建目录 → 再恢复文件 → 最后移除多余路径 → 最后清空目录。</summary>
    private static void OrderSteps(RestorePlan plan)
    {
        plan.Steps.Sort((a, b) =>
        {
            var r = Rank(a.Action).CompareTo(Rank(b.Action));
            if (r != 0) return r;
            // 目录按深度升序（先建浅的）；删除按深度降序（先删深的）
            var da = a.RelativePath.Count(c => c == '/');
            var db = b.RelativePath.Count(c => c == '/');
            if (a.Action is RestoreAction.RemovePath or RestoreAction.RemoveDirectory)
            {
                var d = db.CompareTo(da);
                if (d != 0) return d;
            }
            else
            {
                var d = da.CompareTo(db);
                if (d != 0) return d;
            }
            return string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int Rank(RestoreAction a) => a switch
    {
        RestoreAction.CreateDirectory => 0,
        RestoreAction.RestoreContent => 1,
        RestoreAction.NoChange => 2,
        RestoreAction.RemovePath => 3,
        RestoreAction.RemoveDirectory => 4,
        _ => 5,
    };
}

/// <summary>恢复策略选项。</summary>
public sealed class RestoreOptions
{
    public string? RootPath { get; set; }

    public DateTime TargetTimeUtc { get; set; }

    /// <summary>用户已在预览中明确接受"删除目标时刻之后的新增路径"。</summary>
    public bool AllowRemoveNewPathsWithoutConfirmation { get; set; }

    /// <summary>单文件恢复上限（超过则拒绝，防止误点导致巨量写入）。</summary>
    public long MaxRestorableFileSizeBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>是否把被移除的文件移入内部隔离区以便撤销（默认 true；false = 直接删除，更危险）。</summary>
    public bool QuarantineRemovedFiles { get; set; } = true;
}
