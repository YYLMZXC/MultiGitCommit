using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiGitCommit;

namespace MultiGitCommit.Mcp;

/// <summary>JSON 构造 / 序列化与凭据脱敏的公共工具。</summary>
internal static class McpJson
{
    /// <summary>协议线上格式：单行、不缩进、中文不转义。</summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>返回给 AI 的文本格式：缩进、中文不转义。</summary>
    private static readonly JsonSerializerOptions TextOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static string ToWire(JsonNode? node) => (node ?? new JsonObject()).ToJsonString(WireOptions);

    public static string ToText(JsonNode? node) => (node ?? new JsonObject()).ToJsonString(TextOptions);

    public static JsonNode Node(JsonElement element) => JsonNode.Parse(element.GetRawText()) ?? new JsonObject();

    /// <summary>所有对外文本统一脱敏，避免 https URL 里的令牌被回显给 AI。</summary>
    public static string Mask(string? text) => GitCredentials.MaskText(text ?? string.Empty);

    /// <summary>限制输出体积，避免 git log / status 把整屏文本塞进一次工具结果。</summary>
    public static string Truncate(string? text, int maxLength = 64 * 1024)
    {
        string value = Mask(text);
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + $"\n…（输出已截断，共 {value.Length} 字符）";
    }

    public static bool IsOk(JsonNode? payload)
        => payload is not JsonObject obj || obj["ok"]?.ToJsonString() != "false";
}

/// <summary>参数错误（工具用法不对），会以 isError 结果返回给 AI，而不是打断 MCP 会话。</summary>
internal sealed class ToolUsageException(string message) : Exception(message);

/// <summary>JSON-RPC 协议级错误。</summary>
internal sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>工具入参读取：把 JsonElement 的样板判断集中到一处。</summary>
internal static class Args
{
    private static bool Try(JsonElement args, string name, out JsonElement value)
    {
        value = default;
        return args.ValueKind == JsonValueKind.Object &&
               args.TryGetProperty(name, out value) &&
               value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined;
    }

    public static string? Str(JsonElement args, string name)
    {
        if (!Try(args, name, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public static string Required(JsonElement args, string name)
    {
        string? value = Str(args, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ToolUsageException($"缺少必需参数：{name}");
        return value.Trim();
    }

    public static bool? BoolOrNull(JsonElement args, string name)
    {
        if (!Try(args, name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ToolUsageException($"参数 {name} 必须是布尔值")
        };
    }

    public static bool Bool(JsonElement args, string name, bool fallback = false)
        => BoolOrNull(args, name) ?? fallback;

    public static int Int(JsonElement args, string name, int fallback, int min, int max)
    {
        if (!Try(args, name, out JsonElement value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            throw new ToolUsageException($"参数 {name} 必须是整数");
        if (number < min || number > max)
            throw new ToolUsageException($"参数 {name} 必须在 {min}～{max} 之间");
        return number;
    }

    /// <summary>字符串数组；也接受单个字符串，便于 AI 直接传一个值。</summary>
    public static string[]? Strings(JsonElement args, string name)
    {
        if (!Try(args, name, out JsonElement value)) return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            string? single = value.GetString();
            return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single };
        }

        if (value.ValueKind != JsonValueKind.Array)
            throw new ToolUsageException($"参数 {name} 必须是字符串数组");

        var items = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ToolUsageException($"参数 {name} 必须是字符串数组");
            string? text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text)) items.Add(text.Trim());
        }

        return items.ToArray();
    }

    /// <summary>写操作的显式确认闸：必须传 confirm=true。</summary>
    public static bool Confirm(JsonElement args) => Bool(args, "confirm");
}

/// <summary>一次工具调用的日志缓冲：git 输出逐行写入，返回时脱敏并限量。</summary>
internal sealed class LogBuffer
{
    private const int MaxLines = 500;
    private const int MaxLineLength = 2000;

    private readonly List<string> _lines = new();
    private readonly object _gate = new();
    private int _dropped;

    public void Add(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        string masked = McpJson.Mask(line);
        if (masked.Length > MaxLineLength) masked = masked[..MaxLineLength] + "…";

        lock (_gate)
        {
            if (_lines.Count >= MaxLines) { _dropped++; return; }
            _lines.Add(masked);
        }
    }

    public int Count { get { lock (_gate) return _lines.Count; } }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                var sb = new StringBuilder();
                foreach (string line in _lines) sb.Append(line).Append('\n');
                if (_dropped > 0) sb.Append($"…（另有 {_dropped} 行日志已省略）\n");
                return sb.ToString();
            }
        }
    }
}

/// <summary>一次工具调用的上下文：独立的会话、日志与仓库路径解析。</summary>
internal sealed class ToolContext
{
    public ToolContext(AppSession session, LogBuffer log, bool readOnly)
    {
        Session = session;
        Log = log;
        ReadOnly = readOnly;
    }

    public AppSession Session { get; }

    public LogBuffer Log { get; }

    public bool ReadOnly { get; }

    public AppSettings Settings => Session.Settings.Current;

    /// <summary>
    /// 解析仓库根目录：path → 上次使用的仓库 → 当前工作目录，统一交给 RepoLocator 做严格校验。
    /// MCP 客户端启动时的 CWD 不固定，建议在工具参数里传绝对路径。
    /// </summary>
    public string Repo(JsonElement args)
    {
        string? path = Args.Str(args, "path");
        if (string.IsNullOrWhiteSpace(path)) path = Settings.LastRepoPath;
        if (string.IsNullOrWhiteSpace(path)) path = Environment.CurrentDirectory;

        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path!.Trim()));
        }
        catch (Exception ex)
        {
            throw new ToolUsageException($"路径无效：{path}（{ex.Message}）");
        }

        string? root = Session.RepoLocator.TryResolve(full, out string error);
        if (root == null)
            throw new ToolUsageException($"目录不是有效的 Git 仓库：{full}。{McpJson.Mask(error)}");
        return root;
    }

    public ProjectConfig Config(string repoRoot) => Session.ProjectConfigs.Load(repoRoot);
}
