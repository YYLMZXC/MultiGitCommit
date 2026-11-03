using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MultiGitCommit;

/// <summary>
/// 提交历史窗口：浏览当前分支的历史提交（git log），并可把本地分支重置到任意一条记录。
///
/// 设计要点：
/// 1. 所有 git 调用都通过 <see cref="IGitCliRunner"/> 异步执行并带超时，绝不阻塞 UI 线程；
/// 2. 重置是破坏性操作，执行前必须二次确认；hard 模式额外提示未提交改动的文件数量；
/// 3. 只重置本地分支，不推送；界面提示远端可能已超前，需要强制推送。
/// </summary>
public partial class HistoryWindow : Window
{
    /// <summary>最多读取的提交条数（避免超大仓库一次性拉取过多数据）。</summary>
    private const int MaxCommits = 200;

    private readonly string _repoRoot;
    private readonly IGitCliRunner _cli;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<GitCommit> _commits = new();

    /// <summary>重置进行中：防止重复点击与并发 git 调用。</summary>
    private bool _busy;

    /// <summary>读取序号：快速刷新时丢弃过期的 git log 结果。</summary>
    private int _loadSeq;

    /// <summary>git reset 的三种模式。</summary>
    private enum ResetMode
    {
        Soft,
        Mixed,
        Hard
    }

    public HistoryWindow(string repoRoot) : this(repoRoot, GitCliRunner.Default)
    {
    }

    /// <summary>可注入命令执行器（单元测试时传入假实现，无需真实 git 与 UI 运行时）。</summary>
    public HistoryWindow(string repoRoot, IGitCliRunner cli)
    {
        _repoRoot = repoRoot;
        _cli = cli;

        InitializeComponent();
        Dialogs.ApplyIcon(this);

        TxtRepoPath.Text = repoRoot;

        // 关闭窗口时中止未完成的 git 进程（读取历史 / 重置）
        Closing += (_, _) => _lifetime.Cancel();

        // 打开窗口后再读取，避免构造函数里做 IO 拖慢窗口显示
        Opened += async (_, _) => await LoadCommitsAsync();
    }

    // ==================== 读取历史 ====================

    /// <summary>读取当前分支最近的提交并刷新列表。</summary>
    private async Task LoadCommitsAsync()
    {
        int seq = ++_loadSeq;
        BtnRefresh.IsEnabled = false;
        SetStatus("正在读取提交历史…", null);

        try
        {
            // 字段之间用 0x1F 分隔：提交标题里的空格 / 制表符不会破坏解析
            GitCommandResult result = await _cli.RunAsync(new GitCommandRequest
            {
                Arguments = $"log -n {MaxCommits} --date=iso --pretty=format:%H%x1f%h%x1f%ad%x1f%an%x1f%s",
                WorkingDirectory = _repoRoot,
                Timeout = GitTimeouts.Quick
            }, _lifetime.Token).ConfigureAwait(true);

            if (seq != _loadSeq || _lifetime.IsCancellationRequested) return;

            if (!result.Success)
            {
                _commits.Clear();
                RefreshList();

                SetStatus(result.TimedOut
                        ? "× 读取提交历史超时，已终止 git 进程（仓库过大或磁盘响应慢）。"
                        : IsEmptyRepository(result)
                            ? "当前仓库还没有任何提交（执行一次提交后即可在这里查看历史）。"
                            : "× 读取提交历史失败：" + result.ErrorText,
                    false);
                return;
            }

            _commits.Clear();
            _commits.AddRange(GitCommit.Parse(result.Stdout));
            RefreshList();

            SetStatus(_commits.Count == 0
                    ? "当前仓库还没有任何提交（执行一次提交后即可在这里查看历史）。"
                    : $"共显示最近 {_commits.Count} 条提交（最多 {MaxCommits} 条）。选中一条后可重置到该提交。",
                null);
        }
        catch (OperationCanceledException)
        {
            // 窗口已关闭：忽略
        }
        catch (Exception ex)
        {
            if (seq != _loadSeq || _lifetime.IsCancellationRequested) return;
            SetStatus("× 读取提交历史失败：" + ex.Message, false);
        }
        finally
        {
            if (seq == _loadSeq && !_busy) BtnRefresh.IsEnabled = true;
        }
    }

