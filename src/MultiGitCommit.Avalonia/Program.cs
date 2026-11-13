using Avalonia;

namespace MultiGitCommit;

#if !ANDROID && !IOS
/// <summary>
/// 桌面版程序入口（Avalonia 11 跨平台桌面应用，Windows / macOS / Linux 共用）。
/// 移动端（Android / iOS）不使用本入口：Android 由 MainActivity、iOS 由 AppDelegate 启动。
/// 命令行带参数时进入 MCP Server 模式（--stdio / --self-test / --list-tools / --help），
/// 不带参数启动图形界面。
/// </summary>
internal static class Program
{
    [STAThread]
    internal static async Task<int> Main(string[] args)
    {
        if (Mcp.McpProgram.IsMcpArgs(args))
            return await Mcp.McpProgram.RunAsync(args).ConfigureAwait(false);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Avalonia 应用构建（macOS / Linux / Windows 通用，平台后端自动探测）。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
#endif
