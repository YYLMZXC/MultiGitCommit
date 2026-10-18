    using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiGitCommit;

/// <summary>git 可执行文件来源模式（与 Windows 版枚举数值一致，配置文件可通用）。</summary>
public enum GitSourceMode
{
    /// <summary>自动检测：软件内嵌 Git → 常见安装位置 → 系统 PATH。</summary>
    Auto = 0,

    /// <summary>仅使用系统 PATH 环境变量中的 git（环境 git）。</summary>
    System = 1,

    /// <summary>用户指定的 git 可执行文件 / Git 目录（便携版、软件自带 git）。</summary>
    Custom = 2,

    /// <summary>仅使用软件内嵌的 Git（.app 内 Resources/git 或软件目录 git/ 子目录）。</summary>
    Embedded = 3
}

/// <summary>
/// 应用级全局设置（与具体仓库无关），macOS 下持久化到
/// <c>~/Library/Application Support/MultiGitCommit/settings.json</c>。
/// </summary>
public class AppSettings
{
    /// <summary>git 来源模式，默认自动检测。</summary>
    [JsonPropertyName("GitSource")]
    public GitSourceMode GitSource { get; set; } = GitSourceMode.Auto;

    /// <summary>用户指定的 git 路径或 Git 根目录（仅 Custom 模式使用）。</summary>
    [JsonPropertyName("CustomGitPath")]
    public string CustomGitPath { get; set; } = string.Empty;

    /// <summary>设置文件完整路径（加载时填充）。</summary>
    [JsonIgnore]
    public string SettingsFilePath { get; private set; } = string.Empty;

    private static string SettingsDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "MultiGitCommit");

    /// <summary>读取设置；文件不存在或损坏时返回默认设置。</summary>
    public static AppSettings Load()
    {
        var settings = new AppSettings
        {
            SettingsFilePath = Path.Combine(SettingsDir, "settings.json")
        };

        try
        {
            if (File.Exists(settings.SettingsFilePath))
            {
                string json = File.ReadAllText(settings.SettingsFilePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    loaded.SettingsFilePath = settings.SettingsFilePath;
                    loaded.CustomGitPath ??= string.Empty;
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // 读取失败不抛异常，返回默认设置
        }

        return settings;
    }

    /// <summary>保存设置到 ~/Library/Application Support/MultiGitCommit/settings.json。</summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json, new UTF8Encoding(false));
    }
}

/// <summary>
/// git 定位器（macOS 版）：使工具【不依赖系统是否安装 / 配置了 git】。
///
/// 四种来源：
/// 1. Auto     自动检测：软件内嵌 Git → 常见安装位置（Homebrew / Xcode / MacPorts 等）→ 系统 PATH；
/// 2. System   仅使用系统 PATH 中的 git；
/// 3. Custom   用户指定的 git 可执行文件或 Git 根目录；
/// 4. Embedded 仅使用软件内嵌的 Git（.app/Contents/Resources/git 或软件目录 git/）。
///
/// 说明：macOS 从 Finder / Dock 启动的 GUI 应用 PATH 通常只有 /usr/bin:/bin:/usr/sbin:/sbin，
/// 因此 Homebrew 等路径必须显式枚举，而不能只依赖 PATH 查找。
/// </summary>
public static class GitLocator
{
    private static AppSettings? _settings;

    /// <summary>应用级设置（首次访问时加载）。</summary>
    public static AppSettings Settings => _settings ??= AppSettings.Load();

    private static string? _gitExe;
    private static string _description = string.Empty;

    /// <summary>当前生效的 git 完整路径；未找到时为 null。</summary>
    public static string? GitExePath => _gitExe;

    /// <summary>进程调用的 git 文件名：已解析则用完整路径，否则退回 "git"。</summary>
    public static string GitExe => _gitExe ?? "git";

    /// <summary>git 可执行文件所在目录（注入子进程 PATH 用）；未解析时为 null。</summary>
    public static string? GitDirectory => _gitExe == null ? null : Path.GetDirectoryName(_gitExe);

