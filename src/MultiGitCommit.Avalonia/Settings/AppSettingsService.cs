namespace MultiGitCommit;

/// <summary>【Git 环境】对话框确认后需要写回的设置项（一次提交，避免视图逐个字段改动全局对象）。</summary>
public sealed record EnvironmentSettingsUpdate(
    GitSourceMode GitSource,
    string CustomGitPath,
    CredentialSourceMode CredentialSource,
    StartupRepoMode StartupRepo,
    StageMode StageMode,
    AppLanguage Language);

/// <summary>
/// 应用设置的「变更 + 落盘」唯一入口，同时承载与设置有关的业务规则
/// （最近项目去重与上限、启动页、提交身份、Git 环境写回）。
///
/// 抽取原因：这些规则原先散落在 MainView / RepoHomeView 的 code-behind 里——
/// 每个视图都直接改 <see cref="ISettingsProvider.Current"/> 的可变字段、各自 try/catch 保存，
/// 最近项目列表的「去重 → 置顶 → 裁到上限」还只能靠约定维护。
/// 现在视图只提交用户选择并展示保存结果，设置的读写与持久化策略全部集中在这里；
/// 服务不引用任何 Avalonia 类型，可由 <see cref="AppServices"/> 注入测试替身。
/// </summary>
public sealed class AppSettingsService
{
    private readonly ISettingsProvider _provider;

    public AppSettingsService(ISettingsProvider provider)
        => _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    /// <summary>只读访问当前设置（展示用）；所有写入都走本服务的 TryXxx 方法。</summary>
    public AppSettings Current => _provider.Current;

    /// <summary>保存并返回失败原因；调用方决定是提示用户还是静默忽略。</summary>
    public bool TrySave(out string error)
    {
        try
        {
            _provider.Save();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 记住本次成功加载的仓库：写入「上次使用的仓库」，并把路径去重后置顶到最近项目列表，
    /// 超出 <see cref="AppSettings.MaxRecentRepoPaths"/> 时裁掉最旧的。
    /// 没有变化时不会落盘；保存失败不影响当前会话（下次改动会重试）。
    /// </summary>
    public void RememberRepo(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return;

        AppSettings settings = Current;
        bool lastChanged = !string.Equals(settings.LastRepoPath, repoRoot, StringComparison.Ordinal);
        if (lastChanged) settings.LastRepoPath = repoRoot;

        List<string> recents = settings.RecentRepoPaths;
        string before = string.Join("\n", recents);
        recents.RemoveAll(p => string.Equals(p, repoRoot, StringComparison.Ordinal));
        recents.Insert(0, repoRoot);
        if (recents.Count > AppSettings.MaxRecentRepoPaths)
            recents.RemoveRange(AppSettings.MaxRecentRepoPaths, recents.Count - AppSettings.MaxRecentRepoPaths);

        if (lastChanged || string.Join("\n", recents) != before)
            TrySave(out _);
    }

    public bool TryRemoveRecent(string repoPath, out string error)
    {
        Current.RecentRepoPaths.RemoveAll(p => string.Equals(p, repoPath, StringComparison.Ordinal));
        return TrySave(out error);
    }

    public bool TryClearRecent(out string error)
    {
        Current.RecentRepoPaths.Clear();
        return TrySave(out error);
    }

    public bool TrySetDefaultStartPage(StartPageMode mode, out string error)
    {
        Current.DefaultStartPage = mode;
        return TrySave(out error);
    }

    /// <summary>一次性写回【Git 环境】对话框的全部选项并落盘。</summary>
    public bool TryApplyEnvironmentSettings(EnvironmentSettingsUpdate update, out string error)
    {
        AppSettings settings = Current;
        settings.GitSource = update.GitSource;
        settings.CustomGitPath = update.CustomGitPath ?? string.Empty;
        settings.CredentialSource = update.CredentialSource;
        settings.StartupRepo = update.StartupRepo;
        settings.StageMode = update.StageMode;
        settings.Language = update.Language;
        return TrySave(out error);
    }

    public bool TrySetCommitIdentity(GitIdentity identity, out string error)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Current.CommitIdentity = identity;
        return TrySave(out error);
    }
}
