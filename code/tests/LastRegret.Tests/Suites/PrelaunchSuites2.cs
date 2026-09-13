using LastRegret.Core.Model;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 发布前收口（round 19）补的一组回归测试。
///
/// 每一条都对应一个**在真实 Release EXE 上被用户复现过**的缺陷，
/// 而不是"理论上可能出问题"。写在这里是为了让同一类问题再也不能悄悄回来：
/// 状态判断一旦退化成"靠内容数量猜有没有扫描完"，或者监听状态可以被陈旧实例改掉，
/// 这些用例就会红。
/// </summary>
public static class PrelaunchSuites2
{
    public static IEnumerable<TestCase> All()
    {
        // ── 问题 1：空文件夹被"0 个文件"误判成"扫描没完成" ────────────────
        //   症状：同一个文件夹同时显示「● 正在保护」和「还没准备好」，
        //         「现在补扫完」按钮常驻，点了也没用（补扫一百次还是 0 个文件）。
        yield return new("收口·空目录", "空文件夹建立基线后必须被判成「准备好了」", () =>
        {
            using var box = Sandbox.Create("rl19-empty-health");
            box.Protect();

            var health = box.Watch.DescribeRootHealth(box.RootId);
            Check.True(health.HasBaseline,
                "空文件夹的基线快照合法地是 0 个文件 / 0 个目录，仍然算「已经准备好了」。" +
                $"实际 HasBaseline={health.HasBaseline} BaselineFiles={health.BaselineFiles}");
            Check.Equal(0, health.BaselineFiles, "空目录的基线文件数就是 0（这是事实，不是失败）");

            // 反面：不能因为"0 个文件"就把它再列进"需要补扫"的名单
            var missing = box.Watch.FindRootsMissingBaseline();
            Check.False(missing.Any(r => r.Id == box.RootId),
                "空文件夹已经建立过基线，不得再出现在「尚未建立基线」名单里" +
                "（否则启动时会反复提示「上次准备没有做完」）");
        });

        // ── 问题 1 的临界点：基线是 0 个文件，但事后真的能记录变化 ──────────
        yield return new("收口·空目录", "空文件夹保护后，新文件的变化照常被记录", () =>
        {
            using var box = Sandbox.Create("rl19-empty-then-event");
            box.Protect();

            box.WriteFile("later.txt", "空目录之后创建的文件");
            var ev = box.WaitForEvent("later.txt", OperationType.Created, 15000);

            Check.Equal("later.txt", ev.RelativePath, "空目录保护后创建的文件必须被记录");
            Check.True(box.Watch.DescribeRootHealth(box.RootId).HasBaseline,
                "记录到变化之后，状态判断不能被翻转回「还没准备好」");
        });

        // ── 问题 2 / 3：暂停 → 恢复后，监听状态必须回到"真的在监听" ─────────
        //   症状：日志里明明写了"开始监听"，顶部却长期显示「（0 个在监听）」。
        yield return new("收口·监听状态", "暂停→恢复后必须真的回到监听中（空目录）", () =>
        {
            using var box = Sandbox.Create("rl19-resume-empty");
            box.Protect();

            box.Watch.DisableRoot(box.RootId);
            Thread.Sleep(200);
            Check.False(box.Watch.GetState(box.RootId).Watching, "暂停后不该还在监听");

            box.Watch.EnableRoot(box.RootId);

            // 监听是异步挂起的，同时给"被停掉的旧监听器"留出迟到报错的窗口。
            var back = box.WaitFor(() => box.Watch.GetState(box.RootId).Watching, 6000,
                "恢复保护后 Watching 一直是 false");
            Check.True(back, "恢复保护后必须重新处于监听中。实际：" + box.DescribeWatcher());
            Check.True(box.Watch.GetState(box.RootId).Enabled, "恢复保护后 Enabled 必须是 true");

            // 恢复之后变化照样记得到（状态没回来，这里是记不到的）
            box.WriteFile("after-resume.txt", "恢复之后");
            box.WaitForEvent("after-resume.txt", OperationType.Created, 15000);
        });

        yield return new("收口·监听状态", "暂停→恢复后必须真的回到监听中（非空目录）", () =>
        {
            using var box = Sandbox.Create("rl19-resume-full");
            box.WriteFile("a.txt", "A");
            box.Protect();

            box.Watch.DisableRoot(box.RootId);
            Thread.Sleep(200);
            Check.False(box.Watch.GetState(box.RootId).Watching, "暂停后不该还在监听");

            box.Watch.EnableRoot(box.RootId);
            Check.True(box.WaitFor(() => box.Watch.GetState(box.RootId).Watching, 6000,
                "恢复保护后 Watching 一直是 false"), "恢复保护后必须重新处于监听中");

            box.WriteFile("b.txt", "B");
            box.WaitForEvent("b.txt", OperationType.Created, 15000);
        });

        // ── 问题 2 的根因：陈旧监听器的迟到错误绝不能改掉新监听器的状态 ────
        //   这条用例走的是"停掉监听 → 立刻恢复"这个真实顺序，并在恢复后反复确认
        //   监听状态没有被任何迟到的错误报告打回去。
        yield return new("收口·监听状态", "反复暂停/恢复不会把监听状态弄丢", () =>
        {
            using var box = Sandbox.Create("rl19-resume-loop");
            box.WriteFile("a.txt", "A");
            box.Protect();

            for (var i = 0; i < 3; i++)
            {
                box.Watch.DisableRoot(box.RootId);
                Thread.Sleep(100);
                box.Watch.EnableRoot(box.RootId);
                Check.True(box.WaitFor(() => box.Watch.GetState(box.RootId).Watching, 6000,
                    $"第 {i + 1} 轮恢复后 Watching 没回来"),
                    $"第 {i + 1} 轮暂停/恢复后必须仍在监听。实际：{box.DescribeWatcher()}");
            }

            // 给迟到的错误报告足够时间"打冷枪"，状态必须还是稳定的
            Thread.Sleep(1500);
            Check.True(box.Watch.GetState(box.RootId).Watching,
                "反复暂停/恢复之后，监听状态必须稳定为「在监听」（不能被陈旧实例的迟到报错改掉）");

            box.WriteFile("after-loop.txt", "循环之后");
            box.WaitForEvent("after-loop.txt", OperationType.Created, 15000);
        });
    }
}
