using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace MultiGitCommit;

/// <summary>
/// 克隆项目视图：填写远端 URL 与本地目录，执行 git clone。
///
/// 说明：
/// 1. 仅负责收集参数并做前置校验；命令预览与实际执行共用 <see cref="GitCloneArguments"/>，
///    真正的 git 调用由 <see cref="IRepositoryCommands"/> 完成；
/// 2. 目标目录名默认根据 URL 自动推导，用户手动修改后不再被自动覆盖；
/// 3. 可选项：指定分支、递归子模块、浅克隆、克隆后写入推送远端列表。
/// </summary>
public partial class CloneView : DialogView<bool>
{
    /// <summary>自带 ScrollViewer + 固定底部按钮栏：移动端铺满视口布局，桌面端锁定窗口尺寸。</summary>
    public override bool FitsOverlayViewport => true;

    /// <summary>用户确认后的远端仓库 URL。</summary>
    public string RepoUrl { get; private set; } = string.Empty;

    /// <summary>用户确认后的本地父目录（仓库克隆到该目录下）。</summary>
    public string ParentDir { get; private set; } = string.Empty;

    /// <summary>用户确认后的完整目标目录（父目录 + 目录名），即仓库根目录。</summary>
    public string TargetDir { get; private set; } = string.Empty;

    /// <summary>用户确认后要检出的分支；空表示使用远端默认分支。</summary>
    public string BranchName { get; private set; } = string.Empty;

    /// <summary>是否递归克隆子模块（--recursive）。</summary>
    public bool Recursive { get; private set; }

    /// <summary>是否浅克隆（--depth 1）。</summary>
    public bool Shallow { get; private set; }

    /// <summary>克隆成功后是否把该 URL 写入本项目的推送远端列表。</summary>
    public bool AddToRemotes { get; private set; }

    /// <summary>目录名是否被用户手动编辑过（编辑后不再根据 URL 自动覆盖）。</summary>
    private bool _folderNameEdited;

    /// <summary>程序内部设置目录名时抑制 TextChanged 的标志位。</summary>
    private bool _suppress;

    /// <param name="initialDir">父目录初始值（一般传入当前仓库目录或程序工作目录）。</param>
    public CloneView(string? initialDir = null)
    {
        _suppress = true;
        InitializeComponent();
        _suppress = false;

        ChkAddRemote.IsChecked = true;
        // 安卓端预填一个确实可写的仓库父目录（用户主目录在安卓上不是常规存储位置）
        TxtParentDir.Text = !string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir)
            ? initialDir
            : AndroidGitPlatform.SuggestRepoParent();

