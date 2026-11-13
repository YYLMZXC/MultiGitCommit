using Avalonia;

namespace MultiGitCommit;

#if !ANDROID && !IOS
/// <summary>
/// 桌面版程序入口（Avalonia 11 跨平台桌面应用，Windows / macOS / Linux 共用）。
/// 移动端（Android / iOS）不使用本入口：Android 由 MainActivity、iOS 由 AppDelegate 启动。
/// 三种模式：首参数不以 - 开头 = CLI（未知命令报用法错误退出 2）；
/// 带 -- 开头参数 = MCP Server（--stdio 等）；不带参数 = 图形界面。
/// </summary>
internal static class Program
{
    [STAThread]
    internal static async Task<int> Main(string[] args)
    {
        if (Cli.CliProgram.IsCliCommand(args))
            return await Cli.CliProgram.RunAsync(args).ConfigureAwait(false);

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
