using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Lexi;

public static class VisualAcceptanceTests
{
    public static async Task RunAsync(MainWindow w)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        Directory.CreateDirectory(folder);
        var log = new List<string>();
        var exit = 0;
        T C<T>(string name) where T : Control => w.FindControl<T>(name) ?? throw new Exception("Missing " + name);
        object? Call(string name, params object[] args) => (typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new Exception("Missing handler " + name)).Invoke(w, args);
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Snapshot(string name)
        {
            await Task.Delay(350);
            using var bmp = new RenderTargetBitmap(new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height), new Vector(96, 96));
            bmp.Render(w);
            bmp.Save(Path.Combine(folder, name + ".png"));
        }
        try
        {
            Check(C<CheckBox>("AiOptPhrases") != null, "lookup exposes optional phrases module");
            Check(!C<Border>("LookupActionBar").IsEffectivelyVisible, "lookup action bar hidden before a dictionary result exists");
            var themeToggle = C<Button>("ThemeToggleBtn");
            var previousTheme = w.RequestedThemeVariant;
            Click(themeToggle);
            Check(themeToggle.IsEffectivelyVisible && w.RequestedThemeVariant != previousTheme, "visible title-bar theme button switches theme");
            using (var store = new VocabularyService()) Check(store.LoadSettings().Theme == w.RequestedThemeVariant!.Key.ToString(), "title-bar theme button persists without API settings");
            Click(C<Button>("NavSettings"));
            await Task.Delay(100);
            var themeCard = C<Border>("ThemeSettingsCard");
            Check(themeCard.IsEffectivelyVisible && themeCard.TranslatePoint(new Point(), C<ScrollViewer>("PageSettings"))!.Value.Y < 200, "theme settings visible at top of settings page");
            await Snapshot("theme-settings");
            Click(C<Button>("NavLookup"));
            var input = C<TextBox>("LookupInput");
            var clear = C<Button>("LookupClearBtn");
            Check(!clear.IsHitTestVisible && clear.Opacity < .01, "empty search starts with hidden non-interactive clear button");
            input.Text = "lexicon";
            await (Task)Call("PerformLookupAsync")!;
            Check(C<TextBlock>("ResultWordText").Text == "lexicon", "lookup still resolves offline");
            var result = new LlmResult
            {
                Examples = [new("Reading every day helps you build a richer lexicon.", "每天阅读，有助于丰富你的词汇。"), new("This little notebook is my personal lexicon.", "这本小笔记本就是我的私人词库。"), new("A shared lexicon makes it easier for people from different backgrounds to understand one another.", "共同的词汇让来自不同背景的人更容易理解彼此。")],
                Synonyms = ["vocabulary", "dictionary", "glossary"],
                Antonyms = ["silence", "wordlessness"],
                Phrases = [new("mental lexicon", "心理词库"), new("expand your lexicon", "扩充词汇")]
            };
            Call("RenderExpansion", result);
            await (Task)Call("OpenDrawerAsync")!;
            Check(C<StackPanel>("AiResultPhrasesContainer").Children.Count == 2, "phrases render bilingual cards independently");
            var drawer = C<Border>("AiDrawerSlot");
            var toggle = C<Button>("AiDrawerToggleBtn");
            Check(drawer.IsVisible && double.IsNaN(drawer.Height), "drawer settles at natural height without fixed truncation");
            Check(C<StackPanel>("AiResultExamplesContainer").Children.Count == 3, "each bilingual example has an independent card");
            Check(C<WrapPanel>("AiResultSynonymsPanel").Children.OfType<Button>().Count() == 3, "real renderer builds clickable synonym pills");
            Click(toggle); await Task.Delay(300);
            Check(!drawer.IsVisible, "user collapse button closes drawer");
            Click(toggle); await Task.Delay(30); Click(toggle); await Task.Delay(30); Click(toggle); await Task.Delay(350);
            Check(drawer.IsVisible && double.IsNaN(drawer.Height), "rapid drawer toggles finish in the latest requested state");
            await LookupFooterTests.RunAsync(w, log, folder);
            Click(C<Button>("AddWordBtn"));
            Check(C<TextBlock>("NavVocabBadge").Text == "1", "add-to-vocabulary saves the word after generating expansion");
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                C<ComboBox>("SettingsThemeCombo").SelectedIndex = theme == ThemeVariant.Dark ? 1 : 0;
                Check(w.RequestedThemeVariant == theme, "theme picker previews " + theme.Key);
                using (var store = new VocabularyService()) Check(store.LoadSettings().Theme == theme.Key.ToString(), "theme picker persists " + theme.Key);
                C<ScrollViewer>("PageLookup").Offset = new Vector(0, 210);
                await Snapshot("word-" + theme.Key);
                var english = C<StackPanel>("AiResultExamplesContainer").GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == result.Examples[0].English);
                Check(english.Foreground?.ToString() == w.FindResource(w.ActualThemeVariant, "InkBrush")?.ToString(), "example colors follow " + theme.Key + " theme");
            }
            w.Width = 840; w.Height = 600;
            await Snapshot("word-narrow");
            var card = C<Border>("LookupResultCard");
            Check(card.TranslatePoint(new Point(card.Bounds.Width, 0), w)!.Value.X < w.Bounds.Width - 10, "narrow word card stays inside the window");
            Check(drawer.Bounds.Width > 200 && C<Border>("AiResultBox").Bounds.Height <= drawer.Bounds.Height + 2, "narrow window retains full drawer height");
            Click(C<WrapPanel>("AiResultSynonymsPanel").Children.OfType<Button>().First());
            await Task.Delay(100);
            Check(C<TextBlock>("ResultWordText").Text == "vocabulary" && !drawer.IsVisible, "actual synonym button queries new word and clears old expansion");
            Click(C<Button>("AddWordBtn")); Click(C<Button>("NavVocab"));
            Check(!C<Border>("LookupActionBar").IsEffectivelyVisible, "lookup action bar hidden on vocabulary page");
            await Task.Delay(150);
            var list = C<ListBox>("VocabListBox");
            var toggles = list.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains("chevron-toggle")).ToList();
            Check(toggles.Count >= 2, "real vocabulary rows expose independent expand controls");
            toggles[0].IsChecked = true;
            await Task.Delay(150);
            Check(list.Items.Cast<WordItem>().Count(i => i.IsExpanded) == 1, "one row expands without expanding other rows");
            var row = list.Items.Cast<WordItem>().First(i => i.IsExpanded);
            row.AiResult = result;
            using (var store = new VocabularyService()) store.SaveExpansion(row.Id, result);
            await Task.Delay(150);
            Check(list.GetVisualDescendants().OfType<CheckBox>().Count(c => c.DataContext == row && c.Classes.Contains("capsule")) == 4, "expanded vocabulary row has four independent module checkboxes");
            Check(list.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "mental lexicon"), "vocabulary row displays its own phrase result");
            list.SelectedIndex = 0;
            await Task.Delay(80);
            var surface = list.GetVisualDescendants().OfType<Border>().First(b => b.Name == "RowSurface");
            Check(surface.Background is ISolidColorBrush brush && brush.Color.A == 0, "native row selection paints no blue background");
            Check(!row.Selected, "blank row selection does not change batch checkbox state");
            await InteractionStyleTests.RunAsync(w, log, folder);
            var originalSource = list.ItemsSource;
            Call("ApplyVocabFilters");
            Check(ReferenceEquals(originalSource, list.ItemsSource), "unchanged filters preserve virtualized item source");
            Call("RefreshWords");
            Check(list.Items.Cast<WordItem>().Any(i => ReferenceEquals(row, i) && i.IsExpanded && i.AiResult?.Phrases.SequenceEqual(result.Phrases) == true), "refresh preserves row expansion and persisted AI content by ID");
            C<TextBox>("VocabSearchInput").Text = "vocabulary";
            await Task.Delay(250);
            Check(list.Items.Count == 1, "pasted search text triggers debounced filtering");
            C<TextBox>("VocabSearchInput").Text = "";
            await Task.Delay(250);
            await Snapshot("vocabulary-narrow");
            w.Width = 1060; w.Height = 760;
            await Snapshot("vocabulary-dark");
            w.RequestedThemeVariant = ThemeVariant.Light;
            await Snapshot("vocabulary-light");
            var a = C<TextBlock>("NavVocabBadge").TranslatePoint(new Point(), w)!.Value;
            var b = C<TextBlock>("NavReviewBadge").TranslatePoint(new Point(), w)!.Value;
            Check(Math.Abs(a.X - b.X) < 1, "sidebar count glyphs align in the same column");
            await ScrollingRegressionTests.RunAsync(w, log, folder);
            // In-memory workload only: never writes 10,000 fake records to a database.
            var many = Enumerable.Range(1, 10000).Select(i => new WordItem { Id = i, Word = "sample" + i }).ToList();
            typeof(MainWindow).GetField("_allWords", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, many);
            Call("ApplyVocabFilters");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            C<CheckBox>("SelectAllCheckBox").IsChecked = true;
            Call("OnSelectAllClicked"); Call("OnInvertSelectClicked");
            timer.Stop();
            Check(list.ItemCount == 50 && many.All(i => !i.Selected) && C<TextBlock>("SelectedCountText").Text == "已选 0 项", "10000 words render only one bounded page and bulk selection remains correct");
            Check(timer.ElapsedMilliseconds < 1500, "bulk select and invert avoid quadratic recounts (" + timer.ElapsedMilliseconds + "ms)");
        }
        catch (Exception ex) { exit = 1; log.Add("FAIL " + ex); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "visual-result.txt"), log);
            w.ForceClose();
            (App.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
        }
    }
}
