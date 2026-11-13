using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MultiGitCommit;

/// <summary>
/// 可作为对话框呈现的视图基类（UserControl）：
/// 桌面端由 <see cref="UiDialogs"/> 包进 Window 用 ShowDialog 模态弹出；
/// 移动端（Android / iOS，无窗口概念）放进 <see cref="DialogOverlay"/> 覆盖层堆叠显示。
/// 子类通过 <see cref="DialogView{TResult}.Close(TResult)"/> 结束对话框并回传结果。
/// </summary>
public abstract class DialogViewBase : UserControl
{
    /// <summary>视图请求关闭（结果已就绪）时触发，由宿主（Window / 覆盖层）订阅。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>宿主在结果就绪前直接关闭（如桌面端点窗口 × 按钮）时触发，视图可借此中止后台任务。</summary>
    public event EventHandler? HostClosed;

    /// <summary>由派生类（DialogView&lt;TResult&gt;）在宿主直接关闭时调用。</summary>
    internal void RaiseHostClosed() => HostClosed?.Invoke(this, EventArgs.Empty);

    /// <summary>取回对话框结果（Close 时完成；宿主异常关闭时以默认值完成）。</summary>
    public abstract Task<object?> GetResultAsync();

    /// <summary>宿主被直接关闭（如桌面端点窗口 × 按钮）时由宿主调用：以默认值完成结果任务，避免调用方永久等待。</summary>
    internal abstract void CancelIfPending();

    /// <summary>
    /// 视图是否自带纵向滚动区 + 固定底部按钮栏，并按移动端覆盖层可用视口铺满布局。
    /// 为 true 时：卡片占满视口（扣除安全区），视图固定宽高，由视图内部 ScrollViewer 滚动，底部按钮常驻可见。
    /// 为 false 时：小型提示 / 输入对话框，卡片按内容高度居中，超出视口时由卡片自带 ScrollViewer 滚动。
    /// 两种情况都会把视图宽度约束到视口宽度、禁用横向滚动，长文本 / 比例列才会正常换行与收敛。
    /// 桌面端 Window 路径只用它决定是否锁定窗口尺寸（内容变化不再把窗口撑到屏幕外）。
    /// </summary>
    public virtual bool FitsOverlayViewport => false;

    /// <summary>触发关闭事件（宿主会取走结果）。</summary>
    protected void RaiseClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>带结果的对话框视图基类。</summary>
/// <typeparam name="TResult">对话框结果类型（无实际结果的视图用 bool，关闭时传 true）。</typeparam>
public abstract class DialogView<TResult> : DialogViewBase
{
    private readonly TaskCompletionSource<TResult> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>对话框结果任务（Close 后完成）。</summary>
    public Task<TResult> Result => _tcs.Task;

    public override async Task<object?> GetResultAsync() => await Result.ConfigureAwait(false);

    internal override void CancelIfPending()
    {
        if (_tcs.TrySetResult(default!))
            RaiseHostClosed();
    }

    /// <summary>以指定结果关闭对话框。</summary>
    protected void Close(TResult result)
    {
        if (_tcs.TrySetResult(result))
            RaiseClose();
    }
}

/// <summary>
/// 统一对话框宿主服务：
/// 桌面端把视图包进临时 Window（ShowDialog 模态，与原窗口体验一致）；
/// 移动端（Android / iOS）把视图推进 <see cref="DialogOverlay"/> 覆盖层（支持嵌套堆叠）。
/// 调用方代码两个平台完全一致，仅通过 anchor 控件自动选择呈现方式。
/// </summary>
internal static class UiDialogs
{
    /// <summary>
    /// 移动端覆盖层宿主（由 MainView 在单视图生命周期下注册）。
    /// 桌面端不使用（桌面端优先从可视树找到 Window）。
    /// </summary>
    public static DialogOverlay? OverlayHost { get; set; }

    /// <summary>显示无实际结果的对话框（如「关于」、浏览历史）。</summary>
    public static Task ShowAsync(Visual anchor, DialogViewBase content, string title)
        => ShowCore(anchor, content, title);

    /// <summary>显示带结果的对话框，返回用户操作结果；宿主被直接关闭时返回 default。</summary>
    public static async Task<T?> ShowAsync<T>(Visual anchor, DialogViewBase content, string title)
    {
        object? raw = await ShowCore(anchor, content, title).ConfigureAwait(true);
        return raw is T typed ? typed : default;
    }

