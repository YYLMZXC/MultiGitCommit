namespace MultiGitCommit;

/// <summary>拉取分支更新时 git 如何处理本地已有提交（对应 pull 的三种策略参数）。</summary>
public enum PullStrategy
{
    /// <summary>仅允许快进（--ff-only）：本地没有分叉时最安全，分叉直接失败。</summary>
    FastForwardOnly,

    /// <summary>变基（--rebase）：把本地提交放到远端最新提交之上，历史保持线性。</summary>
    Rebase,

    /// <summary>合并（--no-rebase）：产生一个合并提交。</summary>
    Merge
}

/// <summary>拉取更新的输入（全部来自界面选择，不含任何 UI 类型）。</summary>
public sealed class PullRequest
{
    /// <summary>仓库根目录。</summary>
    public string RepoRoot { get; init; } = string.Empty;

    /// <summary>要拉取的远端 URL（与推送一致，直接用 URL，不依赖 git remote 别名）。</summary>
    public string RemoteUrl { get; init; } = string.Empty;

    /// <summary>当前分支名称。</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>本地与远端分叉时的处理策略。</summary>
    public PullStrategy Strategy { get; init; } = PullStrategy.FastForwardOnly;
}

/// <summary>拉取更新的结果。</summary>
public sealed class PullResult
{
    /// <summary>是否成功拉取。</summary>
    public bool Success { get; init; }

    /// <summary>是否被用户取消。</summary>
    public bool Cancelled { get; init; }

    /// <summary>是否因超时被终止。</summary>
    public bool TimedOut { get; init; }

    /// <summary>失败 / 取消时的说明；成功时为空。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 「从远端拉取更新」业务流程。
///
/// 与推送保持一致：直接使用配置里的远端 URL（<c>git pull &lt;url&gt; &lt;branch&gt;</c>），
/// 不改动 .git/config、不依赖 git remote 别名；https 远端按【Git 环境】里的账号来源注入凭据。
/// 工具为非交互执行，因此所有调用都带超时并可取消，失败时给出可操作的原因提示。
/// </summary>
public sealed class GitPullService
{
    private readonly ILoggedGitRunner _git;
    private readonly ISettingsProvider _settings;
    private readonly GitCredentialResolver _credentials;

    public GitPullService(ILoggedGitRunner gitRunner, ISettingsProvider settings)
    {
        _git = gitRunner;
        _settings = settings;
        _credentials = new GitCredentialResolver(settings);
    }

    /// <summary>执行一次拉取；过程与 git 输出实时写入 <paramref name="log"/>。</summary>
    public async Task<PullResult> PullAsync(PullRequest request, Action<string> log,
        CancellationToken cancellationToken = default)
    {
        log("========== 开始拉取更新 ==========");
        log($"远端：{GitCredentials.Mask(request.RemoteUrl)}");
        log($"分支：{request.Branch}（{DescribeStrategy(request.Strategy)}）");

        if (string.IsNullOrWhiteSpace(request.RemoteUrl) || string.IsNullOrWhiteSpace(request.Branch))
        {
            log("流程终止：缺少远端地址或分支。");
            return new PullResult { Message = "缺少远端地址或分支" };
        }

        string pullUrl = request.RemoteUrl;

        // https 远端：按账号来源取得凭据并注入 URL（与推送同样的处理）
        if (GitCredentials.NeedsCredential(pullUrl))
        {
            GitCredential? credential = await _credentials.ResolveAsync(pullUrl).ConfigureAwait(true);
            if (credential != null)
            {
                pullUrl = GitCredentials.Apply(pullUrl, credential);
                log(credential is DetectedCredential detected
                    ? $"已自动使用系统 git 凭据（{detected.Source}）：{GitCredentials.Mask(pullUrl)}"
                    : "已为拉取地址注入已保存的 git 凭据。");
            }
            else if (_settings.Current.CredentialSource != CredentialSourceMode.None)
            {
                log($"提示：{GitCredentials.Mask(pullUrl)} 未找到可用的凭据，https 远端的非交互拉取很可能失败" +
                    "（可在【Git 环境】中选择账号来源，并用【检测系统凭据…】导入或手动填写）。");
            }
        }

        // 配置的 credential.helper 在当前 git 里不可用时（如缺少 git-credential-manager），
        // 临时改用实际存在的助手，保证能从系统凭据库取到已保存的账号（不改动用户 git 配置）
        string helperArg = GitCredentialHelper.OverrideArgument() ?? string.Empty;
        string strategyArg = request.Strategy switch
        {
            PullStrategy.Rebase => "--rebase",
            PullStrategy.Merge => "--no-rebase",
            _ => "--ff-only"
        };

        string args = $"{helperArg}pull {strategyArg} {pullUrl} {request.Branch}";
        string label = $"git pull {strategyArg} {GitCredentials.Mask(pullUrl)} {request.Branch}";

        GitCommandResult result = await _git.RunAsync(request.RepoRoot, args, label,
            timeout: GitTimeouts.Default, cancellationToken: cancellationToken).ConfigureAwait(true);

        if (result.TimedOut)
        {
            log("拉取超时，已终止 git 进程。");
            return new PullResult { TimedOut = true, Message = "拉取超时" };
        }

        if (result.Cancelled)
        {
            log("拉取已取消。");
            return new PullResult { Cancelled = true, Message = "已取消" };
        }

        if (!result.Success)
        {
            log("[失败] 拉取未完成。");
            string suggestion = Suggest(result);
            if (suggestion.Length > 0) log(suggestion);
            return new PullResult { Message = result.ErrorText };
        }

        log("[成功] 拉取完成，已把远端最新提交同步到本地分支。");
        return new PullResult { Success = true };
    }

    /// <summary>策略的中文说明（日志展示用）。</summary>
    private static string DescribeStrategy(PullStrategy strategy) => strategy switch
    {
        PullStrategy.Rebase => "变基 --rebase",
        PullStrategy.Merge => "合并 --no-rebase",
        _ => "仅快进 --ff-only"
    };

    /// <summary>针对常见拉取失败给出可操作的建议（git 原始报错已由执行器逐行输出）。</summary>
    private static string Suggest(GitCommandResult result)
    {
        string text = result.ErrorText;

        if (text.Contains("Not possible to fast-forward", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("divergent branches", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("need to specify how to reconcile", StringComparison.OrdinalIgnoreCase))
        {
            return "提示：本地与远端已分叉。可改用【变基 --rebase】或【合并 --no-rebase】重新拉取。";
        }

        if (text.Contains("Your local changes", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Please commit your changes or stash them", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("would be overwritten by merge", StringComparison.OrdinalIgnoreCase))
        {
            return "提示：工作区有未提交改动且与远端改动冲突。请先提交或暂存（git stash）后再拉取。";
        }

        if (text.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "提示：远端认证失败。https 远端请在【Git 环境】中配置账号；ssh 远端请确认密钥已加载。";
        }

        if (text.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase))
        {
            return "提示：拉取产生冲突，请在仓库目录手动解决冲突后再继续。";
        }

        if (text.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase))
        {
            return "提示：远端地址或分支名可能不正确，也可能是网络不可达。";
        }

        return string.Empty;
    }
}
