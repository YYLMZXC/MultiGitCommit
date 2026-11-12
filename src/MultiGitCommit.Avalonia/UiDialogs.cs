using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
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
    /// 视图是否自带纵向滚动区、并希望按移动端覆盖层视口尺寸布局（桌面端 Window 路径不读此属性）。
    /// 为 true 时：卡片宽度固定为屏幕宽度且禁用横向滚动（长文本自动换行、窄屏布局生效），
    /// 卡片高度固定为屏幕高度并传给视图，由视图内部的 ScrollViewer 滚动，底部按钮栏始终可见。
    /// 默认 false：沿用卡片 ScrollViewer 双向滚动（适合小视图 / 固定列宽表格）。
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
            throw new InvalidOperationException("无法确定对话框宿主：anchor 可视树中既没有 Window 也没有覆盖层。");

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
        var dim = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) };

        // 视图根上的固定 Width 是给桌面端 SizeToContent 窗口撑尺寸用的（桌面路径不经过这里）；
        // 移动端小屏下先重置，随后由卡片 / 视口宽度重新约束，避免整体超宽被裁切。
        view.Width = double.NaN;

        // 自带滚动区的大视图（如 Git 环境设置）按视口尺寸布局：
        // 横向 Auto 会用「无限宽度」测量内容，TextWrapping 全部失效，页面整体超宽、右侧被裁切，
        // 视图里的窄屏响应式也因 Bounds.Width 超阈值而不生效。
        // 这类视图改为禁用横向滚动并把宽度约束到屏幕宽度；高度固定后由视图内部的
        // ScrollViewer 滚动，底部【确定/取消】按钮栏始终可见。
        bool fitViewport = view.FitsOverlayViewport;
        if (fitViewport)
        {
            // 给桌面窗口撑尺寸用的 MinHeight / MinWidth 在移动端会顶破视口，必须清掉
            view.MinHeight = 0;
            view.MinWidth = 0;
        }

        // 首帧尺寸取覆盖层当前边界（通常已完成布局），随后由 layer.SizeChanged 跟随屏幕旋转 / 窗口变化
        double initialW = Math.Max(240, Bounds.Width - 16);
        double initialH = Math.Max(200, Bounds.Height - 16);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer
            {
                // 纵向滚动保证小视图（未声明视口布局）也能完整查看；
                // 横向：固定列宽表格（如提交历史）仍允许横向滚动，避免静默裁切
                HorizontalScrollBarVisibility = fitViewport ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = view
            }
        };
        if (fitViewport)
        {
            card.Width = initialW;
            card.Height = initialH;
            // 显式固定视图宽度：即使 ScrollViewer 以「无限宽度」测量内容，
            // 内部 TextWrapping 与窄屏响应式也会按视口宽度生效，不会整体撑宽
            view.Width = initialW;
            view.Height = initialH;
        }

        var layer = new Grid { Children = { dim, card } };
        layer.SizeChanged += (_, _) =>
        {
            double w = Math.Max(240, layer.Bounds.Width - 16);
            double h = Math.Max(200, layer.Bounds.Height - 16);
            card.MaxWidth = w;
            card.MaxHeight = h;
            if (fitViewport)
            {
                card.Width = w;
                card.Height = h;
                view.Width = w;
                view.Height = h;
            }
        };

        Children.Add(layer);
        _layers.Add(new LayerInfo { LayerRoot = layer, View = view });
        IsVisible = true;

        EventHandler? onClose = null;
        onClose = (_, _) =>
        {
            view.CloseRequested -= onClose;
            _layers.RemoveAll(l => l.View == view);
            Children.Remove(layer);
            if (_layers.Count == 0)
                IsVisible = false;
        };
        view.CloseRequested += onClose;

        try
        {
            return await view.GetResultAsync().ConfigureAwait(true);
        }
        finally
        {
            // 兜底：结果任务以任何方式完成（正常 Close）后确保层被移除
            if (_layers.Any(l => l.View == view))
            {
                view.CloseRequested -= onClose;
                _layers.RemoveAll(l => l.View == view);
                Children.Remove(layer);
                if (_layers.Count == 0)
                    IsVisible = false;
            }
        }
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
