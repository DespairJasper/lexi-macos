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
                ("NavSettings", "ThemeSettingsCard"), ("NavReview", "ReviewPreviewHost") })
            {
                C<Button>(page).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (page == "NavReview")
                    Check(C<Grid>("ReviewPreviewHost").IsVisible && !C<Border>("ReviewEmptyCard").IsVisible,
                        "review navigation opens the due-word preview before a batch starts");
                await Resize(1060, 760);
                var baseline = C<Control>(card).Bounds.Size;
                var font = C<TextBox>("LookupInput").FontSize;
                var buttonFont = C<Button>("QuickWord1").FontSize;
                await Resize(1470, 860);
                var large = C<Control>(card).Bounds.Size;
                Check(large.Width > baseline.Width * 1.2 && large.Height > baseline.Height * 1.08,
                    $"{card} grows in both dimensions with the window ({baseline} -> {large})");
                Check(C<TextBox>("LookupInput").FontSize == font && C<Button>("QuickWord1").FontSize == buttonFont,
                    "resizing preserves readable text and button font sizes");
                await Resize(840, 600);
                var small = C<Control>(card).Bounds.Size;
                Check(small.Width < baseline.Width && small.Height <= baseline.Height + 2,
                    $"{card} contracts without shrinking fonts ({small})");
                var end = C<Control>(card).TranslatePoint(new Point(small.Width, 0), window)!.Value;
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
