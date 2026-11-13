namespace MultiGitCommit;

/// <summary>
/// 本地 Git 仓库扫描：BFS 逐层遍历目录，命中 .git（目录 / 文件）或 multigitcommit.config.json 即算一个仓库。
/// MCP 工具 find_repos 与仓库管理首页共用同一实现；单个目录不可读时跳过，不影响整体扫描。
/// </summary>
public static class RepoScanner
{
    /// <summary>扫描时跳过的目录名（依赖目录 / 构建产物 / IDE 配置等）。</summary>
    private static readonly HashSet<string> SkippedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages", "target", "dist", "build", "__pycache__",
        ".vs", ".idea", ".vscode", ".codebuddy", ".gradle", ".git"
    };

    /// <summary>判定目录是否为 Git 仓库根（.git 目录 / .git 文件 / 本工具项目配置文件即命中）。</summary>
    public static bool IsRepoRoot(string dir)
        => Directory.Exists(Path.Combine(dir, ".git")) ||
           File.Exists(Path.Combine(dir, ".git")) ||
           File.Exists(Path.Combine(dir, ProjectConfig.ConfigFileName));

    /// <summary>扫描单个根目录下的 Git 仓库（不含跳过目录），最多返回 limit 个。</summary>
    public static IReadOnlyList<string> Scan(string root, int maxDepth, int limit, CancellationToken ct)
        => Scan(new[] { root }, maxDepth, limit, ct);

    /// <summary>扫描多个根目录下的 Git 仓库；跨根目录去重（Windows 忽略大小写），最多返回 limit 个。</summary>
    public static IReadOnlyList<string> Scan(IEnumerable<string> roots, int maxDepth, int limit, CancellationToken ct)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var queue = new Queue<(string Dir, int Depth)>();
        foreach (string root in roots)
            queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count >= limit) break;

            (string dir, int depth) = queue.Dequeue();
            if (!seen.Add(dir)) continue;
            try
            {
                if (IsRepoRoot(dir)) found.Add(dir);
                if (depth >= maxDepth) continue;

                // 命中仓库后仍继续向下：父目录本身是仓库时，也可能存在嵌套仓库
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || SkippedDirNames.Contains(name)) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 单个目录不可读时跳过，不影响整体扫描
            }
        }

        return found;
    }
}

/// <summary>
/// 读取仓库当前分支：只解析 .git/HEAD，不启动 git 进程。
/// 仓库管理首页一次可能展示数百个仓库，逐个执行 git branch 会明显卡顿，
/// 而且在未配置 git 的环境（如 iOS、git 环境异常时）拿不到结果。
/// 兼容 .git 目录、.git 文件（子模块 / worktree 的 "gitdir: ..."）与游离 HEAD。
/// </summary>
public static class RepoBranchReader
{
    /// <summary>无 .git 或无法读取分支时的占位显示。</summary>
    public const string Unknown = "—";

    private const string RefMarker = "ref: ";
    private const string HeadsPrefix = "refs/heads/";

    /// <summary>当前分支名；游离 HEAD 返回「HEAD@短哈希」，无 .git 或读取失败返回 <see cref="Unknown"/>。</summary>
    public static string Read(string repoDir)
    {
        try
        {
            string? headFile = ResolveHeadFile(repoDir);
            if (headFile == null) return Unknown;

            string head = File.ReadAllText(headFile).Trim();
            if (head.StartsWith(RefMarker, StringComparison.Ordinal))
            {
                string refName = head[RefMarker.Length..].Trim();
                if (refName.Length == 0) return Unknown;
                return refName.StartsWith(HeadsPrefix, StringComparison.Ordinal)
                    ? refName[HeadsPrefix.Length..]
                    : refName;
            }

            // detached HEAD：显示短提交号，一眼看出当前不在任何分支上
            return head.Length > 0 ? "HEAD@" + head[..Math.Min(7, head.Length)] : Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 目录不可读 / 路径异常时不影响其他仓库的展示
            return Unknown;
        }
    }

    /// <summary>定位 HEAD 文件：普通仓库在 .git/HEAD；worktree / 子模块按 .git 文件里的 gitdir 指向定位。</summary>
    private static string? ResolveHeadFile(string repoDir)
    {
        string dotGit = Path.Combine(repoDir, ".git");
        if (Directory.Exists(dotGit)) return Path.Combine(dotGit, "HEAD");
        if (!File.Exists(dotGit)) return null;

        const string gitDirMarker = "gitdir:";
        string content = File.ReadAllText(dotGit).Trim();
        if (!content.StartsWith(gitDirMarker, StringComparison.OrdinalIgnoreCase)) return null;

        string gitDir = content[gitDirMarker.Length..].Trim().Trim('"');
        if (gitDir.Length == 0) return null;
        if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(repoDir, gitDir));
        return Path.Combine(gitDir, "HEAD");
    }
}
