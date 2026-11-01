using System.Diagnostics;
using System.IO;
using System.Text;

namespace MultiGitCommit;

/// <summary>账号（推送凭据）验证结果状态。</summary>
public enum CredentialTestStatus
{
    /// <summary>验证通过：服务端接受该账号，且可访问探测的仓库。</summary>
    Ok,

    /// <summary>账号不可用：用户名 / 访问令牌错误，或该账号无访问权限。</summary>
    AuthFailed,

    /// <summary>服务端已接受该账号身份，但探测的仓库路径不存在（凭据本身有效）。</summary>
    RepoNotFound,

    /// <summary>网络不通 / 主机不可达 / 超时，无法判定账号是否有效。</summary>
    NetworkError,

    /// <summary>用户取消了验证。</summary>
    Cancelled,

    /// <summary>其他错误（git 不可用、输入不完整等）。</summary>
    Error
}

/// <summary>
/// 账号验证结果。<see cref="Message"/> / <see cref="Detail"/> 都经过脱敏，绝不含令牌明文。
/// </summary>
public sealed class CredentialTestResult
{
    /// <summary>验证状态。</summary>
    public CredentialTestStatus Status { get; init; } = CredentialTestStatus.Error;

    /// <summary>面向用户的结论（一行）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>服务端 / git 返回的关键信息（已脱敏，用于排查）。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>实际探测的地址（已脱敏，令牌显示为 ***）。</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>账号是否可用（认证通过即算可用）。</summary>
    public bool Success => Status == CredentialTestStatus.Ok || Status == CredentialTestStatus.RepoNotFound;
}

/// <summary>
/// 账号（推送凭据）验证：用一次 <c>git ls-remote</c> 实测「用户名 + 访问令牌」能否通过服务端认证。
///
/// 设计要点（避免界面卡住）：
/// 1. 只验证【一个】账号，不做任何批量 / 全量扫描；
/// 2. 全程异步，默认 20 秒超时，超时立即杀掉整个 git 进程树；
/// 3. 支持 <see cref="CancellationToken"/>，用户可随时中止；
/// 4. 非交互（GIT_TERMINAL_PROMPT=0 / credential.helper 置空），不会弹系统凭据窗口，也不会误用系统里的其他账号；
/// 5. 所有输出先经 <see cref="GitCredentials.MaskText"/> 脱敏，令牌绝不进入界面与日志。
/// </summary>
public static class CredentialTester
{
    /// <summary>单次验证的超时秒数（网络不通时最多等待这么久）。</summary>
    public const int DefaultTimeoutSeconds = 20;

    /// <summary>
    /// 验证一个账号是否可用。
    /// </summary>
    /// <param name="gitExe">git 可执行文件完整路径（由调用方按当前 git 来源解析；为空则直接返回错误）。</param>
    /// <param name="cred">待验证的账号（主机 / 用户名 / 令牌）。</param>
    /// <param name="remoteUrls">当前项目的远端 URL（可选）：有同主机远端时用它做真实探测，否则退化为 https://host/ 根探测。</param>
    /// <param name="ct">取消令牌（用户点【取消验证】时触发）。</param>
    /// <param name="timeoutSeconds">超时秒数。</param>
    public static async Task<CredentialTestResult> TestAsync(
        string? gitExe,
        GitCredential cred,
        IEnumerable<string>? remoteUrls = null,
        CancellationToken ct = default,
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        string host = GitCredentials.GetHost(cred.Host) ?? (cred.Host ?? string.Empty).Trim();
        string user = (cred.Username ?? string.Empty).Trim();
        string token = (cred.Token ?? string.Empty).Trim();

        if (host.Length == 0)
            return Error("主机为空，无法验证账号。");

        if (user.Length == 0 || token.Length == 0)
            return Error("用户名或访问令牌为空，无法验证账号。");

        if (string.IsNullOrWhiteSpace(gitExe))
            return Error("未找到可用的 git，请先配置 git 来源再验证账号。");

        var probe = new GitCredential { Host = host, Username = user, Token = token };
        string probeUrl = PickProbeUrl(host, remoteUrls);
        string target = GitCredentials.Mask(GitCredentials.Apply(probeUrl, probe));

        try
        {
            var psi = CreateProbeProcess(gitExe, GitCredentials.Apply(probeUrl, probe));
            var (completed, exitCode, output) = await RunProbeAsync(psi, timeoutSeconds, ct).ConfigureAwait(false);

            if (!completed)
            {
                return ct.IsCancellationRequested
                    ? new CredentialTestResult { Status = CredentialTestStatus.Cancelled, Message = "已取消账号验证。", Target = target }
                    : new CredentialTestResult
                    {
                        Status = CredentialTestStatus.NetworkError,
                        Message = $"验证超时（超过 {timeoutSeconds} 秒未响应）：无法确认 {user}@{host} 是否可用，请检查网络或主机地址。",
                        Target = target
                    };
            }

            string detail = FirstLines(GitCredentials.MaskText(output), 2);
            return Classify(exitCode, output, detail, host, user, target);
        }
        catch (Exception ex)
        {
            return Error("验证账号失败：" + GitCredentials.MaskText(ex.Message), target);
        }
    }

