namespace MultiGitCommit;

/// <summary>
/// 组合根（Composition Root）：全应用唯一的对象组装点。
///
/// 之前每个窗口/视图都在自己的构造函数里 <c>new GitIdentityService()</c>、
/// 取 <c>GitCliRunner.Default</c>、<c>SettingsProvider.Instance</c>，
/// 依赖图散落在 UI 里，且子视图常常绕过注入拿到另一套执行器。
/// 现在静态默认值只在这里出现一次，其余代码一律依赖 <see cref="AppServices"/> / <see cref="AppSession"/>
/// 暴露的抽象或实例；将来换成 DI 容器或测试替身，只需改这一处。
/// </summary>
public sealed class AppServices
{
    /// <summary>进程内共享的默认组合（不依赖日志回调的服务）。</summary>
    public static AppServices Current { get; } = new();

    public IGitCliRunner Cli { get; }

    public ISettingsProvider Settings { get; }

    /// <summary>设置变更 / 落盘服务（共享组合使用；注入自定义设置时由会话创建替身）。</summary>
    public AppSettingsService SettingsEditor { get; }

    public IProjectConfigStore ProjectConfigs { get; }

    public IGitEnvironmentService GitEnvironment { get; }

    public GitIdentityService Identity { get; }

    public RepoLocator RepoLocator { get; }

    public IGitHistoryReader History { get; }

    private AppServices()
    {
        Cli = GitCliRunner.Default;
        Settings = SettingsProvider.Instance;
        SettingsEditor = new AppSettingsService(Settings);
        ProjectConfigs = new ProjectConfigStore();
        GitEnvironment = new GitEnvironmentService();
        Identity = new GitIdentityService(Cli);
        RepoLocator = new RepoLocator(Cli);
        History = new GitHistoryReader(Cli);
    }

    /// <summary>用默认执行器与设置创建一个带日志输出的会话。</summary>
    public AppSession CreateSession(Action<string> log) => CreateSession(Cli, Settings, log);

    /// <summary>
    /// 创建一个会话：依赖日志回调的服务（命令日志、推送、拉取、仓库命令）在此统一装配。
    /// 传入自定义执行器/设置即可完全脱离真实 git 与磁盘设置，便于测试。
    /// </summary>
    public AppSession CreateSession(IGitCliRunner cli, ISettingsProvider settings, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        var logged = new GitCommandReporter(cli, log);
        // 默认执行器时复用组合根里的共享实例；注入自定义执行器时整套服务都跟着换成新实例
        bool shared = ReferenceEquals(cli, Cli);
        var identity = shared ? Identity : new GitIdentityService(cli);
        var history = shared ? History : new GitHistoryReader(cli);
        var repoLocator = shared ? RepoLocator : new RepoLocator(cli);

        // 依赖设置的策略服务必须跟随注入的 settings，否则测试替身会被共享单例绕过
        var settingsEditor = ReferenceEquals(settings, Settings)
            ? SettingsEditor
            : new AppSettingsService(settings);
        var repoSession = new RepoSessionService(repoLocator, ProjectConfigs, GitEnvironment, settingsEditor);

        return new AppSession(
            cli,
            settings,
            logged,
            identity,
            repoLocator,
            history,
            ProjectConfigs,
            GitEnvironment,
            new CommitPushService(logged, identity, settings),
            new GitPullService(logged, settings),
            new RepositoryCommands(cli, logged, GitEnvironment),
            settingsEditor,
            new StartupRepoResolver(settings),
            new RemoteUrlPolicy(),
            repoSession);
    }
}

/// <summary>
/// 一次「主界面生命周期」内共享的服务集合。
/// 视图之间传递 <see cref="AppSession"/> 即可复用同一套执行器与服务，避免各自重新构造。
/// </summary>
public sealed class AppSession
{
    public IGitCliRunner Cli { get; }

    public ISettingsProvider Settings { get; }

    public ILoggedGitRunner Logged { get; }

    public GitIdentityService Identity { get; }

    public RepoLocator RepoLocator { get; }

    public IGitHistoryReader History { get; }

    public IProjectConfigStore ProjectConfigs { get; }

    public IGitEnvironmentService GitEnvironment { get; }

    public CommitPushService CommitPush { get; }

    public GitPullService Pull { get; }

    public IRepositoryCommands Repositories { get; }

    /// <summary>设置的变更与落盘（视图不直接改 AppSettings 的字段）。</summary>
    public AppSettingsService SettingsEditor { get; }

    /// <summary>启动仓库定位策略。</summary>
    public StartupRepoResolver StartupRepo { get; }

    /// <summary>远端 URL 规则（GUI 与 MCP 共用）。</summary>
    public RemoteUrlPolicy RemoteUrls { get; }

    /// <summary>仓库加载（校验 / 配置 / 最近项目记忆）。</summary>
    public RepoSessionService RepoSession { get; }

    internal AppSession(
        IGitCliRunner cli,
        ISettingsProvider settings,
        ILoggedGitRunner logged,
        GitIdentityService identity,
        RepoLocator repoLocator,
        IGitHistoryReader history,
        IProjectConfigStore projectConfigs,
        IGitEnvironmentService gitEnvironment,
        CommitPushService commitPush,
        GitPullService pull,
        IRepositoryCommands repositories,
        AppSettingsService settingsEditor,
        StartupRepoResolver startupRepo,
        RemoteUrlPolicy remoteUrls,
        RepoSessionService repoSession)
    {
        Cli = cli;
        Settings = settings;
        Logged = logged;
        Identity = identity;
        RepoLocator = repoLocator;
        History = history;
        ProjectConfigs = projectConfigs;
        GitEnvironment = gitEnvironment;
        CommitPush = commitPush;
        Pull = pull;
        Repositories = repositories;
        SettingsEditor = settingsEditor;
        StartupRepo = startupRepo;
        RemoteUrls = remoteUrls;
        RepoSession = repoSession;
    }
}
