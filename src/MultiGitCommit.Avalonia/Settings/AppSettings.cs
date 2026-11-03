using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiGitCommit;

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

    /// <summary>保存设置到 %APPDATA%\MultiGitCommit\settings.json（macOS 为 ~/Library/Application Support 下同名文件）。</summary>
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
