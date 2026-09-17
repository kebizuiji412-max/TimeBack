using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LastRegret.Runtime;

namespace LastRegret.Agent;

/// <summary>
/// timeback-agent 1.0 的最小 CLI 宿主：**一次进程处理一个请求**。
///
/// <code>
/// LastRegret.Agent.exe capabilities
/// echo '{ "rootId": 1, "limit": 20 }' | LastRegret.Agent.exe changes
/// </code>
///
/// 约定：
///   · stdout **只**输出一个 JSON（诊断信息一律走 stderr），否则 Agent 无法可靠解析；
///   · 退出码只给 shell 用（0 完成 / 1 参数或请求错误 / 2 业务拒绝 / 3 内部失败），
///     真正的机器语义在 JSON 的 status 字段里；
///   · Runtime 用完必须释放（finally），不维护任何全局单例、不跨进程共享状态；
///   · 没有网络端口、没有常驻循环。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向时可能不支持，忽略 */ }
        try { Console.InputEncoding = Encoding.UTF8; } catch { }

        var command = args.Length > 0 ? args[0].Trim() : string.Empty;
        var capability = command.Length > 0 ? AgentCommands.CapabilityOf(command) : null;

        try
        {
            return Run(command, capability);
        }
        catch (Exception ex)
        {
            // 完整异常只进 stderr；stdout 仍然是稳定的、人能看懂的 JSON
            Console.Error.WriteLine("[internal] " + ex);
            Write(AgentResponseFactory.Failed(capability ?? "capabilities",
                AgentErrorCode.InternalError, "TimeBack 在处理请求时遇到内部错误。"));
            return 3;
        }
    }

    private static int Run(string command, string? capability)
    {
        if (command.Length == 0)
        {
            Write(AgentCommands.Usage());
            return 1;                                    // 缺参数
        }

        if (command is "--help" or "-h" or "help")
        {
            Write(AgentCommands.Usage());
            return 0;
        }

        if (capability is null)
        {
            Write(AgentResponseFactory.Failed(command, AgentErrorCode.UnknownCommand,
                $"未知命令「{command}」。可用命令：" + string.Join(" / ", AgentCommands.All)));
            return 1;
        }

        AgentRequest request;
        try
        {
            request = ReadRequest();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine("[request] " + ex.Message);
            Write(AgentResponseFactory.Failed(capability, AgentErrorCode.InvalidRequest,
                "stdin 不是合法的 JSON 请求对象。"));
            return 1;
        }

        AgentResponse response;
        if (!AgentCommands.NeedsRuntime(command))
        {
            response = AgentCommands.Run(command, request, null);
        }
        else
        {
            AppRuntime? runtime = null;
            try
            {
                // 组合根仍然只有 AppRuntime：CLI 不自己组装任何组件
                runtime = AppRuntime.Create();
                response = AgentCommands.Run(command, request, runtime.Application);
            }
            finally
            {
                runtime?.Dispose();
            }
        }

        Write(response);
        return ExitCodeFor(response.Status);
    }

    /// <summary>stdin 的 JSON 请求（可选）。没有重定向 / 内容为空时用默认请求。</summary>
    private static AgentRequest ReadRequest()
    {
        if (!Console.IsInputRedirected) return new AgentRequest();
        var text = Console.In.ReadToEnd();
        if (string.IsNullOrWhiteSpace(text)) return new AgentRequest();
        return JsonSerializer.Deserialize<AgentRequest>(text, Json) ?? new AgentRequest();
    }

    private static void Write(AgentResponse response)
    {
        Console.Out.Write(JsonSerializer.Serialize(response, Json));
        Console.Out.Write('\n');
        Console.Out.Flush();
    }

    private static int ExitCodeFor(string status) => status switch
    {
        AgentStatus.Success or AgentStatus.NoChange => 0,
        AgentStatus.Rejected => 2,      // 业务拒绝：请求合法但被安全机制挡下
        _ => 3,                          // failed
    };
}
