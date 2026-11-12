using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MultiGitCommit;

/// <summary>
/// Git 环境设置视图：选择 git 来源（自动检测 / 系统 PATH / 指定路径 / 软件内嵌），
/// 让工具不依赖系统安装的 git，可使用软件自带或便携版 Git。
/// </summary>
public partial class GitSettingsView : DialogView<bool>
{
    /// <summary>
    /// 本视图自带 ScrollViewer + 固定底部按钮栏：移动端按覆盖层视口尺寸布局，
    /// 宽度受约束后长文案才会换行、底部按钮才不会被挤到滚动区最下方（桌面 Window 路径不受影响）。
    /// </summary>
    public override bool FitsOverlayViewport => true;

    /// <summary>用户点击确定后选定的来源模式。</summary>
    public GitSourceMode SelectedSource { get; private set; } = GitSourceMode.Auto;

    /// <summary>用户点击确定后选定的自定义路径（仅 Custom 模式有意义）。</summary>
    public string SelectedCustomPath { get; private set; } = string.Empty;

    /// <summary>用户点击确定后选定的凭据（账号）来源模式。</summary>
    public CredentialSourceMode SelectedCredentialSource { get; private set; } = CredentialSourceMode.Auto;

    /// <summary>用户点击确定后选定的「启动时加载的仓库」模式。</summary>
    public StartupRepoMode SelectedStartupRepo { get; private set; } = StartupRepoMode.CurrentDirectory;

    /// <summary>用户点击确定后选定的「执行 git add 的文件添加方式」。</summary>
    public StageMode SelectedStageMode { get; private set; } = StageMode.AddAll;

    /// <summary>传入的全局设置对象；点确定时把凭据写回它。</summary>
    private readonly AppSettings _settings;

    /// <summary>当前仓库根目录（设置提交身份时写入仓库级 config 用）。</summary>
    private readonly string? _repoPath;

    /// <summary>当前编辑中的凭据副本（点确定才写回设置，取消不影响）。</summary>
    private readonly List<GitCredential> _credentials;

    /// <summary>当前项目的远端 URL（验证账号时优先用同主机的真实远端做探测）。</summary>
    private readonly IReadOnlyList<string>? _remoteUrls;

    /// <summary>账号验证的取消令牌（【取消验证】按钮 / 关闭对话框时使用）。</summary>
    private CancellationTokenSource? _credTestCts;

    /// <summary>git 配置检测序号：快速切换选项时丢弃过期的检测结果。</summary>
    private int _testSeq;

    /// <summary>提交身份读写服务（与主视图共用组合根里的同一实例）。</summary>
    private readonly GitIdentityService _identityService;

    /// <summary>git 环境服务（来源预览、凭据助手缓存失效）。</summary>
    private readonly IGitEnvironmentService _environment;

    /// <summary>是否已触发打开后的自动检测（只执行一次）。</summary>
    private bool _initialTested;

    /// <summary>窄屏（移动端）布局阈值：视图宽度低于该值时凭据输入换两行（手机竖屏对话框卡片约 300）。</summary>
    private const double NarrowLayoutThreshold = 420;

    /// <summary>当前是否处于窄屏布局。</summary>
    private bool _isNarrowLayout;

