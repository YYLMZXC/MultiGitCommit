using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiGitCommit;

namespace MultiGitCommit.Mcp;

/// <summary>
/// 极简 MCP over stdio 实现（JSON-RPC 2.0，每行一条 JSON 消息）。
///
/// 不依赖任何第三方 MCP SDK：MCP 的 stdio transport 就是换行分隔的 JSON-RPC，
/// 服务端只需要处理 initialize / tools/list / tools/call / ping 等少数方法。
/// 这样构建完全离线可用，也不会把 Avalonia / ASP.NET Core 拖进无界面进程。
///
/// 铁律：stdout 只允许输出 JSON-RPC 消息（每行一条），诊断信息一律走 stderr；
/// 所有 git 子进程的 stdout/stderr 都由 GitCliRunner 重定向，不会污染协议通道。
/// </summary>
internal sealed class McpProtocol
{
    public const string LatestProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocolVersions =
    {
        "2024-11-05", "2025-03-26", "2025-06-18"
    };

    private const string Instructions =
        "MultiGitCommit（Git 一键提交推送工具）的 MCP 接口。" +
        "先用 resolve_repo 或 get_repo_status 确认仓库根目录，再调用其他工具；路径建议传绝对路径。" +
        "远端列表来自仓库根目录的 multigitcommit.config.json，全局设置与 GUI 共用。" +
        "所有写操作（commit_and_push / pull / clone_repo / init_repo / set_config / set_identity）" +
        "必须显式传 confirm:true；提交信息固定为「本地时间戳 + remark」。" +
        "出于安全考虑，MCP 不提供强制推送、reset --hard 与任意系统命令；run_git 仅允许只读 git 子命令。" +
        "结果中的 log 字段为该次调用的命令日志（凭据已脱敏）。";

    private readonly StreamReader _input;
    private readonly StreamWriter _output;
    private readonly McpToolSet _tools;
    private bool _stop;

    public McpProtocol(Stream input, Stream output, bool readOnly)
    {
        // 明确禁用 BOM 探测 / BOM 写入：stdout 上的第一个字节必须是 JSON 的 '{'
        _input = new StreamReader(input, new UTF8Encoding(false), false);
        _output = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _tools = new McpToolSet(readOnly);
    }

    /// <summary>stdio 主循环：串行处理请求，天然避开全局单例的并发写冲突。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Diag("MCP stdio 服务已启动");

        while (!_stop && !cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                Diag("stdin 读取失败：" + ex.Message);
                break;
            }

            // stdin 关闭（客户端退出）→ 自然结束进程
            if (line == null) break;
            line = line.Trim('\uFEFF', ' ', '\t', '\r');
            if (line.Length == 0) continue;

            // id 复制为独立 JsonNode：JsonDocument 在异常离开 try 时即被释放，
            // 错误响应不能再持有指向它的 JsonElement
            JsonNode? idNode = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("jsonrpc", out JsonElement version) ||
                    version.ValueKind != JsonValueKind.String ||
                    version.GetString() != "2.0")
                {
                    throw new JsonRpcException(-32600, "Invalid Request：需要 JSON-RPC 2.0 对象");
                }

                if (root.TryGetProperty("id", out JsonElement idElement) &&
                    idElement.ValueKind != JsonValueKind.Null)
                    idNode = McpJson.Node(idElement);

                string? method = root.TryGetProperty("method", out JsonElement methodNode) &&
                                 methodNode.ValueKind == JsonValueKind.String
                    ? methodNode.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(method))
                    throw new JsonRpcException(-32600, "Invalid Request：缺少 method");

                JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;

                // 通知（无 id）不回复；exit 同时结束进程
                if (idNode == null)
                {
                    if (method is "exit" or "notifications/exit") _stop = true;
                    continue;
                }

                JsonNode result = await HandleAsync(method!, parameters, cancellationToken).ConfigureAwait(false);
                SendResult(idNode, result);
            }
            catch (JsonException)
            {
                SendError(idNode, -32700, "Parse error：消息不是合法 JSON");
            }
            catch (JsonRpcException ex)
            {
                SendError(idNode, ex.Code, ex.Message);
            }
            catch (OperationCanceledException)
            {
                SendError(idNode, -32000, "请求已取消");
            }
            catch (Exception ex)
            {
                Diag("请求处理失败：" + ex);
                SendError(idNode, -32603, "Internal error：" + McpJson.Mask(ex.Message));
            }
        }

        Diag("MCP stdio 服务已退出");
    }

    /// <summary>方法分派；self-test 也直接调用本方法，不需要真实 stdio 管道。</summary>
    public async Task<JsonNode> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return Initialize(parameters);

            case "ping":
            case "logging/setLevel":
            case "shutdown":
                return new JsonObject();

            case "exit":
                _stop = true;
                return new JsonObject();

            case "resources/list":
                return new JsonObject { ["resources"] = new JsonArray() };

            case "resources/templates/list":
                return new JsonObject { ["resourceTemplates"] = new JsonArray() };

            case "prompts/list":
                return new JsonObject { ["prompts"] = new JsonArray() };

            case "tools/list":
                return _tools.List();

            case "tools/call":
                return await _tools.CallAsync(parameters, cancellationToken).ConfigureAwait(false);

            default:
                throw new JsonRpcException(-32601, $"Method not found：{method}");
        }
    }

    private static JsonNode Initialize(JsonElement parameters)
    {
        string? requested = parameters.ValueKind == JsonValueKind.Object &&
                            parameters.TryGetProperty("protocolVersion", out JsonElement v) &&
                            v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

        string version = requested != null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : LatestProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["logging"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "multigitcommit",
                ["version"] = AppVersion.Current
            },
            ["instructions"] = Instructions
        };
    }

    private void SendResult(JsonNode? id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
        Send(response);
    }

    private void SendError(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = McpJson.Mask(message)
            }
        };
        Send(response);
    }

    private void Send(JsonNode response)
    {
        // 一行一条消息；JSON 字符串里的换行已被转义，不会破坏帧边界
        _output.Write(McpJson.ToWire(response));
        _output.Write('\n');
        _output.Flush();
    }

    private static void Diag(string message)
    {
        if (Environment.GetEnvironmentVariable("MULTIGITCOMMIT_MCP_DEBUG") == "1")
            Console.Error.WriteLine("[MultiGitCommit.Mcp] " + message);
    }
}
