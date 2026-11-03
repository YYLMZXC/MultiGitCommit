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

        // 版本号来自 csproj 构建时自动生成的 Version，界面无需手改
        TxtVersion.Text = $"  v{AppVersion.Current}（Windows / macOS / Linux）";
    }

    /// <summary>点击项目地址时使用系统默认浏览器打开（Windows / macOS / Linux 通用）。</summary>
    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        string? url = (sender as HyperlinkButton)?.NavigateUri?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            // UseShellExecute：Windows 走 ShellExecute，macOS 等价于 open，Linux 走 xdg-open，都交给默认浏览器
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
