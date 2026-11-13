namespace MultiGitCommit;

/// <summary>工作区 <c>git status --porcelain</c> 输出的解析（历史与拉取流程共用）。</summary>
public static class GitStatusText
{
    /// <summary>统计有改动的文件/条目数量。</summary>
    public static int CountChanged(string? porcelain)
        => string.IsNullOrEmpty(porcelain)
            ? 0
            : porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>git reset 的三种模式（业务枚举，避免 UI 里各自定义）。</summary>
public enum GitResetMode
{
    Soft,
    Mixed,
    Hard
}

/// <summary>reset 模式的命令行参数与中文说明（展示文案与命令拼装一一对应）。</summary>
public static class GitResetDescriptions
{
    public static string Argument(GitResetMode mode) => mode switch
    {
        GitResetMode.Hard => "--hard",
        GitResetMode.Mixed => "--mixed",
        _ => "--soft"
    };

    public static string Effect(GitResetMode mode) => mode switch
    {
        GitResetMode.Hard => Loc.T("工作区中所有未提交的改动都会被永久丢弃（无法恢复）"),
        GitResetMode.Mixed => Loc.T("暂存区会被重置，工作区中的文件内容保持不变"),
        _ => Loc.T("不会改动暂存区和工作区，改动会保留为已暂存状态")
    };
}

/// <summary>一次「读取提交历史」的结果（已解析，界面只负责显示）。</summary>
public sealed class HistoryLoadResult
{
    public bool Success { get; init; }

    public bool TimedOut { get; init; }

    /// <summary>失败原因是「仓库还没有任何提交」。</summary>
    public bool EmptyRepository { get; init; }

    public string Error { get; init; } = string.Empty;

    public IReadOnlyList<GitCommit> Commits { get; init; } = Array.Empty<GitCommit>();
}

/// <summary>
/// 提交历史相关 git 命令的唯一入口：命令拼装、输出解析、空仓库判定、工作区改动统计都在这一层。
/// 视图只负责触发、取消与展示。
/// </summary>
public interface IGitHistoryReader
{
    Task<HistoryLoadResult> LoadCommitsAsync(string repoRoot, int maxCommits, CancellationToken cancellationToken);

    Task<GitCommandResult> LoadChangedFilesAsync(string repoRoot, string commitHash, CancellationToken cancellationToken);

    Task<int> CountDirtyFilesAsync(string repoRoot, CancellationToken cancellationToken);

    Task<GitCommandResult> ResetAsync(string repoRoot, GitResetMode mode, string commitHash,
        CancellationToken cancellationToken);
}

/// <summary><see cref="IGitHistoryReader"/> 的默认实现。</summary>
public sealed class GitHistoryReader : IGitHistoryReader
{
    private readonly IGitCliRunner _cli;

    public GitHistoryReader(IGitCliRunner cliRunner) => _cli = cliRunner;

    public async Task<HistoryLoadResult> LoadCommitsAsync(string repoRoot, int maxCommits,
        CancellationToken cancellationToken)
    {
        // 字段之间用 0x1F 分隔：提交标题里的空格 / 制表符不会破坏解析
        GitCommandResult result = await _cli.RunAsync(new GitCommandRequest
        {
            Arguments = $"log -n {maxCommits} --date=iso --pretty=format:%H%x1f%h%x1f%ad%x1f%an%x1f%s",
            WorkingDirectory = repoRoot,
            Timeout = GitTimeouts.Quick
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return new HistoryLoadResult
            {
                TimedOut = result.TimedOut,
                EmptyRepository = IsEmptyRepository(result),
                Error = result.ErrorText
            };
        }

        return new HistoryLoadResult
        {
            Success = true,
            Commits = GitCommit.Parse(result.Stdout)
        };
    }

    public Task<GitCommandResult> LoadChangedFilesAsync(string repoRoot, string commitHash,
        CancellationToken cancellationToken)
        // --format= 只输出文件清单；--first-parent 让合并提交与第一父提交比较（否则不显示文件）
        => _cli.RunAsync(new GitCommandRequest
        {
            Arguments = $"show --name-status --format= --first-parent {commitHash}",
            WorkingDirectory = repoRoot,
            Timeout = GitTimeouts.Quick
        }, cancellationToken);

    public async Task<int> CountDirtyFilesAsync(string repoRoot, CancellationToken cancellationToken)
    {
        GitCommandResult status = await _cli.RunAsync(new GitCommandRequest
        {
            Arguments = "status --porcelain",
            WorkingDirectory = repoRoot,
            Timeout = GitTimeouts.Quick
        }, cancellationToken).ConfigureAwait(false);

        return status.Success ? GitStatusText.CountChanged(status.Stdout) : 0;
    }

    public Task<GitCommandResult> ResetAsync(string repoRoot, GitResetMode mode, string commitHash,
        CancellationToken cancellationToken)
        => _cli.RunAsync(new GitCommandRequest
        {
            Arguments = $"reset {GitResetDescriptions.Argument(mode)} {commitHash}",
            WorkingDirectory = repoRoot,
            Timeout = GitTimeouts.Default
        }, cancellationToken);

    /// <summary>git log 在空仓库上的报错文本判定（不同 git 版本措辞略有差异）。</summary>
    private static bool IsEmptyRepository(GitCommandResult result)
    {
        string text = result.ErrorText;
        return text.Contains("does not have any commits yet", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains("your current branch", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("no commits", StringComparison.OrdinalIgnoreCase)) ||
               text.Contains("unknown revision", StringComparison.OrdinalIgnoreCase);
    }
}
