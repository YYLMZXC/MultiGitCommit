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
