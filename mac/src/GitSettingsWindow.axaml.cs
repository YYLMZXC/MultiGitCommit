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

    public GitSettingsWindow(AppSettings current)
    {
        _suppress = true;
        InitializeComponent();
        Dialogs.ApplyIcon(this);
        _suppress = false;

        RbAuto.IsChecked = current.GitSource == GitSourceMode.Auto;
        RbEmbedded.IsChecked = current.GitSource == GitSourceMode.Embedded;
        RbSystem.IsChecked = current.GitSource == GitSourceMode.System;
        RbCustom.IsChecked = current.GitSource == GitSourceMode.Custom;
        TxtCustomPath.Text = current.CustomGitPath ?? string.Empty;

        UpdateEnabled();
        Test(); // 打开窗口即显示当前配置的检测结果
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

        Close(true);
    }
}
