namespace MultiGitCommit;

/// <summary>提交并推送流程的输入（全部来自界面选择，不含任何 UI 类型）。</summary>
public sealed class CommitPushRequest
{
    /// <summary>仓库根目录。</summary>
    public string RepoRoot { get; init; } = string.Empty;

    /// <summary>本项目配置的远端 URL 列表（逐个推送，一个失败不阻断后续）。</summary>
    public IReadOnlyList<string> RemoteUrls { get; init; } = Array.Empty<string>();

    /// <summary>是否先执行 <c>git add .</c>。</summary>
    public bool AddAll { get; init; }

    /// <summary>工作区无变更时是否仍生成空提交（--allow-empty）。</summary>
    public bool ForceCommit { get; init; }

    /// <summary>是否强制推送（--force，会覆盖远端同名分支历史）。</summary>
    public bool ForcePush { get; init; }

    /// <summary>推送时是否同时推送标签（--tags）。</summary>
    public bool PushTags { get; init; }

    /// <summary>用户填写的提交备注（拼在时间戳之后）。</summary>
    public string Remark { get; init; } = string.Empty;

    /// <summary>
    /// 提交身份缺失时的补全委托（由宿主弹窗引导用户填写），返回 true 表示已补全可继续。
    /// 这样流程服务不必知道「身份是怎么问出来的」。
    /// </summary>
    public Func<string, Task<bool>>? EnsureIdentity { get; init; }

    /// <summary>分支解析成功后的回调（界面用于显示当前分支）。</summary>
    public Action<string>? OnBranchDetected { get; init; }
}

/// <summary>提交并推送流程的结果。</summary>
public sealed class CommitPushResult
{
    /// <summary>流程是否完整跑完（推送阶段有远端失败也算跑完，看 <see cref="FailedRemotes"/>）。</summary>
    public bool Completed { get; init; }

    /// <summary>中止原因；为 null 表示未中止。</summary>
    public string? AbortReason { get; init; }

    /// <summary>当前分支；未能解析时为 null。</summary>
    public string? Branch { get; init; }

    /// <summary>远端总数。</summary>
    public int TotalRemotes { get; init; }

    /// <summary>推送失败的远端数量。</summary>
    public int FailedRemotes { get; init; }

    /// <summary>是否全部远端都推送成功。</summary>
    public bool AllRemotesSucceeded => Completed && TotalRemotes > 0 && FailedRemotes == 0;

    public static CommitPushResult Aborted(string reason, int totalRemotes = 0, string? branch = null)
        => new() { Completed = false, AbortReason = reason, TotalRemotes = totalRemotes, Branch = branch };
}

/// <summary>
/// 「提交 + 推送全部远端」业务流程。
///
/// 抽取原因：这段流程原本压在 <c>MainWindow.BtnCommitPush_Click</c> 里（160 余行），
/// git 命令、凭据注入、日志文案、按钮状态混在一起，既无法测试也无法复用。
/// 现在它只依赖三个抽象：命令执行、提交身份、应用设置；
/// 界面负责收集参数、显示日志、在需要时弹窗补全身份。
/// </summary>
public sealed class CommitPushService
{
    private readonly ILoggedGitRunner _git;
    private readonly GitIdentityService _identity;
    private readonly AppSettings _settings;

    public CommitPushService(ILoggedGitRunner gitRunner, GitIdentityService identityService, AppSettings settings)
    {
        _git = gitRunner;
        _identity = identityService;
        _settings = settings;
    }

