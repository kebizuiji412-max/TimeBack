using System.Diagnostics;
using LastRegret.Core;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Compare;
using LastRegret.Core.Config;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;
using LastRegret.Core.Util;
using LastRegret.Data;
using LastRegret.Windows.Io;

namespace LastRegret.Engine;

/// <summary>恢复执行结果。</summary>
public sealed class RestoreOutcome
{
    public bool Ok { get; init; }
    public long OperationId { get; init; }
    public RestoreStatus Status { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public long? PreSnapshotId { get; init; }
    public long? PostSnapshotId { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 给用户看的"恢复到了什么时候"，例如「今天 11:32」。
    /// 界面上要说人话，所以由调用方传入；为空时界面用"已恢复"兜底。
    /// </summary>
    public string? TargetLabel { get; init; }

    public List<string> Failures { get; } = new();

    /// <summary>
    /// 本次恢复**实际改动了几个文件**（写入内容或删除文件）。
    /// 只统计真正碰到文件内容的步骤 —— 建目录/清空目录不算"改动"，因为那些不丢内容。
    /// </summary>
    public int FilesChanged { get; init; }

    /// <summary>
    /// 是否可以撤销。
    ///
    /// 语义（RC 修复）：**只要本次恢复已经真的改动了文件、并且存在有效的恢复前安全点，
    /// 用户就应该能撤销** —— 不论整体是"全部成功"还是"部分成功"。
    ///
    /// 为什么不能用 <see cref="Ok"/> 判：部分成功时 <c>Ok == false</c>，但文件**已经被改了**，
    /// 而安全点也已经建好。旧实现用 <c>Ok &amp;&amp; PreSnapshotId</c> 会直接隐藏界面上的
    /// "撤销"按钮，于是"部分恢复了、想撤回"变成无处可撤 ——
    /// 而数据库层（<c>GetLastUndoable</c> 接受 completed/partial）本来认为它是可撤销的，
    /// 两边语义不一致。
    ///
    /// 反向保证：如果一个文件都没改（全失败、或只建了目录），不给 Undo ——
    /// 不凭空提供一个"撤销了也什么都没变"的入口。
    /// </summary>
    public bool CanUndo => PreSnapshotId is not null && FilesChanged > 0;
}

/// <summary>
/// 恢复预览与执行。
///
/// 不可动摇的六条安全规则（每一条都有对应代码，不是文档口号）：
///  1. **先预览后执行**：<see cref="BuildPreview"/> 与 <see cref="Execute"/> 分离，
///     执行时必须校验用户看到的预览指纹（<see cref="RestorePlan.Fingerprint"/>）。
///  2. **执行前必有安全点**：先对"当前状态"做一次完整快照，
///     任何一个文件读不到都会**中止恢复**（宁可不做，绝不制造不可回滚的破坏）。
///  3. **只做差异**：绝不整目录覆盖；每一步都能对应预览里的一条。
///  4. **冲突即跳过**：执行时磁盘内容与预览时不一致 → 跳过并如实报告，绝不静默覆盖。
///  5. **逐步记账**：每一步的"执行前内容"写入 CAS 并落库，
///     程序中途崩溃后可以据此判断实际做到了哪一步。
///  6. **恢复本身可撤销**：撤销 = 以"恢复前安全点"为目标再做一次恢复，
///     因此撤销也是可预览、可追踪、可再次撤销的。
/// </summary>
public sealed class RestoreEngine
{
    private readonly LastRegretDatabase _db;
    private readonly IWatchedRootRepository _roots;
    private readonly FileIndexRepository _index;
    private readonly ISnapshotRepository _snapshotRepo;
    private readonly IRestoreRepository _restoreRepo;
    private readonly IFileVersionRepository _versions;
    private readonly SnapshotService _snapshots;
    private readonly CompareService _compare;
    private readonly IContentStore _store;
    private readonly ContentWriter _writer;
    private readonly FileSystemReader _reader;
    private readonly IClock _clock;
    private readonly WatchService _watch;
    private readonly AppSettings _settings;

    public RestoreEngine(
        LastRegretDatabase db,
        IWatchedRootRepository roots,
        FileIndexRepository index,
        ISnapshotRepository snapshotRepo,
        IRestoreRepository restoreRepo,
        IFileVersionRepository versions,
        SnapshotService snapshots,
        CompareService compare,
        IContentStore store,
        ContentWriter writer,
        FileSystemReader reader,
        IClock clock,
        WatchService watch,
        AppSettings settings)
    {
        _db = db;
        _roots = roots;
        _index = index;
        _snapshotRepo = snapshotRepo;
        _restoreRepo = restoreRepo;
        _versions = versions;
        _snapshots = snapshots;
        _compare = compare;
        _store = store;
        _writer = writer;
        _reader = reader;
        _clock = clock;
        _watch = watch;
        _settings = settings;
    }

    // ═════════════════════════════════════════════════════════════════════
    // 预览
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 生成恢复预览。**不修改任何磁盘文件**。
    /// </summary>
    public (RestorePlan? Plan, string? Error) BuildPreview(long rootId, DateTime atLocal)
    {
        var atUtc = atLocal.Kind == DateTimeKind.Utc ? atLocal : atLocal.ToUniversalTime();
        return BuildPreviewAt(rootId, atUtc);
    }

    /// <summary>
    /// 生成恢复预览（按**UTC** 精确指定时间点）。
    ///
    /// 撤销与修复路径必须走这个重载：它们的目标是某个已存在的快照，
    /// 而"用本地时间反查快照"在进程时区与系统时区不一致时会错位，
    /// 导致"该时间点之前没有任何恢复点"这类极具迷惑性的失败。
    /// </summary>
    public (RestorePlan? Plan, string? Error) BuildPreviewAt(long rootId, DateTime atUtc)
        => BuildPreviewAt(rootId, atUtc, includePaths: null);

    /// <summary>
    /// 生成恢复预览（按 UTC 精确指定时间点，并限定只处理用户勾选的路径）。
    /// <paramref name="includePaths"/> 为 null 表示全部差异。
    /// </summary>
    public (RestorePlan? Plan, string? Error) BuildPreviewAt(
        long rootId, DateTime atUtc, IReadOnlyCollection<string>? includePaths)
    {
        var root = _roots.Get(rootId);
        if (root is null) return (null, "受保护范围不存在。");

        if (atUtc.Kind != DateTimeKind.Utc) atUtc = atUtc.ToUniversalTime();

        var snapshot = _snapshots.GetAtOrBefore(rootId, atUtc);
        if (snapshot is null)
        {
            return (null, "该时间点之前没有任何恢复点。本程序无法恢复到「开始保护之前」的状态。");
        }

        var comparison = _compare.CompareWithSnapshot(rootId, snapshot, TreeComparer.DefaultMaxChanges, includePaths);
        if (comparison is null) return (null, "无法读取目标时间点的状态。");

        var resolver = new ContentResolver(this);
        var options = new RestoreOptions
        {
            RootPath = root.Path,
            TargetTimeUtc = snapshot.TimestampUtc,
            MaxRestorableFileSizeBytes = Math.Max(_settings.MaxStoreFileSizeBytes, 1024L * 1024 * 1024),
            QuarantineRemovedFiles = true,
        };

        var planner = new RestorePlanner(resolver);
        var plan = planner.Plan(comparison.Diff, comparison.Target, comparison.Current, options);

        // 目标时间点与用户点选的时间不完全一致时，必须如实告知
        if (!SameMinute(snapshot.TimestampLocal, atUtc.ToLocalTime()))
        {
            plan.Warnings.Insert(0,
                $"该时间点没有记录恢复点；将使用最近的一个：{snapshot.TimestampLocal:yyyy-MM-dd HH:mm:ss}（{snapshot.Kind.ToChinese()}）。");
        }
        if (!plan.HasEffect)
        {
            plan.Warnings.Add("当前状态与目标状态一致，无需恢复。");
        }

        plan.ComputeFingerprint();
        return (plan, null);
    }

    private static bool SameMinute(DateTime a, DateTime b) =>
        Math.Abs((a - b).TotalSeconds) < 60;

    /// <summary>
    /// 为"用户明确要删除的若干路径"生成一份删除计划。
    ///
    /// 用途：**自定义删除** —— 用户想删掉某个文件，而这个文件在他选的时间点里是存在的。
    /// 这种需求用"恢复到某时间点"表达不出来（恢复到那时只会把它还原成那时的内容），
    /// 所以必须有一个显式的删除入口。
    ///
    /// 安全性完全复用现有机制，没有任何"绕过"：
    ///   · 每一步都会在预览时记下磁盘当前哈希，执行前重新核对，不一致就跳过（绝不错删）；
    ///   · 删除前会把文件内容留存一份（`StoreExistingFile`），作为额外审计；
    ///   · 执行前照旧创建恢复前安全点，所以这次删除**可以整体撤销**；
    ///   · 每一步都标记 `RequiresConfirmation`，没有用户确认就不会执行。
    /// </summary>
    public (RestorePlan? Plan, string? Error) BuildDeletionPreview(
        long rootId, IReadOnlyCollection<string> relativePaths)
    {
        var root = _roots.Get(rootId);
        if (root is null) return (null, "受保护范围不存在。");
        if (relativePaths.Count == 0) return (null, "没有选中要删除的文件。");

        var current = _snapshots.LoadCurrentManifest(rootId);
        var keep = new HashSet<string>(relativePaths, LastRegret.Core.Util.PathUtil.Comparer);

        var plan = new RestorePlan
        {
            RootId = rootId,
            TargetTimeUtc = _clock.UtcNow,
            TargetTimeLocal = _clock.UtcNow.ToLocalTime(),
            Current = current,
        };

        var missing = new List<string>();
        foreach (var entry in current.Entries.Values)
        {
            if (entry.Kind != EntryKind.File) continue;
            if (!keep.Contains(entry.RelativePath)) continue;

            plan.Steps.Add(new RestoreStep
            {
                Action = RestoreAction.RemovePath,
                RelativePath = entry.RelativePath,
                Kind = EntryKind.File,
                ExpectedCurrentHash = entry.Hash,
                TargetSize = entry.Size,
                ContentAvailable = true,
                RequiresConfirmation = true,
                SourceChange = ChangeKind.Added,
                RootId = rootId,
                Description = "删除这个文件（内容会在删除前留存一份，可撤销）",
            });
        }

        foreach (var path in keep)
        {
            if (!current.Entries.Values.Any(e => LastRegret.Core.Util.PathUtil.Comparer.Equals(e.RelativePath, path)))
                missing.Add(path);
        }

        if (missing.Count > 0)
        {
            plan.Warnings.Add($"有 {missing.Count} 个文件现在不存在，已跳过：{string.Join("、", missing.Take(3))}");
        }
        if (!plan.HasEffect)
        {
            plan.Warnings.Add("所选文件当前都不存在，没有可删除的内容。");
        }

        plan.ComputeFingerprint();
        return (plan, null);
    }

    /// <summary>内容解析实现：把哈希解析为本地可用的对象 Id。</summary>
    private sealed class ContentResolver : IContentResolver
    {
        private readonly RestoreEngine _engine;
        private readonly Dictionary<string, long?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public ContentResolver(RestoreEngine engine) => _engine = engine;

        public long? ResolveObjectId(string hash)
        {
            if (_cache.TryGetValue(hash, out var cached)) return cached;
            var obj = _engine._store.FindByHash(hash);
            _cache[hash] = obj?.Id;
            return obj?.Id;
        }

        public bool IsObjectAvailable(long objectId) => _engine._store.Exists(objectId);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 执行
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 执行恢复。
    /// </summary>
    /// <param name="plan">已经过用户确认的预览计划。</param>
    /// <param name="confirmFingerprint">用户在界面上看到并确认的指纹。</param>
    /// <param name="allowNewRemovals">用户是否明确同意删除"目标时刻之后新增的路径"。</param>
    /// <param name="log">进度日志回调。</param>
    public RestoreOutcome Execute(
        RestorePlan plan,
        string confirmFingerprint,
        bool allowNewRemovals,
        Action<string>? log = null)
    {
        var root = _roots.Get(plan.RootId);
        if (root is null) return Fail(0, "受保护范围不存在。");

        // ── 规则 1：确认的必须是同一份预览 ──
        if (!string.Equals(plan.Fingerprint, confirmFingerprint, StringComparison.Ordinal))
        {
            plan.ComputeFingerprint();
            if (!string.Equals(plan.Fingerprint, confirmFingerprint, StringComparison.Ordinal))
            {
                return Fail(0, "预览内容已发生变化（历史可能在这期间被清理），请重新预览后再确认。");
            }
        }

        if (!plan.HasEffect)
        {
            return Fail(0, "当前状态与目标状态一致，没有需要恢复的内容。");
        }

        if (!allowNewRemovals && plan.ConfirmationCount > 0)
        {
            return Fail(0, $"有 {plan.ConfirmationCount} 个条目需要您明确确认（删除目标时刻之后新增的文件），请先勾选确认。");
        }

        // ── 规则 2：执行前必须有一个完整的安全点 ──
        log?.Invoke("正在创建恢复前安全点（记录当前完整状态与内容）…");
        var safety = CreateSafetySnapshot(root, plan, log);
        if (safety.Snapshot is null)
        {
            return Fail(0, $"无法创建恢复前安全点，已中止恢复（原因：{safety.Error}）。这保证了任何恢复操作都是可撤销的。");
        }

        var operation = new RestoreOperation
        {
            RootId = root.Id,
            TargetTimeUtc = plan.TargetTimeUtc,
            TargetTimeLocal = plan.TargetTimeLocal,
            TargetSnapshotId = plan.TargetSnapshotId,
            PreRestoreSnapshotId = safety.Snapshot.Id,
            Status = RestoreStatus.Running,
            StartedUtc = _clock.UtcNow,
            PlannedCount = plan.Steps.Count,
            PlanFingerprint = plan.Fingerprint,
        };
        _restoreRepo.Insert(operation);
        _restoreRepo.InsertSteps(BuildStepRecords(operation.Id, plan));

        log?.Invoke($"安全点已创建（快照 #{safety.Snapshot.Id}，{safety.Snapshot.FileCount} 个文件）。");
        log?.Invoke($"开始恢复：共 {plan.Steps.Count} 项操作。");

        int succeeded = 0, failed = 0, skipped = 0;
        var failures = new List<string>();
        bool watcherWasRunning = _watch.GetState(root.Id).Watching;
        _executionBaseline = plan.Current;

        try
        {
            // ── 规则 4 前提：暂停监听，避免恢复动作本身产生成百上千条噪声事件 ──
            _watch.Pause(root.Id);

            var stepRecords = _restoreRepo.ListSteps(operation.Id);
            var bySeq = stepRecords.ToDictionary(s => s.Sequence);
            int sequence = 0;

            // 执行顺序：建目录 → 恢复文件内容 → 移除多余路径 → 清理空目录。
            // 顺序很重要：先保证目录存在再写文件；先写回再删除（避免中间态把用户数据置于风险中）。
            foreach (var step in plan.Steps.Where(s => s.Action == RestoreAction.CreateDirectory))
            {
                Tally(ExecuteStep(root, step, operation.Id, sequence++), bySeq, ref succeeded, ref failed, ref skipped, failures, log);
            }

            foreach (var step in plan.Steps.Where(s => s.Action == RestoreAction.RestoreContent))
            {
                Tally(ExecuteStep(root, step, operation.Id, sequence++), bySeq, ref succeeded, ref failed, ref skipped, failures, log);
            }

            foreach (var step in plan.Steps.Where(s => s.Action == RestoreAction.RemovePath))
            {
                Tally(ExecuteStep(root, step, operation.Id, sequence++), bySeq, ref succeeded, ref failed, ref skipped, failures, log);
            }

            // 最后尝试清理空目录（非空目录一律保留并如实报告）
            foreach (var step in plan.Steps.Where(s => s.Action == RestoreAction.RemoveDirectory))
            {
                Tally(ExecuteStep(root, step, operation.Id, sequence++), bySeq, ref succeeded, ref failed, ref skipped, failures, log);
            }

            // 把每一步的执行结果落库（崩溃后据此判断实际做到了哪一步）
            foreach (var pair in bySeq)
            {
                try { _restoreRepo.UpdateStep(pair.Value); }
                catch (Exception) { /* 单步记账失败不应中断整个恢复 */ }
            }

            // ── 让索引与磁盘重新对齐（恢复期间监听是暂停的） ──
            log?.Invoke("正在重新对齐索引与磁盘状态…");
            ResyncAfterRestore(root, log);

            log?.Invoke("正在创建恢复后状态点…");
            var post = _snapshots.Create(root.Id, SnapshotKind.PostRestore,
                $"恢复到 {plan.TargetTimeLocal:yyyy-MM-dd HH:mm:ss} 之后的状态");

            operation.Status = failed == 0
                ? RestoreStatus.Completed
                : (succeeded > 0 ? RestoreStatus.PartiallyCompleted : RestoreStatus.Failed);
            operation.SucceededCount = succeeded;
            operation.FailedCount = failed;
            operation.PostRestoreSnapshotId = post.Id;
            operation.FinishedUtc = _clock.UtcNow;
            operation.Message = BuildMessage(succeeded, failed, skipped, failures);
            _restoreRepo.Update(operation);

            log?.Invoke(operation.Message);

            // ── "到底动了几个文件"：撤销可用性的依据（只看真正碰内容的动作） ──
            // 只建目录 / 清空目录不算改动 —— 那些不丢内容，不该因此给用户一个撤销入口。
            var filesChanged = bySeq.Values.Count(r =>
                r.Succeeded && r.Action is RestoreAction.RestoreContent or RestoreAction.RemovePath);

            return new RestoreOutcome
            {
                Ok = failed == 0,
                OperationId = operation.Id,
                Status = operation.Status,
                Succeeded = succeeded,
                Failed = failed,
                Skipped = skipped,
                PreSnapshotId = safety.Snapshot.Id,
                PostSnapshotId = post.Id,
                Message = operation.Message,
                FilesChanged = filesChanged,
                Failures = { },
            };        }
        catch (Exception ex)
        {
            operation.Status = succeeded > 0 ? RestoreStatus.PartiallyCompleted : RestoreStatus.Failed;
            operation.SucceededCount = succeeded;
            operation.FailedCount = failed;
            operation.FinishedUtc = _clock.UtcNow;
            operation.Message = $"恢复过程中出现异常：{ex.Message}";
            TryUpdate(operation);
            failures.Add(ex.Message);
            return new RestoreOutcome
            {
                Ok = false,
                OperationId = operation.Id,
                Status = operation.Status,
                Succeeded = succeeded,
                Failed = failed,
                Skipped = skipped,
                PreSnapshotId = safety.Snapshot.Id,
                Message = operation.Message,
                // 异常路径下 bySeq 不在作用域内，无法精确统计；这里用"是否有步骤成功"做保守近似：
                // 只要有步骤成功就允许撤销（安全点已建好），否则不给撤销入口。
                FilesChanged = succeeded > 0 ? 1 : 0,
            };
        }
        finally
        {
            if (watcherWasRunning)
            {
                _watch.Resume(root.Id, rescan: false); // 上面已显式对齐
            }
        }
    }

    private static void Tally(
        StepResult result, Dictionary<int, RestoreStepRecord> bySeq,
        ref int succeeded, ref int failed, ref int skipped, List<string> failures, Action<string>? log)
    {
        if (result.Record is not null && bySeq.TryGetValue(result.Record.Sequence, out var record))
        {
            record.Succeeded = result.Success;
            record.SkippedDueToConflict = result.Skipped;
            record.Error = result.Error;
            record.BeforeHash = result.BeforeHash;
            record.BeforeObjectId = result.BeforeObjectId;
            record.BeforeSize = result.BeforeSize;
            record.ExecutedUtc = DateTime.UtcNow;
        }

        if (result.Success && result.Skipped)
        {
            skipped++;
        }
        else if (result.Success)
        {
            succeeded++;
        }
        else
        {
            failed++;
            if (result.Error is not null && failures.Count < 20) failures.Add(result.Error);
        }

        if (result.Error is not null && log is not null)
        {
            var path = result.Record?.RelativePath ?? "?";
            log(result.Success && result.Skipped
                ? $"跳过：{path} —— {result.Error}"
                : !result.Success
                    ? $"失败：{path} —— {result.Error}"
                    : $"完成：{path}");
        }
    }

    /// <summary>单步执行结果。</summary>
    private sealed class StepResult
    {
        public bool Success { get; init; }
        public bool Skipped { get; init; }
        public string? Error { get; init; }
        public string? BeforeHash { get; init; }
        public long? BeforeObjectId { get; init; }
        public long? BeforeSize { get; init; }
        public RestoreStepRecord? Record { get; init; }
        public string? Message { get; init; }
    }

    private StepResult ExecuteStep(WatchedRoot root, RestoreStep step, long operationId, int sequence)
    {
        var record = new RestoreStepRecord
        {
            OperationId = operationId,
            Sequence = sequence,
            Action = step.Action,
            RelativePath = step.RelativePath,
            TargetHash = step.TargetHash,
        };

        // 冲突检测基线 = **预览时刻用户看到的内容**（plan.Current 就是那时生成的"当前状态清单"）。
        //
        // 为什么用它与磁盘现状比较（而不是用索引）：
        //  · 索引可能落后于磁盘（事件还在合并窗口里等待确认），用它当基线会导致
        //    "刚改过的文件被认为没改过"，从而**静默覆盖用户的新内容**——这是最危险的失败方式；
        //  · plan.Current 是用户在预览里逐条看过的状态，语义上正是"我看到的那一版"。
        // 只有"预览之后又被改动"才会不一致，从而触发跳过。文件已不存在时回退到索引值。
        var baseline = _executionBaseline?.Find(step.RelativePath)?.Hash
                       ?? _index.Get(root.Id, step.RelativePath)?.Hash;
        step.ExpectedCurrentHash = baseline;

        try
        {
            var absolute = PathUtil.ToAbsolute(root.Path, step.RelativePath);

            switch (step.Action)
            {
                case RestoreAction.CreateDirectory:
                    Directory.CreateDirectory(FileSystemReader.Extend(absolute));
                    record.Succeeded = true;
                    return new StepResult { Success = true, Record = record };

                case RestoreAction.RemoveDirectory:
                {
                    // 只在空目录时移除；非空一律保留（绝不递归删除用户的文件）
                    if (!Directory.Exists(FileSystemReader.Extend(absolute)))
                    {
                        return new StepResult { Success = true, Skipped = true, Record = record, Error = "目录已不存在" };
                    }
                    if (Directory.EnumerateFileSystemEntries(FileSystemReader.Extend(absolute)).Any())
                    {
                        record.Succeeded = true;
                        record.Error = "目录非空，已保留（不会递归删除用户文件）";
                        return new StepResult { Success = true, Skipped = true, Record = record, Error = record.Error };
                    }
                    Directory.Delete(FileSystemReader.Extend(absolute), recursive: false);
                    record.Succeeded = true;
                    return new StepResult { Success = true, Record = record };
                }

                case RestoreAction.RemovePath:
                {
                    if (!File.Exists(FileSystemReader.Extend(absolute)))
                    {
                        return new StepResult { Success = true, Skipped = true, Record = record, Error = "文件已不存在" };
                    }

                    // 冲突检测：确认磁盘内容仍是我们预览时看到的内容
                    var actualHash = _reader.TryComputeHash(absolute, out var hashError);
                    record.BeforeHash = actualHash;
                    record.BeforeSize = new FileInfo(FileSystemReader.Extend(absolute)).Length;

                    if (actualHash is null)
                    {
                        record.Error = $"无法读取文件内容（{hashError}），出于安全考虑已跳过删除";
                        return new StepResult { Success = false, Error = record.Error, Record = record };
                    }

                    if (step.ExpectedCurrentHash is not null &&
                        !string.Equals(actualHash, step.ExpectedCurrentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        record.SkippedDueToConflict = true;
                        record.Error = "文件在预览之后又被修改过，为避免误删已跳过（可重新预览）";
                        return new StepResult { Success = true, Skipped = true, Error = record.Error, Record = record,
                            BeforeHash = actualHash };
                    }

                    // 记录"执行前内容"，作为额外审计（主要回滚手段仍是恢复前安全点）
                    var stored = _writer.StoreExistingFile(absolute, step.RelativePath, _settings.MaxStoreFileSizeBytes, out _, out _);
                    record.BeforeObjectId = stored;

                    File.Delete(FileSystemReader.Extend(absolute));
                    record.Succeeded = true;
                    return new StepResult { Success = true, Record = record, BeforeHash = actualHash };
                }

                case RestoreAction.RestoreContent:
                {
                    if (!step.ContentAvailable || step.TargetObjectId is null)
                    {
                        record.Error = step.Warning ?? "该条目的历史内容不可用，无法恢复";
                        return new StepResult { Success = false, Error = record.Error, Record = record };
                    }

                    // 冲突检测：磁盘上的内容是否还是预览时看到的样子？
                    if (File.Exists(FileSystemReader.Extend(absolute)))
                    {
                        var actualHash = _reader.TryComputeHash(absolute, out _);
                        record.BeforeHash = actualHash;
                        record.BeforeSize = new FileInfo(FileSystemReader.Extend(absolute)).Length;

                        if (actualHash is not null &&
                            step.ExpectedCurrentHash is not null &&
                            !string.Equals(actualHash, step.ExpectedCurrentHash, StringComparison.OrdinalIgnoreCase))
                        {
                            record.SkippedDueToConflict = true;
                            record.Error = "文件在预览之后又被修改过，为避免覆盖新内容已跳过（可重新预览）";
                            return new StepResult { Success = true, Skipped = true, Error = record.Error, Record = record };
                        }
                    }

                    if (!_writer.TryMaterialize(step.TargetObjectId.Value, absolute, out var error))
                    {
                        record.Error = error ?? "写入失败";
                        return new StepResult { Success = false, Error = record.Error, Record = record };
                    }

                    record.Succeeded = true;
                    return new StepResult { Success = true, Record = record };
                }

                default:
                    record.Succeeded = true;
                    return new StepResult { Success = true, Skipped = true, Record = record };
            }
        }
        catch (Exception ex)
        {
            record.Error = FileSystemReader.Describe(ex);
            return new StepResult { Success = false, Error = record.Error, Record = record };
        }
    }

    private RestoreOutcome Fail(long operationId, string message) => new()
    {
        Ok = false,
        OperationId = operationId,
        Status = RestoreStatus.Failed,
        Message = message,
    };

    /// <summary>
    /// 本次执行使用的"当前状态清单"（即用户在预览里看到的那一版）。
    /// 冲突检测以它为基线：只有"预览之后确实又被改过"的文件才会不一致。
    /// </summary>
    private FileTreeManifest? _executionBaseline;

    private static List<RestoreStepRecord> BuildStepRecords(long operationId, RestorePlan plan) =>
        plan.Steps.Select((s, i) => new RestoreStepRecord
        {
            OperationId = operationId,
            Sequence = i,
            Action = s.Action,
            RelativePath = s.RelativePath,
            TargetHash = s.TargetHash,
        }).ToList();

    private (Snapshot? Snapshot, string? Error) CreateSafetySnapshot(WatchedRoot root, RestorePlan plan, Action<string>? log)
    {
        try
        {
            var snapshot = _snapshots.Create(root.Id, SnapshotKind.PreRestore,
                $"恢复前自动创建的安全点（{_clock.Now:HH:mm:ss}）", forceFull: false);

            // ── 关键校验：本次恢复**实际会动到、且当前存在**的文件，其当前内容必须能在 CAS 中取到 ──
            //
            // 为什么必须按 plan 收窄范围（真实缺陷，由 RC 审计的实验暴露）：
            //   早期实现遍历的是"安全点里的全部文件"。在默认的「智能留存」模式下，
            //   大于阈值（默认 4MB）的文件只记 Hash、没有 ObjectId；
            //   于是只要保护目录里**存在任意一个大文件**，它的缺失就会把
            //   **整个恢复**判为"不可撤销"并中止 —— 哪怕用户只想恢复一个 3KB 的配置。
            //   这与「智能留存」的设计目的（小文件可恢复、大文件只记事实）直接矛盾。
            //
            // 收窄后的语义（安全检查**没有放松**）：
            //   · 只有"会被覆盖（RestoreContent）或会被删除（RemovePath）"的文件才需要
            //     先留存当前内容 —— 因为只有它们的当前内容会在本次恢复中丢失；
            //   · 目标时刻之后新增、将被删除的文件：如果它没有留存内容，
            //     恢复后它就回不来了，所以同样必须拦截；
            //   · 与本次恢复无关的文件（包括没被勾选的大文件）不再阻断本次恢复。
            var affected = AffectedExistingFiles(plan);
            var safetyFiles = _snapshotRepo.LoadFiles(snapshot.Id)
                .Where(x => x.Kind == EntryKind.File && x.Hash is not null)
                .ToDictionary(x => x.RelativePath, x => x, LastRegret.Core.Util.PathUtil.Comparer);

            int missing = 0;
            var missingList = new List<string>();
            foreach (var path in affected)
            {
                if (!safetyFiles.TryGetValue(path, out var f)) continue;   // 安全点里没有它 → 无处可丢
                if (f.ObjectId is null || !_store.Exists(f.ObjectId.Value))
                {
                    missing++;
                    if (missingList.Count < 5) missingList.Add(f.RelativePath);
                }
            }

            if (missing > 0)
            {
                return (null,
                    $"本次恢复会影响 {missing} 个文件，但它们当前的内容没有留存下来（例如：{string.Join("、", missingList)}），" +
                    "恢复后无法把它们还原，因此已中止。请先等待这些文件产生一次新变化，或调整留存大小上限。");
            }

            return (snapshot, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// 本次恢复**实际会改动、且当前在快照里存在**的文件相对路径。
    ///
    /// 判定依据是动作语义，不是"清单里有什么"：
    ///   · <see cref="RestoreAction.RestoreContent"/> —— 会覆盖当前内容 → 需要留存当前内容
    ///   · <see cref="RestoreAction.RemovePath"/>    —— 会删除当前文件 → 需要留存当前内容
    ///   · CreateDirectory / RemoveDirectory / NoChange —— 不丢内容 → 不需要
    /// </summary>
    private static HashSet<string> AffectedExistingFiles(RestorePlan plan)
    {
        var result = new HashSet<string>(LastRegret.Core.Util.PathUtil.Comparer);

        foreach (var step in plan.Steps)
        {
            if (step.Action is not (RestoreAction.RestoreContent or RestoreAction.RemovePath)) continue;
            if (string.IsNullOrEmpty(step.RelativePath)) continue;
            result.Add(step.RelativePath);
        }

        return result;
    }

    /// <summary>恢复完成后让索引与磁盘重新对齐（复用停机对齐的同一套逻辑）。</summary>
    private void ResyncAfterRestore(WatchedRoot root, Action<string>? log)
    {
        if (_watch.Rescanner is null)
        {
            log?.Invoke("（未配置重扫器，索引将在下一次事件时自动修正）");
            return;
        }

        var report = _watch.Rescanner.Align(root, ResyncMode.Resync);
        log?.Invoke($"重新对齐完成：新增 {report.DetectedCreated}、修改 {report.DetectedModified}、消失 {report.DetectedDeleted}。");
    }

    private static string BuildMessage(int succeeded, int failed, int skipped, List<string> failures)
    {
        var parts = new List<string> { $"成功 {succeeded} 项" };
        if (skipped > 0) parts.Add($"跳过 {skipped} 项");
        if (failed > 0) parts.Add($"失败 {failed} 项");
        var msg = string.Join("，", parts);
        if (failures.Count > 0) msg += $"；失败示例：{failures[0]}";
        return msg;
    }

    private void TryUpdate(RestoreOperation operation)
    {
        try
        {
            _restoreRepo.Update(operation);
        }
        catch (Exception)
        {
            // 状态更新失败不应掩盖真正的错误
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 撤销恢复
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>最近一次"可以撤销"的恢复操作。</summary>
    public RestoreOperation? GetLastUndoable(long? rootId = null) => _restoreRepo.GetLastUndoable(rootId);

    /// <summary>
    /// 撤销一次恢复：以该操作的"恢复前安全点"为目标，再执行一次恢复。
    /// 撤销本身同样会创建安全点、同样可预览、同样可再次撤销。
    /// </summary>
    public (RestorePlan? Plan, string? Error) BuildUndoPreview(long operationId)
    {
        var op = _restoreRepo.Get(operationId);
        if (op is null) return (null, "找不到该恢复操作。");
        if (op.PreRestoreSnapshotId is null) return (null, "该操作没有安全点，无法撤销。");
        if (op.UndoneByOperationId is not null) return (null, "该操作已经被撤销过。");

        var safety = _snapshotRepo.Get(op.PreRestoreSnapshotId.Value);
        if (safety is null) return (null, "恢复前安全点已被清理，无法撤销（历史清理策略不删除恢复点，请检查是否手动清理过）。");

        var (plan, error) = BuildPreviewAt(op.RootId, safety.TimestampUtc);
        if (plan is null) return (null, error);

        plan.Warnings.Insert(0,
            $"这是「撤销恢复」：将把范围恢复到操作 #{operationId} 执行**之前**的状态（{safety.TimestampLocal:yyyy-MM-dd HH:mm:ss}）。");
        plan.ComputeFingerprint();
        return (plan, null);
    }

    /// <summary>执行撤销。</summary>
    public RestoreOutcome ExecuteUndo(long operationId, string confirmFingerprint, bool allowNewRemovals, Action<string>? log = null)
    {
        var op = _restoreRepo.Get(operationId);
        if (op is null) return Fail(0, "找不到该恢复操作。");

        var (plan, error) = BuildUndoPreview(operationId);
        if (plan is null) return Fail(operationId, error ?? "无法生成撤销预览。");

        // 撤销同样走完整的安全流程
        var outcome = ExecuteInternal(plan, confirmFingerprint, allowNewRemovals, log, undoesOperationId: operationId);

        if (outcome.Ok)
        {
            op.UndoneByOperationId = outcome.OperationId;
            op.Status = RestoreStatus.Undone;
            TryUpdate(op);
        }
        return outcome;
    }

    private RestoreOutcome ExecuteInternal(
        RestorePlan plan, string confirmFingerprint, bool allowNewRemovals, Action<string>? log, long? undoesOperationId)
    {
        // 复用 Execute 的完整流程，只是额外记录"本操作是对谁的撤销"
        var outcome = Execute(plan, confirmFingerprint, allowNewRemovals, log);
        if (undoesOperationId is not null && outcome.OperationId != 0)
        {
            var created = _restoreRepo.Get(outcome.OperationId);
            if (created is not null)
            {
                created.UndoesOperationId = undoesOperationId;
                TryUpdate(created);
            }
        }
        return outcome;
    }

    // ═════════════════════════════════════════════════════════════════════
    // 崩溃恢复
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 启动时检查是否有"执行到一半"的恢复操作（程序崩溃/断电）。
    /// 返回需要用户知道的事实描述；**不自动重放**，避免在不确定的状态上再叠加操作。
    /// </summary>
    public IReadOnlyList<string> DetectInterruptedOperations()
    {
        var messages = new List<string>();
        foreach (var op in _restoreRepo.FindInterrupted())
        {
            var steps = _restoreRepo.ListSteps(op.Id);
            int done = steps.Count(s => s.Succeeded);
            int skipped = steps.Count(s => s.SkippedDueToConflict);

            var msg =
                $"检测到一次未完成的恢复操作 #{op.Id}（目标时间 {op.TargetTimeLocal:yyyy-MM-dd HH:mm:ss}）：" +
                $"已执行 {done}/{steps.Count} 步" + (skipped > 0 ? $"、跳过 {skipped} 步" : string.Empty) + "。" +
                (op.PreRestoreSnapshotId is not null
                    ? $"它有一个恢复前安全点（快照 #{op.PreRestoreSnapshotId}），可以把它撤销回去。"
                    : "但该操作没有安全点，无法自动回滚。");

            // 把状态修正为"部分完成"，避免它一直停留在 running
            op.Status = done > 0 ? RestoreStatus.PartiallyCompleted : RestoreStatus.Failed;
            op.FinishedUtc ??= _clock.UtcNow;
            op.Message = (op.Message is null ? string.Empty : op.Message + "；") + "程序在该操作执行期间退出";
            TryUpdate(op);

            messages.Add(msg);
        }
        return messages;
    }

    /// <summary>最近的恢复操作记录（UI"恢复"页展示）。</summary>
    public IReadOnlyList<RestoreOperation> ListOperations(long? rootId = null, int limit = 30) =>
        _restoreRepo.ListRecent(rootId, limit);

    public IReadOnlyList<RestoreStepRecord> ListSteps(long operationId) => _restoreRepo.ListSteps(operationId);

    /// <summary>
    /// 尝试修复"执行到一半"的恢复：以安全点为目标再做一次恢复。
    /// 仅在用户明确要求时调用。
    /// </summary>
    public RestoreOutcome RepairInterrupted(long operationId, Action<string>? log = null)
    {
        var op = _restoreRepo.Get(operationId);
        if (op is null) return Fail(operationId, "找不到该恢复操作。");
        if (op.PreRestoreSnapshotId is null) return Fail(operationId, "该操作没有安全点，无法自动修复。");

        var (plan, error) = BuildUndoPreview(operationId);
        if (plan is null) return Fail(operationId, error ?? "无法生成修复预览。");
        plan.Warnings.Insert(0, "这是对「中断的恢复操作」的修复：将把范围还原到该操作开始之前的安全点。");
        plan.ComputeFingerprint();

        return ExecuteInternal(plan, plan.Fingerprint, allowNewRemovals: true, log, undoesOperationId: null);
    }
}
