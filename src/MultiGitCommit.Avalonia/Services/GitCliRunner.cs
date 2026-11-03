using System.Diagnostics;
using System.Text;

namespace MultiGitCommit;

/// <summary>
/// <see cref="IGitCliRunner"/> 的唯一实现：把「如何安全地调用外部命令」全部收在这里。
///
/// 统一保证的行为（此前散落在 6 个文件、各自漏掉一部分）：
/// 1. UTF-8 编码，中文输出不乱码；
/// 2. stdin 重定向后立即关闭，非交互执行（GUI 下绝不因等待输入而卡死）；
/// 3. 超时 / 取消即杀掉整个进程树；
/// 4. 便携版 / 内嵌 git 的目录注入子进程 PATH（ssh、git-lfs、credential helper 才能找到）；
/// 5. git 命令统一追加 <c>-c core.quotepath=false</c>，中文文件名不转八进制。
/// </summary>
public sealed class GitCliRunner : IGitCliRunner
{
    /// <summary>
    /// 共享实例：当前项目尚未引入 DI，业务代码统一从这里取。
    /// 需要测试时直接 new 一个假的 <see cref="IGitCliRunner"/> 传入即可，无需改调用方签名。
    /// </summary>
    public static IGitCliRunner Default { get; } = new GitCliRunner();

    /// <summary>超时后等待输出流收尾的上限，避免直接访问已释放的流。</summary>
    private static readonly TimeSpan DrainGracePeriod = TimeSpan.FromSeconds(3);

    public GitCommandResult Run(GitCommandRequest request)
    {
        // 内部所有 await 均使用 ConfigureAwait(false)，同步等待不会牵回 UI 线程，故不会死锁。
        return RunAsync(request).GetAwaiter().GetResult();
    }

