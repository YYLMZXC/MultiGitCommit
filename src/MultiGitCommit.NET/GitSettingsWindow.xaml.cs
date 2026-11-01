using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MultiGitCommit
{
    /// <summary>
    /// Git 环境设置窗口：选择 git.exe 来源（自动检测 / 系统 PATH / 指定路径），
    /// 让工具不依赖系统安装的 git，可使用便携版或软件自带的 Git。
    /// </summary>
    public partial class GitSettingsWindow : Window
    {
        /// <summary>用户点击确定后选定的来源模式。</summary>
        public GitSourceMode SelectedSource { get; private set; } = GitSourceMode.Auto;

        /// <summary>用户点击确定后选定的自定义路径（仅 Custom 模式有意义）。</summary>
        public string SelectedCustomPath { get; private set; } = string.Empty;

        /// <summary>用户点击确定后选定的凭据（账号）来源模式。</summary>
        public CredentialSourceMode SelectedCredentialSource { get; private set; } = CredentialSourceMode.Auto;

        /// <summary>传入的全局设置对象；点确定时把凭据（账号）写回它。</summary>
        private readonly AppSettings _settings;

        /// <summary>当前编辑中的凭据副本（点确定才写回设置，取消不影响）。</summary>
        private readonly List<GitCredential> _credentials;

        /// <summary>InitializeComponent / 初始化过程中抑制事件（避免抢焦点、避免重复检测）。</summary>
        private bool _suppress;

        /// <summary>当前项目的远端 URL，用于自动检测时优先探测这些主机、验证账号时优先探测真实远端。</summary>
        private readonly IReadOnlyList<string>? _remoteUrls;

        /// <summary>账号验证的取消令牌（【取消验证】按钮 / 关闭窗口时使用）。</summary>
        private CancellationTokenSource? _credTestCts;

        /// <summary>当前仓库根目录（设置提交身份时写入仓库级 config 用）。</summary>
        private readonly string? _repoPath;

        /// <param name="current">全局设置（凭据写回它）。</param>
        /// <param name="remoteUrls">当前项目的推送远端 URL（可选，用于缩小自动检测范围）。</param>
        /// <param name="repoPath">当前仓库根目录（可选，用于读取/写入仓库级提交身份）。</param>
        public GitSettingsWindow(AppSettings current, IEnumerable<string>? remoteUrls = null, string? repoPath = null)
        {
            _remoteUrls = remoteUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            _repoPath = string.IsNullOrWhiteSpace(repoPath) ? null : repoPath;
            _settings = current;
            _credentials = (current.Credentials ?? new List<GitCredential>())
                .Where(c => c != null)
                .Select(c => new GitCredential
                {
                    Host = c.Host ?? string.Empty,
                    Username = c.Username ?? string.Empty,
                    Token = c.Token ?? string.Empty
                })
                .ToList();

            _suppress = true;
            InitializeComponent();

            RbAuto.IsChecked = current.GitSource == GitSourceMode.Auto;
            RbEmbedded.IsChecked = current.GitSource == GitSourceMode.Embedded;
            RbSystem.IsChecked = current.GitSource == GitSourceMode.System;
            RbCustom.IsChecked = current.GitSource == GitSourceMode.Custom;
            TxtCustomPath.Text = current.CustomGitPath ?? string.Empty;

            // 凭据（账号）来源：与 git 来源一样可由用户选择（两组靠 XAML 里的 GroupName 区分，互不干扰）。
            // 初始化期间抑制事件：否则 CredMode_Changed 会覆盖掉下面 Test() 的 git 检测结果。
            RbCredAuto.IsChecked = current.CredentialSource == CredentialSourceMode.Auto;
            RbCredSystem.IsChecked = current.CredentialSource == CredentialSourceMode.System;
            RbCredCustom.IsChecked = current.CredentialSource == CredentialSourceMode.Custom;
            RbCredNone.IsChecked = current.CredentialSource == CredentialSourceMode.None;

            // 兜底：配置值异常（或历史 bug 导致的脏数据）时保证两组都各有一项被选中
            if (RbAuto.IsChecked != true && RbEmbedded.IsChecked != true &&
                RbSystem.IsChecked != true && RbCustom.IsChecked != true)
                RbAuto.IsChecked = true;

            if (RbCredAuto.IsChecked != true && RbCredSystem.IsChecked != true &&
                RbCredCustom.IsChecked != true && RbCredNone.IsChecked != true)
                RbCredAuto.IsChecked = true;

            _suppress = false;

            // 关闭窗口时中止未完成的账号验证，避免后台 git 进程继续空跑
            Closing += (_, _) => _credTestCts?.Cancel();

            RefreshCredList();
            RefreshIdentityInfo();
            RefreshHelperInfo();
            UpdateEnabled();
            Test(); // 打开窗口即显示当前配置的检测结果
        }

        // ==================== 推送凭据（账号） ====================

        /// <summary>刷新凭据列表显示（ListBox 直接显示 GitCredential.ToString()，不含令牌）。</summary>
        private void RefreshCredList()
        {
            ListCred.ItemsSource = null;
            ListCred.ItemsSource = _credentials;
        }

        /// <summary>选中某条凭据时填入输入框，便于修改。</summary>
        private void ListCred_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ListCred.SelectedItem is not GitCredential cred) return;

            TxtCredHost.Text = cred.Host;
            TxtCredUser.Text = cred.Username;
            TxtCredToken.Password = cred.Token;
        }

        /// <summary>
        /// 添加或更新一条凭据：先用一次 git ls-remote 验证账号是否可用（有超时、可取消），
        /// 验证不通过时让用户确认是否仍然保存，避免保存一个推送必然失败的账号。
        /// </summary>
        private async void BtnCredAdd_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtCredHost.Text.Trim();
            string user = TxtCredUser.Text.Trim();
            string token = TxtCredToken.Password.Trim();

            if (host.Length == 0)
            {
                ShowResult("× 请先填写远端主机（如 cnb.cool）。", false);
                return;
            }

            if (user.Length == 0 || token.Length == 0)
            {
                ShowResult("× 请同时填写用户名与访问令牌。", false);
                return;
            }

            var result = await VerifyCredentialAsync(host, user, token);
            if (result == null) return; // 无可用 git.exe：不写入

            if (!result.Success)
            {
                string reason = string.IsNullOrWhiteSpace(result.Detail)
                    ? result.Message
                    : result.Message + "\n\n服务端返回：" + result.Detail;

                var choice = MessageBox.Show(this,
                    reason + "\n\n该账号未验证通过，仍要保存吗？（未验证的账号推送时很可能失败）",
                    "账号验证", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                {
                    ShowResult("已放弃保存，该账号未写入列表。", null);
                    return;
                }
            }

            SaveCredential(host, user, token);
            ShowResult((result.Success ? "√ 验证通过：" : "! 未验证通过但已保存：") +
                       $" {host} 的推送凭据（点【确定】后生效）。", result.Success);
        }

        /// <summary>
        /// 【验证账号】：用一次 git ls-remote 实测输入框中的账号能否通过服务端认证。
        /// 验证进行中再点一次该按钮即中止（按钮文字会变为【取消验证】）。
        /// </summary>
        private async void BtnCredTest_Click(object sender, RoutedEventArgs e)
        {
            if (_credTestCts != null)
            {
                _credTestCts.Cancel(); // 正在验证：再点一次就是中止
                return;
            }

            string host = TxtCredHost.Text.Trim();
            string user = TxtCredUser.Text.Trim();
            string token = TxtCredToken.Password.Trim();

            if (host.Length == 0)
            {
                ShowResult("× 请先填写远端主机（如 cnb.cool）。", false);
                return;
            }

            if (user.Length == 0 || token.Length == 0)
            {
                ShowResult("× 请同时填写用户名与访问令牌。", false);
                return;
            }

            await VerifyCredentialAsync(host, user, token);
        }

        /// <summary>
        /// 实测账号是否可用：全程异步、默认 20 秒超时、可随时取消，绝不会卡住界面。
        /// 返回 null 表示未执行（没有可用的 git.exe）。
        /// </summary>
        private async Task<CredentialTestResult?> VerifyCredentialAsync(string host, string user, string token)
        {
            // 按窗口里当前选择的 git 来源实时解析，未点【确定】也能用刚选的 git 验证
            string? exe = GitLocator.ResolvePreview(CurrentMode, TxtCustomPath.Text.Trim(), out _);
            if (exe == null)
            {
                ShowResult("× 当前 git 来源下未找到可用的 git.exe，请先配置 git 来源再验证账号。", false);
                return null;
            }

            _credTestCts?.Dispose();
            _credTestCts = new CancellationTokenSource();
            var ct = _credTestCts.Token;

            BtnCredTest.Content = "取消验证";
            BtnCredAdd.IsEnabled = false;
            ShowResult($"正在验证 {user}@{host}（git ls-remote 实测，最多 {CredentialTester.DefaultTimeoutSeconds} 秒，可点【取消验证】中止）…", null);

            try
            {
                var result = await CredentialTester.TestAsync(
                    exe,
                    new GitCredential { Host = host, Username = user, Token = token },
                    _remoteUrls,
                    ct);

                ShowResult((result.Success ? "√ " : "× ") + result.Message, result.Success);
                return result;
            }
            catch (Exception ex)
            {
                ShowResult("× 验证账号失败：" + ex.Message, false);
                return null;
            }
            finally
            {
                BtnCredTest.Content = "验证账号";
                BtnCredAdd.IsEnabled = true;
                _credTestCts?.Dispose();
                _credTestCts = null;
            }
        }

        /// <summary>写入（或更新同主机的）一条凭据并刷新列表。</summary>
        private void SaveCredential(string host, string user, string token)
        {
            string hostKey = GitCredentials.GetHost(host) ?? host.ToLowerInvariant();
            var existing = _credentials.FirstOrDefault(c =>
                string.Equals(GitCredentials.GetHost(c.Host) ?? c.Host.Trim().ToLowerInvariant(),
                    hostKey, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Host = host;
                existing.Username = user;
                existing.Token = token;
            }
            else
            {
                _credentials.Add(new GitCredential { Host = host, Username = user, Token = token });
            }

            RefreshCredList();
        }

        /// <summary>删除选中的凭据。</summary>
        private void BtnCredDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ListCred.SelectedItem is not GitCredential cred)
            {
                ShowResult("× 请先在上方列表中选中要删除的凭据。", false);
                return;
            }

            _credentials.Remove(cred);
            RefreshCredList();
            TxtCredHost.Text = string.Empty;
            TxtCredUser.Text = string.Empty;
            TxtCredToken.Password = string.Empty;
            ShowResult($"已删除 {cred.Host} 的凭据（点【确定】后生效）。", null);
        }

        /// <summary>
        /// 打开【检测系统凭据】窗口：由用户自行选择要检测的主机后再执行检测（可随时停止、有进度），
        /// 勾选结果导入后与自定义填写的凭据一样保存在凭据列表中。
        /// </summary>
        private void BtnCredDetect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (GitLocator.GitExePath == null)
                {
                    ShowResult("× 未找到可用的 git.exe，请先配置 git 来源再检测系统凭据。", false);
                    return;
                }

                // 打开检测窗口：由用户自行勾选要检测的主机后再执行，随时可停止，不会全量扫描导致卡住
                var win = new CredentialDetectWindow(_remoteUrls) { Owner = this };
                if (win.ShowDialog() != true)
                {
                    ShowResult("已取消，未导入任何系统账号。", null);
                    return;
                }

                var found = win.SelectedCredentials;
                if (found.Count == 0)
                {
                    ShowResult("未勾选任何账号，未导入。", null);
                    return;
                }

                int imported = 0;
                foreach (var cred in found)
                {
                    string hostKey = GitCredentials.GetHost(cred.Host) ?? cred.Host.Trim().ToLowerInvariant();
                    var existing = _credentials.FirstOrDefault(c =>
                        string.Equals(GitCredentials.GetHost(c.Host) ?? c.Host.Trim().ToLowerInvariant(),
                            hostKey, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        // 同一主机只保留一条，导入的系统凭据覆盖原有
                        existing.Host = cred.Host;
                        existing.Username = cred.Username;
                        existing.Token = cred.Token;
                    }
                    else
                    {
                        _credentials.Add(new GitCredential
                        {
                            Host = cred.Host,
                            Username = cred.Username,
                            Token = cred.Token
                        });
                    }

                    imported++;
                }

                RefreshCredList();
                ShowResult($"√ 已从系统导入 {imported} 个账号（点【确定】后生效）。", true);
            }
            catch (Exception ex)
            {
                ShowResult("× 检测系统凭据失败：" + ex.Message, false);
            }
            finally
            {
                BtnCredDetect.IsEnabled = true;
            }
        }

        /// <summary>把操作结果写入 TxtResult（true 成功 / false 失败 / null 中性提示）。</summary>
        private void ShowResult(string message, bool? ok)
        {
            TxtResult.Text = message;
            TxtResult.Foreground = ok == true ? Brushes.SeaGreen
                : ok == false ? Brushes.Firebrick
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        }

        /// <summary>当前选中的凭据（账号）来源模式。</summary>
        private CredentialSourceMode CurrentCredentialSource => RbCredAuto.IsChecked == true
            ? CredentialSourceMode.Auto
            : RbCredSystem.IsChecked == true
                ? CredentialSourceMode.System
                : RbCredCustom.IsChecked == true
                    ? CredentialSourceMode.Custom
                    : CredentialSourceMode.None;

        /// <summary>切换凭据来源时给出对应说明（不执行任何检测，避免卡顿）。</summary>
        private void CredMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;

            switch (CurrentCredentialSource)
            {
                case CredentialSourceMode.System:
                    ShowResult("将只使用系统凭据：每次推送时实时索取，令牌不写入配置文件。", null);
                    break;
                case CredentialSourceMode.Custom:
                    ShowResult("将只使用下方自定义账号：不会访问 Windows 凭据管理器。", null);
                    break;
                case CredentialSourceMode.None:
                    ShowResult("将不使用任何账号：适用于 ssh 远端或 URL 中自带凭据的情况。", null);
                    break;
                default:
                    ShowResult("自动检测：优先使用已保存的账号，找不到时自动从系统凭据获取。", null);
                    break;
            }
        }

        private GitSourceMode CurrentMode => RbAuto.IsChecked == true
            ? GitSourceMode.Auto
            : RbEmbedded.IsChecked == true
                ? GitSourceMode.Embedded
                : RbSystem.IsChecked == true
                    ? GitSourceMode.System
                    : GitSourceMode.Custom;

        /// <summary>切换模式时同步自定义路径输入区可用状态，并立即重新检测。</summary>
        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            UpdateEnabled();
            Test();
        }

        private void UpdateEnabled()
        {
            bool custom = RbCustom.IsChecked == true;
            TxtCustomPath.IsEnabled = custom;
            BtnBrowse.IsEnabled = custom;
            if (custom && !_suppress) TxtCustomPath.Focus();
        }

        /// <summary>浏览选择 git.exe 文件。</summary>
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 git.exe（便携版 Git 解压后位于其 cmd\\ 或 bin\\ 目录）",
                Filter = "git.exe|git.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                TxtCustomPath.Text = dlg.FileName;
                Test();
            }
        }

        /// <summary>测试当前所选配置能否找到并执行 git。</summary>
        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            Test();
        }

        /// <summary>按当前窗口选择实时解析并显示结果。</summary>
        // ==================== 凭据助手（credential.helper） ====================

        /// <summary>
        /// 显示当前 git 的凭据助手状态：配置值是否可用，不可用时会自动改用哪个助手。
        /// 这是"推送时取不到系统账号"最常见的根因（精简便携版 git 缺少 git-credential-manager.exe）。
        /// </summary>
        private void RefreshHelperInfo()
        {
            GitCredentialHelper.Invalidate();
            string text = GitCredentialHelper.Describe();

            TxtHelperInfo.Text = text;
            TxtHelperInfo.Foreground = GitCredentialHelper.ConfiguredUsable()
                ? Brushes.SeaGreen
                : Brushes.Firebrick;
        }

        // ==================== 提交身份（user.name / user.email） ====================

        /// <summary>显示当前生效的提交身份（读取 git config --get，取仓库级 → 全局的生效值）。</summary>
        private void RefreshIdentityInfo()
        {
            var saved = _settings.CommitIdentity;
            string savedText = saved != null && saved.IsComplete ? $"已保存：{saved}" : "尚未保存";

            var (name, email) = ReadIdentityFromGit();
            TxtIdentityInfo.Text = (name.Length > 0 && email.Length > 0)
                ? $"当前生效：{name} <{email}>（{savedText}）"
                : $"当前生效：未设置（{savedText}）";

            TxtIdentityInfo.Foreground = (name.Length > 0 && email.Length > 0)
                ? Brushes.SeaGreen
                : Brushes.Firebrick;
        }

        /// <summary>用 git.exe 读取当前生效的 user.name / user.email（失败返回空串）。</summary>
        private (string Name, string Email) ReadIdentityFromGit()
        {
            try
            {
                string exe = GitLocator.GitExe;
                if (string.IsNullOrEmpty(exe)) return (string.Empty, string.Empty);

                string dir = _repoPath ?? Environment.CurrentDirectory;
                string name = RunGitConfig(dir, exe, "user.name");
                string email = RunGitConfig(dir, exe, "user.email");
                return (name, email);
            }
            catch (Exception)
            {
                return (string.Empty, string.Empty);
            }
        }

        /// <summary>执行 git config --get &lt;key&gt;，返回取值（缺失/失败返回空串）。</summary>
        private static string RunGitConfig(string dir, string exe, string key)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "config --get " + key,
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return string.Empty;
                p.StandardInput.Close();

                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                    return string.Empty;
                }

                return p.ExitCode == 0 ? output.Trim() : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>打开【提交身份】窗口并写入 git config（仓库级或全局）。</summary>
        private void BtnIdentity_Click(object sender, RoutedEventArgs e)
        {
            var (name, email) = ReadIdentityFromGit();
            var current = new GitIdentity
            {
                Name = name,
                Email = email,
                Scope = _settings.CommitIdentity?.Scope ?? IdentityScope.Local
            };

            var win = new IdentityWindow(current) { Owner = this };
            if (win.ShowDialog() != true) return;

            var identity = win.Result;
            string scopeArg = identity.Scope == IdentityScope.Global ? "--global " : string.Empty;
            string dir = _repoPath ?? Environment.CurrentDirectory;

            string exe = GitLocator.GitExe;
            bool ok = SetGitConfig(dir, exe, $"{scopeArg}user.name", identity.Name)
                      && SetGitConfig(dir, exe, $"{scopeArg}user.email", identity.Email);

            if (!ok)
            {
                ShowResult("× 写入提交身份失败，请确认 git.exe 可用且该仓库目录可写。", false);
                return;
            }

            _settings.CommitIdentity = identity;
            RefreshIdentityInfo();
            ShowResult($"√ 已写入提交身份（{(identity.Scope == IdentityScope.Global ? "全局 --global" : "本仓库 --local")}）：" +
                       $"{identity.Name} <{identity.Email}>", true);
        }

        /// <summary>执行 git config &lt;key&gt; "&lt;value&gt;" 写入配置。</summary>
        private static bool SetGitConfig(string dir, string exe, string keyWithScope, string? value)
        {
            string escaped = (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"config {keyWithScope} \"{escaped}\"",
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return false;
                p.StandardInput.Close();

                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                    return false;
                }

                return p.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Test()
        {
            var mode = CurrentMode;
            string custom = TxtCustomPath.Text.Trim();

            if (mode == GitSourceMode.Custom && string.IsNullOrWhiteSpace(custom))
            {
                ShowResult("× 请先填写 git.exe 路径或 Git 根目录。", false);
                return;
            }

            string? exe = GitLocator.ResolvePreview(mode, custom, out string description);
            ShowResult((exe != null ? "√ " : "× ") + description, exe != null);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedSource = CurrentMode;
            SelectedCustomPath = TxtCustomPath.Text.Trim();

            if (SelectedSource == GitSourceMode.Custom)
            {
                if (string.IsNullOrWhiteSpace(SelectedCustomPath))
                {
                    MessageBox.Show(this, "请填写 git.exe 路径或 Git 根目录。", "Git 环境设置",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 路径无效时给出确认，避免静默保存一个用不了的配置
                if (GitLocator.ResolveCustomPath(SelectedCustomPath) == null)
                {
                    var choice = MessageBox.Show(this,
                        "指定路径中未找到可用的 git.exe，仍保存该设置吗？",
                        "Git 环境设置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (choice != MessageBoxResult.Yes) return;
                }
            }

            // 凭据（账号）与来源模式写回设置对象（调用方在点确定后统一保存；点取消不会落盘）
            _settings.Credentials = _credentials;
            _settings.CredentialSource = SelectedCredentialSource = CurrentCredentialSource;

            DialogResult = true;
        }
    }
}
