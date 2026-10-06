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

            var overlay = stageOverlay;
            Call("ShowOverlay", overlay);
            check(overlay.IsVisible && overlay.Opacity < 0.9, $"overlay fades in instead of hard-cutting (opacity={overlay.Opacity:0.00})");
            await Task.Delay(400);
            check(overlay.Opacity >= 0.999 && overlay.Transitions == null, "overlay reaches its resting state");
            Call("HideOverlay", overlay);
            check(overlay.IsVisible, "overlay stays mounted while fading out");
            await Task.Delay(400);
            check(!overlay.IsVisible && overlay.Opacity >= 0.999, "overlay hides only after the fade-out finished");

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
