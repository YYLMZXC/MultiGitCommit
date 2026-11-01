using System.Diagnostics;
using System.Text;

namespace MultiGitCommit;

/// <summary>
/// credential helper（凭据助手）可用性检测与自动适配（Windows / macOS 通用）。
///
/// 典型故障：git 配置里写着 <c>credential.helper=manager</c>（Git Credential Manager），
/// 但当前使用的 git（精简便携版 / 复制不完整的目录）里并不存在 <c>git-credential-manager</c>，
/// 于是每次需要凭据时 git 报：
/// <c>git: 'credential-manager' is not a git command</c> →
/// <c>fatal: could not read Username for 'https://...': terminal prompts disabled</c>，推送必然失败。
///
/// 本类负责：扫描当前 git 真正可用的助手 → 判断配置里的是否可用 → 不可用时给出一个
/// <c>-c credential.helper=xxx</c> 覆盖参数，只作用于本次命令，不改动用户的 git 配置。
///
/// 平台差异：Windows 常用 manager / wincred / store，macOS 常用 manager / osxkeychain / store；
/// 助手程序在 Windows 上是 git-credential-*.exe，在 Unix 上是无扩展名的可执行文件。
/// </summary>
public static class GitCredentialHelper
{
    /// <summary>Windows 上的候选取用顺序：优先 GCM，其次 Windows 凭据管理器，最后明文文件。</summary>
    private static readonly string[] WindowsPreferredOrder = { "manager", "wincred", "store" };

    /// <summary>macOS / Unix 上的候选取用顺序：优先 GCM，其次系统钥匙串，最后明文文件。</summary>
    private static readonly string[] UnixPreferredOrder = { "manager", "osxkeychain", "store" };

    /// <summary>当前平台的候选取用顺序。</summary>
    private static string[] PreferredOrder =>
        OperatingSystem.IsWindows() ? WindowsPreferredOrder : UnixPreferredOrder;

    /// <summary>缓存：当前 git 可用的助手集合（切换 git 来源后需调用 Invalidate）。</summary>
    private static List<string>? _available;

    /// <summary>缓存：当前生效的 credential.helper 配置值。</summary>
    private static List<string>? _configured;

    /// <summary>缓存对应的 git，切换后自动失效。</summary>
    private static string? _cacheKey;

    /// <summary>git 来源变化后调用，清空缓存。</summary>
    public static void Invalidate()
    {
        _available = null;
        _configured = null;
        _cacheKey = null;
    }

    /// <summary>当前 git 安装里实际存在的 credential 助手名称（如 manager / osxkeychain / store）。</summary>
    public static IReadOnlyList<string> Available()
    {
        EnsureCache();
        return _available ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>当前生效的 credential.helper 配置值（可能多条）。</summary>
    public static IReadOnlyList<string> Configured()
    {
        EnsureCache();
        return _configured ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// 首选可用助手：按当前平台顺序（Windows：manager → wincred → store；macOS：manager → osxkeychain → store）
    /// 取第一个实际存在的；都不存在返回 null。
    /// </summary>
    public static string? Best()
    {
        var available = Available();
        foreach (string name in PreferredOrder)
        {
            if (available.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) return name;
        }

        return available.Count > 0 ? available[0] : null;
    }

    /// <summary>
    /// 当前配置的助手是否可用：任一配置项能对应到实际存在的助手即视为可用；
    /// 配置为自定义命令（以 ! 开头或含路径）或显式留空（禁用助手）时不做干预。
    /// </summary>
    public static bool ConfiguredUsable()
    {
        var configured = Configured();
        if (configured.Count == 0) return false;

        var available = Available();
        foreach (string item in configured)
        {
            string value = item.Trim();
            if (value.Length == 0) return true;            // credential.helper=（显式禁用）
            if (value.StartsWith('!')) return true;        // 自定义命令
            if (value.Contains('/') || value.Contains('\\')) return true; // 绝对路径的自定义程序

            string name = value.Split(' ', '\t')[0];
            if (available.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) return true;
        }

        return false;
    }

    /// <summary>
    /// 需要覆盖时返回命令行前缀（形如 "-c credential.helper=osxkeychain "），可直接在 git 子命令前拼接；
    /// 配置本身可用、或没有任何助手可用时返回 null。
    /// </summary>
    public static string? OverrideArgument()
    {
        if (ConfiguredUsable()) return null;

        string? best = Best();
        return best == null ? null : $"-c credential.helper={best} ";
    }

    /// <summary>状态描述（用于设置窗口展示与排查）。</summary>
    public static string Describe()
    {
        var available = Available();
        var configured = Configured();

        string configuredText = configured.Count == 0
            ? "未配置"
            : string.Join("、", configured.Select(c => c.Trim().Length == 0 ? "（留空/禁用）" : c.Trim()));

        if (available.Count == 0)
            return $"当前 git 未发现任何 credential 助手（配置：{configuredText}）：https 推送请手动填写账号。";

        if (ConfiguredUsable())
            return $"凭据助手正常：配置 {configuredText}；可用 {string.Join("、", available)}。";

        string? best = Best();
        return best == null
            ? $"凭据助手不可用（配置：{configuredText}），且无可用替代项。"
            : $"凭据助手不可用（配置：{configuredText}，当前 git 缺少对应程序），已自动改用 {best}。";
    }

    // ==================== 内部实现 ====================

    private static void EnsureCache()
    {
        string key = GitLocator.GitExe;
        if (_cacheKey == key && _available != null && _configured != null) return;

        _cacheKey = key;
        _available = ScanAvailable();
        _configured = ReadConfigured();
    }

    /// <summary>扫描 git 安装目录里的 git-credential-*（Windows 为 .exe，Unix 为无扩展名可执行文件）。</summary>
    private static List<string> ScanAvailable()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string prefix = "git-credential-";

        foreach (string dir in CandidateDirectories())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                foreach (string file in Directory.EnumerateFiles(dir, prefix + "*"))
                {
                    if (!IsCredentialProgram(file)) continue;

                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length > prefix.Length &&
                        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        names.Add(name[prefix.Length..]);
                }
            }
            catch (Exception)
            {
                // 目录不可访问时跳过
            }
        }

