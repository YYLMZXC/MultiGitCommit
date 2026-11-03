using System.Text;
using System.Text.RegularExpressions;
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

/// <summary>提交身份写入 git config 的作用域。</summary>
public enum IdentityScope
{
    /// <summary>仅当前仓库（git config，写入 .git/config）。</summary>
    Local = 0,

    /// <summary>全局（git config --global，对所有仓库生效）。</summary>
    Global = 1
}

/// <summary>
/// 提交身份：git 的 user.name 与 user.email。
/// git 生成 commit 的**前置条件**——二者缺一时 git 直接报错
/// （Please tell me who you are / unable to auto-detect email address），根本生成不了提交。
/// </summary>
public class GitIdentity
{
    /// <summary>提交用户名（git config user.name）。</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>提交邮箱（git config user.email）。</summary>
    [JsonPropertyName("Email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>写入作用域：仓库级 / 全局。</summary>
    [JsonPropertyName("Scope")]
    public IdentityScope Scope { get; set; } = IdentityScope.Local;

    /// <summary>是否已完整填写（用户名与邮箱都非空）。</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Email);

    /// <summary>展示文本。</summary>
    public override string ToString() => IsComplete ? $"{Name} <{Email}>" : "（未设置）";
}

/// <summary>
/// 推送凭据（账号）的来源模式，与 git 来源（<see cref="GitSourceMode"/>）风格一致。
/// </summary>
public enum CredentialSourceMode
{
    /// <summary>自动检测（推荐）：优先用已保存的自定义账号，缺失时自动从系统凭据获取。</summary>
    Auto = 0,

    /// <summary>仅使用系统凭据：每次推送时实时向凭据管理器/helper 索取，令牌不保存到配置文件。</summary>
    System = 1,

    /// <summary>仅使用自定义账号：只使用本工具保存的凭据，不访问系统。</summary>
    Custom = 2,

    /// <summary>不使用凭据：ssh 远端或已自行配置（如 ssh-agent、URL 内已带账号）。</summary>
    None = 3
}

/// <summary>
/// 推送凭据：按远端主机保存用户名与访问令牌。
/// 仅用于 https / http 远端：工具为非交互执行（GIT_TERMINAL_PROMPT=0），
/// 无凭据时 git 无法弹窗询问账号，推送必然失败。
/// </summary>
public class GitCredential
{
    /// <summary>远端主机（如 cnb.cool、gitee.com、github.com），也可填完整 URL。</summary>
    [JsonPropertyName("Host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>用户名（平台账号名）。</summary>
    [JsonPropertyName("Username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>访问令牌 / 密码（推送时作为密码使用）。</summary>
    [JsonPropertyName("Token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>列表展示文本（不含令牌）。</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Username) ? Host : $"{Host} — {Username}";
}

/// <summary>
/// 应用级全局设置（与具体仓库无关），跨平台持久化：
/// Windows → <c>%APPDATA%\MultiGitCommit\settings.json</c>；
/// macOS   → <c>~/Library/Application Support/MultiGitCommit/settings.json</c>。
/// 两端字段完全一致，配置文件可通用。
/// </summary>
public class AppSettings
{
    /// <summary>git 来源模式，默认自动检测。</summary>
    [JsonPropertyName("GitSource")]
    public GitSourceMode GitSource { get; set; } = GitSourceMode.Auto;

    /// <summary>用户指定的 git 路径或 Git 根目录（仅 Custom 模式使用）。</summary>
    [JsonPropertyName("CustomGitPath")]
    public string CustomGitPath { get; set; } = string.Empty;

    /// <summary>https 远端推送凭据列表（按主机匹配，与具体仓库无关）。</summary>
    [JsonPropertyName("Credentials")]
    public List<GitCredential> Credentials { get; set; } = new();

    /// <summary>推送凭据（账号）来源模式，默认自动检测。</summary>
    [JsonPropertyName("CredentialSource")]
    public CredentialSourceMode CredentialSource { get; set; } = CredentialSourceMode.Auto;

    /// <summary>提交身份（user.name / user.email），提交前置条件；缺省时首次提交会引导填写。</summary>
    [JsonPropertyName("CommitIdentity")]
    public GitIdentity CommitIdentity { get; set; } = new();

    /// <summary>上次成功加载的仓库根目录，下次启动自动载入该项目的 multigitcommit.config.json。</summary>
    [JsonPropertyName("LastRepoPath")]
    public string LastRepoPath { get; set; } = string.Empty;

    /// <summary>设置文件完整路径（加载时填充）。</summary>
    [JsonIgnore]
    public string SettingsFilePath { get; private set; } = string.Empty;

    /// <summary>
    /// 设置目录：Windows 用 %APPDATA%，macOS 用 ~/Library/Application Support（各自平台惯例）。
    /// </summary>
    private static string SettingsDir =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiGitCommit")
            : Path.Combine(
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
                    loaded.Credentials ??= new List<GitCredential>();
                    loaded.CommitIdentity ??= new GitIdentity();
                    loaded.LastRepoPath ??= string.Empty;
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

        // 凭据以明文保存，收紧为仅当前用户可读写（chmod 600），避免同机其他账号读取
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(SettingsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // 文件系统不支持权限位时忽略
            }
        }
    }
}

/// <summary>
/// git 定位器（Windows / macOS 通用）：使工具【不依赖系统是否安装 / 配置了 git】。
///
/// 四种来源：
/// 1. Auto     自动检测：软件内嵌 Git → 常见安装位置 → 系统 PATH；
/// 2. System   仅使用系统 PATH 中的 git；
/// 3. Custom   用户指定的 git 可执行文件或 Git 根目录；
/// 4. Embedded 仅使用软件内嵌的 Git（Windows：软件目录 git\cmd\git.exe；macOS：软件目录 git/bin/git 或 .app 内 Resources/git）。
///
/// 说明：macOS 从 Finder / Dock 启动的 GUI 应用 PATH 通常只有 /usr/bin:/bin:/usr/sbin:/sbin，
/// 因此 Homebrew 等路径必须显式枚举，而不能只依赖 PATH 查找；Windows 同理优先用随软件分发的便携版 Git。
/// 平台相关的候选路径 / 可执行判定统一收在 <c>OperatingSystem.IsWindows()</c> 分支里。
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
    /// 需要注入子进程 PATH 的目录集合：
    /// 1. git 所在目录（如便携版 MinGit 的 git\cmd\）；
    /// 2. Git 根目录下的 mingw64\bin、usr\bin（Windows）或 bin、libexec/git-core（macOS）
    ///    ——ssh(.exe)、git-lfs 等附带程序所在位置，保证便携版 / 内嵌 git 执行 ssh 远端推送时无需系统安装组件；
    /// 3. macOS 常见命令目录（GUI 应用 PATH 常缺失 /opt/homebrew/bin、/usr/local/bin 等）。
    /// </summary>
    public static IReadOnlyList<string> GetPathEntries()
    {
        var entries = new List<string>();

        if (!OperatingSystem.IsWindows())
        {
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
                // 两个平台的 Git 发行版布局不同：Windows（MinGit）在 mingw64\bin、usr\bin，
                // macOS / Unix 在 bin、libexec\git-core
                foreach (string sub in OperatingSystem.IsWindows()
                             ? new[]
                             {
                                 Path.Combine(root, "mingw64", "bin"),
                                 Path.Combine(root, "usr", "bin")
                             }
                             : new[]
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
    /// 根据 git 路径推断 Git 根目录：
    /// &lt;root&gt;/bin/git、&lt;root&gt;/cmd/git、&lt;root&gt;/libexec/git-core/git、&lt;root&gt;/mingw64/bin/git.exe → root。
    /// </summary>
    private static string? TryGetGitRoot(string gitExe)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(gitExe))
            ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
        if (dir.Length == 0) return null;

        string folderName = Path.GetFileName(dir);
        string parent = Path.GetDirectoryName(dir) ?? string.Empty;

        if (string.Equals(folderName, "bin", StringComparison.OrdinalIgnoreCase))
        {
            // <root>\mingw64\bin\git.exe → 再上一级才是 Git 根目录
            string parentName = Path.GetFileName(parent);
            if (string.Equals(parentName, "mingw64", StringComparison.OrdinalIgnoreCase))
            {
                string grandparent = Path.GetDirectoryName(parent) ?? string.Empty;
                return grandparent.Length > 0 ? grandparent : null;
            }

            return parent.Length > 0 ? parent : null;
        }

        if (string.Equals(folderName, "cmd", StringComparison.OrdinalIgnoreCase))
            return parent.Length > 0 ? parent : null;

        if (string.Equals(folderName, "git-core", StringComparison.OrdinalIgnoreCase))
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
                // Windows（MinGit / Git for Windows）与 Unix（Homebrew / 内嵌）的布局不同，两套候选都试
                foreach (string sub in new[]
                         {
                             Path.Combine("cmd", "git.exe"),
                             Path.Combine("bin", "git.exe"),
                             Path.Combine("mingw64", "bin", "git.exe"),
                             "git.exe",
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

    /// <summary>Unix 下检查任意执行位（用户/组/其他）；Windows 上不适用，一律视为可执行。</summary>
    private static bool IsUnixExecutable(string path)
    {
        // File.GetUnixFileMode 只在 Unix 上受支持，Windows 调用会抛 PlatformNotSupportedException
        if (OperatingSystem.IsWindows()) return true;

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

        // 只执行 --version：不注入 PATH / 交互环境（候选路径探测会频繁调用，保持轻量）
        GitCommandResult result = GitCliRunner.Default.Run(new GitCommandRequest
        {
            FileName = gitPath,
            Arguments = "--version",
            Timeout = TimeSpan.FromSeconds(5),
            TreatAsGit = false
        });

        if (!result.Success) return false;

        version = result.Output;
        return version.StartsWith("git version", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>自动检测的候选路径（按优先级）：软件内嵌 → 常见安装位置。</summary>
    private static IEnumerable<string> CandidatePaths()
        => EmbeddedCandidates().Concat(InstalledCandidates());

    /// <summary>
    /// 软件内嵌的 Git 候选路径：
    /// Windows → 软件目录 git\cmd\git.exe、PortableGit\cmd\git.exe；
    /// macOS   → 软件目录 git/bin/git，或 .app 包内 Contents/Resources/git。
    /// </summary>
    private static IEnumerable<string> EmbeddedCandidates()
    {
        string appDir = AppContext.BaseDirectory;

        // 单文件发布（Release）时 AppContext.BaseDirectory 不一定是 exe 所在目录，
        // 用 exe 自身路径兜底，保证 git\ 子目录里的内嵌 Git 总能找到。
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            yield return Path.Combine(exeDir, "git", "cmd", "git.exe");
            yield return Path.Combine(exeDir, "git", "bin", "git");
        }

        // Windows / 便携版 Git 布局（随软件分发的 MinGit）
        yield return Path.Combine(appDir, "git", "cmd", "git.exe");
        yield return Path.Combine(appDir, "PortableGit", "cmd", "git.exe");
        yield return Path.Combine(appDir, "git", "bin", "git.exe");

        // Unix / macOS 布局
        yield return Path.Combine(appDir, "git", "bin", "git");
        yield return Path.Combine(appDir, "git", "cmd", "git");
        yield return Path.Combine(appDir, "PortableGit", "bin", "git");

        // .app 包：可执行文件位于 Contents/MacOS，资源位于 Contents/Resources
        yield return Path.Combine(appDir, "..", "Resources", "git", "bin", "git");
        yield return Path.Combine(appDir, "..", "Resources", "git", "cmd", "git");
    }

    /// <summary>
    /// 常见安装位置候选：
    /// Windows → Program Files / 用户级安装 / scoop / winget；
    /// macOS   → Homebrew → Xcode 命令行工具 → MacPorts → 官方安装包 → Nix。
    /// </summary>
    private static IEnumerable<string> InstalledCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string candidate in WindowsInstalledCandidates()) yield return candidate;
            yield break;
        }

        foreach (string candidate in UnixInstalledCandidates()) yield return candidate;
    }

    /// <summary>Windows 常见安装位置：Program Files / 用户级安装 / scoop / winget。</summary>
    private static IEnumerable<string> WindowsInstalledCandidates()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (string root in new[] { programFiles, programFilesX86, localPrograms })
        {
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "Git", "cmd", "git.exe");
        }

        // scoop / winget 包管理器安装位置
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            yield return Path.Combine(userProfile, "scoop", "apps", "git", "current", "cmd", "git.exe");
            yield return Path.Combine(userProfile, "scoop", "shims", "git.exe");
        }

        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "git.exe");
    }

    /// <summary>macOS 常见安装位置：Homebrew → Xcode 命令行工具 → MacPorts → 官方安装包 → Nix。</summary>
    private static IEnumerable<string> UnixInstalledCandidates()
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

    /// <summary>
    /// 查找 ssh：优先 git 自带的（Git 根目录下 usr\bin / bin / libexec/git-core），其次系统 PATH。
    /// 仅用于非交互 ssh 参数（GIT_SSH_COMMAND）；找不到时不设置该变量，
    /// 避免覆盖 git 自身的 ssh 查找逻辑导致 ssh 远端推送失败。
    /// </summary>
    public static string? FindSshExe()
    {
        string fileName = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh";

        foreach (string dir in GetPathEntries())
        {
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (IsExecutable(candidate)) return candidate;
            }
            catch (Exception)
            {
                // 非法路径条目跳过
            }
        }

        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (IsExecutable(candidate)) return candidate;
            }
            catch (Exception)
            {
                // 非法 PATH 条目跳过
            }
        }

        return null;
    }
}

/// <summary>
/// 远端 URL 凭据处理：按主机查找凭据、把凭据注入 https URL、日志脱敏。
///
/// 工具为非交互执行（GIT_TERMINAL_PROMPT=0），https 远端没有凭据时 git 无法弹窗问账号，
/// 推送必然失败（fatal: could not read Username ... terminal prompts disabled）。
/// 因此这里把 用户名:令牌 直接注入 URL：https://user:token@host/path。
/// </summary>
public static class GitCredentials
{
    /// <summary>提取远端 URL 的主机（小写，去端口、去 userinfo）；无法解析返回 null。</summary>
    public static string? GetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        string s = url.Trim();
        string authority;

