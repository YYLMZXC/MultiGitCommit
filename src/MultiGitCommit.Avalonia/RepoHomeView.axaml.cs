using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
// 安卓 TFM 的隐式全局 using 会引入 Android.Widget.Button，与 Avalonia 的 Button 冲突，固定别名消歧
using AvaloniaButton = Avalonia.Controls.Button;

namespace MultiGitCommit;

/// <summary>
/// 仓库管理首页：最近打开的项目记录（保存在 AppSettings.RecentRepoPaths）+ 本地 Git 仓库自动发现
/// （后台线程扫描，算法与 MCP find_repos 共用 RepoScanner），支持唤起系统文件管理器选目录打开。
/// 以覆盖层形式嵌在 MainView 内，由宿主通过 <see cref="Attach"/> 注入回调。
/// </summary>
public partial class RepoHomeView : UserControl
{
    /// <summary>自动发现扫描深度（层数）。</summary>
    private const int ScanMaxDepth = 3;

    /// <summary>自动发现结果数量上限（避免超大磁盘扫描无限耗时）。</summary>
    private const int ScanLimit = 300;

    /// <summary>宿主项目页（打开仓库 / 选目录回调）。</summary>
    private MainView? _host;

    /// <summary>应用设置来源（最近项目记录与默认启动页）。</summary>
    private ISettingsProvider _settings = SettingsProvider.Instance;

    /// <summary>最近打开的项目（绑定到 ListRecent）。</summary>
    private readonly ObservableCollection<string> _recent = new();

    /// <summary>自动发现的本地仓库（绑定到 ListDiscovered）。</summary>
    private readonly ObservableCollection<string> _discovered = new();

    /// <summary>当前扫描的取消令牌源（刷新 / 视图销毁时中止旧扫描）。</summary>
    private CancellationTokenSource? _scanCts;

    /// <summary>本次视图生命周期内是否已启动过自动发现（首次显示时启动一次，之后手动刷新）。</summary>
    private bool _scanStarted;

    /// <summary>回填默认启动页单选框时抑制事件保存。</summary>
    private bool _loadingStartPage;

    public RepoHomeView()
    {
        InitializeComponent();
        ListRecent.ItemsSource = _recent;
        ListDiscovered.ItemsSource = _discovered;

        // 视图销毁时中止仍在进行的扫描，避免后台线程空转
        DetachedFromVisualTree += (_, _) => _scanCts?.Cancel();
    }

    /// <summary>
    /// 宿主注入：本视图由 MainView 的 XAML 静态创建（构造时尚未完成宿主字段初始化），
    /// 因此在 MainView 构造流程中回填依赖并做首次刷新。
    /// </summary>
    internal void Attach(MainView host, ISettingsProvider settings)
    {
        _host = host;
        _settings = settings;
        RefreshRecent();
        RefreshStartPageRadios();
    }

    /// <summary>每次显示首页时调用：刷新最近项目列表，并按需启动一次仓库自动发现。</summary>
    public void OnShown()
    {
        RefreshRecent();
        if (!_scanStarted)
        {
            _scanStarted = true;
            _ = ScanAsync();
        }
    }

    // ==================== 最近打开的项目 ====================

    private void RefreshRecent()
    {
        _recent.Clear();
        var recents = _settings.Current.RecentRepoPaths;
        if (recents != null)
        {
            foreach (string path in recents)
            {
                if (!string.IsNullOrWhiteSpace(path)) _recent.Add(path);
            }
        }

        TxtRecentEmpty.IsVisible = _recent.Count == 0;
        BtnClearRecent.IsEnabled = _recent.Count > 0;
    }

