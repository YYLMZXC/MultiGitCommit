using Avalonia;

namespace MultiGitCommit;

/// <summary>
/// 桌面版程序入口（Avalonia 11 跨平台桌面应用，Windows / macOS / Linux 共用）。
/// </summary>
internal static class Program
{
    [STAThread]
    internal static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Avalonia 应用构建（macOS / Linux / Windows 通用，平台后端自动探测）。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
