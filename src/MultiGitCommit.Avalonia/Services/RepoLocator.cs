namespace MultiGitCommit;

/// <summary>
/// 仓库定位：「用户输入的路径 / 当前目录」→「git 仓库根目录」的唯一判定入口。
///
/// 抽取原因：此前同一件事有三套判定方式（<c>rev-parse --show-toplevel</c>、
/// <c>rev-parse --is-inside-work-tree</c>、逐级向上找 .git 目录），
/// 语义重叠且判定标准不一致，容易出现「能加载配置却提示不是仓库」这类矛盾。
/// 现在统一以 <c>git rev-parse --show-toplevel</c> 为准，其余判定都复用它。
/// </summary>
public sealed class RepoLocator
{
    /// <summary>向上查找疑似仓库根的最大层数。</summary>
    private const int AncestorSearchDepth = 12;

    private readonly IGitCliRunner _cli;

    /// <summary>构造定位器；执行器由 <see cref="AppServices"/> 组合根显式注入。</summary>
    public RepoLocator(IGitCliRunner cliRunner)
        => _cli = cliRunner ?? throw new ArgumentNullException(nameof(cliRunner));

    /// <summary>
    /// 解析仓库根目录：输入可以是仓库的任意子目录（甚至是被 .gitignore 忽略的 bin/ 输出目录）。
    /// 用仓库根而非输入目录，才能保证配置文件与 .git 同级、<c>git add .</c> 在根目录执行。
    /// </summary>
    /// <param name="path">待解析目录。</param>
    /// <param name="error">失败原因：目录不存在或 git 的原始报错（权限 / 进程启动失败等）。</param>
    /// <returns>仓库根目录；不是 git 仓库时返回 null。</returns>
    public string? TryResolve(string? path, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = Loc.T("未指定目录");
            return null;
        }

        if (!Directory.Exists(path))
        {
            error = Loc.F($"目录不存在：{path}");
            return null;
        }

        GitCommandResult result = _cli.Run(new GitCommandRequest
        {
            Arguments = "rev-parse --show-toplevel",
            WorkingDirectory = path,
            Timeout = GitTimeouts.Quick
        });

        string root = result.Output;
        if (!result.Success || root.Length == 0 || !Directory.Exists(root))
        {
            error = result.ErrorText;
            return null;
        }

        return root;
    }

    /// <summary>判断目录是否位于 git 工作树内（与 <see cref="TryResolve"/> 同一判定依据）。</summary>
    public bool IsWorkTree(string? path) => TryResolve(path, out _) != null;

    /// <summary>
    /// 从指定目录起逐级向上查找疑似仓库根（含 .git 目录 / .git 文件 / 本工具配置文件即命中）。
    /// 仅用于启动时给输入框猜一个默认路径，不替代 <see cref="TryResolve"/> 的严格校验。
    /// </summary>
    public static string? FindRepoAncestor(string startDir)
    {
        try
        {
            var dir = new DirectoryInfo(startDir);
            for (int i = 0; i < AncestorSearchDepth && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ProjectConfig.ConfigFileName)))
                    return dir.FullName;
            }
        }
        catch (Exception)
        {
            // 路径不可访问时忽略
        }

        return null;
    }
}
