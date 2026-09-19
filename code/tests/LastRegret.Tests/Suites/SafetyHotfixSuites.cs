using System.Diagnostics;
using System.Reflection;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;
using LastRegret.Engine;
using LastRegret.Windows.Io;

namespace LastRegret.Tests.Suites;

/// <summary>
/// v0.2.1 安全热修的针对性套件。
///
/// 只覆盖三件事：
///   P0-1 物理根边界（Junction / 符号链接不能让恢复穿出受保护范围）
///   P0-2 恢复事务串行（并发恢复必须被拒绝；共享执行状态必须消失）
///   P1-1 真实目录删除的 Kind 判定（要用旧索引，不能用删除后的磁盘）
///
/// 这些用例刻意独立成组，不往 ReliabilitySuites 里继续堆。
/// </summary>
public static class SafetyHotfixSuites
{
    public static IEnumerable<TestCase> All()
    {
        // ═══════════════ P0-1 物理根边界 ═══════════════

        yield return new("安全热修", "扫描不穿 Junction：链接目标的内容不得进入索引与快照清单", () =>
        {
            using var box = Sandbox.Create("v021-scan-junction");
            var outside = OutsideDir(box);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "OUTSIDE");

            var link = Path.Combine(box.WatchDir, "link");
            if (!TryCreateJunction(link, outside))
            {
                Check.True(false,
                    "本机无法创建 Junction（mklink /J 失败），本条安全用例无法验证 —— 不静默通过");
            }

            box.Protect();
            box.Flush();

            Check.Null(box.Index.Get(box.RootId, "link/secret.txt"),
                "Junction 目标里的文件绝不能进入索引（否则保护范围被无声扩大）");
            Check.Null(box.Index.Get(box.RootId, "link"),
                "Junction 目录本身也不应作为普通目录进入索引");

            var snap = box.Snapshot(SnapshotKind.Manual, "v021-scan");
            var manifest = box.SnapshotService.LoadManifest(snap);
            Check.False(manifest.Entries.Values.Any(e =>
                    e.RelativePath.StartsWith("link", StringComparison.OrdinalIgnoreCase)),
                "快照清单里不得出现 Junction 之下的任何条目");
        });

        yield return new("安全热修", "Preview 之后目录被换成 Junction：必须拒绝，且链接目标一个字节都不能被碰", () =>
        {
            using var box = Sandbox.Create("v021-preview-swap");
            var outside = OutsideDir(box);
            Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "a.txt");

            box.Protect();

            // ① watched\safe\a.txt = OLD，建立可恢复历史
            box.WriteFile("safe/a.txt", "OLD");
            box.WaitForIndex("safe/a.txt");
            box.Flush();
            var snap = box.Snapshot(SnapshotKind.Manual, "v021-swap-base");

            // ② 改成 NEW
            box.WriteFile("safe/a.txt", "NEW");
            box.WaitForIndex("safe/a.txt");
            box.Flush();

            // ③ 预览：恢复到 OLD。此刻预览记录下的"当前内容"= NEW
            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap.TimestampUtc, new[] { "safe/a.txt" });
            Check.NotNull(plan, "预览应当成功：" + error);

            // ④ 预览已经生成之后，把 safe 换成指向 outside 的 Junction。
            //
            //    ⚠ 这里必须让 outside\a.txt 的内容**恰好等于预览期望的 NEW**：
            //    否则"计划过期检查"（FindStalePlanReason）会先一步拒绝，
            //    那样即使物理边界完全失效，这条用例也会绿 —— 变成假通过。
            //    内容相同之后，过期检查会放行，能挡住这次越界写入的**只有物理边界**。
            File.WriteAllText(sentinel, "NEW");
            var sentinelHashBefore = Sha256(sentinel);

            Directory.Delete(box.Abs("safe"), recursive: true);
            if (!TryCreateJunction(box.Abs("safe"), outside))
            {
                Check.True(false, "本机无法创建 Junction，本条破坏性不变量无法验证 —— 不静默通过");
            }