    /// <summary>
    /// 需要注入子进程 PATH 的目录集合：git 所在目录、Git 根目录下的 bin / libexec/git-core，
    /// 以及 macOS 常见命令目录（GUI 应用 PATH 常缺失 /opt/homebrew/bin、/usr/local/bin 等）。
    /// </summary>
    public static IReadOnlyList<string> GetPathEntries()
    {
        var entries = new List<string>();

        // GUI 应用默认 PATH 之外的常用目录，保证手动命令区（/bin/bash -lc）也能找到 git
        foreach (string dir in new[]
                 {
                     "/opt/homebrew/bin", "/opt/homebrew/sbin",
                     "/usr/local/bin", "/usr/local/sbin",
                     "/usr/bin", "/bin", "/usr/sbin", "/sbin"
                 })
        {
            if (Directory.Exists(dir)) entries.Add(dir);
        }

        if (_gitExe == null) return entries;

        try
        {
            string? gitDir = Path.GetDirectoryName(Path.GetFullPath(_gitExe));
            if (!string.IsNullOrEmpty(gitDir) && !entries.Contains(gitDir))
                entries.Insert(0, gitDir);

            string? root = TryGetGitRoot(_gitExe);
            if (root != null)
            {
                foreach (string sub in new[]
                         {
                             Path.Combine(root, "bin"),
                             Path.Combine(root, "libexec", "git-core")
                         })
                {
                    if (Directory.Exists(sub) && !entries.Contains(sub))
                        entries.Insert(0, sub);
                }
            }
        }
        catch (Exception)
        {
            // 解析失败仅影响 PATH 注入，不影响主流程
        }

        return entries;
    }

