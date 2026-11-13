using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiGitCommit;

/// <summary>
/// 应用级全局设置（与具体仓库无关），跨平台持久化：
/// Windows → <c>%APPDATA%\MultiGitCommit\settings.json</c>；
/// macOS   → <c>~/Library/Application Support/MultiGitCommit/settings.json</c>；
/// Linux   → <c>$XDG_CONFIG_HOME/MultiGitCommit/settings.json</c>（未设置时为 <c>~/.config/MultiGitCommit/settings.json</c>）。
/// 各平台字段完全一致；但凭据（账号 / 令牌）落盘为密文且与当前用户 / 本机密钥绑定，
/// 配置文件跨机器、跨用户复制后需要重新填写凭据，其余字段仍可通用。
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

    /// <summary>上次成功加载的仓库根目录；启动模式为「上次使用的仓库优先」或「找不到时回退」时用于自动载入。</summary>
    [JsonPropertyName("LastRepoPath")]
    public string LastRepoPath { get; set; } = string.Empty;

    /// <summary>
    /// 启动时加载哪个仓库：默认只读取当前目录（找不到时不自动打开上次项目），
    /// 也可改为「上次使用的仓库优先」或「当前目录优先，找不到时回退上次项目」。
    /// </summary>
    [JsonPropertyName("StartupRepo")]
    public StartupRepoMode StartupRepo { get; set; } = StartupRepoMode.CurrentDirectory;

    /// <summary>
    /// 执行 git add 时的文件添加方式：默认「全提交」（<c>git add .</c>），
    /// 可改为「可选提交」（提交前勾选要添加的文件）。
    /// </summary>
    [JsonPropertyName("StageMode")]
    public StageMode StageMode { get; set; } = StageMode.AddAll;

    /// <summary>最近项目记录最大保留条数（按最近使用排序，超出后裁掉最旧的）。</summary>
    public const int MaxRecentRepoPaths = 10;

    /// <summary>最近打开的项目（仓库根目录）记录，按最近使用排序；仓库管理首页展示并支持快速跳转。</summary>
    [JsonPropertyName("RecentRepoPaths")]
    public List<string> RecentRepoPaths { get; set; } = new();

    /// <summary>
    /// 仓库自动发现的自定义扫描根目录；为空时使用默认根目录
    /// （用户主目录，Windows 另加各固定磁盘，Android 另加共享存储根目录）。
    /// </summary>
    [JsonPropertyName("RepoScanRoots")]
    public List<string> RepoScanRoots { get; set; } = new();

    /// <summary>启动默认页面：项目页或仓库管理首页；当前目录所在仓库存在时始终优先进项目页。</summary>
    [JsonPropertyName("DefaultStartPage")]
    public StartPageMode DefaultStartPage { get; set; } = StartPageMode.RepoPage;

    /// <summary>界面语言，默认简体中文；切换后立即生效并随设置持久化。</summary>
    [JsonPropertyName("Language")]
    public AppLanguage Language { get; set; } = AppLanguage.Chinese;

    /// <summary>设置文件完整路径（加载时填充）。</summary>
    [JsonIgnore]
    public string SettingsFilePath { get; private set; } = string.Empty;

    /// <summary>软件数据目录（settings.json 与凭据密钥文件所在目录），供设置页直接打开。</summary>
    [JsonIgnore]
    public string DataDirectory => SettingsDir;

    /// <summary>非 Windows 平台的凭据主密钥文件；Windows 用 DPAPI 由系统保管密钥，不生成此文件。</summary>
    private string CredentialKeyPath => Path.Combine(SettingsDir, "credential.key");

    /// <summary>加载时有凭据解密失败（密钥文件丢失 / 换了用户或机器），设置页提示重新填写。</summary>
    [JsonIgnore]
    public bool CredentialsDecryptFailed { get; private set; }

    /// <summary>
    /// 设置目录，按各平台惯例：
    /// Windows 用 %APPDATA%；macOS 用 ~/Library/Application Support；
    /// Linux 遵循 XDG 规范用 $XDG_CONFIG_HOME（未设置时回退 ~/.config）；
    /// Android / iOS 用应用沙盒内的应用数据目录（LocalApplicationData 在移动端映射到沙盒）。
    /// </summary>
    private static string SettingsDir
    {
        get
        {
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiGitCommit");

            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiGitCommit");

            if (OperatingSystem.IsMacOS())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "MultiGitCommit");

            // Linux / 其他 Unix：XDG_CONFIG_HOME 必须为绝对路径，否则按规范忽略并回退 ~/.config
            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg))
                return Path.Combine(xdg, "MultiGitCommit");

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "MultiGitCommit");
        }
    }

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
                    loaded.RecentRepoPaths ??= new List<string>();
                    loaded.RepoScanRoots ??= new List<string>();

                    // 凭据密文还原成内存明文；旧版明文配置原样读取，下次保存时自动升级为密文
                    loaded.CredentialsDecryptFailed = DecryptCredentials(loaded.Credentials, loaded.CredentialKeyPath);

                    // 历史配置或手改文件可能带来非法值，统一回落到默认（当前目录优先 / 全提交）
                    if (!Enum.IsDefined(loaded.StartupRepo))
                        loaded.StartupRepo = StartupRepoMode.CurrentDirectory;

                    if (!Enum.IsDefined(loaded.StageMode))
                        loaded.StageMode = StageMode.AddAll;

                    if (!Enum.IsDefined(loaded.DefaultStartPage))
                        loaded.DefaultStartPage = StartPageMode.RepoPage;

                    if (!Enum.IsDefined(loaded.Language))
                        loaded.Language = AppLanguage.Chinese;

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

    /// <summary>保存设置到平台上对应的设置目录（Windows：%APPDATA%；macOS：~/Library/Application Support；Linux：$XDG_CONFIG_HOME 或 ~/.config）。</summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);

        string json = SerializeEncrypted();
        File.WriteAllText(SettingsFilePath, json, new UTF8Encoding(false));

        // 设置文件仍收紧为仅当前用户可读写（chmod 600），避免同机其他账号读取
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

    /// <summary>
    /// 反序列化后把凭据密文还原成内存明文；无 enc: 前缀的旧版明文配置原样保留。
    /// 返回是否存在解密失败（密钥文件丢失 / 换了用户或机器）。
    /// </summary>
    internal static bool DecryptCredentials(IEnumerable<GitCredential> credentials, string keyFilePath)
    {
        int failuresBefore = CredentialProtector.UnprotectFailureCount;
        foreach (var cred in credentials)
        {
            if (cred is null) continue;
            cred.Username = CredentialProtector.Unprotect(cred.Username ?? string.Empty, keyFilePath);
            cred.Token = CredentialProtector.Unprotect(cred.Token ?? string.Empty, keyFilePath);
        }
        return CredentialProtector.UnprotectFailureCount > failuresBefore;
    }

    /// <summary>
    /// 序列化为 JSON：只在序列化期间把账号 / 令牌替换成密文，返回后立即还原。
    /// 内存中始终是明文，界面编辑与 git 注入 URL 都无需感知加密。
    /// </summary>
    internal string SerializeEncrypted()
    {
        var original = Credentials ?? new List<GitCredential>();
        try
        {
            Credentials = original.Where(c => c != null)
                .Select(c => new GitCredential
                {
                    Host = c.Host ?? string.Empty,
                    Username = CredentialProtector.Protect(c.Username ?? string.Empty, CredentialKeyPath),
                    Token = CredentialProtector.Protect(c.Token ?? string.Empty, CredentialKeyPath)
                })
                .ToList();
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            Credentials = original;
        }
    }
}
