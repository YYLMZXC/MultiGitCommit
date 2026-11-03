namespace MultiGitCommit;

/// <summary>
/// 「执行命令 + 把过程写进日志」的协作契约。
///
/// 业务服务（如提交推送流程）只需要「跑这条命令、给我结果」，
/// 而「输出怎么呈现、超时怎么提示」属于界面关注点，由宿主注入实现。
/// 这样同一套日志格式只有一份，业务服务也不再依赖任何 UI 类型。
/// </summary>
public interface ILoggedGitRunner
{
    /// <summary>
    /// 执行命令并把 stdout / stderr 逐行交给日志回调。
    /// </summary>
    /// <param name="workingDirectory">工作目录。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="logLabel">日志中显示的命令摘要（null 表示不显示）。</param>
    /// <param name="timeout">超时上限，null 用默认值。</param>
    /// <param name="fileName">可执行文件；null 表示当前解析到的 git。</param>
    Task<GitCommandResult> RunAsync(string workingDirectory, string arguments, string? logLabel = null,
        TimeSpan? timeout = null, string? fileName = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ILoggedGitRunner"/> 的默认实现：执行命令并逐行写日志，超时时补上可读的原因提示。
/// 命令本身的执行细节仍由 <see cref="IGitCliRunner"/> 负责。
/// </summary>
public sealed class GitCommandReporter : ILoggedGitRunner
{
    private readonly IGitCliRunner _cli;
    private readonly Action<string> _log;

    public GitCommandReporter(IGitCliRunner cliRunner, Action<string> log)
    {
        _cli = cliRunner;
        _log = log;
    }

    public async Task<GitCommandResult> RunAsync(string workingDirectory, string arguments, string? logLabel = null,
        TimeSpan? timeout = null, string? fileName = null, CancellationToken cancellationToken = default)
    {
        TimeSpan limit = timeout ?? GitTimeouts.Default;
        if (logLabel != null) _log("> " + logLabel);

        GitCommandResult result = await _cli.RunAsync(new GitCommandRequest
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Timeout = limit,
            OnOutputLine = line => _log("      " + line),
            OnErrorLine = line => _log("      " + line)
        }, cancellationToken).ConfigureAwait(true);

        if (result.TimedOut)
        {
            _log($"[超时] 命令执行超过 {limit.TotalMinutes:0.##} 分钟仍未结束，已强制终止：{logLabel ?? arguments}");
            _log("       常见原因：远端要求输入用户名/密码或 ssh 口令（本工具为非交互执行，不会弹框输入）、网络不通、仓库过大。");
        }

        return result;
    }
}
