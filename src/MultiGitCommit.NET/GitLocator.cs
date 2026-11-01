using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MultiGitCommit
{
    /// <summary>git.exe 来源模式。</summary>
    public enum GitSourceMode
    {
        /// <summary>自动检测：软件目录便携版 Git → 常见安装位置 → 系统 PATH。</summary>
        Auto = 0,

        /// <summary>仅使用系统 PATH 环境变量中的 git（环境 git）。</summary>
        System = 1,

        /// <summary>用户指定的 git.exe / Git 目录（便携版、软件自带 git）。</summary>
        Custom = 2,

        /// <summary>仅使用软件目录内嵌的便携版 Git（git\ 或 PortableGit\ 子目录）。</summary>
        Embedded = 3
    }

    /// <summary>
    /// 推送凭据：按远端主机保存用户名与访问令牌（账号）。
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
    /// 应用级全局设置（与具体仓库无关），持久化到
    /// <c>%APPDATA%\MultiGitCommit\settings.json</c>。
    /// </summary>
    public class AppSettings
    {
        /// <summary>git.exe 来源模式，默认自动检测。</summary>
        [JsonPropertyName("GitSource")]
        public GitSourceMode GitSource { get; set; } = GitSourceMode.Auto;

        /// <summary>用户指定的 git.exe 完整路径或 Git 根目录（仅 Custom 模式使用）。</summary>
        [JsonPropertyName("CustomGitPath")]
        public string CustomGitPath { get; set; } = string.Empty;

        /// <summary>https 远端推送凭据列表（按主机匹配，与具体仓库无关）。</summary>
        [JsonPropertyName("Credentials")]
        public List<GitCredential> Credentials { get; set; } = new();

        /// <summary>设置文件完整路径（加载时填充）。</summary>
        [JsonIgnore]
        public string SettingsFilePath { get; private set; } = string.Empty;

        private static string SettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiGitCommit");

        /// <summary>从 %APPDATA% 读取设置；文件不存在或损坏时返回默认设置。</summary>
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

        /// <summary>保存设置到 %APPDATA%\MultiGitCommit\settings.json。</summary>
        public void Save()
        {
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json, new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// git.exe 定位器：使工具【不依赖系统安装 / PATH 中的 git】。
    ///
    /// 四种来源：
    /// 1. Auto     自动检测：优先软件目录下的便携版 Git（git\ 或 PortableGit\），
    ///             其次常见安装位置（Program Files、scoop、winget 等），最后系统 PATH；
    /// 2. System   仅使用系统 PATH 环境变量中的 git（环境 git）；
    /// 3. Custom   用户指定的 git.exe 或 Git 根目录（软件自带 / 便携版 git）；
    /// 4. Embedded 仅使用软件目录内嵌的便携版 Git（git\ 子目录，随软件分发）。
    /// </summary>
    public static class GitLocator
    {
        private static AppSettings? _settings;

        /// <summary>应用级设置（首次访问时从 %APPDATA% 加载）。</summary>
        public static AppSettings Settings => _settings ??= AppSettings.Load();

        private static string? _gitExe;
        private static string _description = string.Empty;

        /// <summary>当前生效的 git.exe 完整路径；未找到时为 null。</summary>
        public static string? GitExePath => _gitExe;

        /// <summary>进程调用的 git 文件名：已解析则用完整路径，否则退回 "git"。</summary>
        public static string GitExe => _gitExe ?? "git";

        /// <summary>git.exe 所在目录（注入子进程 PATH 用）；未解析时为 null。</summary>
        public static string? GitDirectory => _gitExe == null ? null : Path.GetDirectoryName(_gitExe);

        /// <summary>
        /// 需要注入子进程 PATH 的目录集合：
        /// 1. git.exe 所在目录（如便携版 MinGit 的 git\cmd\）；
        /// 2. Git 根目录下的 mingw64\bin、usr\bin（ssh.exe、git-lfs 等附带程序所在位置），
        ///    保证便携版 / 内嵌 git 执行 ssh 远端推送时无需系统安装任何 git 组件。
        /// </summary>
        public static IReadOnlyList<string> GetPathEntries()
        {
            var entries = new List<string>();
            if (_gitExe == null) return entries;

            try
            {
                string? gitDir = Path.GetDirectoryName(Path.GetFullPath(_gitExe));
                if (!string.IsNullOrEmpty(gitDir))
                    entries.Add(gitDir);

                string? root = TryGetGitRoot(_gitExe);
                if (root != null)
                {
                    foreach (string sub in new[]
                             {
                                 Path.Combine(root, "mingw64", "bin"),
                                 Path.Combine(root, "usr", "bin")
                             })
                    {
                        if (Directory.Exists(sub) && !entries.Contains(sub))
                            entries.Add(sub);
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
        /// 根据 git.exe 路径推断 Git 根目录：
        /// &lt;root&gt;\cmd\git.exe、&lt;root&gt;\bin\git.exe、&lt;root&gt;\mingw64\bin\git.exe → root。
        /// </summary>
        private static string? TryGetGitRoot(string gitExe)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(gitExe))
                ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
            if (dir.Length == 0) return null;

            string parent = Path.GetDirectoryName(dir) ?? string.Empty;
            string folderName = Path.GetFileName(dir);

            if (folderName.Equals("cmd", StringComparison.OrdinalIgnoreCase))
                return parent.Length > 0 ? parent : null;

            if (folderName.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                // <root>\bin\git.exe → root；<root>\mingw64\bin\git.exe → 再上一级
                string parentName = Path.GetFileName(parent);
                if (parentName.Equals("mingw64", StringComparison.OrdinalIgnoreCase))
                {
                    string grandparent = Path.GetDirectoryName(parent) ?? string.Empty;
                    return grandparent.Length > 0 ? grandparent : null;
                }
                return parent.Length > 0 ? parent : null;
            }

            return null;
        }

        /// <summary>当前 git 来源的描述（含路径与版本），用于日志与状态栏显示。</summary>
        public static string Description => _description;

        /// <summary>根据当前设置解析 git.exe，返回是否成功；结果缓存到 GitExePath。</summary>
        public static bool Resolve()
        {
            _gitExe = ResolvePreview(Settings.GitSource, Settings.CustomGitPath, out string desc);
            _description = desc;
            return _gitExe != null;
        }

        /// <summary>
        /// 按指定模式解析 git.exe（不修改缓存状态），供设置窗口实时预览/测试。
        /// </summary>
        /// <param name="mode">来源模式。</param>
        /// <param name="customPath">Custom 模式下的用户路径（git.exe 或 Git 根目录）。</param>
        /// <param name="description">输出解析结果描述（成功含路径与版本，失败含原因）。</param>
        /// <returns>git.exe 完整路径；未找到返回 null。</returns>
        public static string? ResolvePreview(GitSourceMode mode, string customPath, out string description)
        {
            string? found;
            switch (mode)
            {
                case GitSourceMode.Embedded:
                    found = EmbeddedCandidates().FirstOrDefault(File.Exists);
                    description = found != null
                        ? $"软件内嵌：{found}"
                        : "软件目录未发现内嵌便携版 Git（git\\cmd\\git.exe），请重新发布或改用其他来源";
                    break;

                case GitSourceMode.System:
                    found = FindOnPath();
                    description = found ?? "系统 PATH 环境变量中未找到 git.exe";
                    if (found != null) description = $"系统 PATH：{found}";
                    break;

                case GitSourceMode.Custom:
                    found = ResolveCustomPath(customPath);
                    description = found != null
                        ? $"指定路径：{found}"
                        : $"指定路径中未找到可用的 git.exe：{(string.IsNullOrWhiteSpace(customPath) ? "（空）" : customPath)}";
                    break;

                default:
                    found = CandidatePaths().FirstOrDefault(File.Exists) ?? FindOnPath();
                    description = found != null
                        ? $"自动检测：{found}"
                        : "自动检测：未找到 git.exe（可将便携版 Git 解压到软件目录的 git\\ 子目录，或在设置中指定路径）";
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
        /// 解析用户自定义路径：支持 git.exe 完整路径、Git 安装/解压根目录，
        /// 目录时自动尝试 cmd\git.exe、bin\git.exe、mingw64\bin\git.exe。支持环境变量展开。
        /// </summary>
        public static string? ResolveCustomPath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            string path = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
            try
            {
                if (File.Exists(path))
                    return Path.GetFullPath(path);

                if (Directory.Exists(path))
                {
                    foreach (string sub in new[]
                             {
                                 Path.Combine("cmd", "git.exe"),
                                 Path.Combine("bin", "git.exe"),
                                 Path.Combine("mingw64", "bin", "git.exe"),
                                 "git.exe"
                             })
                    {
                        string candidate = Path.Combine(path, sub);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch (Exception)
            {
                // 非法路径返回 null
            }

            return null;
        }

        /// <summary>校验 git.exe 是否可执行：运行 git --version 并检查输出。</summary>
        public static bool Validate(string gitExePath, out string version)
        {
            version = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = gitExePath,
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

        /// <summary>自动检测的候选路径（按优先级）：软件目录内嵌便携版 → 常见安装位置。</summary>
        private static IEnumerable<string> CandidatePaths()
            => EmbeddedCandidates().Concat(InstalledCandidates());

        /// <summary>软件目录内嵌的便携版 Git 候选路径（与 exe 同级的 git\ 或 PortableGit\）。</summary>
        private static IEnumerable<string> EmbeddedCandidates()
        {
            string appDir = AppContext.BaseDirectory;
            yield return Path.Combine(appDir, "git", "cmd", "git.exe");
            yield return Path.Combine(appDir, "PortableGit", "cmd", "git.exe");
            yield return Path.Combine(appDir, "git", "bin", "git.exe");
        }

        /// <summary>常见安装位置候选：Program Files / 用户级安装 / scoop / winget。</summary>
        private static IEnumerable<string> InstalledCandidates()
        {
            // Program Files / Program Files (x86) / 用户级安装
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

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
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "git.exe");
        }

        /// <summary>在系统 PATH 环境变量的各目录中查找 git.exe。</summary>
        private static string? FindOnPath()
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathVar.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, "git.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                    // 非法 PATH 条目跳过
                }
            }
            return null;
        }

        /// <summary>
        /// 查找 ssh.exe：优先 git 自带的（Git 根目录 usr\bin），其次系统 PATH。
        /// 仅用于非交互 ssh 参数（GIT_SSH_COMMAND）；找不到时不设置该变量，
        /// 避免覆盖 git 自身的 ssh 查找逻辑导致 ssh 远端推送失败。
        /// </summary>
        public static string? FindSshExe()
        {
            foreach (string dir in GetPathEntries())
            {
                try
                {
                    string candidate = Path.Combine(dir, "ssh.exe");
                    if (File.Exists(candidate)) return candidate;
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
                    string candidate = Path.Combine(dir, "ssh.exe");
                    if (File.Exists(candidate)) return candidate;
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
    /// 远端 URL 凭据处理：按主机查找凭据（账号）、把凭据注入 https URL、日志脱敏。
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
                string rest = s.Substring(schemeEnd + 3);
                int slash = rest.IndexOf('/');
                authority = slash < 0 ? rest : rest.Substring(0, slash);
            }
            else if (s.Contains('@') && s.Contains(':'))
            {
                // scp 形式：git@host:group/repo.git
                int at = s.IndexOf('@', StringComparison.Ordinal);
                int colon = s.IndexOf(':', at + 1);
                authority = colon < 0 ? s.Substring(at + 1) : s.Substring(at + 1, colon - at - 1);
            }
            else
            {
                // 仅填了主机名或完整 URL 之外的简写
                return s.Length == 0 ? null : s.ToLowerInvariant();
            }

            int userInfoEnd = authority.IndexOf('@', StringComparison.Ordinal);
            if (userInfoEnd >= 0) authority = authority.Substring(userInfoEnd + 1);

            int portSep = authority.IndexOf(':', StringComparison.Ordinal);
            if (portSep >= 0) authority = authority.Substring(0, portSep);

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
            string rest = s.Substring(schemeEnd + 3);
            int slash = rest.IndexOf('/');
            string authority = slash < 0 ? rest : rest.Substring(0, slash);
            return !authority.Contains("@", StringComparison.Ordinal);
        }

        /// <summary>把凭据注入远端 URL，返回 https://user:token@host/path（令牌做 URL 编码）。</summary>
        public static string Apply(string url, GitCredential cred)
        {
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return url;

            string scheme = url.Substring(0, schemeEnd);
            string rest = url.Substring(schemeEnd + 3);
            int slash = rest.IndexOf('/');
            string authority = slash < 0 ? rest : rest.Substring(0, slash);
            string tail = slash < 0 ? string.Empty : rest.Substring(slash);

            int userInfoEnd = authority.IndexOf('@', StringComparison.Ordinal);
            string host = userInfoEnd >= 0 ? authority.Substring(userInfoEnd + 1) : authority;

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

            string rest = url.Substring(schemeEnd + 3);
            int slash = rest.IndexOf('/');
            string authority = slash < 0 ? rest : rest.Substring(0, slash);
            string tail = slash < 0 ? string.Empty : rest.Substring(slash);

            int userInfoEnd = authority.IndexOf('@', StringComparison.Ordinal);
            if (userInfoEnd < 0) return url;

            string userInfo = authority.Substring(0, userInfoEnd);
            int colon = userInfo.IndexOf(':', StringComparison.Ordinal);
            string name = colon < 0 ? userInfo : userInfo.Substring(0, colon);

            return $"{url.Substring(0, schemeEnd + 3)}{name}:***@{authority.Substring(userInfoEnd + 1)}{tail}";
        }
    }
}
