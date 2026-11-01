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

    /// <summary>传入的全局设置对象；点确定时把凭据写回它。</summary>
    private readonly AppSettings _settings;

    /// <summary>当前编辑中的凭据副本（点确定才写回设置，取消不影响）。</summary>
    private readonly List<GitCredential> _credentials;

    /// <summary>当前项目的远端 URL（验证账号时优先用同主机的真实远端做探测）。</summary>
    private readonly IReadOnlyList<string>? _remoteUrls;

    /// <summary>账号验证的取消令牌（【取消验证】按钮 / 关闭窗口时使用）。</summary>
    private CancellationTokenSource? _credTestCts;

    /// <param name="current">全局设置。</param>
    /// <param name="remoteUrls">当前项目的推送远端 URL（可选，验证账号时用同主机远端做真实探测）。</param>
    public GitSettingsWindow(AppSettings current, IEnumerable<string>? remoteUrls = null)
    {
        _remoteUrls = remoteUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
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

        RefreshCredList();
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

        // 凭据写回设置对象（调用方在点确定后统一保存；点取消不会落盘）
        _settings.Credentials = _credentials;

        Close(true);
    }
}
