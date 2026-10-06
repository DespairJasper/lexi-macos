using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lexi;

public partial class MainWindow
{
    private void ConfigureMacShortcuts()
    {
        AddHandler(KeyDownEvent, (_, e) =>
        {
            var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if ((e.KeyModifiers & command) == 0 || (e.KeyModifiers & KeyModifiers.Alt) != 0) return;
            var shifted = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            // Navigation must not move an unsaved dialog to a different page.
            if (!shifted && !FocusCanNavigate) return;
            if (shifted && e.Key == Key.L) ToggleUiLanguage();
            else if (shifted && e.Key == Key.D) SettingsThemeCombo.SelectedIndex = SettingsThemeCombo.SelectedIndex == 1 ? 0 : 1;
            else if (!shifted && e.Key == Key.L)
            {
                ExitWordFocus();
                ShowPage("lookup");
                LookupInput.Focus();
                LookupInput.SelectAll();
            }
            else if (!shifted && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6)
            {
                ExitWordFocus();
                ShowPage(e.Key switch { Key.D1 => "lookup", Key.D2 => "vocab", Key.D3 => "review", Key.D4 => "settings", Key.D5 => "ielts", _ => "typing" });
            }
            else return;
            e.Handled = true;
        }, RoutingStrategies.Bubble);
    }
}
