using System.Text.Json.Serialization;

namespace MultiGitCommit;

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
