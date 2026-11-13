namespace MultiGitCommit;

/// <summary>启动仓库决策的来源：视图据此选择对应的本地化提示文案。</summary>
public enum StartupRepoReason
{
    /// <summary>按设置不自动打开任何仓库。</summary>
    None,

    /// <summary>当前目录（或其上级）本身位于仓库内。</summary>
    CurrentDirectory,

    /// <summary>设置为「上次使用的仓库优先」。</summary>
    LastUsed,

    /// <summary>当前目录不在仓库内，按设置回退到上次使用的仓库。</summary>
    FallbackToLastUsed
}

/// <summary>启动仓库定位结果；服务只给结论，不拼界面文案、不判断操作系统。</summary>
public sealed record StartupRepoDecision(string? Path, StartupRepoReason Reason);

/// <summary>
/// 启动仓库定位策略：「当前目录优先 / 上次使用的仓库优先 / 找不到时回退」的唯一实现。
///
/// 抽取原因：原先这段判定写在 MainView 的 code-behind 里，与输入框赋值、平台提示文案混在一起，
/// 界面无法拿到「为什么选中这个目录」的结构化结论，其他入口也无从复用。
/// 现在服务只依赖 <see cref="ISettingsProvider"/> 与 <see cref="RepoLocator.FindRepoAncestor"/>，
/// 返回决策；视图负责把决策翻成文案与控件状态。
/// </summary>
public sealed class StartupRepoResolver
{
    private readonly ISettingsProvider _settings;

    public StartupRepoResolver(ISettingsProvider settings)
        => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public StartupRepoDecision Resolve()
    {
        AppSettings settings = _settings.Current;

        string cwd = Environment.CurrentDirectory;
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string cwdFull = string.IsNullOrWhiteSpace(cwd)
            ? string.Empty
            : Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar);

        string last = settings.LastRepoPath ?? string.Empty;
        bool hasLast = last.Length > 0 && Directory.Exists(last);
        StartupRepoMode mode = settings.StartupRepo;

        // 「上次使用的仓库优先」：直接沿用，不再看当前目录
        if (mode == StartupRepoMode.LastUsed && hasLast)
            return new StartupRepoDecision(last, StartupRepoReason.LastUsed);

        // 当前目录（或其上级）所在的仓库：默认最优先
        string? cwdRepo = RepoLocator.FindRepoAncestor(cwdFull.Length > 0 ? cwdFull : baseDir);
        if (cwdRepo != null)
            return new StartupRepoDecision(cwdRepo, StartupRepoReason.CurrentDirectory);

        // 只有显式选择「当前目录优先，找不到时回退」才自动打开上次项目
        if (mode == StartupRepoMode.CurrentDirectoryThenLastUsed && hasLast)
            return new StartupRepoDecision(last, StartupRepoReason.FallbackToLastUsed);

        return new StartupRepoDecision(null, StartupRepoReason.None);
    }
}
