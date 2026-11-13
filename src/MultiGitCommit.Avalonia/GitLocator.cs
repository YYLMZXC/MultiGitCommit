using System.Text.RegularExpressions;

namespace MultiGitCommit;

/// <summary>
/// git 定位器（Windows / macOS / Linux 通用）：使工具【不依赖系统是否安装 / 配置了 git】。
///
/// 四种来源：
/// 1. Auto     自动检测：软件内嵌 Git → 常见安装位置 → 系统 PATH；
/// 2. System   仅使用系统 PATH 中的 git；
/// 3. Custom   用户指定的 git 可执行文件或 Git 根目录；
/// 4. Embedded 仅使用软件内嵌的 Git（Windows：软件目录 git\cmd\git.exe；
///             macOS / Linux：软件目录 git/bin/git，macOS 还支持 .app 内 Resources/git）。
///
/// 说明：macOS 从 Finder / Dock 启动的 GUI 应用 PATH 通常只有 /usr/bin:/bin:/usr/sbin:/sbin，
/// Linux 从桌面图标（.desktop）启动时同样拿不到用户 shell 的 PATH（如 ~/.local/bin、Linuxbrew），
/// 因此这些路径必须显式枚举，而不能只依赖 PATH 查找；Windows 同理优先用随软件分发的便携版 Git。
/// 平台相关的候选路径 / 可执行判定统一收在 <c>OperatingSystem.IsWindows()</c> / <c>IsMacOS()</c> 分支里。
/// </summary>
public static class GitLocator
{
    /// <summary>
    /// 当前环境快照。整体替换（引用赋值）而非逐字段修改，
    /// 保证读取方不会看到「git 已换新、PATH / ssh 结论仍是旧的」这类中间态。
    /// </summary>
    private static volatile GitEnvironment _current = Build(null, string.Empty);

    /// <summary>
    /// 版本探测专用执行器：固定使用空环境。
    /// 「验证 git 能不能跑」发生在环境解析**过程中**，此时不能依赖尚未定稿的快照，
    /// 否则会读到上一次解析的旧 PATH / ssh 结论。探测本身也不需要 PATH 注入。
    /// </summary>
    private static readonly IGitCliRunner ProbeRunner =
        new GitCliRunner(static () => GitEnvironment.Empty);

    /// <summary>当前环境快照（git 路径、来源描述、PATH 注入目录、ssh 路径一次取全）。</summary>
    public static GitEnvironment Current => _current;

    /// <summary>当前生效的 git 完整路径；未找到时为 null。</summary>
    public static string? GitExePath => _current.GitExePath;

    /// <summary>进程调用的 git 文件名：已解析则用完整路径，否则退回 "git"。</summary>
    public static string GitExe => _current.GitExe;

    /// <summary>git 可执行文件所在目录（注入子进程 PATH 用）；未解析时为 null。</summary>
    public static string? GitDirectory => _current.GitDirectory;

    /// <summary>当前快照里需要注入子进程 PATH 的目录（已排序、已去重、仅含存在的目录）。</summary>
    public static IReadOnlyList<string> GetPathEntries() => _current.PathEntries;