    /// <summary>git log 在空仓库上的报错文本判定（不同 git 版本措辞略有差异）。</summary>
    private static bool IsEmptyRepository(GitCommandResult result)
    {
        string text = result.ErrorText;
        return text.Contains("does not have any commits yet", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains("your current branch", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("no commits", StringComparison.OrdinalIgnoreCase)) ||
               text.Contains("unknown revision", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshList()
    {
        ListCommits.ItemsSource = null;
        ListCommits.ItemsSource = _commits;
        UpdateResetState();
    }

    private void ListCommits_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateResetState();

    /// <summary>根据是否选中提交、是否正在重置，同步按钮可用状态与提示。</summary>
    private void UpdateResetState()
    {
        var selected = ListCommits.SelectedItem as GitCommit;
        BtnReset.IsEnabled = selected != null && !_busy;

        TxtHint.Text = selected == null
            ? "请先在列表中选择要重置到的提交。"
            : $"{selected.ShortHash}  {selected.Subject}";
    }

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await LoadCommitsAsync();
    }

    // ==================== 重置到选中提交 ====================

    private async void BtnReset_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || ListCommits.SelectedItem is not GitCommit commit) return;

        ResetMode mode = RbHard.IsChecked == true
            ? ResetMode.Hard
            : RbMixed.IsChecked == true
                ? ResetMode.Mixed
                : ResetMode.Soft;

        string modeArg = mode switch
        {
            ResetMode.Hard => "--hard",
            ResetMode.Mixed => "--mixed",
            _ => "--soft"
        };

        string effect = mode switch
        {
            ResetMode.Hard => "工作区中所有未提交的改动都会被永久丢弃（无法恢复）",
            ResetMode.Mixed => "暂存区会被重置，工作区中的文件内容保持不变",
            _ => "不会改动暂存区和工作区，改动会保留为已暂存状态"
        };

        // hard 会丢掉未提交改动：先统计数量，让用户在确认框里看到具体影响
        string dirty = string.Empty;
        if (mode == ResetMode.Hard)
        {
            GitCommandResult status = await _cli.RunAsync(new GitCommandRequest
            {
                Arguments = "status --porcelain",
                WorkingDirectory = _repoRoot,
                Timeout = GitTimeouts.Quick
            }, _lifetime.Token).ConfigureAwait(true);

            if (_lifetime.IsCancellationRequested) return;

            if (status.Success)
            {
                int changed = status.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                if (changed > 0)
                {
                    dirty = $"{Environment.NewLine}{Environment.NewLine}" +
                            $"注意：当前工作区有 {changed} 个文件存在未提交改动，执行 hard 重置后将全部丢失。";
                }
            }
        }

        string message =
            $"确定要把当前分支重置到提交 {commit.ShortHash} 吗？{Environment.NewLine}{Environment.NewLine}" +
            $"时间：{commit.Date}    作者：{commit.Author}{Environment.NewLine}" +
            $"说明：{commit.Subject}{Environment.NewLine}{Environment.NewLine}" +
            $"重置方式：{modeArg}（{effect}）{dirty}{Environment.NewLine}{Environment.NewLine}" +
            "重置只影响本地分支，不会改动远端；若远端已包含这些提交，普通推送会被拒绝，需要强制推送。";

        if (!await Dialogs.ConfirmAsync(this, "重置分支", message)) return;
        if (_lifetime.IsCancellationRequested) return;

        bool succeeded = false;
        string successText = string.Empty;

        _busy = true;
        BtnReset.IsEnabled = false;
        BtnRefresh.IsEnabled = false;
        TxtProgress.Text = "正在重置…";
        SetStatus($"正在执行 git reset {modeArg} {commit.ShortHash}…", null);

        try
        {
            GitCommandResult result = await _cli.RunAsync(new GitCommandRequest
            {
                Arguments = $"reset {modeArg} {commit.Hash}",
                WorkingDirectory = _repoRoot,
                Timeout = GitTimeouts.Default
            }, _lifetime.Token).ConfigureAwait(true);

            if (_lifetime.IsCancellationRequested) return;

            if (result.Cancelled)
            {
                SetStatus("已取消重置。", null);
                return;
            }

            if (!result.Success)
            {
                SetStatus(result.TimedOut
                        ? "× 重置超时，已终止 git 进程。"
                        : "× 重置失败：" + result.ErrorText,
                    false);
                return;
            }

            succeeded = true;
            successText = $"√ 已重置到 {commit.ShortHash}（{modeArg}）：{result.Output}";
            SetStatus(successText, true);
        }
        catch (OperationCanceledException)
        {
            // 窗口已关闭
        }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested)
                SetStatus("× 重置失败：" + ex.Message, false);
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _busy = false;
                TxtProgress.Text = string.Empty;
                BtnRefresh.IsEnabled = true;
                UpdateResetState();

                // HEAD 已变化：重新读取历史；成功后把状态恢复为重置结果（不被读取提示覆盖）
                await LoadCommitsAsync();
                if (succeeded) SetStatus(successText, true);
            }
        }
    }

    /// <summary>关闭窗口（Avalonia 的 IsCancel 不会自动关窗，必须显式处理 Click）。</summary>
    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>写入状态文本（true 成功 / false 失败 / null 中性提示）。</summary>
    private void SetStatus(string message, bool? ok)
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = ok == true
            ? Brush.Parse("#1E8E3E")
            : ok == false
                ? Brush.Parse("#B00020")
                : Brush.Parse("#666666");
    }
}
