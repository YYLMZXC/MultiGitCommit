using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MultiGitCommit
{
    /// <summary>
    /// 系统凭据探测结果：比 <see cref="GitCredential"/> 多一个来源说明，便于在导入窗口展示。
    /// </summary>
    public class DetectedCredential : GitCredential
    {
        /// <summary>来源说明（Windows 凭据管理器 / .git-credentials / credential helper）。</summary>
        [JsonPropertyName("Source")]
        public string Source { get; set; } = string.Empty;
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
    /// 系统 git 凭据探测器：自动检测本机已经保存好的远端账号，免手工填写。
    ///
    /// 两种来源：
    /// 1. **系统的**：Windows 凭据管理器（Git Credential Manager / wincred）、
    ///    <c>~/.git-credentials</c> 明文文件、<c>git credential fill</c> 可命中的任意 credential helper；
    /// 2. **自定义的**：用户在【Git 环境】→【推送凭据】里手工填写的账号（见 <see cref="GitCredential"/>）。
    ///
    /// 说明：探测全程非交互（GIT_TERMINAL_PROMPT=0 / GCM_INTERACTIVE=never），
    /// 不会弹出凭据管理器窗口，也不会因等待输入而卡死；单次探测带超时保护。
    /// </summary>
    public static class SystemCredentialProbe
    {
        /// <summary>没有额外线索时的默认探测主机（国内外常用平台）。</summary>
        public static readonly IReadOnlyList<string> DefaultHosts = new[]
        {
            "github.com", "gitee.com", "cnb.cool", "gitlab.com",
            "gitcode.com", "gitea.com", "codeup.aliyun.com", "bitbucket.org"
        };

        /// <summary>单个主机探测的超时（凭据管理器响应慢时兜底）。</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

        /// <summary>凭据文件（credential.helper=store 的明文文件）路径。</summary>
        private static string CredentialsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".git-credentials");

        /// <summary>
        /// 构造候选主机列表：用户已有远端 → Windows 凭据管理器 → 凭据文件 → 默认平台。
        /// 去重并保持上述优先级（越靠前越可能是用户真正要用的）。
        /// </summary>
        public static IReadOnlyList<string> CandidateHosts(IEnumerable<string>? extraHosts = null)
        {
            var hosts = new List<string>();

            void Add(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                string? host = GitCredentials.GetHost(raw) ?? raw!.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(host) && !hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                    hosts.Add(host);
            }

            if (extraHosts != null)
            {
                foreach (string h in extraHosts) Add(h);
            }

            foreach (string h in WindowsCredentialManagerHosts()) Add(h);
            foreach (var c in ReadCredentialsFile()) Add(c.Host);
            foreach (string h in DefaultHosts) Add(h);

            return hosts;
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
            if (s.StartsWith("#", StringComparison.Ordinal)) return null;

            int schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return null;

            string rest = s.Substring(schemeEnd + 3);
            int slash = rest.IndexOf('/');
            string authority = slash < 0 ? rest : rest.Substring(0, slash);

            int at = authority.IndexOf('@', StringComparison.Ordinal);
            if (at < 0) return null;

            string userInfo = authority.Substring(0, at);
            string host = authority.Substring(at + 1);
            int colon = userInfo.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) return null;

            string user = Uri.UnescapeDataString(userInfo.Substring(0, colon));
            string token = Uri.UnescapeDataString(userInfo.Substring(colon + 1));
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
        /// </summary>
        private static IEnumerable<string> WindowsCredentialManagerHosts()
        {
            if (!OperatingSystem.IsWindows()) yield break;

            string output;
            try
            {
                var psi = new ProcessStartInfo("cmdkey", "/list")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) yield break;

                output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { /* 忽略 */ }
                    yield break;
                }
            }
            catch (Exception)
            {
                yield break;
            }

            // cmdkey 输出形如： 目标: LegacyGeneric:target=git:https://github.com
            // 中英文系统均包含 "target="，因此按该关键字匹配即可
            foreach (Match m in Regex.Matches(output, @"target=(?<t>[^\s]+)", RegexOptions.IgnoreCase))
            {
                string? host = GitCredentials.GetHost(m.Groups["t"].Value);
                if (!string.IsNullOrEmpty(host)) yield return host;
            }
        }

        /// <summary>
        /// 探测指定主机的系统凭据：先查凭据文件（快且无需唤起凭据管理器），
        /// 再用 <c>git credential fill</c> 向系统 credential helper（Git Credential Manager / wincred / store …）索取。
        /// </summary>
        /// <param name="host">远端主机或完整 URL。</param>
        /// <param name="gitExe">git.exe（用当前生效的，支持便携版 / 内嵌 / 指定路径）。</param>
        /// <returns>探测到的凭据；未找到返回 null。</returns>
        public static async Task<DetectedCredential?> ProbeAsync(string? host, string gitExe)
        {
            string? h = GitCredentials.GetHost(host);
            if (string.IsNullOrEmpty(h)) return null;

            // 1) 明文凭据文件：无需唤起凭据管理器，最快最稳
            var fromFile = ReadCredentialsFile().FirstOrDefault(c =>
                string.Equals(GitCredentials.GetHost(c.Host) ?? c.Host, h, StringComparison.OrdinalIgnoreCase));
            if (fromFile != null) return fromFile;

            // 2) 系统 credential helper：https 优先，其次 http
            var https = await FillAsync("https", h, gitExe).ConfigureAwait(false);
            if (https != null) return https;

            return await FillAsync("http", h, gitExe).ConfigureAwait(false);
        }

        /// <summary>
        /// 执行 <c>git credential fill</c> 并取得 username / password。
        /// 非交互（GIT_TERMINAL_PROMPT=0、GCM_INTERACTIVE=never），超时即终止，绝不弹窗或挂起。
        /// </summary>
        private static async Task<DetectedCredential?> FillAsync(string protocol, string host, string gitExe)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = gitExe,
                    Arguments = "credential fill",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                // 非交互：禁止终端提示与凭据管理器弹窗，取不到就直接返回空
                psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
                psi.EnvironmentVariables["GIT_CREDENTIAL_MANAGER_INTERACTIVE"] = "never";

                // 便携版 / 内嵌 git 所在目录注入 PATH，保证 helper 可被找到
                var pathEntries = GitLocator.GetPathEntries();
                if (pathEntries.Count > 0)
                {
                    string oldPath = psi.EnvironmentVariables["PATH"] ?? string.Empty;
                    psi.EnvironmentVariables["PATH"] =
                        string.Join(Path.PathSeparator.ToString(), pathEntries) + Path.PathSeparator + oldPath;
                }

                using var p = Process.Start(psi);
                if (p == null) return null;

                // git credential 协议：属性行 + 空行结束，行分隔必须用 \n。
                // 同时给出 protocol / host / url 三种字段，兼容不同版本 git 与各类 credential helper 的解析要求。
                await p.StandardInput.WriteAsync(
                        $"protocol={protocol}\nhost={host}\nurl={protocol}://{host}/\n\n")
                    .ConfigureAwait(false);
                p.StandardInput.Close();

                var readTask = p.StandardOutput.ReadToEndAsync();
                var finished = await Task.WhenAny(readTask, Task.Delay(ProbeTimeout)).ConfigureAwait(false);
                if (finished != readTask)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                    return null;
                }

                string output = await readTask.ConfigureAwait(false);
                if (!p.WaitForExit(2000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                }

                string? username = null;
                string? password = null;
                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("username=", StringComparison.OrdinalIgnoreCase))
                        username = line.Substring("username=".Length);
                    else if (line.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
                        password = line.Substring("password=".Length);
                }

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return null;

                return new DetectedCredential
                {
                    Host = host,
                    Username = username,
                    Token = password,
                    Source = "系统凭据管理器（git credential helper）"
                };
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
