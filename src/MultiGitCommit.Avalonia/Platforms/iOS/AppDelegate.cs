using Avalonia;
using Avalonia.iOS;
using Avalonia.Media;
using Foundation;

namespace MultiGitCommit;

/// <summary>
/// iOS 入口（Platforms/iOS 目录约定）：Avalonia 单项目模式。
/// 移动端没有桌面窗口，App 以 MainView 为单视图根（见 App.axaml.cs）。
/// 注意：iOS 沙盒禁止执行子进程，git 相关功能在本平台不可用，应用启动后会在日志中提示。
/// </summary>
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            // iOS 自带 PingFang SC（XAML 字体链里已含），正常情况下可直接匹配；
            // 这里再显式设为默认字体兜底，防止个别系统上回退失败导致中文方块。
            .With(new FontManagerOptions { DefaultFamilyName = "PingFang SC" })
            .AfterSetup(_ =>
            {
                // 后续需要 iOS 特有能力时在这里挂接。
            });
    }
}
