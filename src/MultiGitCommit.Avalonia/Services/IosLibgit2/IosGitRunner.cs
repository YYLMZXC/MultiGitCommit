using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiGitCommit.IosLibgit2;

/// <summary>
/// iOS 版 git 执行器：<see cref="IGitCliRunner"/> 的进程内实现。
///
/// iOS 沙盒禁止 fork/exec 子进程（Process.Start 直接抛异常），因此 git 能力不靠
/// git 可执行文件，而是链接 libgit2 静态库（native-git/ios/，经 mgc_shim.c 暴露简单 C API），
/// 在本类里把「git 命令行字符串」翻译成对应的 shim 调用。
///
/// 契约：本类必须逐字模拟 git CLI 的输出格式与退出码——
/// 上层（RepoLocator / GitCommit.Parse / WorkingTreeFile.Parse / GitStatusText /
/// CommitPushService / GitPullService）都按 CLI 文本与退出码判定结果。
///
/// 支持的子命令与调用方：
/// --version（环境校验）、rev-parse、config、log、show、status、reset、init、
/// symbolic-ref、show-ref、checkout、branch --show-current、add、diff --cached、
/// commit、push、pull、clone、ls-remote、credential（恒失败，iOS 无系统凭据助手）。
///
/// 联网限制：只支持 https（libgit2 SecureTransport）；凭据沿用现有做法——调用方已把
/// 用户名:令牌 注入 URL，本类剥出 userinfo 走 libgit2 的 userpass 凭据，不落日志。
/// 不支持 ssh（静态库未编译 libssh2）。
/// </summary>
public sealed class IosGitRunner : IGitCliRunner
{
    /// <summary>共享实例（组合根在 iOS 上选它作为唯一执行器）。</summary>
    public static IosGitRunner Instance { get; } = new();

    /// <summary>
    /// 伪 git 路径：iOS 没有 git 可执行文件，但设置 / 状态栏 / MCP 都需要一个
    /// 「当前 git」标识，用这个特殊值代替（GitLocator 在 iOS 上固定返回它）。
    /// </summary>
    public const string PseudoGitPath = "__libgit2__";

    /// <summary>libgit2 全局初始化只做一次；返回值是引用计数，忽略。</summary>
    private static int _initialized;

    /// <summary>shim 的动态结果缓冲是单实例的，全程串行（业务侧命令本身也是串行的）。</summary>
    private static readonly object NativeLock = new();

    private IosGitRunner() { }

    /// <summary>libgit2 版本号（状态栏 / 日志展示用）。</summary>
    public static string Libgit2Version
    {
        get
        {
            EnsureInitialized();
            lock (NativeLock)
            {
                var buf = new byte[LibGit2Shim.BufferSize];
                LibGit2Shim.mgc_version(buf, buf.Length);
                return LibGit2Shim.ReadCString(buf);
            }
        }
    }

    public GitCommandResult Run(GitCommandRequest request)
        => RunAsync(request).GetAwaiter().GetResult();

    public async Task<GitCommandResult> RunAsync(GitCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsIOS())
            return GitCommandResult.Failure("iOS 内嵌 git 仅可在 iOS 上使用。");

