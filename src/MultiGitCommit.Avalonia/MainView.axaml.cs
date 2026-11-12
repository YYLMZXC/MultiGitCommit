using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaButton = Avalonia.Controls.Button;

namespace MultiGitCommit;

/// <summary>
/// 主界面视图：自用 Git 一键提交推送工具（桌面端嵌在 MainWindow 壳内，移动端为单视图根）。
///
/// ⚠ 本工具【不会修改 git 原生 .git/config】；远端地址保存在每个项目目录的
///   <c>multigitcommit.config.json</c>，为工具独立配置文件。
/// ⚠ 推送时直接使用 url 推送（git push {url} {branch}），不操作 git remote 命令。
/// ⚠ 每个 git 仓库拥有独立配置，切换仓库自动加载对应项目的远端列表。
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>当前项目独立配置（与仓库根目录绑定，切换仓库时重新加载）。</summary>
    private ProjectConfig? _config;

    /// <summary>当前有效 git 仓库根目录；为 null 表示未选择/校验失败。</summary>
    private string? _repoRoot;

    /// <summary>本次加载配置时用户输入的路径（可能与 _repoRoot 不同：输入的是仓库子目录）。</summary>
    private string? _loadedInputPath;

    /// <summary>加载配置的标志位，防止加载过程中触发复选框事件导致重复保存。</summary>
    private bool _loading;

    /// <summary>窄屏（移动端）布局阈值：视图宽度低于该值时启用窄屏布局（手机竖屏约 360~430）。</summary>
    private const double NarrowLayoutThreshold = 700;

    /// <summary>当前是否处于窄屏布局。</summary>
    private bool _isNarrowLayout;

    /// <summary>仓库目录行的按钮 → 原始 Grid 列号（窄屏搬移出去后，宽屏恢复列位用）。</summary>
    private readonly Dictionary<AvaloniaButton, int> _repoHeaderButtonColumns = new();

    /// <summary>仓库路径输入防抖计时器：停止输入 400ms 后再加载，避免每敲一个字符都执行 git 校验。</summary>
    private readonly DispatcherTimer _pathDebounce;

    /// <summary>远端 URL 列表（绑定到 ListBox）。</summary>
    private readonly ObservableCollection<string> _remotes = new();

    /// <summary>本视图生命周期内共享的服务集合（由组合根 <see cref="AppServices"/> 统一装配）。</summary>
    private readonly AppSession _app;

    /// <summary>提交身份读写服务（与 Git 环境设置视图共用同一实例）。</summary>
    private readonly GitIdentityService _identity;

    /// <summary>仓库定位服务（「路径 → 仓库根目录」的唯一判定入口）。</summary>
    private readonly RepoLocator _repoLocator;

    /// <summary>提交推送业务流程（不依赖任何 UI 类型）。</summary>
    private readonly CommitPushService _commitPush;

    /// <summary>拉取更新业务流程（与推送共用凭据解析与命令执行实现）。</summary>
    private readonly GitPullService _pull;

    /// <summary>仓库级 git 命令（初始化 / 分支 / 克隆 / 状态 / 手动命令）。</summary>
    private readonly IRepositoryCommands _commands;

    /// <summary>项目配置仓储（模型不自持久化）。</summary>
    private readonly IProjectConfigStore _configs;

    /// <summary>git 环境（解析与缓存失效由服务统一负责）。</summary>
    private readonly IGitEnvironmentService _environment;

    /// <summary>当前拉取的取消令牌（【取消拉取】按钮 / 视图销毁时使用）。</summary>
    private CancellationTokenSource? _pullCts;

    /// <summary>应用设置来源。</summary>
    private readonly ISettingsProvider _settings;

    public MainView() : this(AppServices.Current.Cli, AppServices.Current.Settings)
    {
    }

    /// <summary>可注入命令执行器（单元测试时传入假实现，无需真实 git 与 UI 运行时）。</summary>
    public MainView(IGitCliRunner cli) : this(cli, AppServices.Current.Settings)
    {
    }

    /// <summary>可注入命令执行器与设置来源（完全脱离真实磁盘设置，便于测试）。</summary>
    public MainView(IGitCliRunner cli, ISettingsProvider settings)
    {
        _app = AppServices.Current.CreateSession(cli, settings, Log);
        _settings = _app.Settings;
        _identity = _app.Identity;
        _repoLocator = _app.RepoLocator;
        _commitPush = _app.CommitPush;
        _pull = _app.Pull;
        _commands = _app.Repositories;
        _configs = _app.ProjectConfigs;
        _environment = _app.GitEnvironment;

        _loading = true;
        InitializeComponent();
        _loading = false;

        // 移动端（单视图生命周期，无 Window）：把覆盖层宿主注册给统一对话框服务，
        // 并启用整页纵向滚动 + 日志区最小高度（视口高度由屏幕决定，内容可超出）。
        // 桌面端保持有限高度测量：日志区随窗口剩余空间伸缩，日志只在 TxtLog 内部滚动，
        // 输出多少行都不会改变窗口尺寸或页面布局。
        if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
        {
            // 直接赋值而不是 ??=：MainView 被重建时旧覆盖层已脱离可视树，
            // 残留的静态宿主会让弹窗推到看不见的旧层上
            UiDialogs.OverlayHost = Overlay;
            RootScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            LogFrame.MinHeight = 160;

            // 安卓没有可用的本地文件夹选择器：按钮改成直接输入路径（可自动新建目录）
            if (OperatingSystem.IsAndroid())
                BtnBrowse.Content = "输入目录";
        }

        ListRemotes.ItemsSource = _remotes;

        // 窄屏响应式：记录仓库目录行按钮的原始列号，按视图宽度在「单行网格」与「换行 WrapPanel」间切换
        foreach (var (button, column) in new[]
                 {
                     (BtnBrowse, 2), (BtnClone, 3), (BtnInitRepo, 4), (BtnCreateBranch, 5),
                     (BtnLoadConfig, 6), (BtnAbout, 7), (BtnGitEnv, 8)
                 })
        {
            _repoHeaderButtonColumns.Add(button, column);
        }

        SizeChanged += OnSizeChanged;
        // 移动端没有 Window.Closing：视图离开可视树时统一收尾，避免拉取进程残留
        DetachedFromVisualTree += (_, _) => Shutdown();

        _pathDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _pathDebounce.Tick += OnPathDebounceTick;

        Log("MultiGitCommit 已启动（跨平台版：Windows / macOS / Linux / Android / iOS）。");
        Log("提示：本工具不会修改 git 原生 .git/config；远端地址保存在每个项目目录下的 multigitcommit.config.json。");
        Log("提示：每个 git 仓库拥有独立配置，切换仓库会自动加载对应项目的远端列表。");

        if (OperatingSystem.IsIOS())
            Log("注意：iOS 沙盒禁止执行子进程，git 相关功能在本平台不可用；界面与设置仅供浏览。");
        else if (OperatingSystem.IsAndroid())
        {
            Log("提示：Android 已内嵌 git（APK 原生库目录），提交推送默认开箱即用；");
            Log("      远端请使用 https 协议（APK 未内置 ssh 客户端，ssh 协议远端不可用）。");
            Log("      安卓无系统文件夹选择器：请点【输入目录】填写仓库绝对路径（可自动新建）；");
            Log("      使用 /storage/emulated/0 需先授予本应用「所有文件访问」权限。");
        }

        // 解析 git 来源（内嵌 / 自动检测 / 系统 PATH / 指定路径），不依赖系统安装 git
        RefreshGitEnvironment();

        // 启动仓库定位：按设置决定「当前目录优先」还是「上次使用的仓库优先」
        string? startup = ResolveStartupRepoPath(out string startupMessage);
        TxtRepoPath.Text = startup ?? string.Empty;
        if (startupMessage.Length > 0) Log(startupMessage);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void OnPathDebounceTick(object? sender, EventArgs e)
    {
        _pathDebounce.Stop();
        LoadConfigFromPath();
    }

    /// <summary>主窗口关闭 / 视图销毁时中止仍在进行的拉取，避免 git 进程残留。</summary>
    internal void Shutdown()
    {
        _pullCts?.Cancel();
        _pathDebounce.Stop();
        SizeChanged -= OnSizeChanged;
        _pathDebounce.Tick -= OnPathDebounceTick;
    }

    // ==================== 日志输出 ====================

    /// <summary>向日志区追加一行（带时间戳），并自动滚动到底部。</summary>
    private void Log(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Log(message));
            return;
        }

        // 统一脱敏：git 输出中可能回显带凭据的 URL，令牌绝不落日志
        TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {GitCredentials.MaskText(message)}{Environment.NewLine}";
        if (ChkAutoScroll.IsChecked == true)
        {
            TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0;
        }
    }

    // ==================== 配置加载与保存 ====================

    /// <summary>
    /// 根据当前仓库路径文本框内容加载项目配置；切换不同仓库自动加载对应项目自己的远端列表。
    /// </summary>
    private void LoadConfigFromPath(bool force = false)
    {
        string path = TxtRepoPath.Text?.Trim() ?? string.Empty;

        // 已成功加载过同一路径时跳过：回填仓库根目录会再次触发 TextChanged，避免重复执行 git 命令
        if (!force && _repoRoot != null && _loadedInputPath != null &&
            string.Equals(path, _loadedInputPath, StringComparison.Ordinal))
            return;

        _repoRoot = null;
        _loadedInputPath = null;
        _config = null;

        _loading = true;
        try
        {
            _remotes.Clear();
            ChkAddAll.IsChecked = false;
            ChkPushTags.IsChecked = false;
            ChkForceCommit.IsChecked = false;
            ChkForcePush.IsChecked = false;
            TxtBranchInfo.Text = string.Empty;
            BtnEditRemote.IsEnabled = false;
            BtnDeleteRemote.IsEnabled = false;
            BtnOpenRemote.IsEnabled = false;

            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!EnsureGitAvailable()) return;

            // 校验是否为合法 git 仓库，并取真正的仓库根目录（判定细节统一在 RepoLocator 内）
            string? repoRoot = _repoLocator.TryResolve(path, out string resolveError);
            if (repoRoot == null)
            {
                Log(resolveError.StartsWith("目录不存在", StringComparison.Ordinal)
                    ? resolveError
                    : $"不是合法的 git 仓库目录：{path}（未发现有效的 .git 仓库）");

                // 输出 git 的真实报错（权限不足 / 目录不可访问 / 进程启动失败等），便于定位
                if (resolveError.Length > 0 && !resolveError.StartsWith("目录不存在", StringComparison.Ordinal))
                    Log("       git 返回：" + resolveError);
                return;
            }

            _repoRoot = repoRoot;
            _loadedInputPath = repoRoot;

            // 记住该仓库，下次启动自动载入它的项目配置
            RememberRepoPath(repoRoot);

            // 输入的是子目录时回填真正的仓库根目录，保证配置文件始终与 .git 同级、git add . 在根执行
            if (!string.Equals(path, repoRoot, StringComparison.Ordinal))
            {
                Log($"已定位到仓库根目录：{repoRoot}");
                TxtRepoPath.Text = repoRoot;
            }

            // 读取本项目独立配置；不存在则返回空白配置（首次保存时才写入文件）
            _config = _configs.Load(repoRoot);

            foreach (var url in _config.RemoteUrls)
            {
                if (!string.IsNullOrWhiteSpace(url))
                    _remotes.Add(url);
            }

            ChkAddAll.IsChecked = _config.CheckAddAll;
            ChkPushTags.IsChecked = _config.CheckPushTags;
            ChkForceCommit.IsChecked = _config.CheckForceCommit;
            ChkForcePush.IsChecked = _config.CheckForcePush;

            string urlSummary = _config.RemoteUrls.Count == 0
                ? "（空）"
                : string.Join("、", _config.RemoteUrls);
            Log($"已加载项目配置：{ProjectConfig.ConfigFileName}（目录：{repoRoot}）");
            Log($"当前项目远端共 {_config.RemoteUrls.Count} 个：{urlSummary}");
        }
        finally
        {
            _loading = false;
            RefreshIdentityStatus();
        }
    }

    /// <summary>保存当前配置到项目根目录 multigitcommit.config.json（仅工具自己的配置文件，不动 .git/config）。</summary>
    private void SaveConfig()
    {
        if (_config == null) return;
        try
        {
            _configs.Save(_config);
            Log($"已保存配置：{_config.ConfigFilePath}");
        }
        catch (Exception ex)
        {
            Log($"保存配置失败：{ex.Message}");
        }
    }

    /// <summary>刷新远端列表 UI（不触发保存）。</summary>
    private void RefreshRemoteList()
    {
        _loading = true;
        try
        {
            _remotes.Clear();
            if (_config != null)
            {
                foreach (var url in _config.RemoteUrls)
                {
                    if (!string.IsNullOrWhiteSpace(url))
                        _remotes.Add(url);
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    // ==================== 窄屏（移动端）响应式布局 ====================

    /// <summary>
    /// 窄屏适配（视图宽度变化时调用）：
    /// 1. 宽度低于阈值（手机竖屏）时，把仓库目录行的一排按钮从单行网格搬移到下方 WrapPanel
    ///    自动换行，避免横向溢出被裁切；恢复宽屏（桌面窗口 / 手机横屏）时搬回原列位；
    /// 2. 约束日志状态栏文本最大宽度，防止长 git 路径把页面撑出横向滚动。
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        double width = Bounds.Width;
        if (width > 0)
            TxtGitStatus.MaxWidth = Math.Min(560, Math.Max(120, width - 40));

        bool narrow = width > 0 && width < NarrowLayoutThreshold;
        if (narrow == _isNarrowLayout)
            return;

        _isNarrowLayout = narrow;
        if (narrow)
        {
            foreach (var button in _repoHeaderButtonColumns.Keys)
            {
                RepoHeaderGrid.Children.Remove(button);
                RepoHeaderNarrow.Children.Add(button);
            }

            RepoHeaderNarrow.IsVisible = true;
        }
        else
        {
            foreach (var pair in _repoHeaderButtonColumns)
            {
                RepoHeaderNarrow.Children.Remove(pair.Key);
                Grid.SetColumn(pair.Key, pair.Value);
                RepoHeaderGrid.Children.Add(pair.Key);
            }

            RepoHeaderNarrow.IsVisible = false;
        }
    }

    // ==================== 仓库目录区域事件 ====================

    /// <summary>路径输入变化：防抖后重新加载项目配置。</summary>
    private void TxtRepoPath_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _pathDebounce.Stop();
        _pathDebounce.Start();
    }

    /// <summary>选择文件夹：浏览选择 git 仓库根目录，选择后自动读取该项目配置。</summary>
    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        // 安卓：StorageProvider 走 SAF 文档树，返回 content:// URI（TryGetLocalPath 恒为 null），
        // 而内嵌 git 必须用真实 POSIX 路径，故改走「输入 / 新建目录」对话框。
        if (OperatingSystem.IsAndroid())
        {
            string? picked = await Dialogs.PickFolderAsync(this, "输入仓库目录",
                "请输入 Git 仓库根目录的绝对路径（与 .git 同级），例如：" + Environment.NewLine +
                "/storage/emulated/0/GitRepos/MyRepo" + Environment.NewLine +
                "目录不存在时会询问是否新建；新建的空目录请再点【初始化仓库】。" + Environment.NewLine +
                "使用共享存储需先在系统设置中授予本应用「所有文件访问」。",
                AndroidGitPlatform.SuggestRepoParent());
            if (picked == null) return;

            TxtRepoPath.Text = picked;
            _pathDebounce.Stop();
            LoadConfigFromPath();
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "请选择 Git 仓库根目录（与 .git 文件夹同级）",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        string? path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            Log("所选位置不是本地文件系统路径（移动端 SAF 目录无法直接当作仓库目录使用），请直接粘贴本地路径。");
            return;
        }

        TxtRepoPath.Text = path.TrimEnd(Path.DirectorySeparatorChar);
        // 赋值会触发 TextChanged 并重新开启防抖计时器，须在赋值后再次停止，避免 400ms 后重复加载一次
        _pathDebounce.Stop();
        LoadConfigFromPath();
    }

    /// <summary>手动重新读取当前目录下的 multigitcommit.config.json。</summary>
    private void BtnLoadConfig_Click(object? sender, RoutedEventArgs e)
    {
        _pathDebounce.Stop();
        LoadConfigFromPath(force: true);
    }

    /// <summary>打开“关于”对话框。</summary>
    private async void BtnAbout_Click(object? sender, RoutedEventArgs e)
    {
        await UiDialogs.ShowAsync(this, new AboutView(), "关于 MultiGitCommit");
    }

    // ==================== 克隆项目 ====================

    /// <summary>
    /// 打开克隆对话框，按用户填写的参数执行 git clone；
    /// 成功后自动切换到该仓库目录、加载其独立配置，并按需把来源 URL 写入推送远端列表。
    /// </summary>
    private async void BtnClone_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureGitAvailable()) return;

        string initialDir = Directory.Exists(TxtRepoPath.Text?.Trim() ?? string.Empty)
            ? TxtRepoPath.Text!.Trim()
            : Environment.CurrentDirectory;

        var cloneView = new CloneView(initialDir);
        if (await UiDialogs.ShowAsync<bool?>(this, cloneView, "克隆项目") != true) return;

        // 去掉粘贴时可能带上的引号 / 空白，否则 git clone 会拿到变形的 URL
        string url = GitCredentials.NormalizeUrl(cloneView.RepoUrl);
        string parentDir = cloneView.ParentDir;
        string targetDir = cloneView.TargetDir;

        if (url.Length == 0)
        {
            Log("克隆地址为空，已取消。");
            return;
        }

        BtnClone.IsEnabled = false;
        try
        {
            // 与对话框里的命令预览共用同一份参数组装规则，保证「所见即所执行」
            string fullArgs = GitCloneArguments.Build(
                cloneView.BranchName, cloneView.Recursive, cloneView.Shallow, url, targetDir);

            Log($"========== 开始克隆仓库：{url} ==========");
            Log($"目标目录：{targetDir}");
            var clone = await _commands.CloneAsync(parentDir, fullArgs, $"git {fullArgs}");
            if (!clone.Success)
            {
                Log("克隆失败：请查看上方日志（常见原因：URL 无效或无权限、目标目录已存在且非空、网络/ssh 配置问题）。");
                return;
            }

            Log($"克隆成功：{targetDir}");

            // 克隆完成后切换到该仓库并加载其独立配置
            TxtRepoPath.Text = targetDir;
            LoadConfigFromPath();
            _pathDebounce.Stop();

            // 按需把克隆来源 URL 写入本项目推送远端列表（工具独立配置，不动 .git/config）
            if (cloneView.AddToRemotes)
            {
                if (_config == null)
                {
                    Log("警告：项目配置未加载，未能将该 URL 写入推送远端列表。");
                }
                else if (_config.RemoteUrls.Contains(url))
                {
                    Log($"推送远端列表中已存在该 URL：{url}");
                }
                else
                {
                    _config.RemoteUrls.Add(url);
                    RefreshRemoteList();
                    SaveConfig();
                    Log($"已将该 URL 添加到推送远端列表：{url}");
                }
            }

            await GetCurrentBranchAsync(_repoRoot ?? targetDir);
            Log("========== 克隆流程完成 ==========");
        }
        catch (Exception ex)
        {
            Log("克隆异常：" + ex);
        }
        finally
        {
            BtnClone.IsEnabled = true;
        }
    }

    // ==================== Git 环境管理（不依赖系统安装 git） ====================

    /// <summary>打开 Git 环境设置对话框：选择 自动检测 / 系统 PATH / 指定路径 / 内嵌 Git。</summary>
    private async void BtnGitEnv_Click(object? sender, RoutedEventArgs e)
    {
        // 传入当前项目的远端 URL：验证账号时优先用同主机的真实远端做探测；
        // 传入仓库根目录：在设置对话框里可直接写入 / 读取仓库级提交身份
        // 传入会话：子视图复用同一套执行器 / 身份服务 / git 环境，不再自行 new
        var settingsView = new GitSettingsView(_settings.Current, _config?.RemoteUrls, _repoRoot, _app);
        if (await UiDialogs.ShowAsync<bool?>(this, settingsView, "Git 环境设置") != true) return;

        _settings.Current.GitSource = settingsView.SelectedSource;
        _settings.Current.CustomGitPath = settingsView.SelectedCustomPath;
        _settings.Current.CredentialSource = settingsView.SelectedCredentialSource;
        _settings.Current.StartupRepo = settingsView.SelectedStartupRepo;
        _settings.Current.StageMode = settingsView.SelectedStageMode;
        try
        {
            _settings.Save();
            Log($"已保存 Git 环境设置：{_settings.Current.SettingsFilePath}");
        }
        catch (Exception ex)
        {
            Log($"保存 Git 环境设置失败：{ex.Message}");
        }

        RefreshGitEnvironment();
        Log(_settings.Current.StartupRepo switch
        {
            StartupRepoMode.LastUsed => "启动时加载的仓库：上次使用的仓库优先（下次启动生效）。",
            StartupRepoMode.CurrentDirectoryThenLastUsed =>
                "启动时加载的仓库：当前目录优先，找不到时回退上次项目（下次启动生效）。",
            _ => "启动时加载的仓库：只读取当前目录，不自动加载上次项目（下次启动生效）。"
        });
        Log(_settings.Current.StageMode == StageMode.Select
            ? "执行 git add 时的文件添加方式：可选提交（提交前勾选要添加的文件）。"
            : "执行 git add 时的文件添加方式：全提交（git add .）。");
    }

    /// <summary>按当前设置重新解析 git，更新状态栏显示并输出日志。</summary>
    private void RefreshGitEnvironment()
    {
        // 解析 + 凭据助手缓存失效都由环境服务统一负责，视图不直接读写静态状态
        GitEnvironmentSnapshot env = _environment.Refresh(
            _settings.Current.GitSource, _settings.Current.CustomGitPath);

        TxtGitStatus.Text = env.StatusText;
        ToolTip.SetTip(TxtGitStatus, env.ToolTipText);

        Log(env.Available
            ? $"Git 环境：{env.Description}"
            : "未找到可用的 git：请点击【Git 环境】按钮选择环境 git（系统 PATH）或指定路径 / 内嵌 Git。");
    }

    /// <summary>确保 git 可用（已解析成功）；不可用时输出日志并返回 false。</summary>
    private bool EnsureGitAvailable()
    {
        if (_environment.IsAvailable) return true;

        Log("流程终止：未找到可用的 git，请点击【Git 环境】按钮配置（自动检测 / 系统 PATH / 指定路径 / 内嵌 Git）。");
        return false;
    }

    // ==================== 仓库初始化 / 默认分支 ====================

    /// <summary>默认分支名（初始化仓库 / 创建默认分支时的预填值，可在弹窗中修改）。</summary>
    private const string DefaultBranchName = "main";

    /// <summary>
    /// 初始化仓库：对当前目录执行 git init，并创建默认分支。
    /// 目录不存在时询问是否自动新建；已是 git 仓库则跳过初始化，避免误覆盖。
    /// </summary>
    private async void BtnInitRepo_Click(object? sender, RoutedEventArgs e)
    {
        BtnInitRepo.IsEnabled = false;
        try
        {
            string path = TxtRepoPath.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                Log("请先选择或输入要初始化的仓库目录。");
                return;
            }

            if (!EnsureGitAvailable()) return;

            if (!Directory.Exists(path))
            {
                bool create = await Dialogs.ConfirmAsync(this, "初始化仓库",
                    $"目录不存在，是否新建该目录？{Environment.NewLine}{path}");
                if (!create) return;

                try
                {
                    Directory.CreateDirectory(path);
                    Log($"已新建目录：{path}");
                }
                catch (Exception ex)
                {
                    Log($"新建目录失败：{ex.Message}");
                    return;
                }
            }

            if (_repoLocator.IsWorkTree(path))
            {
                Log($"该目录已经是 git 仓库，无需重复初始化：{path}");
                return;
            }

            string? branch = await PromptBranchName("初始化仓库", "请输入默认分支名（例如 main / master）：");
            if (branch == null) return;

            Log($"========== 初始化 git 仓库：{path} ==========");
            var init = await _commands.InitAsync(path);
            if (!init.Success)
            {
                Log("git init 执行失败，初始化终止。");
                return;
            }

            // git init 默认分支可能是 master，统一改为用户指定的默认分支
            await SetDefaultBranchAsync(path, branch);

            _pathDebounce.Stop();
            LoadConfigFromPath();
            Log("========== 仓库初始化完成 ==========");
        }
        catch (Exception ex)
        {
            Log("初始化仓库异常：" + ex);
        }
        finally
        {
            BtnInitRepo.IsEnabled = true;
        }
    }

    /// <summary>
    /// 创建默认分支：为当前仓库创建/切换默认分支。
    /// 空仓库直接设置 HEAD 指向该分支；已有提交则新建（或切换）分支。
    /// </summary>
    private async void BtnCreateBranch_Click(object? sender, RoutedEventArgs e)
    {
        BtnCreateBranch.IsEnabled = false;
        try
        {
            string path = TxtRepoPath.Text?.Trim() ?? string.Empty;
            if (!Directory.Exists(path))
            {
                Log($"目录不存在：{path}");
                return;
            }

            if (!_repoLocator.IsWorkTree(path))
            {
                Log("该目录不是 git 仓库，请先点击【初始化仓库】。");
                return;
            }

            string? branch = await PromptBranchName("创建默认分支", "请输入要创建/切换的默认分支名：");
            if (branch == null) return;

            Log($"========== 创建默认分支：{branch} ==========");
            await SetDefaultBranchAsync(path, branch);

            _pathDebounce.Stop();
            LoadConfigFromPath();
            _repoRoot ??= path;
            await GetCurrentBranchAsync(_repoRoot);
            Log("========== 默认分支处理完成 ==========");
        }
        catch (Exception ex)
        {
            Log("创建默认分支异常：" + ex);
        }
        finally
        {
            BtnCreateBranch.IsEnabled = true;
        }
    }

    /// <summary>设置默认分支（空仓库 / 已有提交两种情况由命令服务判定）。</summary>
    private async Task SetDefaultBranchAsync(string repoDir, string branch)
    {
        SetDefaultBranchResult result = await _commands.SetDefaultBranchAsync(repoDir, branch);
        Log(result.Message);
    }

    /// <summary>
    /// 弹出分支名输入框并做基本合法性校验；返回 null 表示用户取消。
    /// </summary>
    private async Task<string?> PromptBranchName(string title, string message)
    {
        while (true)
        {
            string? text = await Dialogs.PromptAsync(this, title, message, DefaultBranchName);
            if (text == null) return null; // 用户取消

            text = text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                await Dialogs.ShowMessageAsync(this, title, "分支名不能为空。");
                continue;
            }

            if (!GitRefName.IsValidBranchName(text))
            {
                await Dialogs.ShowMessageAsync(this, title,
                    "分支名不合法：不能包含空格或 ~ ^ : ? * [ \\ .. // 等字符，且不能以 - / . 开头或结尾。");
                continue;
            }

            return text;
        }
    }

    // ==================== 远端地址管理事件 ====================

    private void ListRemotes_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = ListRemotes.SelectedIndex >= 0;
        BtnEditRemote.IsEnabled = hasSelection;
        BtnDeleteRemote.IsEnabled = hasSelection;
        BtnOpenRemote.IsEnabled = hasSelection;
    }

    /// <summary>添加远端 URL，并自动保存到项目目录 multigitcommit.config.json。</summary>
    private async void BtnAddRemote_Click(object? sender, RoutedEventArgs e)
    {
        if (_config == null) { Log("请先选择有效的 git 仓库目录。"); return; }

        var text = await Dialogs.PromptAsync(this, "添加远端",
            "请输入推送远端 URL（支持 ssh/https 格式，例如 https://gitee.com/user/repo.git）：", string.Empty);
        if (text == null) return;

        // 去掉粘贴时可能带上的引号 / 空白，避免写入脏 URL 后推送与验证都失败
        string url = GitCredentials.NormalizeUrl(text);
        if (url.Length == 0) { Log("远端 URL 不能为空。"); return; }
        if (_config.RemoteUrls.Contains(url)) { Log($"远端 URL 已存在：{url}"); return; }

        _config.RemoteUrls.Add(url);
        RefreshRemoteList();
        Log($"已添加远端：{url}");
        SaveConfig();
    }

    /// <summary>编辑选中的远端 URL，并自动保存。</summary>
    private async void BtnEditRemote_Click(object? sender, RoutedEventArgs e)
    {
        if (_config == null) { Log("请先选择有效的 git 仓库目录。"); return; }
        if (ListRemotes.SelectedIndex < 0 || ListRemotes.SelectedIndex >= _config.RemoteUrls.Count)
        {
            Log("请先选中要编辑的远端。");
            return;
        }

        int index = ListRemotes.SelectedIndex;
        string oldUrl = _config.RemoteUrls[index];
        var text = await Dialogs.PromptAsync(this, "编辑远端", "修改远端 URL：", oldUrl);
        if (text == null) return;

        string url = GitCredentials.NormalizeUrl(text);
        if (url.Length == 0) { Log("远端 URL 不能为空。"); return; }

        _config.RemoteUrls[index] = url;
        RefreshRemoteList();
        Log($"已修改远端：{oldUrl} → {url}");
        SaveConfig();
    }

    /// <summary>在系统默认浏览器中打开选中的远端 URL（TopLevel.Launcher 跨平台）。</summary>
    private async void BtnOpenRemote_Click(object? sender, RoutedEventArgs e)
    {
        if (ListRemotes.SelectedItem is not string selected)
        {
            Log("请先选中要打开的远端。");
            return;
        }

        if (string.IsNullOrWhiteSpace(selected))
        {
            Log("远端 URL 为空，无法打开。");
            return;
        }

        try
        {
            // 跨平台：交给系统默认程序处理（Windows 走 ShellExecute，macOS 等价于 open，移动端走系统浏览器）
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null || !Uri.TryCreate(selected, UriKind.Absolute, out var uri))
            {
                Log("无法打开：当前环境不支持外部链接。");
                return;
            }

            if (await topLevel.Launcher.LaunchUriAsync(uri))
                Log($"已在浏览器中打开：{selected}");
            else
                Log("系统没有能处理该 URL 的程序。");
        }
        catch (Exception ex)
        {
            Log($"打开浏览器失败：{ex.Message}");
        }
    }

    /// <summary>删除选中的远端 URL，并自动保存。</summary>
    private void BtnDeleteRemote_Click(object? sender, RoutedEventArgs e)
    {
        if (_config == null) { Log("请先选择有效的 git 仓库目录。"); return; }
        if (ListRemotes.SelectedItem is not string selected)
        {
            Log("请先选中要删除的远端。");
            return;
        }

        _config.RemoteUrls.Remove(selected);
        RefreshRemoteList();
        Log($"已删除远端：{selected}");
        SaveConfig();
    }

    private void ChkAddAll_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _config == null) return;
        _config.CheckAddAll = ChkAddAll.IsChecked == true;
        SaveConfig();
    }

    private void ChkPushTags_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _config == null) return;
        _config.CheckPushTags = ChkPushTags.IsChecked == true;
        SaveConfig();
    }

    private void ChkForceCommit_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _config == null) return;
        _config.CheckForceCommit = ChkForceCommit.IsChecked == true;
        SaveConfig();
    }

    private void ChkForcePush_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _config == null) return;
        _config.CheckForcePush = ChkForcePush.IsChecked == true;
        SaveConfig();
    }

    // ==================== 主流程：执行提交并推送全部远端 ====================

    private async void BtnCommitPush_Click(object? sender, RoutedEventArgs e)
    {
        string idleText = BtnCommitPush.Content?.ToString() ?? "执行提交并推送全部远端";
        BtnCommitPush.Content = "执行中…";
        BtnCommitPush.IsEnabled = false;
        try
        {
            // 若路径文本框与当前加载的仓库不一致，先重新加载配置
            if (_repoRoot == null ||
                !string.Equals(TxtRepoPath.Text?.Trim(), _loadedInputPath, StringComparison.Ordinal))
            {
                _pathDebounce.Stop();
                LoadConfigFromPath(force: true);
            }

            if (_repoRoot == null || _config == null)
            {
                Log("流程终止：请先选择有效的 git 仓库目录。");
                return;
            }

            string repoDir = _repoRoot;
            if (!EnsureGitAvailable()) return;

            // 流程细节全部在 CommitPushService 内；这里只负责收集界面选择并呈现日志
            await _commitPush.RunAsync(new CommitPushRequest
            {
                RepoRoot = repoDir,
                RemoteUrls = _config.RemoteUrls,
                AddAll = _config.CheckAddAll,
                ForceCommit = _config.CheckForceCommit,
                ForcePush = _config.CheckForcePush,
                PushTags = _config.CheckPushTags,
                Remark = TxtCommitMessage.Text?.Trim() ?? string.Empty,
                // 添加方式由【Git 环境】设置决定：全提交 → 不传回调（走 git add .）；可选提交 → 弹窗勾选文件
                SelectFilesToStage = _settings.Current.StageMode == StageMode.Select
                    ? SelectFilesToStageAsync
                    : null,
                EnsureIdentity = EnsureIdentityInteractiveAsync,
                OnBranchDetected = branch => TxtBranchInfo.Text = $"当前分支：{branch}"
            }, Log);
        }
        catch (Exception ex)
        {
            Log("发生异常：" + ex);
        }
        finally
        {
            BtnCommitPush.Content = idleText;
            BtnCommitPush.IsEnabled = true;
        }
    }

    /// <summary>
    /// 选择性 git add 的界面部分（由 <see cref="CommitPushService"/> 在勾选了 add 后回调）：
    /// 弹出文件勾选对话框，返回用户要添加的相对路径；用户取消返回 null 让流程终止。
    /// </summary>
    private async Task<IReadOnlyList<string>?> SelectFilesToStageAsync(IReadOnlyList<WorkingTreeFile> files)
    {
        var stageView = new StageFilesView(_repoRoot ?? string.Empty, files);
        if (await UiDialogs.ShowAsync<bool?>(this, stageView, "选择要添加的文件") != true)
        {
            Log("已取消选择要添加的文件。");
            return null;
        }

        IReadOnlyList<string> selected = stageView.SelectedPaths;
        Log($"已勾选 {selected.Count} / {files.Count} 个文件执行 git add。");
        return selected;
    }

    // ==================== 拉取更新 ====================

    /// <summary>
    /// 【拉取更新】：选择远端与策略后执行 git pull。
    /// 执行中按钮变为【取消拉取】，再点一次即中止 git 进程。
    /// </summary>
    private async void BtnPull_Click(object? sender, RoutedEventArgs e)
    {
        if (_pullCts != null)
        {
            _pullCts.Cancel(); // 正在拉取：再点一次就是取消
            Log("正在取消拉取…");
            return;
        }

        if (_repoRoot == null)
        {
            Log("请先选择有效的 git 仓库目录，再拉取更新。");
            return;
        }

        if (!EnsureGitAvailable()) return;

        var remotes = _remotes.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (remotes.Count == 0)
        {
            Log("尚未配置推送远端地址，请先在【推送远端地址管理】中添加远端 URL。");
            return;
        }

        string? branch = await GetCurrentBranchAsync(_repoRoot);
        if (branch == null)
        {
            Log("无法确定当前分支（可能处于 detached HEAD 状态），已取消拉取。");
            return;
        }

        var pullView = new PullView(_repoRoot, remotes, branch);
        if (await UiDialogs.ShowAsync<bool?>(this, pullView, "拉取更新") != true) return;

        // 工作区有未提交改动时给出提示（不阻止：能否拉取最终由 git 判断）
        int changed = await _commands.CountChangedFilesAsync(_repoRoot);
        if (changed > 0)
        {
            bool keepGoing = await Dialogs.ConfirmAsync(this, "拉取更新",
                $"当前工作区有 {changed} 项未提交的改动。" + Environment.NewLine +
                "如果远端改动涉及这些文件，git 会拒绝拉取或产生冲突。" + Environment.NewLine + Environment.NewLine +
                "仍要继续拉取吗？（建议先提交或暂存）");
            if (!keepGoing) return;
        }

        _pullCts = new CancellationTokenSource();
        string idleText = BtnPull.Content?.ToString() ?? "拉取更新";
        BtnPull.Content = "取消拉取";

        try
        {
            PullResult result = await _pull.PullAsync(new PullRequest
            {
                RepoRoot = _repoRoot,
                RemoteUrl = pullView.SelectedRemoteUrl,
                Branch = branch,
                Strategy = pullView.SelectedStrategy
            }, Log, _pullCts.Token);

            if (result.Cancelled)
                Log("拉取已取消。");
            else if (result.TimedOut)
                Log("拉取超时，已终止 git 进程。");
            else if (result.Success)
                Log("拉取完成。");
            else
                Log("拉取失败，请查看上方 git 输出与提示。");
        }
        catch (Exception ex)
        {
            Log("拉取发生异常：" + ex.Message);
        }
        finally
        {
            BtnPull.Content = idleText;
            _pullCts?.Dispose();
            _pullCts = null;

            // 拉取可能改变 HEAD / 工作区：刷新当前分支显示
            if (_repoRoot != null)
                await GetCurrentBranchAsync(_repoRoot);
        }
    }

    /// <summary>获取当前 git 分支名称并刷新界面显示（判定逻辑在 CommitPushService 内）。</summary>
    private async Task<string?> GetCurrentBranchAsync(string repoDir)
    {
        string? branch = await _commitPush.GetCurrentBranchAsync(repoDir, Log);
        if (branch != null) TxtBranchInfo.Text = $"当前分支：{branch}";
        return branch;
    }

    // ==================== 提交历史 ====================

    /// <summary>打开提交历史对话框：浏览当前分支的历史提交，并可重置到指定提交。</summary>
    private async void BtnHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_repoRoot == null)
        {
            Log("请先选择有效的 git 仓库目录，再查看提交历史。");
            return;
        }

        if (!EnsureGitAvailable()) return;

        await UiDialogs.ShowAsync(this, new HistoryView(_repoRoot, _app.History), "提交历史");

        // 对话框内可能执行过 reset：关闭后重新读取分支信息，保持界面与仓库一致
        if (_repoRoot != null)
            await GetCurrentBranchAsync(_repoRoot);
    }

    // ==================== 手动命令执行区 ====================

    private async void BtnRunCommand_Click(object? sender, RoutedEventArgs e)
    {
        await ExecuteManualCommandAsync();
    }

    /// <summary>命令输入框回车直接执行。</summary>
    private void TxtCommand_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = ExecuteManualCommandAsync();
        }
    }

    /// <summary>在当前选定的仓库目录执行任意 git/shell 命令，stdout/stderr 输出到日志框。</summary>
    private async Task ExecuteManualCommandAsync()
    {
        string command = TxtCommand.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            Log("请输入要执行的命令。");
            return;
        }

        try
        {
            string workDir = _repoRoot ?? TxtRepoPath.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                workDir = Environment.CurrentDirectory;

            Log($"执行命令：{command}（目录：{workDir}）");
            ManualCommandResult r = await _commands.RunManualCommandAsync(workDir, command);
            if (r.Error != null)
            {
                Log(r.Error);
                return;
            }

            Log($"命令退出码：{r.Result?.ExitCode ?? -1}");
        }
        catch (Exception ex)
        {
            Log("命令执行异常：" + ex);
        }
    }

    // ==================== 其他 ====================

    /// <summary>清空日志区。</summary>
    private void BtnClearLog_Click(object? sender, RoutedEventArgs e)
    {
        TxtLog.Text = string.Empty;
    }

    // ==================== 启动仓库定位（记住上次项目） ====================

    /// <summary>
    /// 决定启动时默认加载哪个仓库目录，顺序由设置「启动时加载的仓库」决定：
    /// 「只读取当前目录」（默认）：
    /// 1) 当前目录（含程序自身目录）向上最近的 .git / multigitcommit.config.json 所在目录，
    ///    使 IDE 调试或把程序放在仓库内的场景直接加载当前仓库；
    /// 2) 当前目录不在仓库内时，不自动打开上次项目，也不填入一个必然无效的路径，
    ///    留空由用户点【浏览】选择。
    /// 「上次使用的仓库优先」：1) 上次成功加载过的仓库；2) 再走上面的当前目录逻辑。
    /// 「当前目录优先，找不到时回退」：先走当前目录逻辑，找不到再回退到上次使用的仓库。
    /// </summary>
    private string? ResolveStartupRepoPath(out string message)
    {
        string cwd = Environment.CurrentDirectory;
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string cwdFull = string.IsNullOrWhiteSpace(cwd)
            ? string.Empty
            : Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar);

        string last = _settings.Current.LastRepoPath ?? string.Empty;
        bool hasLast = last.Length > 0 && Directory.Exists(last);
        StartupRepoMode mode = _settings.Current.StartupRepo;

        // 设置里选了「上次使用的仓库优先」：直接沿用，不再看当前目录
        if (mode == StartupRepoMode.LastUsed && hasLast)
        {
            message = $"已载入上次使用的仓库目录（设置：上次使用的仓库优先）：{last}";
            return last;
        }

        // 当前目录（或其上级）所在的仓库：默认最优先
        string? cwdRepo = RepoLocator.FindRepoAncestor(cwdFull.Length > 0 ? cwdFull : baseDir);
        if (cwdRepo != null)
        {
            message = $"已载入当前目录所在的仓库：{cwdRepo}";
            return cwdRepo;
        }

        // 只有显式选择「当前目录优先，找不到时回退」才自动打开上次项目；
        // 默认的「只读取当前目录」不会把上个项目带进来
        if (mode == StartupRepoMode.CurrentDirectoryThenLastUsed && hasLast)
        {
            message = $"当前目录不是 Git 仓库，已载入上次使用的仓库目录（设置：找不到时回退）：{last}";
            return last;
        }

        string hint = OperatingSystem.IsAndroid()
            ? "安卓无系统文件夹选择器：请点【输入目录】填写仓库绝对路径（可自动新建），" +
              "或先用【克隆项目】/【初始化仓库】创建仓库；如需自动打开上次项目，可在【Git 环境】→「启动时加载的仓库」中修改。"
            : "请点击【浏览…】选择 Git 仓库根目录；" +
              "如需自动打开上次项目，可在【Git 环境】→「启动时加载的仓库」中修改。";

        message = $"当前目录及上级目录中未找到 Git 仓库，按设置不自动加载上次项目，已留空仓库目录：{hint}";
        return null;
    }

    /// <summary>记住本次成功加载的仓库目录，下次启动自动载入其项目配置。</summary>
    private void RememberRepoPath(string repoRoot)
    {
        if (string.Equals(_settings.Current.LastRepoPath, repoRoot, StringComparison.Ordinal))
            return;

        _settings.Current.LastRepoPath = repoRoot;
        try
        {
            _settings.Save();
        }
        catch (Exception)
        {
            // 保存失败不影响当前使用
        }
    }

    // ==================== 提交身份（user.name / user.email） ====================
    // 读写细节在 GitIdentityService（与 Git 环境设置视图共用），这里只负责界面呈现。

    /// <summary>刷新界面上的提交身份状态文本。</summary>
    private void RefreshIdentityStatus()
    {
        if (_repoRoot == null)
        {
            TxtIdentity.Text = "提交身份：未选择仓库";
            TxtIdentity.Foreground = Brush.Parse("#666666");
            return;
        }

        var (name, email) = _identity.Read(_repoRoot);
        bool ok = name.Length > 0 && email.Length > 0;
        TxtIdentity.Text = ok ? $"提交身份：{name} <{email}>" : "提交身份：未设置（提交前必须设置）";
        TxtIdentity.Foreground = ok
            ? Brush.Parse("#666666")
            : Brush.Parse("#B00020");
    }

    /// <summary>
    /// 提交身份缺失时的界面补全（由 <see cref="CommitPushService"/> 在确认缺失后回调）：
    /// 优先套用设置里已保存的身份，没有则弹窗引导填写；用户取消返回 false，流程应终止。
    /// </summary>
    private async Task<bool> EnsureIdentityInteractiveAsync(string repoDir)
    {
        Log("未检测到 git 提交身份（user.name / user.email）：这是生成 commit 的前置条件，缺失时 git 会直接报错。");

        var saved = _settings.Current.CommitIdentity;
        GitIdentity identity;

        if (saved != null && saved.IsComplete)
        {
            identity = saved;
            Log($"套用已保存的提交身份：{identity.Name} <{identity.Email}>");
        }
        else
        {
            var identityView = new IdentityView(saved);
            if (await UiDialogs.ShowAsync<bool?>(this, identityView, "提交身份（git user.name / user.email）") != true)
            {
                Log("已取消设置提交身份，无法生成 commit。");
                return false;
            }

            identity = identityView.SelectedIdentity;
            _settings.Current.CommitIdentity = identity;
            try
            {
                _settings.Save();
                Log($"已保存提交身份到：{_settings.Current.SettingsFilePath}");
            }
            catch (Exception ex)
            {
                Log("保存提交身份失败：" + ex.Message);
            }
        }

        return ApplyCommitIdentity(identity, repoDir);
    }

    /// <summary>把身份写入 git config（仓库级或全局），并刷新界面状态。</summary>
    private bool ApplyCommitIdentity(GitIdentity identity, string repoDir)
    {
        if (!_identity.TryApply(identity, repoDir, out string error))
        {
            Log($"写入提交身份失败：{error}");
            return false;
        }

        Log($"已写入提交身份（{(identity.Scope == IdentityScope.Global ? "全局 --global" : "本仓库 --local")}）：" +
            $"{identity.Name} <{identity.Email}>");
        RefreshIdentityStatus();
        return true;
    }

    /// <summary>手动打开【提交身份】设置对话框并立即应用。</summary>
    private async void BtnIdentity_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureGitAvailable()) return;

        var (name, email) = _identity.Read(_repoRoot);
        var current = new GitIdentity
        {
            Name = name,
            Email = email,
            Scope = _settings.Current.CommitIdentity?.Scope ?? IdentityScope.Local
        };

        var identityView = new IdentityView(current);
        if (await UiDialogs.ShowAsync<bool?>(this, identityView, "提交身份（git user.name / user.email）") != true) return;

        _settings.Current.CommitIdentity = identityView.SelectedIdentity;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log("保存提交身份失败：" + ex.Message);
        }

        string dir = _repoRoot ?? TxtRepoPath.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir)) dir = Environment.CurrentDirectory;
        ApplyCommitIdentity(identityView.SelectedIdentity, dir);
    }
}
