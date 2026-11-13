namespace MultiGitCommit;

/// <summary>一次远端 URL 编辑的结果；<see cref="Message"/> 为需要展示给用户的原因或提示。</summary>
public sealed record RemoteUrlChange(bool Changed, string? Url, string? PreviousUrl, string Message);

/// <summary>
/// 远端 URL 的唯一规则源：清洗粘贴内容、拒绝脱敏占位符回写、去重、数量上限，
/// 以及 GUI 添加 / 编辑远端与 MCP set_config 共用的配置变更操作。
///
/// 抽取原因：这套规则原先在 MCP 处理器里有一份（含内嵌凭据拒绝），
/// 在 MainView 的添加 / 编辑 / 克隆三处又各写了一遍（且规则不一致）。
/// 现在两边都调用本服务：MCP 面向 AI 客户端，额外启用 rejectEmbeddedCredentials；
/// GUI 为人工输入，沿用原有宽松策略。策略差异是显式参数，不再是两份实现。
/// </summary>
public sealed class RemoteUrlPolicy
{
    /// <summary>单个项目允许配置的远端数量上限。</summary>
    public const int MaxRemoteCount = 20;

    /// <summary>清洗并校验单条 URL；失败返回 null 并给出原因。</summary>
    /// <param name="rejectEmbeddedCredentials">是否禁止 https URL 内嵌用户名 / 令牌（MCP 写入配置时为 true）。</param>
    public string? Normalize(string? raw, bool rejectEmbeddedCredentials, out string error)
    {
        error = string.Empty;
        string url = GitCredentials.NormalizeUrl(raw);
        if (url.Length == 0)
        {
            error = Loc.T("远端 URL 不能为空。");
            return null;
        }

        if (url.Contains(":***@", StringComparison.Ordinal))
        {
            error = Loc.T("远端 URL 含脱敏占位符 ***，请提供真实地址（不要用 get_config 返回的脱敏值回写）");
            return null;
        }

        if (rejectEmbeddedCredentials && HasEmbeddedCredentials(url))
        {
            error = Loc.T("远端 URL 不要内嵌用户名 / 令牌；凭据请在软件的【Git 环境】中配置");
            return null;
        }

        return url;
    }

    /// <summary>批量清洗：过滤空值、去重并按上限截断；任一条非法则整批失败。</summary>
    public IReadOnlyList<string> NormalizeAll(IEnumerable<string> rawUrls, bool rejectEmbeddedCredentials, out string error)
    {
        var result = new List<string>();
        error = string.Empty;

        foreach (string raw in rawUrls)
        {
            string? url = Normalize(raw, rejectEmbeddedCredentials, out error);
            if (url == null) return Array.Empty<string>();
            if (!result.Contains(url, StringComparer.Ordinal)) result.Add(url);
            if (result.Count >= MaxRemoteCount) break;
        }

        return result;
    }

    /// <summary>向项目配置追加一条远端（GUI 添加远端 / 克隆后写入共用）。</summary>
    public RemoteUrlChange Add(ProjectConfig config, string? raw)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? url = Normalize(raw, rejectEmbeddedCredentials: false, out string message);
        if (url == null) return new RemoteUrlChange(false, null, null, message);

        if (config.RemoteUrls.Contains(url, StringComparer.Ordinal))
            return new RemoteUrlChange(false, url, null, Loc.F($"远端 URL 已存在：{url}"));

        if (config.RemoteUrls.Count >= MaxRemoteCount)
            return new RemoteUrlChange(false, url, null,
                Loc.F($"远端数量超过上限（最多 {MaxRemoteCount} 个）"));

        config.RemoteUrls.Add(url);
        return new RemoteUrlChange(true, url, null, string.Empty);
    }

    /// <summary>替换项目配置中指定位置的远端（GUI 编辑远端）。</summary>
    public RemoteUrlChange Replace(ProjectConfig config, int index, string? raw)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (index < 0 || index >= config.RemoteUrls.Count)
            return new RemoteUrlChange(false, null, null, Loc.T("请先选中要编辑的远端。"));

        string? url = Normalize(raw, rejectEmbeddedCredentials: false, out string message);
        if (url == null) return new RemoteUrlChange(false, null, null, message);

        string previous = config.RemoteUrls[index];
        if (string.Equals(previous, url, StringComparison.Ordinal))
            return new RemoteUrlChange(false, url, previous, string.Empty);

        config.RemoteUrls[index] = url;
        return new RemoteUrlChange(true, url, previous, string.Empty);
    }

    /// <summary>禁止把带 user:token@ 的 https URL 写进配置或回显给 AI（令牌应放在软件设置里）。</summary>
    private static bool HasEmbeddedCredentials(string url)
    {
        bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return isHttp && !GitCredentials.NeedsCredential(url);
    }
}
