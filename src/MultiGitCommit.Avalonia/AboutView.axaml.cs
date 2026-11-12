using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MultiGitCommit;

/// <summary>
/// “关于”视图：展示软件名称、版本及项目开源地址（桌面端为模态窗口内容，移动端为覆盖层卡片）。
/// </summary>
public partial class AboutView : DialogView<bool>
{
    /// <summary>
    /// 移动端覆盖层按视口尺寸布局：卡片内禁用横向滚动，视图宽度固定为屏幕宽度，
    /// 标题 / 说明 / 链接的 TextWrapping 才会真正生效，避免桌面版 500 宽的内容被手机屏幕裁掉。
    /// 桌面端不经过覆盖层，仍按 XAML 的 Width=500 呈现。
    /// </summary>
    public override bool FitsOverlayViewport => true;

    public AboutView()
    {
        InitializeComponent();

        // 版本号来自 csproj 构建时自动生成的 Version，界面无需手改
        TxtVersion.Text = $"v{AppVersion.Current}（Windows / macOS / Linux / Android / iOS）";
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
