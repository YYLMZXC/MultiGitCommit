using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MultiGitCommit;

/// <summary>
/// 应用程序入口。Avalonia 无 WPF 的 DispatcherUnhandledException，
/// 这里统一兜底捕获未处理异常，避免程序直接崩溃退出。
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            System.Console.Error.WriteLine("未处理异常：" + (args.ExceptionObject as System.Exception)?.Message);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