    /// <summary>
    /// 为指定 git 计算需要注入子进程 PATH 的目录集合：
    /// 1. git 所在目录（如便携版 MinGit 的 git\cmd\）；
    /// 2. Git 根目录下的 mingw64\bin、usr\bin（Windows）或 bin、libexec/git-core（macOS / Linux）
    ///    ——ssh(.exe)、git-lfs 等附带程序所在位置，保证便携版 / 内嵌 git 执行 ssh 远端推送时无需系统安装组件；
    /// 3. macOS / Linux 常见命令目录（从 Finder / 桌面图标启动的 GUI 应用 PATH 常缺失
    ///    /opt/homebrew/bin、~/.local/bin、/snap/bin 等）。
    /// 纯算法，无外部状态：结果只取决于入参与当前文件系统。
    /// </summary>
    private static List<string> BuildPathEntries(string? gitExe)
    {
        var entries = new List<string>();

        if (OperatingSystem.IsMacOS())
        {
            AddExistingDirectories(entries, MacPathDirectories());
        }
        else if (!OperatingSystem.IsWindows())
        {
            AddExistingDirectories(entries, LinuxPathDirectories());
        }

        if (gitExe == null) return entries;

        try
        {
            string? gitDir = Path.GetDirectoryName(Path.GetFullPath(gitExe));
            if (!string.IsNullOrEmpty(gitDir) && !entries.Contains(gitDir))
                entries.Insert(0, gitDir);

            string? root = TryGetGitRoot(gitExe);
            if (root != null)
            {
                // 各平台的 Git 发行版布局不同：Windows（MinGit）在 mingw64\bin、usr\bin，
                // macOS / Linux 在 bin、libexec/git-core
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

    /// <summary>把存在的目录按顺序追加到列表（跳过空项与重复项）。</summary>
    private static void AddExistingDirectories(List<string> entries, IEnumerable<string> dirs)
    {
        foreach (string dir in dirs)
        {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && !entries.Contains(dir))
                entries.Add(dir);
        }
    }

    /// <summary>
    /// macOS：从 Finder / Dock 启动时 PATH 常缺失的常用目录（Homebrew、MacPorts、用户级安装）。
    /// </summary>
    private static IEnumerable<string> MacPathDirectories()
    {
        yield return "/opt/homebrew/bin";
        yield return "/opt/homebrew/sbin";
        yield return "/usr/local/bin";
        yield return "/usr/local/sbin";
        yield return "/opt/local/bin";
        yield return "/usr/bin";
        yield return "/bin";
        yield return "/usr/sbin";
        yield return "/sbin";

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".nix-profile", "bin");
        }
    }