        // 重活（联网 / 大仓库 status）丢到线程池，别堵 UI 线程；本地快操作一次锁内完成
        return await Task.Run(() => Execute(request, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    // ==================== 命令分派 ====================

    private static GitCommandResult Execute(GitCommandRequest request, CancellationToken token)
    {
        try
        {
            EnsureInitialized();

            // 非 git 的 FileName（手动命令区的 shell）在 iOS 上不可执行
            if (!string.IsNullOrWhiteSpace(request.FileName) &&
                !string.Equals(Path.GetFileName(request.FileName), PseudoGitPath, StringComparison.Ordinal) &&
                Path.GetFileName(request.FileName) is not ("git" or "git.exe" or "libgit2"))
                return GitCommandResult.Failure(
                    Loc.T("iOS 沙盒禁止执行子进程：仅支持 git 子命令，shell / 其他程序不可用。"));

            List<string> tokens = Tokenize(request.Arguments);
            // 剥掉前置 -c key=value（iOS 没有独立 git 配置，credential.helper 覆盖参数在这里没有意义）
            while (tokens.Count >= 2 && tokens[0] == "-c") tokens.RemoveRange(0, 2);
            if (tokens.Count == 0) return Fail(128, "git: 缺少子命令");

            lock (NativeLock)
            {
                if (token.IsCancellationRequested) return GitCommandResult.Canceled();
                return Dispatch(request, tokens, token);
            }
        }
        catch (OperationCanceledException)
        {
            return GitCommandResult.Canceled();
        }
        catch (Exception ex)
        {
            return GitCommandResult.Failure($"iOS git 执行异常：{ex.Message}");
        }
    }

    private static GitCommandResult Dispatch(GitCommandRequest request, List<string> tokens,
        CancellationToken token)
    {
        string verb = tokens[0];

        // 独立于仓库的命令
        if (verb == "--version")
            return Ok($"git version {Libgit2Version} (libgit2; MultiGitCommit iOS)");
        if (verb == "credential")
            return Fail(128, "fatal: could not read Username（iOS 内嵌 git 没有系统凭据助手，请在【Git 环境】配置账号）");
        if (verb == "ls-remote")
            return RunLsRemote(request, tokens, token);
        if (verb == "clone")
            return RunClone(request, tokens, token);
        if (verb == "init")
            return RunInit(request); // git init 在「还不是仓库」的目录里执行，不能先开仓库

        // 其余命令都需要一个真实仓库（config --global 除外）
        return RunWithRepository(request, tokens, verb, token);
    }

    private static GitCommandResult RunWithRepository(GitCommandRequest request, List<string> tokens,
        string verb, CancellationToken token)
    {
        string dir = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? DefaultFolders.RepoParent()
            : request.WorkingDirectory!;

        bool needsRepo = !(verb == "config" && tokens.Contains("--global"));
        if (needsRepo && !Directory.Exists(dir))
            return Fail(128, $"fatal: 目录不存在：{dir}");

        IntPtr repo = IntPtr.Zero;
        try
        {
            if (needsRepo)
            {
                int rc = LibGit2Shim.mgc_repo_open(dir, out repo);
                if (rc != 0)
                    return Fail(128,
                        "fatal: not a git repository (or any of the parent directories): .git");
            }

            return verb switch
            {
                "rev-parse" => RunRevParse(request, tokens, repo),
                "config" => RunConfig(request, tokens, repo),
                "log" => RunLog(request, tokens, repo),
                "show" => RunShow(tokens, repo),
                "status" => RunStatus(tokens, repo),
                "reset" => RunReset(tokens, repo),
                "symbolic-ref" => RunSymbolicRef(tokens, repo),
                "show-ref" => RunShowRef(tokens, repo),
                "checkout" => RunCheckout(tokens, repo),
                "branch" => RunBranch(tokens, repo),
                "add" => RunAdd(tokens, repo),
                "diff" => RunDiffCached(tokens, repo),
                "commit" => RunCommit(tokens, repo),
                "push" => RunPush(request, tokens, repo, token),
                "pull" => RunPull(request, tokens, repo, token),
                _ => Fail(1, $"git: '{verb}' 不是 iOS 内嵌 git 支持的子命令")
            };
        }
        finally
        {
            if (repo != IntPtr.Zero) LibGit2Shim.mgc_repo_close(repo);
        }
    }

    // ==================== 各子命令实现 ====================

    private static GitCommandResult RunRevParse(GitCommandRequest request, List<string> tokens, IntPtr repo)
    {
        if (tokens.Contains("--show-toplevel"))
        {
            var buf = new byte[LibGit2Shim.BufferSize];
            if (LibGit2Shim.mgc_repo_workdir(repo, buf, buf.Length) != 0)
                return Fail(128, "fatal: this operation must be run in a work tree");
            return Ok(LibGit2Shim.ReadCString(buf));
        }

        if (tokens.Contains("--verify") && tokens.Contains("HEAD"))
        {
            if (LibGit2Shim.mgc_repo_has_head(repo) == 0)
            {
                // git rev-parse --verify HEAD 会打印完整 hash；调用方只用退出码，但保持输出一致
                var head = LoadLog(request, repo, max: 1);
                return head.Success && head.Stdout.Length > 0
                    ? Ok(head.Stdout.Split('\u001f')[0])
                    : Ok(string.Empty);
            }
            return Fail(128, "fatal: ambiguous argument 'HEAD': unknown revision or path not in the working tree.");
        }

        return Fail(128, "git rev-parse: iOS 内嵌 git 仅支持 --show-toplevel / --verify HEAD");
    }

    private static GitCommandResult RunConfig(GitCommandRequest request, List<string> tokens, IntPtr repo)
    {
        bool global = tokens.Remove("--global");
        bool getAll = tokens.Remove("--get-all");
        bool get = tokens.Remove("--get");

        List<string> rest = tokens.Skip(1).Where(t => !t.StartsWith('-')).ToList();

        if (get || getAll)
        {
            // get-all 目前只用于 credential.helper 探测：iOS 无助手，恒「未配置」（git 退出码 1）
            if (getAll) return Fail(1, string.Empty);

            if (rest.Count == 0) return Fail(129, "usage: git config --get <key>");
            var buf = new byte[LibGit2Shim.BufferSize];
            int rc = LibGit2Shim.mgc_config_get(global ? IntPtr.Zero : repo, rest[0], buf, buf.Length);
            return rc switch
            {
                0 => Ok(LibGit2Shim.ReadCString(buf)),
                1 => new GitCommandResult { ExitCode = 1 },
                _ => Fail(128, ReadLastError())
            };
        }

        if (rest.Count < 2) return Fail(129, "usage: git config [--global] <key> <value>");
        var err = new byte[LibGit2Shim.BufferSize];
        return LibGit2Shim.mgc_config_set(global ? IntPtr.Zero : repo, rest[0], rest[1], err, err.Length) == 0
            ? Ok(string.Empty)
            : Fail(128, LibGit2Shim.ReadCString(err));
    }

    private static GitCommandResult RunLog(GitCommandRequest request, List<string> tokens, IntPtr repo)
    {
        int max = 100;
        for (int i = 1; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "-n" && int.TryParse(tokens[i + 1], out int n)) max = n;
            else if (tokens[i].StartsWith("--max-count=", StringComparison.Ordinal) &&
                     int.TryParse(tokens[i]["--max-count=".Length..], out int m)) max = m;
        }

        GitCommandResult head = LoadLog(request, repo, max);
        return head;
    }

    /// <summary>执行 mgc_log 并处理空仓库语义（错误文案对齐 git，供空仓库判定用）。</summary>
    private static GitCommandResult LoadLog(GitCommandRequest request, IntPtr repo, int max)
    {
        int rc;
        try
        {
            rc = LibGit2Shim.mgc_log(repo, max);
        }
        finally
        {
            // 无论成败都读走 / 释放缓冲，避免下一次调用读到残留
        }

        if (rc == 0)
        {
            string output = LibGit2Shim.ReadBuffer();
            LibGit2Shim.mgc_buffer_free();
            return Ok(output);
        }

        LibGit2Shim.mgc_buffer_free();
        var branchBuf = new byte[LibGit2Shim.BufferSize];
        LibGit2Shim.mgc_repo_head_branch(repo, branchBuf, branchBuf.Length);
        string branch = LibGit2Shim.ReadCString(branchBuf);
        return Fail(128,
            $"fatal: your current branch '{(branch.Length > 0 ? branch : "main")}' does not have any commits yet");
    }

    private static GitCommandResult RunShow(List<string> tokens, IntPtr repo)
    {
        string? rev = tokens.Skip(1).FirstOrDefault(t => !t.StartsWith('-'));
        if (rev == null) return Fail(128, "fatal: git show: 缺少提交引用");

        int rc;
        string output;
        try
        {
            rc = LibGit2Shim.mgc_show_files(repo, rev);
            output = LibGit2Shim.ReadBuffer();
        }
        finally
        {
            LibGit2Shim.mgc_buffer_free();
        }

        return rc == 0 ? Ok(output) : Fail(128, ReadLastError());
    }

    private static GitCommandResult RunStatus(List<string> tokens, IntPtr repo)
    {
        bool nulSep = tokens.Contains("-z");
        bool untrackedAll = tokens.Contains("--untracked-files=all") || tokens.Contains("-uall");

        int rc;
        string output;
        try
        {
            rc = LibGit2Shim.mgc_status_porcelain(repo, nulSep ? 1 : 0, untrackedAll ? 1 : 0);
            output = LibGit2Shim.ReadBuffer();
        }
        finally
        {
            LibGit2Shim.mgc_buffer_free();
        }

        return rc == 0 ? Ok(output) : Fail(128, ReadLastError());
    }

    private static GitCommandResult RunReset(List<string> tokens, IntPtr repo)
    {
        int mode = 2; // --hard
        if (tokens.Contains("--soft")) mode = 0;
        else if (tokens.Contains("--mixed")) mode = 1;

        string? rev = tokens.Skip(1).FirstOrDefault(t => !t.StartsWith('-'));
        if (rev == null) return Fail(128, "fatal: git reset: 缺少目标提交");

        var err = new byte[LibGit2Shim.BufferSize];
        return LibGit2Shim.mgc_reset(repo, rev, mode, err, err.Length) == 0
            ? Ok(string.Empty)
            : Fail(128, LibGit2Shim.ReadCString(err));
    }

    private static GitCommandResult RunInit(GitCommandRequest request)
    {
        string dir = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? DefaultFolders.RepoParent()
            : request.WorkingDirectory!;
        var err = new byte[LibGit2Shim.BufferSize];
        if (LibGit2Shim.mgc_repo_init(dir, err, err.Length) != 0)
            return Fail(128, LibGit2Shim.ReadCString(err));
        return Ok($"Initialized empty Git repository in {Path.Combine(dir, ".git")}/");
    }

    private static GitCommandResult RunSymbolicRef(List<string> tokens, IntPtr repo)
    {
        // symbolic-ref --short HEAD（读）
        if (tokens.Contains("--short"))
        {
            var buf = new byte[LibGit2Shim.BufferSize];
            int rc = LibGit2Shim.mgc_repo_head_symbolic_short(repo, buf, buf.Length);
            if (rc == 0) return Ok(LibGit2Shim.ReadCString(buf));
            if (rc == 1) return Fail(1, "fatal: ref HEAD is not a symbolic ref");
            return Fail(128, ReadLastError());
        }

        // symbolic-ref HEAD refs/heads/<branch>（写）
        string? target = tokens.Skip(1).FirstOrDefault(t => t.StartsWith("refs/"));
        if (target == null) return Fail(128, "fatal: symbolic-ref: 缺少目标引用");
        var err = new byte[LibGit2Shim.BufferSize];
        return LibGit2Shim.mgc_repo_set_head(repo, target, err, err.Length) == 0
            ? Ok(string.Empty)
            : Fail(128, LibGit2Shim.ReadCString(err));
    }

    private static GitCommandResult RunShowRef(List<string> tokens, IntPtr repo)
    {
        string? refName = tokens.Skip(1).FirstOrDefault(t => t.StartsWith("refs/"));
        if (refName == null) return Fail(128, "fatal: show-ref: 缺少引用名");
        int rc = LibGit2Shim.mgc_repo_ref_exists(repo, refName);
        return rc switch
        {
            0 => Ok(string.Empty),
            1 => new GitCommandResult { ExitCode = 1 },
            _ => Fail(128, ReadLastError())
        };
    }

    private static GitCommandResult RunCheckout(List<string> tokens, IntPtr repo)
    {
        bool create = tokens.Contains("-b");
        string? branch = tokens.Skip(1).Where(t => !t.StartsWith('-')).FirstOrDefault();
        if (branch == null) return Fail(128, "fatal: git checkout: 缺少分支名");

        var err = new byte[LibGit2Shim.BufferSize];
        return LibGit2Shim.mgc_repo_checkout_branch(repo, branch, create ? 1 : 0, err, err.Length) == 0
            ? Ok($"Switched to{(create ? " a new" : "")} branch '{branch}'")
            : Fail(1, LibGit2Shim.ReadCString(err));
    }

    private static GitCommandResult RunBranch(List<string> tokens, IntPtr repo)
    {
        if (!tokens.Contains("--show-current"))
            return Fail(1, "git branch: iOS 内嵌 git 仅支持 --show-current");

        var buf = new byte[LibGit2Shim.BufferSize];
        LibGit2Shim.mgc_repo_head_branch(repo, buf, buf.Length);
        return Ok(LibGit2Shim.ReadCString(buf)); // detached HEAD：空输出、退出码 0，与 git 一致
    }

    private static GitCommandResult RunAdd(List<string> tokens, IntPtr repo)
    {
        var err = new byte[LibGit2Shim.BufferSize];

        // add . / add -A / add --all：全量（libgit2 的 add_all 语义 = 暂存所有修改 / 新增 / 删除）
        if (tokens.Contains(".") || tokens.Contains("-A") || tokens.Contains("--all"))
        {
            return LibGit2Shim.mgc_add_all(repo, err, err.Length) == 0
                ? Ok(string.Empty)
                : Fail(128, LibGit2Shim.ReadCString(err));
        }

        // add -- <paths...> / add <paths...>
        List<string> paths = tokens.Skip(1).Where(t => t != "--" && !t.StartsWith('-')).ToList();
        if (paths.Count == 0) return Fail(129, "usage: git add [<pathspec>...]");

        return LibGit2Shim.mgc_add_paths(repo, paths.ToArray(), paths.Count, err, err.Length) == 0
            ? Ok(string.Empty)
            : Fail(128, LibGit2Shim.ReadCString(err));
    }

    private static GitCommandResult RunDiffCached(List<string> tokens, IntPtr repo)
    {
        if (!tokens.Contains("--cached") || !tokens.Contains("--quiet"))
            return Fail(128, "git diff: iOS 内嵌 git 仅支持 diff --cached --quiet");

        int rc = LibGit2Shim.mgc_index_has_staged(repo);
        return rc switch
        {
            0 => Ok(string.Empty),                      // 暂存区与 HEAD 一致 → 退出码 0
            1 => new GitCommandResult { ExitCode = 1 }, // 有暂存改动 → 退出码 1
            _ => Fail(128, ReadLastError())
        };
    }

    private static GitCommandResult RunCommit(List<string> tokens, IntPtr repo)
    {
        int msgIndex = tokens.IndexOf("-m");
        if (msgIndex < 0 || msgIndex + 1 >= tokens.Count)
            return Fail(129, "usage: git commit -m <message>");
        string message = tokens[msgIndex + 1];
        bool allowEmpty = tokens.Contains("--allow-empty");

        var outBuf = new byte[LibGit2Shim.BufferSize];
        var err = new byte[LibGit2Shim.BufferSize];
        int rc = LibGit2Shim.mgc_commit(repo, message, allowEmpty ? 1 : 0, outBuf, outBuf.Length, err, err.Length);
        if (rc == 0) return Ok(LibGit2Shim.ReadCString(outBuf));
        if (rc == 1) return Fail(1, "On branch (nothing to commit, working tree clean)");
        return Fail(128, LibGit2Shim.ReadCString(err));
    }

    // ==================== 联网命令 ====================

    private static unsafe GitCommandResult RunPush(GitCommandRequest request, List<string> tokens, IntPtr repo,
        CancellationToken token)
    {
        List<string> rest = tokens.Skip(1).Where(t => !t.StartsWith('-')).ToList();
        if (rest.Count < 2) return Fail(129, "usage: git push [--force] <url> <branch> [--tags]");

        bool force = tokens.Contains("--force");
        bool tags = tokens.Contains("--tags");
        (string cleanUrl, string user, string pass) = SplitUserInfo(rest[0]);
        string branch = rest[1];

        return RunNetworkOp(request, token, (ctx) =>
        {
            var err = new byte[LibGit2Shim.BufferSize];
            int rc = LibGit2Shim.mgc_push(repo, cleanUrl, branch, force ? 1 : 0, tags ? 1 : 0,
                user, pass, LibGit2Shim.Progress, ctx, err, err.Length);
            if (rc == 0)
                return Ok($"To {GitCredentials.Mask(cleanUrl)}\n * [new branch]      {branch} -> {branch}");
            return Fail(128, LibGit2Shim.ReadCString(err));
        });
    }

    private static unsafe GitCommandResult RunPull(GitCommandRequest request, List<string> tokens, IntPtr repo,
        CancellationToken token)
    {
        int strategy = tokens.Contains("--rebase") ? 1 : tokens.Contains("--no-rebase") ? 2 : 0;
        List<string> rest = tokens.Skip(1).Where(t => !t.StartsWith('-')).ToList();
        if (rest.Count < 2) return Fail(129, "usage: git pull <url> <branch>");

        (string cleanUrl, string user, string pass) = SplitUserInfo(rest[0]);
        string branch = rest[1];

        return RunNetworkOp(request, token, (ctx) =>
        {
            var err = new byte[LibGit2Shim.BufferSize];
            int rc = LibGit2Shim.mgc_pull(repo, cleanUrl, branch, strategy,
                user, pass, LibGit2Shim.Progress, ctx, err, err.Length);
            return rc == 0 ? Ok(string.Empty) : Fail(128, LibGit2Shim.ReadCString(err));
        });
    }

    private static unsafe GitCommandResult RunClone(GitCommandRequest request, List<string> tokens,
        CancellationToken token)
    {
        string? branch = null;
        bool recursive = false;
        List<string> rest = new();

        for (int i = 1; i < tokens.Count; i++)
        {
            string t = tokens[i];
            if (t == "--branch" && i + 1 < tokens.Count) { branch = tokens[++i]; continue; }
            if (t == "--depth") { i++; continue; } // libgit2 不支持浅克隆：忽略深度，做完整克隆
            if (t.StartsWith("--depth=", StringComparison.Ordinal)) continue;
            if (t == "--recursive" || t == "--recurse-submodules") { recursive = true; continue; }
            if (t.StartsWith('-')) continue; // --progress 等
            rest.Add(t);
        }

        if (rest.Count < 2) return Fail(129, "usage: git clone <url> <dir>");
        (string cleanUrl, string user, string pass) = SplitUserInfo(rest[0]);
        string targetDir = rest[1].Trim('"');

        return RunNetworkOp(request, token, (ctx) =>
        {
            var err = new byte[LibGit2Shim.BufferSize];
            int rc = LibGit2Shim.mgc_clone(cleanUrl, targetDir, branch, 0, recursive ? 1 : 0,
                user, pass, LibGit2Shim.Progress, ctx, err, err.Length);
            return rc == 0 ? Ok(string.Empty) : Fail(128, LibGit2Shim.ReadCString(err));
        });
    }

    private static unsafe GitCommandResult RunLsRemote(GitCommandRequest request, List<string> tokens,
        CancellationToken token)
    {
        List<string> rest = tokens.Skip(1).Where(t => !t.StartsWith('-')).ToList();
        if (rest.Count == 0) return Fail(129, "usage: git ls-remote <url>");
        (string cleanUrl, string user, string pass) = SplitUserInfo(rest[0]);

        return RunNetworkOp(request, token, (ctx) =>
        {
            var err = new byte[LibGit2Shim.BufferSize];
            int rc = LibGit2Shim.mgc_ls_remote(cleanUrl, user, pass,
                LibGit2Shim.Progress, ctx, err, err.Length);
            if (rc != 0) return Fail(128, LibGit2Shim.ReadCString(err));
            string output = LibGit2Shim.ReadBuffer();
            LibGit2Shim.mgc_buffer_free();
            return Ok(output);
        });
    }

    /// <summary>
    /// 联网命令的公共壳：进度回调 + 超时 / 取消中止 + 异常兜底。
    /// 进度回调是 libgit2 主动逐次调用的：超时 / 取消就在回调里判定并请求中止，
    /// 不需要额外的监视线程（响应粒度 = 一次网络进度事件，足够 UI 及时反馈）。
    /// </summary>
    private static GitCommandResult RunNetworkOp(GitCommandRequest request, CancellationToken token,
        Func<IntPtr, GitCommandResult> operation)
    {
        long deadline = Stopwatch.GetTimestamp() +
                        (long)(request.Timeout.TotalSeconds * Stopwatch.Frequency);
        var context = new LibGit2Shim.ProgressContext
        {
            Emit = line =>
            {
                // git 的联网进度行走 stderr；日志层对 stdout / stderr 一视同仁
                request.OnErrorLine?.Invoke(line);
                if (request.OnErrorLine == null) request.OnOutputLine?.Invoke(line);
            },
            ShouldAbort = () => token.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadline
        };

        GCHandle handle = GCHandle.Alloc(context);
        try
        {
            return operation(GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
            LibGit2Shim.mgc_buffer_free();
        }
    }

    // ==================== 工具 ====================

    private static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            LibGit2Native.git_libgit2_init();
    }

    /// <summary>读取 libgit2 最近一次错误的文本（shim 的 err 缓冲没填上时的兜底）。</summary>
    private static string ReadLastError() => LibGit2Native.LastError();

    /// <summary>成功结果（stdout 有内容）。</summary>
    private static GitCommandResult Ok(string stdout) => new() { ExitCode = 0, Stdout = stdout };

    /// <summary>失败结果（模拟 git 的退出码 + stderr 文案）。</summary>
    private static GitCommandResult Fail(int exitCode, string stderr)
        => new() { ExitCode = exitCode, Stderr = stderr };

    /// <summary>
    /// 从 URL 中剥出 userinfo（用户名:密码），返回清洗后的 URL 与凭据。
    /// 与 GitCredentials.Apply 的注入方式对应（凭据内嵌在 URL 里）；值做 URL 解码。
    /// </summary>
    internal static (string CleanUrl, string User, string Pass) SplitUserInfo(string url)
    {
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return (url, string.Empty, string.Empty);

        int authStart = schemeEnd + 3;
        int slash = url.IndexOf('/', authStart);
        int authorityEnd = slash < 0 ? url.Length : slash;

        int at = -1;
        for (int i = authorityEnd - 1; i >= authStart; i--)
        {
            if (url[i] == '@') { at = i; break; }
        }
        if (at < authStart) return (url, string.Empty, string.Empty);

        string userinfo = url[authStart..at];
        string cleanUrl = url[..authStart] + url[(at + 1)..];

        int colon = userinfo.IndexOf(':');
        string user = colon < 0 ? userinfo : userinfo[..colon];
        string pass = colon < 0 ? string.Empty : userinfo[(colon + 1)..];
        try
        {
            user = Uri.UnescapeDataString(user);
            pass = Uri.UnescapeDataString(pass);
        }
        catch
        {
            // 解码失败按原样使用
        }

        return (cleanUrl, user, pass);
    }

    /// <summary>
    /// git 风格命令行分词：空白分隔；双引号内允许 \" 与 \\ 转义（与 GitArgument.Quote 对应）。
    /// </summary>
    internal static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            switch (c)
            {
                case '"' when !inQuotes && current.Length == 0:
                    inQuotes = true;
                    break;
                case '\\' when inQuotes && i + 1 < command.Length &&
                               (command[i + 1] is '"' or '\\'):
                    current.Append(command[++i]);
                    break;
                case '"' when inQuotes:
                    inQuotes = false;
                    break;
                case ' ' or '\t' when !inQuotes:
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
