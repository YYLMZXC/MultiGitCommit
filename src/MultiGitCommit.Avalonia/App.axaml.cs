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

        // 必须在创建任何视图之前：XAML 的 DynamicResource 在控件加载时就会解析资源值
        Loc.Initialize(AppServices.Current.Settings.Current.Language);
        Loc.LanguageChanged += RefreshResources;
        RefreshResources();
    }

    /// <summary>
    /// 把当前语言的全部 XAML 文案写进应用级资源。原地赋值（而不是替换整个字典对象）
    /// 才能触发 DynamicResource 的资源变更通知，实现不重启切换语言。
    /// </summary>
    private void RefreshResources()
    {
        foreach (var entry in Loc.GetResourceEntries())
            Resources[entry.Key] = entry.Value;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            System.Console.Error.WriteLine(Loc.T("未处理异常：") + (args.ExceptionObject as System.Exception)?.Message);
        };

        // 唯一的应用装配入口：桌面 / 移动两条启动路径都从组合根取同一套默认依赖
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(AppServices.Current.Cli, AppServices.Current.Settings);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView(AppServices.Current.Cli, AppServices.Current.Settings);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
