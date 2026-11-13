using Avalonia.Controls;

namespace MultiGitCommit;

/// <summary>
/// 主窗口（桌面壳）：Windows / macOS / Linux 上承载 <see cref="MainView"/>。
/// 标题版本号、窗口图标、关闭时中止拉取等 Window 专属逻辑都在这里；
/// 移动端（Android / iOS）不经过本类，由 App 直接创建 MainView 作为单视图根。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>默认主界面宽度；同时也是所有桌面对话框的尺寸上限。</summary>
    public const double DefaultWidth = 1120;

    /// <summary>默认主界面高度；同时也是所有桌面对话框的尺寸上限。</summary>
    public const double DefaultHeight = 800;

    private readonly MainView _view;

    public MainWindow() : this(AppServices.Current.Cli, AppServices.Current.Settings)
    {
    }

    /// <summary>可注入命令执行器（单元测试时传入假实现，无需真实 git 与 UI 运行时）。</summary>
    public MainWindow(IGitCliRunner cli) : this(cli, AppServices.Current.Settings)
    {
    }

    /// <summary>可注入命令执行器与设置来源（完全脱离真实磁盘设置，便于测试）。</summary>
    public MainWindow(IGitCliRunner cli, ISettingsProvider settings)
    {
        InitializeComponent();
        Width = DefaultWidth;
        Height = DefaultHeight;
        Dialogs.ApplyIcon(this);

        _view = new MainView(cli, settings);
        Content = _view;

        // 标题带上大版本号 + 月日（如 v26.11.03），不同构建一眼区分；
        // 完整版本号（含构建时间 -HHmm）仍在【关于】对话框与文件属性里
        RefreshTitle();
        Loc.LanguageChanged += RefreshTitle;

        // 关闭主窗口时中止仍在进行的拉取，避免 git 进程残留
        Closing += (_, _) => _view.Shutdown();
    }

    private void RefreshTitle()
    {
        Title = AppVersion.TitleVersion.Length > 0
            ? Loc.F($"MultiGitCommit v{AppVersion.TitleVersion} - 一键提交推送工具")
            : Loc.T("MultiGitCommit - 一键提交推送工具");
    }
}
