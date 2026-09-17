using System.Globalization;
using LastRegret.Core.Abstractions;
using LastRegret.Core.Model;
using LastRegret.Core.Restore;
using LastRegret.Engine;
using LastRegret.Runtime;

namespace LastRegret.Agent;

/// <summary>
/// 命令 → capability → <see cref="TimeBackApplication"/> 的映射。
///
/// 这一层只做四件事：解析、校验、映射、调用。它<b>不</b>计算恢复计划、
/// <b>不</b>碰 SQLite、<b>不</b>直接读写受保护目录 —— 恢复计划一律由
/// <c>TimeBackApplication</c>（进而 <c>RestoreEngine</c>）产出。
///
/// 安全链在这里被结构性保留：
///   · 预览必须零副作用（只读 capability）；
///   · 执行必须携带**同一次预览**得到的指纹，指纹不符 → rejected（绝不"重新生成一份再执行"）；
///   · 没有任何 force / skip-preview / ignore-fingerprint 之类的开关；
///   · allowNewRemovals 缺省 false，不替调用方放宽删除条件。
/// </summary>
internal static class AgentCommands
{
    public const string Capabilities = "capabilities";
    public const string ProtectedFolders = "protected-folders";
    public const string Timeline = "timeline";
    public const string Changes = "changes";
    public const string PreviewRestore = "preview-restore";
    public const string Restore = "restore";
    public const string Undo = "undo";

    public static readonly string[] All =
    {
        Capabilities, ProtectedFolders, Timeline, Changes, PreviewRestore, Restore, Undo,
    };

    /// <summary>命令对应的 capability。能力 id 本身也接受（Agent 可直接用协议里的名字）。</summary>
    public static string? CapabilityOf(string command) => command switch
    {
        ProtectedFolders or "protected-folders.read" => "protected-folders.read",
        Timeline or "timeline.read" => "timeline.read",
        Changes or "changes.read" => "changes.read",
        PreviewRestore or "restore.preview" => "restore.preview",
        Restore or "restore.execute" => "restore.execute",
        Undo or "restore.undo" => "restore.undo",
        Capabilities => Capabilities,          // 元命令：不属于任何 capability
        _ => null,
    };

    /// <summary>只有 capabilities 不需要真实数据，其余都要拉起 Runtime。</summary>
    public static bool NeedsRuntime(string command) => command != Capabilities;

    public static AgentResponse Run(string command, AgentRequest request, TimeBackApplication? app)
    {
        var capability = CapabilityOf(command)!;
        return command switch
        {
            Capabilities => ShowCapabilities(),
            ProtectedFolders or "protected-folders.read" => ListProtectedFolders(app!, capability),
            Timeline or "timeline.read" => ReadTimeline(app!, capability, request),
            Changes or "changes.read" => ReadChanges(app!, capability, request),
            PreviewRestore or "restore.preview" => PreviewRestorePlan(app!, capability, request),
            Restore or "restore.execute" => ExecuteRestore(app!, capability, request),
            Undo or "restore.undo" => UndoRestore(app!, capability, request),
            _ => AgentResponseFactory.Failed(capability, AgentErrorCode.UnknownCommand,
                    $"未知命令「{command}」。可用命令见 capabilities。"),
        };
    }

    public static AgentResponse Usage()
    {
        var data = new UsageData
        {
            Usage = "LastRegret.Agent <command>    （需要参数的命令从 stdin 读一个 JSON 对象）",
            Commands = All.ToList(),
            Notes = new List<string>
            {
                "stdout 只输出一个 JSON；诊断信息走 stderr。",
                "restore 必须携带同一次预览得到的 fingerprint；本版本不提供任何绕过预览或忽略指纹的开关。",
                "预览与读取类命令零副作用：不写磁盘、不改历史。",
            },
        };
        return AgentResponseFactory.Success(Capabilities, data);
    }

    // ───────────────────────── capabilities ─────────────────────────

    private static AgentResponse ShowCapabilities()
    {
        var data = new CapabilitiesData();
        foreach (var (id, ro, desc) in AgentCapabilities.All)
        {
            data.Capabilities.Add(new CapabilityInfo { Id = id, Readonly = ro, Description = desc });
        }
        return AgentResponseFactory.Success(Capabilities, data);
    }

    // ───────────────────────── protected-folders.read ─────────────────────────

