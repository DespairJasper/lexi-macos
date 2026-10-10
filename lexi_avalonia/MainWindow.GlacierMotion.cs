using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Lexi;

public partial class MainWindow
{
    private CancellationTokenSource? _glacierPageMotion;
    private Control? _glacierAnimatedPage;

    // Call before replacing page visibility: the outgoing page always rests before reuse.
    private void GlacierCancelPageMotion()
    {
        var previous = _glacierPageMotion;
        _glacierPageMotion = null;
        previous?.Cancel();
        previous?.Dispose();
        if (_glacierAnimatedPage != null) Motion.Reset(_glacierAnimatedPage);
        _glacierAnimatedPage = null;
    }

    // Visibility and focus remain owned by ShowPage; this facade animates only the incoming page.
    private void GlacierAnimatePage(Control target)
    {
        GlacierCancelPageMotion();
        if (Motion.IsReduced(target))
        {
            Motion.Reset(target);
            return;
        }

        _glacierAnimatedPage = target;
        var source = _glacierPageMotion = new CancellationTokenSource();
        Motion.SetPose(target, Motion.Pose(8, 1), 0);
        _ = GlacierFinishPageMotionAsync(target, source, source.Token);
    }

    private async Task GlacierFinishPageMotionAsync(Control target, CancellationTokenSource source,
        CancellationToken token)
    {
        await Motion.ToPoseAsync(target, Motion.Rest, 1, Motion.Page, Motion.Enter, token);
        // A cancelled transition must never clean up or write into a newer transition.
        if (token.IsCancellationRequested || !ReferenceEquals(_glacierPageMotion, source)) return;
        Motion.Reset(target);
        _glacierAnimatedPage = null;
        _glacierPageMotion = null;
        source.Dispose();
    }
}
