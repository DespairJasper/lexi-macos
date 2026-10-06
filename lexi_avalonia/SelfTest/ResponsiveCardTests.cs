using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Lexi;

public static class ResponsiveCardTests
{
    public static async Task RunAsync(MainWindow window, List<string> report)
    {
        T C<T>(string name) where T : Control => window.FindControl<T>(name)!;
        var original = new Size(window.Width, window.Height);
        async Task Resize(double width, double height)
        {
            window.Width = width; window.Height = height;
            await Task.Delay(200);
        }
        void Check(bool result, string message)
        {
            report.Add((result ? "PASS " : "FAIL ") + message);
            if (!result) throw new Exception(message);
        }
        try
        {
            foreach (var (page, card) in new[] { ("NavLookup", "LookupEmptyCard"),
                ("NavSettings", "ThemeSettingsCard"), ("NavReview", "ReviewEmptyCard") })
            {
                C<Button>(page).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Resize(1060, 760);
                var baseline = C<Border>(card).Bounds.Size;
                var font = C<TextBox>("LookupInput").FontSize;
                var buttonFont = C<Button>("QuickWord1").FontSize;
                await Resize(1470, 860);
                var large = C<Border>(card).Bounds.Size;
                Check(large.Width > baseline.Width * 1.2 && large.Height > baseline.Height * 1.08,
                    $"{card} grows in both dimensions with the window ({baseline} -> {large})");
                Check(C<TextBox>("LookupInput").FontSize == font && C<Button>("QuickWord1").FontSize == buttonFont,
                    "resizing preserves readable text and button font sizes");
                await Resize(840, 600);
                var small = C<Border>(card).Bounds.Size;
                Check(small.Width < baseline.Width && small.Height <= baseline.Height + 2,
                    $"{card} contracts without shrinking fonts ({small})");
                var end = C<Border>(card).TranslatePoint(new Point(small.Width, 0), window)!.Value;
                Check(end.X < window.Bounds.Width, "resized card remains inside the window");
            }
        }
        finally
        {
            await Resize(original.Width, original.Height);
            C<Button>("NavLookup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
