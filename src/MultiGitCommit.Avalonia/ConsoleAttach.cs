using System.Runtime.InteropServices;

namespace MultiGitCommit;

/// <summary>
/// 无控制台可执行程序（WinExe）的终端输出支持：从终端直接运行 CLI / MCP 的人类可读模式
/// （--help / --list-tools / --self-test / CLI 命令）时，Windows 下没有自己的控制台，
/// 附加启动进程的父控制台后输出才可见。
/// stdout / stderr 已被重定向（MCP 客户端、脚本捕获）时保留继承的管道句柄，不做附加。
/// macOS / Linux 的可执行文件天然共享终端，无需处理。
/// </summary>
internal static class ConsoleAttach
{
    private const int AttachParentProcess = unchecked((int)0xFFFFFFFF); // ATTACH_PARENT_PROCESS

    public static void Attach()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Console.IsOutputRedirected || Console.IsErrorRedirected) return;
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;
            // 附加成功后重开标准流，Console.WriteLine 才会写到父控制台
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            // 无父控制台（如双击启动 / 图形化拉起）时忽略
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
