using System.Text;

namespace MultiGitCommit;

/// <summary>
/// git clone 参数组装：命令预览（CloneView）与实际执行（RepositoryCommands）共用同一份规则，
/// 避免「预览的命令」与「真正跑的命令」各自拼装产生漂移。
/// </summary>
public static class GitCloneArguments
{
    /// <summary>组装 clone 的选项部分（不含 url 与目标目录）。</summary>
    public static string BuildOptions(string? branch, bool recursive, bool shallow)
    {
        var sb = new StringBuilder("clone");
        if (!string.IsNullOrWhiteSpace(branch))
            sb.Append(" --branch ").Append(branch.Trim());
        if (recursive)
            sb.Append(" --recursive");
        if (shallow)
            sb.Append(" --depth 1");
        // --progress：输出被重定向到管道时强制 git 输出克隆进度，便于写入日志
        sb.Append(" --progress ");
        return sb.ToString();
    }

    /// <summary>组装完整的 clone 参数串（url 与目标目录分别加引号，兼容路径含空格）。</summary>
    public static string Build(string? branch, bool recursive, bool shallow, string url, string targetDir)
        => $"{BuildOptions(branch, recursive, shallow)}\"{url}\" \"{targetDir}\"";
}

/// <summary>设置默认分支的结果（界面只需要成功与否和一句提示）。</summary>
public sealed class SetDefaultBranchResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>手动命令的执行结果；<see cref="Error"/> 非空表示未执行（如未找到 git）。</summary>
public sealed class ManualCommandResult
{
    public GitCommandResult? Result { get; init; }

    public string? Error { get; init; }
}

/// <summary>一条手动命令被翻译成「可执行文件 + 参数」的计划（纯函数产出，便于测试）。</summary>
internal sealed record ManualCommandPlan(string? FileName, string Arguments, string Label, string? Error);

/// <summary>
/// 仓库级 git 命令的唯一入口：初始化、默认分支、克隆、工作区状态、手动命令（含各平台 shell 选择）。
/// 界面只负责收集参数、确认与展示，不再直接拼命令或判断操作系统。
/// </summary>
public interface IRepositoryCommands
{
    Task<GitCommandResult> InitAsync(string repoDir);

    Task<SetDefaultBranchResult> SetDefaultBranchAsync(string repoDir, string branch);

    Task<GitCommandResult> CloneAsync(string parentDir, string arguments, string label);

    Task<int> CountChangedFilesAsync(string repoDir, CancellationToken cancellationToken = default);

    Task<ManualCommandResult> RunManualCommandAsync(string workDir, string command);
}

/// <summary><see cref="IRepositoryCommands"/> 的默认实现。</summary>
public sealed class RepositoryCommands : IRepositoryCommands
{
    private readonly IGitCliRunner _cli;
    private readonly ILoggedGitRunner _logged;
    private readonly IGitEnvironmentService _environment;

    public RepositoryCommands(IGitCliRunner cliRunner, ILoggedGitRunner loggedRunner,
        IGitEnvironmentService environment)
    {
        _cli = cliRunner;
        _logged = loggedRunner;
        _environment = environment;
    }

    public Task<GitCommandResult> InitAsync(string repoDir)
        => _logged.RunAsync(repoDir, "init", "git init");

    public async Task<SetDefaultBranchResult> SetDefaultBranchAsync(string repoDir, string branch)
    {
        // 空仓库（尚无提交）：直接把 HEAD 指向目标分支
        GitCommandResult head = _cli.Run(new GitCommandRequest
        {
            Arguments = "rev-parse --verify HEAD",
            WorkingDirectory = repoDir,
            Timeout = GitTimeouts.Quick
        });

        if (!head.Success)
        {
            string args = $"symbolic-ref HEAD refs/heads/{branch}";
            GitCommandResult r = await _logged.RunAsync(repoDir, args, $"git {args}").ConfigureAwait(true);
            return new SetDefaultBranchResult
            {
                Success = r.Success,
                Message = r.Success ? $"已将默认分支设置为：{branch}" : $"设置默认分支失败：{branch}"
            };
        }

        // 已有提交：分支存在则切换，不存在则新建并切换
        GitCommandResult exists = _cli.Run(new GitCommandRequest
        {
            Arguments = $"show-ref --verify --quiet refs/heads/{branch}",
            WorkingDirectory = repoDir,
            Timeout = GitTimeouts.Quick
        });

        string checkoutArgs = exists.Success ? $"checkout {branch}" : $"checkout -b {branch}";
        GitCommandResult res = await _logged.RunAsync(repoDir, checkoutArgs, $"git {checkoutArgs}")
            .ConfigureAwait(true);

        return new SetDefaultBranchResult
        {
            Success = res.Success,
            Message = res.Success
                ? (exists.Success ? $"已切换到默认分支：{branch}" : $"已创建并切换到默认分支：{branch}")
                : $"创建默认分支失败：{branch}"
        };
    }

    public Task<GitCommandResult> CloneAsync(string parentDir, string arguments, string label)
        => _logged.RunAsync(parentDir, arguments, label, GitTimeouts.Clone);

    public async Task<int> CountChangedFilesAsync(string repoDir, CancellationToken cancellationToken = default)
    {
        GitCommandResult status = await _logged.RunAsync(repoDir, "status --porcelain",
            "git status --porcelain", GitTimeouts.Quick, cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        return status.Success ? GitStatusText.CountChanged(status.Stdout) : 0;
    }

    public async Task<ManualCommandResult> RunManualCommandAsync(string workDir, string command)
    {
        ManualCommandPlan plan = PlanManualCommand(command, _environment.IsAvailable,
            OperatingSystem.IsWindows(), ResolveShell());
        if (plan.Error != null)
            return new ManualCommandResult { Error = plan.Error };

        GitCommandResult result = await _logged.RunAsync(workDir, plan.Arguments, plan.Label,
            fileName: plan.FileName).ConfigureAwait(true);
        return new ManualCommandResult { Result = result };
    }

    /// <summary>
    /// 把用户输入翻译成实际要执行的进程：git 命令走当前解析到的 git，
    /// 其他命令按平台交给 shell（Windows 先切 UTF-8 代码页，避免中文乱码）。
    /// </summary>
    internal static ManualCommandPlan PlanManualCommand(string command, bool gitAvailable, bool isWindows,
        string shell)
    {
        if (command.StartsWith("git ", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "git", StringComparison.OrdinalIgnoreCase))
        {
            string gitArgs = command.Length > 4 ? command[4..] : string.Empty;
            return new ManualCommandPlan(null, gitArgs, command,
                gitAvailable
                    ? null
                    : "未找到可用的 git：请点击【Git 环境】按钮配置（自动检测 / 系统 PATH / 指定路径 / 内嵌 Git）。");
        }

        if (isWindows)
            return new ManualCommandPlan("cmd.exe", $"/c chcp 65001 >nul & {command}", command, null);

        // macOS / Linux / Android：-l 保证能拿到 Homebrew 等 PATH 配置
        string escaped = command.Replace("\"", "\\\"", StringComparison.Ordinal);
        return new ManualCommandPlan(shell, $"-lc \"{escaped}\"", command, null);
    }

    /// <summary>非 Windows 平台的 shell 探测（平台差异只在这里出现一次）。</summary>
    internal static string ResolveShell()
        => File.Exists("/bin/bash") ? "/bin/bash"
            : File.Exists("/bin/sh") ? "/bin/sh"
            : "/system/bin/sh";
}
