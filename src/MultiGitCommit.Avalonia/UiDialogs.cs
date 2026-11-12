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
        content.CloseRequested += async (_, _) => win.Close(await content.GetResultAsync().ConfigureAwait(true));
        // 用户直接点窗口 × 按钮 → 以默认值完成结果任务，避免调用方永久等待
        win.Closed += (_, _) => content.CancelIfPending();

        await win.ShowDialog(owner).ConfigureAwait(true);
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
        // 移动端小屏下重置为自适应，宽度交给卡片 MaxWidth 收窄，避免内容横向被裁切。
        view.Width = double.NaN;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer
            {
                // 纵向滚动保证大视图（如 Git 环境设置）在小屏可完整查看；
                // 横向：视图内部仍有固定列宽的表格（如提交历史），溢出时允许横向滚动而不是静默裁切
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = view
            }
        };

        var layer = new Grid { Children = { dim, card } };
        layer.SizeChanged += (_, _) =>
        {
            card.MaxWidth = Math.Max(240, layer.Bounds.Width - 16);
            card.MaxHeight = Math.Max(200, layer.Bounds.Height - 16);
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
