using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MultiGitCommit;

/// <summary>
/// 主窗口：自用 Git 一键提交推送工具（macOS / Avalonia 版）。
///
/// ⚠ 本工具【不会修改 git 原生 .git/config】；远端地址保存在每个项目目录的
///   <c>multigitcommit.config.json</c>，为工具独立配置文件。
/// ⚠ 推送时直接使用 url 推送（git push {url} {branch}），不操作 git remote 命令。
/// ⚠ 每个 git 仓库拥有独立配置，切换仓库自动加载对应项目的远端列表。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>当前项目独立配置（与仓库根目录绑定，切换仓库时重新加载）。</summary>
    private ProjectConfig? _config;

    /// <summary>当前有效 git 仓库根目录；为 null 表示未选择/校验失败。</summary>
    private string? _repoRoot;

    /// <summary>本次加载配置时用户输入的路径（可能与 _repoRoot 不同：输入的是仓库子目录）。</summary>
    private string? _loadedInputPath;

    /// <summary>加载配置的标志位，防止加载过程中触发复选框事件导致重复保存。</summary>
    private bool _loading;

    /// <summary>仓库路径输入防抖计时器：停止输入 400ms 后再加载，避免每敲一个字符都执行 git 校验。</summary>
    private readonly DispatcherTimer _pathDebounce;

    /// <summary>远端 URL 列表（绑定到 ListBox）。</summary>
    private readonly ObservableCollection<string> _remotes = new();

    /// <summary>
    /// 外部命令执行器：进程创建、UTF-8 编码、非交互环境、超时杀进程树、PATH 注入
    /// 等细节全部由它负责，本窗口只关心「跑什么命令、结果怎么用」。
    /// </summary>
    private readonly IGitCliRunner _cli;

    /// <summary>提交身份读写服务（与 Git 环境设置窗口共用同一套实现）。</summary>
    private readonly GitIdentityService _identity;

    /// <summary>仓库定位服务（「路径 → 仓库根目录」的唯一判定入口）。</summary>
    private readonly RepoLocator _repoLocator;

    /// <summary>带日志呈现的命令执行（日志格式只有一份实现）。</summary>
    private readonly ILoggedGitRunner _logged;

    /// <summary>提交推送业务流程（不依赖任何 UI 类型）。</summary>
    private readonly CommitPushService _commitPush;

    /// <summary>拉取更新业务流程（与推送共用凭据解析与命令执行实现）。</summary>
    private readonly GitPullService _pull;

    /// <summary>当前拉取的取消令牌（【取消拉取】按钮 / 关闭主窗口时使用）。</summary>
    private CancellationTokenSource? _pullCts;

    /// <summary>应用设置来源（不再直接摸 GitLocator 的静态单例）。</summary>
    private readonly ISettingsProvider _settings;

    public MainWindow() : this(GitCliRunner.Default, SettingsProvider.Instance)
    {
    }

    /// <summary>可注入命令执行器（单元测试时传入假实现，无需真实 git 与 UI 运行时）。</summary>
    public MainWindow(IGitCliRunner cli) : this(cli, SettingsProvider.Instance)
    {
    }

    /// <summary>可注入命令执行器与设置来源（完全脱离真实磁盘设置，便于测试）。</summary>
    public MainWindow(IGitCliRunner cli, ISettingsProvider settings)
    {
        _cli = cli;
        _settings = settings;
        _identity = new GitIdentityService(cli);
        _repoLocator = new RepoLocator(cli);
        _logged = new GitCommandReporter(cli, Log);
        _commitPush = new CommitPushService(_logged, _identity, settings);
        _pull = new GitPullService(_logged, settings);
        _loading = true;
        InitializeComponent();
        Dialogs.ApplyIcon(this);
        _loading = false;

        // 关闭主窗口时中止仍在进行的拉取，避免 git 进程残留
        Closing += (_, _) => _pullCts?.Cancel();

        ListRemotes.ItemsSource = _remotes;

        _pathDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _pathDebounce.Tick += (_, _) =>
        {
            _pathDebounce.Stop();
            LoadConfigFromPath();
        };

        Log("MultiGitCommit 已启动（跨平台版：Windows / macOS）。");
        Log("提示：本工具不会修改 git 原生 .git/config；远端地址保存在每个项目目录下的 multigitcommit.config.json。");
        Log("提示：每个 git 仓库拥有独立配置，切换仓库会自动加载对应项目的远端列表。");

        // 解析 git 来源（内嵌 / 自动检测 / 系统 PATH / 指定路径），不依赖系统安装 git
        RefreshGitEnvironment();

        // 启动仓库定位：上次使用过的仓库 → 当前工作目录 → 程序目录向上逐级查找仓库
        string? startup = ResolveStartupRepoPath(out string startupMessage);
        TxtRepoPath.Text = startup ?? string.Empty;
        if (startupMessage.Length > 0) Log(startupMessage);
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

    // ==================== 外部命令执行 ====================
    // 进程细节（编码 / 非交互 / 超时杀进程树 / PATH 注入）全部在 IGitCliRunner 内，
    // 这里只做「调用 + 日志呈现」，业务代码不再直接接触 Process。

    /// <summary>同步执行快速校验类命令（输出量小），超时即终止。</summary>
    private GitCommandResult RunCommand(string workingDir, string fileName, string arguments)
        => _cli.Run(new GitCommandRequest
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            Timeout = GitTimeouts.Quick
        });

    /// <summary>异步执行命令，stdout/stderr 逐行实时输出到日志区；超时后终止进程并返回失败。</summary>
    private Task<GitCommandResult> RunCommandAsync(string workingDir, string fileName, string arguments,
        string? logLabel = null, TimeSpan? timeout = null)
        => _logged.RunAsync(workingDir, arguments, logLabel, timeout, fileName);

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
            _config = ProjectConfig.Load(repoRoot);
            if (_config == null)
            {
                Log($"读取项目配置失败：{repoRoot}");
                return;
            }

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
            _config.Save();
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
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "请选择 Git 仓库根目录（与 .git 文件夹同级）",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        string? path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

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

    /// <summary>打开“关于”窗口。</summary>
    private async void BtnAbout_Click(object? sender, RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
    }

    // ==================== 克隆项目 ====================

    /// <summary>
    /// 打开克隆窗口，按用户填写的参数执行 git clone；
    /// 成功后自动切换到该仓库目录、加载其独立配置，并按需把来源 URL 写入推送远端列表。
    /// </summary>
    private async void BtnClone_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureGitAvailable()) return;

        string initialDir = Directory.Exists(TxtRepoPath.Text?.Trim() ?? string.Empty)
            ? TxtRepoPath.Text!.Trim()
            : Environment.CurrentDirectory;

        var win = new CloneWindow(initialDir);
        if (await win.ShowDialog<bool?>(this) != true) return;

        string url = win.RepoUrl;
        string parentDir = win.ParentDir;
        string targetDir = win.TargetDir;

        BtnClone.IsEnabled = false;
        try
        {
            // url 与目标目录分别加引号，兼容其中包含空格的情况
            string args = CloneWindow.BuildCloneArgs(win.BranchName, win.Recursive, win.Shallow);
            string fullArgs = $"{args}\"{url}\" \"{targetDir}\"";

            Log($"========== 开始克隆仓库：{url} ==========");
            Log($"目标目录：{targetDir}");
            var clone = await RunCommandAsync(parentDir, GitLocator.GitExe, fullArgs, $"git {fullArgs}", GitTimeouts.Clone);
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
            if (win.AddToRemotes)
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

    /// <summary>打开 Git 环境设置窗口：选择 自动检测 / 系统 PATH / 指定路径 / 内嵌 Git。</summary>
    private async void BtnGitEnv_Click(object? sender, RoutedEventArgs e)
    {
        // 传入当前项目的远端 URL：验证账号时优先用同主机的真实远端做探测；
        // 传入仓库根目录：在设置窗口里可直接写入 / 读取仓库级提交身份
        var win = new GitSettingsWindow(_settings.Current, _config?.RemoteUrls, _repoRoot);
        if (await win.ShowDialog<bool?>(this) != true) return;

        _settings.Current.GitSource = win.SelectedSource;
        _settings.Current.CustomGitPath = win.SelectedCustomPath;
        _settings.Current.CredentialSource = win.SelectedCredentialSource;
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
    }

    /// <summary>按当前设置重新解析 git，更新状态栏显示并输出日志。</summary>
    private void RefreshGitEnvironment()
    {
        // git 变了，凭据助手的扫描结果也失效，必须重新扫描（否则会沿用旧 git 的助手列表）
        GitCredentialHelper.Invalidate();

        bool ok = GitLocator.Resolve();
        TxtGitStatus.Text = ok
            ? $"Git：{GitLocator.GitExePath}"
            : "Git：未找到（点击【Git 环境】配置）";
        ToolTip.SetTip(TxtGitStatus, ok
            ? $"当前生效：{GitLocator.Description}"
            : "未找到可用的 git，请点击【Git 环境】按钮配置来源。");

        if (ok)
        {
            Log($"Git 环境：{GitLocator.Description}");
        }
        else
        {
            Log("未找到可用的 git：请点击【Git 环境】按钮选择环境 git（系统 PATH）或指定路径 / 内嵌 Git。");
        }
    }

    /// <summary>确保 git 可用（已解析成功）；不可用时输出日志并返回 false。</summary>
    private bool EnsureGitAvailable()
    {
        if (GitLocator.GitExePath != null) return true;

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
            var init = await RunCommandAsync(path, GitLocator.GitExe, "init", "git init");
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

    /// <summary>
    /// 将仓库默认分支设置为指定名称：
    /// 1. 空仓库（尚无任何提交）：git symbolic-ref HEAD refs/heads/{branch}；
    /// 2. 已有提交：分支已存在则切换，不存在则新建并切换。
    /// </summary>
    private async Task SetDefaultBranchAsync(string repoDir, string branch)
    {
        var head = RunCommand(repoDir, GitLocator.GitExe, "rev-parse --verify HEAD");
        if (!head.Success)
        {
            string args = $"symbolic-ref HEAD refs/heads/{branch}";
            var r = await RunCommandAsync(repoDir, GitLocator.GitExe, args, $"git {args}");
            Log(r.Success ? $"已将默认分支设置为：{branch}" : $"设置默认分支失败：{branch}");
            return;
        }

        var exists = RunCommand(repoDir, GitLocator.GitExe, $"show-ref --verify --quiet refs/heads/{branch}");
        string checkoutArgs = exists.Success ? $"checkout {branch}" : $"checkout -b {branch}";
        var res = await RunCommandAsync(repoDir, GitLocator.GitExe, checkoutArgs, $"git {checkoutArgs}");
        Log(res.Success
            ? (exists.Success ? $"已切换到默认分支：{branch}" : $"已创建并切换到默认分支：{branch}")
            : $"创建默认分支失败：{branch}");
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

            if (!IsValidBranchName(text))
            {
                await Dialogs.ShowMessageAsync(this, title,
                    "分支名不合法：不能包含空格或 ~ ^ : ? * [ \\ .. // 等字符，且不能以 - / . 开头或结尾。");
                continue;
            }

            return text;
        }
    }

    /// <summary>校验分支名是否符合 git 引用命名基本规则。</summary>
    internal static bool IsValidBranchName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Any(char.IsWhiteSpace)) return false;
        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.')) return false;
        if (name.Contains("..") || name.Contains("//") || name.Contains("@{")) return false;
        if (name.IndexOfAny(new[] { '~', '^', ':', '?', '*', '[', '\\' }) >= 0) return false;
        return true;
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
        if (string.IsNullOrWhiteSpace(text)) { Log("远端 URL 不能为空。"); return; }
        if (_config.RemoteUrls.Contains(text)) { Log($"远端 URL 已存在：{text}"); return; }

        _config.RemoteUrls.Add(text);
        RefreshRemoteList();
        Log($"已添加远端：{text}");
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
        if (string.IsNullOrWhiteSpace(text)) { Log("远端 URL 不能为空。"); return; }

        _config.RemoteUrls[index] = text;
        RefreshRemoteList();
        Log($"已修改远端：{oldUrl} → {text}");
        SaveConfig();
    }

    /// <summary>在系统默认浏览器中打开选中的远端 URL。</summary>
    private void BtnOpenRemote_Click(object? sender, RoutedEventArgs e)
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
            // 跨平台：交给系统默认程序处理（Windows 走 ShellExecute，macOS 等价于 open，http(s) / ssh:// 均可）
            Process.Start(new ProcessStartInfo(selected) { UseShellExecute = true });
            Log($"已在浏览器中打开：{selected}");
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

        var win = new PullWindow(_repoRoot, remotes, branch);
        if (await win.ShowDialog<bool?>(this) != true) return;

        // 工作区有未提交改动时给出提示（不阻止：能否拉取最终由 git 判断）
        GitCommandResult status = await _logged.RunAsync(_repoRoot, "status --porcelain",
            "git status --porcelain", GitTimeouts.Quick);
        if (status.Success)
        {
            int changed = status.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            if (changed > 0)
            {
                bool keepGoing = await Dialogs.ConfirmAsync(this, "拉取更新",
                    $"当前工作区有 {changed} 项未提交的改动。" + Environment.NewLine +
                    "如果远端改动涉及这些文件，git 会拒绝拉取或产生冲突。" + Environment.NewLine + Environment.NewLine +
                    "仍要继续拉取吗？（建议先提交或暂存）");
                if (!keepGoing) return;
            }
        }

        _pullCts = new CancellationTokenSource();
        string idleText = BtnPull.Content?.ToString() ?? "拉取更新";
        BtnPull.Content = "取消拉取";

        try
        {
            PullResult result = await _pull.PullAsync(new PullRequest
            {
                RepoRoot = _repoRoot,
                RemoteUrl = win.SelectedRemoteUrl,
                Branch = branch,
                Strategy = win.SelectedStrategy
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

    /// <summary>打开提交历史窗口：浏览当前分支的历史提交，并可重置到指定提交。</summary>
    private async void BtnHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_repoRoot == null)
        {
            Log("请先选择有效的 git 仓库目录，再查看提交历史。");
            return;
        }

        if (!EnsureGitAvailable()) return;

        var win = new HistoryWindow(_repoRoot, _cli);
        await win.ShowDialog(this);

        // 窗口内可能执行过 reset：关闭后重新读取分支信息，保持界面与仓库一致
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
            GitCommandResult r;
            if (command.StartsWith("git ", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "git", StringComparison.OrdinalIgnoreCase))
            {
                // 以 git 开头的命令直接调用解析到的 git（支持内嵌 / 系统 / 指定路径）
                string gitArgs = command.Length > 4 ? command[4..] : string.Empty;
                if (GitLocator.GitExePath == null)
                {
                    Log("未找到可用的 git：请点击【Git 环境】按钮配置（自动检测 / 系统 PATH / 指定路径 / 内嵌 Git）。");
                    return;
                }

                r = await RunCommandAsync(workDir, GitLocator.GitExe, gitArgs, command);
            }
            else if (OperatingSystem.IsWindows())
            {
                // Windows：其他命令（dir 等）通过 cmd.exe 执行；
                // 先 chcp 65001 切到 UTF-8 代码页，否则中文输出乱码
                r = await RunCommandAsync(workDir, "cmd.exe", $"/c chcp 65001 >nul & {command}", command);
            }
            else
            {
                // macOS / Linux：其他命令通过登录 shell 执行，-l 保证能拿到 Homebrew 等 PATH 配置
                string shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
                string escaped = command.Replace("\"", "\\\"");
                r = await RunCommandAsync(workDir, shell, $"-lc \"{escaped}\"", command);
            }

            Log($"命令退出码：{r.ExitCode}");
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
    /// 决定启动时默认加载哪个仓库目录（依次尝试）：
    /// 1) 上次成功加载过的仓库（最符合"打开就看到上次项目"的预期）；
    /// 2) 当前工作目录（非程序自身目录时直接使用）；
    /// 3) 程序自身目录（bin\Debug\net10.0 等）时向上逐级查找含 .git / multigitcommit.config.json 的祖先目录，
    ///    使 IDE 调试或程序放在仓库内的场景也能自动加载配置；
    /// 4) 都没有则返回 null（由用户点【浏览】选择）。
    /// </summary>
    private string? ResolveStartupRepoPath(out string message)
    {
        string last = _settings.Current.LastRepoPath ?? string.Empty;
        if (last.Length > 0 && Directory.Exists(last))
        {
            message = $"已载入上次使用的仓库目录：{last}";
            return last;
        }

        string cwd = Environment.CurrentDirectory;
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string cwdFull = string.IsNullOrWhiteSpace(cwd)
            ? string.Empty
            : Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar);

        bool isAppDir = cwdFull.Length == 0 ||
                        string.Equals(cwdFull, baseDir, StringComparison.Ordinal) ||
                        cwdFull.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        if (!isAppDir)
        {
            message = string.Empty;
            return cwd;
        }

        // 程序自身目录不能当仓库（通常是被 .gitignore 忽略的 bin\ 输出目录），改为向上找真正的仓库
        string? found = RepoLocator.FindRepoAncestor(cwdFull.Length > 0 ? cwdFull : baseDir);
        if (found != null)
        {
            message = $"已从程序目录向上定位到 Git 仓库：{found}";
            return found;
        }

        message = "未自动填充仓库目录：当前目录及上级目录中未找到 Git 仓库，请点击【浏览…】选择 Git 仓库根目录。";
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
    // 读写细节在 GitIdentityService（与 Git 环境设置窗口共用），这里只负责界面呈现。

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
            var win = new IdentityWindow(saved);
            if (await win.ShowDialog<bool?>(this) != true)
            {
                Log("已取消设置提交身份，无法生成 commit。");
                return false;
            }

            identity = win.Result;
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

    /// <summary>手动打开【提交身份】设置窗口并立即应用。</summary>
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

        var win = new IdentityWindow(current);
        if (await win.ShowDialog<bool?>(this) != true) return;

        _settings.Current.CommitIdentity = win.Result;
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
        ApplyCommitIdentity(win.Result, dir);
    }
}
