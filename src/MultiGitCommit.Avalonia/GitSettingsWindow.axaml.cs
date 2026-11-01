using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MultiGitCommit;

/// <summary>
/// Git 环境设置窗口：选择 git 来源（自动检测 / 系统 PATH / 指定路径 / 软件内嵌），
/// 让工具不依赖系统安装的 git，可使用软件自带或便携版 Git。
/// </summary>
public partial class GitSettingsWindow : Window
{
    /// <summary>用户点击确定后选定的来源模式。</summary>
    public GitSourceMode SelectedSource { get; private set; } = GitSourceMode.Auto;

    /// <summary>用户点击确定后选定的自定义路径（仅 Custom 模式有意义）。</summary>
    public string SelectedCustomPath { get; private set; } = string.Empty;

    /// <summary>用户点击确定后选定的凭据（账号）来源模式。</summary>
    public CredentialSourceMode SelectedCredentialSource { get; private set; } = CredentialSourceMode.Auto;

    /// <summary>传入的全局设置对象；点确定时把凭据写回它。</summary>
    private readonly AppSettings _settings;

    /// <summary>当前仓库根目录（设置提交身份时写入仓库级 config 用）。</summary>
    private readonly string? _repoPath;

    /// <summary>当前编辑中的凭据副本（点确定才写回设置，取消不影响）。</summary>
    private readonly List<GitCredential> _credentials;

    /// <summary>当前项目的远端 URL（验证账号时优先用同主机的真实远端做探测）。</summary>
    private readonly IReadOnlyList<string>? _remoteUrls;

    /// <summary>账号验证的取消令牌（【取消验证】按钮 / 关闭窗口时使用）。</summary>
    private CancellationTokenSource? _credTestCts;

    /// <param name="current">全局设置。</param>
    /// <param name="remoteUrls">当前项目的推送远端 URL（可选，验证账号时用同主机远端做真实探测）。</param>
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
        Dialogs.ApplyIcon(this);
        _suppress = false;

        RbAuto.IsChecked = current.GitSource == GitSourceMode.Auto;
        RbEmbedded.IsChecked = current.GitSource == GitSourceMode.Embedded;
        RbSystem.IsChecked = current.GitSource == GitSourceMode.System;
        RbCustom.IsChecked = current.GitSource == GitSourceMode.Custom;
        TxtCustomPath.Text = current.CustomGitPath ?? string.Empty;

        // 关闭窗口时中止未完成的账号验证，避免后台 git 进程继续空跑
        Closing += (_, _) => _credTestCts?.Cancel();

        // 凭据（账号）来源：与 git 来源一样可由用户选择
        RbCredAuto.IsChecked = current.CredentialSource == CredentialSourceMode.Auto;
        RbCredSystem.IsChecked = current.CredentialSource == CredentialSourceMode.System;
        RbCredCustom.IsChecked = current.CredentialSource == CredentialSourceMode.Custom;
        RbCredNone.IsChecked = current.CredentialSource == CredentialSourceMode.None;

        // 兜底：配置值异常（或历史脏数据）时保证两组都各有一项被选中
        if (RbAuto.IsChecked != true && RbEmbedded.IsChecked != true &&
            RbSystem.IsChecked != true && RbCustom.IsChecked != true)
            RbAuto.IsChecked = true;

        if (RbCredAuto.IsChecked != true && RbCredSystem.IsChecked != true &&
            RbCredCustom.IsChecked != true && RbCredNone.IsChecked != true)
            RbCredAuto.IsChecked = true;

