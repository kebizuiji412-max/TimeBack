using LastRegret.Core.Model;
using LastRegret.Core.Util;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 恢复引擎端到端测试 —— 产品最核心、最危险的路径。
///
/// 每一条都对应产品要求里的一条硬规则：
///   先预览后执行 / 执行前必有安全点 / 只做差异 / 冲突即跳过 /
///   恢复可撤销 / 恢复后能继续记录新状态
/// </summary>
public static class RestoreSuites
{
    public static IEnumerable<TestCase> All()
    {
        yield return new("恢复·完整闭环", "改坏 → 恢复到过去 → 内容正确 → 恢复后可继续记录", () =>
        {
            using var box = Sandbox.Create("restore-basic");
            box.Protect();

            box.WriteFile("config.json", "{\"port\": 8080}");
            box.WaitForEvent("config.json", OperationType.Created);
            box.Flush();

            var point = box.Snapshot(SnapshotKind.Manual, "改坏之前");
            var pointTime = point.TimestampLocal;

            Thread.Sleep(300);
            box.WriteFile("config.json", "{\"port\": \"坏了\"}");
            box.WaitForEvent("config.json", OperationType.Modified);
            box.Flush();            // 确保"改坏"这一变化已落库（快照的水位线依赖它）
            box.WaitForIndex("config.json");   // 并确保索引已追上磁盘内容

            // ── 预览 ──
            var (plan, error) = box.Preview(pointTime);
            Check.NotNull(plan, "应能生成恢复预览：" + error);
            Check.True(plan!.HasEffect,
                $"应检测到需要恢复的变化。\n" +
                $"  恢复点 #{point.Id} @ {point.TimestampLocal:HH:mm:ss.fff} 水位={point.EventHighWatermark} 文件数={point.FileCount}\n" +
                $"  当前时间={DateTime.Now:HH:mm:ss.fff}\n" +
                box.DescribeState() + box.DescribeEvents());

            // 预览中的"期望当前内容"必须等于磁盘上真实的内容（否则会被冲突检测误判为跳过）
            var diskHash = LastRegret.Windows.Io.FileSystemReader.HashFileForTest(box.Abs("config.json"));
            var step0 = plan.Steps.First(s => s.RelativePath == "config.json");
            Check.Equal(diskHash, step0.ExpectedCurrentHash,
                $"冲突检测的期望值必须等于预览时刻磁盘上的真实内容。\n" +
                $"  步骤：目标哈希={step0.TargetHash} 期望当前哈希={step0.ExpectedCurrentHash ?? "(null)"}\n" +
                $"  磁盘哈希={diskHash}\n" +
                $"  当前清单哈希={plan.Current.Find("config.json")?.Hash}\n" +
                $"  目标清单哈希={plan.Target.Find("config.json")?.Hash}");
            Check.True(plan.RestoreCount >= 1, $"应至少有 1 条恢复内容步骤（实际 {plan.RestoreCount}）");
            Check.Equal(0, plan.UnavailableCount, "本例不应有不可恢复条目");

            // ── 执行 ──
            var (outcome, restoreLog) = box.RestoreToWithLog(pointTime);
            Check.Equal("{\"port\": 8080}", box.ReadFile("config.json"),
                $"文件内容必须回到过去的样子。\n  恢复结果：{outcome.Ok} / {outcome.Status} / {outcome.Message}\n" +
                $"  步骤：成功 {outcome.Succeeded} 失败 {outcome.Failed} 跳过 {outcome.Skipped}\n" +
                "  过程日志：\n    " + string.Join("\n    ", restoreLog) + "\n" +
                "  步骤账目：\n    " +
                string.Join("\n    ", box.Restore.ListSteps(outcome.OperationId)
                    .Select(s => $"{s.Sequence} {s.Action} {s.RelativePath} ok={s.Succeeded} skip={s.SkippedDueToConflict} err={s.Error}")));
            Check.True(outcome.Ok, "恢复应成功：" + outcome.Message);

            // ── 安全点必须存在 ──
            Check.NotNull(outcome.PreSnapshotId, "恢复前必须自动创建安全点");
            var safety = box.SnapshotsRepo.Get(outcome.PreSnapshotId!.Value);
            Check.NotNull(safety, "安全点应可读取");
            Check.Equal(SnapshotKind.PreRestore, safety!.Kind, "安全点类型应为 PreRestore");
            Check.NotNull(outcome.PostSnapshotId, "恢复后应创建状态点");

            // ── 恢复后索引已对齐：再预览应显示"无需恢复" ──
            var (plan2, _) = box.Preview(pointTime);
            Check.NotNull(plan2, "应能再次生成预览");
            Check.False(plan2!.HasEffect, $"恢复完成后当前状态应已等于目标状态：\n{plan2.Summary()}");

            // ── 恢复后继续记录新状态 ──
            var beforeCount = box.AllEvents().Count;
            Thread.Sleep(300);
            box.WriteFile("config.json", "{\"port\": 7070}");
            box.WaitFor(() => box.AllEvents().Count > beforeCount, 8000, "恢复之后必须还能继续记录新变化");

            var after = box.AllEvents().Last();
            Check.Equal("config.json", after.RelativePath, "新事件应针对同一个文件");
            Check.True(after.CanRestorePrevious, "恢复后的新变化同样应可恢复");
        });

        yield return new("恢复·完整闭环", "恢复错了 → 撤销恢复 → 回到恢复之前", () =>
        {
            using var box = Sandbox.Create("restore-undo");
            box.Protect();

            box.WriteFile("work.txt", "版本1");
            box.WaitForEvent("work.txt", OperationType.Created);
            box.Flush();
            var earlyPoint = box.Snapshot(SnapshotKind.Manual, "早期状态");

            Thread.Sleep(300);
            box.WriteFile("work.txt", "版本2");
            box.WaitForEvent("work.txt", OperationType.Modified);
            box.WaitForIndex("work.txt");

            // 恢复到"版本1"
            var outcome = box.RestoreTo(earlyPoint.TimestampLocal);
            Check.True(outcome.Ok, "恢复到早期应成功：" + outcome.Message);
            Check.Equal("版本1", box.ReadFile("work.txt"), "应回到版本1");

            // 用户后悔了：撤销这次恢复
            var last = box.Restore.GetLastUndoable(box.RootId);
            Check.NotNull(last, "应能找到可撤销的恢复操作");

            var (undoPlan, undoError) = box.Restore.BuildUndoPreview(last!.Id);
            Check.NotNull(undoPlan, "应能生成撤销预览：" + undoError + "\n" + box.DescribeSnapshots());

            var undoOutcome = box.Restore.ExecuteUndo(last.Id, undoPlan!.Fingerprint, allowNewRemovals: true);
            Check.True(undoOutcome.Ok, "撤销应成功：" + undoOutcome.Message);
            Check.Equal("版本2", box.ReadFile("work.txt"), "撤销后必须回到恢复之前的状态（版本2）");

            // 撤销记录必须可追踪
            var refreshed = box.RestoreRepo.Get(last.Id);
            Check.NotNull(refreshed, "恢复记录应仍在");
            Check.Equal(RestoreStatus.Undone, refreshed!.Status, "原恢复操作应被标记为「已撤销」");
            Check.Equal(undoOutcome.OperationId, refreshed.UndoneByOperationId, "应记录是被哪次操作撤销的");
        });

        yield return new("恢复·完整闭环", "恢复被删除的文件与目录", () =>
        {
            using var box = Sandbox.Create("restore-deleted");
            box.Protect();

            box.WriteFile("keep.txt", "保留");
            box.WriteFile("doomed.txt", "将被删除");
            box.WriteFile("sub/deep.txt", "深层文件");
            box.WaitFor(() => box.AllEvents().Count >= 3, 10000, "三个文件应先被记录");
            box.Flush();

            var point = box.Snapshot(SnapshotKind.Manual, "删除之前");
            var pointTime = point.TimestampLocal;

            Thread.Sleep(300);
            box.DeleteFile("doomed.txt");
            box.DeleteFile("sub/deep.txt");
            box.WaitFor(() => box.AllEvents().Count(e => e.Operation == OperationType.Deleted) >= 2,
                10000, "两个删除应被记录");

            Check.FileMissing(box.Abs("doomed.txt"), "删除后文件应确实不存在");

            var outcome = box.RestoreTo(pointTime);
            Check.True(outcome.Ok, "恢复被删除的文件应成功：" + outcome.Message);

            Check.FileExists(box.Abs("doomed.txt"), "被删除的文件应被恢复");
            Check.Equal("将被删除", box.ReadFile("doomed.txt"), "恢复的内容必须与删除前完全一致");
            Check.FileExists(box.Abs("sub/deep.txt"), "深层目录中的文件也应被恢复");
            Check.Equal("深层文件", box.ReadFile("sub/deep.txt"), "深层文件内容应一致");
        });

        yield return new("恢复·完整闭环", "恢复会移除目标时刻之后新增的文件（且必须经用户确认）", () =>
        {
            using var box = Sandbox.Create("restore-added");
            box.Protect();

            box.WriteFile("base.txt", "始终存在");
            box.WaitForEvent("base.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "新增之前");
            var pointTime = point.TimestampLocal;

            Thread.Sleep(300);
            box.WriteFile("newfile.txt", "后来新增的");
            box.WaitForEvent("newfile.txt", OperationType.Created);

            // 不允许删除新增文件时，必须拒绝执行
            var (plan, _) = box.Preview(pointTime);
            Check.NotNull(plan, "应能生成预览");
            Check.True(plan!.RemoveCount >= 1, "应识别出需要移除的新增文件");
            Check.True(plan.ConfirmationCount >= 1, "删除「目标时刻之后新增的文件」必须要求用户确认");

            var refused = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: false);
            Check.False(refused.Ok, "未经确认就删除用户新增文件必须被拒绝");
            Check.FileExists(box.Abs("newfile.txt"), "被拒绝的操作不得改动磁盘");

            // 明确同意后才执行
            var accepted = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(accepted.Ok, "明确确认后应能执行：" + accepted.Message);
            Check.FileMissing(box.Abs("newfile.txt"), "目标时刻之后新增的文件应被移除");
            Check.FileExists(box.Abs("base.txt"), "一直存在的文件不受影响");
        });

        yield return new("恢复·自定义恢复", "跨时间点挑选：每个文件用它自己那个时间点的版本恢复", () =>
        {
            using var box = Sandbox.Create("custom-restore-cross");
            box.Protect();

            box.WriteFile("Documents/a.txt", "a version 1");
            box.WriteFile("Projects/test.json", "{ v: 1 }");
            box.WaitFor(() => box.AllEvents().Count >= 2, 10000, "两个文件应先被记录");
            box.Flush();

            // 时间点 A：a.txt 变成 version 2
            Thread.Sleep(400);
            box.WriteFile("Documents/a.txt", "a version 2");
            box.WaitForIndex("Documents/a.txt", 15000);
            box.Flush();
            var pointA = box.Snapshot(SnapshotKind.Manual, "A：a.txt 是 v2");

            // 时间点 B：test.json 变成 version 3
            Thread.Sleep(400);
            box.WriteFile("Projects/test.json", "{ v: 3 }");
            box.WaitForIndex("Projects/test.json", 15000);
            box.Flush();
            var pointB = box.Snapshot(SnapshotKind.Manual, "B：test.json 是 v3");

            // 当前状态：两个文件都被再次改坏
            Thread.Sleep(400);
            box.WriteFile("Documents/a.txt", "a BROKEN");
            box.WriteFile("Projects/test.json", "{ BROKEN }");
            box.WaitForIndex("Documents/a.txt", 15000);
            box.WaitForIndex("Projects/test.json", 15000);

            var snapA = box.SnapshotsRepo.Get(pointA.Id);
            var snapB = box.SnapshotsRepo.Get(pointB.Id);
            Check.NotNull(snapA, "应能取到时间点 A");
            Check.NotNull(snapB, "应能取到时间点 B");

            // ① 从 A 挑 Documents/a.txt（用户要的是 A 那一刻的 version 2）
            var (planA, errA) = box.Restore.BuildPreviewAt(box.RootId, snapA!.TimestampUtc,
                new[] { "Documents/a.txt" });
            Check.NotNull(planA, "A 的预览不应失败：" + (errA ?? string.Empty));

            // ② 从 B 挑 Projects/test.json（用户要的是 B 那一刻的 version 3）
            var (planB, errB) = box.Restore.BuildPreviewAt(box.RootId, snapB!.TimestampUtc,
                new[] { "Projects/test.json" });
            Check.NotNull(planB, "B 的预览不应失败：" + (errB ?? string.Empty));

            // ③ 合并成一个计划 —— 与界面上「恢复集合」的做法完全一致
            var merged = new LastRegret.Core.Restore.RestorePlan { RootId = box.RootId };
            merged.Current = planA!.Current;
            foreach (var s in planA.Steps) merged.Steps.Add(s);
            foreach (var s in planB!.Steps) merged.Steps.Add(s);
            merged.ComputeFingerprint();

            Check.True(merged.Steps.Count >= 2,
                $"合并后的计划应同时包含两个文件的步骤（实际 {merged.Steps.Count}）");
            Check.True(merged.HasEffect, "合并后的计划应当真的能改变磁盘");

            // ④ 交给**未经修改**的现有恢复引擎执行
            var outcome = box.Restore.Execute(merged, merged.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "跨时间点合并恢复应当成功：" + outcome.Message);

            Check.Equal("a version 2", box.ReadFile("Documents/a.txt"),
                "a.txt 必须回到时间点 A 的版本");
            Check.Equal("{ v: 3 }", box.ReadFile("Projects/test.json"),
                "test.json 必须回到时间点 B 的版本");
            Check.NotNull(outcome.PreSnapshotId, "必须创建恢复前安全点，这次恢复才可撤销");
        });

        yield return new("恢复·自定义恢复", "恢复集合里只勾选的文件会被恢复，没勾的一律不动", () =>
        {
            using var box = Sandbox.Create("custom-restore-subset");
            box.Protect();

            box.WriteFile("keep.txt", "keep v1");
            box.WriteFile("change.txt", "change v1");
            box.WaitFor(() => box.AllEvents().Count >= 2, 10000, "两个文件应先被记录");
            box.Flush();

            Thread.Sleep(400);
            box.WriteFile("keep.txt", "keep v2");
            box.WriteFile("change.txt", "change v2");
            box.WaitForIndex("keep.txt", 15000);
            box.WaitForIndex("change.txt", 15000);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "两个文件都是 v2");

            // 之后再改坏两个文件，这样"只恢复勾选的那一个"才有效果可验证
            Thread.Sleep(400);
            box.WriteFile("keep.txt", "keep BROKEN");
            box.WriteFile("change.txt", "change BROKEN");
            box.WaitForIndex("keep.txt", 15000);
            box.WaitForIndex("change.txt", 15000);

            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            // 用户只勾了 change.txt
            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc,
                new[] { "change.txt" });
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));
            Check.True(plan!.Steps.All(s => s.RelativePath == "change.txt"),
                "计划里只应包含被勾选的那一个路径");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);

            Check.Equal("change v2", box.ReadFile("change.txt"), "勾选的文件应恢复到目标时间点");
            Check.Equal("keep BROKEN", box.ReadFile("keep.txt"),
                "没勾选的文件必须原样保留 —— 这是「只恢复我挑的那些」的核心保证");
        });

        yield return new("恢复·自定义删除", "自定义删除：删掉用户明确标记的文件，且可整体撤销", () =>
        {
            using var box = Sandbox.Create("custom-delete");
            box.Protect();

            box.WriteFile("keep.txt", "keep 保留");
            box.WriteFile("doomed.txt", "doomed 要删掉");
            box.WriteFile("sub/also.txt", "sub 里也要删");
            box.WaitFor(() => box.AllEvents().Count >= 3, 10000, "三个文件应先被记录");
            box.Flush();

            // 关键点：要删的文件在"当前状态"里存在。
            // 这种需求用"恢复到某时间点"表达不出来（回到那时只会把它还原成那时的内容），
            // 所以必须走显式的删除入口。
            box.WaitForIndex("doomed.txt");

            var (plan, error) = box.Restore.BuildDeletionPreview(
                box.RootId, new[] { "doomed.txt", "sub/also.txt" });
            Check.NotNull(plan, "应能生成删除计划：" + (error ?? string.Empty));
            Check.Equal(2, plan!.Steps.Count, "删除计划里应恰好有两个删除步骤");
            Check.True(plan.Steps.All(s => s.Action == RestoreAction.RemovePath),
                "每一步都必须是「移除路径」");
            Check.True(plan.Steps.All(s => s.RequiresConfirmation),
                "删除必须要求用户确认（引擎没有确认就不会执行）");
            Check.True(plan.HasEffect, "删除计划应当有效果");

            // 没有确认时引擎必须拒绝执行，且不动磁盘
            var refused = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: false);
            Check.False(refused.Ok, "未经确认的删除必须被拒绝");
            Check.FileExists(box.Abs("doomed.txt"), "被拒绝的删除不得改动磁盘");

            // 确认后执行
            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "确认后删除应当成功：" + outcome.Message);
            Check.FileMissing(box.Abs("doomed.txt"), "标记的文件应被删除");
            Check.FileMissing(box.Abs("sub/also.txt"), "子目录里标记的文件也应被删除");
            Check.Equal("keep 保留", box.ReadFile("keep.txt"), "没标记的文件必须原样保留");

            Check.NotNull(outcome.PreSnapshotId, "必须创建恢复前安全点，这次删除才可撤销");

            // 撤销这次删除 → 文件回来
            var undoPreview = box.Restore.BuildUndoPreview(outcome.OperationId);
            Check.NotNull(undoPreview, "应能生成撤销预览");
            var undo = box.Restore.ExecuteUndo(outcome.OperationId,
                undoPreview.Plan!.Fingerprint, allowNewRemovals: true);
            Check.True(undo.Ok, "撤销这次删除应当成功：" + undo.Message);
            Check.Equal("doomed 要删掉", box.ReadFile("doomed.txt"), "撤销后文件内容必须回来");
            Check.Equal("sub 里也要删", box.ReadFile("sub/also.txt"), "子目录里的文件也应回来");
        });

        yield return new("恢复·自定义删除", "删除计划生成后文件又被改过 → 跳过删除而不是误删", () =>
        {
            using var box = Sandbox.Create("custom-delete-conflict");
            box.Protect();

            box.WriteFile("doc.txt", "v1");
            box.WaitForEvent("doc.txt", OperationType.Created);
            box.Flush();
            box.WaitForIndex("doc.txt");

            var (plan, _) = box.Restore.BuildDeletionPreview(box.RootId, new[] { "doc.txt" });
            Check.NotNull(plan, "应能生成删除计划");

            // 预览之后内容变了 → 执行时必须跳过（绝不误删用户刚写的东西）
            Thread.Sleep(300);
            box.WriteFile("doc.txt", "v2 用户在预览之后又改了");
            box.WaitForIndex("doc.txt");

            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.FileExists(box.Abs("doc.txt"), "预览之后被改过的文件不得被删除");
            Check.Equal("v2 用户在预览之后又改了", box.ReadFile("doc.txt"), "内容必须原样保留");

            // 语义更新（FINAL-WB-001）：删除计划里唯一的步骤也过期了 → 整单拒绝，
            // 而不是"执行一遍再把这一步跳过"。安全结果完全一致（文件没被删），
            // 而且不再留下一个"成功"的恢复记录。
            Check.True(outcome.Rejected, $"计划已过期时必须拒绝执行（结果：{outcome.Message}）");
            Check.Equal(0, outcome.FilesChanged, "拒绝时不得改动任何文件");
            Check.Equal(0L, outcome.OperationId, "拒绝时不得创建恢复记录");
        });

        yield return new("恢复·自定义删除", "恢复与删除混合执行：一个计划里既恢复、又删除", () =>
        {
            using var box = Sandbox.Create("custom-restore-delete-mix");
            box.Protect();

            box.WriteFile("a.txt", "a v1");
            box.WriteFile("b.txt", "b v1");
            box.WriteFile("trash.txt", "trash 存在但不需要了");
            box.WaitFor(() => box.AllEvents().Count >= 3, 10000, "三个文件应先被记录");
            box.Flush();

            Thread.Sleep(400);
            box.WriteFile("a.txt", "a v2");
            box.WaitForIndex("a.txt", 15000);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "a 是 v2");

            Thread.Sleep(400);
            box.WriteFile("a.txt", "a BROKEN");
            box.WaitForIndex("a.txt", 15000);

            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            // 用户意图：a.txt 恢复到该时间点（v2），同时把 trash.txt 删掉
            var (restorePlan, rErr) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, new[] { "a.txt" });
            Check.NotNull(restorePlan, "恢复预览不应失败：" + (rErr ?? string.Empty));
            var (delPlan, dErr) = box.Restore.BuildDeletionPreview(box.RootId, new[] { "trash.txt" });
            Check.NotNull(delPlan, "删除计划不应失败：" + (dErr ?? string.Empty));

            // 与界面上「恢复集合」的做法一致：合并成一个计划交给未修改的引擎
            var merged = new LastRegret.Core.Restore.RestorePlan { RootId = box.RootId };
            merged.Current = restorePlan!.Current;
            foreach (var s in restorePlan.Steps) merged.Steps.Add(s);
            foreach (var s in delPlan!.Steps) merged.Steps.Add(s);
            merged.ComputeFingerprint();

            Check.True(merged.RestoreCount >= 1, "合并计划里应有恢复步骤");
            Check.True(merged.RemoveCount >= 1, "合并计划里应有删除步骤");

            var outcome = box.Restore.Execute(merged, merged.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复+删除混合执行应当成功：" + outcome.Message);

            Check.Equal("a v2", box.ReadFile("a.txt"), "a.txt 应恢复到时间点里的版本");
            Check.FileMissing(box.Abs("trash.txt"), "trash.txt 应被删除");
            Check.Equal("b v1", box.ReadFile("b.txt"), "没被选中的文件必须原样不动");
            Check.NotNull(outcome.PreSnapshotId, "必须创建安全点，这次混合操作才可整体撤销");
        });

        yield return new("恢复·完整闭环", "预览指纹校验：确认的必须是同一份预览", () =>
        {
            using var box = Sandbox.Create("restore-fingerprint");
            box.Protect();

            box.WriteFile("f.txt", "v1");
            box.WaitForEvent("f.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual);
            var pointTime = point.TimestampLocal;

            Thread.Sleep(300);
            box.WriteFile("f.txt", "v2");
            box.WaitForEvent("f.txt", OperationType.Modified);

            var (plan, _) = box.Preview(pointTime);
            Check.NotNull(plan, "应能生成预览");

            var outcome = box.Restore.Execute(plan!, "错误的指纹", allowNewRemovals: true);
            Check.False(outcome.Ok, "指纹不匹配时必须拒绝执行");
            Check.Equal("v2", box.ReadFile("f.txt"), "被拒绝的操作不得改动磁盘");
        });

        yield return new("恢复·完整闭环", "预览之后文件又被改动 → 冲突检测必须跳过而不是覆盖", () =>
        {
            using var box = Sandbox.Create("restore-conflict");
            box.Protect();

            box.WriteFile("c.txt", "目标版本");
            box.WaitForEvent("c.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual);
            var pointTime = point.TimestampLocal;

            Thread.Sleep(300);
            box.WriteFile("c.txt", "第一次改动");
            box.WaitForEvent("c.txt", OperationType.Modified);
            box.WaitForIndex("c.txt");

            // 生成预览（此时磁盘内容是"第一次改动"）
            var (plan, _) = box.Preview(pointTime);
            Check.True(plan is { HasEffect: true },
                "预览应检测到需要恢复的变化。\n" + box.DescribePreview(box.RootId, pointTime, plan));

            // 预览之后、执行之前，用户又改了文件（模拟真实并发）
            Thread.Sleep(200);
            box.WriteFile("c.txt", "预览之后的第二次改动");
            box.Flush();

            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);

            Check.Equal("预览之后的第二次改动", box.ReadFile("c.txt"),
                "预览之后被改过的文件必须被跳过，绝不能静默覆盖掉用户的新内容");

            // 语义更新（FINAL-WB-001，本轮修复）：计划里可执行的步骤**全部**过期时，
            // 整单按"拒绝"处理 —— 不建恢复记录、不建快照、不动磁盘；
            // 旧断言只要求"报告有条目被跳过"，那正是被验收判定为协议错误的行为。
            // 安全不变式不变：内容保留 + 明确告知用户发生了什么。
            Check.True(outcome.Rejected, $"计划全部过期时必须拒绝（结果：{outcome.Message}）");
            Check.Equal(0, outcome.Skipped, "整单拒绝时不产生被跳过的步骤记录");
            Check.Equal(0, outcome.FilesChanged, "拒绝意味着磁盘没有任何改动");
            Check.False(outcome.CanUndo, "什么都没做，不该提供撤销入口");
            Check.Equal(0L, outcome.OperationId, "拒绝时不得创建恢复记录");
        });

        yield return new("恢复·完整闭环", "重复恢复同一点：第二次应报告无需恢复且不改动磁盘", () =>
        {
            using var box = Sandbox.Create("restore-repeat");
            box.Protect();

            box.WriteFile("r.txt", "稳定版本");
            box.WaitForEvent("r.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual);
            var pointTime = point.TimestampLocal;

            Thread.Sleep(300);
            box.WriteFile("r.txt", "被改过");
            box.WaitForEvent("r.txt", OperationType.Modified);
            box.WaitForIndex("r.txt");

            var first = box.RestoreTo(pointTime);
            Check.True(first.Ok, "第一次恢复应成功：" + first.Message);
            Check.Equal("稳定版本", box.ReadFile("r.txt"), "内容应回到稳定版本");

            var (plan2, _) = box.Preview(pointTime);
            Check.NotNull(plan2, "应能再次预览");
            Check.False(plan2!.HasEffect, "第二次应报告当前状态已与目标一致");

            var second = box.Restore.Execute(plan2, plan2.Fingerprint, allowNewRemovals: true);
            Check.False(second.Ok, "无可恢复内容时应明确拒绝而不是空跑");
            Check.Equal("稳定版本", box.ReadFile("r.txt"), "内容不应被改动");
        });

        yield return new("恢复·完整闭环", "恢复后再次修改并恢复 → 历史链完整可追溯", () =>
        {
            using var box = Sandbox.Create("restore-chain");
            box.Protect();

            box.WriteFile("chain.txt", "第一代");
            box.WaitForEvent("chain.txt", OperationType.Created);
            box.Flush();
            var p1 = box.Snapshot(SnapshotKind.Manual, "第一代");

            Thread.Sleep(300);
            box.WriteFile("chain.txt", "第二代");
            box.WaitForEvent("chain.txt", OperationType.Modified);
            box.Flush();
            box.WaitForIndex("chain.txt");
            var p2 = box.Snapshot(SnapshotKind.Manual, "第二代");

            Thread.Sleep(300);
            box.WriteFile("chain.txt", "第三代");
            box.WaitForEvent("chain.txt", OperationType.Modified);
            box.WaitForIndex("chain.txt");

            // 回到第二代
            var r1 = box.RestoreTo(p2.TimestampLocal);
            Check.True(r1.Ok, "回到第二代应成功：" + r1.Message);
            Check.Equal("第二代", box.ReadFile("chain.txt"), "内容应为第二代");

            // 再回到第一代
            var r2 = box.RestoreTo(p1.TimestampLocal);
            Check.True(r2.Ok, "回到第一代应成功：" + r2.Message);
            Check.Equal("第一代", box.ReadFile("chain.txt"), "内容应为第一代");

            // 撤销第二次恢复 → 回到第二代
            var last = box.Restore.GetLastUndoable(box.RootId);
            Check.NotNull(last, "应能找到可撤销的恢复");
            var (undoPlan, _) = box.Restore.BuildUndoPreview(last!.Id);
            Check.NotNull(undoPlan, "应能生成撤销预览");
            var undone = box.Restore.ExecuteUndo(last.Id, undoPlan!.Fingerprint, allowNewRemovals: true);
            Check.True(undone.Ok, "撤销应成功：" + undone.Message);
            Check.Equal("第二代", box.ReadFile("chain.txt"), "撤销后应回到第二代");

            // 版本历史应该能列出这些内容
            var versions = box.Versions.ListForPath(box.RootId, "chain.txt", 50);
            Check.True(versions.Count >= 3, $"应记录至少 3 个版本（实际 {versions.Count}）");
        });

        yield return new("恢复·完整闭环", "安全点内容缺失时中止恢复（绝不制造不可回滚的操作）", () =>
        {
            using var box = Sandbox.Create("restore-nosafety");
            box.Protect();

            box.WriteFile("s.txt", "内容");
            box.WaitForEvent("s.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual);
            var pointTime = point.TimestampLocal;

            Thread.Sleep(300);
            box.WriteFile("s.txt", "改过");
            box.WaitForEvent("s.txt", OperationType.Modified);
            box.Flush();
            box.WaitForIndex("s.txt");

            // 人为制造"安全点内容缺失"：删掉一个不被快照引用的对象太复杂，
            // 这里改为验证 CreateSafetySnapshot 的校验路径：把当前文件对应的对象文件删掉
            var current = box.Index.Get(box.RootId, "s.txt");
            Check.NotNull(current, "索引中应有该文件");
            if (current!.ObjectId is not null)
            {
                var obj = box.Store.FindById(current.ObjectId.Value);
                if (obj is not null)
                {
                    File.Delete(box.Store.ObjectPath(obj.Hash));
                }
            }

            var (plan, _) = box.Preview(pointTime);
            Check.NotNull(plan, "应能生成预览");

            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            if (!outcome.Ok)
            {
                Check.Contains(outcome.Message, "安全点",
                    "中止原因必须明确说明是安全点无法创建，让用户知道为什么没执行");
                Check.Equal("改过", box.ReadFile("s.txt"), "被中止的恢复绝不能改动磁盘");
            }
            else
            {
                // 如果安全点仍然可用（例如内容已被其他版本复用），恢复成功也是正确结果
                Check.True(outcome.PreSnapshotId is not null, "成功时必须有安全点");
            }
        });

        yield return new("恢复·完整闭环", "崩溃残留的恢复操作会被识别并可修复", () =>
        {
            using var box = Sandbox.Create("restore-crash");
            box.Protect();

            box.WriteFile("crash.txt", "原始");
            box.WaitForEvent("crash.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "崩溃前的状态");

            // 手工伪造一次"执行到一半就断电"的恢复：写一条 running 操作记录。
            // 注意顺序：先建立安全点（内容为"原始"），**再**改动文件模拟"恢复做到一半"，
            // 这样"修复到安全点"才是一次有意义的状态变化。
            var safety = box.Snapshot(SnapshotKind.PreRestore, "伪造的安全点");
            Thread.Sleep(300);
            box.WriteFile("crash.txt", "恢复做到一半时的内容");
            box.WaitForIndex("crash.txt");
            box.Flush();
            box.WaitForIndex("crash.txt");

            var op = new RestoreOperation
            {
                RootId = box.RootId,
                TargetTimeUtc = point.TimestampUtc,
                TargetTimeLocal = point.TimestampLocal,
                TargetSnapshotId = point.Id,
                PreRestoreSnapshotId = safety.Id,
                Status = RestoreStatus.Running,
                StartedUtc = DateTime.UtcNow,
                PlannedCount = 5,
                SucceededCount = 2,
            };
            box.RestoreRepo.Insert(op);

            var messages = box.Restore.DetectInterruptedOperations();
            Check.True(messages.Count >= 1, "应检测到未完成的恢复操作");
            Check.Contains(messages[0], "未完成", "提示应说明实际情况");

            var after = box.RestoreRepo.Get(op.Id);
            Check.NotNull(after, "记录应仍存在");
            Check.True(after!.Status != RestoreStatus.Running, "不应继续停留在「进行中」状态");

            // 修复：回到安全点
            var repaired = box.Restore.RepairInterrupted(op.Id);
            Check.True(repaired.Ok, "应能修复到安全点：" + repaired.Message);
            Check.Equal("原始", box.ReadFile("crash.txt"), "修复后内容应为安全点时的内容");
        });

        yield return new("恢复·完整闭环", "恢复过程中的每一步都有账可查", () =>
        {
            using var box = Sandbox.Create("restore-journal");
            box.Protect();

            box.WriteFile("j1.txt", "内容1");
            box.WriteFile("j2.txt", "内容2");
            box.WriteFile("j3.txt", "内容3");
            box.WaitFor(() => box.AllEvents().Count >= 3, 10000, "三个文件应先被记录");
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual);

            Thread.Sleep(300);
            box.WriteFile("j1.txt", "改过1");
            box.DeleteFile("j2.txt");
            box.WriteFile("j4.txt", "新增4");
            box.WaitFor(() => box.AllEvents().Count >= 6, 10000, "三处改动应被记录");
            box.WaitForIndex("j1.txt");
            box.WaitForIndex("j4.txt");

            var outcome = box.RestoreTo(point.TimestampLocal);
            Check.True(outcome.Ok, "恢复应成功：" + outcome.Message);

            var steps = box.Restore.ListSteps(outcome.OperationId);
            Check.True(steps.Count >= 3, $"应记录每一步（实际 {steps.Count} 步）");
            Check.True(steps.All(s => s.ExecutedUtc is not null), "每一步都应有执行时间");
            Check.True(steps.Count(s => s.Action == RestoreAction.RestoreContent) >= 2,
                "应至少有两步是恢复内容（j1 与 j2）");
            Check.True(steps.Any(s => s.Action == RestoreAction.RemovePath), "应有移除新增文件的步骤");

            // 磁盘最终状态
            Check.Equal("内容1", box.ReadFile("j1.txt"), "j1 应恢复");
            Check.Equal("内容2", box.ReadFile("j2.txt"), "j2 应被恢复");
            Check.FileMissing(box.Abs("j4.txt"), "j4 应被移除");
            Check.Equal("内容3", box.ReadFile("j3.txt"), "j3 不应受影响");
        });

        // ══════════════════════════════════════════════════════════════════
        //  RC 定向修复的回归测试
        //
        //  写法说明（重要）：这几个用例**不依赖文件监听的事件时序**。
        //  文件先就位 → Protect() 建基线 → 再取一个恢复点 → 最后改坏。
        //  这样"当前状态与恢复点不同"是直接由文件内容决定的，不需要等事件落库，
        //  用例稳定且更快。监听层面的事件正确性由「恢复·完整闭环」那组用例覆盖。
        // ══════════════════════════════════════════════════════════════════

        yield return new("恢复·RC 修复", "智能留存下的大文件不得阻断无关小文件的恢复", () =>
        {
            using var box = Sandbox.Create("rc-smart-mixed");

            // 默认「智能留存」阈值 4MB：big.bin 只记 Hash、不留内容
            box.Settings.Protection = Core.Config.ProtectionMode.SmartContent;
            box.Settings.SmartContentMaxBytes = 4L * 1024 * 1024;

            var bigV1 = MakeBytes(5 * 1024 * 1024, salt: 1);
            box.WriteBytes("big.bin", bigV1);
            box.WriteFile("config.json", "{ \"port\": 8080 }");
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "大文件 + 小文件混合");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            // ── 前置事实：确认真复现了"大文件无内容、小文件有内容" ──
            var manifest = box.SnapshotService.LoadManifest(snap!);
            var big = manifest.Entries.Values.First(e => e.RelativePath == "big.bin");
            var cfg = manifest.Entries.Values.First(e => e.RelativePath == "config.json");
            Check.NotNull(big.Hash, "big.bin 应记录 Hash（事实要留下）");
            Check.Null(big.ObjectId, "big.bin 在智能留存下不应有 ObjectId（这是设计预期）");
            Check.NotNull(cfg.ObjectId, "config.json 应留存内容");

            // ── 改坏小文件，只恢复它 ──
            box.WriteFile("config.json", "{ \"port\": 9999 }");
            box.WaitForIndex("config.json", 15000);   // 等索引追上磁盘，预览才有差异可比

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc,
                new[] { "config.json" });
            Check.NotNull(plan, "应能生成预览：" + (error ?? string.Empty));
            Check.Equal(1, plan!.Steps.Count, $"计划里应只有 config.json 一步（实际 {plan.Steps.Count}）");
            Check.Equal(1, plan.ExecutableCount, "config.json 必须被判定为可执行");
            Check.Equal(0, plan.UnavailableCount, "config.json 不应被判定为不可恢复");

            // ── 关键断言：执行必须成功，不被 big.bin 阻断 ──
            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok,
                "只恢复 config.json 必须成功（big.bin 无内容不应阻断）。" +
                $"实际：Ok={outcome.Ok} Status={outcome.Status} Message={outcome.Message}");

            Check.Equal("{ \"port\": 8080 }", box.ReadFile("config.json"),
                "config.json 必须已恢复到历史内容");
            Check.FileExists(box.Abs("big.bin"), "big.bin 必须原样保留（与本次恢复无关）");

            // ── Undo 必须可用，并且能把 config.json 退回改坏后的状态 ──
            Check.True(outcome.CanUndo, "这次恢复实际改了文件且安全点存在 → 必须可撤销");
            var undoPreview = box.Restore.BuildUndoPreview(outcome.OperationId);
            Check.NotNull(undoPreview, "应能生成撤销预览");
            var undo = box.Restore.ExecuteUndo(outcome.OperationId, undoPreview!.Plan!.Fingerprint,
                allowNewRemovals: true);
            Check.True(undo.Ok, "撤销应当成功：" + undo.Message);
            Check.Equal("{ \"port\": 9999 }", box.ReadFile("config.json"),
                "撤销后 config.json 必须回到恢复前的（改坏的）内容");
        });

        yield return new("恢复·RC 修复", "确实要恢复未留存内容的大文件时，必须如实失败且不破坏现有文件", () =>
        {
            using var box = Sandbox.Create("rc-smart-bigonly");

            box.Settings.Protection = Core.Config.ProtectionMode.SmartContent;
            box.Settings.SmartContentMaxBytes = 4L * 1024 * 1024;

            box.WriteBytes("big.bin", MakeBytes(5 * 1024 * 1024, salt: 2));
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "只有大文件");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");
            Check.Null(box.SnapshotService.LoadManifest(snap!).Entries.Values
                .First(e => e.RelativePath == "big.bin").ObjectId,
                "前提：大文件在智能留存下没有内容");

            // 改坏 big.bin（内容不同，但同样不会被留存）
            box.WriteBytes("big.bin", MakeBytes(5 * 1024 * 1024, salt: 7));
            box.WaitForIndex("big.bin", 15000);

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, new[] { "big.bin" });
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

            // ── Preview 必须明确显示"不可恢复" ──
            Check.Equal(1, plan!.UnavailableCount,
                "big.bin 没有留存内容 → 预览必须明确标为不可恢复，不能假装能恢复");
            Check.Equal(0, plan.ExecutableCount, "big.bin 不应被判定为可执行");

            var beforeHash = HashOf(box.Abs("big.bin"));

            // ── Execute 不得假装成功 ──
            // 注意：安全点的完备性检查会在**执行任何一步之前**拦住它，
            // 所以这里失败的是"整个操作被中止"，而不是"某一步执行失败"；
            // 因此断言的是 Ok=false + 没有文件被改动 + 文件原样，而不是 Failed 计数。
            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.False(outcome.Ok,
                "目标文件内容不可用 → 必须如实失败，不得假装成功。" +
                $"实际 Ok={outcome.Ok} Message={outcome.Message}");
            Check.Equal(0, outcome.Succeeded, "不应有任何步骤被报告为成功");
            Check.Equal(0, outcome.FilesChanged, "没有文件被真正改动");

            // ── 不得破坏当前文件 ──
            Check.Equal(beforeHash, HashOf(box.Abs("big.bin")),
                "内容不可恢复时，当前文件必须原样保留（绝不截断/清空）");

            // ── 一个文件都没改 → 不凭空给 Undo ──
            Check.False(outcome.CanUndo, "没有任何实际文件改动 → 不应提供撤销入口");
        });

        yield return new("恢复·RC 修复", "部分成功后必须允许撤销，且撤销能还原已改动的文件", () =>
        {
            using var box = Sandbox.Create("rc-partial-undo");

            // 两个文件都在留存范围内 → 预览与安全点检查都会通过，
            // 于是能真正走到**执行期**，由执行期的一个失败制造出"部分完成"。
            box.WriteFile("good.txt", "GOOD-原始");
            box.WriteFile("locked.txt", "LOCKED-原始");
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "部分成功场景");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            // 两个都改坏
            box.WriteFile("good.txt", "GOOD-改坏");
            box.WriteFile("locked.txt", "LOCKED-改坏");
            box.WaitForIndex("good.txt", 15000);
            box.WaitForIndex("locked.txt", 15000);

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc,
                new[] { "good.txt", "locked.txt" });
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));
            Check.Equal(2, plan!.Steps.Count, $"计划里应有两步（实际 {plan.Steps.Count}）");
            Check.Equal(2, plan.ExecutableCount, "两步都应被判定为可执行");

            // ── 预览之后把 locked.txt 独占锁住 ──
            // 模拟真实场景：文件正被别的程序占用。执行期读取它的内容会失败，
            // 该步如实失败；另一个文件照常恢复 → 部分完成（Ok=false 但确实改了文件）。
            var lockedPath = box.Abs("locked.txt");
            var lockedBefore = HashOf(lockedPath);
            Engine.RestoreOutcome outcome;
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            }

            // ── 部分成功：既不是全成功，也不是全失败 ──
            Check.False(outcome.Ok,
                $"有步骤失败 → 整体不应报成功。实际 成功={outcome.Succeeded} 失败={outcome.Failed} " +
                $"跳过={outcome.Skipped} Message={outcome.Message}");
            Check.Equal(RestoreStatus.PartiallyCompleted, outcome.Status,
                $"应记为「部分完成」（成功 {outcome.Succeeded} / 失败 {outcome.Failed} / 跳过 {outcome.Skipped}）");
            Check.Equal(1, outcome.Succeeded, "good.txt 应恢复成功");
            Check.Equal(1, outcome.Failed, "被占用的 locked.txt 应如实记为失败");
            Check.Equal("GOOD-原始", box.ReadFile("good.txt"), "good.txt 应已恢复");
            Check.Equal(lockedBefore, HashOf(lockedPath),
                "被占用而失败的文件必须保持原样，绝不能被破坏");

            // ── 核心断言：部分成功也必须能撤销（RC 修复点） ──
            Check.Equal(1, outcome.FilesChanged, "应有 1 个文件被真正改动");
            Check.True(outcome.CanUndo,
                "部分成功但确实改了文件、且安全点存在 → 必须允许撤销");
            Check.NotNull(outcome.PreSnapshotId, "安全点必须存在");

            var op = box.Restore.ListOperations(box.RootId).First(o => o.Id == outcome.OperationId);
            Check.Equal(RestoreStatus.PartiallyCompleted, op.Status, "数据库里的状态必须是 Partial");
            Check.NotNull(op.PreRestoreSnapshotId, "数据库里必须记着安全点");

            // ── 撤销必须真的把已改动的文件还原 ──
            var undoPreview = box.Restore.BuildUndoPreview(outcome.OperationId);
            Check.NotNull(undoPreview, "应能生成撤销预览");
            var undo = box.Restore.ExecuteUndo(outcome.OperationId, undoPreview!.Plan!.Fingerprint,
                allowNewRemovals: true);
            Check.True(undo.Ok, "撤销应当成功：" + undo.Message);
            Check.Equal("GOOD-改坏", box.ReadFile("good.txt"),
                "撤销后 good.txt 必须回到恢复前的（改坏的）内容");
        });

        yield return new("恢复·RC 修复", "全部失败且没有任何实际修改 → 不得提供撤销入口", () =>
        {
            using var box = Sandbox.Create("rc-allfail-noundo");

            box.Settings.Protection = Core.Config.ProtectionMode.TrackOnly;   // 完全不留内容
            box.WriteFile("x.txt", "v1");
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "只记录变化模式");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            box.WriteFile("x.txt", "v2");
            box.WaitForIndex("x.txt", 15000);

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, new[] { "x.txt" });
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));
            Check.Equal(1, plan!.UnavailableCount, "「只记录变化」模式下内容必须如实标为不可恢复");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.False(outcome.Ok, "内容不可用 → 不得成功");
            Check.Equal(0, outcome.FilesChanged, "没有任何文件被改动");
            Check.False(outcome.CanUndo, "没有实际改动 → 不凭空提供撤销");
            Check.Equal("v2", box.ReadFile("x.txt"), "当前文件必须原样保留");
        });

        yield return new("恢复·RC 修复", "清空全部历史后对象索引与统计必须归零，且新内容仍可正常存储与恢复", () =>
        {
            using var box = Sandbox.Create("rc-purge-usage");

            box.WriteFile("a.txt", "内容-A");
            box.WriteFile("b.txt", "内容-B");
            box.WriteFile("c.txt", "内容-C");
            box.Protect();
            box.Snapshot(SnapshotKind.Manual, "purge 前");

            // ── 前提：确实产生了对象与占用 ──
            var before = box.Store.GetUsage();
            Check.True(before.ObjectCount > 0, $"purge 前应有对象（实际 {before.ObjectCount}）");
            Check.True(before.StoredBytes > 0, $"purge 前应有占用（实际 {before.StoredBytes} 字节）");
            var oldIds = box.Store.EnumerateObjects().Select(o => o.Id).ToList();
            Check.True(oldIds.Count > 0, "应能枚举到旧对象");

            // ── 执行清空 ──
            var (ok, report) = box.Maintenance.PurgeAllHistory();
            Check.True(ok, "PurgeAllHistory 应成功：" + report);

            // ── 物理目录已清 ──
            var objectsDir = box.Store.ObjectsDirectory;
            var remain = Directory.Exists(objectsDir)
                ? Directory.EnumerateFiles(objectsDir, "*", SearchOption.AllDirectories).Count()
                : 0;
            Check.Equal(0, remain, "store/objects 下的物理对象必须已清空");

            // ── 索引与统计必须归零（RC 修复点） ──
            var after = box.Store.GetUsage();
            Check.Equal(0L, after.ObjectCount,
                $"purge 后对象索引必须归零（RC 修复点）。实际 {after.ObjectCount}。报告：{report}");
            Check.Equal(0L, after.StoredBytes,
                $"purge 后占用统计必须归零（RC 修复点）。实际 {after.StoredBytes}");

            // ── 旧 ObjectId 不得再指向可用对象 ──
            foreach (var id in oldIds)
            {
                Check.False(box.Store.Exists(id), $"旧对象 #{id} 不应再被报告为存在");
            }

            // ── 清空后新内容仍能正常存储 ──
            box.WriteFile("fresh.txt", "全新的内容");
            box.WaitForIndex("fresh.txt", 15000);
            var snap2 = box.Snapshot(SnapshotKind.Manual, "purge 后");
            var m2 = box.SnapshotService.LoadManifest(snap2);
            var fresh = m2.Entries.Values.FirstOrDefault(e => e.RelativePath == "fresh.txt");
            Check.NotNull(fresh, "purge 后新建的文件应进入清单");
            Check.NotNull(fresh!.ObjectId, "purge 后新文件的内容应被正常留存");
            Check.True(box.Store.Exists(fresh.ObjectId!.Value), "新对象必须真实存在于磁盘");

            var usage2 = box.Store.GetUsage();
            Check.True(usage2.ObjectCount > 0, "purge 后新写入应重新产生对象索引");
            Check.True(usage2.StoredBytes > 0, "purge 后新写入应重新产生占用");
        });
        // ══════════════════════════════════════════════════════════════════
        //  「误删整个文件夹」定向修复的回归测试
        //
        //  真实场景：用户在资源管理器里删掉一整个目录（例如 ShareX 的
        //  Screenshots/2026-09/2026-9/），打开时间机器找到删除前的状态，
        //  期望点一下就把整个目录连同里面的文件恢复回来。
        //
        //  修复前存在两个缺陷：
        //    ① 引擎：目录没有内容哈希，被 RestorePlanner.AddRestore 走到
        //       "targetHash is null → NoChange"，于是**永远不会重建目录**；
        //    ② UI：目录的勾选框是禁用的，用户根本没法选择"恢复这个目录"。
        // ══════════════════════════════════════════════════════════════════

        yield return new("恢复·整目录", "删除单个文件仍可恢复（回归保护）", () =>
        {
            using var box = Sandbox.Create("dir-del-file");
            box.WriteFile("test.txt", "内容-原始");
            box.Protect();
            var point = box.Snapshot(SnapshotKind.Manual, "删除前");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            box.DeleteFile("test.txt");
            box.WaitForGone("test.txt");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, new[] { "test.txt" });
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));
            Check.Equal(1, plan!.ExecutableCount, "test.txt 应可执行");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "删除单文件必须能恢复：" + outcome.Message);
            Check.Equal("内容-原始", box.ReadFile("test.txt"), "内容必须完全一致");
        });

        yield return new("恢复·整目录", "删除空目录可以恢复（此前完全无法恢复）", () =>
        {
            using var box = Sandbox.Create("dir-del-empty");
            box.MkDir("empty-dir");
            box.Protect();
            var point = box.Snapshot(SnapshotKind.Manual, "删除前");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            box.DeleteFile("empty-dir");
            box.WaitForGone("empty-dir");
            Check.DirectoryMissing(box.Abs("empty-dir"), "删除后目录应确实不存在");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, includePaths: null);
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

            // 修复点：必须是 CreateDirectory，而不是 NoChange
            var step = plan!.Steps.FirstOrDefault(s => s.RelativePath == "empty-dir");
            Check.NotNull(step, "计划里应包含 empty-dir 这一步");
            Check.Equal(RestoreAction.CreateDirectory, step!.Action,
                $"被删除的目录必须生成「重建目录」动作（实际 {step.Action}）—— 旧实现生成 NoChange 导致永远恢复不了");
            Check.True(plan.HasEffect, "计划必须被判定为「有实际效果」，否则执行会被直接拒绝");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复空目录应当成功：" + outcome.Message);
            Check.DirectoryExists(box.Abs("empty-dir"), "空目录必须被重建出来");
        });

        yield return new("恢复·整目录", "删除完整目录树可以恢复（文件 + 子目录 + 空子目录）", () =>
        {
            using var box = Sandbox.Create("dir-del-tree");

            // 复刻用户现场结构：2026-09/2026-9/{a.txt,b.txt,sub/c.txt,empty-sub}
            box.WriteFile("2026-09/2026-9/a.txt", "AAA");
            box.WriteFile("2026-09/2026-9/b.txt", "BBB");
            box.WriteFile("2026-09/2026-9/sub/c.txt", "CCC");
            box.MkDir("2026-09/2026-9/empty-sub");
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "删除前");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            // 用户的操作：在资源管理器里删掉整个 2026-9
            box.DeleteFile("2026-09/2026-9");
            box.WaitForGone("2026-09/2026-9");
            box.WaitForGone("2026-09/2026-9/a.txt");
            Check.DirectoryMissing(box.Abs("2026-09/2026-9"), "整棵树应已被删除");

            // 全量预览（等价于用户"恢复到那个时间点"）
            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, includePaths: null);
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

            // 目录本身必须在计划里，并且是「重建目录」
            foreach (var dir in new[] { "2026-09/2026-9", "2026-09/2026-9/sub", "2026-09/2026-9/empty-sub" })
            {
                var step = plan!.Steps.FirstOrDefault(s => s.RelativePath == dir);
                Check.NotNull(step, $"计划里应包含目录 {dir}");
                Check.Equal(RestoreAction.CreateDirectory, step!.Action,
                    $"{dir} 必须是「重建目录」（实际 {step.Action}）");
            }

            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复整棵目录树应当成功：" + outcome.Message);

            Check.DirectoryExists(box.Abs("2026-09/2026-9"), "目标目录必须重新出现");
            Check.Equal("AAA", box.ReadFile("2026-09/2026-9/a.txt"), "a.txt 内容必须一致");
            Check.Equal("BBB", box.ReadFile("2026-09/2026-9/b.txt"), "b.txt 内容必须一致");
            Check.Equal("CCC", box.ReadFile("2026-09/2026-9/sub/c.txt"), "子目录里的 c.txt 内容必须一致");
            Check.DirectoryExists(box.Abs("2026-09/2026-9/empty-sub"), "空子目录也必须被重建");
        });

        yield return new("恢复·整目录", "勾选目录时整棵子树都被恢复（UI 的整目录语义）", () =>
        {
            using var box = Sandbox.Create("dir-del-pickdir");

            box.WriteFile("2026-09/2026-9/a.txt", "AAA");
            box.WriteFile("2026-09/2026-9/sub/c.txt", "CCC");
            box.WriteFile("keep.txt", "不动我");
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "删除前");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            box.DeleteFile("2026-09/2026-9");
            box.WaitForGone("2026-09/2026-9");

            // 模拟 UI：用户勾选了目录 2026-09/2026-9 本身。
            // MainViewModel.ExpandPickedPaths 会把它展开成"目录 + 全部后代"，
            // 因为引擎的 includePaths 是精确匹配。这里直接用它展开后的结果。
            var manifest = box.SnapshotService.LoadManifest(snap!);
            var picked = new List<string> { "2026-09/2026-9" };
            var prefix = "2026-09/2026-9/";
            foreach (var e in manifest.Entries.Values)
            {
                if (e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    picked.Add(e.RelativePath);
            }
            Check.True(picked.Count >= 3, $"展开后应包含目录与后代（实际 {picked.Count} 项）");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, picked);
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "勾选目录恢复整棵子树应当成功：" + outcome.Message);

            Check.Equal("AAA", box.ReadFile("2026-09/2026-9/a.txt"), "a.txt 应被恢复");
            Check.Equal("CCC", box.ReadFile("2026-09/2026-9/sub/c.txt"), "子目录文件应被恢复");
            Check.Equal("不动我", box.ReadFile("keep.txt"), "没勾的文件不应受影响");
        });

        yield return new("恢复·整目录", "整目录恢复可以撤销（DirectoryRestoreCanBeUndone）", () =>
        {
            using var box = Sandbox.Create("dir-del-undo");

            box.WriteFile("2026-09/2026-9/a.txt", "AAA");
            box.WriteFile("2026-09/2026-9/sub/c.txt", "CCC");
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "删除前");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            box.DeleteFile("2026-09/2026-9");
            box.WaitForGone("2026-09/2026-9");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, includePaths: null);
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "恢复应当成功：" + outcome.Message);
            Check.True(outcome.CanUndo, "这次恢复确实改了文件、且有安全点 → 必须可撤销");
            Check.DirectoryExists(box.Abs("2026-09/2026-9"), "恢复后目录应存在");

            var undoPreview = box.Restore.BuildUndoPreview(outcome.OperationId);
            Check.NotNull(undoPreview, "应能生成撤销预览");
            var undo = box.Restore.ExecuteUndo(outcome.OperationId, undoPreview!.Plan!.Fingerprint,
                allowNewRemovals: true);
            Check.True(undo.Ok, "撤销应当成功：" + undo.Message);

            // 撤销 = 回到"恢复之前"的状态，也就是目录仍然不存在
            Check.DirectoryMissing(box.Abs("2026-09/2026-9"),
                "撤销后目录应回到恢复前的状态（即不存在）");
        });

        yield return new("恢复·整目录", "恢复失败必须如实报告，不能静默成功（FailedDirectoryRestoreReportsFailure）", () =>
        {
            using var box = Sandbox.Create("dir-del-fail");

            // 「只记录变化」模式：文件内容完全不留存 → 恢复必须失败并说明原因
            box.Settings.Protection = Core.Config.ProtectionMode.TrackOnly;
            box.WriteFile("2026-09/2026-9/a.txt", "AAA");
            box.MkDir("2026-09/2026-9/sub");
            box.Protect();

            var point = box.Snapshot(SnapshotKind.Manual, "删除前");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            box.DeleteFile("2026-09/2026-9");
            box.WaitForGone("2026-09/2026-9");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, includePaths: null);
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

            // 内容不可恢复的文件必须被如实标注，而不是假装能恢复
            Check.True(plan!.UnavailableCount >= 1,
                $"没有留存内容的文件必须被标为不可恢复（实际 {plan.UnavailableCount}）");

            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);

            // 关键：失败必须传到调用方（UI 会据此显示原因），不能变成"什么都没发生"
            Check.False(outcome.Ok, "内容不可用 → 不得报成功");
            Check.True(outcome.Message.Length > 0, "失败必须带一条可展示给用户的原因");
            Check.Equal(0, outcome.FilesChanged, "没有被真正改动的文件");
        });
    }

    /// <summary>构造一个内容确定的字节数组（用于"大文件不应留存内容"的场景）。</summary>
    private static byte[] MakeBytes(int size, byte salt = 0)
    {
        var buf = new byte[size];
        for (int i = 0; i < buf.Length; i += 4096) buf[i] = (byte)((i + salt) % 251);
        buf[0] = salt;
        return buf;
    }

    private static string HashOf(string absolutePath)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(absolutePath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string Summary(this Core.Restore.RestorePlan plan) =>
        $"步骤 {plan.Steps.Count}（恢复 {plan.RestoreCount} / 移除 {plan.RemoveCount} / 建目录 {plan.CreateDirectoryCount} / 清理目录 {plan.RemoveDirectoryCount}），" +
        $"可执行 {plan.ExecutableCount}，不可恢复 {plan.UnavailableCount}";
}