    /// <param name="current">全局设置。</param>
    /// <param name="remoteUrls">当前项目的推送远端 URL（可选，验证账号时用同主机远端做真实探测）。</param>
    /// <param name="repoPath">当前仓库根目录（可选，用于读取/写入仓库级提交身份）。</param>
    /// <param name="services">调用方会话（复用同一套执行器与环境服务）；为 null 时取组合根默认值。</param>
    public GitSettingsView(AppSettings current, IEnumerable<string>? remoteUrls = null, string? repoPath = null,
        AppSession? services = null)
    {
        _identityService = services?.Identity ?? AppServices.Current.Identity;
        _environment = services?.GitEnvironment ?? AppServices.Current.GitEnvironment;
        _remoteUrls = remoteUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        _repoPath = string.IsNullOrWhiteSpace(repoPath) ? null : repoPath;
        _settings = current;
        _credentials = (current.Credentials ?? new List<GitCredential>())
            .Where(c => c != null)
            .Select(c => new GitCredential
            {
                Host = c.Host ?? string.Empty,
                Username = c.Username ?? string.Empty,
                Token = c.Token ?? string.Empty
            })
            .ToList();

        _suppress = true;
        InitializeComponent();
        _suppress = false;

        // 窄屏响应式：宽度变化时在「主机/用户名/令牌一行三个」与「令牌独占第二行」之间切换
        SizeChanged += (_, _) => ApplyResponsiveLayout();

        RbAuto.IsChecked = current.GitSource == GitSourceMode.Auto;
        RbEmbedded.IsChecked = current.GitSource == GitSourceMode.Embedded;
        RbSystem.IsChecked = current.GitSource == GitSourceMode.System;
        RbCustom.IsChecked = current.GitSource == GitSourceMode.Custom;
        TxtCustomPath.Text = current.CustomGitPath ?? string.Empty;

        // 关闭对话框（确认关闭或宿主直接关闭）时中止未完成的账号验证，避免后台 git 进程继续空跑
        CloseRequested += (_, _) => _credTestCts?.Cancel();
        HostClosed += (_, _) => _credTestCts?.Cancel();

        // 凭据（账号）来源：与 git 来源一样可由用户选择
        RbCredAuto.IsChecked = current.CredentialSource == CredentialSourceMode.Auto;
        RbCredSystem.IsChecked = current.CredentialSource == CredentialSourceMode.System;
        RbCredCustom.IsChecked = current.CredentialSource == CredentialSourceMode.Custom;
        RbCredNone.IsChecked = current.CredentialSource == CredentialSourceMode.None;

        // 启动时加载的仓库：默认「当前目录优先」，用户可在设置里改为「上次使用的仓库优先」
        RbStartupCurrent.IsChecked = current.StartupRepo == StartupRepoMode.CurrentDirectory;
        RbStartupLast.IsChecked = current.StartupRepo == StartupRepoMode.LastUsed;
        RbStartupFallback.IsChecked = current.StartupRepo == StartupRepoMode.CurrentDirectoryThenLastUsed;
        if (RbStartupCurrent.IsChecked != true && RbStartupLast.IsChecked != true &&
            RbStartupFallback.IsChecked != true)
            RbStartupCurrent.IsChecked = true;
        RefreshStartupHint();

        // 执行 git add 的文件添加方式：默认「全提交」（git add .），也可改为「可选提交」（提交前勾选文件）
        RbStageAll.IsChecked = current.StageMode != StageMode.Select;
        RbStageSelect.IsChecked = current.StageMode == StageMode.Select;
        RefreshStageHint();

        // 兜底：配置值异常（或历史脏数据）时保证两组都各有一项被选中
        if (RbAuto.IsChecked != true && RbEmbedded.IsChecked != true &&
            RbSystem.IsChecked != true && RbCustom.IsChecked != true)
            RbAuto.IsChecked = true;

        if (RbCredAuto.IsChecked != true && RbCredSystem.IsChecked != true &&
            RbCredCustom.IsChecked != true && RbCredNone.IsChecked != true)
            RbCredAuto.IsChecked = true;

        if (RbStageAll.IsChecked != true && RbStageSelect.IsChecked != true)
            RbStageAll.IsChecked = true;

        RefreshCredList();
        UpdateEnabled();

        // 默认“自定义”：只显示说明，不改变用户已填内容
        ApplyPlatform(string.Empty);

        // 挂到可视树后再执行检测：git 调用全部放到线程池，界面（含【取消】按钮）始终可响应
        AttachedToVisualTree += async (_, _) =>
        {
            if (_initialTested) return;
            _initialTested = true;
            try
            {
                await RefreshIdentityInfoAsync();
                await RefreshHelperInfoAsync();
                await TestAsync();
            }
            catch (Exception ex)
            {
                // async void 事件处理器异常会终止进程，这里兜底显示为普通提示
                ShowResult("× 检测 git 配置失败：" + ex.Message, false);
            }
        };
    }

    // ==================== 平台预设 ====================

    /// <summary>“自定义”在下拉中的索引（未知主机 / 手动填写时使用）。</summary>
    private const int CustomPlatformIndex = 3;

    /// <summary>平台预设：主机名、固定用户名、访问令牌页面、填写说明（规则来自各平台官方文档）。</summary>
    private sealed record PlatformPreset(string Host, string? FixedUser, string TokenUrl, string Hint);

