using System;
using System.IO;
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

    /// <summary>
    /// 移动端「输入 / 新建目录」：系统文件夹选择器的兜底方案。
    /// 安卓的 StorageProvider 走 SAF 文档树，返回 content:// URI（TryGetLocalPath 恒为 null），
    /// 内嵌 git 又必须用真实 POSIX 路径；能由 AndroidGitPlatform.ResolveSafTreeUri 映射时走选择器，
    /// 映射不了（云端盘 / 下载内容等 provider）或选择器不可用时，这里让用户直接填绝对路径，
    /// 目录不存在则询问并新建，最后写临时文件确认可写（顺带提示「所有文件访问」权限）。
    /// 返回 null 表示取消 / 多次输入都无效。
    /// </summary>
    public static async Task<string?> PickFolderAsync(Visual anchor, string title, string message, string suggestedPath)
    {
        string suggestion = suggestedPath;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string? input = await PromptAsync(anchor, title, message, suggestion);
            if (input == null) return null;

            string path = input.Trim();
            if (path.Length == 0) return null;

            if (!Path.IsPathRooted(path))
            {
                await ShowMessageAsync(anchor, title, Loc.T("请输入以 / 开头的绝对路径，安卓不支持相对路径。"));
                suggestion = path;
                continue;
            }

            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (path.Length == 0) path = Path.DirectorySeparatorChar.ToString();

            if (!Directory.Exists(path))
            {
                bool create = await ConfirmAsync(anchor, title,
                    Loc.F($"目录不存在，是否新建？{Environment.NewLine}{path}"));
                if (!create)
                {
                    suggestion = path;
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync(anchor, title, Loc.F($"新建目录失败：{ex.Message}"));
                    suggestion = path;
                    continue;
                }
            }

            if (!TryEnsureWritable(path, out string error))
            {
                await ShowMessageAsync(anchor, title,
                    Loc.F($"目录不可写：{path}{Environment.NewLine}{error}{Environment.NewLine}") +
                    Loc.T("若使用 /storage/emulated/0，请先在系统设置中为 MultiGitCommit 授予「所有文件访问」。"));
                suggestion = path;
                continue;
            }

            return path;
        }

        return null;
    }

    /// <summary>写一个临时文件再删除，确认目录真的可写（目录存在不代表有写权限）。</summary>
    public static bool TryEnsureWritable(string path, out string error)
    {
        string probe = Path.Combine(path, ".mgc-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(probe, "ok");
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            // 即使校验失败也不在仓库目录里留下临时文件
            try { File.Delete(probe); } catch { }
        }
    }

    /// <summary>提示框 / 确认框内容视图（确定，可选取消）。</summary>
    private sealed class MessageBoxView : DialogView<bool>
    {
        public MessageBoxView(string message, bool showCancel)
        {
            Width = 460;
            var ok = new AvaloniaButton { Content = Loc.T("确定"), Width = 90, IsDefault = true, Padding = new Thickness(8, 2) };
            ok.Click += (_, _) => Close(true);

            var buttons = new StackPanel
            {
                Orientation = AvaloniaOrientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ok);
            if (showCancel)
            {
                var cancel = new AvaloniaButton { Content = Loc.T("取消"), Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2) };
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
            var ok = new AvaloniaButton { Content = Loc.T("确定"), Width = 90, IsDefault = true, Padding = new Thickness(8, 2) };
            var cancel = new AvaloniaButton { Content = Loc.T("取消"), Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2) };
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


