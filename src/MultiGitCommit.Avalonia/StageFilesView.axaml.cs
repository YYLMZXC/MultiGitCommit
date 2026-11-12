using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MultiGitCommit;

/// <summary>文件勾选列表的一行（勾选状态 + 展示字段）。</summary>
public sealed class StageFileItem
{
    /// <summary>对应的工作区变更项。</summary>
    public WorkingTreeFile File { get; init; } = new();

    /// <summary>是否执行 git add（双向绑定到复选框）。</summary>
    public bool Selected { get; set; }

    public string Path => File.Path;

    public string Display => File.Display;

    public string StatusLabel => File.StatusLabel;

    public string StageLabel => File.StageLabel;
}

/// <summary>
/// 「执行 git add」前的文件勾选视图：列出工作区全部变更，让用户勾选要添加的文件。
///
/// 只做选择，不执行任何 git 命令——add 由 <see cref="CommitPushService"/> 统一执行并写日志，
/// 这样 add 的命令拼接、分批与失败提示只有一份实现。
/// </summary>
public partial class StageFilesView : DialogView<bool>
{
    /// <summary>底部按钮栏固定、中间列表内部滚动：移动端铺满视口布局，桌面端锁定窗口尺寸。</summary>
    public override bool FitsOverlayViewport => true;

    private readonly List<StageFileItem> _items;

    /// <summary>用户勾选、需要 add 的相对路径；取消对话框时为 null（由宿主判断）。</summary>
    public IReadOnlyList<string> SelectedPaths =>
        _items.Where(i => i.Selected).Select(i => i.Path).ToList();

    /// <param name="repoRoot">仓库根目录（仅展示）。</param>
    /// <param name="files">工作区变更文件列表。</param>
    public StageFilesView(string repoRoot, IReadOnlyList<WorkingTreeFile> files)
    {
        // 默认全选：与「勾选执行 git add .」的既有语义一致，用户只需取消不想要的
        _items = files.Select(f => new StageFileItem { File = f, Selected = true }).ToList();

        InitializeComponent();

        // 桌面端靠 MinHeight 撑开 SizeToContent 窗口；移动端视口高度固定，
        // 保留最小高度会把底部按钮栏顶出可视区，这里改为由 DockPanel 按剩余空间分配
        if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
            ListFiles.MinHeight = 0;

        TxtRepo.Text = repoRoot;
        ListFiles.ItemsSource = _items;
        RefreshCount();
    }

    // ==================== 批量操作 ====================

    private void BtnSelectAll_Click(object? sender, RoutedEventArgs e) => SetAll(_ => true);

    private void BtnSelectNone_Click(object? sender, RoutedEventArgs e) => SetAll(_ => false);

    /// <summary>只勾选工作区仍有未暂存改动的文件（已暂存的不重复 add）。</summary>
    private void BtnSelectUnstaged_Click(object? sender, RoutedEventArgs e) => SetAll(i => i.File.HasUnstagedChange);

    private void SetAll(Func<StageFileItem, bool> selector)
    {
        foreach (var item in _items) item.Selected = selector(item);
        RefreshList();
        ShowStatus(string.Empty);
    }

    /// <summary>
    /// 刷新列表：勾选状态直接写在普通属性上（无 INotifyPropertyChanged），
    /// 因此重建 ItemsSource 让界面按新值重新渲染。
    /// </summary>
    private void RefreshList()
    {
        ListFiles.ItemsSource = null;
        ListFiles.ItemsSource = _items;
        RefreshCount();
    }

    /// <summary>复选框勾选变化：刷新计数并清掉旧提示。</summary>
    private void Item_Checked(object? sender, RoutedEventArgs e)
    {
        RefreshCount();
        ShowStatus(string.Empty);
    }

    private void RefreshCount()
    {
        int selected = _items.Count(i => i.Selected);
        TxtCount.Text = $"已勾选 {selected} / {_items.Count} 个文件";
        BtnOk.IsEnabled = _items.Count > 0;
    }

    // ==================== 确认 / 取消 ====================

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedPaths.Count == 0)
        {
            ShowStatus("请至少勾选一个要添加的文件；如果本次不想执行 git add，请点【取消】返回。");
            return;
        }

        Close(true);
    }

    /// <summary>【取消】：关闭对话框并返回 false（Avalonia 的 IsCancel 不会自动关窗）。</summary>
    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowStatus(string message)
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = Brush.Parse("#B00020");
    }
}
