namespace MultiGitCommit;

/// <summary>提交并推送流程的输入（全部来自界面选择，不含任何 UI 类型）。</summary>
public sealed class CommitPushRequest
{
    /// <summary>仓库根目录。</summary>
    public string RepoRoot { get; init; } = string.Empty;

    /// <summary>本项目配置的远端 URL 列表（逐个推送，一个失败不阻断后续）。</summary>
    public IReadOnlyList<string> RemoteUrls { get; init; } = Array.Empty<string>();

    /// <summary>是否先执行 <c>git add</c>（勾选后由 <see cref="SelectFilesToStage"/> 决定添加哪些文件）。</summary>
    public bool AddAll { get; init; }

    /// <summary>
    /// 选择性 add 的委托：把工作区变更列表交给宿主（界面弹窗勾选），返回要 add 的相对路径；
    /// 返回 null 表示用户取消，流程应中止。<see cref="AddAll"/> 为 true 且此委托为 null 时退回 <c>git add .</c>。
    /// </summary>
    public Func<IReadOnlyList<WorkingTreeFile>, Task<IReadOnlyList<string>?>>? SelectFilesToStage { get; init; }

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
    private readonly ISettingsProvider _settings;

    /// <summary>远端凭据解析（与拉取流程共用同一套账号来源逻辑）。</summary>
    private readonly GitCredentialResolver _credentials;

    public CommitPushService(ILoggedGitRunner gitRunner, GitIdentityService identityService, ISettingsProvider settings)
    {
        _git = gitRunner;
        _identity = identityService;
        _settings = settings;
        _credentials = new GitCredentialResolver(settings);
    }

