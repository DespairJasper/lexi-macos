using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Lexi;

// Exercise the effective template paint, including inherited Fluent state setters.
// Checking only the outer Border missed the inner blue selection in 0.2.4.
public static class InteractionStyleTests
{
    public static async Task RunAsync(MainWindow window, List<string> report, string folder)
    {
        var errors = new List<string>();
        void Check(bool ok, string text)
        {
            if (!ok) errors.Add(text);
            report.Add((ok ? "PASS " : "FAIL ") + text);
        }
        bool Clear(IBrush? brush) => brush == null || brush is ISolidColorBrush color && color.Color.A == 0;
        var list = window.FindControl<ListBox>("VocabListBox")!;
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            list.SelectedIndex = -1;
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
            var word = (WordItem)row.DataContext!;
            var initialChecked = word.Selected;
            var pointer = new Pointer(123, PointerType.Mouse, true);
            var point = row.TranslatePoint(new Point(row.Bounds.Width - 100, 8), window)!.Value;
            row.RaiseEvent(new PointerPressedEventArgs(row, pointer, window, point, 1,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            row.RaiseEvent(new PointerReleasedEventArgs(row, pointer, window, point, 2,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Check(list.SelectedIndex == 0, $"{theme.Key} actual blank-row pointer event exercises selected state");
            foreach (var hover in new[] { false, true })
            {
                ((IPseudoClasses)row.Classes).Set(":pointerover", hover);
                row.Focus(NavigationMethod.Tab);
                await Task.Delay(220);
                var presenter = row.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                Check(Clear(presenter.Background), $"{theme.Key} selected row inner presenter has no fill (hover={hover}), actual={presenter.Background}");
                Check(word.Selected == initialChecked, $"{theme.Key} row focus/selection preserves batch checkbox");
            }
            ((IPseudoClasses)row.Classes).Set(":pointerover", false);
            var nav = window.FindControl<Button>("NavVocab")!;
            Check(nav.Focus(NavigationMethod.Tab) && nav.IsKeyboardFocusWithin, $"{theme.Key} button remains keyboard focusable");
            Check(nav.FocusAdorner == null && nav.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ButtonSurface").CornerRadius.TopLeft > 0,
                $"{theme.Key} keyboard focus uses the rounded surface instead of a rectangular adorner");
            var primary = window.FindControl<Button>("ExportPrintBtn")!;
            Check(primary.Focus(NavigationMethod.Tab), $"{theme.Key} primary button accepts keyboard focus");
            await Task.Delay(240);
            var primarySurface = primary.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ButtonSurface");
            Check(primarySurface.BoxShadow.Equals(window.FindResource(theme, "FocusGlowShadow")),
                $"{theme.Key} primary button retains a visible keyboard focus glow");
            // Include sidebar, action, title-bar, clear and generated tag buttons.
            var buttons = window.FindControl<Border>("RootWindowBorder")!.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button)
                && b.GetVisualDescendants().OfType<Border>().Any(border => border.Name == "ButtonSurface")).ToArray();
            foreach (var state in new[] { ":pointerover", ":pressed", ":focus-visible" })
            {
                foreach (var button in buttons) ((IPseudoClasses)button.Classes).Set(state, true);
                await Task.Delay(240);
                foreach (var button in buttons)
                {
                    var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");
                    if (presenter == null) continue;
                    Check(Clear(presenter.Background) || presenter.CornerRadius == button.CornerRadius,
                        $"{theme.Key} {button.Name ?? button.Classes.ToString()} {state} has no square inner paint ({presenter.Background}, radius={presenter.CornerRadius})");
                }
                using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bmp.Render(window); bmp.Save(Path.Combine(folder, $"interaction-{theme.Key}-{state[1..]}.png"));
                foreach (var button in buttons) ((IPseudoClasses)button.Classes).Set(state, false);
            }
        }
        if (errors.Count > 0) throw new Exception("Interaction style regressions: " + string.Join("; ", errors));
    }
}
