using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Lexi;

// 浮层（编辑档案 / 恢复备份 / 调整阶段 / 删除确认）的进出场过渡。
// 打开：遮罩淡入 + 卡片轻微放大；关闭：快速淡出后才隐藏，避免硬切。
public partial class MainWindow
{
    private readonly Dictionary<Control, CancellationTokenSource> _overlayAnimations = [];

    private void ShowOverlay(Border overlay)
    {
        CancelOverlayAnimation(overlay);
        if (overlay.IsVisible)
        {
            Motion.Reset(overlay);
            if (overlay.Child is Control alreadyShown) Motion.Reset(alreadyShown);
            return;
        }

        var card = overlay.Child as Control;
        overlay.IsVisible = true;
        if (Motion.IsReduced(overlay))
        {
            Motion.Reset(overlay);
            if (card != null) Motion.Reset(card);
            return;
        }
        Motion.SetPose(overlay, Motion.Rest, 0);
        if (card != null) Motion.SetPose(card, Motion.Pose(0, 0.97), 0);

        var cts = new CancellationTokenSource();
        _overlayAnimations[overlay] = cts;
        _ = ShowOverlayAsync(overlay, card, cts, cts.Token);
    }

    private async Task ShowOverlayAsync(Border overlay, Control? card, CancellationTokenSource source,
        CancellationToken token)
    {
        var backdrop = Motion.ToPoseAsync(overlay, Motion.Rest, 1, Motion.Standard, Motion.Enter, token);
        var surface = card == null ? Task.CompletedTask
            : Motion.ToPoseAsync(card, Motion.Rest, 1, Motion.Standard, Motion.Enter, token);
        await Task.WhenAll(backdrop, surface);
        CompleteOverlayAnimation(overlay, source, token);
    }

    private void HideOverlay(Border overlay)
    {
        if (!overlay.IsVisible) return;
        CancelOverlayAnimation(overlay);
        if (Motion.IsReduced(overlay))
        {
            overlay.IsVisible = false;
            Motion.Reset(overlay);
            if (overlay.Child is Control content) Motion.Reset(content);
            return;
        }
        var cts = new CancellationTokenSource();
        _overlayAnimations[overlay] = cts;
        _ = HideOverlayAsync(overlay, overlay.Child as Control, cts, cts.Token);
    }

    private async Task HideOverlayAsync(Border overlay, Control? card, CancellationTokenSource source,
        CancellationToken ct)
    {
        var surface = card == null ? Task.CompletedTask
            : Motion.ToPoseAsync(card, Motion.Pose(0, 0.98), 0, Motion.Fast, Motion.Exit, ct);
        var backdrop = Motion.ToPoseAsync(overlay, Motion.Rest, 0, Motion.Fast, Motion.Exit, ct);
        await Task.WhenAll(surface, backdrop);
        if (ct.IsCancellationRequested) return;
        overlay.IsVisible = false;
        Motion.Reset(overlay);
        if (card != null) Motion.Reset(card);
        CompleteOverlayAnimation(overlay, source, ct);
    }

    private void CompleteOverlayAnimation(Control overlay, CancellationTokenSource source,
        CancellationToken token)
    {
        if (token.IsCancellationRequested || !_overlayAnimations.TryGetValue(overlay, out var current)
            || !ReferenceEquals(source, current)) return;
        _overlayAnimations.Remove(overlay);
        source.Dispose();
    }

    private void CancelOverlayAnimation(Control overlay)
    {
        if (_overlayAnimations.Remove(overlay, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
    }
}
