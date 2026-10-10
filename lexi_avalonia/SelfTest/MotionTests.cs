using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Lexi;

// 过渡动画回归：页面交叉淡入淡出必须真的发生、结束后不留动画残值；
// 打开“减少动态效果”时必须退化为即时切换。
public static class MotionTests
{
    public static async Task RunAsync(MainWindow w, Action<bool, string> check)
    {
        T C<T>(string name) where T : Control => w.FindControl<T>(name)!;
        void Click(string name) => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        object? Call(string name, params object?[] args)
            => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, args);
        var reduce = C<CheckBox>("ReduceMotionBox");
        var host = C<Grid>("LookupPageHost");
        var vocab = C<Grid>("PageVocab");
        var review = C<Grid>("PageReview");
        var stageOverlay = C<Border>("DialogStageOverlay");
        var originalReduce = reduce.IsChecked;
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        void Save(string name)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            using var bmp = new RenderTargetBitmap(new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height), new Vector(96, 96));
            bmp.Render(w); bmp.Save(Path.Combine(folder!, name + ".png"));
        }
        try
        {
            reduce.IsChecked = false;
            Click("NavLookup");
            await Task.Delay(400);

            Click("NavVocab");
            check(vocab.IsVisible && !host.IsVisible, "page switch retires the previous page immediately");
            check(vocab.Opacity < 0.9, $"incoming page fades in instead of hard-cutting (opacity={vocab.Opacity:0.00})");
            Save("motion-page-mid-fade");
            await Task.Delay(450);
            check(!host.IsVisible && Math.Abs(vocab.Opacity - 1) < 0.001 && vocab.Transitions == null,
                "page switch settles at the final pose without leftover animation values");

            Click("NavReview");
            await Task.Delay(35);
            Click("NavVocab");
            await Task.Delay(35);
            Click("NavLookup");
            await Task.Delay(400);
            check(host.IsVisible && !review.IsVisible && !vocab.IsVisible && host.Opacity >= 0.999,
                "rapid page navigation settles only the last requested page");
            check(host.Transitions == null && review.Transitions == null && vocab.Transitions == null,
                "cancelled page transitions leave reused pages at rest");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Motion.ToPoseAsync(host, Motion.Pose(20, .9), .2, Motion.Page, Motion.Enter, cancelled.Token);
                check(host.Opacity >= .999 && host.RenderTransform?.Value.IsIdentity == true,
                    "an already cancelled transition cannot alter the current page");
            }

            var overlay = stageOverlay;
            Call("ShowOverlay", overlay);
            check(overlay.IsVisible && overlay.Opacity < 0.9, $"overlay fades in instead of hard-cutting (opacity={overlay.Opacity:0.00})");
            await Task.Delay(400);
            check(overlay.Opacity >= 0.999 && overlay.Transitions == null, "overlay reaches its resting state");
            Call("HideOverlay", overlay);
            check(overlay.IsVisible, "overlay stays mounted while fading out");
            await Task.Delay(400);
            check(!overlay.IsVisible && overlay.Opacity >= 0.999, "overlay hides only after the fade-out finished");

            Call("ShowOverlay", overlay);
            await Task.Delay(35);
            Call("HideOverlay", overlay);
            await Task.Delay(35);
            Call("ShowOverlay", overlay);
            await Task.Delay(400);
            check(overlay.IsVisible && overlay.Opacity >= .999 && overlay.Transitions == null,
                "reopening a closing overlay cancels stale hide completion");
            check(overlay.Child is Control settledCard && settledCard.Opacity >= .999
                && settledCard.Transitions == null && settledCard.RenderTransform?.Value.IsIdentity == true,
                "reopened overlay card has no leftover fade or scale");
            Call("HideOverlay", overlay);
            await Task.Delay(220);

            reduce.IsChecked = true;
            Click("NavReview");
            check(review.IsVisible && review.Opacity >= 0.999 && !vocab.IsVisible,
                "reduce-motion switches pages instantly with no intermediate pose");
            await Task.Delay(120);
            check(review.Transitions == null && !host.IsVisible, "reduce-motion leaves no transitions behind");
        }
        finally
        {
            reduce.IsChecked = originalReduce;
            Click("NavLookup");
            await Task.Delay(350);
        }
    }
}
