using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        Custom = 2
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
    /// 三种来源：
    /// 1. Auto   自动检测：优先软件目录下的便携版 Git（git\ 或 PortableGit\），
    ///           其次常见安装位置（Program Files、scoop、winget 等），最后系统 PATH；
    /// 2. System 仅使用系统 PATH 环境变量中的 git（环境 git）；
    /// 3. Custom 用户指定的 git.exe 或 Git 根目录（软件自带 / 便携版 git）。
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

        /// <summary>自动检测的候选路径（按优先级排序）：软件目录便携版 → 常见安装位置。</summary>
        private static IEnumerable<string> CandidatePaths()
        {
            // 1) 软件目录自带的便携版 Git（与 exe 同级的 git\ 或 PortableGit\）
            string appDir = AppContext.BaseDirectory;
            yield return Path.Combine(appDir, "git", "cmd", "git.exe");
            yield return Path.Combine(appDir, "PortableGit", "cmd", "git.exe");
            yield return Path.Combine(appDir, "git", "bin", "git.exe");

            // 2) 常见安装位置：Program Files / Program Files (x86) / 用户级安装
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

            foreach (string root in new[] { programFiles, programFilesX86, localPrograms })
            {
                if (!string.IsNullOrEmpty(root))
                    yield return Path.Combine(root, "Git", "cmd", "git.exe");
            }

            // 3) scoop / winget 包管理器安装位置
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
    }
}
