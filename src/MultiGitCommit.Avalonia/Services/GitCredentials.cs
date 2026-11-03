using System.Text;
using System.Text.RegularExpressions;

namespace MultiGitCommit;

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
            // 仅填了主机名或完整 URL 之外的简写：清掉复制时可能带上的引号、空格、路径
            string bareHost = SanitizeBareHost(s);
            return bareHost.Length == 0 ? null : bareHost.ToLowerInvariant();
        }

        int userInfoEnd = authority.IndexOf('@', StringComparison.Ordinal);
        if (userInfoEnd >= 0) authority = authority[(userInfoEnd + 1)..];

        int portSep = authority.IndexOf(':', StringComparison.Ordinal);
        if (portSep >= 0) authority = authority[..portSep];

        string bare = SanitizeBareHost(authority);
        return bare.Length == 0 ? null : bare.ToLowerInvariant();
    }

    /// <summary>
    /// 清洗从命令行 / 文档复制的 URL：去掉首尾空白与包裹的引号、尖括号、反引号等。
    /// 只做格式清理，不改动 URL 结构（不会编码或重写凭据部分）。
    /// </summary>
    public static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        string s = url.Trim().Trim('"', '\'', '`', '<', '>', '(', ')', ' ');
        s = s.TrimEnd(';', ',', '。', '；', '，');
        return s.Trim();
    }

    /// <summary>
    /// 把用户输入规范化为纯主机名：支持粘贴完整 URL、带引号的 URL 或仅主机名；
    /// 没有协议时会去掉误粘进来的路径。无法得到合法主机名时返回空字符串。
    /// </summary>
    public static string NormalizeHost(string? input)
    {
        string s = NormalizeUrl(input);
        if (s.Length == 0) return string.Empty;

        if (s.Contains("://") || (s.Contains('@') && s.Contains(':')))
            return GetHost(s) ?? string.Empty;

        int slash = s.IndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) s = s[..slash];

        string bare = SanitizeBareHost(s);
        return bare.Length == 0 ? string.Empty : bare.ToLowerInvariant();
    }

    /// <summary>只保留主机名可能出现的字符（字母数字、点、连字符、冒号），去掉引号 / 空白 / 标点。</summary>
    private static string SanitizeBareHost(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == ':')
                builder.Append(c);
        }

        return builder.ToString().Trim('.', ':', '-');
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
