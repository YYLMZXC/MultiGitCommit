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

    /// <summary>点击项目地址时使用系统默认浏览器打开（Windows / macOS 通用）。</summary>
    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        string? url = (sender as HyperlinkButton)?.NavigateUri?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            // UseShellExecute：Windows 走 ShellExecute，macOS 等价于 open，两平台都能交给默认浏览器
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
