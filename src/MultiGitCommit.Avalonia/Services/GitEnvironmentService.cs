namespace MultiGitCommit;

/// <summary>一次 git 环境刷新后的展示快照（界面只读，不再自己拼状态文案）。</summary>
public sealed class GitEnvironmentSnapshot
{
    /// <summary>是否成功解析到可用的 git。</summary>
    public bool Available { get; init; }

    /// <summary>当前生效的 git 完整路径。</summary>
    public string? GitExePath { get; init; }

    /// <summary>来源描述（含路径与版本）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>非交互 ssh 的完整路径；未找到时为 null。</summary>
    public string? SshExe { get; init; }

    /// <summary>状态栏文本。</summary>
    public string StatusText => Available
        ? $"Git：{GitExePath}"
        : Loc.T("Git：未找到（点击【Git 环境】配置）");

    /// <summary>状态栏悬浮提示。</summary>
    public string ToolTipText => Available
        ? Loc.F($"当前生效：{Description}")
        : Loc.T("未找到可用的 git，请点击【Git 环境】按钮配置来源。");
}

/// <summary>
/// git 环境访问契约：把「解析来源、缓存失效、可用性判断」收在一处，
/// 避免调用方各自读写 <see cref="GitLocator"/> 的可变静态状态、忘记清凭据助手缓存。
/// </summary>
public interface IGitEnvironmentService
{
    /// <summary>当前是否有可用的 git。</summary>
    bool IsAvailable { get; }

    /// <summary>执行命令用的 git 文件名。</summary>
    string GitExe { get; }

    /// <summary>当前生效环境的完整快照（不触发重新解析；状态栏、MCP env 工具共用一份数据）。</summary>
    GitEnvironmentSnapshot Describe();

    /// <summary>按设置重新解析 git，同时让凭据助手扫描缓存失效，返回展示快照。</summary>
    GitEnvironmentSnapshot Refresh(GitSourceMode mode, string? customPath);

    /// <summary>仅让凭据助手扫描缓存失效（用于「重新检测」这类场景）。</summary>
    void InvalidateCredentialHelpers();

    /// <summary>预览某个来源能解析到哪个 git（不改动当前生效环境）。</summary>
    string? Preview(GitSourceMode mode, string? customPath, out string description);

    /// <summary>校验用户填写的自定义 git 路径。</summary>
    string? ResolveCustomPath(string? input);
}

/// <summary><see cref="IGitEnvironmentService"/> 的默认实现（对静态定位器的一层窄封装）。</summary>
public sealed class GitEnvironmentService : IGitEnvironmentService
{
    public bool IsAvailable => GitLocator.GitExePath != null;

    public string GitExe => GitLocator.GitExe;

    public GitEnvironmentSnapshot Describe() => new()
    {
        Available = GitLocator.GitExePath != null,
        GitExePath = GitLocator.GitExePath,
        Description = GitLocator.Description,
        SshExe = GitLocator.Current.SshExe
    };

    public GitEnvironmentSnapshot Refresh(GitSourceMode mode, string? customPath)
    {
        // git 变了，凭据助手的扫描结果也失效；由服务统一保证，调用方无需记得手工调用
        InvalidateCredentialHelpers();
        GitLocator.Resolve(mode, customPath);
        return Describe();
    }

    public void InvalidateCredentialHelpers() => GitCredentialHelper.Invalidate();

    public string? Preview(GitSourceMode mode, string? customPath, out string description)
        => GitLocator.ResolvePreview(mode, customPath, out description);

    public string? ResolveCustomPath(string? input) => GitLocator.ResolveCustomPath(input);
}
