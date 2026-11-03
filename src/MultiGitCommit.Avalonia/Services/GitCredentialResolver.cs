namespace MultiGitCommit;

/// <summary>
/// 按【Git 环境】里选择的账号来源，为远端 URL 解析可用凭据（推送 / 拉取共用）。
///
/// Auto：自定义账号优先，缺失时取系统凭据；System：只用系统凭据；
/// Custom：只用自定义列表；None：不提供凭据。
/// 返回 <see cref="DetectedCredential"/> 表示来自系统，普通 <see cref="GitCredential"/> 表示来自自定义列表。
/// </summary>
public sealed class GitCredentialResolver
{
    private readonly ISettingsProvider _settings;

    public GitCredentialResolver(ISettingsProvider settings)
    {
        _settings = settings;
    }

    /// <summary>为指定远端 URL 解析凭据；无法解析时返回 null。</summary>
    public async Task<GitCredential?> ResolveAsync(string url)
    {
        CredentialSourceMode mode = _settings.Current.CredentialSource;
        if (mode == CredentialSourceMode.None) return null;

        if (mode != CredentialSourceMode.System)
        {
            GitCredential? custom = GitCredentials.Find(url, _settings.Current.Credentials);
            if (custom != null) return custom;
        }

        if (mode == CredentialSourceMode.Custom) return null;

        // 系统凭据：凭据管理器 / 钥匙串 / ~/.git-credentials / credential helper（短超时，不会卡住）
        string helperArg = GitCredentialHelper.OverrideArgument() ?? string.Empty;
        return await SystemCredentialProbe.ProbeAsync(
            GitCredentials.GetHost(url), GitLocator.GitExe, default, helperArg).ConfigureAwait(true);
    }
}