    /// <summary>
    /// 根据 git 路径推断 Git 根目录：&lt;root&gt;/bin/git、&lt;root&gt;/cmd/git、&lt;root&gt;/libexec/git-core/git → root。
    /// </summary>
    private static string? TryGetGitRoot(string gitExe)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(gitExe))?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        if (dir.Length == 0) return null;

        string folderName = Path.GetFileName(dir);
        string parent = Path.GetDirectoryName(dir) ?? string.Empty;

        if (folderName is "bin" or "cmd")
            return parent.Length > 0 ? parent : null;

        if (folderName == "git-core")
        {
            // <root>/libexec/git-core/git → root
            string libexec = Path.GetDirectoryName(parent) ?? string.Empty;
            return libexec.Length > 0 ? libexec : null;
        }

        return null;
    }

    /// <summary>当前 git 来源的描述（含路径与版本），用于日志与状态栏显示。</summary>
    public static string Description => _description;

    /// <summary>根据当前设置解析 git，返回是否成功；结果缓存到 GitExePath。</summary>
    public static bool Resolve()
    {
        _gitExe = ResolvePreview(Settings.GitSource, Settings.CustomGitPath, out string desc);
        _description = desc;
        return _gitExe != null;
    }

    /// <summary>按指定模式解析 git（不修改缓存状态），供设置窗口实时预览/测试。</summary>
    public static string? ResolvePreview(GitSourceMode mode, string customPath, out string description)
    {
        string? found;
        switch (mode)
        {
            case GitSourceMode.Embedded:
                found = EmbeddedCandidates().FirstOrDefault(IsExecutable);
                description = found != null
                    ? $"软件内嵌：{found}"
                    : "软件目录 / .app 内未发现内嵌 Git（Resources/git/bin/git），请改用其他来源";
                break;

            case GitSourceMode.System:
                found = FindOnPath();
                description = found != null
                    ? $"系统 PATH：{found}"
                    : "系统 PATH 环境变量中未找到 git";
                break;

            case GitSourceMode.Custom:
                found = ResolveCustomPath(customPath);
                description = found != null
                    ? $"指定路径：{found}"
                    : $"指定路径中未找到可用的 git：{(string.IsNullOrWhiteSpace(customPath) ? "（空）" : customPath)}";
                break;

            default:
                found = CandidatePaths().FirstOrDefault(IsExecutable) ?? FindOnPath();
                description = found != null
                    ? $"自动检测：{found}"
                    : "自动检测：未找到 git（可安装 Xcode 命令行工具或 Homebrew git，也可在设置中指定路径）";
                break;
        }

        if (found == null) return null;

        // 校验：能真实执行 git --version 才算可用
        if (Validate(found, out string version))
        {
            description = $"{description}（{version}）";
            return found;
        }

        description += "（文件存在但执行 git --version 校验失败）";
        return null;
    }

    /// <summary>
    /// 解析用户自定义路径：支持 git 可执行文件完整路径或 Git 根目录，
    /// 目录时自动尝试 bin/git、cmd/git、git、libexec/git-core/git。支持 ~ 与环境变量展开。
    /// </summary>
    public static string? ResolveCustomPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string raw = input.Trim().Trim('"').Trim('\'');
        if (raw.StartsWith('~'))
            raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), raw.TrimStart('~').TrimStart('/'));

        string path = Environment.ExpandEnvironmentVariables(raw);
        try
        {
            if (File.Exists(path) && IsExecutable(path))
                return Path.GetFullPath(path);

            if (Directory.Exists(path))
            {
                foreach (string sub in new[]
                         {
                             Path.Combine("bin", "git"),
                             Path.Combine("cmd", "git"),
                             Path.Combine("libexec", "git-core", "git"),
                             "git"
                         })
                {
                    string candidate = Path.Combine(path, sub);
                    if (IsExecutable(candidate)) return candidate;
                }
            }
        }
        catch (Exception)
        {
            // 非法路径返回 null
        }

        return null;
    }

    /// <summary>判断文件是否存在且具备可执行权限。</summary>
    private static bool IsExecutable(string path)
    {
        try
        {
            return File.Exists(path) &&
                   (Path.GetFileName(path).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsUnixExecutable(path));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Unix 下检查任意执行位（用户/组/其他）。</summary>
    private static bool IsUnixExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception)
        {
            // 不支持 Unix 文件模式（极旧系统）时放宽为仅判断存在
            return true;
        }
    }

    /// <summary>校验 git 是否可执行：运行 git --version 并检查输出。</summary>
    public static bool Validate(string gitPath, out string version)
    {
        version = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gitPath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return false;

            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { /* 忽略 */ }
                return false;
            }

            version = output.Trim();
            return p.ExitCode == 0 && version.StartsWith("git version", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>自动检测的候选路径（按优先级）：软件内嵌 → 常见安装位置。</summary>
    private static IEnumerable<string> CandidatePaths()
        => EmbeddedCandidates().Concat(InstalledCandidates());

    /// <summary>软件内嵌的 Git 候选路径：.app/Contents/Resources/git 或软件目录 git/。</summary>
    private static IEnumerable<string> EmbeddedCandidates()
    {
        string appDir = AppContext.BaseDirectory;
        yield return Path.Combine(appDir, "git", "bin", "git");
        yield return Path.Combine(appDir, "git", "cmd", "git");
        yield return Path.Combine(appDir, "PortableGit", "bin", "git");
        // .app 包：可执行文件位于 Contents/MacOS，资源位于 Contents/Resources
        yield return Path.Combine(appDir, "..", "Resources", "git", "bin", "git");
        yield return Path.Combine(appDir, "..", "Resources", "git", "cmd", "git");
    }

    /// <summary>macOS 常见安装位置：Homebrew → Xcode 命令行工具 → MacPorts → 官方安装包 → Nix。</summary>
    private static IEnumerable<string> InstalledCandidates()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Homebrew（Apple Silicon / Intel）
        yield return "/opt/homebrew/bin/git";
        yield return "/opt/homebrew/opt/git/bin/git";
        yield return "/usr/local/bin/git";
        yield return "/usr/local/opt/git/bin/git";

        // 用户级 Homebrew
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, "homebrew", "bin", "git");
            yield return Path.Combine(home, ".homebrew", "bin", "git");
            yield return Path.Combine(home, ".nix-profile", "bin", "git");
        }

        // MacPorts / pkgsrc / Nix 全局
        yield return "/opt/local/bin/git";
        yield return "/nix/var/nix/profiles/default/bin/git";

        // Git 官方安装包（.pkg 默认安装位置）
        yield return "/usr/local/git/bin/git";

        // Xcode 命令行工具 / Xcode 内置（放最后：未安装时会弹出系统安装对话框）
        yield return "/usr/bin/git";
        yield return "/Applications/Xcode.app/Contents/Developer/usr/bin/git";
    }

    /// <summary>在系统 PATH 环境变量的各目录中查找 git。</summary>
    private static string? FindOnPath()
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                foreach (string name in new[] { "git", "git.exe" })
                {
                    string candidate = Path.Combine(dir, name);
                    if (IsExecutable(candidate)) return candidate;
                }
            }
            catch (Exception)
            {
                // 非法 PATH 条目跳过
            }
        }

        return null;
    }
}
