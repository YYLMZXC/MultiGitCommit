using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaButton = Avalonia.Controls.Button;
using AvaloniaOrientation = Avalonia.Layout.Orientation;

namespace MultiGitCommit;

/// <summary>
/// 轻量对话框帮助类（纯代码构建，不引入第三方 UI 库）：
/// 提示框、确认框、输入框。Avalonia 无内置 MessageBox，故自行实现。
/// 对话框统一经 <see cref="UiDialogs"/> 呈现：桌面端为模态窗口，移动端为覆盖层。
/// anchor 参数传调用方控件（桌面端为主窗口/所在对话框视图，移动端为主视图即可）。
/// </summary>
internal static class Dialogs
{
    private const string AppIconUri = "avares://MultiGitCommit/res/icon.png";

    /// <summary>为窗口设置应用图标（图标缺失时静默忽略；仅桌面端 Window 有图标概念）。</summary>
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
    public static Task ShowMessageAsync(Visual anchor, string title, string message)
        => UiDialogs.ShowAsync(anchor, new MessageBoxView(message, showCancel: false), title);

    /// <summary>显示确认框（确定 / 取消），返回用户是否点击确定。</summary>
    public static Task<bool> ConfirmAsync(Visual anchor, string title, string message)
        => UiDialogs.ShowAsync<bool>(anchor, new MessageBoxView(message, showCancel: true), title);

    /// <summary>
    /// 显示输入框，返回用户输入内容；用户取消返回 null。
    /// </summary>
    public static Task<string?> PromptAsync(Visual anchor, string title, string message, string initialValue)
        => UiDialogs.ShowAsync<string?>(anchor, new PromptBoxView(message, initialValue), title);

    /// <summary>提示框 / 确认框内容视图（确定，可选取消）。</summary>
    private sealed class MessageBoxView : DialogView<bool>
    {
        public MessageBoxView(string message, bool showCancel)
        {
            Width = 460;
            var ok = new AvaloniaButton { Content = "确定", Width = 90, IsDefault = true, Padding = new Thickness(8, 2) };
            ok.Click += (_, _) => Close(true);

            var buttons = new StackPanel
            {
                Orientation = AvaloniaOrientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ok);
            if (showCancel)
            {
                var cancel = new AvaloniaButton { Content = "取消", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2) };
                cancel.Click += (_, _) => Close(false);
                buttons.Children.Add(cancel);
            }

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) },
                    buttons
                }
            };
        }
    }

    /// <summary>输入框内容视图（文本框 + 确定 / 取消）。</summary>
    private sealed class PromptBoxView : DialogView<string?>
    {
        public PromptBoxView(string message, string initialValue)
        {
            Width = 520;
            var box = new TextBox { Text = initialValue, MinHeight = 30 };
            var ok = new AvaloniaButton { Content = "确定", Width = 90, IsDefault = true, Padding = new Thickness(8, 2) };
            var cancel = new AvaloniaButton { Content = "取消", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2) };
            ok.Click += (_, _) => Close(box.Text?.Trim() ?? string.Empty);
            cancel.Click += (_, _) => Close(null);

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) },
                    box,
                    new StackPanel
                    {
                        Orientation = AvaloniaOrientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 14, 0, 0),
                        Children = { ok, cancel }
                    }
                }
            };

            AttachedToVisualTree += (_, _) =>
            {
                box.Focus();
                box.SelectAll();
            };
        }
    }
}


