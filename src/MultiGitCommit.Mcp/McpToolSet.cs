using System.Text.Json;
using System.Text.Json.Nodes;
using MultiGitCommit;

namespace MultiGitCommit.Mcp;

using ToolHandler = Func<JsonElement, ToolContext, CancellationToken, Task<JsonNode>>;

internal sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema,
    bool ReadOnly,
    bool Destructive,
    ToolHandler Handler)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["name"] = Name,
        ["description"] = Description,
        // JsonNode 只能挂在一个父节点下，tools/list 每次都要克隆一份
        ["inputSchema"] = InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = Name,
            ["readOnlyHint"] = ReadOnly,
            ["destructiveHint"] = Destructive,
            ["idempotentHint"] = ReadOnly,
            ["openWorldHint"] = !ReadOnly
        }
    };
}

/// <summary>工具清单（tools/list）与调用分发（tools/call）。</summary>
internal sealed class McpToolSet
{
    private readonly IReadOnlyList<ToolDefinition> _definitions = BuildDefinitions();
    private readonly bool _readOnly;

    public McpToolSet(bool readOnly) => _readOnly = readOnly;

    public JsonNode List()
    {
        var tools = new JsonArray();
        foreach (ToolDefinition tool in _definitions)
        {
            // 只读模式下直接隐藏写工具，AI 不会做无谓的调用
            if (_readOnly && !tool.ReadOnly) continue;
            tools.Add(tool.ToJson());
        }

        return new JsonObject { ["tools"] = tools };
    }

    public async Task<JsonNode> CallAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        string? name = parameters.ValueKind == JsonValueKind.Object &&
                       parameters.TryGetProperty("name", out JsonElement nameNode) &&
                       nameNode.ValueKind == JsonValueKind.String
            ? nameNode.GetString()
            : null;

        JsonElement arguments = parameters.ValueKind == JsonValueKind.Object &&
                                parameters.TryGetProperty("arguments", out JsonElement argsNode)
            ? argsNode
            : default;

        if (string.IsNullOrWhiteSpace(name)) return Error("缺少工具名 name");

        ToolDefinition? tool = _definitions.FirstOrDefault(d => d.Name == name);
        if (tool == null)
            return Error($"未知工具：{name}。可用工具：{string.Join("、", _definitions.Select(d => d.Name))}");

        if (_readOnly && !tool.ReadOnly)
            return Error("MCP Server 运行在只读模式（--readonly），写操作已禁用。");

        if (!tool.ReadOnly && !Args.Confirm(arguments))
            return Error($"工具 {name} 会修改仓库或配置。请先向用户确认，再带 confirm:true 重试。");

        // 每次调用独立构造会话与日志缓冲：服务层无 UI 依赖，日志不会在调用之间串台
        var log = new LogBuffer();
        AppSession session = AppServices.Current.CreateSession(log.Add);
        var context = new ToolContext(session, log, _readOnly);

