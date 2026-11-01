using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MultiGitCommit;

/// <summary>
/// “关于”窗口：展示软件名称、版本及项目开源地址。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Dialogs.ApplyIcon(this);
    }

    /// <summary>点击项目地址时使用系统默认浏览器打开（macOS 使用 open 命令）。</summary>
    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        string? url = (sender as HyperlinkButton)?.NavigateUri?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
        }
        catch (Exception)
        {
            // 打开失败忽略
        }
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
