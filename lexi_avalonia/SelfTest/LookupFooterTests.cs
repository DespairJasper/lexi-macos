using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Lexi;

public static class LookupFooterTests
{
    public static async Task RunAsync(MainWindow window, List<string> report, string folder)
    {
        var scroll = window.FindControl<ScrollViewer>("PageLookup")!;
        var button = window.FindControl<Button>("AddWordBtn")!;
        foreach (var size in new[] { new Size(1060, 760), new Size(840, 600) })
        {
            window.Width = size.Width; window.Height = size.Height;
            await Task.Delay(150);
            foreach (var bottom in new[] { true, false })
            {
                if (bottom) scroll.ScrollToEnd(); else scroll.Offset = default;
                await Task.Delay(150);
                var start = button.TranslatePoint(default, window)!.Value;
                var end = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                var visible = button.IsEffectivelyVisible && start.Y >= 60 && end.Y <= window.Bounds.Height - 36 && start.X >= 216 && end.X < window.Bounds.Width;
                var label = $"add-to-vocabulary fully visible after AI expansion, size={size}, bottom={bottom}; button={start}..{end}, extent={scroll.Extent}, viewport={scroll.Viewport}, offset={scroll.Offset}";
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window); bitmap.Save(Path.Combine(folder, $"lookup-footer-{size.Width}-{bottom}.png"));
                report.Add((visible ? "PASS " : "FAIL ") + label);
                if (!visible) throw new Exception(label);
                if (bottom)
                {
                    var drawer = window.FindControl<Border>("AiDrawerSlot")!;
                    var drawerEnd = drawer.TranslatePoint(new Point(0, drawer.Bounds.Height), scroll)!.Value.Y;
                    if (drawerEnd > scroll.Bounds.Height + 1) throw new Exception("Last expansion content remains clipped at scroll end");
                    var scrollEnd = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)!.Value.Y;
                    if (start.Y < scrollEnd) throw new Exception("Fixed action bar overlaps the scroll viewport");
                    report.Add("PASS complete expansion is reachable and fixed action bar does not overlay content");
                }
            }
        }
    }
}
