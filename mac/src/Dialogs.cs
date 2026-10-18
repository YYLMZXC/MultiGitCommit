using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MultiGitCommit;

/// <summary>
/// 轻量对话框帮助类（纯代码构建，不引入第三方 UI 库）：
/// 提示框、确认框、输入框。Avalonia 无内置 MessageBox，故自行实现。
/// </summary>
internal static class Dialogs
{
    private const string AppIconUri = "avares://MultiGitCommit/res/icon.png";

    /// <summary>构建窗口通用外观的私有工厂。</summary>
    private static Window CreateShell(string title, double width, Window? owner)
    {
        var win = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontSize = 13
        };

        ApplyIcon(win);
        return win;
    }

    /// <summary>为窗口设置应用图标（图标缺失时静默忽略）。</summary>
    public static void ApplyIcon(Window win)
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(AppIconUri));
            win.Icon = new WindowIcon(stream);
        }
        catch (Exception)
        {
            // 图标缺失不影响功能
        }
    }

    /// <summary>显示单按钮提示框。</summary>
    public static async Task ShowMessageAsync(Window? owner, string title, string message)
    {
        var win = CreateShell(title, 460, owner);

        var ok = new Button { Content = "确定", Width = 90, IsDefault = true, Padding = new Thickness(8, 2) };
        ok.Click += (_, _) => win.Close();

        win.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } }
            }
        };

        await win.ShowDialog(owner);
    }

    /// <summary>显示确认框（确定 / 取消），返回用户是否点击确定。</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, string title, string message)
    {
        var win = CreateShell(title, 460, owner);

        bool result = false;
        var ok = new Button { Content = "确定", Width = 90, IsDefault = true, Padding = new Thickness(8, 2) };
        var cancel = new Button { Content = "取消", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2) };
        ok.Click += (_, _) => { result = true; win.Close(); };
        cancel.Click += (_, _) => win.Close();

        win.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };

        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// 显示输入框，返回用户输入内容；用户取消返回 null。
    /// </summary>
    public static async Task<string?> PromptAsync(Window? owner, string title, string message, string initialValue)
    {
        var win = CreateShell(title, 520, owner);

        var box = new TextBox { Text = initialValue, MinHeight = 30 };
        var ok = new Button { Content = "确定", Width = 90, IsDefault = true, Padding = new Thickness(8, 2) };
        var cancel = new Button { Content = "取消", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2) };

        string? result = null;
        ok.Click += (_, _) => { result = box.Text?.Trim() ?? string.Empty; win.Close(); };
        cancel.Click += (_, _) => win.Close();

        win.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { ok, cancel }
                }
            }
        };

        win.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        await win.ShowDialog(owner);
        return result;
    }
}
