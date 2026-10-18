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

    public GitSettingsWindow(AppSettings current)
    {
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

    /// <summary>添加或更新一条凭据（同一主机只保留一条）。</summary>
    private void BtnCredAdd_Click(object? sender, RoutedEventArgs e)
    {
        string host = TxtCredHost.Text?.Trim() ?? string.Empty;
        string user = TxtCredUser.Text?.Trim() ?? string.Empty;
        string token = TxtCredToken.Text?.Trim() ?? string.Empty;

        if (host.Length == 0)
        {
            TxtResult.Text = "× 请先填写远端主机（如 cnb.cool）。";
            TxtResult.Foreground = Brush.Parse("#B00020");
            return;
        }

        if (user.Length == 0 || token.Length == 0)
        {
            TxtResult.Text = "× 请同时填写用户名与访问令牌。";
            TxtResult.Foreground = Brush.Parse("#B00020");
            return;
        }

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
        TxtResult.Text = $"√ 已保存 {host} 的推送凭据（点【确定】后生效）。";
        TxtResult.Foreground = Brush.Parse("#1E8E3E");
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