    private static async Task<object?> ShowCore(Visual anchor, DialogViewBase content, string title)
    {
        // ---- 移动端（或任何无 Window 的宿主）：覆盖层堆叠 ----
        // anchor 本身或其祖先命中的覆盖层优先（嵌套对话框：从覆盖层内再弹，复用同一宿主实现堆叠）
        var overlay = (anchor as DialogOverlay)
                      ?? anchor.GetVisualAncestors().OfType<DialogOverlay>().FirstOrDefault()
                      ?? OverlayHost;
        if (overlay != null)
            return await overlay.PushAsync(content, title).ConfigureAwait(true);

        // ---- 桌面端：包进临时 Window，走 ShowDialog 模态 ----
        var owner = (anchor as Window)
                    ?? anchor.GetVisualAncestors().OfType<Window>().FirstOrDefault();
        if (owner == null)
            throw new InvalidOperationException(Loc.T("无法确定对话框宿主：anchor 可视树中既没有 Window 也没有覆盖层。"));

        var win = new Window
        {
            Title = title,
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontSize = 13
        };
        Dialogs.ApplyIcon(win);
        ConfigureDesktopWindowSize(win, content);

        // 视图请求关闭 → 把结果带回 ShowDialog 返回值
        async void OnCloseRequested(object? _, EventArgs __)
            => win.Close(await content.GetResultAsync().ConfigureAwait(true));

        // 用户直接点窗口 × 按钮 → 以默认值完成结果任务，避免调用方永久等待
        void OnClosed(object? _, EventArgs __)
        {
            content.CancelIfPending();
            content.CloseRequested -= OnCloseRequested;
            win.Closed -= OnClosed;
        }

        content.CloseRequested += OnCloseRequested;
        win.Closed += OnClosed;

        await win.ShowDialog(owner).ConfigureAwait(true);
        content.CloseRequested -= OnCloseRequested;
        return await content.GetResultAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 桌面对话框尺寸策略：SizeToContent 会随内容变化撑窗口（展开【Git 环境设置 → 高级设置】时最明显）。
    /// 窗口位置不会重算，向下长到屏幕外后，固定的底部按钮栏与下半部分内容就看不见了。
    /// ① 把窗口限制在当前屏幕工作区内；② 自带滚动区 + 固定按钮栏的视图在首帧后锁定尺寸，
    /// 之后内容变化全部由视图内部 ScrollViewer 承担，窗口不再跳动。
    /// </summary>
    private static void ConfigureDesktopWindowSize(Window win, DialogViewBase content)
    {
        double maxW = double.PositiveInfinity;
        double maxH = double.PositiveInfinity;
        if (win.Screens.Primary is { } screen && screen.Scaling > 0)
        {
            maxW = Math.Max(320, screen.WorkingArea.Width / screen.Scaling - 40);
            maxH = Math.Max(400, screen.WorkingArea.Height / screen.Scaling - 60);
            win.MaxWidth = maxW;
            win.MaxHeight = maxH;
        }

        if (!content.FitsOverlayViewport)
            return;

        win.Opened += (_, _) =>
        {
            double width = Math.Min(win.ClientSize.Width, maxW);
            double height = Math.Min(win.ClientSize.Height, maxH);

            // 长设置页（MinHeight ≥ 450：Git 环境 / 系统凭据检测）给足初始高度，避免锁定后
            // 滚动区过窄、展开高级设置要滚很久；内容少的小对话框仍保持按内容自适应。
            if (content.MinHeight >= 450 && !double.IsInfinity(maxH))
                height = Math.Min(maxH, Math.Max(height, maxH * 0.75));

            win.SizeToContent = SizeToContent.Manual;
            win.Width = width;
            win.Height = height;
        };
    }
}

/// <summary>
/// 移动端对话框覆盖层宿主：
/// 每推入一个对话框就叠一层（半透明压暗背景 + 居中卡片），支持嵌套堆叠；
/// 层内用 ScrollViewer 包裹，保证大视图（如 Git 环境设置）在小屏可滚动查看。
/// </summary>
public class DialogOverlay : Panel
{
    private sealed class LayerInfo
    {
        public required Panel LayerRoot;
        public required DialogViewBase View;
    }

    /// <summary>卡片与安全区之间的额外留白。</summary>
    private const double EdgeMargin = 8;

    /// <summary>移动端没有窗口标题栏，卡片自带标题栏（标题 + 关闭按钮）的高度。</summary>
    private const double TitleBarHeight = 44;

    private readonly List<LayerInfo> _layers = new();

    public DialogOverlay()
    {
        IsVisible = false;
    }

    /// <summary>当前是否有对话框正在显示。</summary>
    public bool HasOpenDialogs => _layers.Count > 0;

    /// <summary>推入一个对话框层；返回的任务在对话框关闭时完成并携带结果。</summary>
    public async Task<object?> PushAsync(DialogViewBase view, string title)
    {
        var dim = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)) };

        // 根 UserControl 上的固定 Width / MinHeight 是给桌面 SizeToContent 窗口撑尺寸用的。
        // 移动端全部清掉：否则 460~920 宽的内容会顶破手机视口，右侧直接被裁掉，
        // TextWrapping 也会因「无限宽度测量」而失效。
        view.Width = double.NaN;
        view.Height = double.NaN;
        view.MinWidth = 0;
        view.MinHeight = 0;
        view.MaxWidth = double.PositiveInfinity;
        view.MaxHeight = double.PositiveInfinity;

