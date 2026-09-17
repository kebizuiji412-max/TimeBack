using System.Reflection;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;
using LastRegret.Engine;
using LastRegret.Runtime;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 应用能力边界（TimeBackApplication）的针对性验证。
///
/// 这里刻意**不**测试 "整个产品还能用"（那是其它套件的事），
/// 只验证一件事：<b>对外能力门面真的把 6 个 capability 映射到了现有引擎上，
/// 而且没有绕过任何一条既有安全链。</b>
/// </summary>
public static class ApplicationSuites
{
    /// <summary>用沙箱里真实的引擎/仓储装配门面（组装本身由组合根负责，这里只是注入）。</summary>
    private static TimeBackApplication App(Sandbox box) =>
        new(box.Watch, box.Compare, box.Events, box.Restore);

    public static IEnumerable<TestCase> All()
    {
        yield return new("应用能力边界", "protected-folders.read：能通过门面读到受保护文件夹及其状态", () =>
        {
            using var box = Sandbox.Create("app-folders");
            box.Protect();
            var app = App(box);

            var folders = app.GetProtectedFolders();

            Check.Equal(1, folders.Count, "应能看到 1 个受保护文件夹");
            Check.Equal(box.RootId, folders[0].RootId, "根 Id 应与沙箱一致");
            Check.True(folders[0].Enabled, "该文件夹应为启用状态");
            Check.True(folders[0].RootPath.Length > 0, "应能读到路径");
        });

        yield return new("应用能力边界", "timeline.read：能通过门面读到恢复点（时间点）清单", () =>
        {
            using var box = Sandbox.Create("app-timeline");
            box.Protect();
            box.WriteFile("a.txt", "第一版");
            box.WaitForEvent("a.txt", OperationType.Created);
            box.Snapshot(SnapshotKind.Manual, "门面测试点");

            var app = App(box);
            var points = app.GetTimeline(box.RootId);

            Check.True(points.Count >= 1, "应至少有一个时间点");
            Check.True(points.Any(p => p.Note == "门面测试点"), "应能读到刚创建的恢复点");
            Check.True(points.All(p => p.RootId == box.RootId), "时间点都应属于该根");
            Check.True(points.Any(p => p.FileCount >= 1), "应带规模信息（文件数）");
        });

        yield return new("应用能力边界", "changes.read：能通过门面读到变化记录", () =>
        {
            using var box = Sandbox.Create("app-changes");
            box.Protect();
            box.WriteFile("b.txt", "内容");
            box.WaitForEvent("b.txt", OperationType.Created);

            var app = App(box);
            var changes = app.GetChanges(new EventQuery { RootId = box.RootId, Limit = 100 });

            Check.True(changes.Any(e => e.RelativePath == "b.txt"), "应能读到 b.txt 的变化记录");
            Check.True(changes.All(e => e.RootId == box.RootId), "记录都应属于该根");
        });

        yield return new("应用能力边界", "restore.preview：生成预览不产生任何磁盘副作用", () =>
        {
            using var box = Sandbox.Create("app-preview");
            box.Protect();
            box.WriteFile("c.txt", "旧内容");
            box.WaitForEvent("c.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "预览用");
            Thread.Sleep(300);
            box.WriteFile("c.txt", "新内容");
            box.WaitForEvent("c.txt", OperationType.Modified);
            box.Flush();                      // 确保"改动"已落库（快照水位线依赖它）
            box.WaitForIndex("c.txt");        // 并确保索引已追上磁盘内容

            var app = App(box);

            // 副作用基线：快照数 / 版本行数 / 恢复操作数 / 内容对象数 / 磁盘内容
            int snapshotsBefore = box.SnapshotsRepo.List(box.RootId).Count;
            long versionsBefore = box.Versions.Count(box.RootId);
            int operationsBefore = box.RestoreRepo.ListRecent(box.RootId, 100).Count;
            long objectsBefore = box.Store.GetUsage().ObjectCount;
            string diskBefore = box.ReadFile("c.txt");

            var (plan, error) = app.PreviewRestore(box.RootId, point.TimestampUtc);

            Check.NotNull(plan, "预览应成功生成计划" + (error is null ? "" : $"（错误：{error}）"));
            Check.True(plan!.HasEffect, "预览应显示确实有内容需要恢复");
            Check.True(plan.Fingerprint.Length > 0, "计划应带指纹");
            Check.Equal(diskBefore, box.ReadFile("c.txt"), "预览不得改动磁盘上的文件");
            Check.Equal(snapshotsBefore, box.SnapshotsRepo.List(box.RootId).Count, "预览不得创建快照");
            Check.Equal(versionsBefore, box.Versions.Count(box.RootId), "预览不得写入文件版本行");
            Check.Equal(operationsBefore, box.RestoreRepo.ListRecent(box.RootId, 100).Count, "预览不得写入恢复操作");
            Check.Equal(objectsBefore, box.Store.GetUsage().ObjectCount, "预览不得写入内容对象");
        });

        yield return new("应用能力边界", "restore.execute：仍走原 RestoreEngine（指纹校验 + 安全点 + 真恢复）", () =>
        {
            using var box = Sandbox.Create("app-execute");
            box.Protect();
            box.WriteFile("d.txt", "原始内容");
            box.WaitForEvent("d.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "执行用");
            Thread.Sleep(300);
            box.WriteFile("d.txt", "被改坏");
            box.WaitForEvent("d.txt", OperationType.Modified);
            box.Flush();
            box.WaitForIndex("d.txt");

            var app = App(box);
            var (plan, error) = app.PreviewRestore(box.RootId, point.TimestampUtc);
            Check.NotNull(plan, "预览应成功" + (error is null ? "" : $"（错误：{error}）"));
            Check.True(plan!.HasEffect, "预览应显示确实有内容需要恢复");

            // ① 指纹不符必须被拒绝，且磁盘不得被改动
            var rejected = app.ExecuteRestore(plan!, "错误的指纹", allowNewRemovals: true);
            Check.False(rejected.Ok, "指纹不符必须被拒绝");
            Check.Equal("被改坏", box.ReadFile("d.txt"), "被拒绝的执行不得改动磁盘");

            // ② 指纹正确则执行，且必须落成一条真实恢复记录 + 恢复前安全点
            var outcome = app.ExecuteRestore(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "正确指纹应执行成功：" + outcome.Message);
            Check.Equal("原始内容", box.ReadFile("d.txt"), "应真的恢复到目标时间点");
            Check.True(outcome.OperationId > 0, "应产生恢复操作 Id");
            Check.NotNull(outcome.PreSnapshotId, "执行前必须建立安全点");
            Check.True(outcome.FilesChanged > 0, "应报告实际改动数");
            Check.True(box.RestoreRepo.Get(outcome.OperationId) is not null, "恢复记录必须真的落库");
        });

        yield return new("应用能力边界", "restore.undo：仍走原撤销逻辑（以安全点为目标再恢复一次）", () =>
        {
            using var box = Sandbox.Create("app-undo");
            box.Protect();
            box.WriteFile("e.txt", "第一版");
            box.WaitForEvent("e.txt", OperationType.Created);
            box.Flush();
            var point = box.Snapshot(SnapshotKind.Manual, "撤销用");
            Thread.Sleep(300);
            box.WriteFile("e.txt", "第二版");
            box.WaitForEvent("e.txt", OperationType.Modified);
            box.Flush();
            box.WaitForIndex("e.txt");

            var app = App(box);
            var (plan, _) = app.PreviewRestore(box.RootId, point.TimestampUtc);
            Check.NotNull(plan, "预览应成功");
            Check.True(plan!.HasEffect, "预览应显示确实有内容需要恢复");
            var restored = app.ExecuteRestore(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(restored.Ok, "先做一次恢复：" + restored.Message);
            Check.Equal("第一版", box.ReadFile("e.txt"), "恢复后应为第一版");

            // 撤销必须是"可发现的 + 必须先预览"的
            var last = app.GetLastUndoableRestore(box.RootId);
            Check.NotNull(last, "应能发现可撤销的恢复操作");
            Check.Equal(restored.OperationId, last!.Id, "应指向刚才那次恢复");

            var (undoPlan, undoError) = app.PreviewUndo(last.Id);
            Check.NotNull(undoPlan, "撤销预览应成功" + (undoError is null ? "" : $"（错误：{undoError}）"));

            var undone = app.UndoRestore(last.Id, undoPlan!.Fingerprint, allowNewRemovals: true);
            Check.True(undone.Ok, "撤销应成功：" + undone.Message);
            Check.Equal("第二版", box.ReadFile("e.txt"), "撤销后应回到恢复之前的内容");
            Check.True(box.RestoreRepo.Get(last.Id)!.UndoneByOperationId is not null, "被撤销的操作应被标记");
        });

        yield return new("应用能力边界", "边界本身：门面不依赖 WPF、公开签名不暴露 SQLite 内部类型", () =>
        {
            var assembly = typeof(TimeBackApplication).Assembly;

            var refs = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
            foreach (var forbidden in new[] { "PresentationFramework", "PresentationCore", "WindowsBase" })
            {
                Check.False(refs.Any(n => n.Equals(forbidden, StringComparison.OrdinalIgnoreCase)),
                    $"应用层程序集不得引用 {forbidden}");
            }

            // 公开能力签名里不得出现 SQLite / 数据库内部类型（Agent 面向 capability，不面向实现）
            var exposed = typeof(TimeBackApplication)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
                .Select(t => t.FullName ?? t.Name)
                .ToList();

            Check.False(exposed.Any(n => n.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)),
                "公开能力不得暴露 Sqlite 类型，实际：" + string.Join(", ", exposed.Where(n => n.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))));
            Check.False(exposed.Any(n => n.Contains("System.Data", StringComparison.OrdinalIgnoreCase)),
                "公开能力不得暴露 System.Data 类型");
        });
    }
}
