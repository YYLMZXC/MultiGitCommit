using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiGitCommit;
using MultiGitCommit.Mcp;

namespace MultiGitCommit.Cli;

/// <summary>CLI 用法错误：打印命令帮助并以退出码 2 结束。</summary>
internal sealed class CliUsageException(string message) : Exception(message);

/// <summary>
/// CLI 命令行入口：把命令映射到 MCP 的同一套工具处理器（McpToolSet / McpToolHandlers），
/// 复用全部业务实现、脱敏与安全白名单，输出 JSON 文本。
/// 命令行直接执行写命令即视为人工确认（自动补 confirm:true；只读工具忽略该字段）。
/// 仅桌面 TFM 编译；不带参数启动 = 图形界面，带 --stdio 等参数 = MCP Server。
/// </summary>
internal static class CliProgram
{
    private const string Usage = """
        MultiGitCommit CLI（命令行调用软件能力，输出为 JSON 文本，桌面版专用）

        用法：MultiGitCommit <命令> [参数]

          env                                      git 环境 / 设置摘要
          repos [父目录] [--depth N] [--limit N]    扫描本地 Git 仓库
          status [路径]                            仓库状态（分支 / 变更文件 / 身份 / 配置摘要）
          config [路径]                            读取 multigitcommit.config.json（远端与开关）
          history [路径] [--limit N] [--show 哈希]  提交历史；--show 改为列出某提交的改动文件
          identity get [路径]                      查看提交身份
          identity set <用户名> <邮箱> [--path 路径] [--global]  写入提交身份
          run [路径] <git 参数...>                 执行一条只读 git 命令（白名单，20 秒超时）
          push [路径] -m <备注> [--add-all] [--tags] [--empty]
               [--file 相对路径]... [--remote url]... [--apply-identity]  提交并推送全部远端
          pull [路径] [--url 远端] [--strategy ff_only|rebase|merge]     拉取当前分支
          clone <url> <父目录> [目录名] [--branch 分支] [--recursive] [--shallow]
          init <路径> [--branch 分支]
          help                                     显示本帮助

        说明：
          - [路径] 可省略：依次用软件设置里「上次使用的仓库」、当前工作目录。
          - 写命令（push / pull / clone / init / identity set）在命令行直接执行即视为人工确认。
          - 退出码：0 成功，1 执行失败，2 用法错误。
          - 不带参数启动 = 图形界面；带 --stdio 等参数 = MCP Server（供 AI 客户端调用）。
        """;

    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "env", "repos", "status", "config", "history",
        "identity", "run", "git", "push", "pull", "clone", "init"
    };

    /// <summary>
    /// 第一个参数存在且不以 - 开头即进入 CLI 模式：
    /// 已知命令按命令处理，未知词元报用法错误（退出码 2），而不是静默拉起图形界面。
    /// </summary>
    public static bool IsCliCommand(string[] args)
        => args.Length > 0 && !args[0].StartsWith('-');

    public static async Task<int> RunAsync(string[] args)
    {
        ConsoleAttach.Attach();
        Console.OutputEncoding = new UTF8Encoding(false);
        McpProgram.InitializeGitEnvironment();

        try
        {
            return await DispatchAsync(args[0].ToLowerInvariant(), args[1..]).ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MultiGitCommit] CLI 异常：" + ex.Message);
            return 1;
        }
    }

    private static Task<int> DispatchAsync(string command, string[] a) => command switch
    {
        "help" => Task.FromResult<int>(Help()),
        "env" => CallAsync("get_git_environment", new JsonObject()),
        "repos" => ReposAsync(a),
        "status" => CallAsync("get_repo_status", PathArgs(a)),
        "config" => CallAsync("get_config", PathArgs(a)),
        "history" => HistoryAsync(a),
        "identity" => IdentityAsync(a),
        "run" or "git" => RunGitAsync(a),
        "push" => PushAsync(a),
        "pull" => PullAsync(a),
        "clone" => CloneAsync(a),
        "init" => InitAsync(a),
        _ => throw new CliUsageException($"未知命令：{command}")
    };

    private static int Help()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    // ==================== 参数解析小工具 ====================

    private static string TakeValue(string[] a, ref int i, string opt)
    {
        if (++i >= a.Length) throw new CliUsageException($"{opt} 缺少值");
        return a[i];
    }

    private static int TakeInt(string[] a, ref int i, string opt)
    {
        string value = TakeValue(a, ref i, opt);
        if (!int.TryParse(value, out int n)) throw new CliUsageException($"{opt} 需要整数，收到：{value}");
        return n;
    }

    private static void TakePositionalPath(JsonObject args, string token)
    {
        if (args["path"] != null) throw new CliUsageException($"多余参数：{token}");
        args["path"] = token;
    }

    /// <summary>status / config 的参数表：[路径] 与 --path。</summary>
    private static JsonObject PathArgs(string[] a)
    {
        var args = new JsonObject();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--path" or "-p":
                    args["path"] = TakeValue(a, ref i, "--path");
                    break;
                default:
                    if (a[i].StartsWith('-')) throw new CliUsageException($"未知选项：{a[i]}");
                    TakePositionalPath(args, a[i]);
                    break;
            }
        }
        return args;
    }

    // ==================== 各命令 ====================

    private static Task<int> ReposAsync(string[] a)
    {
        var args = new JsonObject();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--depth": args["max_depth"] = TakeInt(a, ref i, "--depth"); break;
                case "--limit": args["limit"] = TakeInt(a, ref i, "--limit"); break;
                default:
                    if (a[i].StartsWith('-')) throw new CliUsageException($"未知选项：{a[i]}");
                    if (args["root"] != null) throw new CliUsageException($"多余参数：{a[i]}");
                    args["root"] = a[i];
                    break;
            }
        }
        return CallAsync("find_repos", args);
    }

    private static Task<int> HistoryAsync(string[] a)
    {
        var args = new JsonObject();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--path" or "-p": args["path"] = TakeValue(a, ref i, "--path"); break;
                case "--limit": args["limit"] = TakeInt(a, ref i, "--limit"); break;
                case "--show": args["commit_hash"] = TakeValue(a, ref i, "--show"); break;
                default:
                    if (a[i].StartsWith('-')) throw new CliUsageException($"未知选项：{a[i]}");
                    TakePositionalPath(args, a[i]);
                    break;
            }
        }
        return CallAsync("get_history", args);
    }

    // identity get [路径] | identity set <用户名> <邮箱> [--path 路径] [--global]
    private static Task<int> IdentityAsync(string[] a)
    {
        if (a.Length == 0) throw new CliUsageException("identity 需要 get / set 子命令");

        if (a[0].Equals("get", StringComparison.OrdinalIgnoreCase))
            return CallAsync("get_identity", PathArgs(a[1..]));

        if (a[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            var positional = new List<string>();
            var args = new JsonObject();
            for (int i = 1; i < a.Length; i++)
            {
                switch (a[i])
                {
                    case "--path" or "-p": args["path"] = TakeValue(a, ref i, "--path"); break;
                    case "--global": args["scope"] = "global"; break;
                    default:
                        if (a[i].StartsWith('-')) throw new CliUsageException($"未知选项：{a[i]}");
                        positional.Add(a[i]);
                        break;
                }
            }
            if (positional.Count != 2)
                throw new CliUsageException("identity set 需要 <用户名> <邮箱> 两个参数");
            args["name"] = positional[0];
            args["email"] = positional[1];
            return CallAsync("set_identity", args);
        }

        throw new CliUsageException("identity 的第一个参数必须是 get 或 set");
    }

    // run [路径] <git 参数...>：第一个「已存在的目录」当作仓库路径，其余拼成 git_args
    private static Task<int> RunGitAsync(string[] a)
    {
        string? path = null;
        var rest = new List<string>();
        foreach (string token in a)
        {
            if (path == null && rest.Count == 0 && !token.StartsWith('-') && Directory.Exists(token))
                path = token;
            else
                rest.Add(token);
        }
        if (rest.Count == 0)
            throw new CliUsageException("run 需要至少一个 git 参数，例如：run status --short");
        var args = new JsonObject { ["git_args"] = string.Join(' ', rest) };
        if (path != null) args["path"] = path;
        return CallAsync("run_git", args);
    }

    private static Task<int> PushAsync(string[] a)
    {
        var args = new JsonObject();
        var files = new List<string>();
        var remotes = new List<string>();
        bool hasRemark = false;
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "-m" or "--remark": args["remark"] = TakeValue(a, ref i, "-m"); hasRemark = true; break;
                case "--path" or "-p": args["path"] = TakeValue(a, ref i, "--path"); break;
                case "--add-all": args["add_all"] = true; break;
                case "--tags": args["push_tags"] = true; break;
                case "--empty": args["force_commit"] = true; break;
                case "--file": files.Add(TakeValue(a, ref i, "--file")); break;
                case "--remote": remotes.Add(TakeValue(a, ref i, "--remote")); break;
                case "--apply-identity": args["apply_saved_identity"] = true; break;
                default:
                    if (a[i].StartsWith('-')) throw new CliUsageException($"未知选项：{a[i]}");
                    TakePositionalPath(args, a[i]);
                    break;
            }
        }
        if (!hasRemark)
            throw new CliUsageException("push 需要 -m <备注>（提交消息固定为 本地时间戳 + 备注）");
        if (files.Count > 0) args["files"] = ToJsonArray(files);
        if (remotes.Count > 0) args["remote_urls"] = ToJsonArray(remotes);
        return CallAsync("commit_and_push", args);
    }

    private static Task<int> PullAsync(string[] a)
    {
        var args = new JsonObject();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--path" or "-p": args["path"] = TakeValue(a, ref i, "--path"); break;
                case "--url": args["remote_url"] = TakeValue(a, ref i, "--url"); break;
                case "--strategy":
                    string strategy = TakeValue(a, ref i, "--strategy");
                    if (strategy is not ("ff_only" or "rebase" or "merge"))
                        throw new CliUsageException($"--strategy 只支持 ff_only / rebase / merge，收到：{strategy}");
                    args["strategy"] = strategy;
                    break;
                default:
                    if (a[i].StartsWith('-')) throw new CliUsageException($"未知选项：{a[i]}");
                    TakePositionalPath(args, a[i]);
                    break;
            }
        }
        return CallAsync("pull", args);
    }

    private static Task<int> CloneAsync(string[] a)
    {
        var args = new JsonObject();
        var positional = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--branch": args["branch"] = TakeValue(a, ref i, "--branch"); break;
                case "--recursive": args["recursive"] = true; break;
                case "--shallow": args["shallow"] = true; break;
                case "--path" or "-p": throw new CliUsageException("clone 用位置参数 <url> <父目录> [目录名]，不支持 --path");
                default:
                    if (a[i].StartsWith('-')) throw new CliUsageException($"未知选项：{a[i]}");
                    positional.Add(a[i]);
                    break;
            }
        }
        if (positional.Count is < 2 or > 3)
            throw new CliUsageException("clone 需要 <url> <父目录> [目录名]");
        args["url"] = positional[0];
        args["parent_dir"] = positional[1];
        if (positional.Count == 3) args["target_dir"] = positional[2];
        return CallAsync("clone_repo", args);
    }

    private static Task<int> InitAsync(string[] a)
    {
        var args = new JsonObject();
        var positional = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--branch": args["default_branch"] = TakeValue(a, ref i, "--branch"); break;
                default:
                    if (a[i].StartsWith('-')) throw new CliUsageException($"未知选项：{a[i]}");
                    positional.Add(a[i]);
                    break;
            }
        }
        if (positional.Count != 1) throw new CliUsageException("init 需要 <路径>");
        args["path"] = positional[0];
        return CallAsync("init_repo", args);
    }

    // ==================== 执行 ====================

    /// <summary>调用与 MCP 相同的工具处理器并打印 JSON 文本结果。命令行执行即视为确认（confirm:true）。</summary>
    private static async Task<int> CallAsync(string tool, JsonObject args)
    {
        args["confirm"] = true;
        var toolSet = new McpToolSet(readOnly: false);
        string call = new JsonObject { ["name"] = tool, ["arguments"] = args }.ToJsonString();
        using var doc = JsonDocument.Parse(call);
        JsonNode result = await toolSet.CallAsync(doc.RootElement, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(result["content"]?[0]?["text"]?.GetValue<string>() ?? McpJson.ToText(result));
        return (result["isError"]?.GetValue<bool>() ?? true) ? 1 : 0;
    }

    private static JsonArray ToJsonArray(List<string> items)
        => new(items.Select(item => JsonValue.Create(item)).ToArray());
}
