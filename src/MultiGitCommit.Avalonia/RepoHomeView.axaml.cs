using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
// 安卓 TFM 的隐式全局 using 会引入 Android.Widget.Button，与 Avalonia 的 Button 冲突，固定别名消歧
using AvaloniaButton = Avalonia.Controls.Button;

namespace MultiGitCommit;

/// <summary>
/// 仓库管理首页：最近打开的项目记录（保存在 AppSettings.RecentRepoPaths）+ 本地 Git 仓库自动发现
/// （后台线程扫描，算法与 MCP find_repos 共用 RepoScanner），支持唤起系统文件管理器选目录打开。
/// 两个列表的每一项都显示仓库名称（目录名）、完整路径与当前分支
/// （由 RepoBranchReader 解析 .git/HEAD，不启动 git 进程），
/// 并可点【打开文件夹】在系统文件管理器中打开该仓库目录。
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

    /// <summary>应用设置来源（只读展示；由宿主会话注入）。</summary>
    private ISettingsProvider _settings = AppServices.Current.Settings;

    /// <summary>设置的变更与落盘（最近项目 / 默认启动页），规则不在视图里。</summary>
    private AppSettingsService _settingsEditor = AppServices.Current.SettingsEditor;

    /// <summary>最近打开的项目（绑定到 ListRecent，含名称、完整路径与当前分支）。</summary>
    private readonly ObservableCollection<RepoHomeItem> _recent = new();

    /// <summary>自动发现的本地仓库（绑定到 ListDiscovered，含名称、完整路径与当前分支）。</summary>
    private readonly ObservableCollection<RepoHomeItem> _discovered = new();

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
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    /// <summary>
    /// 宿主注入：本视图由 MainView 的 XAML 静态创建（构造时尚未完成宿主字段初始化），
    /// 因此在 MainView 构造流程中回填依赖并做首次刷新。
    /// </summary>
    internal void Attach(MainView host, AppSession session)
    {
        _host = host;
        _settings = session.Settings;
        _settingsEditor = session.SettingsEditor;
        RefreshRecent();
        RefreshStartPageRadios();
    }

    /// <summary>每次显示首页时调用：刷新最近项目列表，并按需启动一次仓库自动发现。</summary>
    public void OnShown()
    {
        // 覆盖层刚显示时 Bounds 可能还是旧值，布局完成后再按视口高度分配列表高度
        Dispatcher.UIThread.Post(ApplyResponsiveLayout, DispatcherPriority.Loaded);
        RefreshRecent();

        // 在项目页切换分支 / 新建分支后返回首页，自动发现列表的分支信息也要同步刷新
        foreach (RepoHomeItem item in _discovered) item.RefreshBranch();

        if (!_scanStarted)
        {
            _scanStarted = true;
            _ = ScanAsync();
        }
    }

    /// <summary>
    /// 按视口高度给两个列表限高：最近项目是 Auto 行，条目多（或窄屏路径换行）时会把下面的
    /// 自动发现列表和底部「默认启动页」操作条挤出视口。两个列表各自内部滚动、互不嵌套，
    /// 底部条因此始终留在视口内；窄屏按钮与操作条由 XAML 的 WrapPanel 自动换行。
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        double height = Bounds.Height;
        if (height <= 0) return;

        RecentScroll.MaxHeight = Math.Clamp(height * 0.24, 96, 220);
        DiscoveredScroll.MaxHeight = Math.Clamp(height * 0.34, 140, 360);
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
                if (!string.IsNullOrWhiteSpace(path)) _recent.Add(new RepoHomeItem(path));
            }
        }

        TxtRecentEmpty.IsVisible = _recent.Count == 0;
        BtnClearRecent.IsEnabled = _recent.Count > 0;
    }

    private void RemoveRecent_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as AvaloniaButton)?.DataContext is not RepoHomeItem item) return;
        _settingsEditor.TryRemoveRecent(item.RepoPath, out _);
        RefreshRecent();
    }

    private void BtnClearRecent_Click(object? sender, RoutedEventArgs e)
    {
        _settingsEditor.TryClearRecent(out _);
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
        TxtScanStatus.Text = Loc.F($"正在扫描本地 Git 仓库（深度 {ScanMaxDepth}，上限 {ScanLimit}）…");

        try
        {
            // 分支读取（解析 .git/HEAD）与目录扫描一起放后台线程：最多 300 个仓库，避免阻塞 UI
            List<RepoHomeItem> found = await Task.Run(
                () => RepoScanner.Scan(roots, ScanMaxDepth, ScanLimit, ct)
                    .Select(path => new RepoHomeItem(path)).ToList(), ct);

            foreach (RepoHomeItem repo in found) _discovered.Add(repo);
            TxtScanStatus.Text = found.Count >= ScanLimit
                ? Loc.F($"已发现 {found.Count} 个仓库（达到上限，可能未扫全）")
                : Loc.F($"共发现 {found.Count} 个本地仓库");
            TxtDiscoveredEmpty.IsVisible = found.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // 扫描被新扫描 / 页面销毁取代，静默退出
        }
        catch (Exception ex)
        {
            TxtScanStatus.Text = Loc.T("扫描失败：") + ex.Message;
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
        if (_host == null || (sender as AvaloniaButton)?.DataContext is not RepoHomeItem item) return;
        _host.OpenRepoFromHome(item.RepoPath);
    }

    /// <summary>列表「打开文件夹」按钮（两个列表共用）：在系统文件管理器中打开该项目的目录，不切换页面。</summary>
    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as AvaloniaButton)?.DataContext is not RepoHomeItem item) return;
        if (FileOpener.TryOpenFolder(item.RepoPath, out string error)) return;

        await Dialogs.ShowMessageAsync(this, Loc.T("打开文件夹"), Loc.T("× 打开文件夹失败：") + error);
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
        _settingsEditor.TrySetDefaultStartPage(
            RadioStartHome.IsChecked == true ? StartPageMode.RepoHome : StartPageMode.RepoPage, out _);
    }
}