    /// <summary>执行完整流程。</summary>
    /// <param name="request">流程输入。</param>
    /// <param name="log">日志输出（逐行）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<CommitPushResult> RunAsync(CommitPushRequest request, Action<string> log,
        CancellationToken cancellationToken = default)
    {
        string repoDir = request.RepoRoot;
        var urls = request.RemoteUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct()
            .ToList();

        log("========== 开始提交并推送流程 ==========");

        // 1) 自动获取当前分支名称（禁止硬编码 main/master）
        string? branch = await GetCurrentBranchAsync(repoDir, log).ConfigureAwait(true);
        if (branch == null)
        {
            log("流程终止：无法获取当前分支。");
            return CommitPushResult.Aborted("无法获取当前分支", urls.Count);
        }

        request.OnBranchDetected?.Invoke(branch);

        // 2) 远端列表为空则无需推送
        if (urls.Count == 0)
        {
            log("尚未配置推送远端地址，请添加远端URL");
            log("流程终止。");
            return CommitPushResult.Aborted("尚未配置推送远端地址", 0, branch);
        }

        // 3) 勾选【git add .】才执行；未勾选则跳过，由用户提前手动 add
        if (request.AddAll)
        {
            GitCommandResult add = await _git.RunAsync(repoDir, "add .", "git add .", cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            if (!add.Success) log("git add . 执行失败，继续后续流程。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4) 用 git status 判断工作区状态：无变更则跳过 commit，不生成空提交
        GitCommandResult status = await _git.RunAsync(repoDir, "status --porcelain", "git status --porcelain",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        bool hasChanges = !string.IsNullOrWhiteSpace(status.Stdout);

        if (!hasChanges && !request.ForceCommit)
        {
            log("工作区无任何变更（nothing to commit），跳过 git commit，不生成空提交。");
        }
        else
        {
            if (!hasChanges)
                log("工作区无变更，但已勾选【强制提交】，仍执行一次空提交（--allow-empty）。");

            // 4.5) 提交前置条件：必须存在 user.name / user.email，否则 git 生成不了 commit
            if (!await EnsureIdentityAsync(repoDir, request, log).ConfigureAwait(true))
            {
                log("流程终止：未设置提交身份（user.name / user.email），无法生成 commit。");
                return CommitPushResult.Aborted("未设置提交身份", urls.Count, branch);
            }

            if (!await CommitAsync(repoDir, request, log, cancellationToken).ConfigureAwait(true))
                log("git commit 执行失败，继续尝试推送已提交的内容。");
        }

        // 5) 循环推送每个远端；一个失败不阻断后续
        (bool allSuccess, int failed) = await PushAllAsync(repoDir, urls, branch, request, log, cancellationToken)
            .ConfigureAwait(true);

        log(allSuccess
            ? "========== 全部远端推送成功 =========="
            : "========== 推送流程结束（存在失败项，请查看上方日志） ==========");

        return new CommitPushResult
        {
            Completed = true,
            Branch = branch,
            TotalRemotes = urls.Count,
            FailedRemotes = failed
        };
    }

    /// <summary>获取当前分支名称（<c>git branch --show-current</c>，detached HEAD 时退回 symbolic-ref）。</summary>
    public async Task<string?> GetCurrentBranchAsync(string repoDir, Action<string> log)
    {
        GitCommandResult result = await _git.RunAsync(repoDir, "branch --show-current", "git branch --show-current")
            .ConfigureAwait(true);

        string branch = result.Stdout.Trim();
        if (result.Success && branch.Length > 0) return branch;

        GitCommandResult fallback = await _git.RunAsync(repoDir, "symbolic-ref --short HEAD", "git symbolic-ref --short HEAD")
            .ConfigureAwait(true);

        string shortName = fallback.Stdout.Trim();
        if (fallback.Success && shortName.Length > 0) return shortName;

        log("获取当前分支失败（可能处于 detached HEAD 状态）。");
        return null;
    }

    // ==================== 步骤实现 ====================

    /// <summary>身份校验：已配置直接通过；否则交给宿主补全（弹窗 / 套用已保存身份）。</summary>
    private async Task<bool> EnsureIdentityAsync(string repoDir, CommitPushRequest request, Action<string> log)
    {
        var (name, email) = _identity.Read(repoDir);
        if (name.Length > 0 && email.Length > 0)
        {
            log($"提交身份：{name} <{email}>");
            return true;
        }

        if (request.EnsureIdentity == null) return false;
        return await request.EnsureIdentity(repoDir).ConfigureAwait(true);
    }

    /// <summary>组装并执行 commit：提交消息固定为「yyyy-MM-dd HH:mm:ss 用户备注」。</summary>
    private async Task<bool> CommitAsync(string repoDir, CommitPushRequest request, Action<string> log,
        CancellationToken cancellationToken)
    {
        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string remark = request.Remark.Trim();
        string commitMessage = remark.Length == 0 ? time : $"{time} {remark}";

        // 对消息内双引号做转义，防止命令行语法报错
        string escapedMessage = commitMessage.Replace("\"", "\\\"");
        log($"提交消息：{commitMessage}");

        string forceFlag = request.ForceCommit ? " --allow-empty" : string.Empty;
        string commitArgs = "commit -m \"" + escapedMessage + "\"" + forceFlag;
        string commitLabel = "git commit -m \"...\"" + forceFlag;

        GitCommandResult commit = await _git.RunAsync(repoDir, commitArgs, commitLabel,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        return commit.Success;
    }

    /// <summary>逐个远端推送，返回「是否全部成功 / 失败数量」。</summary>
    private async Task<(bool AllSuccess, int Failed)> PushAllAsync(string repoDir, List<string> urls, string branch,
        CommitPushRequest request, Action<string> log, CancellationToken cancellationToken)
    {
        // 存在 https 远端时，先说明凭据助手状态（助手不可用时会导致"取不到账号"的典型故障）
        if (urls.Any(GitCredentials.NeedsCredential))
            log("凭据助手：" + GitCredentialHelper.Describe());

        bool allSuccess = true;
        int failed = 0;

        if (request.ForcePush)
            log("警告：已勾选【强制推送 --force】，将覆盖远端同名分支的提交历史。");

        foreach (string url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string forceFlag = request.ForcePush ? " --force" : string.Empty;
            string tagsFlag = request.PushTags ? " --tags" : string.Empty;

            // https 远端：按【Git 环境】里选择的账号来源取得凭据并注入 URL。
            // 工具为非交互执行（GIT_TERMINAL_PROMPT=0），无凭据时 git 不能弹窗问账号，推送必然失败。
            string pushUrl = url;
            if (GitCredentials.NeedsCredential(url))
            {
                GitCredential? credential = await ResolveCredentialAsync(url).ConfigureAwait(true);
                if (credential != null)
                {
                    pushUrl = GitCredentials.Apply(url, credential);
                    log(credential is DetectedCredential detected
                        ? $"已自动使用系统 git 凭据（{detected.Source}）：{GitCredentials.Mask(pushUrl)}"
                        : $"已为 {GitCredentials.Mask(pushUrl)} 注入已保存的推送凭据。");
                }
                else if (_settings.CredentialSource != CredentialSourceMode.None)
                {
                    log($"提示：{GitCredentials.Mask(url)} 未找到可用的推送凭据，https 远端的非交互推送很可能失败" +
                        "（可在【Git 环境】中选择账号来源，并用【检测系统凭据…】导入或手动填写）。");
                }
            }

            // 日志一律脱敏，令牌不落日志
            string displayUrl = GitCredentials.Mask(pushUrl);

            // 配置的 credential.helper 在当前 git 里不可用时（如缺少 git-credential-manager），
            // 临时改用实际存在的助手，保证能从系统凭据库取到已保存的账号（不改动用户 git 配置）
            string helperArg = GitCredentialHelper.OverrideArgument() ?? string.Empty;
            string pushArgs = $"{helperArg}push{forceFlag} {pushUrl} {branch}{tagsFlag}";
            string pushLabel = $"git push{forceFlag} {displayUrl} {branch}{tagsFlag}";

            log($"--- 推送到：{displayUrl}（分支：{branch}{(request.ForcePush ? "，强制推送" : string.Empty)}）---");

            GitCommandResult push = await _git.RunAsync(repoDir, pushArgs, pushLabel,
                cancellationToken: cancellationToken).ConfigureAwait(true);

            if (push.Success)
            {
                log($"[成功] 推送到：{url}");
            }
            else
            {
                allSuccess = false;
                failed++;
                log($"[失败] 推送到：{url}（继续推送其余远端）");
            }
        }

        return (allSuccess, failed);
    }

    /// <summary>
    /// 按【Git 环境】中选择的账号来源取得该远端的推送凭据：
    /// Auto 自定义优先、缺失时自动取系统凭据；System 只用系统凭据；Custom 只用自定义；None 返回 null。
    /// 返回 <see cref="DetectedCredential"/> 表示来自系统，普通 <see cref="GitCredential"/> 表示来自自定义列表。
    /// </summary>
    private async Task<GitCredential?> ResolveCredentialAsync(string url)
    {
        CredentialSourceMode mode = _settings.CredentialSource;
        if (mode == CredentialSourceMode.None) return null;

        if (mode != CredentialSourceMode.System)
        {
            GitCredential? custom = GitCredentials.Find(url, _settings.Credentials);
            if (custom != null) return custom;
        }

        if (mode == CredentialSourceMode.Custom) return null;

        // 系统凭据：凭据管理器 / 钥匙串 / ~/.git-credentials / credential helper（短超时，不会卡住）
        string helperArg = GitCredentialHelper.OverrideArgument() ?? string.Empty;
        return await SystemCredentialProbe.ProbeAsync(
            GitCredentials.GetHost(url), GitLocator.GitExe, default, helperArg).ConfigureAwait(true);
    }
}
