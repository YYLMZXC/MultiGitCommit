using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MultiGitCommit;

/// <summary>主机选择列表的一行：主机 + 线索来源 + 是否检测。</summary>
public class HostSelectItem
{
    /// <summary>主机。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>线索来源说明。</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>是否检测该主机。</summary>
    public bool Selected { get; set; }
}

/// <summary>
/// 系统凭据检测窗口：先让用户【选择要检测的主机】→ 再执行检测（可随时停止、有进度）→ 最后勾选导入。
///
/// 这样设计的目的是避免"点一下就全量扫描所有平台"导致长时间无响应：
/// 默认只勾选有明确线索的主机（当前项目远端 / 凭据管理器 / 凭据文件），
/// 常用平台列表默认不勾选，由用户自己决定要不要检测。
/// </summary>
public partial class CredentialDetectWindow : Window
{
    /// <summary>可检测的主机列表。</summary>
    private readonly List<HostSelectItem> _hosts;

    /// <summary>检测到的账号。</summary>
    private readonly List<CredentialImportItem> _results = new();

    /// <summary>检测取消令牌（【停止】按钮使用）。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>用户勾选并确认导入的凭据。</summary>
    public IReadOnlyList<DetectedCredential> SelectedCredentials =>
        _results.Where(i => i.Selected).Select(i => i.Credential).ToList();

    /// <param name="remoteUrls">当前项目的推送远端 URL（作为最优先的检测线索）。</param>
    public CredentialDetectWindow(IEnumerable<string>? remoteUrls)
    {
        _hosts = SystemCredentialProbe.CandidateHosts(remoteUrls)
            .Select(c => new HostSelectItem { Host = c.Host, Origin = c.Origin, Selected = c.Preselected })
            .ToList();

        InitializeComponent();
        Dialogs.ApplyIcon(this);
        ListHosts.ItemsSource = _hosts;
        ListResult.ItemsSource = _results;
    }

    // ==================== 主机列表 ====================

    /// <summary>手动添加一个要检测的主机。</summary>
    private void BtnAddHost_Click(object? sender, RoutedEventArgs e)
    {
        string input = TxtHost.Text?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            ShowStatus("请先填写要检测的主机（如 gitee.com）。", false);
            return;
        }

        string? host = GitCredentials.GetHost(input) ?? input.ToLowerInvariant();
        if (string.IsNullOrEmpty(host))
        {
            ShowStatus("主机名无效，请检查输入。", false);
            return;
        }

        var existing = _hosts.FirstOrDefault(h => string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Selected = true;
        }
        else
        {
            _hosts.Add(new HostSelectItem { Host = host, Origin = "手动添加", Selected = true });
        }

        TxtHost.Text = string.Empty;
        RefreshHosts();
        ShowStatus($"已添加主机：{host}", null);
    }

    private void BtnSelectAll_Click(object? sender, RoutedEventArgs e) => SetAllHosts(true);

    private void BtnSelectNone_Click(object? sender, RoutedEventArgs e) => SetAllHosts(false);

    private void SetAllHosts(bool selected)
    {
        foreach (var h in _hosts) h.Selected = selected;
        RefreshHosts();
    }

    private void RefreshHosts()
    {
        ListHosts.ItemsSource = null;
        ListHosts.ItemsSource = _hosts;
    }

    private void RefreshResults()
    {
        ListResult.ItemsSource = null;
        ListResult.ItemsSource = _results;
    }

    // ==================== 检测 ====================

    /// <summary>按勾选的主机逐个检测；整体可【停止】，每个主机由探测自身 5 秒超时兜底。</summary>
    private async void BtnDetect_Click(object? sender, RoutedEventArgs e)
    {
        var targets = _hosts
            .Where(h => h.Selected && !string.IsNullOrWhiteSpace(h.Host))
            .Select(h => h.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            ShowStatus("请至少勾选一个要检测的主机。", false);
            return;
        }

        if (GitLocator.GitExePath == null)
        {
            ShowStatus("未找到可用的 git，请先在 Git 环境设置里配置 git 来源。", false);
            return;
        }

        _results.Clear();
        RefreshResults();

        BtnDetect.IsEnabled = false;
        BtnStop.IsEnabled = true;
        Progress.Minimum = 0;
        Progress.Maximum = targets.Count;
        Progress.Value = 0;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int found = 0;

        // 配置的 credential.helper 在当前 git 中不可用时（缺少 git-credential-manager 等），
        // 改用实际存在的助手，否则探测会直接失败、一个账号都找不到
        string helperArg = GitCredentialHelper.OverrideArgument() ?? string.Empty;

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                string host = targets[i];
                TxtProgress.Text = $"正在检测 {i + 1}/{targets.Count}：{host}";

                var cred = await SystemCredentialProbe.ProbeAsync(host, GitLocator.GitExe, ct, helperArg)
                    .ConfigureAwait(true);

                if (cred != null)
                {
                    _results.Add(new CredentialImportItem { Credential = cred, Selected = true });
                    RefreshResults();
                    found++;
                }

                Progress.Value = i + 1;
            }

            TxtProgress.Text = string.Empty;
            if (ct.IsCancellationRequested)
            {
                ShowStatus($"已停止检测：已检测 {Progress.Value:0}/{targets.Count} 个主机，找到 {found} 个账号。", null);
            }
            else if (found == 0)
            {
                ShowStatus($"检测完成：未在这 {targets.Count} 个主机上发现系统凭据。" +
                           "可先在本机成功推送一次让系统保存凭据，或手动填写账号。", false);
            }
            else
            {
                ShowStatus($"检测完成：找到 {found} 个账号，勾选后点击【导入选中】。", true);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("检测过程中发生异常：" + ex.Message, false);
        }
        finally
        {
            BtnDetect.IsEnabled = true;
            BtnStop.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>停止当前检测。</summary>
    private void BtnStop_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts == null) return;
        _cts.Cancel();
        BtnStop.IsEnabled = false;
        TxtProgress.Text = "正在停止…";
    }

    // ==================== 导入 ====================

    private async void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedCredentials.Count == 0)
        {
            await Dialogs.ShowMessageAsync(this, "检测系统凭据", "请先检测并勾选至少一个账号。");
            return;
        }

        Close(true);
    }

    /// <summary>
    /// 【关闭】：先中止可能仍在进行的检测，再关闭窗口（返回 false，不导入账号）。
    /// Avalonia 的 IsCancel 只是把 Esc 键映射成"点击该按钮"，按钮本身不关窗，必须显式处理 Click。
    /// </summary>
    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close(false);
    }

    /// <summary>写入状态文本（true 成功 / false 警告 / null 中性）。</summary>
    private void ShowStatus(string message, bool? ok)
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = ok == true
            ? Brush.Parse("#1E8E3E")
            : ok == false
                ? Brush.Parse("#B00020")
                : Brush.Parse("#666666");
    }
}
