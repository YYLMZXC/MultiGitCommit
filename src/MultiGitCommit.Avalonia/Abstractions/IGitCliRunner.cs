namespace MultiGitCommit;

/// <summary>
/// 外部命令（git / shell / 系统工具）的统一执行入口。
///
/// 为什么要抽这一层：进程调用是本工具最核心、也最容易出错的外部依赖
/// （非交互、UTF-8、超时杀进程树、PATH 注入、凭据脱敏）。此前这套样板在
/// 6 个文件里各自手写一遍，任何一处漏掉超时都会让 GUI 永久卡死。
/// 收敛到这里之后：
/// 1. 行为只有一份实现，改一次即全局生效；
/// 2. 业务代码只依赖本接口，可注入假实现做单元测试（断言生成了哪些 git 参数）；
/// 3. 平台 / 编码等细节被彻底隔离在执行器内部。
/// </summary>
public interface IGitCliRunner
{
    /// <summary>同步执行（仅用于输出量小、耗时短的校验类命令）。超时即终止进程树。</summary>
    GitCommandResult Run(GitCommandRequest request);

    /// <summary>
    /// 异步执行，stdout / stderr 可逐行回调（用于实时写日志）。
    /// 超时或 <paramref name="cancellationToken"/> 取消时终止整个进程树，绝不挂起。
    /// </summary>
    Task<GitCommandResult> RunAsync(GitCommandRequest request, CancellationToken cancellationToken = default);
}

/// <summary>一次外部命令的执行结果。</summary>
public sealed class GitCommandResult
{
    /// <summary>进程退出码；未能正常结束时为 -1。</summary>
    public int ExitCode { get; init; }

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    /// <summary>是否因超时被终止。</summary>
    public bool TimedOut { get; init; }

    /// <summary>是否因取消被终止。</summary>
    public bool Cancelled { get; init; }

    /// <summary>命令是否成功完成（退出码 0 且未超时 / 未取消）。</summary>
    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;

    /// <summary>去掉首尾空白的标准输出（方便直接当值使用）。</summary>
    public string Output => Stdout.Trim();

    /// <summary>失败时用于展示的错误文本：优先 stderr，其次 stdout。</summary>
    public string ErrorText => (Stderr.Length > 0 ? Stderr : Stdout).Trim();

    public static GitCommandResult Timeout(string message)
        => new() { ExitCode = -1, TimedOut = true, Stderr = message };

    // 默认文案需经 Loc 翻译，不能写成默认参数（必须是编译期常量），故拆成重载
    public static GitCommandResult Canceled()
        => Canceled(Loc.T("操作已取消"));

    public static GitCommandResult Canceled(string message)
        => new() { ExitCode = -1, Cancelled = true, Stderr = message };

    public static GitCommandResult Failure(string message)
        => new() { ExitCode = -1, Stderr = message };
}

/// <summary>一次外部命令的执行参数。</summary>
public sealed class GitCommandRequest
{
    /// <summary>可执行文件；为 null / 空时使用当前解析到的 git（<see cref="GitLocator.GitExe"/>）。</summary>
    public string? FileName { get; init; }

    public string Arguments { get; init; } = string.Empty;

    /// <summary>工作目录；为 null 时继承当前进程目录。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>超时上限，默认 20 秒（校验类命令）。普通 git 命令请显式传 <see cref="GitTimeouts.Default"/>。</summary>
    public TimeSpan Timeout { get; init; } = GitTimeouts.Quick;

    /// <summary>
    /// 写入子进程 stdin 的文本，写完立即关闭。为 null 时直接给 EOF——
    /// 这样 git / ssh 需要交互输入时立刻失败，而不是在 GUI 下永久等待。
    /// </summary>
    public string? StandardInput { get; init; }

    /// <summary>额外环境变量（覆盖同名变量）。</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>标准输出逐行回调（实时日志用）；为 null 时不回调。</summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>标准错误逐行回调；为 null 时不回调。</summary>
    public Action<string>? OnErrorLine { get; init; }

    /// <summary>
    /// 是否按 git 语义处理：加 <c>-c core.quotepath=false</c>、注入 PATH、禁止终端提示。
    /// 默认 true；可执行文件名不是 git 时执行器会自动跳过 git 专属处理。
    /// </summary>
    public bool TreatAsGit { get; init; } = true;
}

/// <summary>外部命令的常用超时档位（集中定义，避免各处散落魔法数字）。</summary>
public static class GitTimeouts
{
    /// <summary>快速校验类命令（rev-parse / config --get 等）。</summary>
    public static readonly TimeSpan Quick = TimeSpan.FromSeconds(20);

    /// <summary>普通 git 命令（add / commit / push 等）。</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(10);

    /// <summary>克隆仓库（大仓库耗时较长）。</summary>
    public static readonly TimeSpan Clone = TimeSpan.FromMinutes(30);

    /// <summary>探测类命令（ls-remote / credential fill / cmdkey 等），需要短超时快速失败。</summary>
    public static readonly TimeSpan Probe = TimeSpan.FromSeconds(8);
}
