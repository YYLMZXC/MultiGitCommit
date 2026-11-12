using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MultiGitCommit;

/// <summary>
/// 应用程序入口。Avalonia 无 WPF 的 DispatcherUnhandledException，
/// 这里统一兜底捕获未处理异常，避免程序直接崩溃退出。
/// 桌面端（Windows / macOS / Linux）走经典窗口生命周期；
/// 移动端（Android / iOS）走单视图生命周期，直接以 MainView 为根。
/// </summary>
public partial class App : Avalonia.Application
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
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
