using System.Diagnostics;
using System.Text;
using LastRegret.Tests.Suites;

namespace LastRegret.Tests;

/// <summary>
/// 测试入口（控制台）。
///
/// 运行方式：
///   pwsh -File code\build\build.ps1 -Target test
/// 或直接：
///   dotnet run --project code\tests\LastRegret.Tests
///
/// 退出码：0 = 全部通过，1 = 有失败。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 先对齐时区：否则本地时间与 UTC 的往返反查会整体错位 8 小时，
        // 让"恢复到某个时间点"这类用例以极具迷惑性的方式失败。
        TimeZoneBootstrap.Apply();

        var filter = args.FirstOrDefault(a => !a.StartsWith('-'));
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        bool listOnly = args.Contains("--list");

        var cases = new List<TestCase>();
        cases.AddRange(CoreSuites.All());
        cases.AddRange(TimelineSuites.All());
        cases.AddRange(RestoreSuites.All());
        cases.AddRange(ManagementSuites.All());
        cases.AddRange(TimezoneSuites.All());
        cases.AddRange(PrelaunchSuites.All());
        cases.AddRange(PrelaunchSuites2.All());
        cases.AddRange(ApplicationSuites.All());
        cases.AddRange(SnapshotRegressionSuites.All());
        cases.AddRange(WatchSplitSuites.All());
        cases.AddRange(AgentCliSuites.All());
        cases.AddRange(ReliabilitySuites.All());
        cases.AddRange(SafetyHotfixSuites.All());


        if (!string.IsNullOrWhiteSpace(filter))
        {
            cases = cases.Where(c => c.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (listOnly)
        {
            foreach (var c in cases) Console.WriteLine(c.FullName);
            Console.WriteLine();
            Console.WriteLine($"共 {cases.Count} 个用例。");
            return 0;
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine(" 最后悔的 Ctrl+Z — 测试套件");
        Console.WriteLine($" 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}   用例数：{cases.Count}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        var results = new List<TestResult>();
        string? currentSuite = null;

        foreach (var c in cases)
        {
            if (c.Suite != currentSuite)
            {
                currentSuite = c.Suite;
                Console.WriteLine();
                Console.WriteLine($"── {currentSuite} ───────────────────────────────");
            }

            var sw = Stopwatch.StartNew();
            try
            {
                c.Body();
                sw.Stop();
                results.Add(new TestResult(c.FullName, true, null, sw.ElapsedMilliseconds));
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  PASS  ");
                Console.ResetColor();
                Console.WriteLine($"{c.Name}  ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                var message = ex is AssertFailedException
                    ? ex.Message
                    : $"{ex.GetType().Name}: {ex.Message}\n{Indent(ex.StackTrace)}";
                results.Add(new TestResult(c.FullName, false, message, sw.ElapsedMilliseconds));
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  FAIL  ");
                Console.ResetColor();
                Console.WriteLine($"{c.Name}  ({sw.ElapsedMilliseconds} ms)");
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine(Indent(message, "        "));
                Console.ResetColor();
            }
        }

        // ── 汇总 ──
        int passed = results.Count(r => r.Passed);
        int failed = results.Count - passed;

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($" 通过 {passed} / {results.Count}    失败 {failed}    总耗时 {results.Sum(r => r.ElapsedMs)} ms");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        if (failed > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("失败的用例：");
            foreach (var r in results.Where(r => !r.Passed)) Console.WriteLine("  · " + r.FullName);
            Console.ResetColor();
        }

        if (verbose)
        {
            Console.WriteLine();
            Console.WriteLine("慢用例（> 1000 ms）：");
            foreach (var r in results.Where(r => r.ElapsedMs > 1000).OrderByDescending(r => r.ElapsedMs))
                Console.WriteLine($"  {r.ElapsedMs,6} ms  {r.FullName}");
        }

        return failed == 0 ? 0 : 1;
    }

    private static string Indent(string? text, string prefix = "        ")
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(prefix).AppendLine(line.TrimEnd('\r'));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>测试集中共用的注册入口。</summary>
public static class TestRegistry
{
    public static readonly List<TestCase> All = new();
}
