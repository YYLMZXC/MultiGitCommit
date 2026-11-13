namespace MultiGitCommit;

/// <summary>
/// 一次 git 环境解析的完整结果快照（不可变）。
///
/// 为什么要抽成值对象：此前这些信息是 <c>GitLocator</c> 里的若干个独立静态字段
/// （<c>_gitExe</c>、<c>_description</c>），并各自暴露成静态属性，调用方随时可能读到
/// 「git 已换新、但 PATH / ssh 结论还是旧的」这类前后不一致的中间态。
/// 现在一次解析产出一个整体快照，读取方拿到的一定是同一时刻的完整结论；
/// PATH 与 ssh 也只探测一次，而不是每条 git 命令都重新扫一遍磁盘。
/// </summary>
public sealed class GitEnvironment
{
    /// <summary>
    /// 完全空的快照：无 git、无 PATH 注入目录、无 ssh。
    /// 用于「探测 git 自身是否可用」这类自举场景——此时不能依赖尚未解析出来的环境。
    /// </summary>
    public static GitEnvironment Empty { get; } = new();

    /// <summary>当前生效的 git 完整路径；未找到时为 null。</summary>
    public string? GitExePath { get; init; }

    /// <summary>来源描述（含路径与版本），用于状态栏与日志展示。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// 需要注入子进程 PATH 的目录集合：已按优先级排好序、已去重，且只包含真实存在的目录。
    /// 未解析到 git 时也包含各平台「GUI 启动时 PATH 常缺失」的常用目录。
    /// </summary>
    public IReadOnlyList<string> PathEntries { get; init; } = Array.Empty<string>();

    /// <summary>非交互 ssh 的完整路径；未找到时为 null（此时不设置 GIT_SSH_COMMAND）。</summary>
    public string? SshExe { get; init; }

    /// <summary>进程调用用的 git 文件名：已解析则用完整路径，否则退回 "git"（依赖子进程 PATH）。</summary>
    public string GitExe => GitExePath ?? "git";

    /// <summary>git 可执行文件所在目录；未解析时为 null。</summary>
    public string? GitDirectory
        => string.IsNullOrEmpty(GitExePath) ? null : Path.GetDirectoryName(GitExePath);

    /// <summary>是否成功解析到可用的 git。</summary>
    public bool IsResolved => !string.IsNullOrEmpty(GitExePath);

    /// <inheritdoc />
    public override string ToString()
        => IsResolved ? Description : Loc.T("git 未解析");
}