        RefreshCredList();
        RefreshIdentityInfo();
        RefreshHelperInfo();
        UpdateEnabled();
        Test(); // 打开窗口即显示当前配置的检测结果
    }

    // ==================== 推送凭据 ====================

    /// <summary>刷新凭据列表显示（ListBox 直接显示 GitCredential.ToString()）。</summary>
    private void RefreshCredList()
    {
        ListCred.ItemsSource = null;
        ListCred.ItemsSource = _credentials;
    }

    /// <summary>选中某条凭据时填入输入框，便于修改。</summary>
    private void ListCred_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ListCred.SelectedItem is not GitCredential cred) return;

        TxtCredHost.Text = cred.Host;
        TxtCredUser.Text = cred.Username;
        TxtCredToken.Text = cred.Token;
    }

    /// <summary>
    /// 添加或更新一条凭据：先用一次 git ls-remote 验证账号是否可用（有超时、可取消），
    /// 验证不通过时让用户确认是否仍然保存，避免保存一个推送必然失败的账号。
    /// </summary>
    private async void BtnCredAdd_Click(object? sender, RoutedEventArgs e)
    {
        string host = TxtCredHost.Text?.Trim() ?? string.Empty;
        string user = TxtCredUser.Text?.Trim() ?? string.Empty;
        string token = TxtCredToken.Text?.Trim() ?? string.Empty;

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
        if (result == null) return; // 无可用 git / 用户取消：不写入

        if (!result.Success)
        {
            string reason = string.IsNullOrWhiteSpace(result.Detail)
                ? result.Message
                : result.Message + "\n\n服务端返回：" + result.Detail;

            bool keep = await Dialogs.ConfirmAsync(this, "账号验证",
                reason + "\n\n该账号未验证通过，仍要保存吗？（未验证的账号推送时很可能失败）");
            if (!keep)
            {
                ShowResult("已放弃保存，该账号未写入列表。", null);
                return;
            }
        }

        SaveCredential(host, user, token);
        ShowResult((result.Success ? "√ 验证通过：" : "! 未验证通过但已保存：") +
                   $" {host} 的推送凭据（点【确定】后生效）。", result.Success);
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

    /// <summary>
    /// 【验证账号】：用一次 git ls-remote 实测输入框中的账号能否通过服务端认证。
    /// 验证进行中再点一次该按钮即中止（按钮文字会变为【取消验证】）。
    /// </summary>
    private async void BtnCredTest_Click(object? sender, RoutedEventArgs e)
    {
        if (_credTestCts != null)
        {
            _credTestCts.Cancel(); // 正在验证：再点一次就是中止
            return;
        }

        string host = TxtCredHost.Text?.Trim() ?? string.Empty;
        string user = TxtCredUser.Text?.Trim() ?? string.Empty;
        string token = TxtCredToken.Text?.Trim() ?? string.Empty;

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
    /// 返回 null 表示未执行（没有可用的 git）。
    /// </summary>
    private async Task<CredentialTestResult?> VerifyCredentialAsync(string host, string user, string token)
    {
        // 按窗口里当前选择的 git 来源实时解析，未点【确定】也能用刚选的 git 验证
        string? exe = GitLocator.ResolvePreview(CurrentMode, TxtCustomPath.Text?.Trim() ?? string.Empty, out _);
        if (exe == null)
        {
            ShowResult("× 当前 git 来源下未找到可用的 git，请先配置 git 来源再验证账号。", false);
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

    /// <summary>把操作结果写入 TxtResult（true 成功 / false 失败 / null 中性提示）。</summary>
    private void ShowResult(string message, bool? ok)
    {
        TxtResult.Text = message;
        TxtResult.Foreground = ok == true
            ? Brush.Parse("#1E8E3E")
            : ok == false
                ? Brush.Parse("#B00020")
                : Brush.Parse("#666666");
    }

    /// <summary>删除选中的凭据。</summary>
    private void BtnCredDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (ListCred.SelectedItem is not GitCredential cred)
        {
            TxtResult.Text = "× 请先在上方列表中选中要删除的凭据。";
            TxtResult.Foreground = Brush.Parse("#B00020");
            return;
        }

        _credentials.Remove(cred);
        RefreshCredList();
        TxtCredHost.Text = string.Empty;
        TxtCredUser.Text = string.Empty;
        TxtCredToken.Text = string.Empty;
        TxtResult.Text = $"已删除 {cred.Host} 的凭据（点【确定】后生效）。";
        TxtResult.Foreground = Brush.Parse("#666");
    }

    /// <summary>InitializeComponent / 初始化过程中抑制事件。</summary>
    private readonly bool _suppress;

    private GitSourceMode CurrentMode => RbAuto.IsChecked == true
        ? GitSourceMode.Auto
        : RbEmbedded.IsChecked == true
            ? GitSourceMode.Embedded
            : RbSystem.IsChecked == true
                ? GitSourceMode.System
                : GitSourceMode.Custom;

    /// <summary>切换模式时同步自定义路径输入区可用状态。</summary>
    private void Mode_Changed(object? sender, RoutedEventArgs e)
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

    /// <summary>浏览选择 git 可执行文件。</summary>
    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 git 可执行文件（Homebrew 一般在 /opt/homebrew/bin/git）",
            AllowMultiple = false
        });

        if (files.Count == 0) return;

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        TxtCustomPath.Text = path;
        Test();
    }

    /// <summary>测试当前所选配置能否找到并执行 git。</summary>
    private void BtnTest_Click(object? sender, RoutedEventArgs e)
    {
        Test();
    }

    /// <summary>按当前窗口选择实时解析并显示结果。</summary>
    private void Test()
    {
        var mode = CurrentMode;
        string custom = TxtCustomPath.Text?.Trim() ?? string.Empty;

        if (mode == GitSourceMode.Custom && string.IsNullOrWhiteSpace(custom))
        {
            TxtResult.Text = "× 请先填写 git 路径或 Git 根目录。";
            TxtResult.Foreground = Brush.Parse("#B00020");
            return;
        }

        string? exe = GitLocator.ResolvePreview(mode, custom, out string description);
        TxtResult.Text = (exe != null ? "√ " : "× ") + description;
        TxtResult.Foreground = exe != null
            ? Brush.Parse("#1E8E3E")
            : Brush.Parse("#B00020");
    }

    // ==================== 凭据（账号）来源 ====================

    /// <summary>当前选中的凭据（账号）来源模式。</summary>
    private CredentialSourceMode CurrentCredentialSource => RbCredAuto.IsChecked == true
        ? CredentialSourceMode.Auto
        : RbCredSystem.IsChecked == true
            ? CredentialSourceMode.System
            : RbCredCustom.IsChecked == true
                ? CredentialSourceMode.Custom
                : CredentialSourceMode.None;

    /// <summary>切换凭据来源时给出对应说明（不执行任何检测，避免卡顿）。</summary>
    private void CredMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;

        switch (CurrentCredentialSource)
        {
            case CredentialSourceMode.System:
                ShowResult("将只使用系统凭据：每次推送时实时索取，令牌不写入配置文件。", null);
                break;
            case CredentialSourceMode.Custom:
                ShowResult("将只使用下方自定义账号：不会访问系统凭据库。", null);
                break;
            case CredentialSourceMode.None:
                ShowResult("将不使用任何账号：适用于 ssh 远端或 URL 中自带凭据的情况。", null);
                break;
            default:
                ShowResult("自动检测：优先使用已保存的账号，找不到时自动从系统凭据获取。", null);
                break;
        }
    }

    /// <summary>
    /// 打开【检测系统凭据】窗口：由用户自行选择要检测的主机后再执行检测（可随时停止、有进度），
    /// 勾选结果导入后与自定义填写的凭据一样保存在凭据列表中。
    /// </summary>
    private async void BtnCredDetect_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (GitLocator.ResolvePreview(CurrentMode, TxtCustomPath.Text?.Trim() ?? string.Empty, out _) == null)
            {
                ShowResult("× 当前 git 来源未找到可用的 git，请先配置 git 来源再检测系统凭据。", false);
                return;
            }

            var win = new CredentialDetectWindow(_remoteUrls);
            if (await win.ShowDialog<bool?>(this) != true)
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
    }

    // ==================== 凭据助手（credential.helper） ====================

    /// <summary>
    /// 显示当前 git 的凭据助手状态：配置值是否可用，不可用时会自动改用哪个助手。
    /// 这是"推送时取不到系统账号"最常见的根因（精简便携版 git 缺少 git-credential-manager）。
    /// </summary>
    private void RefreshHelperInfo()
    {
        GitCredentialHelper.Invalidate();
        TxtHelperInfo.Text = GitCredentialHelper.Describe();
        TxtHelperInfo.Foreground = GitCredentialHelper.ConfiguredUsable()
            ? Brush.Parse("#1E8E3E")
            : Brush.Parse("#B00020");
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
            ? Brush.Parse("#1E8E3E")
            : Brush.Parse("#B00020");
    }

    /// <summary>用 git 读取当前生效的 user.name / user.email（失败返回空串）。</summary>
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
    private async void BtnIdentity_Click(object? sender, RoutedEventArgs e)
    {
        var (name, email) = ReadIdentityFromGit();
        var current = new GitIdentity
        {
            Name = name,
            Email = email,
            Scope = _settings.CommitIdentity?.Scope ?? IdentityScope.Local
        };

        var win = new IdentityWindow(current);
        if (await win.ShowDialog<bool?>(this) != true) return;

        var identity = win.Result;
        string scopeArg = identity.Scope == IdentityScope.Global ? "--global " : string.Empty;
        string dir = _repoPath ?? Environment.CurrentDirectory;

        string exe = GitLocator.GitExe;
        bool ok = SetGitConfig(dir, exe, $"{scopeArg}user.name", identity.Name)
                  && SetGitConfig(dir, exe, $"{scopeArg}user.email", identity.Email);

        if (!ok)
        {
            ShowResult("× 写入提交身份失败，请确认 git 可用且该仓库目录可写。", false);
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

    private async void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        SelectedSource = CurrentMode;
        SelectedCustomPath = TxtCustomPath.Text?.Trim() ?? string.Empty;

        if (SelectedSource == GitSourceMode.Custom)
        {
            if (string.IsNullOrWhiteSpace(SelectedCustomPath))
            {
                await Dialogs.ShowMessageAsync(this, "Git 环境设置", "请填写 git 路径或 Git 根目录。");
                return;
            }

            // 路径无效时给出确认，避免静默保存一个用不了的配置
            if (GitLocator.ResolveCustomPath(SelectedCustomPath) == null)
            {
                bool keep = await Dialogs.ConfirmAsync(this, "Git 环境设置",
                    "指定路径中未找到可用的 git，仍保存该设置吗？");
                if (!keep) return;
            }
        }

        // 凭据（账号）与来源模式写回设置对象（调用方在点确定后统一保存；点取消不会落盘）
        _settings.Credentials = _credentials;
        _settings.CredentialSource = SelectedCredentialSource = CurrentCredentialSource;

        Close(true);
    }
}