    /// <summary>
    /// 探测地址：优先使用当前项目中同主机的 https 远端（真实仓库，最贴近实际推送场景），
    /// 没有匹配远端时退化为 https://host/ 根探测（服务端仍会校验身份，只是仓库路径不存在）。
    /// </summary>
    private static string PickProbeUrl(string host, IEnumerable<string>? remoteUrls)
    {
        if (remoteUrls != null)
        {
            foreach (string url in remoteUrls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;

                string s = url.Trim();
                if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? h = GitCredentials.GetHost(s);
                if (!string.IsNullOrEmpty(h) && string.Equals(h, host, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }

        return "https://" + host + "/";
    }

    /// <summary>构造非交互的 git ls-remote 探测进程。</summary>
    private static ProcessStartInfo CreateProbeProcess(string gitExe, string authUrl)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitExe,
            // -c credential.helper= ：探测只认 URL 里的账号，不用（也不会改）系统保存的账号
            Arguments = "-c credential.helper= ls-remote --heads \"" + authUrl + "\"",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 非交互：禁止终端提示与凭据管理器弹窗，取不到就直接报错而不是挂起等待
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
        psi.EnvironmentVariables["GIT_CREDENTIAL_MANAGER_INTERACTIVE"] = "never";

        // 内嵌 / 指定路径的 git 也能找到自带的 ssh、http 后端等程序
        var pathEntries = GitLocator.GetPathEntries();
        if (pathEntries.Count > 0)
        {
            string oldPath = psi.EnvironmentVariables.ContainsKey("PATH")
                ? psi.EnvironmentVariables["PATH"] ?? string.Empty
                : string.Empty;
            psi.EnvironmentVariables["PATH"] =
                string.Join(Path.PathSeparator, pathEntries) + Path.PathSeparator + oldPath;
        }

        return psi;
    }

    /// <summary>执行探测进程：超时或取消即杀掉进程树，绝不挂起。</summary>
    private static async Task<(bool Completed, int ExitCode, string Output)> RunProbeAsync(
        ProcessStartInfo psi, int timeoutSeconds, CancellationToken ct)
    {
        using var p = Process.Start(psi);
        if (p == null) return (false, -1, string.Empty);

        p.StandardInput.Close(); // 立即给 EOF，杜绝等待交互输入

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var ioTask = Task.WhenAll(outTask, errTask);

        Task delay = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        Task cancel = CancellationTask(ct);
        Task finished = await Task.WhenAny(ioTask, delay, cancel).ConfigureAwait(false);

        if (finished != ioTask)
        {
            Kill(p);
            await Task.WhenAny(ioTask, Task.Delay(3000)).ConfigureAwait(false);
            try { await p.WaitForExitAsync().ConfigureAwait(false); } catch { /* 已退出 */ }
            return (false, -1, string.Empty);
        }

        string output = (outTask.Result + Environment.NewLine + errTask.Result).Trim();
        try { await p.WaitForExitAsync().ConfigureAwait(false); } catch { /* 已退出 */ }

        int exitCode;
        try { exitCode = p.ExitCode; } catch { exitCode = -1; }

        return (true, exitCode, output);
    }

    /// <summary>把取消令牌包装成可等待的任务（不可取消时返回永不完成的任务）。</summary>
    private static Task CancellationTask(CancellationToken ct)
    {
        if (!ct.CanBeCanceled) return new TaskCompletionSource().Task;

        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch
        {
            try { p.Kill(); } catch { /* 已退出 */ }
        }
    }

    /// <summary>按退出码与输出判定账号是否可用（认证失败优先于仓库不存在，避免误判为通过）。</summary>
    private static CredentialTestResult Classify(int exitCode, string output, string detail,
        string host, string user, string target)
    {
        string lower = output.ToLowerInvariant();

        if (exitCode == 0)
        {
            return new CredentialTestResult
            {
                Status = CredentialTestStatus.Ok,
                Message = $"账号可用：{user}@{host} 已通过认证，可访问 {target}。",
                Detail = detail,
                Target = target
            };
        }

        if (ContainsAny(lower, AuthFailureKeywords))
        {
            return new CredentialTestResult
            {
                Status = CredentialTestStatus.AuthFailed,
                Message = $"账号不可用：{user}@{host} 未通过认证（用户名 / 访问令牌不正确，或该账号无访问权限）。",
                Detail = detail,
                Target = target
            };
        }

        if (ContainsAny(lower, NotFoundKeywords))
        {
            return new CredentialTestResult
            {
                Status = CredentialTestStatus.RepoNotFound,
                Message = $"账号可用：{user}@{host} 的身份已被服务端接受（探测地址 {target} 不存在，凭据有效）。",
                Detail = detail,
                Target = target
            };
        }

        if (ContainsAny(lower, NetworkKeywords))
        {
            return new CredentialTestResult
            {
                Status = CredentialTestStatus.NetworkError,
                Message = $"无法连接 {host}，账号未能验证（网络不通 / 主机不可达 / 被代理拦截）。",
                Detail = detail,
                Target = target
            };
        }

        return new CredentialTestResult
        {
            Status = CredentialTestStatus.Error,
            Message = $"未能验证 {user}@{host} 是否可用（git ls-remote 退出码 {exitCode}）。",
            Detail = detail,
            Target = target
        };
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (string k in keywords)
        {
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>取输出的前若干行并脱敏截断，避免把大段服务端回显塞进界面。</summary>
    private static string FirstLines(string? text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(maxLines)
            .ToList();

        string result = string.Join(" ", lines);
        return result.Length <= 200 ? result : result[..200] + "…";
    }

    private static CredentialTestResult Error(string message, string target = "") =>
        new() { Status = CredentialTestStatus.Error, Message = message, Target = target };

    /// <summary>认证失败关键字（中英兼顾，覆盖 GitHub / Gitee / GitLab / CNB 等常见回显）。</summary>
    private static readonly string[] AuthFailureKeywords =
    {
        "authentication failed", "auth failed", "authentication required", "authorization failed",
        "invalid username or password", "incorrect username or password", "bad credentials",
        "access denied", "permission denied", "login failed",
        "unauthorized", "401 unauthorized", "403 forbidden", "http 401", "http 403",
        "could not read username", "terminal prompts disabled", "no permission",
        "用户名或密码错误", "用户名或密码不正确", "认证失败", "鉴权失败", "身份验证失败",
        "未授权", "没有权限", "权限不足", "令牌无效", "token 无效", "invalid token"
    };

    /// <summary>仓库不存在关键字：身份已通过校验，只是路径不存在，凭据视为有效。</summary>
    private static readonly string[] NotFoundKeywords =
    {
        "repository not found", "repo not found", "project not found", "not found",
        "does not appear to be a git repository", "couldn't find", "could not find",
        "no such project", "no such repository",
        "仓库不存在", "项目不存在", "未找到仓库", "未找到项目", "找不到仓库", "不存在"
    };

    /// <summary>网络 / 连接类关键字：无法判定账号是否有效。</summary>
    private static readonly string[] NetworkKeywords =
    {
        "could not resolve host", "couldn't resolve host", "failed to connect", "connection refused",
        "connection timed out", "timed out", "timeout", "unable to access", "no route to host",
        "network is unreachable", "ssl certificate", "proxy", "recv failure",
        "无法解析", "连接超时", "网络不可达", "连接被拒绝"
    };
}