    private static AgentResponse ListProtectedFolders(TimeBackApplication app, string capability)
    {
        var data = new ProtectedFoldersData();
        foreach (var f in app.GetProtectedFolders())
        {
            data.Folders.Add(new ProtectedFolderInfo
            {
                RootId = f.RootId,
                Path = f.RootPath,
                Enabled = f.Enabled,
                Watching = f.Watching,
                HasBaseline = f.HasBaseline,
                EventCount = f.EventCount,
                LastEventUtc = Iso(f.LastEventUtc),
            });
        }
        return AgentResponseFactory.Success(capability, data);
    }

    // ───────────────────────── timeline.read ─────────────────────────

    private static AgentResponse ReadTimeline(TimeBackApplication app, string capability, AgentRequest request)
    {
        if (request.RootId is null) return Missing(capability, "rootId");
        if (!TryRoot(app, request.RootId.Value, capability, out var failure)) return failure!;

        if (!TryParseUtc(request.FromUtc, out var from, out var dateError)) return BadRequest(capability, dateError!);
        if (!TryParseUtc(request.ToUtc, out var to, out dateError)) return BadRequest(capability, dateError!);

        var limit = Math.Clamp(request.Limit ?? 50, 1, 1000);
        var points = app.GetTimeline(request.RootId.Value, from, to, limit);

        var data = new TimelineData { RootId = request.RootId.Value };
        foreach (var p in points)
        {
            data.Points.Add(new TimelinePointInfo
            {
                SnapshotId = p.SnapshotId,
                TimestampUtc = p.TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
                Kind = p.Kind.ToCode(),
                FileCount = p.FileCount,
                DirectoryCount = p.DirectoryCount,
                TotalBytes = p.TotalBytes,
                Note = p.Note,
            });
        }
        return AgentResponseFactory.Success(capability, data);
    }

    // ───────────────────────── changes.read ─────────────────────────

    private static AgentResponse ReadChanges(TimeBackApplication app, string capability, AgentRequest request)
    {
        if (request.RootId is null) return Missing(capability, "rootId");
        if (!TryRoot(app, request.RootId.Value, capability, out var failure)) return failure!;

        if (!TryParseUtc(request.FromUtc, out var from, out var dateError)) return BadRequest(capability, dateError!);
        if (!TryParseUtc(request.ToUtc, out var to, out dateError)) return BadRequest(capability, dateError!);

        var query = new EventQuery
        {
            RootId = request.RootId.Value,
            FromUtc = from,
            ToUtc = to,
            Limit = Math.Clamp(request.Limit ?? 200, 1, 5000),
            Offset = Math.Max(0, request.Offset ?? 0),
            IncludeTransient = request.IncludeTransient ?? false,
            Descending = true,
        };

        var events = app.GetChanges(query);
        var data = new ChangesData { RootId = request.RootId.Value, Count = events.Count };
        foreach (var e in events)
        {
            data.Changes.Add(new ChangeInfo
            {
                EventId = e.Id,
                TimestampUtc = e.TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
                Operation = e.Operation.ToCode(),
                Kind = e.Kind.ToCode(),
                Path = e.RelativePath,
                OldPath = e.OldRelativePath,
                SizeAfter = e.SizeAfter,
                HashAfter = e.HashAfter,
            });
        }
        return AgentResponseFactory.Success(capability, data);
    }

    // ───────────────────────── restore.preview（只读）─────────────────────────

    private static AgentResponse PreviewRestorePlan(TimeBackApplication app, string capability, AgentRequest request)
    {
        if (request.RootId is null) return Missing(capability, "rootId");
        if (!TryRoot(app, request.RootId.Value, capability, out var failure)) return failure!;
        if (string.IsNullOrWhiteSpace(request.AtUtc)) return Missing(capability, "atUtc");
        if (!TryParseUtc(request.AtUtc, out var atUtc, out var dateError)) return BadRequest(capability, dateError!);

        // 只读：TimeBackApplication → RestoreEngine.BuildPreviewAt。本层不算计划。
        var (plan, error) = app.PreviewRestore(request.RootId.Value, atUtc!.Value, request.IncludePaths);
        if (plan is null)
        {
            return AgentResponseFactory.Failed(capability, AgentErrorCode.PreviewFailed,
                error ?? "无法生成恢复预览。");
        }

        var response = MapPlan(capability, request, atUtc.Value, plan);
        response.Status = plan.HasEffect ? AgentStatus.Success : AgentStatus.NoChange;
        return response;
    }

    // ───────────────────────── restore.execute（写磁盘）─────────────────────────

