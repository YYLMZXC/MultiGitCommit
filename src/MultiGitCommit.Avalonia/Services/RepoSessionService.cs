namespace MultiGitCommit;

/// <summary>加载仓库失败的结构化原因（视图据此选择本地化提示，服务不拼界面文案）。</summary>
public enum RepoLoadFailure
{
    None,
    EmptyPath,
    GitUnavailable,
    DirectoryMissing,
    NotRepo
}

/// <summary>一次「输入路径 → 仓库根 + 项目配置」的加载结果。</summary>
public sealed record RepoLoadResult(
    string? RepoRoot,
    ProjectConfig? Config,
    RepoLoadFailure Failure,
    string GitError,
    string InputPath);

/// <summary>
/// 仓库会话业务：校验目录是否位于 git 工作树、取仓库根目录、加载该仓库的独立配置，
/// 并记住本次成功加载的仓库（最近项目 / 上次使用的仓库）。
///
/// 抽取原因：这些步骤原先整段写在 MainView.LoadConfigFromPath 里，
/// 与清空控件、回填复选框、输出日志混在一起；业务规则无法被 CLI / MCP 或测试复用。
/// 现在服务只依赖组合根注入的抽象与 <see cref="RepoLocator"/>，
/// 返回结构化结果；视图只负责把结果映射到控件与日志。
/// </summary>
public sealed class RepoSessionService
{
    private readonly RepoLocator _repos;
    private readonly IProjectConfigStore _configs;
    private readonly IGitEnvironmentService _environment;
    private readonly AppSettingsService _settings;

    public RepoSessionService(RepoLocator repos, IProjectConfigStore configs,
        IGitEnvironmentService environment, AppSettingsService settings)
    {
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public RepoLoadResult Load(string? inputPath)
    {
        string path = inputPath?.Trim() ?? string.Empty;
        if (path.Length == 0)
            return Failed(RepoLoadFailure.EmptyPath, path);

        if (!_environment.IsAvailable)
            return Failed(RepoLoadFailure.GitUnavailable, path);

        // 严格判定与「仓库根目录」解析统一走 RepoLocator
        string? root = _repos.TryResolve(path, out string error);
        if (root == null)
        {
            RepoLoadFailure failure = error.StartsWith(Loc.T("目录不存在"), StringComparison.Ordinal)
                ? RepoLoadFailure.DirectoryMissing
                : RepoLoadFailure.NotRepo;
            return new RepoLoadResult(null, null, failure, error, path);
        }

        _settings.RememberRepo(root);
        return new RepoLoadResult(root, _configs.Load(root), RepoLoadFailure.None, string.Empty, path);
    }

    private static RepoLoadResult Failed(RepoLoadFailure failure, string path)
        => new(null, null, failure, string.Empty, path);
}
