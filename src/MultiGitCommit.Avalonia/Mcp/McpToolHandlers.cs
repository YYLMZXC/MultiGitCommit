using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MultiGitCommit;

namespace MultiGitCommit.Mcp;

/// <summary>
/// 各 MCP 工具的业务实现。全部通过 AppServices / AppSession 组合根调用 GUI 项目的同一套服务，
/// 不重新实现 git 命令，也不读写凭据：设置与每仓库配置都与界面版完全一致。
/// </summary>
internal static class McpToolHandlers
{
    private const int MaxRemoteCount = 20;

    // ==================== 只读工具 ====================

    public static Task<JsonNode> GetGitEnvironmentAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        => Task.FromResult<JsonNode>(new JsonObject
        {
            ["ok"] = true,
            ["appVersion"] = AppVersion.Current,
            ["git"] = new JsonObject
            {
                ["available"] = ctx.Session.GitEnvironment.IsAvailable,
                ["exe"] = ctx.Session.GitEnvironment.GitExe,
                ["exePath"] = GitLocator.Current.GitExePath,
                ["description"] = GitLocator.Current.Description,
                ["sshExe"] = GitLocator.Current.SshExe
            },
            ["settingsFile"] = ctx.Settings.SettingsFilePath,
            ["settings"] = new JsonObject
            {
                ["gitSource"] = ctx.Settings.GitSource.ToString(),
                ["customGitPath"] = ctx.Settings.CustomGitPath,
                ["credentialSource"] = ctx.Settings.CredentialSource.ToString(),
                ["savedCredentialCount"] = ctx.Settings.Credentials.Count,
                ["lastRepoPath"] = ctx.Settings.LastRepoPath,
                ["startupRepo"] = ctx.Settings.StartupRepo.ToString(),
                ["stageMode"] = ctx.Settings.StageMode.ToString()
            },
            ["mcp"] = new JsonObject
            {
                ["transport"] = "stdio",
                ["protocolVersion"] = McpProtocol.LatestProtocolVersion,
                ["readOnly"] = ctx.ReadOnly
            }
        });

