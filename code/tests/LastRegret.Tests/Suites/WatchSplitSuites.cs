using System.Reflection;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Engine;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 第 9 刀（WatchService 内部职责整理）的回归与边界测试。
///
/// 两类：
///  ① <b>行为回归</b>——FlushPending 的"取出即处理"、overflow/重新对齐链路、
///     多 root 隔离、自动快照的"不是有事件就建"。这些是拆分时最容易悄悄改坏的地方。
///  ② <b>职责边界</b>——用反射与源码结构检查断言：
///     事件流水线不碰 SQLite/WPF、监听器注册表不写库、调度仍然只有一个 Timer。
///     （§四十 明确允许这种简单检查，不引入架构测试框架。）
/// </summary>
public static class WatchSplitSuites
{
    private static readonly Assembly EngineAssembly = typeof(WatchService).Assembly;

    private static Type RequireType(string fullName)
    {
        var t = EngineAssembly.GetType(fullName);
        Check.NotNull(t, $"应存在类型 {fullName}");
        return t!;
    }

    private static List<string> FieldTypeNames(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
         .Select(f => f.FieldType.FullName ?? f.FieldType.Name)
         .ToList();

    /// <summary>从测试程序集向上找到解决方案根（用于源码结构检查）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LastRegret.sln")))
        {
            dir = dir.Parent;
        }
        Check.NotNull(dir, "应能从测试输出目录向上找到 LastRegret.sln（源码结构检查需要它）");
        return dir!.FullName;
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        Check.True(File.Exists(path), "源码应存在：" + path);
        return File.ReadAllText(path);
    }

    public static IEnumerable<TestCase> All()
    {
        // ── ① FlushPending：收件箱里的通知必须被真正处理 ──────────────────
        yield return new("监听运行时·拆分回归", "FlushPending：写入后的通知都必须落库（不得静默丢掉）", () =>
        {
            using var box = Sandbox.Create("k9-flush");
            box.Protect();

            const int n = 8;
            for (int i = 0; i < n; i++) box.WriteFile($"flush{i}.txt", "内容 " + i);
            Check.True(box.WaitFor(() => (box.Watch.InspectWatchersForTest(box.RootId)?.BatchCount ?? 0) > 0, 6000,
                    "监听器应先收到内核通知"),
                "监听器应收到通知");

            // 立刻落库（不等合并窗口自然到期）—— 这是"退出前 / 恢复前后"依赖的路径
            box.Watch.FlushPending();

            var missing = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var p = $"flush{i}.txt";
                if (!box.WaitFor(() => box.AllEvents().Any(e => e.RelativePath == p), 3000, p)) missing.Add(p);
            }
            Check.Equal(0, missing.Count,
                "FlushPending 之后所有写过的文件都必须有事件，缺失：" + string.Join(", ", missing));
        });

        // ── ① FlushPending 的结构不变量（源码检查，钉住历史缺陷）──────────
        yield return new("监听运行时·拆分回归", "FlushPending：取出的通知必须交给下一步处理（历史缺陷的回归钉）", () =>
        {
            var source = ReadSource(Path.Combine("src", "LastRegret.Engine", "WatchService.cs"));
            var start = source.IndexOf("public void FlushPending()", StringComparison.Ordinal);
            Check.True(start >= 0, "应能找到 FlushPending 方法");
            var body = source.Substring(start, Math.Min(600, source.Length - start));

            Check.Contains(body, "var notifications = DrainInbox();",
                "FlushPending 必须把 DrainInbox() 的返回值接住");
            Check.Contains(body, "ProcessNotifications(notifications);",
                "FlushPending 必须把取出的通知交给 ProcessNotifications —— 早期实现漏了这一步，" +
                "导致\"立刻落库\"会静默丢掉刚收到的通知");
        });

        // ── ① overflow → pending rescan → Rescanner → Resync 快照 ─────────
        yield return new("监听运行时·拆分回归", "重新对齐：RequestRescan 必须真的走 Rescanner 并留下 Resync 快照", () =>
        {
            using var box = Sandbox.Create("k9-rescan");
            box.Protect();
            box.WriteFile("a.txt", "第一版");
            box.WaitForEvent("a.txt", OperationType.Created);
            box.Flush();

            var rescansBefore = box.Watch.Statistics.RescanCount;
            box.Watch.RequestRescan(box.RootId, "拆分回归测试");

            Check.True(box.WaitFor(
                    () => box.SnapshotsRepo.List(box.RootId).Any(s => s.Kind == SnapshotKind.Resync),
                    20000, "重新对齐应留下 Resync 快照"),
                "重新对齐之后必须建立 Resync 快照（否则时间线与快照链会错位）");
            Check.True(box.Watch.Statistics.RescanCount > rescansBefore, "重新对齐次数应计入统计");
        });