    private static AgentResponse ExecuteRestore(TimeBackApplication app, string capability, AgentRequest request)
    {
        if (request.RootId is null) return Missing(capability, "rootId");
        if (!TryRoot(app, request.RootId.Value, capability, out var failure)) return failure!;
        if (string.IsNullOrWhiteSpace(request.AtUtc)) return Missing(capability, "atUtc");
        if (!TryParseUtc(request.AtUtc, out var atUtc, out var dateError)) return BadRequest(capability, dateError!);

        // 没有指纹就不执行 —— 不允许"直接给我一份新计划然后跑掉"
        if (string.IsNullOrWhiteSpace(request.Fingerprint))
        {
            return AgentResponseFactory.Rejected(capability, AgentErrorCode.FingerprintRequired,
                "执行恢复必须携带同一次预览得到的 fingerprint。请先调用 preview-restore。");
        }

        // 用同样的输入重新取一次计划，只为核对指纹；不一致就拒绝，绝不改写后执行
        var (plan, error) = app.PreviewRestore(request.RootId.Value, atUtc!.Value, request.IncludePaths);
        if (plan is null)
        {
            return AgentResponseFactory.Failed(capability, AgentErrorCode.PreviewFailed,
                error ?? "无法生成恢复预览。");
        }
        if (!string.Equals(plan.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
        {
            return AgentResponseFactory.Rejected(capability, AgentErrorCode.FingerprintMismatch,
                "恢复计划已经变化，请重新预览后再执行。");
        }
        if (!plan.HasEffect)
        {
            return AgentResponseFactory.NoChange(capability, MapPlanData(request, atUtc.Value, plan),
                "当前状态与目标状态一致，没有需要执行的操作。");
        }

        // 照旧交给引擎：指纹校验、恢复前安全点、冲突跳过、差异执行、逐步记账
        var outcome = app.ExecuteRestore(plan, request.Fingerprint, request.AllowNewRemovals ?? false);
        return MapOutcome(capability, outcome);
    }

    // ───────────────────────── restore.undo ─────────────────────────

    private static AgentResponse UndoRestore(TimeBackApplication app, string capability, AgentRequest request)
    {
        long? rootId = request.RootId;
        if (rootId is not null && !TryRoot(app, rootId.Value, capability, out var rootFailure)) return rootFailure!;

        var operationId = request.OperationId ?? app.GetLastUndoableRestore(rootId)?.Id;
        if (operationId is null)
        {
            return AgentResponseFactory.Failed(capability, AgentErrorCode.UndoUnavailable,
                "没有可撤销的恢复操作。");
        }

        var (plan, error) = app.PreviewUndo(operationId.Value);
        if (plan is null)
        {
            return AgentResponseFactory.Failed(capability, AgentErrorCode.UndoUnavailable,
                error ?? "无法生成撤销预览。");
        }

        // 不带指纹 = 只预览（只读，零副作用）
        if (string.IsNullOrWhiteSpace(request.Fingerprint))
        {
            var preview = new UndoPreviewData
            {
                OperationId = operationId.Value,
                Fingerprint = plan.Fingerprint,
                TargetTimeUtc = plan.TargetTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                StepCount = plan.Steps.Count,
                ToRestore = plan.RestoreCount,
                ToRemove = plan.RemoveCount,
                Warnings = plan.Warnings.ToList(),
            };
            return plan.HasEffect
                ? AgentResponseFactory.Success(capability, preview)
                : AgentResponseFactory.NoChange(capability, preview, "这次撤销没有需要执行的操作。");
        }

        if (!string.Equals(plan.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
        {
            return AgentResponseFactory.Rejected(capability, AgentErrorCode.FingerprintMismatch,
                "撤销计划已经变化，请重新预览后再执行。");
        }

        var outcome = app.UndoRestore(operationId.Value, request.Fingerprint, request.AllowNewRemovals ?? false);
        return MapOutcome(capability, outcome);
    }

    // ───────────────────────── 映射与校验 ─────────────────────────

    private static AgentResponse MapPlan(string capability, AgentRequest request, DateTime atUtc, RestorePlan plan) =>
        AgentResponseFactory.Success(capability, MapPlanData(request, atUtc, plan));

    private static PlanData MapPlanData(AgentRequest request, DateTime atUtc, RestorePlan plan) => new()
    {
        PreviewInput = new PreviewInputs
        {
            RootId = request.RootId ?? plan.RootId,
            AtUtc = atUtc.ToString("o", CultureInfo.InvariantCulture),
            IncludePaths = request.IncludePaths,
        },
        Fingerprint = plan.Fingerprint,
        HasEffect = plan.HasEffect,
        StepCount = plan.Steps.Count,
        ToRestore = plan.RestoreCount,
        ToCreateDirectories = plan.CreateDirectoryCount,
        ToRemove = plan.RemoveCount,
        ToRemoveDirectories = plan.RemoveDirectoryCount,
        Unavailable = plan.UnavailableCount,
        Affected = plan.ExecutableCount,
        TargetTimeUtc = plan.TargetTimeUtc.ToString("o", CultureInfo.InvariantCulture),
        Warnings = plan.Warnings.ToList(),
    };

    private static AgentResponse MapOutcome(string capability, RestoreOutcome outcome)
    {
        var data = new RestoreData
        {
            ExecutionOk = outcome.Ok,
            OperationId = outcome.OperationId,
            OperationStatus = outcome.Status.ToCode(),
            Succeeded = outcome.Succeeded,
            Failed = outcome.Failed,
            Skipped = outcome.Skipped,
            FilesChanged = outcome.FilesChanged,
            PreSnapshotId = outcome.PreSnapshotId,
            PostSnapshotId = outcome.PostSnapshotId,
            CanUndo = outcome.CanUndo,
            Message = outcome.Message,
        };

        if (!outcome.Ok)
        {
            var message = string.IsNullOrWhiteSpace(outcome.Message) ? "恢复没有成功完成。" : outcome.Message;

            // 拒绝 ≠ 失败（FINAL-WB-001）：计划过期时什么都没执行，
            // 必须回报 rejected + plan_stale，让调用方重新预览，
            // 而不是 failed（看起来像"试过了但坏了"），更不能是 success。
            var response = outcome.Rejected
                ? AgentResponseFactory.Rejected(capability, AgentErrorCode.PlanStale, message)
                : AgentResponseFactory.Failed(capability, AgentErrorCode.RestoreFailed, message);
            response.Data = data;
            if (outcome.Failures.Count > 0) response.Warnings = outcome.Failures.Take(20).ToList();
            return response;
        }

        return AgentResponseFactory.Success(capability, data);
    }

    /// <summary>受保护范围内不存在这个 rootId 时给出明确错误，而不是静默返回空结果。</summary>
    private static bool TryRoot(TimeBackApplication app, long rootId, string capability, out AgentResponse? failure)
    {
        var exists = app.GetProtectedFolders().Any(f => f.RootId == rootId);
        if (exists)
        {
            failure = null;
            return true;
        }

        failure = AgentResponseFactory.Failed(capability, AgentErrorCode.RootNotFound,
            $"找不到 rootId={rootId} 的受保护文件夹。可以先调用 protected-folders 查看清单。");
        return false;
    }

    private static AgentResponse Missing(string capability, string field) =>
        AgentResponseFactory.Failed(capability, AgentErrorCode.MissingParameter, $"缺少必需参数：{field}。");

    private static AgentResponse BadRequest(string capability, string message) =>
        AgentResponseFactory.Failed(capability, AgentErrorCode.InvalidRequest, message);

    /// <summary>解析 UTC 时间。接受 ISO 8601（带 Z / 带偏移 / 不带时区按 UTC 处理）。</summary>
    private static bool TryParseUtc(string? text, out DateTime? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed.UtcDateTime;
            return true;
        }

        error = $"时间格式无法识别：{text}。请使用 ISO 8601（例如 2026-09-14T01:02:03Z）。";
        return false;
    }

    private static string? Iso(DateTime? value) =>
        value?.ToString("o", CultureInfo.InvariantCulture);
}

/// <summary>统一外壳构造：四种 status 只能从这里产生，避免各处自己拼出"失败但 ok=true"。</summary>
internal static class AgentResponseFactory
{
    public static AgentResponse Success(string capability, object? data) => new()
    {
        Capability = capability,
        Ok = true,
        Status = AgentStatus.Success,
        Data = data,
    };

    public static AgentResponse NoChange(string capability, object? data, string? note = null) => new()
    {
        Capability = capability,
        Ok = true,
        Status = AgentStatus.NoChange,
        Data = data,
        Warnings = note is null ? null : new List<string> { note },
    };

    public static AgentResponse Rejected(string capability, string code, string message) => new()
    {
        Capability = capability,
        Ok = false,
        Status = AgentStatus.Rejected,
        Error = new AgentError { Code = code, Message = message },
    };

    public static AgentResponse Failed(string capability, string code, string message) => new()
    {
        Capability = capability,
        Ok = false,
        Status = AgentStatus.Failed,
        Error = new AgentError { Code = code, Message = message },
    };
}