    public static Task<JsonNode> ResolveRepoAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        ProjectConfig config = ctx.Config(root);

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["ok"] = true,
            ["repoRoot"] = root,
            ["configFile"] = config.ConfigFilePath,
            ["hasConfig"] = File.Exists(config.ConfigFilePath),
            ["remoteCount"] = config.RemoteUrls.Count
        });
    }

    public static Task<JsonNode> FindReposAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = Args.Str(args, "root") ?? Environment.CurrentDirectory;
        try
        {
            root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root.Trim()));
        }
        catch (Exception ex)
        {
            throw new ToolUsageException($"扫描目录无效：{root}（{ex.Message}）");
        }

        if (!Directory.Exists(root)) throw new ToolUsageException($"扫描目录不存在：{root}");

        int maxDepth = Args.Int(args, "max_depth", 2, 0, 5);
        int limit = Args.Int(args, "limit", 50, 1, 200);

        // 扫描算法统一在 RepoScanner（仓库管理首页共用同一实现）
        IReadOnlyList<string> found = RepoScanner.Scan(root, maxDepth, limit, ct);

        var repositories = new JsonArray();
        foreach (string dir in found)
        {
            repositories.Add(new JsonObject
            {
                ["path"] = dir,
                ["hasConfig"] = File.Exists(Path.Combine(dir, ProjectConfig.ConfigFileName))
            });
        }

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["ok"] = true,
            ["root"] = root,
            ["maxDepth"] = maxDepth,
            ["count"] = repositories.Count,
            ["truncated"] = found.Count >= limit,
            ["repositories"] = repositories
        });
    }

    public static async Task<JsonNode> GetRepoStatusAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        int maxFiles = Args.Int(args, "max_files", 200, 1, 1000);

        string? branch = await ctx.Session.CommitPush.GetCurrentBranchAsync(root, ctx.Log.Add).ConfigureAwait(false);
        IReadOnlyList<WorkingTreeFile>? files = await ctx.Session.CommitPush
            .ReadWorkingTreeChangesAsync(root, ctx.Log.Add, ct).ConfigureAwait(false);

        var items = new JsonArray();
        if (files != null)
        {
            foreach (WorkingTreeFile file in files.Take(maxFiles))
            {
                items.Add(new JsonObject
                {
                    ["status"] = file.StatusLabel,
                    ["path"] = file.Path,
                    ["originalPath"] = file.OriginalPath,
                    ["staged"] = file.IsStaged,
                    ["unstaged"] = file.HasUnstagedChange,
                    ["untracked"] = file.IsUntracked
                });
            }
        }

        (string name, string email) = ctx.Session.Identity.Read(root);
        ProjectConfig config = ctx.Config(root);

        return new JsonObject
        {
            ["ok"] = files != null,
            ["repoRoot"] = root,
            ["branch"] = branch,
            ["changedFileCount"] = files?.Count ?? -1,
            ["filesTruncated"] = files?.Count > maxFiles,
            ["files"] = items,
            ["identity"] = new JsonObject
            {
                ["name"] = name,
                ["email"] = email,
                ["complete"] = !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(email)
            },
            ["config"] = ConfigSummary(config)
        };
    }

    public static Task<JsonNode> GetConfigAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        ProjectConfig config = ctx.Config(root);
        return Task.FromResult<JsonNode>(ConfigDetail(root, config));
    }

    public static async Task<JsonNode> GetHistoryAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        string? hash = Args.Str(args, "commit_hash");

        if (!string.IsNullOrWhiteSpace(hash))
        {
            if (!Regex.IsMatch(hash.Trim(), "^[0-9a-fA-F]{4,64}$"))
                throw new ToolUsageException("commit_hash 必须是 4～64 位十六进制提交哈希");

            GitCommandResult changed = await ctx.Session.History
                .LoadChangedFilesAsync(root, hash.Trim(), ct).ConfigureAwait(false);

            return new JsonObject
            {
                ["ok"] = changed.Success,
                ["repoRoot"] = root,
                ["commitHash"] = hash.Trim(),
                ["stdout"] = McpJson.Truncate(changed.Stdout),
                ["stderr"] = McpJson.Truncate(changed.Stderr)
            };
        }

        int limit = Args.Int(args, "limit", 20, 1, 200);
        HistoryLoadResult history = await ctx.Session.History.LoadCommitsAsync(root, limit, ct).ConfigureAwait(false);

        var commits = new JsonArray();
        foreach (GitCommit commit in history.Commits)
        {
            commits.Add(new JsonObject
            {
                ["hash"] = commit.Hash,
                ["shortHash"] = commit.ShortHash,
                ["date"] = commit.Date,
                ["author"] = commit.Author,
                ["subject"] = commit.Subject
            });
        }

        return new JsonObject
        {
            ["ok"] = history.Success,
            ["repoRoot"] = root,
            ["count"] = commits.Count,
            ["emptyRepository"] = history.EmptyRepository,
            ["timedOut"] = history.TimedOut,
            ["error"] = McpJson.Truncate(history.Error, 8 * 1024),
            ["commits"] = commits
        };
    }

    public static Task<JsonNode> GetIdentityAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        (string name, string email) = ctx.Session.Identity.Read(root);
        GitIdentity saved = ctx.Settings.CommitIdentity;

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["ok"] = true,
            ["repoRoot"] = root,
            ["effective"] = new JsonObject
            {
                ["name"] = name,
                ["email"] = email,
                ["complete"] = !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(email)
            },
            ["savedInApp"] = new JsonObject
            {
                ["name"] = saved.Name,
                ["email"] = saved.Email,
                ["scope"] = saved.Scope.ToString(),
                ["complete"] = saved.IsComplete
            }
        });
    }

    public static async Task<JsonNode> RunGitAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string gitArgs = Args.Required(args, "git_args");
        ValidateReadOnlyGitArgs(gitArgs);
        string root = ctx.Repo(args);

        GitCommandResult result = await ctx.Session.Cli.RunAsync(new GitCommandRequest
        {
            Arguments = gitArgs.Trim(),
            WorkingDirectory = root,
            Timeout = GitTimeouts.Quick
        }, ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["ok"] = result.Success,
            ["repoRoot"] = root,
            ["exitCode"] = result.ExitCode,
            ["timedOut"] = result.TimedOut,
            ["cancelled"] = result.Cancelled,
            ["stdout"] = McpJson.Truncate(result.Stdout),
            ["stderr"] = McpJson.Truncate(result.Stderr)
        };
    }

    // ==================== 写工具（调用前由 McpToolSet 统一检查 confirm） ====================

    public static Task<JsonNode> SetConfigAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        ProjectConfig config = ctx.Config(root);

        if (Args.Strings(args, "remote_urls") is { } urls)
        {
            if (urls.Length > MaxRemoteCount)
                throw new ToolUsageException($"远端数量超过上限（最多 {MaxRemoteCount} 个）");
            config.RemoteUrls = NormalizeUrls(urls, rejectEmbeddedCredentials: true);
        }

        if (Args.BoolOrNull(args, "check_add_all") is bool addAll) config.CheckAddAll = addAll;
        if (Args.BoolOrNull(args, "check_push_tags") is bool tags) config.CheckPushTags = tags;
        if (Args.BoolOrNull(args, "check_force_commit") is bool forceCommit) config.CheckForceCommit = forceCommit;
        if (Args.BoolOrNull(args, "check_force_push") is bool forcePush) config.CheckForcePush = forcePush;

        ctx.Session.ProjectConfigs.Save(config);
        ctx.Log.Add($"已保存配置：{config.ConfigFilePath}");

        JsonNode detail = ConfigDetail(root, config);
        ((JsonObject)detail)["saved"] = true;
        return Task.FromResult(detail);
    }

    public static async Task<JsonNode> CommitAndPushAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        ProjectConfig config = ctx.Config(root);

        string remark = Args.Str(args, "remark") ?? string.Empty;
        if (remark.Length > 500 || remark.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ToolUsageException("remark 最长 500 字符，且不能包含换行");

        List<string> urls = Args.Strings(args, "remote_urls") is { } overrideUrls
            ? NormalizeUrls(overrideUrls, rejectEmbeddedCredentials: false)
            : NormalizeUrls(config.RemoteUrls, rejectEmbeddedCredentials: false);
        if (urls.Count == 0)
            throw new ToolUsageException("没有可用的推送远端：请先用 set_config 配置，或在本次调用里传 remote_urls");

        bool addAll = Args.BoolOrNull(args, "add_all") ?? config.CheckAddAll;
        Func<IReadOnlyList<WorkingTreeFile>, Task<IReadOnlyList<string>?>>? selector = null;

        if (Args.Strings(args, "files") is { Length: > 0 } selected)
        {
            foreach (string relative in selected) ValidateRepoRelativePath(root, relative);
            selector = _ => Task.FromResult<IReadOnlyList<string>?>(selected);
            addAll = true;
        }

        Func<string, Task<bool>>? ensureIdentity = null;
        if (Args.Bool(args, "apply_saved_identity"))
        {
            GitIdentity saved = ctx.Settings.CommitIdentity;
            ensureIdentity = dir => Task.FromResult(saved.IsComplete &&
                                                   ctx.Session.Identity.TryApply(saved, dir, out _));
        }

        bool pushTags = Args.BoolOrNull(args, "push_tags") ?? config.CheckPushTags;
        bool forceCommit = Args.BoolOrNull(args, "force_commit") ?? config.CheckForceCommit;
        string? detectedBranch = null;

        CommitPushResult result = await ctx.Session.CommitPush.RunAsync(new CommitPushRequest
        {
            RepoRoot = root,
            RemoteUrls = urls,
            AddAll = addAll,
            SelectFilesToStage = selector,
            ForceCommit = forceCommit,
            // MCP 永不启用强制推送：即使配置文件里勾选了也不生效
            ForcePush = false,
            PushTags = pushTags,
            Remark = remark,
            EnsureIdentity = ensureIdentity,
            OnBranchDetected = branch => detectedBranch = branch
        }, ctx.Log.Add, ct).ConfigureAwait(false);

        bool success = result.Completed && result.FailedRemotes == 0 && result.AbortReason == null;
        return new JsonObject
        {
            ["ok"] = success,
            ["repoRoot"] = root,
            ["completed"] = result.Completed,
            ["abortReason"] = McpJson.Mask(result.AbortReason),
            ["branch"] = result.Branch ?? detectedBranch,
            ["totalRemotes"] = result.TotalRemotes,
            ["failedRemotes"] = result.FailedRemotes,
            ["allRemotesSucceeded"] = result.AllRemotesSucceeded,
            ["options"] = new JsonObject
            {
                ["addAll"] = addAll,
                ["selectedFiles"] = selector != null,
                ["pushTags"] = pushTags,
                ["forceCommit"] = forceCommit,
                ["forcePush"] = false,
                ["remark"] = remark
            }
        };
    }

    public static async Task<JsonNode> PullAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        ProjectConfig config = ctx.Config(root);

        string url = GitCredentials.NormalizeUrl(Args.Str(args, "remote_url") ?? config.RemoteUrls.FirstOrDefault());
        if (url.Length == 0)
            throw new ToolUsageException("缺少拉取地址：请先配置远端，或传 remote_url");

        string? branch = await ctx.Session.CommitPush.GetCurrentBranchAsync(root, ctx.Log.Add).ConfigureAwait(false);
        if (branch == null) throw new ToolUsageException("无法获取当前分支，拉取已中止");

        PullStrategy strategy = (Args.Str(args, "strategy") ?? "ff_only").Trim().ToLowerInvariant() switch
        {
            "ff_only" or "fastforwardonly" or "" => PullStrategy.FastForwardOnly,
            "rebase" => PullStrategy.Rebase,
            "merge" => PullStrategy.Merge,
            _ => throw new ToolUsageException("strategy 只能是 ff_only / rebase / merge")
        };

        PullResult result = await ctx.Session.Pull.PullAsync(new PullRequest
        {
            RepoRoot = root,
            RemoteUrl = url,
            Branch = branch,
            Strategy = strategy
        }, ctx.Log.Add, ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["ok"] = result.Success,
            ["repoRoot"] = root,
            ["remote"] = McpJson.Mask(url),
            ["branch"] = branch,
            ["strategy"] = strategy.ToString(),
            ["success"] = result.Success,
            ["cancelled"] = result.Cancelled,
            ["timedOut"] = result.TimedOut,
            ["message"] = McpJson.Truncate(result.Message, 8 * 1024)
        };
    }

    public static async Task<JsonNode> CloneRepoAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string parentDir = ExpandDirectory(Args.Required(args, "parent_dir"));
        string url = GitCredentials.NormalizeUrl(Args.Required(args, "url"));
        if (url.Length == 0 || url.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ToolUsageException("url 不是合法的远端地址");
        RejectEmbeddedCredentials(url);

        string targetDir = Args.Required(args, "target_dir");
        if (targetDir is "." or ".." || Path.IsPathRooted(targetDir) ||
            targetDir.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            throw new ToolUsageException("target_dir 只能是单个目录名，不能包含路径分隔符");

        string? branch = Args.Str(args, "branch");
        if (!string.IsNullOrWhiteSpace(branch) && !GitRefName.IsValidBranchName(branch.Trim()))
            throw new ToolUsageException($"分支名不合法：{branch}");

        string targetPath = Path.Combine(parentDir, targetDir);
        if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"目标目录已存在且非空：{targetPath}"
            };

        Directory.CreateDirectory(parentDir);
        string gitArgs = GitCloneArguments.Build(branch, Args.Bool(args, "recursive"), Args.Bool(args, "shallow"),
            url, targetPath);
        string label = $"git clone {McpJson.Mask(url)} → {targetPath}";

        GitCommandResult result = await ctx.Session.Repositories
            .CloneAsync(parentDir, gitArgs, label).ConfigureAwait(false);

        return new JsonObject
        {
            ["ok"] = result.Success,
            ["parentDir"] = parentDir,
            ["targetPath"] = targetPath,
            ["branch"] = branch,
            ["exitCode"] = result.ExitCode,
            ["timedOut"] = result.TimedOut,
            ["stdout"] = McpJson.Truncate(result.Stdout, 16 * 1024),
            ["stderr"] = McpJson.Truncate(result.Stderr, 16 * 1024)
        };
    }

    public static async Task<JsonNode> InitRepoAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string path = ExpandDirectory(Args.Required(args, "path"));
        if (File.Exists(path)) throw new ToolUsageException($"目标路径是文件，不能初始化仓库：{path}");
        if (RepoScanner.IsRepoRoot(path)) throw new ToolUsageException($"目录已经是 Git 仓库：{path}");

        Directory.CreateDirectory(path);
        GitCommandResult init = await ctx.Session.Repositories.InitAsync(path).ConfigureAwait(false);
        if (!init.Success)
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "git init 失败：" + McpJson.Truncate(init.ErrorText, 8 * 1024)
            };

        string? defaultBranch = Args.Str(args, "default_branch");
        string branchMessage = "未设置默认分支";
        if (!string.IsNullOrWhiteSpace(defaultBranch))
        {
            if (!GitRefName.IsValidBranchName(defaultBranch.Trim()))
                throw new ToolUsageException($"默认分支名不合法：{defaultBranch}");

            SetDefaultBranchResult branchResult = await ctx.Session.Repositories
                .SetDefaultBranchAsync(path, defaultBranch.Trim()).ConfigureAwait(false);
            if (!branchResult.Success)
                return new JsonObject { ["ok"] = false, ["error"] = branchResult.Message };
            branchMessage = branchResult.Message;
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["repoRoot"] = path,
            ["defaultBranch"] = defaultBranch,
            ["message"] = branchMessage
        };
    }

    public static Task<JsonNode> SetIdentityAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        string root = ctx.Repo(args);
        string name = Args.Required(args, "name");
        string email = Args.Required(args, "email");
        if (name.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || email.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ToolUsageException("name / email 不能包含换行");

        IdentityScope scope = (Args.Str(args, "scope") ?? "local").Trim().ToLowerInvariant() switch
        {
            "local" or "" => IdentityScope.Local,
            "global" => IdentityScope.Global,
            _ => throw new ToolUsageException("scope 只能是 local / global")
        };

        var identity = new GitIdentity { Name = name, Email = email, Scope = scope };
        bool ok = ctx.Session.Identity.TryApply(identity, root, out string error);

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["ok"] = ok,
            ["repoRoot"] = root,
            ["name"] = name,
            ["email"] = email,
            ["scope"] = scope.ToString(),
            ["error"] = McpJson.Truncate(error, 8 * 1024)
        });
    }

    // ==================== 内部辅助 ====================

    private static JsonObject ConfigDetail(string repoRoot, ProjectConfig config)
    {
        var summary = ConfigSummary(config);
        summary["repoRoot"] = repoRoot;
        return summary;
    }

    private static JsonObject ConfigSummary(ProjectConfig config) => new()
    {
        ["ok"] = true,
        ["configFile"] = config.ConfigFilePath,
        ["hasConfig"] = File.Exists(config.ConfigFilePath),
        ["remoteUrls"] = MaskedUrls(config.RemoteUrls),
        ["checkAddAll"] = config.CheckAddAll,
        ["checkPushTags"] = config.CheckPushTags,
        ["checkForceCommit"] = config.CheckForceCommit,
        ["checkForcePush"] = config.CheckForcePush
    };

    private static JsonArray MaskedUrls(IEnumerable<string> urls)
    {
        var array = new JsonArray();
        foreach (string url in urls) array.Add(McpJson.Mask(url));
        return array;
    }

    private static List<string> NormalizeUrls(IEnumerable<string> urls, bool rejectEmbeddedCredentials)
    {
        var result = new List<string>();
        foreach (string raw in urls)
        {
            string url = GitCredentials.NormalizeUrl(raw);
            if (url.Length == 0) continue;
            if (url.Contains(":***@", StringComparison.Ordinal))
                throw new ToolUsageException("远端 URL 含脱敏占位符 ***，请提供真实地址（不要用 get_config 返回的脱敏值回写）");
            if (rejectEmbeddedCredentials) RejectEmbeddedCredentials(url);
            if (!result.Contains(url, StringComparer.Ordinal)) result.Add(url);
            if (result.Count >= MaxRemoteCount) break;
        }

        return result;
    }

    /// <summary>禁止把带 user:token@ 的 https URL 写进配置或回显给 AI（令牌应放在软件设置里）。</summary>
    private static void RejectEmbeddedCredentials(string url)
    {
        bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isHttp && !GitCredentials.NeedsCredential(url))
            throw new ToolUsageException("远端 URL 不要内嵌用户名 / 令牌；凭据请在软件的【Git 环境】中配置");
    }

    private static string ExpandDirectory(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception ex)
        {
            throw new ToolUsageException($"目录路径无效：{path}（{ex.Message}）");
        }
    }

    private static void ValidateRepoRelativePath(string repoRoot, string relative)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(repoRoot, relative));
        }
        catch (Exception ex)
        {
            throw new ToolUsageException($"文件路径无效：{relative}（{ex.Message}）");
        }

        string prefix = repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new ToolUsageException($"files 只能是仓库内的相对路径：{relative}");
    }

    // ==================== run_git 只读白名单 ====================

    private static readonly HashSet<string> AllowedGitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "branch", "tag", "rev-parse", "rev-list", "ls-files",
        "ls-tree", "ls-remote", "remote", "config", "describe", "blame", "for-each-ref",
        "cat-file", "merge-base", "shortlog", "reflog", "count-objects", "name-rev"
    };

    private static readonly string[] ForbiddenGitOptions =
    {
        "-c", "--config", "--exec", "--upload-pack", "--receive-pack", "--work-tree",
        "--git-dir", "--output", "--ext-diff", "--delete", "--move", "-c=", "--namespace"
    };

    /// <summary>
    /// 手动 git 命令的安全约束：只允许只读子命令；拒绝 shell 元字符、git -c 等能触发
    /// 外部程序 / 写操作的选项；branch / tag / remote / config 再逐项收紧到只读用法。
    /// GitCliRunner 本身不经过 shell，但仍要防止「借 git 命令绕过 MCP 的 confirm 闸」。
    /// </summary>
    private static void ValidateReadOnlyGitArgs(string text)
    {
        if (text.IndexOfAny(new[] { ';', '|', '&', '>', '<', '`', '\n', '\r', '\0' }) >= 0)
            throw new ToolUsageException("git_args 不能包含 shell 元字符或换行");

        string trimmed = text.Trim();
        int split = trimmed.IndexOfAny(new[] { ' ', '\t' });
        string sub = split < 0 ? trimmed : trimmed[..split];
        if (!AllowedGitSubcommands.Contains(sub))
            throw new ToolUsageException($"git {sub} 不在只读白名单内。可用：{string.Join("、", AllowedGitSubcommands.OrderBy(x => x))}");

        string rest = split < 0 ? string.Empty : trimmed[(split + 1)..].Trim();
        string lower = trimmed.ToLowerInvariant();

        foreach (string token in rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = token.ToLowerInvariant();
            foreach (string forbidden in ForbiddenGitOptions)
            {
                if (t == forbidden || t.StartsWith(forbidden + "=", StringComparison.Ordinal))
                    throw new ToolUsageException($"git_args 不允许使用 {forbidden} 选项");
            }
        }

        if (sub.Equals("config", StringComparison.OrdinalIgnoreCase) &&
            !lower.Contains("--get", StringComparison.Ordinal) &&
            !lower.Contains("--list", StringComparison.Ordinal) &&
            !lower.Contains(" -l", StringComparison.Ordinal))
            throw new ToolUsageException("git config 只允许 --get / --get-regexp / --list 等只读查询");

        if (sub.Equals("remote", StringComparison.OrdinalIgnoreCase))
        {
            string first = FirstToken(rest);
            if (first.Length > 0 && first is not ("-v" or "--verbose" or "show" or "get-url"))
                throw new ToolUsageException("git remote 只允许 -v / --verbose / show / get-url");
        }

        if (sub.Equals("branch", StringComparison.OrdinalIgnoreCase) ||
            sub.Equals("tag", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string token in rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.StartsWith('-'))
                    throw new ToolUsageException($"git {sub} 只允许查询选项（如 -a / -l），不能创建或删除引用");
            }
        }
    }

    private static string FirstToken(string text)
    {
        int split = text.IndexOfAny(new[] { ' ', '\t' });
        return (split < 0 ? text : text[..split]).ToLowerInvariant();
    }
}