    /// <summary>
    /// Linux：从 .desktop 图标启动时 PATH 常缺失的常用目录
    /// （用户级安装、Linuxbrew、snap、Nix、发行版标准目录）。
    /// </summary>
    private static IEnumerable<string> LinuxPathDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".local", "sbin");
            yield return Path.Combine(home, ".linuxbrew", "bin");
            yield return Path.Combine(home, ".nix-profile", "bin");
        }

        yield return "/home/linuxbrew/.linuxbrew/bin";
        yield return "/usr/local/bin";
        yield return "/usr/local/sbin";
        yield return "/usr/bin";
        yield return "/bin";
        yield return "/usr/sbin";
        yield return "/sbin";
        yield return "/snap/bin";
        yield return "/nix/var/nix/profiles/default/bin";
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
    public static string Description => _current.Description;

    /// <summary>
    /// 按指定来源解析 git 并替换当前快照，返回是否成功。
    /// 来源由调用方传入，而不是在内部去读应用设置单例——定位器不再隐式依赖全局状态。
    /// </summary>
    public static bool Resolve(GitSourceMode mode, string? customPath)
    {
        string? found = ResolvePreview(mode, customPath, out string description);
        _current = Build(found, description);
        return found != null;
    }

    /// <summary>
    /// 由「git 路径 + 描述」构造完整快照，一次性算好 PATH 注入目录与 ssh 路径。
    /// 这是唯一写 <see cref="_current"/> 的地方，保证读取方拿到的快照整体一致。
    /// </summary>
    private static GitEnvironment Build(string? gitExe, string description)
    {
        List<string> entries = BuildPathEntries(gitExe);

        return new GitEnvironment
        {
            GitExePath = gitExe,
            Description = description,
            PathEntries = entries,
            SshExe = FindSshExe(entries)
        };
    }

    /// <summary>按指定模式解析 git（不修改当前快照），供设置窗口实时预览/测试。</summary>
    public static string? ResolvePreview(GitSourceMode mode, string? customPath, out string description)
    {
        // iOS：沙盒禁止 fork/exec 子进程，任何来源（内嵌 / 系统 / 自定义）都拿不到可执行文件。
        // git 能力来自链接进 App 的 libgit2（IosGitRunner），这里返回伪路径并跳过 Validate。
        if (OperatingSystem.IsIOS())
        {
            description = Loc.F($"iOS 内嵌 libgit2 {IosLibgit2.IosGitRunner.Libgit2Version}（进程内实现；仅支持 https 远端）");
            return IosLibgit2.IosGitRunner.PseudoGitPath;
        }

        string? found;
        switch (mode)
        {
            case GitSourceMode.Embedded:
                found = EmbeddedCandidates().FirstOrDefault(IsExecutable);
                description = found != null
                    ? Loc.F($"软件内嵌：{found}")
                    : OperatingSystem.IsAndroid()
                        ? Loc.T("APK 原生库目录未发现内嵌 Git（libgit.so），请改用其他来源")
                        : Loc.T("软件目录 / .app 内未发现内嵌 Git（git/bin/git 或 Resources/git/bin/git），请改用其他来源");
                break;

            case GitSourceMode.System:
                found = FindOnPath();
                description = found != null
                    ? Loc.F($"系统 PATH：{found}")
                    : Loc.T("系统 PATH 环境变量中未找到 git");
                break;

            case GitSourceMode.Custom:
                found = ResolveCustomPath(customPath);
                description = found != null
                    ? Loc.F($"指定路径：{found}")
                    : Loc.F($"指定路径中未找到可用的 git：{(string.IsNullOrWhiteSpace(customPath) ? Loc.T("（空）") : customPath)}");
                break;

            default:
                found = CandidatePaths().FirstOrDefault(IsExecutable) ?? FindOnPath();
                description = found != null
                    ? Loc.F($"自动检测：{found}")
                    : Loc.F($"自动检测：未找到 git（macOS 可安装 Xcode 命令行工具或 Homebrew；Linux 可用系统包管理器安装 git 或 Linuxbrew；也可在设置中指定路径）");
                break;
        }

        if (found == null) return null;

        // 校验：能真实执行 git --version 才算可用
        if (Validate(found, out string version))
        {
            description = $"{description}（{version}）";
            return found;
        }

        description += Loc.T("（文件存在但执行 git --version 校验失败）");
        return null;
    }

    /// <summary>
    /// 解析用户自定义路径：支持 git 可执行文件完整路径或 Git 根目录，
    /// 目录时自动尝试 bin/git、cmd/git、git、libexec/git-core/git。
    /// 支持 ~ 与环境变量展开（Windows 的 %VAR% 与 Unix 的 $VAR / ${VAR}）。
    /// </summary>
    public static string? ResolveCustomPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string raw = input.Trim().Trim('"').Trim('\'');
        if (raw.StartsWith('~'))
            raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), raw.TrimStart('~').TrimStart('/'));

        string path = ExpandUnixVariables(Environment.ExpandEnvironmentVariables(raw));
        try
        {
            if (File.Exists(path) && IsExecutable(path))
                return Path.GetFullPath(path);

            if (Directory.Exists(path))
            {
                // Windows（MinGit / Git for Windows）与 Unix（macOS / Linux 的 Homebrew、发行版包、内嵌）布局不同，两套候选都试
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

    /// <summary>
    /// 展开 Unix 风格的 <c>$VAR</c> / <c>${VAR}</c>：
    /// Linux / macOS 用户习惯这种写法，而 <see cref="Environment.ExpandEnvironmentVariables"/> 只认 %VAR%。
    /// 变量不存在时保留原样，交给后续的存在性检查报错。Windows 上不处理（$ 是合法路径字符）。
    /// </summary>
    private static string ExpandUnixVariables(string input)
    {
        if (OperatingSystem.IsWindows() || !input.Contains('$')) return input;

        try
        {
            return Regex.Replace(
                input,
                @"\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|\$(?<plain>[A-Za-z_][A-Za-z0-9_]*)",
                m =>
                {
                    string name = m.Groups["braced"].Success ? m.Groups["braced"].Value : m.Groups["plain"].Value;
                    return Environment.GetEnvironmentVariable(name) ?? m.Value;
                });
        }
        catch (Exception)
        {
            return input;
        }
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

        // 只执行 --version：不注入 PATH / 交互环境（候选路径探测会频繁调用，保持轻量）。
        // Android：动态链接的 libgit.so 依赖同目录的 lib*.so，探测也必须能找到（LD_LIBRARY_PATH）。
        GitCommandResult result = ProbeRunner.Run(new GitCommandRequest
        {
            FileName = gitPath,
            Arguments = "--version",
            Timeout = TimeSpan.FromSeconds(5),
            TreatAsGit = false,
            Environment = AndroidGitPlatform.LoaderEnvironment()
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
    /// macOS / Linux → 软件目录 git/bin/git，macOS 还支持 .app 包内 Contents/Resources/git。
    /// </summary>
    private static IEnumerable<string> EmbeddedCandidates()
    {
#if ANDROID
        // Android：内嵌 git 以 lib*.so 形式打进 APK 原生库目录（Android 10+ 禁止执行应用数据
        // 目录里的文件，原生库目录是唯一保持可执行的落点），运行时支撑见 AndroidGitPlatform。
        string? androidNativeDir = AndroidGitPlatform.NativeLibraryDir;
        if (!string.IsNullOrEmpty(androidNativeDir))
            yield return Path.Combine(androidNativeDir, "libgit.so");
#endif
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

        // Unix（macOS / Linux）布局
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
    /// macOS   → Homebrew → Xcode 命令行工具 → MacPorts → 官方安装包 → Nix；
    /// Linux   → 用户级安装 / Linuxbrew → 发行版标准目录 → snap / Nix → 源码安装。
    /// </summary>
    private static IEnumerable<string> InstalledCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string candidate in WindowsInstalledCandidates()) yield return candidate;
            yield break;
        }

        IEnumerable<string> candidates = OperatingSystem.IsMacOS()
            ? MacInstalledCandidates()
            : LinuxInstalledCandidates();

        foreach (string candidate in candidates) yield return candidate;
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
    private static IEnumerable<string> MacInstalledCandidates()
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

    /// <summary>
    /// Linux 常见安装位置：用户级安装 / Linuxbrew → 发行版标准目录 → snap / Nix → 源码安装。
    /// 覆盖 Debian/Ubuntu、Fedora/RHEL、Arch、openSUSE 等常见布局（git 基本都在 /usr/bin）。
    /// </summary>
    private static IEnumerable<string> LinuxInstalledCandidates()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 用户级安装（手动编译 prefix=~/.local、Linuxbrew、Nix）
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".local", "bin", "git");
            yield return Path.Combine(home, ".linuxbrew", "bin", "git");
            yield return Path.Combine(home, ".nix-profile", "bin", "git");
        }

        yield return "/home/linuxbrew/.linuxbrew/bin/git";

        // 发行版包管理器（apt / dnf / pacman / zypper 等）安装位置
        yield return "/usr/local/bin/git";
        yield return "/usr/bin/git";
        yield return "/bin/git";

        // snap / Nix 全局
        yield return "/snap/bin/git";
        yield return "/nix/var/nix/profiles/default/bin/git";

        // 源码安装（configure --prefix=/usr/local/git 或 /opt/git）
        yield return "/usr/local/git/bin/git";
        yield return "/opt/git/bin/git";
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
    public static string? FindSshExe() => _current.SshExe;

    /// <summary>在已算好的 PATH 注入目录中查找 ssh，找不到再回退到系统 PATH。纯算法，无外部状态。</summary>
    private static string? FindSshExe(IReadOnlyList<string> entries)
    {
        string fileName = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh";

        foreach (string dir in entries)
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
                // 非法路径条目跳过
            }
        }

        return null;
    }
}