    public async Task<GitCommandResult> RunAsync(GitCommandRequest request, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            ProcessStartInfo psi = BuildStartInfo(request);
            process = new Process { StartInfo = psi };

            if (!process.Start())
                return GitCommandResult.Failure($"无法启动进程：{psi.FileName}");

            await WriteInputAndCloseAsync(process, request.StandardInput).ConfigureAwait(false);

            Task<string> stdoutTask = DrainAsync(process.StandardOutput, request.OnOutputLine);
            Task<string> stderrTask = DrainAsync(process.StandardError, request.OnErrorLine);
            Task ioTask = Task.WhenAll(stdoutTask, stderrTask);

            Task timeoutTask = Task.Delay(request.Timeout);
            Task cancelTask = WaitForCancellationAsync(cancellationToken);
            Task finished = await Task.WhenAny(ioTask, timeoutTask, cancelTask).ConfigureAwait(false);

            if (finished != ioTask)
            {
                KillTree(process);
                // 等输出流收尾（子进程可能仍在输出），再确定性地释放
                await Task.WhenAny(ioTask, Task.Delay(DrainGracePeriod)).ConfigureAwait(false);
                try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { /* 已退出 */ }

                return finished == cancelTask
                    ? GitCommandResult.Canceled()
                    : GitCommandResult.Timeout(
                        $"命令执行超时（{Describe(request.Timeout)}），已终止：{Describe(request, psi)}");
            }

            await ioTask.ConfigureAwait(false);
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { /* 已退出 */ }

            int exitCode;
            try { exitCode = process.ExitCode; }
            catch { exitCode = -1; }

            return new GitCommandResult
            {
                ExitCode = exitCode,
                Stdout = stdoutTask.Result,
                Stderr = stderrTask.Result
            };
        }
        catch (Exception ex)
        {
            return GitCommandResult.Failure(ex.ToString());
        }
        finally
        {
            process?.Dispose();
        }
    }

    // ==================== 进程配置 ====================

    /// <summary>把请求翻译为 ProcessStartInfo，集中处理编码、环境变量与 PATH 注入。</summary>
    private static ProcessStartInfo BuildStartInfo(GitCommandRequest request)
    {
        string fileName = string.IsNullOrWhiteSpace(request.FileName) ? GitLocator.GitExe : request.FileName!;
        bool isGit = request.TreatAsGit && IsGitExecutable(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            // git 默认会把非 ASCII 文件名转义成 "\346\226\260" 这类八进制序列，日志里无法阅读
            Arguments = isGit ? "-c core.quotepath=false " + request.Arguments : request.Arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 重定向并立即关闭 stdin：需要交互输入时直接读到 EOF 失败，而不是继承终端 stdin 永久阻塞
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            psi.WorkingDirectory = request.WorkingDirectory!;

        if (isGit) ApplyNonInteractiveEnvironment(psi);

        // 请求级环境变量优先（例如探测凭据时强制 GCM_INTERACTIVE=never）
        if (request.Environment != null)
        {
            foreach (var pair in request.Environment)
                psi.EnvironmentVariables[pair.Key] = pair.Value;
        }

        PrependGitDirectoriesToPath(psi);
        return psi;
    }

    /// <summary>非交互执行：禁止 git / ssh / 凭据管理器弹窗询问，取不到凭据直接报错。</summary>
    private static void ApplyNonInteractiveEnvironment(ProcessStartInfo psi)
    {
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        // 仅在确实能找到 ssh 时才设置，避免覆盖 git 自身的 ssh 查找逻辑
        if (psi.EnvironmentVariables.ContainsKey("GIT_SSH_COMMAND")) return;

        string? ssh = GitLocator.FindSshExe();
        if (ssh != null)
            psi.EnvironmentVariables["GIT_SSH_COMMAND"] =
                $"\"{ssh}\" -o BatchMode=yes -o StrictHostKeyChecking=accept-new";
    }

    /// <summary>
    /// 把解析到的 git 目录（含 mingw64\bin、usr\bin 等）前置到子进程 PATH：
    /// 便携版 / 内嵌 git 自带 ssh、git-lfs、credential helper，无需系统安装；
    /// 同时补上 GUI 应用常缺失的 /opt/homebrew/bin 等目录，手动命令区才能找到 git。
    /// </summary>
    private static void PrependGitDirectoriesToPath(ProcessStartInfo psi)
    {
        IReadOnlyList<string> entries = GitLocator.GetPathEntries();
        if (entries.Count == 0) return;

        string oldPath = psi.EnvironmentVariables.ContainsKey("PATH")
            ? psi.EnvironmentVariables["PATH"] ?? string.Empty
            : string.Empty;

        psi.EnvironmentVariables["PATH"] =
            string.Join(Path.PathSeparator, entries) + Path.PathSeparator + oldPath;
    }

    /// <summary>按文件名判定是否为 git（含无扩展名的 macOS 版与自定义完整路径）。</summary>
    private static bool IsGitExecutable(string fileName)
        => string.Equals(Path.GetFileNameWithoutExtension(fileName), "git", StringComparison.OrdinalIgnoreCase);

    // ==================== 输入输出 ====================

    /// <summary>写入请求指定的 stdin 内容（如有）后立即关闭，给出 EOF。</summary>
    private static async Task WriteInputAndCloseAsync(Process process, string? input)
    {
        try
        {
            if (!string.IsNullOrEmpty(input))
                await process.StandardInput.WriteAsync(input).ConfigureAwait(false);

            process.StandardInput.Close();
        }
        catch (Exception)
        {
            // 子进程可能已退出或不读取 stdin，不影响退出码判定
        }
    }

    /// <summary>逐行读取输出流：回调交给调用方（实时日志），同时累积完整文本用于返回。</summary>
    private static async Task<string> DrainAsync(StreamReader reader, Action<string>? onLine)
    {
        var buffer = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (onLine != null && line.Length > 0) onLine(line);
            buffer.AppendLine(line);
        }

        return buffer.ToString();
    }

    // ==================== 取消与清理 ====================

    /// <summary>把取消令牌包装成可等待任务；不可取消时返回永不完成的任务（不占用计时器）。</summary>
    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await Task.Delay(System.Threading.Timeout.Infinite).ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), tcs))
        {
            await tcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>强制终止进程及其子进程树（失败时忽略）。</summary>
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try { process.Kill(); } catch { /* 已退出 */ }
        }
    }

    /// <summary>超时的人话描述（秒 / 分钟）。</summary>
    private static string Describe(TimeSpan timeout)
        => timeout.TotalSeconds < 60
            ? $"{timeout.TotalSeconds:0} 秒"
            : $"{timeout.TotalMinutes:0.##} 分钟";

    /// <summary>完整命令文本（用于日志与错误提示）。</summary>
    private static string Describe(GitCommandRequest request, ProcessStartInfo psi)
        => $"{Path.GetFileName(psi.FileName)} {request.Arguments}".Trim();
}