        try
        {
            JsonNode payload = await tool.Handler(arguments, context, cancellationToken).ConfigureAwait(false);
            return Content(payload, log, !McpJson.IsOk(payload));
        }
        catch (ToolUsageException ex)
        {
            return Content(new JsonObject { ["ok"] = false, ["error"] = McpJson.Mask(ex.Message) }, log, true);
        }
        catch (OperationCanceledException)
        {
            return Content(new JsonObject { ["ok"] = false, ["error"] = "操作已取消" }, log, true);
        }
        catch (Exception ex)
        {
            return Content(new JsonObject
            {
                ["ok"] = false,
                ["error"] = "工具执行失败：" + McpJson.Mask(ex.Message)
            }, log, true);
        }
    }

    private static JsonNode Content(JsonNode payload, LogBuffer? log, bool isError)
    {
        if (payload is not JsonObject obj)
        {
            obj = new JsonObject { ["result"] = payload };
            payload = obj;
        }

        if (log != null && log.Count > 0)
            obj["log"] = McpJson.Truncate(log.Text, 32 * 1024);

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = McpJson.ToText(payload) }
            },
            ["isError"] = isError
        };
    }

    private static JsonNode Error(string message)
        => Content(new JsonObject { ["ok"] = false, ["error"] = McpJson.Mask(message) }, null, true);

    // ==================== 工具定义 ====================

    private static IReadOnlyList<ToolDefinition> BuildDefinitions() => new[]
    {
        new ToolDefinition(
            "get_git_environment",
            "查看软件的运行环境：git 是否可用及其路径、版本号、全局设置文件路径与设置摘要（git 来源、凭据来源、上次仓库等）。不返回任何令牌。排查「git 未找到」或确认配置是否生效时先调用此工具。",
            Schema(new JsonObject()),
            true, false,
            McpToolHandlers.GetGitEnvironmentAsync),

        new ToolDefinition(
            "resolve_repo",
            "把目录解析为 Git 仓库根目录（输入子目录也可），并报告该仓库是否存在工具配置文件 multigitcommit.config.json、已配置多少个推送远端。不传 path 时依次尝试 settings.json 里的 LastRepoPath、当前工作目录。",
            Schema(new JsonObject { ["path"] = PathProperty() }),
            true, false,
            McpToolHandlers.ResolveRepoAsync),

        new ToolDefinition(
            "find_repos",
            "在指定父目录下扫描 Git 仓库（命中 .git 或 multigitcommit.config.json 即算一个），用于让 AI 发现可操作的本地项目。跳过 node_modules/bin/obj 等常见噪声目录，深度与结果数有上限。",
            Schema(new JsonObject
            {
                ["root"] = S("要扫描的父目录，绝对路径；不传则用当前工作目录"),
                ["max_depth"] = I("最多向下扫描几层（0 表示只检查 root 本身），默认 2，最大 5", 0, 5),
                ["limit"] = I("最多返回多少个仓库，默认 50，最大 200", 1, 200)
            }),
            true, false,
            McpToolHandlers.FindReposAsync),

        new ToolDefinition(
            "get_repo_status",
            "读取仓库当前状态：当前分支、工作区变更文件列表（状态/是否暂存/是否未跟踪）、提交身份、工具配置摘要。提交前用它确认要提交什么。",
            Schema(new JsonObject
            {
                ["path"] = PathProperty(),
                ["max_files"] = I("最多返回多少个变更文件，默认 200，最大 1000", 1, 1000)
            }),
            true, false,
            McpToolHandlers.GetRepoStatusAsync),

        new ToolDefinition(
            "get_config",
            "读取仓库根目录的 multigitcommit.config.json（软件自己的多远端配置，不是 .git/config）：远端 URL 列表与四个开关。URL 内嵌的令牌会被脱敏。",
            Schema(new JsonObject { ["path"] = PathProperty() }),
            true, false,
            McpToolHandlers.GetConfigAsync),

        new ToolDefinition(
            "get_history",
            "读取提交历史；传 commit_hash 时改为列出该提交改动的文件。历史条数默认 20、最大 200。",
            Schema(new JsonObject
            {
                ["path"] = PathProperty(),
                ["limit"] = I("读取最近多少条提交，默认 20，最大 200", 1, 200),
                ["commit_hash"] = S("可选：十六进制提交哈希；传入后返回该提交改动的文件清单，而不是历史列表")
            }),
            true, false,
            McpToolHandlers.GetHistoryAsync),

        new ToolDefinition(
            "get_identity",
            "读取仓库当前生效的 git 提交身份 user.name / user.email，以及软件设置里保存的身份。提交前缺少身份会导致流程中止。",
            Schema(new JsonObject { ["path"] = PathProperty() }),
            true, false,
            McpToolHandlers.GetIdentityAsync),

        new ToolDefinition(
            "run_git",
            "在仓库目录执行一条【只读】git 命令（如 status --short、log -n 5 --oneline、diff、branch -a、rev-parse HEAD）。只允许白名单内的子命令，禁止任何写操作、shell 元字符与 git -c 等可执行外部程序的选项，20 秒超时。",
            Schema(new JsonObject
            {
                ["path"] = PathProperty(),
                ["git_args"] = S("传给 git 的参数（不含开头的 git），例如：status --short")
            }, "git_args"),
            true, false,
            McpToolHandlers.RunGitAsync),

        new ToolDefinition(
            "set_config",
            "修改仓库根目录的 multigitcommit.config.json（远端列表与四个开关）。只更新传入的字段；remote_urls 提供时整体替换列表。不会修改 .git/config，也不会使用 git remote。",
            Schema(new JsonObject
            {
                ["path"] = PathProperty(),
                ["remote_urls"] = A("新的推送远端 URL 列表（ssh / https 均可），提供时整体替换；最多 20 个，禁止在 URL 内嵌令牌"),
                ["check_add_all"] = B("提交前默认执行 git add ."),
                ["check_push_tags"] = B("推送时默认追加 --tags"),
                ["check_force_commit"] = B("工作区无改动时默认生成空提交 --allow-empty"),
                ["check_force_push"] = B("界面里【强制推送】复选框的默认值；注意 MCP 的 commit_and_push 不会启用强制推送"),
                ["confirm"] = ConfirmProperty()
            }, "confirm"),
            false, true,
            McpToolHandlers.SetConfigAsync),

        new ToolDefinition(
            "commit_and_push",
            "执行软件的核心流程：git add（可选）→ git commit（消息固定为 本地时间戳 + remark）→ 逐个推送到配置里的所有远端。未传的选项默认沿用该仓库 multigitcommit.config.json。安全限制：MCP 永不启用 --force 强制推送；提交身份缺失时中止并提示使用 set_identity，除非显式传 apply_saved_identity:true。",
            Schema(new JsonObject
            {
                ["path"] = PathProperty(),
                ["remark"] = S("提交备注，会拼在本地时间戳之后；最长 500 字符，不能含换行"),
                ["remote_urls"] = A("可选：本次只推送到这些远端（不写回配置文件）；不传则用配置文件里的列表"),
                ["add_all"] = B("覆盖配置：是否执行 git add .（把全部变更加入暂存区）"),
                ["files"] = A("可选：只把列出的仓库相对路径加入暂存区（选择性 add）；提供时自动开启 add_all"),
                ["push_tags"] = B("覆盖配置：推送时追加 --tags"),
                ["force_commit"] = B("覆盖配置：工作区无改动时仍生成空提交 --allow-empty"),
                ["apply_saved_identity"] = B("提交身份缺失时，是否自动把软件设置里保存的身份写入本仓库 git config（默认 false）"),
                ["confirm"] = ConfirmProperty()
            }, "confirm"),
            false, true,
            McpToolHandlers.CommitAndPushAsync),

        new ToolDefinition(
            "pull",
            "从远端 URL 拉取当前分支更新（与软件一样直接使用 URL，不依赖 git remote 别名）。默认取配置文件里的第一个远端，默认策略 ff_only（仅快进）。",
            Schema(new JsonObject
            {
                ["path"] = PathProperty(),
                ["remote_url"] = S("可选：拉取的远端 URL；不传则用配置里的第一个远端"),
                ["strategy"] = S("拉取策略：ff_only（默认，--ff-only）、rebase（--rebase）、merge（--no-rebase）"),
                ["confirm"] = ConfirmProperty()
            }, "confirm"),
            false, true,
            McpToolHandlers.PullAsync),

        new ToolDefinition(
            "clone_repo",
            "克隆远端仓库到本机（git clone）。可指定分支、递归子模块、浅克隆；目标目录必须为空或不存在。克隆成功后可用 set_config 写入推送远端。",
            Schema(new JsonObject
            {
                ["parent_dir"] = S("本地父目录（绝对路径），不存在时自动创建"),
                ["url"] = S("远端仓库 URL"),
                ["target_dir"] = S("要创建的目录名（只能是单个目录名，不能含路径分隔符）"),
                ["branch"] = S("可选：指定分支 --branch"),
                ["recursive"] = B("可选：递归克隆子模块 --recursive"),
                ["shallow"] = B("可选：浅克隆 --depth 1"),
                ["confirm"] = ConfirmProperty()
            }, "parent_dir", "url", "target_dir", "confirm"),
            false, false,
            McpToolHandlers.CloneRepoAsync),

        new ToolDefinition(
            "init_repo",
            "在本地目录初始化新的 Git 仓库（git init），可选设置默认分支名。目录不存在时自动创建；已是仓库时会被拒绝。",
            Schema(new JsonObject
            {
                ["path"] = S("新仓库目录（绝对路径）"),
                ["default_branch"] = S("可选：默认分支名，如 main；符合 git 分支命名规则"),
                ["confirm"] = ConfirmProperty()
            }, "path", "confirm"),
            false, false,
            McpToolHandlers.InitRepoAsync),

        new ToolDefinition(
            "set_identity",
            "写入 git 提交身份 user.name / user.email（默认仓库级，可选全局）。等价于软件的【提交身份】功能；不会改动 settings.json 中保存的身份。",
            Schema(new JsonObject
            {
                ["path"] = PathProperty(),
                ["name"] = S("提交用户名"),
                ["email"] = S("提交邮箱"),
                ["scope"] = S("作用域：local（默认，写本仓库）或 global（--global）"),
                ["confirm"] = ConfirmProperty()
            }, "name", "email", "confirm"),
            false, false,
            McpToolHandlers.SetIdentityAsync)
    };

    // ==================== JSON Schema 构造小工具 ====================

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };

        if (required.Length > 0)
        {
            var array = new JsonArray();
            foreach (string name in required) array.Add(name);
            schema["required"] = array;
        }

        return schema;
    }

    private static JsonObject PathProperty()
        => S("Git 仓库根目录或其子目录（建议绝对路径）；不传则用软件设置里上次使用的仓库，再否则用当前工作目录");

    private static JsonObject ConfirmProperty()
        => B("写操作的显式确认：必须设为 true 才会执行（防止 AI 误调用）");

    private static JsonObject S(string description)
        => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject B(string description)
        => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject I(string description, int min, int max)
        => new()
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = min,
            ["maximum"] = max
        };

    private static JsonObject A(string description)
        => new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
            ["description"] = description
        };
}
