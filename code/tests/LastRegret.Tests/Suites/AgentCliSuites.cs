using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LastRegret.Core.Model;
using LastRegret.Engine;
using LastRegret.Runtime;

namespace LastRegret.Tests.Suites;

/// <summary>
/// 第 10 刀：Agent CLI（<c>LastRegret.Agent.exe</c>）的**真实进程黑盒测试**。
///
/// 这里不调用任何内部类：每个用例都真的启动 CLI 进程、通过 argv 与 stdin 传请求、
/// 解析 stdout 的 JSON 并核对退出码 —— 这样才能证明"外部程序"真的能用它。
///
/// 关于数据目录：CLI 用的是组合根 <see cref="AppRuntime"/> 解析出来的真实数据目录
/// （与 GUI 完全相同的那一个）。测试为了准备状态，会先 <see cref="AppRuntime.Create"/>
/// 一次拿到**同一个**目录，在里面临时登记一个受保护目录，跑完在 finally 里连历史一起删掉。
/// 这也是不改动任何产品代码就能让真实 CLI 看到准备数据的唯一办法。
/// </summary>
public static class AgentCliSuites
{
    // ───────────────────────── CLI 进程调用 ─────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LastRegret.sln")))
        {
            dir = dir.Parent;
        }
        Check.NotNull(dir, "应能从测试输出目录向上找到 LastRegret.sln");
        return dir!.FullName;
    }

    /// <summary>
    /// CLI 可执行文件的运行位置。
    ///
    /// 为什么要把产物复制到测试输出目录再运行：CLI 与测试都通过组合根解析数据目录，
    /// 而在"用户目录不可写"的受限环境里会退回**程序目录兜底**（= 各自 AppContext.BaseDirectory
    /// 下的 LastRegretData）。两个进程的 BaseDirectory 不同就会各用一份数据，
    /// 测试准备的状态 CLI 自然看不到。复制到同一目录后，两者解析结果一致 ——
    /// 在正常环境里两者本来就都用"用户自定义数据目录"，复制只是无害的多余动作。
    /// </summary>
    private static string AgentExe()
    {
        var testDir = AppContext.BaseDirectory;
        var config = new DirectoryInfo(testDir).Parent?.Name ?? "Debug";
        var srcDir = Path.Combine(RepoRoot(), "src", "LastRegret.Agent", "bin", config, "net8.0");
        Check.True(File.Exists(Path.Combine(srcDir, "LastRegret.Agent.exe")), "CLI 应已构建：" + srcDir);

        foreach (var file in Directory.GetFiles(srcDir))
        {
            var target = Path.Combine(testDir, Path.GetFileName(file));
            try { File.Copy(file, target, overwrite: true); } catch (IOException) { /* 正在使用则复用已有副本 */ }
        }

        var exe = Path.Combine(testDir, "LastRegret.Agent.exe");
        Check.True(File.Exists(exe), "CLI 应可运行：" + exe);
        return exe;
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr, JsonDocument Json);

    private static CliResult RunCli(string command, string? stdinJson = null)
    {
        var psi = new ProcessStartInfo(AgentExe())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(command);

        using var p = Process.Start(psi)!;
        if (!string.IsNullOrEmpty(stdinJson)) p.StandardInput.Write(stdinJson);
        p.StandardInput.Close();                       // 一定要关：CLI 会读到 EOF 才继续

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(); } catch { }
            throw new AssertFailedException($"CLI 未在 60 秒内退出（命令 {command}）");
        }

        // stdout 必须是**恰好一个** JSON 文档：多一个字符都会在这里抛
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new AssertFailedException(
                $"stdout 不是合法的单个 JSON（命令 {command}）：{ex.Message}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
        return new CliResult(p.ExitCode, stdout, stderr, json);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>协议契约：每个响应都必须带这四项，且 status 只能是四个取值之一。</summary>
    private static JsonElement CheckEnvelope(CliResult r, string expectedCapability, string expectedStatus)
    {
        var root = r.Json.RootElement;
        Check.Equal("timeback-agent", Str(root, "protocol"), "protocol 必须固定");
        Check.Equal("1.0", Str(root, "version"), "协议版本必须仍是 1.0（本刀不是协议升级）");
        Check.Equal(expectedCapability, Str(root, "capability"), "capability 必须与命令对应");
        Check.True(root.TryGetProperty("ok", out _), "每个响应都必须有 ok");

        var status = Str(root, "status");
        Check.True(status is "success" or "no_change" or "rejected" or "failed",
            "status 只能是 success/no_change/rejected/failed，实际：" + status);
        Check.Equal(expectedStatus, status, "status 不符合预期");

        // ok 与 status 必须一致：失败/拒绝不能是 ok=true
        var ok = Bool(root, "ok");
        Check.Equal(expectedStatus is "success" or "no_change", ok, "ok 必须与 status 一致");
        return root;
    }

    // ───────────────────────── 测试数据准备 ─────────────────────────

    private sealed class Prepared : IDisposable
    {
        public required string DataDirectory { get; init; }
        public required string WatchDir { get; init; }
        public required long RootId { get; init; }
        public required DateTime BaselineUtc { get; init; }
        public required string FileRelativePath { get; init; }
        public required string ContentBefore { get; init; }
        public required string ContentAfter { get; init; }

        public string FilePath => Path.Combine(WatchDir, FileRelativePath);
        public string DiskContent() => File.ReadAllText(FilePath);

        public void Dispose()
        {
            // 清理：把测试登记的受保护目录连历史一起删掉；数据目录本身若在程序目录兜底里也一并清
            try
            {
                using var runtime = AppRuntime.Create();
                var root = runtime.Roots.FindByPath(WatchDir);
                if (root is not null) runtime.Watch.RemoveRoot(root.Id, deleteHistory: true);
            }
            catch (Exception) { /* 清理失败不应掩盖测试结论 */ }

            try { if (Directory.Exists(WatchDir)) Directory.Delete(WatchDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 准备：登记一个受保护目录 → 建立基线（快照 T0）→ 改一个文件 → 冲刷落库。
    /// 完成后：T0 时刻磁盘上是「旧内容」，现在是「新内容」，正好可以用来验证恢复与撤销。
    /// </summary>
    private static Prepared PrepareState(string name)
    {
        var watchDir = Path.Combine(Path.GetTempPath(), "lastregret-cli", $"{name}-{Guid.NewGuid():N}"[..(name.Length + 12)]);
        Directory.CreateDirectory(watchDir);

        const string rel = "cli.txt";
        const string before = "CLI 测试：旧内容";
        const string after = "CLI 测试：新内容";
        File.WriteAllText(Path.Combine(watchDir, rel), before);

        string dataDir;
        long rootId;
        DateTime baselineUtc;

        using (var runtime = AppRuntime.Create())
        {
            dataDir = runtime.DataDirectory;

            var (ok, id, message) = runtime.Watch.RegisterRoot(watchDir);
            Check.True(ok, "应能登记受保护目录：" + message);
            rootId = id;

            runtime.Watch.RunBaseline(rootId);                       // 基线：索引 + 基线快照（T0）
            var baseline = runtime.SnapshotService.GetLatest(rootId);
            Check.NotNull(baseline, "基线快照应已建立");
            baselineUtc = baseline!.TimestampUtc;

            // 制造一次真实变化，并强制落库（不等合并窗口）
            File.WriteAllText(Path.Combine(watchDir, rel), after);
            Thread.Sleep(400);
            runtime.Watch.FlushPending();
            Thread.Sleep(200);
            runtime.Watch.FlushPending();
        }

        Check.Equal(after, File.ReadAllText(Path.Combine(watchDir, rel)),
            "准备完成时磁盘上应是新内容（旧内容只存在于基线快照里）");

        return new Prepared
        {
            DataDirectory = dataDir,
            WatchDir = watchDir,
            RootId = rootId,
            BaselineUtc = baselineUtc,
            FileRelativePath = rel,
            ContentBefore = before,
            ContentAfter = after,
        };
    }

    private static string Iso(DateTime utc) => utc.ToString("o");

    // ───────────────────────── 用例 ─────────────────────────

    public static IEnumerable<TestCase> All()
    {
        // ── 契约：capabilities（不需要 Runtime，立即返回）────────────────
        yield return new("Agent CLI·契约", "capabilities：协议头与 6 项能力必须与 agent-interface.json 完全一致", () =>
        {
            var r = RunCli("capabilities");
            var root = CheckEnvelope(r, "capabilities", "success");
            Check.Equal(0, r.ExitCode, "成功应返回退出码 0");
            Check.Equal(string.Empty, r.Stderr.Trim(), "成功路径 stderr 应为空");

            // 协议声明在工程根目录（code\ 的上一级）
            var interfacePath = Path.GetFullPath(Path.Combine(RepoRoot(), "..", "agent-interface.json"));
            Check.True(File.Exists(interfacePath), "应找到 agent-interface.json：" + interfacePath);
            var declared = JsonDocument.Parse(File.ReadAllText(interfacePath));
            var declaredIds = declared.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var reported = root.GetProperty("data").GetProperty("capabilities").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Check.Equal(string.Join(",", declaredIds), string.Join(",", reported),
                "CLI 报的能力必须与 agent-interface.json 声明**逐项一致**（声明 = 实现）");
            Check.Equal(6, reported.Count, "能力数量必须仍是 6（本刀不新增 capability）");

            // readonly 也必须一致
            foreach (var c in declared.RootElement.GetProperty("capabilities").EnumerateArray())
            {
                var id = c.GetProperty("id").GetString()!;
                var ro = c.GetProperty("readonly").GetBoolean();
                var mine = root.GetProperty("data").GetProperty("capabilities").EnumerateArray()
                    .First(x => x.GetProperty("id").GetString() == id);
                Check.Equal(ro, mine.GetProperty("readonly").GetBoolean(), $"{id} 的 readonly 必须一致");
            }
        });

        // ── 错误与拒绝路径 ─────────────────────────────────────────────
        yield return new("Agent CLI·契约", "错误路径：未知命令 / 缺命令 / 非法 JSON 都不能崩溃", () =>
        {
            var unknown = RunCli("不存在这样的命令");
            CheckEnvelope(unknown, "不存在这样的命令", "failed");
            Check.Equal(1, unknown.ExitCode, "命令错误应返回退出码 1");

            var badJson = RunCli("timeline", "{ 这不是 JSON");
            CheckEnvelope(badJson, "timeline.read", "failed");
            Check.Equal(1, badJson.ExitCode, "请求不合法应返回退出码 1");
            Check.True(badJson.Stderr.Length > 0, "诊断信息应写到 stderr");
        });

        yield return new("Agent CLI·契约", "restore 缺少 fingerprint → rejected（绝不自作主张生成计划再执行）", () =>
        {
            using var prepared = PrepareState("cli-nofp");
            var r = RunCli("restore", JsonSerializer.Serialize(new
            {
                rootId = prepared.RootId,
                atUtc = Iso(prepared.BaselineUtc),
            }));

            var root = CheckEnvelope(r, "restore.execute", "rejected");
            Check.Equal(2, r.ExitCode, "业务拒绝应返回退出码 2");
            Check.Equal("fingerprint_required", Str(root.GetProperty("error"), "code"), "错误码应是 fingerprint_required");
            Check.Equal(prepared.ContentAfter, prepared.DiskContent(), "被拒绝的请求绝不能让磁盘发生变化");
        });

        yield return new("Agent CLI·契约", "未知 rootId → failed(root_not_found)，而不是静默返回空结果", () =>
        {
            var r = RunCli("changes", JsonSerializer.Serialize(new { rootId = 999_999, limit = 5 }));
            var root = CheckEnvelope(r, "changes.read", "failed");
            Check.Equal("root_not_found", Str(root.GetProperty("error"), "code"), "错误码应是 root_not_found");
        });

        // ── 完整链（preview → restore → undo）──────────────────────────
        yield return new("Agent CLI·完整链", "保护范围 / 历史 / 变化 / 预览 / 恢复 / 撤销 全链路走真实引擎", () =>
        {
            using var prepared = PrepareState("cli-chain");
            var rootId = prepared.RootId;

            // ① protected-folders：能看到刚登记的目录
            var folders = RunCli("protected-folders");
            CheckEnvelope(folders, "protected-folders.read", "success");
            var listed = folders.Json.RootElement.GetProperty("data").GetProperty("folders").EnumerateArray()
                .FirstOrDefault(f => f.TryGetProperty("rootId", out var id) && id.GetInt64() == rootId);
            Check.True(listed.ValueKind == JsonValueKind.Object, "应能看到刚登记的受保护目录");
            Check.True(Bool(listed, "watching"), "该目录应处于监听中");
            Check.True(Bool(listed, "hasBaseline"), "该目录应已建立基线");

            // ② timeline：能看到基线恢复点
            var timeline = RunCli("timeline", JsonSerializer.Serialize(new { rootId, limit = 20 }));
            CheckEnvelope(timeline, "timeline.read", "success");
            Check.True(timeline.Json.RootElement.GetProperty("data").GetProperty("points").GetArrayLength() >= 1,
                "应至少有一个恢复点");

            // ③ changes：能看到刚才那次修改
            var changes = RunCli("changes", JsonSerializer.Serialize(new { rootId, limit = 50 }));
            CheckEnvelope(changes, "changes.read", "success");
            var paths = changes.Json.RootElement.GetProperty("data").GetProperty("changes").EnumerateArray()
                .Select(c => c.TryGetProperty("path", out var p) ? p.GetString() : null).ToList();
            Check.True(paths.Contains(prepared.FileRelativePath),
                "变化记录里应能看到被修改的文件，实际：" + string.Join(",", paths));

            // ④ preview-restore：只读，磁盘必须原样
            var preview = RunCli("preview-restore", JsonSerializer.Serialize(new
            {
                rootId,
                atUtc = Iso(prepared.BaselineUtc),
            }));
            var previewRoot = CheckEnvelope(preview, "restore.preview", "success");
            Check.Equal(prepared.ContentAfter, prepared.DiskContent(), "预览绝不能改动磁盘");
            var plan = previewRoot.GetProperty("data");
            Check.True(plan.GetProperty("hasEffect").GetBoolean(), "预览应显示确实有内容需要恢复");
            Check.True(plan.GetProperty("toRestore").GetInt32() >= 1, "应至少有一个文件需要恢复");
            var fingerprint = plan.GetProperty("fingerprint").GetString();
            Check.True(!string.IsNullOrWhiteSpace(fingerprint), "预览必须给出指纹");

            // ⑤ restore：错误指纹 → rejected，且磁盘不变
            var rejected = RunCli("restore", JsonSerializer.Serialize(new
            {
                rootId,
                atUtc = Iso(prepared.BaselineUtc),
                fingerprint = "这不是真的指纹",
            }));
            var rejectedRoot = CheckEnvelope(rejected, "restore.execute", "rejected");
            Check.Equal("fingerprint_mismatch", Str(rejectedRoot.GetProperty("error"), "code"),
                "错误指纹必须被拒绝，且错误码是 fingerprint_mismatch");
            Check.Equal(2, rejected.ExitCode, "业务拒绝应返回退出码 2");
            Check.Equal(prepared.ContentAfter, prepared.DiskContent(), "被拒绝的恢复绝不能让磁盘发生变化");

            // ⑥ restore：正确指纹 → 真的把文件恢复成旧内容
            var restore = RunCli("restore", JsonSerializer.Serialize(new
            {
                rootId,
                atUtc = Iso(prepared.BaselineUtc),
                fingerprint,
                allowNewRemovals = false,
            }));
            var restoreRoot = CheckEnvelope(restore, "restore.execute", "success");
            Check.Equal(0, restore.ExitCode, "成功应返回退出码 0");
            Check.Equal(prepared.ContentBefore, prepared.DiskContent(), "恢复后磁盘上应是旧内容");
            var restoreData = restoreRoot.GetProperty("data");
            Check.True(restoreData.GetProperty("executionOk").GetBoolean(), "引擎应报告执行成功");
            Check.True(restoreData.GetProperty("operationId").GetInt64() > 0, "应产生恢复操作 Id");
            Check.True(restoreData.GetProperty("canUndo").GetBoolean(), "成功恢复之后应可撤销");
            var operationId = restoreData.GetProperty("operationId").GetInt64();

            // ⑦ 再预览一次：磁盘此刻已等于目标时刻 → 必须是 no_change（既不是成功，也不是失败）
            var noChange = RunCli("preview-restore", JsonSerializer.Serialize(new
            {
                rootId,
                atUtc = Iso(prepared.BaselineUtc),
            }));
            var noChangeRoot = CheckEnvelope(noChange, "restore.preview", "no_change");
            Check.Equal(0, noChange.ExitCode, "无变化也是正常结束（退出码 0）");
            Check.False(noChangeRoot.GetProperty("data").GetProperty("hasEffect").GetBoolean(),
                "恢复之后已经没有需要恢复的内容");
            Check.Equal(prepared.ContentBefore, prepared.DiskContent(), "无变化的预览同样不得改动磁盘");

            // ⑧ undo（不带 fingerprint）：只预览，返回指纹；磁盘仍应是恢复后的内容
            var undoPreview = RunCli("undo", JsonSerializer.Serialize(new { rootId, operationId }));
            var undoPreviewRoot = CheckEnvelope(undoPreview, "restore.undo", "success");
            Check.Equal(prepared.ContentBefore, prepared.DiskContent(), "撤销预览绝不能改动磁盘");
            var undoPlan = undoPreviewRoot.GetProperty("data");
            var undoFingerprint = undoPlan.GetProperty("fingerprint").GetString();
            Check.True(!string.IsNullOrWhiteSpace(undoFingerprint), "撤销预览必须给出指纹");

            // ⑨ undo（带指纹）：真的撤销，磁盘回到恢复之前
            var undo = RunCli("undo", JsonSerializer.Serialize(new
            {
                rootId,
                operationId,
                fingerprint = undoFingerprint,
                allowNewRemovals = false,
            }));
            var undoRoot = CheckEnvelope(undo, "restore.undo", "success");
            Check.Equal(0, undo.ExitCode, "成功应返回退出码 0");
            Check.Equal(prepared.ContentAfter, prepared.DiskContent(), "撤销后磁盘应回到恢复之前的内容");
            Check.True(undoRoot.GetProperty("data").GetProperty("executionOk").GetBoolean(), "引擎应报告撤销成功");
        });

        // ── undo 没有可撤销对象 → failed（不是崩溃、也不是假装成功）──────
        yield return new("Agent CLI·契约", "undo：没有可撤销的恢复操作 → failed(undo_unavailable)", () =>
        {
            using var prepared = PrepareState("cli-noop");
            var r = RunCli("undo", JsonSerializer.Serialize(new
            {
                rootId = prepared.RootId,
                operationId = 999_999,
            }));
            var root = CheckEnvelope(r, "restore.undo", "failed");
            Check.Equal("undo_unavailable", Str(root.GetProperty("error"), "code"), "错误码应是 undo_unavailable");
        });

        // ── 边界：CLI 只引用 Runtime（不碰 Engine/Data/Windows/WPF）───────
        yield return new("Agent CLI·边界", "CLI 项目只引用 Runtime，且不含任何网络/端口代码", () =>
        {
            var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LastRegret.Agent", "LastRegret.Agent.csproj"));
            var refs = csproj.Split('\n')
                .Where(l => l.Contains("ProjectReference", StringComparison.Ordinal))
                .Select(l => l.Trim()).ToList();
            Check.Equal(1, refs.Count, "只允许一个 ProjectReference，实际：" + string.Join(" | ", refs));
            Check.Contains(refs[0], "LastRegret.Runtime", "唯一引用必须是 Runtime（组合根）");
            foreach (var forbidden in new[] { "LastRegret.Data", "LastRegret.Windows", "LastRegret.Engine", "LastRegret.App" })
            {
                Check.False(refs[0].Contains(forbidden, StringComparison.Ordinal),
                    $"不得直接引用 {forbidden}");
            }

            var sources = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "LastRegret.Agent"), "*.cs")
                .Select(File.ReadAllText).ToList();
            foreach (var forbidden in new[] { "HttpListener", "Kestrel", "TcpListener", "NamedPipeServer", "Socket", "WebSocket", "System.Data", "Sqlite" })
            {
                Check.False(sources.Any(s => s.Contains(forbidden, StringComparison.Ordinal)),
                    $"CLI 不得出现 {forbidden}（不开放网络、不直接碰数据库）");
            }

            // 危险开关一个都不许有（只扫代码，注释里出现这些词是用来声明"没有它"的）
            var codeOnly = sources
                .SelectMany(s => s.Split('\n'))
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Select(l => l.Trim())
                .ToList();
            foreach (var forbidden in new[] { "--force", "skip-preview", "ignore-fingerprint", "disable-safety" })
            {
                Check.False(codeOnly.Any(l => l.Contains(forbidden, StringComparison.OrdinalIgnoreCase)),
                    $"不得提供危险开关 {forbidden}");
            }
        });
    }
}
