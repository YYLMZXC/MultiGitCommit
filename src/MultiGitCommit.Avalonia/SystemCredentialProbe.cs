using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MultiGitCommit;

/// <summary>
/// 系统凭据探测结果：比 <see cref="GitCredential"/> 多一个来源说明，便于在导入窗口展示。
/// </summary>
public class DetectedCredential : GitCredential
{
    /// <summary>来源说明（Windows 凭据管理器 / .git-credentials / credential helper）。</summary>
    [JsonPropertyName("Source")]
    public string Source { get; set; } = string.Empty;
}

/// <summary>一个可供检测的候选主机（附带它为什么被列为候选）。</summary>
public class CandidateHost
{
    /// <summary>主机（小写）。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>线索来源：当前项目远端 / 凭据管理器 / 凭据文件 / 常用平台。</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>是否默认勾选检测（有明确线索的默认勾选，常用平台默认不勾选，避免全量扫描卡顿）。</summary>
    public bool Preselected { get; set; }
}

/// <summary>导入窗口的一行数据：一条探测结果 + 是否勾选。</summary>
public class CredentialImportItem
{
    /// <summary>探测到的凭据（含来源）。</summary>
    public DetectedCredential Credential { get; set; } = new();

    /// <summary>是否勾选导入。</summary>
    public bool Selected { get; set; } = true;

    /// <summary>主机。</summary>
    public string Host => Credential.Host;

    /// <summary>用户名（平台账号名）。</summary>
    public string Username => Credential.Username;

    /// <summary>来源说明。</summary>
    public string Source => Credential.Source;
}

/// <summary>
/// 系统 git 凭据探测器（Windows / macOS 通用）：自动检测本机已经保存好的远端账号，免手工填写。
///
/// 两种来源：
/// 1. **系统的**：Windows 凭据管理器（Git Credential Manager / wincred）、macOS 钥匙串（osxkeychain）、
///    <c>~/.git-credentials</c> 明文文件、<c>git credential fill</c> 可命中的任意 credential helper；
/// 2. **自定义的**：用户在【Git 环境】→【推送凭据】里手工填写或导入的账号。
///
/// 防卡死设计（重要）：
/// - 每个主机单次探测 5 秒超时，超时即杀掉整个进程树；
/// - 全程支持 <see cref="CancellationToken"/>，检测窗口可随时【停止】；
/// - 非交互（GIT_TERMINAL_PROMPT=0 / GCM_INTERACTIVE=never），不弹系统授权窗口；
/// - 候选主机默认只勾选「有明确线索」的（当前项目远端、凭据管理器、凭据文件），
///   常用平台列表默认不勾选，由用户按需选择，避免逐个扫描造成长时间等待。
/// </summary>
public static class SystemCredentialProbe
{
    /// <summary>没有额外线索时的补充探测主机（国内外常用平台），默认不勾选。</summary>
    public static readonly IReadOnlyList<string> DefaultHosts = new[]
    {
        "github.com", "gitee.com", "cnb.cool", "gitlab.com",
        "gitcode.com", "gitea.com", "codeup.aliyun.com", "bitbucket.org"
    };

