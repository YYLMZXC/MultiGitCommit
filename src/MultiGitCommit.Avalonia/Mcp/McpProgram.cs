using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiGitCommit;

namespace MultiGitCommit.Mcp;

/// <summary>
/// MCP Server 入口逻辑（原独立项目 MultiGitCommit.Mcp 的 Program，合并进主项目后
/// 由桌面 Program.Main 分派：MultiGitCommit.exe --stdio 等）。
/// 仅桌面 TFM 编译（移动端无法执行 git 子进程，不参与 MCP）。
/// </summary>
internal static class McpProgram
{
    private enum Mode { Stdio, Help, ListTools, SelfTest }

    private sealed record McpOptions(bool ReadOnly, string? GitPath, string? AppDir);

    private const string Usage = """
        MultiGitCommit MCP Server（stdio transport，供外部 AI 客户端调用软件功能）

        不带参数启动本程序 = 启动图形界面；带以下参数则运行 MCP 模式：

        用法：
          MultiGitCommit --stdio             MCP stdio 模式（由 MCP 客户端拉起，不要手动运行）
          MultiGitCommit --self-test         自检：协议握手、工具清单、环境探测
          MultiGitCommit --list-tools        打印可用工具
          MultiGitCommit --help              显示本帮助

        选项：
          --readonly                        只读模式：隐藏全部写工具
          --git <路径>                      指定 git 可执行文件 / Git 根目录（覆盖软件设置）
          --app-dir <目录>                  GUI 安装目录，用于复用其 git\ 子目录里的便携 Git

        环境变量（与命令行选项等价，便于写在 MCP 客户端配置的 env 里）：
          MULTIGITCOMMIT_MCP_READONLY=1     只读模式
          MULTIGITCOMMIT_MCP_GIT=<路径>     指定 git
          MULTIGITCOMMIT_MCP_APP_DIR=<目录> GUI 安装目录
          MULTIGITCOMMIT_MCP_DEBUG=1        把诊断日志写到 stderr（stdout 始终只承载 JSON-RPC）

        全局设置 settings.json、每仓库 multigitcommit.config.json 与图形界面共用；
        MCP 不会读取或返回任何保存的令牌。
        """;

    /// <summary>命令行里出现任何已知 MCP 参数即进入 MCP 模式，否则按图形界面启动。</summary>
    public static bool IsMcpArgs(string[] args)
        => args.Any(static a => a.StartsWith('-'));

    /// <summary>MCP 模式主流程，返回进程退出码。</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        (Mode mode, McpOptions options, string? error) = Parse(args);
        if (error != null)
        {
            ConsoleAttach.Attach();
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (mode != Mode.Stdio)
        {
            // 仅人类可读输出模式设置编码并附加控制台；stdio 模式绝不碰 Console.OutputEncoding
            ConsoleAttach.Attach();
            Console.OutputEncoding = new UTF8Encoding(false);
        }

        switch (mode)
        {
            case Mode.Help:
                Console.WriteLine(Usage);
                return 0;

            case Mode.ListTools:
                return ListTools(options);

            case Mode.SelfTest:
                return await SelfTestAsync(options).ConfigureAwait(false);

            default:
                break;
        }

        using var cts = new CancellationTokenSource();
        try
        {
            // MCP 客户端常以无控制台方式拉起进程，注册 Ctrl+C 钩子可能不被支持
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        }
        catch (Exception)
        {
            // 忽略：stdin 关闭本来就会结束 stdio 会话
        }

        InitializeGitEnvironment(options);

        var protocol = new McpProtocol(Console.OpenStandardInput(), Console.OpenStandardOutput(), options.ReadOnly);
        try
        {
            await protocol.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            // stdio 模式下的错误只走 stderr，避免破坏协议帧
            Console.Error.WriteLine("[MultiGitCommit] MCP 服务异常：" + ex);
            return 1;
        }
    }

