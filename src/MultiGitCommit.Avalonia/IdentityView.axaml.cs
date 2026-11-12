using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MultiGitCommit;

/// <summary>
/// 提交身份设置视图：填写 git 的 user.name 与 user.email，并选择写入作用域（当前仓库 / 全局）。
/// 这是 git commit 的前置条件，二者缺一 git 都无法生成提交。
/// </summary>
public partial class IdentityView : DialogView<bool>
{
    /// <summary>用户确认后的提交身份。</summary>
    public GitIdentity SelectedIdentity { get; private set; } = new();

    /// <param name="current">当前身份（用于预填）；可为 null。</param>
    public IdentityView(GitIdentity? current)
    {
        InitializeComponent();

        if (current != null)
        {
            TxtName.Text = current.Name ?? string.Empty;
            TxtEmail.Text = current.Email ?? string.Empty;
            RbGlobal.IsChecked = current.Scope == IdentityScope.Global;
            RbLocal.IsChecked = current.Scope != IdentityScope.Global;
        }

        TxtName.Focus();
    }

    private async void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        string name = TxtName.Text?.Trim() ?? string.Empty;
        string email = TxtEmail.Text?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            await Dialogs.ShowMessageAsync(this, "提交身份", "请填写用户名（git config user.name）。");
            TxtName.Focus();
            return;
        }

        if (email.Length == 0 || !email.Contains('@'))
        {
            await Dialogs.ShowMessageAsync(this, "提交身份", "请填写有效的邮箱（git config user.email）。");
            TxtEmail.Focus();
            return;
        }

        SelectedIdentity = new GitIdentity
        {
            Name = name,
            Email = email,
            Scope = RbGlobal.IsChecked == true ? IdentityScope.Global : IdentityScope.Local
        };

        Close(true);
    }

    /// <summary>
    /// 【取消】：关闭对话框，返回 false（调用方不会写入身份）。
    /// Avalonia 的 IsCancel 只是把 Esc 键映射成"点击该按钮"，按钮本身不关窗，必须显式处理 Click。
    /// </summary>
    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
