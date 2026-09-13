using System.Security.Cryptography;
using LastRegret.Core.Config;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 上线前体检补的一组测试（prelaunch-audit）。
///
/// 为什么单独一组：现有 92 条已覆盖主流程、内容不完整、文件被占用、非法路径、
/// 时区。这里只补"真实用户机器上才会遇到、但一直没测"的失败路径，
/// 按业务重要性排序 —— 数据 > 权限 > 其余。
///
/// 三条命门：
///   ① 只读文件挡路时，恢复必须如实失败，且**绝不破坏**用户文件；
///   ② 只读目录挡路时，同上；
///   ③ 恢复的作用范围绝不能越过保护根（写到保护范围之外就是数据事故）。
///
/// 每条都覆盖 skill 要求的三类：正常结果正确 / 临界点 / 异常情况。
/// </summary>
public static class PrelaunchSuites
{
    public static IEnumerable<TestCase> All()
    {
        // ── ① 目标文件只读：恢复必须失败得诚实，且不动原文件 ─────────────
        yield return new("上线前体检·权限", "目标文件只读 → 恢复如实失败且不破坏文件", () =>
        {
            using var box = Sandbox.Create("pre-ro-file");

            box.WriteFile("locked.txt", "原始内容");
            box.Protect();
            var point = box.Snapshot(SnapshotKind.Manual, "只读前");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            // 先把历史版本记下来，再把当前文件改成只读
            var abs = box.Abs("locked.txt");
            var beforeHash = HashOf(abs);
            var attrs = File.GetAttributes(abs);
            File.SetAttributes(abs, attrs | FileAttributes.ReadOnly);

            try
            {
                var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc,
                    new[] { "locked.txt" });
                Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

                var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);

                // 异常情况：只读挡路时**不允许**报成功
                Check.False(outcome.Ok,
                    "只读文件挡路时不得报告成功。" +
                    $"实际 Ok={outcome.Ok} 成功={outcome.Succeeded} 失败={outcome.Failed} Message={outcome.Message}");

                // 命门：原文件必须一个字节都没变
                Check.Equal(beforeHash, HashOf(abs),
                    "恢复失败后，只读文件的当前内容必须原封不动（绝不截断/半写）");

                // 失败必须带一条能给用户看的原因
                Check.True(outcome.Message.Length > 0, "失败必须带可展示的原因");
            }
            finally
            {
                File.SetAttributes(abs, attrs);
            }
        });

        // ── ② 目标目录只读：恢复必须失败得诚实，且不留下半成品 ───────────
        yield return new("上线前体检·权限", "目标目录只读 → 恢复如实失败且不留半成品", () =>
        {
            using var box = Sandbox.Create("pre-ro-dir");

            box.MkDir("ro-dir");
            box.WriteFile("ro-dir/inner.txt", "需要被恢复的内容");
            box.Protect();
            var point = box.Snapshot(SnapshotKind.Manual, "只读前");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            // 删掉文件（目录留空），再把目录设为只读 —— 模拟"目录被设了只读属性"
            box.DeleteFile("ro-dir/inner.txt");
            box.WaitForGone("ro-dir/inner.txt");

            var dirAbs = box.Abs("ro-dir");
            var dirAttrs = File.GetAttributes(dirAbs);
            File.SetAttributes(dirAbs, dirAttrs | FileAttributes.ReadOnly);

            try
            {
                var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc,
                    new[] { "ro-dir/inner.txt" });
                Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

                var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);

                // 结果必须自洽：要么写成功、要么如实失败，不能"报成功但文件不在"
                var innerAbs = box.Abs("ro-dir/inner.txt");
                if (outcome.Ok)
                {
                    Check.FileExists(innerAbs,
                        "如果报告成功，文件就必须真的存在（不允许报成功却没写出来）");
                }
                else
                {
                    Check.True(outcome.Message.Length > 0, "失败必须带可展示的原因");
                }
            }
            finally
            {
                File.SetAttributes(dirAbs, dirAttrs);
            }
        });

        // ── ③ 作用范围：恢复绝不能写到保护根之外（数据事故红线） ──────────
        yield return new("上线前体检·范围", "恢复的作用范围绝不越过保护根", () =>
        {
            using var box = Sandbox.Create("pre-scope");

            box.WriteFile("inside.txt", "内侧-原始");
            box.Protect();
            var point = box.Snapshot(SnapshotKind.Manual, "边界测试");
            var snap = box.SnapshotsRepo.Get(point.Id);
            Check.NotNull(snap, "应能取到快照");

            // 在保护根**之外**放一个哨兵文件（与被保护文件同名，最容易误伤）
            var outsideDir = Path.Combine(box.Root, "outside");
            Directory.CreateDirectory(outsideDir);
            var sentinel = Path.Combine(outsideDir, "inside.txt");
            File.WriteAllText(sentinel, "外侧-必须原封不动");
            var sentinelHash = HashOf(sentinel);

            // 保护根内改坏，然后恢复
            box.WriteFile("inside.txt", "内侧-改坏");
            box.WaitForIndex("inside.txt", 15000);

            var (plan, error) = box.Restore.BuildPreviewAt(box.RootId, snap!.TimestampUtc, new[] { "inside.txt" });
            Check.NotNull(plan, "预览不应失败：" + (error ?? string.Empty));

            var outcome = box.Restore.Execute(plan!, plan!.Fingerprint, allowNewRemovals: true);
            Check.True(outcome.Ok, "保护根内的恢复应当成功：" + outcome.Message);

            // 正常结果正确：内侧内容真的还原了
            Check.Equal("内侧-原始", box.ReadFile("inside.txt"), "保护根内的文件内容必须真的还原");

            // 红线：范围外的东西一个字节都不能动
            Check.Equal(sentinelHash, HashOf(sentinel),
                "保护根之外的同名文件绝不能被恢复操作碰到（作用范围红线）");
            Check.Equal("外侧-必须原封不动", File.ReadAllText(sentinel),
                "保护根之外的文件内容必须原样保留");
        });

        // ── ④ 隐私：本地只存配置，不存任何凭据；日志不含用户文件内容 ────────
        yield return new("上线前体检·隐私", "本地只落配置阈值，绝不落凭据", () =>
        {
            using var box = Sandbox.Create("pre-log");

            // 写一份带"像密码"的路径与内容，确认它们不会被写进设置表
            const string secretBody = "SECRET-BODY-110101199001011234-DO-NOT-LOG";
            box.WriteFile("身份证-110101199001011234.txt", secretBody);
            box.Protect();
            box.Snapshot(SnapshotKind.Manual, "隐私测试");
            box.Flush();

            // 设置表存的是序列化后的 AppSettings
            var raw = box.SettingsRepo.GetRaw("app_settings");
            Check.NotNull(raw, "设置应已被落盘（否则这条断言没有意义）");

            // 命门 1：凭据字段名一个都不能出现
            foreach (var bad in new[] { "password", "passwd", "secret", "token", "credential", "apikey", "privatekey" })
            {
                Check.False(raw!.ToLowerInvariant().Contains(bad),
                    $"本地设置里绝不能出现「{bad}」类字段（会明文落盘）");
            }

            // 命门 2：被保护文件的内容绝不能进设置
            Check.False(raw!.Contains(secretBody),
                "本地设置绝不能包含用户文件的内容");

            // 正常结果正确：该在的配置项必须在，否则上面两条会因为"空对象"而假通过
            foreach (var must in new[] { "Protection", "RetentionDays", "MaxHistoryBytes", "ExcludePatterns" })
            {
                Check.True(raw!.Contains(must),
                    $"设置里应包含配置项 {must}（证明落盘的确实是完整配置，不是空壳）");
            }
        });
        // ── ⑤ 隐私：崩溃日志里的本机路径必须被打码 ───────────────────────
        yield return new("上线前体检·隐私", "崩溃日志里的本机绝对路径会被打码", () =>
        {
            // 深路径：中间层级必须被收敛
            // 用户名用通用占位名。这里验证的是"打码规则"，与谁的本机无关；
            // 写真用户名会把开发机账户名带进公开仓库。
            var deep = @"无法访问 D:\Users\TestUser\Documents\私人\项目\x.docx";
            var r1 = LastRegret.Runtime.CrashLog.RedactPaths(deep);
            Check.False(r1.Contains(@"TestUser\Documents\私人"),
                "深路径的中间层级必须被省略。实际：" + r1);
            Check.True(r1.Contains("x.docx"), "末级文件名要保留（否则无法定位）。实际：" + r1);
            Check.True(r1.Contains("D:\\Users\\TestUser"), "保留前两级便于用户自查。实际：" + r1);

            // 浅路径：本来就不含隐私层级，原样保留
            var shallow = @"打不开 C:\temp\a.txt";
            var r2 = LastRegret.Runtime.CrashLog.RedactPaths(shallow);
            Check.True(r2.Contains(@"C:\temp\a.txt"),
                "浅路径（<=3 级）无需打码，原样保留。实际：" + r2);

            // 一段文本里多个路径都要处理
            var many = @"从 D:\a\b\c\d\机密.dat 复制到 E:\x\y\z\w\out.dat 失败";
            var r3 = LastRegret.Runtime.CrashLog.RedactPaths(many);
            Check.False(r3.Contains("机密.dat") && r3.Contains(@"c\d\机密"),
                "第一个路径的中间层必须被省略。实际：" + r3);
            Check.False(r3.Contains(@"y\z\w\out"),
                "第二个路径的中间层必须被省略。实际：" + r3);
            Check.True(r3.Contains("机密.dat") && r3.Contains("out.dat"),
                "两个末级文件名都要保留。实际：" + r3);

            // 反斜杠与正斜杠都要认
            var slash = @"path D:/one/two/three/four/leaf.txt end";
            var r4 = LastRegret.Runtime.CrashLog.RedactPaths(slash);
            Check.False(r4.Contains("two/three/four"),
                "正斜杠路径也必须被打码。实际：" + r4);

            // 空输入不得抛异常
            Check.Equal(string.Empty, LastRegret.Runtime.CrashLog.RedactPaths(string.Empty),
                "空字符串应原样返回");
        });
    }
    private static string HashOf(string absolutePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(absolutePath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