            // ⑤ 执行那份已经确认过的计划：它会试着把 safe\a.txt 写回 OLD
            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);

            Check.True(outcome.Rejected || !outcome.Ok,
                $"目录被换成 Junction 之后必须拒绝执行。实际 Ok={outcome.Ok} Rejected={outcome.Rejected} " +
                $"OperationId={outcome.OperationId} Status={outcome.Status} Message={outcome.Message}");

            // 临时诊断：把执行细节打出来，用于确认"到底是谁挡住了越界写入"
            Console.WriteLine("    [诊断] outcome: " + outcome.Message);
            if (outcome.OperationId != 0)
            {
                foreach (var s in box.Restore.ListSteps(outcome.OperationId))
                {
                    Console.WriteLine($"    [诊断] step#{s.Sequence} {s.Action} {s.RelativePath} " +
                                      $"ok={s.Succeeded} skip={s.SkippedDueToConflict} err={s.Error}");
                }
            }
            Console.WriteLine("    [诊断] sentinel 现在 = " + File.ReadAllText(sentinel));
            Console.WriteLine("    [诊断] safe 是否 reparse = " +
                ((File.GetAttributes(box.Abs("safe")) & FileAttributes.ReparsePoint) != 0));
            Check.Equal("NEW", File.ReadAllText(sentinel),
                "链接目标绝不能被写成恢复目标内容 OLD —— 这正是物理边界要挡住的那件事");
            Check.Equal(sentinelHashBefore, Sha256(sentinel), "链接目标文件的哈希必须一字不变");
        });

        yield return new("安全热修", "目标不存在但父目录是 Junction：仍必须拒绝（不能因为文件不存在就放行）", () =>
        {
            using var box = Sandbox.Create("v021-missing-parent-junction");
            var outside = OutsideDir(box);
            Directory.CreateDirectory(outside);

            var link = Path.Combine(box.WatchDir, "link");
            if (!TryCreateJunction(link, outside))
            {
                Check.True(false, "本机无法创建 Junction，本条无法验证 —— 不静默通过");
            }

            var ok = PhysicalPathGuard.TryValidateMutationTarget(
                box.WatchDir, "link/new.txt", out _, out var err);
            Check.False(ok,
                "父目录是 Junction 时，即使目标文件还不存在也必须拒绝；实际放行了：" + (err ?? "<无错误>"));
            Check.NotNull(err, "拒绝时必须给出原因");
        });

        yield return new("安全热修", "普通路径上的不存在目标：必须允许（安全修复不能把正常恢复一起打死）", () =>
        {
            using var box = Sandbox.Create("v021-normal-missing");
            Directory.CreateDirectory(Path.Combine(box.WatchDir, "normal"));

            var ok = PhysicalPathGuard.TryValidateMutationTarget(
                box.WatchDir, "normal/new.txt", out var abs, out var err);
            Check.True(ok, "没有重定向的普通路径必须放行，否则恢复功能整体失效：" + err);
            Check.True(!string.IsNullOrEmpty(abs), "成功时应当给出绝对路径");
        });

        // ═══════════════ P0-2 恢复事务串行 ═══════════════

        yield return new("安全热修", "RestoreEngine 不得再有 _executionBaseline 共享执行状态（结构断言）", () =>
        {
            var field = typeof(RestoreEngine).GetField("_executionBaseline",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Check.Null(field,
                "执行基线必须改成按参数传递；实例字段是并发恢复互相覆盖的根源");
        });

        yield return new("安全热修", "恢复事务闸：已存在另一个恢复事务时必须拒绝，释放后能正常执行", () =>
        {
            using var box = Sandbox.Create("v021-gate");
            box.Protect();
            box.WriteFile("g.txt", "ONE");
            box.WaitForIndex("g.txt");
            box.Flush();
            var snap = box.Snapshot(SnapshotKind.Manual, "v021-gate");
            box.WriteFile("g.txt", "TWO");
            box.WaitForIndex("g.txt");
            box.Flush();

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap.TimestampUtc, new[] { "g.txt" });
            Check.NotNull(plan, "预览应当成功：" + error);

            var opsBefore = box.Restore.ListOperations(box.RootId).Count;
            var snapsBefore = box.SnapshotsRepo.List(box.RootId, 500, null, null).Count;

            // 测试线程先占住命名 mutex，模拟"另一个恢复正在执行"
            using (var held = new Mutex(initiallyOwned: false, @"Local\TimeBack.Restore.Transaction.v1"))
            {
                Check.True(held.WaitOne(0), "测试线程应当能取得命名 mutex");

                try
                {
                    // 必须在**另一个线程**上调用：命名 Mutex 对同一线程是可重入的
                    RestoreOutcome? outcome = null;
                    var worker = new Thread(() =>
                    {
                        outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
                    });
                    worker.Start();
                    Check.True(worker.Join(30000), "被拒绝的恢复应当立刻返回，而不是排队等待");

                    Check.NotNull(outcome, "恢复应当返回结果");
                    Check.True(outcome!.Rejected, "已有事务时必须 rejected，实际：" + outcome.Message);
                    Check.Equal(0L, outcome.OperationId, "被拒绝时不得产生恢复记录");
                    Check.Equal("TWO", File.ReadAllText(box.Abs("g.txt")), "被拒绝时磁盘不得有任何变化");
                    Check.Equal(opsBefore, box.Restore.ListOperations(box.RootId).Count, "恢复记录数不得增加");
                    Check.Equal(snapsBefore, box.SnapshotsRepo.List(box.RootId, 500, null, null).Count,
                        "快照数不得增加（尤其不得悄悄建了安全点）");
                }
                finally
                {
                    held.ReleaseMutex();
                }
            }

            // 事务释放之后，同一份计划应当能正常执行
            var second = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(second.Ok, "事务释放后应能正常执行：" + second.Message);
            Check.Equal("ONE", File.ReadAllText(box.Abs("g.txt")), "应当真的恢复成 ONE");
        });

        yield return new("安全热修", "执行阶段隔离验证：计划里的删目录步骤不得作用到 Junction 目标上", () =>
        {
            using var box = Sandbox.Create("v021-rmdir-boundary");
            var outside = OutsideDir(box);
            Directory.CreateDirectory(outside);

            box.Protect();

            // ① 目标时刻：safe 下没有 sub
            box.WriteFile("safe/keep.txt", "K");
            box.WaitForIndex("safe/keep.txt");
            box.Flush();
            var snap = box.Snapshot(SnapshotKind.Manual, "v021-rmdir-base");

            // ② 现在建一个普通空目录 safe\sub —— 于是"目标没有、现状有"，
            //    计划里会出现「移除空目录 safe\sub」。
            //    这一步不涉及文件内容，不会被"安全点内容覆盖"检查拦住，
            //    因此能真正隔离出**执行阶段**的物理边界是否生效。
            Directory.CreateDirectory(box.Abs("safe/sub"));
            Check.True(box.WaitFor(() =>
            {
                box.Flush();
                return box.Index.Get(box.RootId, "safe/sub") is { IsDeleted: false };
            }, 15000, "空目录 safe/sub 进入索引"), "空目录也应当被记录进索引");

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap.TimestampUtc, includePaths: null);
            Check.NotNull(plan, "预览应当成功：" + error);
            Check.True(plan!.Steps.Any(s => s.Action == RestoreAction.RemoveDirectory),
                "本用例前提：计划里应当包含移除目录的步骤。实际步骤：" +
                string.Join(", ", plan.Steps.Select(s => s.Action + ":" + s.RelativePath)));

            // ③ 预览之后：删掉 safe，换成指向 outside 的 Junction，
            //    并在链接目标里放一个同名空目录 —— 通过 Junction 看过去 safe\sub 仍然"存在"。
            //    此时执行那份计划，就会去删 safe\sub，也就是 outside\sub。
            Directory.CreateDirectory(Path.Combine(outside, "sub"));
            Directory.Delete(box.Abs("safe"), recursive: true);
            if (!TryCreateJunction(box.Abs("safe"), outside))
            {
                Check.True(false, "本机无法创建 Junction，本条无法验证 —— 不静默通过");
            }

            // ④ 执行：绝不能把链接目标里的 sub 删掉
            var outcome = box.Restore.Execute(plan, plan.Fingerprint, allowNewRemovals: true);

            Check.True(Directory.Exists(Path.Combine(outside, "sub")),
                @"恢复绝不能删掉 Junction 目标里的目录 —— outside\sub 必须仍然存在");
            Check.True(outcome.Rejected || !outcome.Ok,
                $"应当拒绝或明确失败。实际 Ok={outcome.Ok} Rejected={outcome.Rejected} Message={outcome.Message}");
        });

        // ═══════════════ P1-1 真实目录删除的 Kind ═══════════════

        yield return new("安全热修", "真实删除目录：Deleted 事件必须记成 Directory，且整个子树都标记为已删除", () =>
        {
            using var box = Sandbox.Create("v021-dir-delete");
            box.Protect();

            box.WriteFile("D/a.txt", "A");
            box.WriteFile("D/sub/b.txt", "B");
            box.WaitForIndex("D/a.txt");
            box.WaitForIndex("D/sub/b.txt");
            box.Flush();

            // 真实的递归删除（走 DirectoryWatcher，不手工构造通知）
            Directory.Delete(box.Abs("D"), recursive: true);

            var gotDeletedEvent = box.WaitFor(
                () => box.EventsOf("D").Any(e => e.Operation == OperationType.Deleted),
                15000, "父目录 D 的删除事件");
            Check.True(gotDeletedEvent, "应当记录到 D 的删除事件");

            var settled = box.WaitFor(() =>
            {
                box.Flush();
                var d = box.Index.Get(box.RootId, "D");
                var a = box.Index.Get(box.RootId, "D/a.txt");
                var s = box.Index.Get(box.RootId, "D/sub");
                var b = box.Index.Get(box.RootId, "D/sub/b.txt");
                return d is { IsDeleted: true }
                    && a is { IsDeleted: true }
                    && s is { IsDeleted: true }
                    && b is { IsDeleted: true };
            }, 15000, "整棵子树都标记为已删除");
            Check.True(settled,
                "删除目录之后，D / D/a.txt / D/sub / D/sub/b.txt 必须全部标记为已删除（否则会留下幽灵子项）：\n" +
                DescribeIndex(box));

            var deleted = box.EventsOf("D").LastOrDefault(e => e.Operation == OperationType.Deleted);
            Check.NotNull(deleted, "应当有 D 的 Deleted 事件");
            Check.Equal(EntryKind.Directory, deleted!.Kind,
                "真实目录删除必须记成 Directory —— 删除已经发生，磁盘上问不出结果，必须用旧索引判定");
        });
    }

    // ───────────────────────────── 辅助 ─────────────────────────────

    /// <summary>与被保护目录同级、**不在其内部**的外部目录。</summary>
    private static string OutsideDir(Sandbox box) =>
        Path.Combine(Path.GetDirectoryName(box.WatchDir)!, "outside");

    /// <summary>用 mklink /J 创建 Junction（不需要管理员权限）。失败返回 false。</summary>
    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000)) return false;
            if (process.ExitCode != 0) return false;

            return Directory.Exists(linkPath)
                && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
               .ToLowerInvariant();

    private static string DescribeIndex(Sandbox box)
    {
        var parts = new List<string>();
        foreach (var rel in new[] { "D", "D/a.txt", "D/sub", "D/sub/b.txt" })
        {
            var entry = box.Index.Get(box.RootId, rel);
            parts.Add(entry is null
                ? $"{rel}=（索引里没有）"
                : $"{rel}=IsDeleted:{entry.IsDeleted},Kind:{entry.Kind}");
        }
        return "  " + string.Join("\n  ", parts);
    }
}
