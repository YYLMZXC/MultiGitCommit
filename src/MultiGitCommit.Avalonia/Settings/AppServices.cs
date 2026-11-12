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

    public IProjectConfigStore ProjectConfigs { get; }

    public IGitEnvironmentService GitEnvironment { get; }

    public GitIdentityService Identity { get; }

    public RepoLocator RepoLocator { get; }

    public IGitHistoryReader History { get; }

    private AppServices()
    {
        Cli = GitCliRunner.Default;
        Settings = SettingsProvider.Instance;
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
            new RepositoryCommands(cli, logged, GitEnvironment));
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
        IRepositoryCommands repositories)
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
    }
}