        // 铺满视口的大视图内部已有 ScrollViewer + 固定按钮栏；小对话框由卡片 ScrollViewer 承担滚动。
        bool fitViewport = view.FitsOverlayViewport;
        var contentScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = fitViewport ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            Content = view
        };

        var titleText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? "MultiGitCommit" : title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.Parse("#2B579A"))
        };
        // 全限定：android TFM 的隐式 using 里也有 Android.Widget.Button，简写会 CS0104 二义性
        var closeButton = new Avalonia.Controls.Button
        {
            Content = Loc.T("关闭"),
            MinWidth = 56,
            Height = 30,
            Padding = new Thickness(10, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        // 移动端没有窗口 × 按钮：这里补一个统一关闭入口，语义与桌面点 × 相同（取消 / 中止后台任务）
        closeButton.Click += (_, _) => view.CancelIfPending();

        var titleDock = new DockPanel();
        DockPanel.SetDock(closeButton, Dock.Right);
        titleDock.Children.Add(closeButton);
        titleDock.Children.Add(titleText);

        var titleBar = new Border
        {
            Height = TitleBarHeight,
            Padding = new Thickness(14, 0, 8, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#D8DEE6")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = titleDock
        };

        var cardBody = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        cardBody.Children.Add(titleBar);
        cardBody.Children.Add(contentScroll);

        // 所有弹窗视图的配色（#2B579A 标题、#666 说明、#CCCCCC 分隔线…）都是浅色主题配色。
        // 系统深色模式下 FluentTheme 会把文字 / 控件画成浅色，白底卡片上就成了白字白按钮；
        // 显式把整棵子树锁到 Light 变体（App.axaml 同时把应用锁到 Light，覆盖 ComboBox 下拉等浮层）。
        var scope = new ThemeVariantScope
        {
            RequestedThemeVariant = ThemeVariant.Light,
            Child = cardBody
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = scope
        };

        var layer = new Grid { Children = { dim, card } };

        void ApplySize()
        {
            Thickness safe = GetSafeArea(layer);
            double w = Math.Max(240, layer.Bounds.Width - safe.Left - safe.Right - EdgeMargin * 2);
            double h = Math.Max(200, layer.Bounds.Height - safe.Top - safe.Bottom - EdgeMargin * 2);

            card.Margin = new Thickness(safe.Left + EdgeMargin, safe.Top + EdgeMargin,
                                        safe.Right + EdgeMargin, safe.Bottom + EdgeMargin);
            card.Width = w;
            card.MaxWidth = w;
            card.MaxHeight = h;
            // 关键：内容拿到的是有限宽度，长文本会换行、比例列 / 窄屏响应式才会真正生效
            view.Width = w;

            if (fitViewport)
            {
                card.Height = h;
                view.Height = Math.Max(0, h - TitleBarHeight);
            }
            else
            {
                card.Height = double.NaN;
                view.Height = double.NaN;
                contentScroll.MaxHeight = Math.Max(0, h - TitleBarHeight);
            }
        }

        EventHandler<SizeChangedEventArgs> onSizeChanged = (_, _) => ApplySize();
        layer.SizeChanged += onSizeChanged;

        var insets = TopLevel.GetTopLevel(this)?.InsetsManager;
        EventHandler<SafeAreaChangedArgs>? onSafeAreaChanged = null;
        if (insets != null)
        {
            onSafeAreaChanged = (_, _) => ApplySize();
            insets.SafeAreaChanged += onSafeAreaChanged;
        }

        ApplySize();
        Children.Add(layer);
        _layers.Add(new LayerInfo { LayerRoot = layer, View = view });
        IsVisible = true;
        // 上树后再按真实视口 / 安全区算一次：有些平台首帧 SizeChanged 不触发，
        // 否则卡片会停留在 240×200 的兜底尺寸上
        Dispatcher.UIThread.Post(ApplySize);

        EventHandler? onClose = null;
        void Cleanup()
        {
            if (!_layers.Any(l => l.View == view))
                return;
            view.CloseRequested -= onClose;
            layer.SizeChanged -= onSizeChanged;
            if (insets != null && onSafeAreaChanged != null)
                insets.SafeAreaChanged -= onSafeAreaChanged;
            _layers.RemoveAll(l => l.View == view);
            Children.Remove(layer);
            if (_layers.Count == 0)
                IsVisible = false;
        }

        onClose = (_, _) => Cleanup();
        view.CloseRequested += onClose;

        try
        {
            return await view.GetResultAsync().ConfigureAwait(true);
        }
        finally
        {
            // 兜底：结果任务以任何方式完成（正常 Close / 点标题栏关闭）后确保层被移除
            Cleanup();
        }
    }

    /// <summary>取系统安全区（刘海 / 状态栏 / Home Indicator），并钳制异常值，避免卡片被压成一条。</summary>
    private static Thickness GetSafeArea(Visual visual)
    {
        Thickness padding = TopLevel.GetTopLevel(visual)?.InsetsManager?.SafeAreaPadding ?? default;
        static double Cap(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Max(0, Math.Min(64, v));
        return new Thickness(Cap(padding.Left), Cap(padding.Top), Cap(padding.Right), Cap(padding.Bottom));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
            child.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
