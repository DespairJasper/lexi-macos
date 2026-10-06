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
        Motion.SetPose(overlay, Motion.Rest, 0);
        if (card != null) Motion.SetPose(card, Motion.Pose(0, 0.96), 0);

        var cts = new CancellationTokenSource();
        _overlayAnimations[overlay] = cts;
        _ = Motion.ToPoseAsync(overlay, Motion.Rest, 1, Motion.Fast, Motion.Enter, cts.Token);
        if (card != null) _ = Motion.ToPoseAsync(card, Motion.Rest, 1, Motion.Standard, Motion.Enter, cts.Token);
    }

    private void HideOverlay(Border overlay)
    {
        if (!overlay.IsVisible) return;
        CancelOverlayAnimation(overlay);
        var cts = new CancellationTokenSource();
        _overlayAnimations[overlay] = cts;
        _ = HideOverlayAsync(overlay, overlay.Child as Control, cts.Token);
    }

    private async Task HideOverlayAsync(Border overlay, Control? card, CancellationToken ct)
    {
        if (card != null) _ = Motion.ToPoseAsync(card, Motion.Pose(0, 0.98), 0, Motion.Fast, Motion.Exit, ct);
        await Motion.ToPoseAsync(overlay, Motion.Rest, 0, Motion.Fast, Motion.Exit, ct);
        if (ct.IsCancellationRequested) return;
        overlay.IsVisible = false;
        Motion.Reset(overlay);
        if (card != null) Motion.Reset(card);
    }

    private void CancelOverlayAnimation(Control overlay)
    {
        if (_overlayAnimations.Remove(overlay, out var previous)) previous.Cancel();
    }
}