        int schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            string rest = s[(schemeEnd + 3)..];
            int slash = rest.IndexOf('/');
            authority = slash < 0 ? rest : rest[..slash];
        }
        else if (s.Contains('@') && s.Contains(':'))
        {
            // scp 形式：git@host:group/repo.git
            int at = s.IndexOf('@', StringComparison.Ordinal);
            int colon = s.IndexOf(':', at + 1);
            authority = colon < 0 ? s[(at + 1)..] : s[(at + 1)..colon];
        }
        else
        {
            // 仅填了主机名或完整 URL 之外的简写
            return s.Length == 0 ? null : s.ToLowerInvariant();
        }

        int userInfoEnd = authority.IndexOf('@', StringComparison.Ordinal);
        if (userInfoEnd >= 0) authority = authority[(userInfoEnd + 1)..];

        int portSep = authority.IndexOf(':', StringComparison.Ordinal);
        if (portSep >= 0) authority = authority[..portSep];

        return authority.Length == 0 ? null : authority.ToLowerInvariant();
    }

    /// <summary>从凭据列表中查找与该远端 URL 主机匹配的凭据。</summary>
    public static GitCredential? Find(string? url, IEnumerable<GitCredential> credentials)
    {
        string? host = GetHost(url);
        if (string.IsNullOrEmpty(host)) return null;

        foreach (var cred in credentials)
        {
            if (cred == null || string.IsNullOrWhiteSpace(cred.Host)) continue;
            string credHost = GetHost(cred.Host) ?? cred.Host.Trim().ToLowerInvariant();
            if (string.Equals(credHost, host, StringComparison.OrdinalIgnoreCase))
                return cred;
        }

        return null;
    }

    /// <summary>该远端是否需要凭据：仅 https/http 且 URL 中尚未内嵌用户名。</summary>
    public static bool NeedsCredential(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        string s = url.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        int schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        string rest = s[(schemeEnd + 3)..];
        int slash = rest.IndexOf('/');
        string authority = slash < 0 ? rest : rest[..slash];
        return !authority.Contains('@', StringComparison.Ordinal);
    }

    /// <summary>把凭据注入远端 URL，返回 https://user:token@host/path（令牌做 URL 编码）。</summary>
    public static string Apply(string url, GitCredential cred)
    {
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return url;

        string scheme = url[..schemeEnd];
        string rest = url[(schemeEnd + 3)..];
        int slash = rest.IndexOf('/');
        string authority = slash < 0 ? rest : rest[..slash];
        string tail = slash < 0 ? string.Empty : rest[slash..];

        int userInfoEnd = authority.IndexOf('@', StringComparison.Ordinal);
        string host = userInfoEnd >= 0 ? authority[(userInfoEnd + 1)..] : authority;

        string userInfo = Uri.EscapeDataString(cred.Username ?? string.Empty) + ":" +
                          Uri.EscapeDataString(cred.Token ?? string.Empty);
        return $"{scheme}://{userInfo}@{host}{tail}";
    }

    /// <summary>匹配任意文本中的 URL 凭据部分：scheme://user:secret@host</summary>
    private static readonly Regex CredentialPattern = new(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)(?<user>[^\s:/@]+):(?<secret>[^@\s]+)@",
        RegexOptions.Compiled);

    /// <summary>
    /// 对任意文本做凭据脱敏：把 scheme://user:secret@host 中的密钥替换为 ***。
    /// 用于兜底——git 自身的报错 / 回显里可能带出完整 URL，不能让令牌明文落入日志。
    /// </summary>
    public static string MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return CredentialPattern.Replace(text,
            m => $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:***@");
    }

    /// <summary>日志脱敏：把 URL 中的令牌替换为 ***，避免令牌明文进入日志。</summary>
    public static string Mask(string url)
    {
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return url;

        string rest = url[(schemeEnd + 3)..];
        int slash = rest.IndexOf('/');
        string authority = slash < 0 ? rest : rest[..slash];
        string tail = slash < 0 ? string.Empty : rest[slash..];

        int userInfoEnd = authority.IndexOf('@', StringComparison.Ordinal);
        if (userInfoEnd < 0) return url;

        string userInfo = authority[..userInfoEnd];
        int colon = userInfo.IndexOf(':', StringComparison.Ordinal);
        string name = colon < 0 ? userInfo : userInfo[..colon];

        return $"{url[..(schemeEnd + 3)]}{name}:***@{authority[(userInfoEnd + 1)..]}{tail}";
    }
}