/// <summary>
/// 仓库管理列表项：仓库名称（目录名）+ 完整路径 + 当前分支。
/// 分支由 <see cref="RepoBranchReader"/> 解析 .git/HEAD 得到（不启动 git 进程，适合批量展示）；
/// 分支可变（在项目页切换分支后返回首页会重读），故实现属性变更通知让绑定自动刷新。
/// </summary>
public sealed class RepoHomeItem : INotifyPropertyChanged
{
    private string _branch;

    public RepoHomeItem(string repoPath)
    {
        RepoPath = repoPath;
        Name = DeriveName(repoPath);
        _branch = RepoBranchReader.Read(repoPath);
    }

    /// <summary>仓库名称：仓库根目录的文件夹名；盘符 / 根目录等无法取名时回退为路径本身。</summary>
    public string Name { get; }

    /// <summary>仓库根目录完整路径。</summary>
    public string RepoPath { get; }

    /// <summary>从路径取最后一段目录名，兼容末尾分隔符、盘符根目录与非法路径。</summary>
    private static string DeriveName(string repoPath)
    {
        try
        {
            string full = Path.GetFullPath(repoPath);
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
            return string.IsNullOrEmpty(name) ? repoPath : name;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return repoPath;
        }
    }

    /// <summary>当前分支名；无法读取时显示「—」，游离 HEAD 显示 HEAD@短哈希。</summary>
    public string Branch
    {
        get => _branch;
        private set
        {
            if (_branch == value) return;
            _branch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Branch)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>重新读取当前分支（切换分支 / 拉取 / 克隆等操作后回到首页时调用）。</summary>
    public void RefreshBranch() => Branch = RepoBranchReader.Read(RepoPath);
}