        // ── ① 多 root 隔离 ───────────────────────────────────────────────
        yield return new("监听运行时·拆分回归", "多 root：事件与索引都不串根", () =>
        {
            using var box = Sandbox.Create("k9-roots");
            box.Protect();

            var dir2 = Path.Combine(box.Root, "watched2");
            Directory.CreateDirectory(dir2);
            var add = box.Watch.AddRoot(dir2);
            Check.True(add.Ok, "应能加第二个根：" + add.Message);
            var root2 = add.RootId;
            box.Watch.RunBaseline(root2);

            box.WriteFile("only1.txt", "根1");
            box.WaitForEvent("only1.txt", OperationType.Created);
            box.Flush();

            File.WriteAllText(Path.Combine(dir2, "only2.txt"), "根2");
            // 注意：Sandbox.EventsOf 只查第一个根，这里必须按根2 查
            Check.True(box.WaitFor(
                    () => box.Events.Query(new EventQuery { RootId = root2, Limit = 500 })
                              .Any(e => e.RelativePath == "only2.txt"),
                    8000, "根2 的文件应被记录"),
                "根2 的文件应被记录");
            box.Flush();

            var e1 = box.Events.Query(new EventQuery { RootId = box.RootId, Limit = 500 }).Select(e => e.RelativePath).ToList();
            var e2 = box.Events.Query(new EventQuery { RootId = root2, Limit = 500 }).Select(e => e.RelativePath).ToList();

            Check.True(e1.Contains("only1.txt"), "根1 的事件应包含自己的文件");
            Check.False(e1.Contains("only2.txt"), "根1 的事件不得出现根2 的文件");
            Check.True(e2.Contains("only2.txt"), "根2 的事件应包含自己的文件");
            Check.False(e2.Contains("only1.txt"), "根2 的事件不得出现根1 的文件");

            Check.NotNull(box.Index.Get(box.RootId, "only1.txt"), "根1 的索引应有自己的文件");
            Check.Null(box.Index.Get(box.RootId, "only2.txt"), "根1 的索引不得出现根2 的文件");
            Check.NotNull(box.Index.Get(root2, "only2.txt"), "根2 的索引应有自己的文件");
        });

        // ── ① 自动快照：不是"有事件就建" ─────────────────────────────────
        yield return new("监听运行时·拆分回归", "自动快照：不是有事件就马上建（间隔条件必须仍然生效）", () =>
        {
            using var box = Sandbox.Create("k9-autosnap");
            box.Protect();

            // 打开自动快照（沙箱默认是 0 = 关），但间隔仍为默认的正数分钟
            box.Settings.AutoSnapshotIntervalMinutes = 30;
            box.Settings.AutoSnapshotMinEvents = 1;
            box.SettingsRepo.Save(box.Settings);
            box.Watch.ReloadSettings();

            for (int i = 0; i < 5; i++) box.WriteFile($"auto{i}.txt", "内容 " + i);
            box.WaitForEvent("auto4.txt", OperationType.Created);
            box.Flush();

            // 已经超过事件阈值，但最近恢复点刚刚建立 → 不得立刻自动建点
            Thread.Sleep(2000);

            var autos = box.SnapshotsRepo.List(box.RootId).Where(s => s.Kind == SnapshotKind.Auto).ToList();
            Check.Equal(0, autos.Count,
                "事件达到阈值也不该马上建自动恢复点：必须同时满足时间间隔与最近恢复点检查");
        });

        // ── ② 边界：事件流水线 ───────────────────────────────────────────
        yield return new("监听运行时·拆分边界", "事件流水线不接触 SQLite 连接、不引用 WPF", () =>
        {
            var pipeline = RequireType("LastRegret.Engine.WatchEventPipeline");
            var fields = FieldTypeNames(pipeline);

            Check.False(fields.Any(f => f.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)),
                "事件流水线不得直接持有 Sqlite 类型，实际：" + string.Join(", ", fields.Where(f => f.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))));
            Check.False(fields.Any(f => f.StartsWith("System.Windows", StringComparison.Ordinal)),
                "事件流水线不得引用 WPF");

            var refs = EngineAssembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
            foreach (var forbidden in new[] { "PresentationFramework", "PresentationCore", "WindowsBase" })
            {
                Check.False(refs.Any(n => n.Equals(forbidden, StringComparison.OrdinalIgnoreCase)),
                    $"Engine 程序集不得引用 {forbidden}");
            }
        });

        // ── ② 边界：监听器注册表 ─────────────────────────────────────────
        yield return new("监听运行时·拆分边界", "监听器注册表不写数据库、不建快照", () =>
        {
            var registry = RequireType("LastRegret.Engine.WatcherRegistry");
            var fields = FieldTypeNames(registry);

            foreach (var forbidden in new[] { "EventRepository", "FileIndexRepository", "SnapshotService", "IContentStore", "Sqlite" })
            {
                Check.False(fields.Any(f => f.Contains(forbidden, StringComparison.OrdinalIgnoreCase)),
                    $"监听器注册表不得持有 {forbidden}，实际：" + string.Join(", ", fields));
            }

            var source = ReadSource(Path.Combine("src", "LastRegret.Engine", "WatcherRegistry.cs"));
            foreach (var sql in new[] { "NonQuery", "QueryFirst", "INSERT INTO", "UPDATE ", "DELETE FROM" })
            {
                Check.False(source.Contains(sql, StringComparison.Ordinal),
                    $"监听器注册表不得出现数据库写入（发现：{sql}）");
            }
        });

        // ── ② 边界：只有一个调度 Timer、watcher 只在注册表里创建 ──────────
        yield return new("监听运行时·拆分边界", "调度只有一个 Timer，且 WatchService 不再自己创建监听器", () =>
        {
            var timers = typeof(WatchService)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(System.Threading.Timer))
                .ToList();
            Check.Equal(1, timers.Count,
                "调度必须只有一个 Timer（所有数据库写入集中在一个调度线程上）");

            var watchSource = ReadSource(Path.Combine("src", "LastRegret.Engine", "WatchService.cs"));
            Check.False(watchSource.Contains("new DirectoryWatcher(", StringComparison.Ordinal),
                "WatchService 不应再自己创建 DirectoryWatcher（应经 WatcherRegistry）");
            Check.False(watchSource.Contains("new Timer(") && watchSource.Split("new Timer(").Length - 1 > 1,
                "WatchService 不应创建第二个 Timer");

            var registrySource = ReadSource(Path.Combine("src", "LastRegret.Engine", "WatcherRegistry.cs"));
            Check.Contains(registrySource, "new DirectoryWatcher(",
                "监听器的创建应集中在 WatcherRegistry（含实例一致性保护）");
        });
    }
}