    /// <summary>单个主机探测的超时（凭据管理器响应慢 / 弹窗被禁用时的兜底）。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>凭据文件（credential.helper=store 的明文文件）路径。</summary>
    private static string CredentialsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".git-credentials");

    /// <summary>
    /// 构造候选主机列表，按线索强度排序：当前项目远端 → 系统凭据管理器 → 凭据文件 → 常用平台。
    /// 只有前三类默认勾选（<see cref="CandidateHost.Preselected"/>），常用平台需用户自行勾选。
    /// </summary>
    /// <param name="extraHosts">当前项目的推送远端 URL（可选）。</param>
    public static IReadOnlyList<CandidateHost> CandidateHosts(IEnumerable<string>? extraHosts = null)
    {
        var list = new List<CandidateHost>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw, string origin, bool preselected)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string? host = GitCredentials.GetHost(raw) ?? raw!.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(host) || !seen.Add(host)) return;
            list.Add(new CandidateHost { Host = host, Origin = origin, Preselected = preselected });
        }

        if (extraHosts != null)
        {
            foreach (string h in extraHosts) Add(h, "当前项目远端", true);
        }

        // 系统凭据库：Windows 用 cmdkey 枚举凭据管理器目标；
        // macOS 不枚举钥匙串（security 命令逐个查询很慢，违背「不全量扫描」原则），
        // 改由 git credential fill 经 osxkeychain 按需索取。
        foreach (string h in WindowsCredentialManagerHosts()) Add(h, "Windows 凭据管理器", true);

        foreach (var c in ReadCredentialsFile()) Add(c.Host, "凭据文件 .git-credentials", true);
        foreach (string h in DefaultHosts) Add(h, "常用平台（默认不检测）", false);

        return list;
    }

    /// <summary>
    /// 读取 ~/.git-credentials（credential.helper=store 生成的明文文件），能直接拿到 用户名:令牌。
    /// 文件不存在 / 格式异常时返回空列表。
    /// </summary>
    public static IReadOnlyList<DetectedCredential> ReadCredentialsFile()
    {
        var result = new List<DetectedCredential>();
        try
        {
            string file = CredentialsFilePath;
            if (!File.Exists(file)) return result;

            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                var cred = ParseCredentialsLine(line);
                if (cred != null) result.Add(cred);
            }
        }
        catch (Exception)
        {
            // 文件不可读时忽略，回退到其他探测方式
        }

        return result;
    }

    /// <summary>解析一行 <c>https://user:token@host</c>。</summary>
    private static DetectedCredential? ParseCredentialsLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        string s = line.Trim();
        if (s.StartsWith('#')) return null;

        int schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return null;

        string rest = s[(schemeEnd + 3)..];
        int slash = rest.IndexOf('/');
        string authority = slash < 0 ? rest : rest[..slash];

        int at = authority.IndexOf('@', StringComparison.Ordinal);
        if (at < 0) return null;

        string userInfo = authority[..at];
        string host = authority[(at + 1)..];
        int colon = userInfo.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) return null;

        string user = Uri.UnescapeDataString(userInfo[..colon]);
        string token = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        if (host.Length == 0 || user.Length == 0 || token.Length == 0) return null;

        return new DetectedCredential
        {
            Host = GitCredentials.GetHost(host) ?? host.ToLowerInvariant(),
            Username = user,
            Token = token,
            Source = "凭据文件 ~/.git-credentials"
        };
    }

    /// <summary>
    /// 枚举 Windows 凭据管理器中与 git 相关的目标主机（<c>cmdkey /list</c>）。
    /// 只能拿到主机名，拿不到密码——用于缩小探测范围，真正的账号由 git credential fill 取得。
    /// 非 Windows 平台直接返回空。
    /// </summary>
    private static IEnumerable<string> WindowsCredentialManagerHosts()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        GitCommandResult listing = GitCliRunner.Default.Run(new GitCommandRequest
        {
            FileName = "cmdkey",
            Arguments = "/list",
            Timeout = TimeSpan.FromSeconds(3),
            TreatAsGit = false
        });

        if (!listing.Success) yield break;

        string output = listing.Stdout;

        // cmdkey 输出形如： 目标: LegacyGeneric:target=git:https://github.com
        // 中英文系统均包含 "target="，因此按该关键字匹配即可
        foreach (Match m in Regex.Matches(output, @"target=(?<t>[^\s]+)", RegexOptions.IgnoreCase))
        {
            string? host = GitCredentials.GetHost(m.Groups["t"].Value);
            if (!string.IsNullOrEmpty(host)) yield return host;
        }
    }

    /// <summary>
    /// 探测指定主机的系统凭据：先查凭据文件（快且无需唤起系统凭据库），
    /// 再用 <c>git credential fill</c> 向系统 credential helper
    /// （Windows：GCM / wincred；macOS：osxkeychain）索取。
    /// </summary>
    /// <param name="host">远端主机或完整 URL。</param>
    /// <param name="gitExe">git 可执行文件（用当前生效的，支持便携版 / 内嵌 / 指定路径）。</param>
    /// <param name="ct">取消令牌（检测窗口【停止】时使用）。</param>
    /// <param name="helperArg">
    /// 覆盖 credential.helper 的命令行前缀（形如 "-c credential.helper=osxkeychain "）；
    /// 当前 git 配置的助手不可用时由调用方传入，避免探测因坏配置直接失败。
    /// </param>
    /// <returns>探测到的凭据；未找到 / 超时 / 被取消返回 null。</returns>
    public static async Task<DetectedCredential?> ProbeAsync(string? host, string gitExe,
        CancellationToken ct = default, string? helperArg = null)
    {
        string? h = GitCredentials.GetHost(host);
        if (string.IsNullOrEmpty(h)) return null;
        ct.ThrowIfCancellationRequested();

        // 1) 明文凭据文件：无需唤起系统凭据库，最快最稳
        var fromFile = ReadCredentialsFile().FirstOrDefault(c =>
            string.Equals(GitCredentials.GetHost(c.Host) ?? c.Host, h, StringComparison.OrdinalIgnoreCase));
        if (fromFile != null) return fromFile;

        // 2) 系统 credential helper：https 优先，其次 http
        var https = await FillAsync("https", h, gitExe, ct, helperArg).ConfigureAwait(false);
        if (https != null) return https;

        ct.ThrowIfCancellationRequested();
        return await FillAsync("http", h, gitExe, ct, helperArg).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行 <c>git credential fill</c> 并取得 username / password。
    /// 非交互（GIT_TERMINAL_PROMPT=0、GCM_INTERACTIVE=never），超时 / 取消即终止进程树，绝不挂起。
    /// </summary>
    private static async Task<DetectedCredential?> FillAsync(string protocol, string host, string gitExe,
        CancellationToken ct, string? helperArg = null)
    {
        try
        {
            // git credential 协议：属性行 + 空行结束，行分隔必须用 \n。
            // 同时给出 protocol / host / url 三种字段，兼容不同版本 git 与各类 credential helper 的解析要求。
            GitCommandResult result = await GitCliRunner.Default.RunAsync(new GitCommandRequest
            {
                FileName = gitExe,
                // helperArg 用于绕过不可用的 credential.helper 配置（如缺少 git-credential-manager）
                Arguments = (helperArg ?? string.Empty) + "credential fill",
                StandardInput = $"protocol={protocol}\nhost={host}\nurl={protocol}://{host}/\n\n",
                Timeout = ProbeTimeout,
                Environment = new Dictionary<string, string>
                {
                    ["GCM_INTERACTIVE"] = "never",
                    ["GIT_CREDENTIAL_MANAGER_INTERACTIVE"] = "never"
                }
            }, ct).ConfigureAwait(false);

            if (result.TimedOut || result.Cancelled) return null;

            string? username = null;
            string? password = null;
            foreach (string raw in result.Stdout.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("username=", StringComparison.OrdinalIgnoreCase))
                    username = line["username=".Length..];
                else if (line.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
                    password = line["password=".Length..];
            }

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return null;

            return new DetectedCredential
            {
                Host = host,
                Username = username,
                Token = password,
                Source = "系统凭据（git credential helper）"
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
