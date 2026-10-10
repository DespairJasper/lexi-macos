using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;

namespace Lexi;

/// <summary>
/// 全应用统一的 macOS 风格动效参数与实现。
/// 只动画合成器友好的属性（Opacity / RenderTransform），由 Avalonia 的渲染时钟驱动；
/// 需要等待动画结束时只做一次定时等待，不做逐帧手写循环。
/// </summary>
internal static class Motion
{
    /// <summary>小元素淡出、收起。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(150);
    /// <summary>页面、浮层、卡片的标准进出场时长。</summary>
    public static readonly TimeSpan Standard = TimeSpan.FromMilliseconds(220);
    /// <summary>页面切换：8px 轻移与淡入，独立于浮层时长。</summary>
    public static readonly TimeSpan Page = TimeSpan.FromMilliseconds(280);
    /// <summary>大范围布局变化的收尾时长。</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(300);

    /// <summary>进场：快速起步、长尾减速（Apple decelerate）。</summary>
    public static Easing Enter { get; } = new SplineEasing(0.16, 1, 0.3, 1);

    /// <summary>退场：短促加速离场，不与用户操作争夺注意力。</summary>
    public static Easing Exit { get; } = new SplineEasing(0.45, 0, 0.9, 0.4);

    /// <summary>静止姿态（与所有过渡的起点结构保持一致，保证可插值）。</summary>
    public const string Rest = "translate(0px, 0px) scale(1)";

    /// <summary>用户开启“减少动态效果”后，全部过渡退化为即时切换。</summary>
    public static bool IsReduced(Visual? visual)
        => TopLevel.GetTopLevel(visual) is StyledElement top && top.Classes.Contains("reduce-motion");

    public static string Pose(double offsetY, double scale) => Pose(0, offsetY, scale);

    public static string Pose(double offsetX, double offsetY, double scale)
        => $"translate({offsetX.ToString(System.Globalization.CultureInfo.InvariantCulture)}px, "
         + $"{offsetY.ToString(System.Globalization.CultureInfo.InvariantCulture)}px) "
         + $"scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})";

    /// <summary>无动画地落到指定姿态，同时清空过渡，避免残留动画值覆盖本地值。</summary>
    public static void SetPose(Control control, string transform, double opacity)
    {
        control.Transitions = null;
        control.RenderTransform = TransformOperations.Parse(transform);
        control.Opacity = opacity;
    }

    /// <summary>复位到静止姿态（隐藏元素复用前调用）。</summary>
    public static void Reset(Control control) => SetPose(control, Rest, 1);

    /// <summary>
    /// 过渡到目标姿态。返回的 Task 在过渡结束后完成，便于编排下一步（如隐藏元素）。
    /// 取消时保留调用方当前应有的终值，不再回写。
    /// </summary>
    public static async Task ToPoseAsync(Control control, string transform, double opacity,
        TimeSpan duration, Easing easing, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;
        if (IsReduced(control) || duration <= TimeSpan.Zero)
        {
            SetPose(control, transform, opacity);
            return;
        }

        control.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = easing },
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = easing },
        };
        control.RenderTransform = TransformOperations.Parse(transform);
        control.Opacity = opacity;

        try { await Task.Delay(duration, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        // 过渡结束后撤掉 Transitions：终值已由本地值持有，后续布局变化保持即时。
        control.Transitions = null;
    }

    /// <summary>
    /// 过渡高度与透明度（折叠抽屉一类需要改布局的容器）。
    /// 高度变化仍按渲染时钟插值，比手写 Task.Delay 循环平滑。
    /// </summary>
    public static async Task HeightToAsync(Control control, double from, double to,
        double fromOpacity, double toOpacity, TimeSpan duration, Easing easing, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;
        if (IsReduced(control) || duration <= TimeSpan.Zero)
        {
            control.Transitions = null;
            control.Height = to;
            control.Opacity = toOpacity;
            return;
        }

        control.Transitions = null;
        control.Height = from;
        control.Opacity = fromOpacity;
        control.Transitions = new Transitions
        {
            new DoubleTransition { Property = Layoutable.HeightProperty, Duration = duration, Easing = easing },
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = easing },
        };
        control.Height = to;
        control.Opacity = toOpacity;

        try { await Task.Delay(duration, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        control.Transitions = null;
        control.Height = to;
        control.Opacity = toOpacity;
    }
}
