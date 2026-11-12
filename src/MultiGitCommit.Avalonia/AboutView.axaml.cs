using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MultiGitCommit;

/// <summary>
/// “关于”视图：展示软件名称、版本及项目开源地址（桌面端为模态窗口内容，移动端为覆盖层卡片）。
/// </summary>
public partial class AboutView : DialogView<bool>
{
    public AboutView()
    {
        InitializeComponent();

        // 版本号来自 csproj 构建时自动生成的 Version，界面无需手改
        TxtVersion.Text = $"  v{AppVersion.Current}（Windows / macOS / Linux / Android / iOS）";
    }

    /// <summary>用系统默认浏览器打开项目地址（TopLevel.Launcher 跨平台，移动端也可用）。</summary>
    private async void Link_Click(object? sender, RoutedEventArgs e)
    {
        string? url = (sender as HyperlinkButton)?.NavigateUri?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
                await topLevel.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception)
        {
            // 打开失败忽略
        }
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }
}