        UpdatePreview();
    }

    // ==================== 输入事件 ====================

    /// <summary>URL 变化：未手动编辑过目录名时，自动推导目录名。</summary>
    private void TxtUrl_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_folderNameEdited)
            SetFolderName(DeriveRepoName(TxtUrl.Text ?? string.Empty));
        UpdatePreview();
    }

    /// <summary>目录名被用户手动编辑后，标记不再自动覆盖。</summary>
    private void TxtFolderName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        _folderNameEdited = true;
        UpdatePreview();
    }

    /// <summary>其余输入项变化时刷新预览。</summary>
    private void Input_Changed(object? sender, RoutedEventArgs e)
    {
        UpdatePreview();
    }

    /// <summary>浏览选择本地父目录（桌面与安卓均为系统文件夹选择器；无法映射的位置回退手输）。</summary>
    private async void BtnBrowseParent_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider != null)
        {
            try
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "请选择要把仓库克隆到的父目录",
                    AllowMultiple = false
                });

                if (folders.Count == 0) return;

                // 安卓 SAF 给的是 content:// 树 URI，需映射成内嵌 git 能用的本地路径
                string? path = folders[0].TryGetLocalPath()
                               ?? AndroidGitPlatform.ResolveSafTreeUri(folders[0].Path);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (OperatingSystem.IsAndroid() && !Dialogs.TryEnsureWritable(path, out string error))
                    {
                        await Warn($"该目录不可写：{path}{Environment.NewLine}{error}{Environment.NewLine}" +
                                   "请先在系统设置中授予本应用「所有文件访问」权限。");
                        return;
                    }

                    TxtParentDir.Text = path.TrimEnd(Path.DirectorySeparatorChar);
                    UpdatePreview();
                    return;
                }

                await Warn("该位置无法转换为本地目录（仅支持「内部存储 / SD 卡」中的文件夹），请手动输入父目录路径。");
            }
            catch (Exception ex)
            {
                // 个别 ROM 的 SAF 实现会抛异常：不能让 async void 事件处理器把应用带崩
                await Warn($"系统文件夹选择器不可用：{ex.Message}，请手动输入父目录路径。");
            }
        }

        // 兜底：手输绝对路径，可自动新建
        string? picked = await Dialogs.PickFolderAsync(this, "输入克隆父目录",
            "请输入要把仓库克隆到的父目录绝对路径，例如：" + Environment.NewLine +
            "/storage/emulated/0/GitRepos" + Environment.NewLine +
            "目录不存在时会询问是否新建；仓库目录名在下方单独填写。",
            string.IsNullOrWhiteSpace(TxtParentDir.Text)
                ? AndroidGitPlatform.SuggestRepoParent()
                : TxtParentDir.Text!.Trim());
        if (picked == null) return;

        TxtParentDir.Text = picked;
        UpdatePreview();
    }

    // ==================== 预览与参数组装 ====================

    /// <summary>刷新目标路径与将要执行的 git clone 命令预览。</summary>
    private void UpdatePreview()
    {
        string url = TxtUrl.Text?.Trim() ?? string.Empty;
        string parent = TxtParentDir.Text?.Trim() ?? string.Empty;
        string folder = TxtFolderName.Text?.Trim() ?? string.Empty;

        string target = string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folder)
            ? "（请先填写远端 URL 与本地目录）"
            : Path.Combine(parent, folder);

        TxtTargetPath.Text = target;
        TxtCommandPreview.Text = string.IsNullOrEmpty(url)
            ? "（请先填写远端 URL）"
            : $"git {GitCloneArguments.Build(TxtBranch.Text?.Trim() ?? string.Empty, ChkRecursive.IsChecked == true, ChkShallow.IsChecked == true, url, target)}";
    }

    /// <summary>根据远端 URL 推导默认目录名（去掉 .git 后缀，取最后一段路径）。</summary>
    private static string DeriveRepoName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        string s = url.Trim().TrimEnd('/', '\\');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];

        // 兼容 ssh（git@host:user/repo.git）、https 与本地路径三种形式
        int idx = s.LastIndexOfAny(new[] { '/', '\\', ':' });
        string name = idx >= 0 ? s[(idx + 1)..] : s;

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), string.Empty);

        return name.Trim();
    }

    /// <summary>程序内部设置目录名（不触发“已手动编辑”标记）。</summary>
    private void SetFolderName(string name)
    {
        _suppress = true;
        try
        {
            TxtFolderName.Text = name;
        }
        finally
        {
            _suppress = false;
        }
    }

    // ==================== 确定 / 校验 ====================

    private async void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        string url = TxtUrl.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            await Warn("请填写远端仓库 URL。");
            TxtUrl.Focus();
            return;
        }

        if (url.Contains('"'))
        {
            await Warn("远端 URL 不能包含双引号。");
            TxtUrl.Focus();
            return;
        }

        string parent = TxtParentDir.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parent))
        {
            await Warn("请填写要克隆到的本地目录。");
            TxtParentDir.Focus();
            return;
        }

        if (!Directory.Exists(parent))
        {
            bool create = await Dialogs.ConfirmAsync(this, "克隆项目",
                $"父目录不存在，是否新建？{Environment.NewLine}{parent}");
            if (!create) return;

            try
            {
                Directory.CreateDirectory(parent);
            }
            catch (Exception ex)
            {
                await Warn($"新建目录失败：{ex.Message}");
                return;
            }
        }

        string folder = TxtFolderName.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(folder))
        {
            folder = DeriveRepoName(url);
            SetFolderName(folder);
        }

        if (string.IsNullOrEmpty(folder))
        {
            await Warn("无法确定目录名，请手动填写【目录名】。");
            TxtFolderName.Focus();
            return;
        }

        if (folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await Warn("目录名包含非法字符（\\ / : * ? \" < > | 等）。");
            TxtFolderName.Focus();
            return;
        }

        string target = Path.Combine(parent, folder);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            await Warn($"目标目录已存在且不为空：{target}{Environment.NewLine}请修改【目录名】或选择其他本地目录。");
            TxtFolderName.Focus();
            return;
        }

        string branch = TxtBranch.Text?.Trim() ?? string.Empty;
        if (branch.Length > 0 && !GitRefName.IsValidBranchName(branch))
        {
            await Warn("分支名不合法：不能包含空格或 ~ ^ : ? * [ \\ .. // 等字符，且不能以 - / . 开头或结尾。");
            TxtBranch.Focus();
            return;
        }

        RepoUrl = url;
        ParentDir = parent;
        TargetDir = target;
        BranchName = branch;
        Recursive = ChkRecursive.IsChecked == true;
        Shallow = ChkShallow.IsChecked == true;
        AddToRemotes = ChkAddRemote.IsChecked == true;

        Close(true);
    }

    /// <summary>
    /// 【取消】：关闭对话框，返回 false（调用方不会执行克隆）。
    /// Avalonia 的 IsCancel 只是把 Esc 键映射成"点击该按钮"，按钮本身不关窗，必须显式处理 Click。
    /// </summary>
    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private Task Warn(string message) =>
        Dialogs.ShowMessageAsync(this, "克隆项目", message);
}
