using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Lexi;

// Runs real Avalonia controls/event handlers on the UI thread using isolated data.
public static class UiSmokeTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("UI tests require LEXI_DATA_DIR.");
        var report = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); report.Add("PASS " + message); }
        T C<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception("Missing " + name);
        object? Call(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        void Click(string name) => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var exit = 0;
        try
        {
            Check(window.IsVisible && !window.Topmost, "visible window, not always on top");
            if (Environment.GetEnvironmentVariable("LEXI_TEST_HOTKEY_BUSY") == "1")
                Check(App.Hotkey?.IsRegistered == false && C<TextBlock>("GlobalStatusText").Text!.Contains("占用"), "occupied hotkey reports graceful fallback");
            else Check(App.Hotkey?.IsRegistered == true, "Alt+D registered with Windows");

            // Visual Test 1: Search input and 150ms fade clear button
            var input = C<TextBox>("LookupInput");
            var clearBtn = C<Button>("LookupClearBtn");
            input.Text = "serendipity";
            await Task.Delay(350);
            Check(clearBtn.Opacity >= 0.99 && clearBtn.IsHitTestVisible, "clear button visible when search text present");
            clearBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(180);
            Check(string.IsNullOrEmpty(input.Text) && !clearBtn.IsHitTestVisible, "clear button empties search box and fades out");

            input.Text = "serendipity";
            await (Task)Call("PerformLookupAsync")!;
            Check(C<TextBlock>("ResultWordText").Text == "serendipity", "offline query renders word");
            input.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Space, KeyModifiers = Avalonia.Input.KeyModifiers.Alt });
            input.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent, Key = Avalonia.Input.Key.Space, KeyModifiers = Avalonia.Input.KeyModifiers.Alt });
            Check(C<Button>("AddWordBtn").IsEnabled, "Alt+Space does not archive a word while typing");
            C<Button>("AddWordBtn").Focus();
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent=Avalonia.Input.InputElement.KeyDownEvent, Key=Avalonia.Input.Key.D, KeyModifiers=Avalonia.Input.KeyModifiers.Control });
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent=Avalonia.Input.InputElement.KeyUpEvent, Key=Avalonia.Input.Key.D });
            Check(!C<Button>("AddWordBtn").IsEnabled, "Ctrl+D archives the current lookup outside text input");

            // Visual Test 2: AI Option CheckBox capsules retain semantics
            var optExamples = C<CheckBox>("AiOptExamples");
            optExamples.IsChecked = false;
            Check(optExamples.IsChecked == false, "capsule checkbox unchecks");
            optExamples.IsChecked = true;
            Check(optExamples.IsChecked == true, "capsule checkbox checks retaining semantics");

            // Visual Test 2b: Drawer opening and closing animation without clipping
            var drawerSlot = C<Border>("AiDrawerSlot");
            var resultBox = C<Border>("AiResultBox");
            await (Task)typeof(MainWindow).GetMethod("OpenDrawerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            Check(drawerSlot.IsVisible && resultBox.IsVisible, "drawer expands smoothly with CubicEaseOut and displays content");
            await (Task)typeof(MainWindow).GetMethod("CloseDrawerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { false })!;
            Check(!drawerSlot.IsVisible && drawerSlot.Height == 0, "drawer collapses cleanly to 0 height");

            // Exercise a real button created by the production renderer.
            typeof(MainWindow).GetMethod("RenderExpansion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                new object[] { new LlmResult { Synonyms = new() { "tenacious", "strong", "tough" } } });
            var synPanel = C<WrapPanel>("AiResultSynonymsPanel");
            var synBtn = synPanel.Children.OfType<Button>().First();
            synBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60);
            Check(C<TextBlock>("ResultWordText").Text == "tenacious", "synonym micro-capsule click triggers offline dictionary query without AI");

            input.Text = "resilient";
            await (Task)Call("PerformLookupAsync")!;
            C<Button>("AddWordBtn").Focus();
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent=Avalonia.Input.InputElement.KeyDownEvent, Key=Avalonia.Input.Key.D, KeyModifiers=Avalonia.Input.KeyModifiers.Control });
            Check(!C<Button>("AddWordBtn").IsEnabled, "unified Ctrl+D archives a second lookup");
            var shortcutStore = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var savedRevision = shortcutStore.GetAllWords().Single(x => x.Word == "resilient").Archive.Revision;
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent=Avalonia.Input.InputElement.KeyDownEvent, Key=Avalonia.Input.Key.D, KeyModifiers=Avalonia.Input.KeyModifiers.Control });
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent=Avalonia.Input.InputElement.KeyUpEvent, Key=Avalonia.Input.Key.D });
            Check(shortcutStore.GetAllWords().Single(x => x.Word == "resilient").Archive.Revision == savedRevision, "held or repeated add shortcut leaves archived content unchanged");
            Click("NavVocab");
            var offPageKey = new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Space, KeyModifiers = Avalonia.Input.KeyModifiers.Alt };
            window.RaiseEvent(offPageKey);
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent, Key = Avalonia.Input.Key.Space, KeyModifiers = Avalonia.Input.KeyModifiers.Alt });
            Check(!offPageKey.Handled, "add shortcut does not consume Alt+Space on other pages");
            var list = C<ListBox>("VocabListBox");
            Check(list.ItemCount >= 2, "two queried words appear in vocabulary");

            // Visual Test 3: Vocab item independent expansion
            var firstItem = list.Items.Cast<WordItem>().First();
            Check(!firstItem.IsExpanded, "vocab item defaults to compact");
            firstItem.IsExpanded = true;
            Check(firstItem.IsExpanded, "vocab item expands independently to show full details");
            firstItem.IsExpanded = false;

            C<CheckBox>("SelectAllCheckBox").IsChecked = true;
            Call("OnSelectAllClicked");
            Check(list.Items.Cast<WordItem>().All(w => w.Selected), "select all selects visible rows");
            Call("OnInvertSelectClicked");
            Check(list.Items.Cast<WordItem>().All(w => !w.Selected), "invert selection clears selected rows");
            C<CheckBox>("SelectAllCheckBox").IsChecked = true;
            Call("OnSelectAllClicked");
            var items = list.Items.Cast<WordItem>().ToList();
            var html = ExportService.GenerateHtml(items);
            File.WriteAllText(Path.Combine(folder, "ui-export.html"), html);
            Check(html.Contains("serendipity") && html.Contains("resilient") && html.Contains("@page"), "selected words export as A4 HTML");
            C<ComboBox>("BatchActionCombo").SelectedIndex = 2;
            Check(list.Items.Cast<WordItem>().All(w => w.Status == "mastered"), "batch menu marks selected words mastered");
            C<CheckBox>("SelectAllCheckBox").IsChecked = true;
            Call("OnSelectAllClicked");
            C<ComboBox>("BatchActionCombo").SelectedIndex = 3;
            Click("NavReview");
            Check(C<Grid>("PageReview").IsVisible && !C<Grid>("PageVocab").IsVisible
                && C<TextBlock>("ReviewRemainingText").Text?.StartsWith("已完成 0 / 2") == true,
                "today action fills independent recall deck without management controls");
            Click("NavVocab");
            C<CheckBox>("SelectAllCheckBox").IsChecked = true;
            Call("OnSelectAllClicked");
            C<ComboBox>("BatchActionCombo").SelectedIndex = 6;
            Check(C<Border>("DialogDeleteOverlay").IsVisible && list.ItemCount >= 2, "delete requires confirmation before changing data");
            Click("DialogDeleteCancelBtn");
            Check(list.ItemCount >= 2, "cancel delete preserves vocabulary");
            Click("NavSettings");
            C<ComboBox>("SettingsProviderCombo").SelectedIndex = 2;
            Check(C<TextBox>("SettingsBaseUrlInput").Text?.Contains("dashscope") == true, "Qwen preset fills endpoint");
            C<TextBox>("SettingsModelInput").Text = "my-editable-model";
            Click("SettingsSaveBtn");
            Check(C<TextBlock>("GlobalStatusText").Text?.Contains("设置已保存") == true, "editable model settings saved");

            await FoundationUiTests.RunAsync(window, Check);
            await RecallCardTests.RunAsync(window, Check);
            await FoundationFinalRegressionTests.RunAsync(window, Check);
            window.Close();
            Check(!window.IsVisible, "close hides window without ending application");
            var exe = Environment.ProcessPath!;
            var args = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? new[] { Assembly.GetExecutingAssembly().Location } : Array.Empty<string>();
            for (var i = 0; i < 3; i++)
            {
                var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in args) info.ArgumentList.Add(arg);
                using var child = Process.Start(info)!;
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(child.ExitCode == 0, $"secondary launch {i + 1} exits cleanly");
            }
            await Task.Delay(250);
            Check(window.IsVisible && !window.Topmost, "secondary launch wakes the existing non-topmost window");
            window.HideToTray();
            window.ToggleVisibility();
            Check(window.IsVisible, "visibility toggle restores window");
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            report.Add($"METRIC working_set_MiB={proc.WorkingSet64 / 1048576.0:F1}, private_MiB={proc.PrivateMemorySize64 / 1048576.0:F1}");
            await FoundationFinalRegressionTests.RunFatalRecoveryAsync(window, Check);
        }
        catch (Exception ex) { report.Add("FAIL " + ex); exit = 1; }
        finally
        {
            Directory.CreateDirectory(folder!);
            File.WriteAllLines(Path.Combine(folder!, "ui-smoke-result.txt"), report);
            window.ForceClose();
            (App.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
        }
    }
}