    private static (Mode, McpOptions, string?) Parse(string[] args)
    {
        bool readOnly = EnvFlag("MULTIGITCOMMIT_MCP_READONLY");
        string? git = Environment.GetEnvironmentVariable("MULTIGITCOMMIT_MCP_GIT");
        string? appDir = Environment.GetEnvironmentVariable("MULTIGITCOMMIT_MCP_APP_DIR");
        Mode mode = Mode.Stdio;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--stdio":
                    break;
                case "--readonly":
                    readOnly = true;
                    break;
                case "--self-test":
                    mode = Mode.SelfTest;
                    break;
                case "--list-tools":
                    mode = Mode.ListTools;
                    break;
                case "--help" or "-h" or "/?":
                    mode = Mode.Help;
                    break;
                case "--git":
                    if (++i >= args.Length) return (mode, new McpOptions(readOnly, git, appDir), "--git 缺少路径参数");
                    git = args[i].Trim('"');
                    break;
                case "--app-dir":
                    if (++i >= args.Length) return (mode, new McpOptions(readOnly, git, appDir), "--app-dir 缺少目录参数");
                    appDir = args[i].Trim('"');
                    break;
                default:
                    return (mode, new McpOptions(readOnly, git, appDir), $"未知参数：{args[i]}");
            }
        }

        return (mode, new McpOptions(readOnly, git, appDir), null);
    }

    /// <summary>
    /// 让 MCP 进程使用与图形界面相同的 git 来源。合并后 MCP 与 GUI 是同一个 exe，
    /// 输出目录自带 git\ 子目录，GitLocator 的 Embedded / Auto 模式会自动优先使用；
    /// --git / --app-dir 仅作覆盖备用。
    /// </summary>
    private static void InitializeGitEnvironment(McpOptions options)
    {
        IGitEnvironmentService environment = AppServices.Current.GitEnvironment;

        if (!string.IsNullOrWhiteSpace(options.GitPath))
        {
            environment.Refresh(GitSourceMode.Custom, options.GitPath);
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.AppDir))
        {
            string? embedded = FindEmbeddedGit(options.AppDir);
            if (embedded != null)
            {
                environment.Refresh(GitSourceMode.Custom, embedded);
                return;
            }
        }

        InitializeGitEnvironment();
    }

    /// <summary>
    /// 按共享设置解析 git 来源，失败再退回自动检测。MCP 与 CLI 共用：
    /// exe 同目录自带 git\ 子目录时，GitLocator 的 Embedded / Auto 模式会自动优先使用。
    /// </summary>
    internal static void InitializeGitEnvironment()
    {
        AppSettings settings = AppServices.Current.Settings.Current;
        IGitEnvironmentService environment = AppServices.Current.GitEnvironment;
        GitEnvironmentSnapshot snapshot = environment.Refresh(settings.GitSource, settings.CustomGitPath);
        if (!snapshot.Available)
            environment.Refresh(GitSourceMode.Auto, null);
    }

    private static string? FindEmbeddedGit(string appDir)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(appDir.Trim().Trim('"')));
        string[] candidates =
        {
            Path.Combine(full, "git", "cmd", "git.exe"),
            Path.Combine(full, "git", "bin", "git"),
            Path.Combine(full, "git", "bin", "git.exe"),
            Path.Combine(full, "git", "cmd", "git")
        };
        return Array.Find(candidates, File.Exists);
    }

    private static int ListTools(McpOptions options)
    {
        JsonNode list = new McpToolSet(options.ReadOnly).List();
        JsonArray? tools = list["tools"]?.AsArray();
        Console.WriteLine($"可用工具（共 {tools?.Count ?? 0} 个，只读模式：{options.ReadOnly}）：");
        if (tools == null) return 1;

        foreach (JsonNode? tool in tools)
        {
            string name = tool?["name"]?.GetValue<string>() ?? "?";
            string description = tool?["description"]?.GetValue<string>() ?? string.Empty;
            Console.WriteLine($"  {name,-22} {description.Split('。')[0]}");
        }

        return 0;
    }

    private static async Task<int> SelfTestAsync(McpOptions options)
    {
        InitializeGitEnvironment(options);

        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var protocol = new McpProtocol(input, output, options.ReadOnly);

        using JsonDocument initDoc = JsonDocument.Parse("""
            {"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"self-test","version":"1.0"}}
            """);
        JsonNode init = await protocol.HandleAsync("initialize", initDoc.RootElement, CancellationToken.None)
            .ConfigureAwait(false);
        JsonNode list = await protocol.HandleAsync("tools/list", default, CancellationToken.None)
            .ConfigureAwait(false);

        using JsonDocument callDoc = JsonDocument.Parse("""
            {"name":"get_git_environment","arguments":{}}
            """);
        JsonNode call = await protocol.HandleAsync("tools/call", callDoc.RootElement, CancellationToken.None)
            .ConfigureAwait(false);

        int toolCount = list["tools"]?.AsArray().Count ?? 0;
        string envText = call["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
        bool handshake = init["protocolVersion"]?.GetValue<string>() == McpProtocol.LatestProtocolVersion;
        bool envOk = envText.Contains("\"ok\": true", StringComparison.Ordinal);
        bool isError = call["isError"]?.GetValue<bool>() ?? true;

        Console.WriteLine($"协议握手：{(handshake ? "通过" : "失败")}（{McpProtocol.LatestProtocolVersion}）");
        Console.WriteLine($"工具清单：{(toolCount > 0 ? "通过" : "失败")}（{toolCount} 个工具）");
        Console.WriteLine($"环境探测：{(envOk && !isError ? "通过" : "失败")}");
        Console.WriteLine($"git 可用：{AppServices.Current.GitEnvironment.IsAvailable}（{AppServices.Current.GitEnvironment.GitExe}）");
        Console.WriteLine($"设置文件：{AppServices.Current.Settings.Current.SettingsFilePath}");

        return handshake && toolCount > 0 && envOk && !isError ? 0 : 1;
    }

    private static bool EnvFlag(string name)
        => Environment.GetEnvironmentVariable(name) is "1" or "true" or "TRUE" or "yes" or "YES";
}