    private static readonly Dictionary<string, PlatformPreset> Platforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gitee.com"] = new("gitee.com", null,
            "https://gitee.com/profile/personal_access_tokens",
            "Gitee：用户名填 Gitee 账号名，密码填「私人令牌」（勾选 projects 权限）。令牌页面：gitee.com/profile/personal_access_tokens"),
        ["github.com"] = new("github.com", null,
            "https://github.com/settings/tokens",
            "GitHub：用户名填 GitHub 账号名，密码填 Personal Access Token（勾选 repo 权限）。令牌页面：github.com/settings/tokens"),
        ["cnb.cool"] = new("cnb.cool", "cnb",
            "https://cnb.cool/profile/token",
            "CNB（cnb.cool）：用户名固定填 cnb，密码填访问令牌；CNB 只支持 HTTPS + 令牌，不支持 ssh。令牌页面：cnb.cool/profile/token")
    };

    /// <summary>选择平台：自动填主机名，CNB 额外固定用户名，并显示该平台的令牌说明。</summary>
    private void CmbPlatform_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        ApplyPlatform(SelectedPlatformHost);
    }

    /// <summary>当前下拉对应的主机名（“自定义”为空字符串）。</summary>
    private string SelectedPlatformHost =>
        (CmbPlatform.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    /// <summary>套用平台预设；hostTag 为空表示自定义，只显示说明、不改动已填内容。</summary>
    private void ApplyPlatform(string hostTag)
    {
        if (hostTag.Length == 0 || !Platforms.TryGetValue(hostTag, out PlatformPreset? preset))
        {
            TxtPlatformHint.Text = "自定义：手动填写主机名（可直接粘贴完整 URL，如 https://git.example.com/group/repo）。";
            BtnPlatformToken.IsEnabled = false;
            return;
        }

        TxtCredHost.Text = preset.Host;
        if (!string.IsNullOrEmpty(preset.FixedUser))
            TxtCredUser.Text = preset.FixedUser;

        TxtPlatformHint.Text = preset.Hint;
        BtnPlatformToken.IsEnabled = true;
    }

    /// <summary>按主机名反查平台，让下拉与选中账号保持一致（未知主机落到“自定义”）。</summary>
    private void SyncPlatformFromHost(string? host)
    {
        string key = GitCredentials.NormalizeHost(host);

        foreach (object? item in CmbPlatform.Items)
        {
            if (item is ComboBoxItem cbi &&
                string.Equals(cbi.Tag?.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                CmbPlatform.SelectedItem = cbi;
                return;
            }
        }

        CmbPlatform.SelectedIndex = CustomPlatformIndex;
        ApplyPlatform(string.Empty);
    }

    /// <summary>打开当前平台的访问令牌创建页面（TopLevel.Launcher 跨平台，移动端也可用）。</summary>
    private async void BtnPlatformToken_Click(object? sender, RoutedEventArgs e)
    {
        if (!Platforms.TryGetValue(SelectedPlatformHost, out PlatformPreset? preset))
        {
            ShowCredResult("× 当前平台没有固定的令牌页面，请手动在该平台的设置中创建访问令牌。", false);
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null &&
                await topLevel.Launcher.LaunchUriAsync(new Uri(preset.TokenUrl)))
            {
                ShowCredResult($"已在浏览器中打开 {preset.Host} 的访问令牌页面。", null);
            }
            else
            {
                ShowCredResult($"× 未能打开浏览器，请手动访问：{preset.TokenUrl}", false);
            }
        }
        catch (Exception ex)
        {
            ShowCredResult("× 打开浏览器失败：" + ex.Message, false);
        }
    }

    /// <summary>CNB 的用户名固定为 cnb（访问令牌才是密码），这里强制纠正并回填输入框。</summary>
    private string NormalizePlatformUser(string host, string user)
    {
        if (string.Equals(host, "cnb.cool", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(user, "cnb", StringComparison.OrdinalIgnoreCase))
        {
            TxtCredUser.Text = "cnb";
            return "cnb";
        }

        return user;
    }

    // ==================== 推送凭据 ====================

    /// <summary>刷新凭据列表显示（ListBox 直接显示 GitCredential.ToString()）。</summary>
    private void RefreshCredList()
    {
        ListCred.ItemsSource = null;
        ListCred.ItemsSource = _credentials;
    }

    /// <summary>选中某条凭据时填入输入框，便于修改。</summary>
    private void ListCred_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ListCred.SelectedItem is not GitCredential cred) return;

        TxtCredHost.Text = cred.Host;
        TxtCredUser.Text = cred.Username;
        TxtCredToken.Text = cred.Token;

        // 让平台下拉与选中账号保持一致
        SyncPlatformFromHost(cred.Host);
    }

    /// <summary>
    /// 添加或更新一条凭据：先用一次 git ls-remote 验证账号是否可用（有超时、可取消），
    /// 验证不通过时让用户确认是否仍然保存，避免保存一个推送必然失败的账号。
    /// </summary>
    private async void BtnCredAdd_Click(object? sender, RoutedEventArgs e)
    {
        // 允许直接粘贴完整 URL / 带引号的地址，这里统一规范化为纯主机名
        string host = GitCredentials.NormalizeHost(TxtCredHost.Text);
        string user = NormalizePlatformUser(host, TxtCredUser.Text?.Trim() ?? string.Empty);
        string token = TxtCredToken.Text?.Trim() ?? string.Empty;

        if (host.Length == 0)
        {
            ShowCredResult("× 请先填写远端主机（如 cnb.cool），或直接粘贴完整 URL。", false);
            return;
        }

        TxtCredHost.Text = host; // 回填清洗后的主机名，避免引号 / 路径混入配置

        if (user.Length == 0 || token.Length == 0)
        {
            ShowCredResult("× 请同时填写用户名与访问令牌。", false);
            return;
        }

        var result = await VerifyCredentialAsync(host, user, token);
        if (result == null) return; // 无可用 git / 用户取消：不写入

        if (!result.Success)
        {
            string reason = string.IsNullOrWhiteSpace(result.Detail)
                ? result.Message
                : result.Message + "\n\n服务端返回：" + result.Detail;

            bool keep = await Dialogs.ConfirmAsync(this, "账号验证",
                reason + "\n\n该账号未验证通过，仍要保存吗？（未验证的账号推送时很可能失败）");
            if (!keep)
            {
                ShowCredResult("已放弃保存，该账号未写入列表。", null);
                return;
            }
        }

        SaveCredential(host, user, token);
        ShowCredResult((result.Success ? "√ 验证通过：" : "! 未验证通过但已保存：") +
                       $" {host} 的推送凭据（点【确定】后生效）。", result.Success);
    }

    /// <summary>按主机查找编辑中的凭据（主机归一规则只在这里写一次）。</summary>
    private GitCredential? FindCredential(string host)
    {
        string hostKey = GitCredentials.GetHost(host) ?? host.Trim().ToLowerInvariant();
        return _credentials.FirstOrDefault(c =>
            string.Equals(GitCredentials.GetHost(c.Host) ?? c.Host.Trim().ToLowerInvariant(),
                hostKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>写入（或更新同主机的）一条凭据并刷新列表。</summary>
    private void SaveCredential(string host, string user, string token)
    {
        var existing = FindCredential(host);

        if (existing != null)
        {
            existing.Host = host;
            existing.Username = user;
            existing.Token = token;
        }
        else
        {
            _credentials.Add(new GitCredential { Host = host, Username = user, Token = token });
        }

        RefreshCredList();
    }

    /// <summary>
    /// 【验证账号】：用一次 git ls-remote 实测输入框中的账号能否通过服务端认证。
    /// 验证进行中再点一次该按钮即中止（按钮文字会变为【取消验证】）。
    /// </summary>
    private async void BtnCredTest_Click(object? sender, RoutedEventArgs e)
    {
        if (_credTestCts != null)
        {
            _credTestCts.Cancel(); // 正在验证：再点一次就是中止
            return;
        }

        string host = GitCredentials.NormalizeHost(TxtCredHost.Text);
        string user = NormalizePlatformUser(host, TxtCredUser.Text?.Trim() ?? string.Empty);
        string token = TxtCredToken.Text?.Trim() ?? string.Empty;

        if (host.Length == 0)
        {
            ShowCredResult("× 请先填写远端主机（如 cnb.cool），或直接粘贴完整 URL。", false);
            return;
        }

        if (user.Length == 0 || token.Length == 0)
        {
            ShowCredResult("× 请同时填写用户名与访问令牌。", false);
            return;
        }

        TxtCredHost.Text = host;
        await VerifyCredentialAsync(host, user, token);
    }

    /// <summary>
    /// 实测账号是否可用：全程异步、默认 20 秒超时、可随时取消，绝不会卡住界面。
    /// 返回 null 表示未执行（没有可用的 git）。
    /// </summary>
    private async Task<CredentialTestResult?> VerifyCredentialAsync(string host, string user, string token)
    {
        // 按对话框里当前选择的 git 来源实时解析，未点【确定】也能用刚选的 git 验证
        string? exe = _environment.Preview(CurrentMode, TxtCustomPath.Text?.Trim() ?? string.Empty, out _);
        if (exe == null)
        {
            ShowCredResult("× 当前 git 来源下未找到可用的 git，请先配置 git 来源再验证账号。", false);
            return null;
        }

        _credTestCts?.Dispose();
        _credTestCts = new CancellationTokenSource();
        var ct = _credTestCts.Token;

        BtnCredTest.Content = "取消验证";
        BtnCredAdd.IsEnabled = false;
        ShowCredResult($"正在验证 {user}@{host}（git ls-remote 实测，最多 {CredentialTester.DefaultTimeoutSeconds} 秒，可点【取消验证】中止）…", null);

        try
        {
            var result = await CredentialTester.TestAsync(
                exe,
                new GitCredential { Host = host, Username = user, Token = token },
                _remoteUrls,
                ct);

            ShowCredResult((result.Success ? "√ " : "× ") + result.Message, result.Success);
            return result;
        }
        catch (Exception ex)
        {
            ShowCredResult("× 验证账号失败：" + ex.Message, false);
            return null;
        }
        finally
        {
            BtnCredTest.Content = "验证账号";
            BtnCredAdd.IsEnabled = true;
            _credTestCts?.Dispose();
            _credTestCts = null;
        }
    }

    /// <summary>
    /// 把 Git 配置测试结果写入 TxtResult（true 成功 / false 失败 / null 中性提示）。
    /// 只服务于「Git 来源」这一组，账号 / 身份的结果各有独立文本，互不覆盖。
    /// </summary>
    private void ShowResult(string message, bool? ok)
    {
        TxtResult.Text = message;
        TxtResult.Foreground = ok == true
            ? Brush.Parse("#1E8E3E")
            : ok == false
                ? Brush.Parse("#B00020")
                : Brush.Parse("#666666");
    }

    /// <summary>把账号相关提示写入账号区的 TxtCredResult（与 git 配置测试结果分开）。</summary>
    private void ShowCredResult(string message, bool? ok)
    {
        TxtCredResult.Text = message;
        TxtCredResult.Foreground = ok == true
            ? Brush.Parse("#1E8E3E")
            : ok == false
                ? Brush.Parse("#B00020")
                : Brush.Parse("#666666");
    }

    /// <summary>把提交身份相关提示写入身份区的 TxtIdentityResult（与 git 配置测试结果分开）。</summary>
    private void ShowIdentityResult(string message, bool? ok)
    {
        TxtIdentityResult.Text = message;
        TxtIdentityResult.Foreground = ok == true
            ? Brush.Parse("#1E8E3E")
            : ok == false
                ? Brush.Parse("#B00020")
                : Brush.Parse("#666666");
    }

    /// <summary>删除选中的凭据。</summary>
    private void BtnCredDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (ListCred.SelectedItem is not GitCredential cred)
        {
            ShowCredResult("× 请先在上方列表中选中要删除的凭据。", false);
            return;
        }

        _credentials.Remove(cred);
        RefreshCredList();
        TxtCredHost.Text = string.Empty;
        TxtCredUser.Text = string.Empty;
        TxtCredToken.Text = string.Empty;
        ShowCredResult($"已删除 {cred.Host} 的凭据（点【确定】后生效）。", null);
    }

    /// <summary>InitializeComponent / 初始化过程中抑制事件。</summary>
    private readonly bool _suppress;

    private GitSourceMode CurrentMode => RbAuto.IsChecked == true
        ? GitSourceMode.Auto
        : RbEmbedded.IsChecked == true
            ? GitSourceMode.Embedded
            : RbSystem.IsChecked == true
                ? GitSourceMode.System
                : GitSourceMode.Custom;

    /// <summary>切换模式时同步自定义路径输入区可用状态。</summary>
    private async void Mode_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        UpdateEnabled();
        await TestAsync();
    }

    private void UpdateEnabled()
    {
        bool custom = RbCustom.IsChecked == true;
        TxtCustomPath.IsEnabled = custom;
        BtnBrowse.IsEnabled = custom;
        if (custom && !_suppress) TxtCustomPath.Focus();
    }

    /// <summary>浏览选择 git 可执行文件。</summary>
    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 git 可执行文件（macOS 常在 /opt/homebrew/bin/git，Linux 常在 /usr/bin/git）",
            AllowMultiple = false
        });

        if (files.Count == 0) return;

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        TxtCustomPath.Text = path;
        await TestAsync();
    }

    /// <summary>测试当前所选配置能否找到并执行 git。</summary>
    private async void BtnTest_Click(object? sender, RoutedEventArgs e)
    {
        await TestAsync();
    }

    /// <summary>
    /// 按当前对话框选择实时解析并显示结果。
    /// git --version 是外部进程调用，放到线程池执行，避免界面卡住（点【取消】无响应）。
    /// </summary>
    private async Task TestAsync()
    {
        var mode = CurrentMode;
        string custom = TxtCustomPath.Text?.Trim() ?? string.Empty;

        if (mode == GitSourceMode.Custom && string.IsNullOrWhiteSpace(custom))
        {
            TxtResult.Text = "× 请先填写 git 路径或 Git 根目录。";
            TxtResult.Foreground = Brush.Parse("#B00020");
            return;
        }

        int seq = ++_testSeq;
        TxtResult.Text = "正在检测当前所选 git 配置…";
        TxtResult.Foreground = Brush.Parse("#666666");

        var (exe, description) = await Task.Run(() =>
        {
            string? found = _environment.Preview(mode, custom, out string desc);
            return (found, desc);
        });

        // 快速切换选项时只显示最后一次的结果，避免旧结果覆盖新结果
        if (seq != _testSeq) return;

        TxtResult.Text = (exe != null ? "√ " : "× ") + description;
        TxtResult.Foreground = exe != null
            ? Brush.Parse("#1E8E3E")
            : Brush.Parse("#B00020");
    }

    // ==================== 启动时加载的仓库 ====================

    /// <summary>当前选中的「启动时加载的仓库」模式（未选中时按默认的只读取当前目录）。</summary>
    private StartupRepoMode CurrentStartupRepo =>
        RbStartupLast.IsChecked == true
            ? StartupRepoMode.LastUsed
            : RbStartupFallback.IsChecked == true
                ? StartupRepoMode.CurrentDirectoryThenLastUsed
                : StartupRepoMode.CurrentDirectory;

    /// <summary>切换启动来源时刷新说明文字（不执行任何目录扫描 / git 调用，避免卡顿）。</summary>
    private void StartupMode_Changed(object? sender, RoutedEventArgs e) => RefreshStartupHint();

    /// <summary>显示当前目录，并给出所选模式的生效方式与回退规则。</summary>
    private void RefreshStartupHint()
    {
        string cwd = Environment.CurrentDirectory;
        TxtStartupHint.Text = CurrentStartupRepo switch
        {
            StartupRepoMode.LastUsed =>
                $"启动时直接打开上次成功加载的仓库（不看当前目录）。当前目录：{cwd}",
            StartupRepoMode.CurrentDirectoryThenLastUsed =>
                $"启动时先看当前目录及其上级目录中的 .git / {ProjectConfig.ConfigFileName}，" +
                $"找不到仓库再回退到上次使用的仓库。当前目录：{cwd}",
            _ =>
                $"启动时只读取当前目录及其上级目录中的 .git / {ProjectConfig.ConfigFileName}；" +
                $"找不到仓库时不自动加载上次项目，留空由你手选。当前目录：{cwd}"
        };
    }

    // ==================== 执行 git add 的文件添加方式 ====================

    /// <summary>当前选中的添加方式（两项单选，未选中时按默认的全提交）。</summary>
    private StageMode CurrentStageMode =>
        RbStageSelect.IsChecked == true ? StageMode.Select : StageMode.AddAll;

    /// <summary>切换添加方式时刷新说明文字（不做任何 git 调用，不会卡顿）。</summary>
    private void StageMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        RefreshStageHint();
    }

    /// <summary>说明当前方式在主流程里的实际行为。</summary>
    private void RefreshStageHint()
    {
        TxtStageHint.Text = CurrentStageMode == StageMode.Select
            ? "可选提交：主界面勾选【执行 git add】后，点提交会先弹出文件列表，勾选的文件才会被添加（默认全部勾选，可取消）。"
            : "全提交：主界面勾选【执行 git add】后，提交前直接执行 git add .，添加工作区全部变更（与旧版行为一致）。";
    }

    // ==================== 凭据（账号）来源 ====================

    /// <summary>当前选中的凭据（账号）来源模式。</summary>
    private CredentialSourceMode CurrentCredentialSource => RbCredAuto.IsChecked == true
        ? CredentialSourceMode.Auto
        : RbCredSystem.IsChecked == true
            ? CredentialSourceMode.System
            : RbCredCustom.IsChecked == true
                ? CredentialSourceMode.Custom
                : CredentialSourceMode.None;

    /// <summary>切换凭据来源时给出对应说明（不执行任何检测，避免卡顿）。</summary>
    private void CredMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;

        switch (CurrentCredentialSource)
        {
            case CredentialSourceMode.System:
                ShowCredResult("将只使用系统凭据：每次推送时实时索取，令牌不写入配置文件。", null);
                break;
            case CredentialSourceMode.Custom:
                ShowCredResult("将只使用下方自定义账号：不会访问系统凭据库。", null);
                break;
            case CredentialSourceMode.None:
                ShowCredResult("将不使用任何账号：适用于 ssh 远端或 URL 中自带凭据的情况。", null);
                break;
            default:
                ShowCredResult("自动检测：优先使用已保存的账号，找不到时自动从系统凭据获取。", null);
                break;
        }
    }

    /// <summary>
    /// 打开【检测系统凭据】对话框：由用户自行选择要检测的主机后再执行检测（可随时停止、有进度），
    /// 勾选结果导入后与自定义填写的凭据一样保存在凭据列表中。
    /// </summary>
    private async void BtnCredDetect_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_environment.Preview(CurrentMode, TxtCustomPath.Text?.Trim() ?? string.Empty, out _) == null)
            {
                ShowCredResult("× 当前 git 来源未找到可用的 git，请先配置 git 来源再检测系统凭据。", false);
                return;
            }

            var detectView = new CredentialDetectView(_remoteUrls, _environment);
            if (await UiDialogs.ShowAsync<bool?>(this, detectView, "检测系统 git 凭据") != true)
            {
                ShowCredResult("已取消，未导入任何系统账号。", null);
                return;
            }

            var found = detectView.SelectedCredentials;
            if (found.Count == 0)
            {
                ShowCredResult("未勾选任何账号，未导入。", null);
                return;
            }

            int imported = 0;
            foreach (var cred in found)
            {
                var existing = FindCredential(cred.Host);

                if (existing != null)
                {
                    // 同一主机只保留一条，导入的系统凭据覆盖原有
                    existing.Host = cred.Host;
                    existing.Username = cred.Username;
                    existing.Token = cred.Token;
                }
                else
                {
                    _credentials.Add(new GitCredential
                    {
                        Host = cred.Host,
                        Username = cred.Username,
                        Token = cred.Token
                    });
                }

                imported++;
            }

            RefreshCredList();
            ShowCredResult($"√ 已从系统导入 {imported} 个账号（点【确定】后生效）。", true);
        }
        catch (Exception ex)
        {
            ShowCredResult("× 检测系统凭据失败：" + ex.Message, false);
        }
    }

    // ==================== 凭据助手（credential.helper） ====================

    /// <summary>
    /// 显示当前 git 的凭据助手状态：配置值是否可用，不可用时会自动改用哪个助手。
    /// 这是"推送时取不到系统账号"最常见的根因（精简便携版 git 缺少 git-credential-manager）。
    /// 扫描目录与读取 git config 都放到线程池，避免打开对话框时界面卡住。
    /// </summary>
    private async Task RefreshHelperInfoAsync()
    {
        _environment.InvalidateCredentialHelpers();

        var (text, usable) = await Task.Run(() =>
        {
            string desc = GitCredentialHelper.Describe();
            return (desc, GitCredentialHelper.ConfiguredUsable());
        });

        TxtHelperInfo.Text = text;
        TxtHelperInfo.Foreground = usable
            ? Brush.Parse("#1E8E3E")
            : Brush.Parse("#B00020");
    }

    // ==================== 提交身份（user.name / user.email） ====================

    /// <summary>
    /// 显示当前生效的提交身份（读取 git config --get，取仓库级 → 全局的生效值）。
    /// 读取 git config 会启动外部进程，放到线程池执行，避免界面卡住。
    /// </summary>
    private async Task RefreshIdentityInfoAsync()
    {
        var saved = _settings.CommitIdentity;
        string savedText = saved != null && saved.IsComplete ? $"已保存：{saved}" : "尚未保存";

        var (name, email) = await Task.Run(() => _identityService.Read(_repoPath));
        TxtIdentityInfo.Text = (name.Length > 0 && email.Length > 0)
            ? $"当前生效：{name} <{email}>（{savedText}）"
            : $"当前生效：未设置（{savedText}）";

        TxtIdentityInfo.Foreground = (name.Length > 0 && email.Length > 0)
            ? Brush.Parse("#1E8E3E")
            : Brush.Parse("#B00020");
    }

    /// <summary>打开【提交身份】对话框并写入 git config（仓库级或全局）。</summary>
    private async void BtnIdentity_Click(object? sender, RoutedEventArgs e)
    {
        var (name, email) = _identityService.Read(_repoPath);
        var current = new GitIdentity
        {
            Name = name,
            Email = email,
            Scope = _settings.CommitIdentity?.Scope ?? IdentityScope.Local
        };

        var identityView = new IdentityView(current);
        if (await UiDialogs.ShowAsync<bool?>(this, identityView, "提交身份（git user.name / user.email）") != true) return;

        var identity = identityView.SelectedIdentity;
        if (!_identityService.TryApply(identity, _repoPath, out string error))
        {
            string detail = error.Length > 0 ? $"{Environment.NewLine}{error}" : string.Empty;
            ShowIdentityResult("× 写入提交身份失败，请确认 git 可用且该仓库目录可写。" + detail, false);
            return;
        }

        _settings.CommitIdentity = identity;
        await RefreshIdentityInfoAsync();
        ShowIdentityResult($"√ 已写入提交身份（{(identity.Scope == IdentityScope.Global ? "全局 --global" : "本仓库 --local")}）：" +
                           $"{identity.Name} <{identity.Email}>", true);
    }

    /// <summary>
    /// 【取消】：中止可能仍在进行的验证，然后关闭对话框（返回 false，调用方不会保存设置）。
    /// Avalonia 的 IsCancel 只是把 Esc 键映射成"点击该按钮"，按钮本身不关窗，必须显式处理 Click。
    /// </summary>
    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        _credTestCts?.Cancel();
        Close(false);
    }

    private async void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        SelectedSource = CurrentMode;
        SelectedCustomPath = TxtCustomPath.Text?.Trim() ?? string.Empty;

        if (SelectedSource == GitSourceMode.Custom)
        {
            if (string.IsNullOrWhiteSpace(SelectedCustomPath))
            {
                await Dialogs.ShowMessageAsync(this, "Git 环境设置", "请填写 git 路径或 Git 根目录。");
                return;
            }

            // 路径无效时给出确认，避免静默保存一个用不了的配置
            if (_environment.ResolveCustomPath(SelectedCustomPath) == null)
            {
                bool keep = await Dialogs.ConfirmAsync(this, "Git 环境设置",
                    "指定路径中未找到可用的 git，仍保存该设置吗？");
                if (!keep) return;
            }
        }

        // 凭据（账号）与来源模式写回设置对象（调用方在点确定后统一保存；点取消不会落盘）
        _settings.Credentials = _credentials;
        _settings.CredentialSource = SelectedCredentialSource = CurrentCredentialSource;

        // 启动时加载的仓库：同样在点确定后由调用方统一保存
        SelectedStartupRepo = CurrentStartupRepo;

        // git add 的文件添加方式：全提交 / 可选提交
        SelectedStageMode = CurrentStageMode;
        _settings.StageMode = SelectedStageMode;

        Close(true);
    }

    // ==================== 窄屏（移动端）响应式布局 ====================

    /// <summary>
    /// 窄屏适配（视图宽度变化时调用）：
    /// 宽度低于阈值（手机竖屏对话框卡片）时，把凭据输入的「令牌」输入框从第一行第三列
    /// 换到第二行并横跨整行（主机/用户名均分第一行），避免三个输入框挤成一团无法输入；
    /// 恢复宽屏（桌面）时回到「主机 150 / 用户名 110 / 令牌占满剩余」的单行布局。
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        double width = Bounds.Width;

        // 身份信息是运行时拼出来的长文本（当前生效：xxx <xxx>（已保存：…）），
        // 窄屏下限制最大宽度让它换行，否则会把整行连同按钮一起撑出视口、右侧被裁切。
        if (width > 0)
            TxtIdentityInfo.MaxWidth = width < NarrowLayoutThreshold
                ? System.Math.Max(120, width - 60)
                // MaxWidth 只接受有限值或 +∞（默认值）；设 NaN 会抛 ArgumentException，
                // 且异常发生在布局的 SizeChanged 事件里会直接终止进程（点击【Git 环境】闪退）
                : double.PositiveInfinity;

        bool narrow = width > 0 && width < NarrowLayoutThreshold;
        if (narrow == _isNarrowLayout)
            return;

        _isNarrowLayout = narrow;
        var columns = CredInputGrid.ColumnDefinitions;
        if (narrow)
        {
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[1].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(TxtCredToken, 1);
            Grid.SetColumn(TxtCredToken, 0);
            Grid.SetColumnSpan(TxtCredToken, 3);
            TxtCredToken.Margin = new Avalonia.Thickness(0, 6, 0, 0);
        }
        else
        {
            columns[0].Width = new GridLength(150);
            columns[1].Width = new GridLength(110);
            Grid.SetRow(TxtCredToken, 0);
            Grid.SetColumn(TxtCredToken, 2);
            Grid.SetColumnSpan(TxtCredToken, 1);
            TxtCredToken.Margin = new Avalonia.Thickness(6, 0, 0, 0);
        }
    }
}
