using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Lexi;

public static class LanguageTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Language tests require LEXI_DATA_DIR.");
        var log = new List<string>();
        var exit = 0;
        T C<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception("Missing " + name);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Snapshot(string name)
        {
            await Task.Delay(150);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(folder, name + ".png"));
        }
        try
        {
            var phase = Environment.GetEnvironmentVariable("LEXI_LANGUAGE_TEST_PHASE");
            if (phase is "persist-en" or "reload-en")
            {
                if (phase == "persist-en")
                {
                    Check(C<Button>("LookupBtn").Content?.ToString() == "查询 ↵", "first process starts in Chinese");
                    Click(C<Button>("LanguageToggleBtn"));
                    Click(C<Button>("SettingsSaveBtn"));
                    using var store = new VocabularyService();
                    Check(store.LoadSettings().UiLanguage == "en", "first process saves English from actual language button");
                }
                else
                {
                    Check(C<Button>("LookupBtn").Content?.ToString() == "Look up ↵", "second fresh process reloads saved English controls");
                    Check(C<ComboBox>("SettingsLanguageCombo").SelectedIndex == 1, "second fresh process restores English selection");
                    window.SetUiLanguage("zh-CN");
                    Check(C<Button>("LookupBtn").Content?.ToString() == "查询 ↵", "second fresh process can switch back to Chinese");
                }
                return;
            }
            Check(new AppSettings().Theme == "Light" && new AppSettings().UiLanguage == "zh-CN", "new settings default to light Chinese UI");
            Check(C<Button>("LookupBtn").Content?.ToString() == "查询 ↵", "default Chinese lookup caption");
            using (var store = new VocabularyService())
            {
                store.AddWord("languagefixture", "/fixture/", "原始释义 · 不允许自动翻译", "Original definition stays exactly as written.");
                var entry = store.GetAllWords().Single(w => w.Word == "languagefixture");
                store.SaveArchive(entry.Id, entry.Translation, "用户备注：书籍、学习中与已掌握都是原文。", entry.Archive, null);
            }
            Call("RefreshWords");
            Click(C<Button>("NavVocab"));
            var item = C<ListBox>("VocabListBox").ItemsSource!.Cast<WordItem>().Single(w => w.Word == "languagefixture");
            Call("OpenArchiveEditor", item);
            Call("RenderExpansion", new LlmResult
            {
                Examples = [new("My original example remains unchanged.", "原始例句翻译保留。")],
                Synonyms = ["vocabulary"], Antonyms = ["silence"]
            });
            var notes = C<TextBox>("ArchiveNotesInput").Text;
            var meaning = C<TextBox>("DialogEditInput").Text;
            window.ToggleUiLanguage();
            await Task.Delay(150);
            Check(C<Button>("LookupBtn").Content?.ToString() == "Look up ↵", "Chinese to English updates existing controls immediately");
            Check(C<Button>("NavVocab").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Vocabulary"), "English navigation resource updates");
            Check(C<Button>("DialogEditConfirmBtn").Content?.ToString() == "Save" && C<TextBlock>("DialogEditTitle").Text == "languagefixture · Vocabulary entry", "open editor labels update in place");
            Check(C<TextBox>("ArchiveNotesInput").Text == notes && C<TextBox>("DialogEditInput").Text == meaning, "language change preserves unsaved note and meaning text");
            Check(C<ComboBox>("VocabStatusFilter").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "All statuses") && C<ComboBox>("BatchActionCombo").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Batch actions…"), "selected combo captions update without changing selections");
            Check(ToolTip.GetTip(C<WrapPanel>("AiResultSynonymsPanel").Children.OfType<Button>().Single())?.ToString() == "Look up 'vocabulary' offline", "existing generated word tooltips switch language immediately");
            Check(C<StackPanel>("AiResultExamplesContainer").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "My original example remains unchanged.") && C<StackPanel>("AiResultExamplesContainer").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "原始例句翻译保留。"), "bilingual AI example content stays exactly as written");
            Check(C<TextBlock>("SelectedCountText").Text == "0 selected", "dynamic selection count is English");
            Check(C<TextBlock>("VocabPageText").Text!.Contains("words"), "dynamic pagination is English");
            // 旧五阶段文案已按规格书 §2 定向更新为"待复习 / To review"（stage 只作兼容投影，
            // 不再承诺"五阶段学完"）。三条域内映射都要覆盖，且域数据本身由下一条断言保证不变。
            Check(UiText.Value(item.StatusLabel) == "To review" && UiText.Value(item.StageDescription) == "To review"
                && UiText.Value("全部完成") == "Reviews paused" && UiText.Value("完成学习") == "Learning complete",
                "domain status converter localizes display without changing domain data");
            Check(item.Status == "learning" && item.StatusLabel == "阶段 1 / 5", "domain status values stay unchanged");
            using (var store = new VocabularyService()) Check(store.LoadSettings().UiLanguage == "en", "English persists independently of API settings");
            await Snapshot("language-en-editor");
            Click(C<Button>("DialogEditCancelBtn"));
            Click(C<Button>("NavSettings"));
            Check(C<Button>("BackupNowBtn").Content?.ToString() == "Back up now" && C<CheckBox>("SettingsRememberKeyBox").Content!.ToString()!.Contains("macOS Keychain"), "English backup and Keychain labels");
            Check(C<TextBlock>("DiagnosticsText").Text!.Contains("Layout:") && C<TextBlock>("DataIdentityText").Text!.Contains("Vocabulary"), "English diagnostics and data summary");
            Click(C<Button>("SettingsSaveBtn"));
            using (var store = new VocabularyService()) Check(store.LoadSettings().UiLanguage == "en" && C<Button>("LookupBtn").Content?.ToString() == "Look up ↵", "saving API settings preserves English language and current English controls");
            await Snapshot("language-en-settings");
            Click(C<Button>("NavReview"));
            var englishProgress = C<TextBlock>("ReviewRemainingText").Text ?? "";
            Check(!englishProgress.Any(c => c >= 0x4E00 && c <= 0x9FFF)
                && ((TextBlock)((StackPanel)C<Button>("ReviewRememberBtn").Content!).Children[0]).Text == "Know it  Q"
                && ((TextBlock)((StackPanel)C<Button>("ReviewUnfamiliarBtn").Content!).Children[0]).Text == "Forgot  E",
                "English review labels and live count");
            // A new window creates fresh controls and reads the saved language from the database.
            var reopened = new MainWindow();
            Check(reopened.FindControl<Button>("LookupBtn")!.Content!.ToString() == "Look up ↵", "recreated window reloads persisted English language");
            reopened.ForceClose();
            window.SetUiLanguage("en"); // same language is a harmless no-op
            Click(C<Button>("LanguageToggleBtn"));
            await Task.Delay(100);
            Check(C<Button>("LookupBtn").Content!.ToString() == "查询 ↵" && C<Button>("BackupNowBtn").Content!.ToString() == "立即备份", "English to Chinese updates controls through actual toggle button");
            Check(C<TextBlock>("ReviewRemainingText").Text!.Contains("今日无到期单词")
                && ((TextBlock)((StackPanel)C<Button>("ReviewRememberBtn").Content!).Children[0]).Text == "认识  Q",
                "dynamic review labels return to Chinese");
            Check(C<Button>("ThemeToggleBtn").Content!.ToString() == "切换深色", "light default and localized theme toggle survive language switching");
            using (var store = new VocabularyService())
            {
                var restored = store.GetAllWords().Single(w => w.Word == "languagefixture");
                Check(restored.Translation == meaning && restored.Notes == notes, "persisted original meaning and notes never translated");
                Check(store.LoadSettings().UiLanguage == "zh-CN", "Chinese language is saved after switching back");
            }
            await Snapshot("language-zh-review");
        }
        catch (Exception ex) { exit = 1; log.Add("FAIL " + ex); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, Environment.GetEnvironmentVariable("LEXI_LANGUAGE_TEST_PHASE") is { Length: > 0 } phaseName ? "language-" + phaseName + "-result.txt" : "language-result.txt"), log);
            window.ForceClose();
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
        }
    }
}