    /// <summary>执行完整流程。</summary>
    /// <param name="request">流程输入。</param>
    /// <param name="log">日志输出（逐行）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<CommitPushResult> RunAsync(CommitPushRequest request, Action<string> log,
        CancellationToken cancellationToken = default)
    {
        string repoDir = request.RepoRoot;
        // 兼容旧配置里带引号 / 空白的 URL：统一清洗后再使用
        var urls = request.RemoteUrls
            .Select(GitCredentials.NormalizeUrl)
            .Where(url => url.Length > 0)
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

        // 3) 勾选【执行 git add】才添加文件；未勾选则跳过，由用户提前手动 add。
        //    界面提供了勾选回调时只添加用户勾选的文件（避免 git add . 把临时文件带进提交）。
        if (request.AddAll)
        {
            bool staged = await StageAsync(repoDir, request, log, cancellationToken).ConfigureAwait(true);
            if (!staged)
            {
                log("流程终止：已取消选择要添加的文件。");
                return CommitPushResult.Aborted("已取消选择要添加的文件", urls.Count, branch);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4) 判断是否真的有东西可提交：以【暂存区】为准。
        //    选择性 add 之后，未勾选的文件仍留在工作区，但本次不该被提交，
        //    因此不能只看 git status 是否为空（否则会生成一个空改动、直接失败的 commit）。
        GitCommandResult status = await _git.RunAsync(repoDir, "status --porcelain", "git status --porcelain",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        bool hasWorkTreeChanges = !string.IsNullOrWhiteSpace(status.Stdout);

        GitCommandResult stagedCheck = await _git.RunQuietAsync(repoDir, "diff --cached --quiet",
            "git diff --cached --quiet（检查暂存区）", GitTimeouts.Quick,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        // 退出码 0 = 暂存区无改动，1 = 有改动；其他退出码说明判定失败，退回按工作区状态判断
        bool hasStagedChanges = stagedCheck.ExitCode == 1
            ? true
            : stagedCheck.ExitCode == 0
                ? false
                : hasWorkTreeChanges;

        if (!hasStagedChanges && !request.ForceCommit)
        {
            log(hasWorkTreeChanges
                ? "暂存区没有待提交的改动（nothing to commit）：未勾选 / 未 add 的文件不会被提交，跳过 git commit。"
                : "工作区无任何变更（nothing to commit），跳过 git commit，不生成空提交。");
        }
        else
        {
            if (!hasStagedChanges)
                log("暂存区无改动，但已勾选【强制提交】，仍执行一次空提交（--allow-empty）。");

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

    /// <summary>
    /// 执行 <c>git add</c>，方式由宿主是否提供勾选回调决定：
    /// 1. 未提供回调（设置＝全提交）：直接 <c>git add .</c>；
    /// 2. 提供回调（设置＝可选提交）：读取工作区变更（<c>git status --porcelain -z --untracked-files=all</c>），
    ///    交给界面弹窗勾选，再只对勾选的路径执行 <c>git add --</c>（路径过多时分批，避免命令行过长）。
    ///
    /// 返回 false 表示用户取消（流程应中止）；工作区无变更时返回 true 但什么也不做。
    /// </summary>
    private async Task<bool> StageAsync(string repoDir, CommitPushRequest request, Action<string> log,
        CancellationToken cancellationToken)
    {
        // 设置里选了「全提交」：直接 git add .，不额外读取文件列表（少一次命令，也不弹窗）
        if (request.SelectFilesToStage == null)
        {
            log("文件添加方式：全提交（git add .，添加全部变更）。");
            GitCommandResult add = await _git.RunAsync(repoDir, "add .", "git add .",
                cancellationToken: cancellationToken).ConfigureAwait(true);
            if (!add.Success) log("git add . 执行失败，继续后续流程。");
            return true;
        }

        // 设置里选了「可选提交」：先读取工作区文件列表，再弹窗勾选
        log("文件添加方式：可选提交（弹窗勾选要添加的文件）。");
        IReadOnlyList<WorkingTreeFile>? files = await ReadWorkingTreeChangesAsync(repoDir, log, cancellationToken)
            .ConfigureAwait(true);

        if (files == null)
        {
            log("无法读取工作区文件列表，已跳过 git add（可稍后重试或手动执行 git add）。");
            return true;
        }

        if (files.Count == 0)
        {
            log("工作区没有可添加的文件（无任何变更）。");
            return true;
        }

        log($"工作区共 {files.Count} 个变更文件。");

        IReadOnlyList<string>? selected = await request.SelectFilesToStage(files).ConfigureAwait(true);
        if (selected == null) return false;

        List<string> paths = selected.Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
        {
            log("未勾选任何文件，跳过 git add（已暂存的内容保持不变）。");
            return true;
        }

        if (paths.Count < files.Count)
            log($"已勾选 {paths.Count} 个文件，其余 {files.Count - paths.Count} 个不添加。");

        return await AddFilesAsync(repoDir, paths, log, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>读取工作区变更列表（未跟踪的目录会展开到具体文件）；读取失败返回 null。</summary>
    public async Task<IReadOnlyList<WorkingTreeFile>?> ReadWorkingTreeChangesAsync(string repoDir, Action<string> log,
        CancellationToken cancellationToken = default)
    {
        GitCommandResult status = await _git.RunQuietAsync(repoDir,
            "status --porcelain -z --untracked-files=all",
            "git status --porcelain -z --untracked-files=all",
            GitTimeouts.Quick, cancellationToken: cancellationToken).ConfigureAwait(true);

        if (!status.Success)
        {
            log("读取工作区文件列表失败：请查看上方 git 输出。");
            return null;
        }

        return WorkingTreeFile.Parse(status.Stdout);
    }

    /// <summary>对指定路径执行 <c>git add --</c>；路径过多时分批，避免命令行超长。</summary>
    private async Task<bool> AddFilesAsync(string repoDir, IReadOnlyList<string> paths, Action<string> log,
        CancellationToken cancellationToken)
    {
        const int batchSize = 40;
        bool allSuccess = true;
        int done = 0;

        for (int start = 0; start < paths.Count; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<string> batch = paths.Skip(start).Take(batchSize).ToList();
            string args = "add -- " + string.Join(" ", batch.Select(GitArgument.Quote));
            string label = batch.Count == paths.Count
                ? $"git add -- {batch.Count} 个文件"
                : $"git add -- {batch.Count} 个文件（{done + 1}-{done + batch.Count}/{paths.Count}）";

            GitCommandResult add = await _git.RunAsync(repoDir, args, label, GitTimeouts.Default,
                cancellationToken: cancellationToken).ConfigureAwait(true);

            if (add.Success)
            {
                foreach (string path in batch) log("      已添加：" + path);
            }
            else
            {
                allSuccess = false;
                log("      本批 git add 执行失败，继续后续流程。");
            }

            done += batch.Count;
        }

        return allSuccess;
    }

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
                GitCredential? credential = await _credentials.ResolveAsync(url).ConfigureAwait(true);
                if (credential != null)
                {
                    pushUrl = GitCredentials.Apply(url, credential);
                    log(credential is DetectedCredential detected
                        ? $"已自动使用系统 git 凭据（{detected.Source}）：{GitCredentials.Mask(pushUrl)}"
                        : $"已为 {GitCredentials.Mask(pushUrl)} 注入已保存的推送凭据。");
                }
                else if (_settings.Current.CredentialSource != CredentialSourceMode.None)
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

}