        return names.ToList();
    }

    /// <summary>判断该文件是否是可执行的 credential 助手程序。</summary>
    private static bool IsCredentialProgram(string file)
    {
        string name = Path.GetFileName(file);
        if (!name.StartsWith("git-credential-", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
                return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.HasExtension(name);

            // Unix：助手是无扩展名的可执行脚本 / 二进制，需具备执行位
            if (Path.HasExtension(name)) return false;

            var mode = File.GetUnixFileMode(file);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>credential 助手可能所在的目录集合（相对 git 位置推导，覆盖两平台的发行版布局）。</summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        var dirs = new List<string>();

        foreach (string dir in GitLocator.GetPathEntries())
        {
            dirs.Add(dir);

            try
            {
                var info = new DirectoryInfo(dir);
                var p1 = info.Parent;      // .../mingw64/bin → .../mingw64，或 .../bin → Git 根目录
                var p2 = p1?.Parent;       // .../mingw64     → Git 根目录

                foreach (var b in new[] { info, p1, p2 }.Where(x => x != null))
                {
                    string baseDir = b!.FullName;

                    // Unix / 通用：git-core 与 bin（Homebrew、官方安装包、内嵌 git 都在这两个位置）
                    dirs.Add(Path.Combine(baseDir, "libexec", "git-core"));
                    dirs.Add(Path.Combine(baseDir, "bin"));

                    // Windows（Git for Windows / MinGit）特有布局
                    dirs.Add(Path.Combine(baseDir, "mingw64", "libexec", "git-core"));
                    dirs.Add(Path.Combine(baseDir, "mingw32", "libexec", "git-core"));
                    dirs.Add(Path.Combine(baseDir, "usr", "lib", "git-core"));
                    dirs.Add(Path.Combine(baseDir, "mingw64", "bin"));
                    dirs.Add(Path.Combine(baseDir, "mingw32", "bin"));
                    dirs.Add(Path.Combine(baseDir, "usr", "bin"));
                }
            }
            catch (Exception)
            {
                // 路径异常时跳过
            }
        }

        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>读取 git config --get-all credential.helper（失败视为未配置）。</summary>
    private static List<string> ReadConfigured()
    {
        var result = new List<string>();
        try
        {
            string exe = GitLocator.GitExe;
            if (string.IsNullOrEmpty(exe)) return result;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "config --get-all credential.helper",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null) return result;
            p.StandardInput.Close();

            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return result;
            }

            if (p.ExitCode != 0) return result; // 未配置时 git 返回 1

            foreach (string line in output.Split('\n'))
            {
                string v = line.Trim();
                if (v.Length > 0) result.Add(v);
            }
        }
        catch (Exception)
        {
            // 读取失败按「未配置」处理
        }

        return result;
    }
}
