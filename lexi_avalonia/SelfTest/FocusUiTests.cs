using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Lexi;

public static class FocusUiTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        var exit = 0;
        T C<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception("Missing " + name);
        object? Call(string name, params object[] args) => (typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new Exception("Missing handler " + name)).Invoke(window, args);
        void Check(bool result, string label) { report.Add((result ? "PASS " : "FAIL ") + label); if (!result) throw new Exception(label); }
        Button FocusButton(string name) => window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == name);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Snapshot(string name)
        {
            await Task.Delay(150);
            using var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
            image.Render(window);
            image.Save(Path.Combine(folder, name + ".png"));
        }
        try
        {
            foreach (var family in new[] { ".AppleSystemUIFont", "Helvetica Neue", "Arial", "PingFang SC", "SF Pro Display" })
            {
                var face = new Typeface(new FontFamily(family), FontStyle.Normal, FontWeight.Normal).GlyphTypeface;
                report.Add($"FONT requested={family}; actual={face.FamilyName}; weight={face.Weight}; stretch={face.Stretch}; simulations={face.FontSimulations}");
            }
            window.SetUiLanguage("zh-CN");
            var latin = new Typeface(C<TextBlock>("ResultWordText").FontFamily).GlyphTypeface;
            Check(latin.FamilyName == "Inter" && latin.Stretch == FontStretch.Normal, "learning uses real Inter with normal width, rather than a font-name fallback");
            if (OperatingSystem.IsMacOS())
            {
                Check(FontManager.Current.TryMatchCharacter('词', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                    C<TextBlock>("ResultWordText").FontFamily, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), out var chinese)
                    && chinese.GlyphTypeface.FamilyName == "PingFang SC", "Chinese learning content resolves to native PingFang SC");
            }
            C<TextBox>("LookupInput").Text = "serendipity";
            await (Task)Call("PerformLookupAsync")!;
            Check(window.GetVisualDescendants().OfType<Button>().Any(x => x.Name == "FocusEntryBtn"), "dictionary word card exposes an explicit focus entry");
            var originalWidth = window.Width;
            var originalHeight = window.Height;
            Click(FocusButton("FocusEntryBtn"));
            await Task.Delay(100);
            Check(!C<Border>("SidebarShell").IsEffectivelyVisible && !C<Border>("DragBar").IsEffectivelyVisible && !C<Border>("WindowStatusBar").IsEffectivelyVisible, "focus removes global navigation, settings and status chrome");
            Check(!C<TextBox>("LookupInput").IsEffectivelyVisible && C<TextBlock>("ResultWordText").IsEffectivelyVisible && C<TextBlock>("ResultPhoneticText").IsEffectivelyVisible && C<TextBlock>("ResultTranslationText").IsEffectivelyVisible && !C<WrapPanel>("ResultPosPanel").IsEffectivelyVisible, "a brand-new word opens the study card with word, pronunciation and meaning");
            Check(!C<Button>("AiGenerateBtn").IsEffectivelyVisible, "AI tools start undisclosed in focus");
            Check(!C<Border>("LookupActionBar").IsEffectivelyVisible,
                "learning footer has no ordinary lookup action bar underneath it");
            Check(FocusButton("FocusBackBtn").Content?.ToString() == "返回", "focus back label follows Chinese UI");
            Check(window.Width == originalWidth && window.Height == originalHeight, "focus preserves native window dimensions");
            Check((C<Border>("LookupResultCard").Background as ISolidColorBrush)?.Color == Colors.Transparent
                && C<Border>("LookupResultCard").BorderThickness == default(Thickness)
                && C<Border>("RootWindowBorder").Background is LinearGradientBrush,
                "word focus is a full-page glacier gradient, without an inner card frame");
            Check(FocusButton("FocusStartRecallBtn").IsEffectivelyVisible && !FocusButton("FocusKnownBtn").IsEffectivelyVisible,
                "first learn starts on the study card and only then exposes Q/W/E recall actions");
            Check(C<Border>("LookupResultCard").Bounds.Width <= 780, "large focus card keeps a readable line length of at most 780 points");
            report.Add("METRIC actual_transparency=" + window.ActualTransparencyLevel);
            if (OperatingSystem.IsMacOS())
                Check(window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur, "macOS uses the actual native frosted-glass backdrop");
            if (OperatingSystem.IsMacOS())
            {
                Call("ApplyAppearance");
                Check(window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur, "repeated appearance refresh preserves native blur");
                C<CheckBox>("OpaqueMaterialBox").IsChecked = true;
                Check(window.ActualTransparencyLevel == WindowTransparencyLevel.None, "opaque accessibility preference disables native blur");
                C<CheckBox>("OpaqueMaterialBox").IsChecked = false;
                Check(window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur, "clearing opaque preference restores native blur");
                C<CheckBox>("HighContrastBox").IsChecked = true;
                Check(window.ActualTransparencyLevel == WindowTransparencyLevel.None, "high contrast disables native blur");
                C<CheckBox>("HighContrastBox").IsChecked = false;
                Check(window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur, "clearing high contrast restores native blur");
            }
            await Snapshot("focus-light-zh");
            var countBeforeReveal = ((IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).GetAllWords().Count;
            await (Task)Call("CompleteFocusLearnAsync")!;
            await Task.Delay(80);
            Check(!C<TextBlock>("ResultTranslationText").IsEffectivelyVisible && FocusButton("FocusKnownBtn").IsEffectivelyVisible
                && !FocusButton("FocusStartRecallBtn").IsEffectivelyVisible,
                "starting recall hides the meaning and exposes Q/W/E");
            Check(((IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).GetAllWords().Count == countBeforeReveal,
                "finishing the study card never writes progress on its own");
            await (Task)Call("ChooseFocusedWordAsync", 0)!;
            Check(C<TextBlock>("ResultTranslationText").IsEffectivelyVisible && FocusButton("FocusNextBtn").IsEffectivelyVisible && !FocusButton("FocusKnownBtn").IsEffectivelyVisible,
                "Know it reveals meaning and replaces three choices with Next Space and Wrong E");
            Check(((IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).GetAllWords().Count == countBeforeReveal + 1,
                "rating a plain lookup word files it into the vocabulary archive");
            Check(FocusButton("FocusPronounceBtn").IsEffectivelyVisible, "front and answer expose a native pronunciation button");
            await Snapshot("focus-answer-zh");
            Click(FocusButton("FocusAiToggleBtn"));
            Check(C<Button>("AiGenerateBtn").IsEffectivelyVisible, "AI tools can be revealed without generating content");
            Click(FocusButton("FocusAiToggleBtn"));
            Check(!C<Button>("AiGenerateBtn").IsEffectivelyVisible, "AI tools can be collapsed again");
            window.SetUiLanguage("en");
            Check(FocusButton("FocusBackBtn").Content?.ToString() == "Back" && ToolTip.GetTip(FocusButton("FocusAiToggleBtn"))?.ToString() == "Show AI tools", "focus labels switch immediately to English");
            window.Width = 840; window.Height = 600;
            await Task.Delay(100);
            var card = C<Border>("LookupResultCard");
            var right = card.TranslatePoint(new Point(card.Bounds.Width, 0), window)!.Value.X;
            Check(card.Bounds.Width > 300 && right <= window.Bounds.Width, "narrow focus word card stays inside the window");
            await Snapshot("focus-narrow-en");
            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            window.RaiseEvent(escape);
            await Task.Delay(100);
            Check(escape.Handled && C<Border>("SidebarShell").IsEffectivelyVisible && C<Border>("DragBar").IsEffectivelyVisible, "Esc restores ordinary navigation");
            Check(C<TextBox>("LookupInput").Text == "serendipity" && C<TextBlock>("ResultWordText").Text == "serendipity", "exit preserves the original lookup query and result");
            window.Width = originalWidth; window.Height = originalHeight;
            await Task.Delay(100);
            window.SetUiLanguage("zh-CN");
            Click(C<Button>("AddWordBtn"));
            Click(C<Button>("NavVocab"));
            var store = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var word = store.GetAllWords().Single(x => x.Word == "serendipity");
            store.SaveExpansion(word.Id, new LlmResult { Examples = [new("It was pure serendipity.", "这完全是机缘巧合。")], Phrases = [new("by serendipity", "机缘巧合地")] });
            Call("RefreshWords");
            word = store.GetAllWords().Single(x => x.Id == word.Id);
            var revision = word.Archive.Revision;
            await (Task)Call("OpenArchivedWordFocusAsync", word)!;
            await Snapshot("focus-front-with-details-zh");
            Check(!C<Grid>("PageVocab").IsEffectivelyVisible && C<TextBlock>("ResultWordText").Text == word.Word && !C<Button>("AiGenerateBtn").IsEffectivelyVisible, "archived word opens the same immersive card without AI generation");
            Call("ExitWordFocus");
            await Task.Delay(100);
            Check(C<Grid>("PageVocab").IsEffectivelyVisible && C<Border>("SidebarShell").IsEffectivelyVisible, "leaving archived word returns to the vocabulary page");
            Check(store.GetAllWords().Single(x => x.Id == word.Id).Archive.Revision == revision, "opening and closing a card does not modify archived data");
            // 模拟新的一天：清掉今天的同日去重记录，让这个词可以再次推进阶段。
            using (var sql = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                sql.Open();
                using var cmd = sql.CreateCommand();
                cmd.CommandText = "DELETE FROM review_logs WHERE word_id = $id AND action = 'batch_review' AND log_date = $today";
                cmd.Parameters.AddWithValue("$id", word.Id);
                cmd.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
                cmd.ExecuteNonQuery();
            }
            await (Task)Call("OpenArchivedWordFocusAsync", store.GetAllWords().Single(x => x.Id == word.Id))!;
            var stageBefore = store.GetAllWords().Single(x => x.Id == word.Id).Stage;
            Check(!C<TextBlock>("ResultTranslationText").IsEffectivelyVisible && FocusButton("FocusKnownBtn").IsEffectivelyVisible
                && !FocusButton("FocusStartRecallBtn").IsEffectivelyVisible,
                "an archived word opens straight into recall with the meaning hidden");
            await (Task)Call("ChooseFocusedWordAsync", 0)!;
            Check(store.GetAllWords().Single(x => x.Id == word.Id).Stage == stageBefore + 1, "Know it reveals the answer and advances one stage");
            await Task.Delay(100);
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "It was pure serendipity." && x.IsEffectivelyVisible)
                && window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "by serendipity  机缘巧合地" && x.IsEffectivelyVisible),
                "revealed examples and phrases use the saved bilingual content");
            await Snapshot("focus-answer-with-details-zh");
            var rating = (Task)Call("RateFocusedWordAsync", StudyRating.Known)!;
            var duplicate = (Task)Call("RateFocusedWordAsync", StudyRating.Known)!;
            await Task.WhenAll(rating, duplicate);
            Check(store.GetAllWords().Single(x => x.Id == word.Id).Stage == stageBefore + 1,
                "an already rated card ignores repeated ratings until the next word");
            var undo = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Alt };
            window.RaiseEvent(undo); await Task.Delay(250);
            Check(undo.Handled && store.GetAllWords().Single(x => x.Id == word.Id).Stage == stageBefore,
                $"Option Space undoes the full-page rating and restores prior progress (handled={undo.Handled}, stage={store.GetAllWords().Single(x => x.Id == word.Id).Stage}, undoCount={((System.Collections.ICollection)typeof(MainWindow).GetField("_focusUndo", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Count})");
            var master = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete };
            window.RaiseEvent(master); await Task.Delay(250);
            Check(master.Handled && store.GetAllWords().Single(x => x.Id == word.Id).Status == "mastered",
                "Delete marks a learning word mastered without deleting its archive");
            await (Task)Call("UndoFocusRatingAsync")!;
            Check(store.GetAllWords().Single(x => x.Id == word.Id).Status == "learning"
                && store.GetAllWords().Single(x => x.Id == word.Id).Stage == stageBefore,
                "undo restores both status and stage after marking mastered");
            Click(FocusButton("FocusBackBtn"));
            Click(C<Button>("NavLookup"));
            var shortcut = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift };
            window.RaiseEvent(shortcut);
            Check(shortcut.Handled && !C<Border>("SidebarShell").IsEffectivelyVisible, "Command Shift F opens focus for the current word");
            C<Border>("DialogEditOverlay").IsVisible = true;
            var dialogEscape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            window.RaiseEvent(dialogEscape);
            Check(!dialogEscape.Handled && !C<Border>("SidebarShell").IsEffectivelyVisible, "Esc does not discard or navigate away from an open edit dialog");
            C<Border>("DialogEditOverlay").IsVisible = false;
            Call("ExitWordFocus");
            // Exercise the real routed window shortcuts, including leaving focus.
            var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            KeyEventArgs Shortcut(Key key, bool shift = false)
            {
                var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = command | (shift ? KeyModifiers.Shift : KeyModifiers.None) };
                window.RaiseEvent(e);
                return e;
            }
            Click(FocusButton("FocusEntryBtn"));
            Check(Shortcut(Key.L).Handled && C<Border>("SidebarShell").IsEffectivelyVisible && C<TextBox>("LookupInput").IsFocused, "Command L exits focus and selects lookup input");
            foreach (var (key, page) in new[] { (Key.D2, "PageVocab"), (Key.D3, "PageReview"), (Key.D4, "PageSettings"), (Key.D1, "LookupPageHost") })
                Check(Shortcut(key).Handled && C<Control>(page).IsEffectivelyVisible, "command page shortcut navigates to " + page);
            var previousLanguage = C<Button>("LanguageToggleBtn").Content?.ToString();
            Check(Shortcut(Key.L, true).Handled && C<Button>("LanguageToggleBtn").Content?.ToString() != previousLanguage, "Command Shift L changes UI language");
            var previousTheme = window.RequestedThemeVariant;
            Check(Shortcut(Key.D, true).Handled && window.RequestedThemeVariant != previousTheme, "Command Shift D changes theme");
            Click(FocusButton("FocusEntryBtn"));
            await Snapshot("focus-dark-zh");
            Call("ExitWordFocus");
            window.SetUiLanguage("zh-CN");
            Check(C<Button>("AddWordBtn").IsEffectivelyVisible && C<CheckBox>("AiOptExamples").IsEffectivelyVisible, "normal lookup restores existing add and AI controls");
        }
        catch (Exception ex) { report.Add("FAIL " + ex); exit = 1; }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "focus-result.txt"), report);
            window.ForceClose();
            (App.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
        }
    }
}