    private void RemoveRecent_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as AvaloniaButton)?.DataContext is not string path) return;
        _settings.Current.RecentRepoPaths.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
        SaveSettingsQuiet();
        RefreshRecent();
    }

    private void BtnClearRecent_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Current.RecentRepoPaths.Clear();
        SaveSettingsQuiet();
        RefreshRecent();
    }

    // ==================== 本地仓库自动发现 ====================

    private void BtnRescan_Click(object? sender, RoutedEventArgs e) => _ = ScanAsync();

    /// <summary>后台线程扫描本地 Git 仓库（扫描期间 UI 保持响应，可随时刷新 / 离开页面取消旧扫描）。</summary>
    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        CancellationToken ct = _scanCts.Token;

        List<string> roots = ResolveScanRoots();
        _discovered.Clear();
        TxtDiscoveredEmpty.IsVisible = false;
        TxtScanStatus.Text = $"正在扫描本地 Git 仓库（深度 {ScanMaxDepth}，上限 {ScanLimit}）…";

        try
        {
            IReadOnlyList<string> found =
                await Task.Run(() => RepoScanner.Scan(roots, ScanMaxDepth, ScanLimit, ct), ct);

            foreach (string repo in found) _discovered.Add(repo);
            TxtScanStatus.Text = found.Count >= ScanLimit
                ? $"已发现 {found.Count} 个仓库（达到上限，可能未扫全）"
                : $"共发现 {found.Count} 个本地仓库";
            TxtDiscoveredEmpty.IsVisible = found.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // 扫描被新扫描 / 页面销毁取代，静默退出
        }
        catch (Exception ex)
        {
            TxtScanStatus.Text = "扫描失败：" + ex.Message;
            TxtDiscoveredEmpty.IsVisible = true;
        }
    }

    /// <summary>扫描根目录：设置里配置的自定义根目录优先；未配置时用默认（用户主目录 + 固定磁盘 + Android 共享存储）。</summary>
    private List<string> ResolveScanRoots()
    {
        var roots = new List<string>();

        var custom = _settings.Current.RepoScanRoots;
        if (custom != null)
        {
            foreach (string root in custom)
            {
                string trimmed = root.Trim();
                if (trimmed.Length > 0 && Directory.Exists(trimmed)) roots.Add(trimmed);
            }

            if (roots.Count > 0) return roots;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home)) roots.Add(home);

        if (OperatingSystem.IsWindows())
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive is { DriveType: DriveType.Fixed, IsReady: true }) roots.Add(drive.Name);
            }
        }

        if (OperatingSystem.IsAndroid())
        {
            const string sharedStorage = "/storage/emulated/0";
            if (Directory.Exists(sharedStorage)) roots.Add(sharedStorage);
        }

        return roots;
    }

    // ==================== 打开仓库 / 选择文件夹 ====================

    /// <summary>列表「打开」按钮（最近项目与自动发现共用）：跳转到对应仓库的项目页。</summary>
    private void OpenRepo_Click(object? sender, RoutedEventArgs e)
    {
        if (_host == null || (sender as AvaloniaButton)?.DataContext is not string path) return;
        _host.OpenRepoFromHome(path);
    }

    /// <summary>唤起系统文件管理器选择仓库目录并载入（复用项目页的选目录管线，含安卓 SAF 与手输兜底）。</summary>
    private void BtnPickFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _ = _host.BrowseForRepoFromHomeAsync();
    }

    // ==================== 启动默认页面设置 ====================

    private void RefreshStartPageRadios()
    {
        _loadingStartPage = true;
        bool home = _settings.Current.DefaultStartPage == StartPageMode.RepoHome;
        RadioStartRepoPage.IsChecked = !home;
        RadioStartHome.IsChecked = home;
        _loadingStartPage = false;
    }

    private void StartPage_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingStartPage) return;
        _settings.Current.DefaultStartPage = RadioStartHome.IsChecked == true
            ? StartPageMode.RepoHome
            : StartPageMode.RepoPage;
        SaveSettingsQuiet();
    }

    /// <summary>保存设置；失败不影响当前使用（下次改动会再次尝试）。</summary>
    private void SaveSettingsQuiet()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception)
        {
            // 保存失败不影响当前使用
        }
    }
}
