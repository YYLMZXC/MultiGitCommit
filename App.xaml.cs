using System.Windows;

namespace MultiGitCommit
{
    /// <summary>
    /// 应用程序入口。全局捕获未处理异常，防止程序直接崩溃退出。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 全局捕获 UI 线程未处理异常，输出提示并阻止崩溃
            DispatcherUnhandledException += (sender, args) =>
            {
                MessageBox.Show(
                    "程序发生未处理异常：" + Environment.NewLine + args.Exception.Message,
                    "MultiGitCommit 异常",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                MessageBox.Show(
                    "程序发生严重异常：" + Environment.NewLine +
                    (args.ExceptionObject as Exception)?.Message,
                    "MultiGitCommit 异常",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            };
        }
    }
}
