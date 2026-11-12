using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MultiGitCommit;

/// <summary>
/// 拉取更新设置视图：选择要拉取的远端与分叉处理策略，再把参数交回主视图执行。
///
/// 只做参数收集，不执行 git 命令——拉取过程需要实时写日志、可取消，
/// 由主视图统一调用 <see cref="GitPullService"/>。
/// </summary>
public partial class PullView : DialogView<bool>
{
    /// <summary>自带 ScrollViewer + 固定底部按钮栏：移动端铺满视口布局，桌面端锁定窗口尺寸。</summary>
    public override bool FitsOverlayViewport => true;

    /// <summary>可选远端（原始 URL，不含任何改写）。</summary>
    private readonly IReadOnlyList<string> _remotes;

    /// <summary>用户确认后的远端 URL。</summary>
    public string SelectedRemoteUrl { get; private set; } = string.Empty;

    /// <summary>用户确认后的拉取策略。</summary>
    public PullStrategy SelectedStrategy { get; private set; } = PullStrategy.FastForwardOnly;

    /// <param name="repoRoot">仓库根目录（仅展示）。</param>
    /// <param name="remotes">项目配置的远端 URL 列表。</param>
    /// <param name="branch">当前分支。</param>
    public PullView(string repoRoot, IEnumerable<string> remotes, string branch)
    {
        _remotes = remotes.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

        InitializeComponent();

        TxtRepo.Text = repoRoot;
        TxtBranch.Text = branch;

        if (_remotes.Count == 0)
        {
            BtnStart.IsEnabled = false;
            CmbRemote.IsEnabled = false;
            TxtError.Text = "当前项目没有配置远端地址，请先在【推送远端地址管理】中添加。";
            return;
        }

        // 显示时脱敏：URL 内可能带令牌
        CmbRemote.ItemsSource = _remotes.Select(GitCredentials.Mask).ToList();
        CmbRemote.SelectedIndex = 0;
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        if (CmbRemote.SelectedIndex < 0 || CmbRemote.SelectedIndex >= _remotes.Count)
        {
            TxtError.Text = "请选择要拉取的远端地址。";
            return;
        }

        SelectedRemoteUrl = _remotes[CmbRemote.SelectedIndex];
        SelectedStrategy = RbRebase.IsChecked == true
            ? PullStrategy.Rebase
            : RbMerge.IsChecked == true
                ? PullStrategy.Merge
                : PullStrategy.FastForwardOnly;

        Close(true);
    }

    /// <summary>【取消】：关闭对话框，返回 false（Avalonia 的 IsCancel 不会自动关窗）。</summary>
    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
