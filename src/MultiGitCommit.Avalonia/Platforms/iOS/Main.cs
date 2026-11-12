using UIKit;

namespace MultiGitCommit;

/// <summary>
/// iOS 程序入口：由 UIApplication 启动 AppDelegate（Platforms/iOS 目录约定）。
/// </summary>
public static class Entry
{
    static void Main(string[] args)
        => UIApplication.Main(args, null, "AppDelegate");
}
