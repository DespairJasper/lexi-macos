using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;

namespace Lexi;

public static class LearningUiTests
{
    private sealed class RecordedAudio : IWordAudioPlayer
    {
        public List<(string Text, string? Path)> Calls { get; } = [];
        public int Stops { get; private set; }
        private string? _path;
        public bool CanSeek => _path != null;
        public bool IsPlaying { get; private set; }
        public double Position { get; private set; }
        public double Duration => CanSeek ? 180 : 0;
        public void TogglePause() => IsPlaying = !IsPlaying;
        public void Seek(double seconds) => Position = Math.Clamp(seconds, 0, Duration);
        public void Play(string text, string? localRecording = null) { Calls.Add((text, localRecording)); _path = localRecording; Position = 0; IsPlaying = true; }
        public void Stop() { Stops++; _path = null; IsPlaying = false; }
        public void Dispose() => Stop();
    }
    private static readonly RecordedAudio Audio = new();
    public static MainWindow CreateWindow() => new(Audio);
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR") ?? throw new InvalidOperationException("Isolated test data required");
        var report = new List<string>();
        void Check(bool ok, string message)
        {
            report.Add((ok ? "PASS " : "FAIL ") + message);
            File.WriteAllLines(Path.Combine(folder, "learning-ui-result.txt"), report);
            if (!ok) throw new Exception(message);
        }
        T C<T>(string name) where T : Control => window.FindControl<T>(name) ?? window.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);
        void Click(string name) => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Control Focused() => window.FocusManager?.GetFocusedElement() as Control ?? window;
        void KeyPress(Key key, bool up = false, KeyModifiers modifiers = KeyModifiers.None) => Focused().RaiseEvent(new KeyEventArgs { RoutedEvent = up ? InputElement.KeyUpEvent : InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(window, args);
        async Task Snapshot(string name)
        {
            await Task.Delay(350);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96,96));
            bitmap.Render(window); bitmap.Save(Path.Combine(folder, name + ".png"));
        }
        var code = 0;
        var shutdownCompleted = false;
        try
        {
            window.Width = 1000; window.Height = 780;
            var successColor = ((Avalonia.Media.ISolidColorBrush)window.FindResource(window.ActualThemeVariant, "SuccessBrush")!).Color;
            Click("NavIelts"); await Task.Delay(220);
            Check(C<Grid>("PageIelts").IsVisible && !C<Grid>("LookupPageHost").IsVisible, "IELTS is an integrated exclusive native page");
            Check(C<ComboBox>("IeltsSection").ItemCount == 22 && C<StackPanel>("IeltsRows").Children.Count == 30, "22 chapters with bounded pages");
            await Snapshot("ielts-vocabulary-light");
            Click("IeltsChapterAudioBtn"); Check(Audio.Calls.Last().Path?.EndsWith("01_自然地理.mp3") == true, "chapter audio uses bundled recording");
            Click("ChapterPauseBtn"); Check(!Audio.IsPlaying, "chapter pause control pauses player");
            C<Slider>("ChapterTimeline").Value = 12; Click("ChapterForwardBtn"); Check(Audio.Position == 17, "chapter seek slider and forward control");
            C<ComboBox>("IeltsPracticeMode").SelectedIndex = 1;
            C<NumericUpDown>("IeltsCount").Value = 3; C<CheckBox>("IeltsAll").IsChecked = false; C<ComboBox>("IeltsOrder").SelectedIndex = 0;
            Click("IeltsPracticeStartBtn"); await Task.Delay(100);
            Check(C<Grid>("PageTyping").IsVisible && Audio.Calls.Last().Text == "atmosphere", "typing begins with automatic word audio");
            Check(!C<Border>("SidebarShell").IsEffectivelyVisible && !C<Border>("DragBar").IsEffectivelyVisible
                && !C<TextBlock>("TypingStats").IsEffectivelyVisible && C<TextBox>("TypingInput").BorderThickness == default(Thickness), "typing is full-page without chrome, live metrics or an input frame");
            C<TextBox>("TypingInput").Text = "ax"; await Task.Delay(60);
            Check(C<TextBox>("TypingInput").Text == "" && C<TextBlock>("TypingFeedback").Text == "" && !C<TextBlock>("TypingFeedback").IsVisible,
                "hinted typo resets and keeps the separate spelling answer hidden");
            var errorRuns = C<TextBlock>("TypingLetters").Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();
            Check(errorRuns[0].Foreground is Avalonia.Media.ISolidColorBrush good && good.Color == successColor
                && errorRuns[1].Foreground is Avalonia.Media.ISolidColorBrush bad && bad.Color == Avalonia.Media.Color.Parse("#F87171"), "hint mode uses Lexi's muted success green and Qwerty-style error red");
            var shakeTimer = (Avalonia.Threading.DispatcherTimer)typeof(MainWindow).GetField("_typingShakeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var shakeOffsets = (double[])typeof(MainWindow).GetField("TypingShakeOffsets", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Check(shakeTimer.Interval == TimeSpan.FromMilliseconds(16)
                && (double)typeof(MainWindow).GetField("TypingShakeDurationMs", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! == 820
                && shakeOffsets.SequenceEqual([0, -1, 2, -4, 4, -4, 4, -4, 2, -1, 0]), "error shake follows Qwerty Learner's 0.82-second keyframes");
            await Snapshot("typing-hints-error");
            Click("TypingReplayBtn"); Check(C<Button>("TypingReplayBtn").Content?.ToString()?.Contains("/") == true && Audio.Calls.Last().Text == "atmosphere", "central audio control pronounces and reveals phonetic");
            C<TextBox>("TypingInput").Text = "atmosphere";
            for (var attempt = 0; attempt < 20 && Audio.Calls.Last().Text != "hydrosphere"; attempt++) await Task.Delay(50);
            Check(Audio.Calls.Last().Text == "hydrosphere", $"only correct input advances and pronounces next word (last={Audio.Calls.Last().Text})");
            await Snapshot("typing-hints-light");
            C<TextBox>("TypingInput").Text = "hy"; await Task.Delay(60);
            window.HideToTray(); window.ShowAndActivate(); await Task.Delay(100);
            Check(C<Grid>("PageTyping").IsVisible && C<TextBox>("TypingInput").Text == "hy",
                "reopening Lexi preserves the active typing question and partial input");
            Click("NavIelts"); Click("NavTyping");
            Check(C<Grid>("PageTyping").IsVisible && C<TextBox>("TypingInput").Text == "hy",
                "leaving and returning through navigation preserves the active typing question and input");
            C<TextBox>("TypingInput").Text = "hydrosphere"; await Task.Delay(400);
            Click("TypingExitBtn");
            Check(C<Border>("SidebarShell").IsEffectivelyVisible, "exiting a typing round restores ordinary navigation");
            Click("NavIelts"); C<ComboBox>("IeltsPracticeMode").SelectedIndex = 2; C<NumericUpDown>("IeltsCount").Value = 2;
            Click("IeltsPracticeStartBtn"); await Task.Delay(60);
            var initialSession = (TypingSession)typeof(MainWindow).GetField("_typingSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(initialSession.Count == 2 && !initialSession.Hints, "IELTS dictation uses selected word count and mode");
            KeyPress(Key.T, modifiers: KeyModifiers.Alt); KeyPress(Key.T, true, KeyModifiers.Alt);
            Check(C<Button>("TypingReplayBtn").Content?.ToString()?.Contains("/") == true, "Alt T replays word and displays phonetic");
            KeyPress(Key.Escape); KeyPress(Key.Escape, true);
            C<ComboBox>("TypingSource").SelectedIndex = 5; C<ComboBox>("TypingMode").SelectedIndex = 1;
            C<NumericUpDown>("TypingCount").Value = 3; C<CheckBox>("TypingAll").IsChecked = false; C<ComboBox>("TypingOrder").SelectedIndex = 0;
            Click("TypingStartBtn"); await Task.Delay(100);
            Check(!(C<TextBlock>("TypingLetters").Inlines!.Text ?? "").Contains("atmosphere"), "no-hint display does not reveal spelling");
            C<TextBox>("TypingInput").Text = "ax"; await Task.Delay(60);
            Check(C<TextBox>("TypingInput").Text == "ax" && C<TextBlock>("TypingFeedback").Text == ""
                && C<TextBlock>("TypingLetters").Inlines!.OfType<Avalonia.Controls.Documents.Run>().ElementAt(0).Foreground is Avalonia.Media.ISolidColorBrush dictationGood && dictationGood.Color == successColor
                && C<TextBlock>("TypingLetters").Inlines!.OfType<Avalonia.Controls.Documents.Run>().ElementAt(1).Foreground is Avalonia.Media.ISolidColorBrush dictationBad && dictationBad.Color == Avalonia.Media.Color.Parse("#DC2626"),
                "dictation shows only typed letters with Qwerty-aligned per-letter correctness");
            C<TextBox>("TypingInput").Text = "axmosphere"; await Task.Delay(60);
            var dictationErrorRuns = C<TextBlock>("TypingLetters").Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();
            Check(C<TextBox>("TypingInput").Text == "" && C<TextBlock>("TypingFeedback").Text == "atmosphere" && C<TextBlock>("TypingFeedback").IsVisible
                && (C<TextBlock>("TypingLetters").Inlines!.Text ?? "") == "axmosphere"
                && dictationErrorRuns[0].Foreground is Avalonia.Media.ISolidColorBrush firstGood && firstGood.Color == successColor
                && dictationErrorRuns[1].Foreground is Avalonia.Media.ISolidColorBrush firstBad && firstBad.Color == Avalonia.Media.Color.Parse("#DC2626"),
                "dictation error shows the correct spelling beneath its marked input");
            await Snapshot("typing-no-hints-error");
            C<TextBox>("TypingInput").Text = "a"; await Task.Delay(60);
            Check(!C<TextBlock>("TypingFeedback").IsVisible && C<TextBox>("TypingInput").Text == "a", "first retry letter dismisses error comparison and starts same word");
            var stops = Audio.Stops; Click("NavIelts"); C<NumericUpDown>("IeltsCount").Value = 3; Check(Audio.Stops > stops, "navigation stops playback and cancels typing advance");
            await (Task)Call("StartIeltsCardsAsync", new List<LearningWord> { new() { Id = "test:atmosphere", Words = ["atmosphere"] }, new() { Id = "test:hydrosphere", Words = ["hydrosphere"] }, new() { Id = "test:oxygen", Words = ["oxygen"] }, new() { Id = "test:wood", Words = ["wood"] } })!;
            Check(Audio.Calls.Last().Text == "atmosphere", "word-card entry automatically pronounces current word");
            Check(((List<string>)typeof(MainWindow).GetField("_focusDeck", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Count == 3, "recognition uses the same selected round count");
            Check(C<Button>("FocusStartRecallBtn").IsEffectivelyVisible && !C<Button>("FocusKnownBtn").IsEffectivelyVisible,
                "a brand-new word opens the study card with the meaning visible instead of a blind recall");
            await (Task)Call("LoadFocusWordAsync")!; Check(Audio.Calls.Last().Text == "atmosphere", "word-card render automatically pronounces");
            var currentCardWord = C<TextBlock>("ResultWordText").Text;
            window.HideToTray(); window.ShowAndActivate(); await Task.Delay(100);
            Check((bool)typeof(MainWindow).GetField("_wordFocusActive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!
                && C<Border>("WordFocusBar").IsEffectivelyVisible && C<TextBlock>("ResultWordText").Text == currentCardWord,
                "reopening Lexi preserves the active word-card recall and current word");
            C<CheckBox>("ReduceMotionBox").IsChecked = true;
            var round = (StudyRound<string>)typeof(MainWindow).GetField("_focusRound", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            // 首次学习：先把整组学完（三张学习卡），回忆遍在轮内打乱顺序。
            KeyPress(Key.Space); await Task.Delay(120);
            KeyPress(Key.Space); await Task.Delay(120);
            KeyPress(Key.Space); await Task.Delay(120);
            Check(round.CurrentStep == StudyStep.Recall && C<Button>("FocusKnownBtn").IsEffectivelyVisible
                && !C<Button>("FocusStartRecallBtn").IsEffectivelyVisible
                && C<TextBlock>("ResultWordText").Text == round.Current, "the study pass hands over to a shuffled recall pass");
            var firstRecall = C<TextBlock>("ResultWordText").Text!;
            KeyPress(Key.Q); KeyPress(Key.Q, true); await Task.Delay(60);
            var feedback = (TextBlock)typeof(MainWindow).GetField("_focusFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(round.Completed == 0 && round.Known == 1 && feedback.Text!.Contains("1/3")
                && C<TextBlock>("ResultWordText").Text == firstRecall,
                "one known rating only banks a single streak point for the current word");
            Check(C<Border>("FocusStreakBar1").Background is Avalonia.Media.ISolidColorBrush lit && lit.Color == Avalonia.Media.Color.Parse("#268D98")
                && C<Border>("FocusStreakBar3").Background is Avalonia.Media.ISolidColorBrush dim && dim.Color != Avalonia.Media.Color.Parse("#268D98"),
                "the streak bars light from the bottom up and stay grey above");
            KeyPress(Key.Space); await Task.Delay(120);
            var cleanFooter = !C<Border>("LookupActionBar").IsEffectivelyVisible;
            await Snapshot("cards-next-word-before-keyup");
            KeyPress(Key.Space, true); await Task.Delay(60);
            var active = (bool)typeof(MainWindow).GetField("_wordFocusActive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(active && cleanFooter && C<TextBlock>("ResultWordText").Text == round.Current
                && C<TextBlock>("ResultWordText").Text != firstRecall,
                $"complete Q/Space key stroke advances without exiting ({active}) and keeps lookup footer hidden ({cleanFooter})");
            Click("FocusKnownBtn"); Click("FocusNextBtn"); await Task.Delay(150);
            Check(C<TextBlock>("ResultWordText").Text == round.Current && !C<Border>("LookupActionBar").IsEffectivelyVisible, "mouse rating reaches the next word with a clean footer");
            // 连击规则：每个词攒够 3 次「认识」，全部通过后本轮才完成。
            for (var guard = 0; guard < 40 && !round.IsFinished; guard++)
            {
                if (round.CurrentStep == StudyStep.Learn) KeyPress(Key.Space);
                else { Click("FocusKnownBtn"); KeyPress(Key.Space); }
                await Task.Delay(90);
            }
            Check((bool)typeof(MainWindow).GetField("_wordFocusActive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!
                && round.IsFinished && round.Known == 9 && round.Total == 3,
                "every word needs three known ratings before the round completes without returning home");
            C<CheckBox>("ReduceMotionBox").IsChecked = false;
            var ieltsArchive = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(!ieltsArchive.GetAllWords().Any(w => w.Word == "atmosphere"), "IELTS recognition stays independent until explicitly saved");
            Call("ExitWordFocus"); Click("NavIelts");
            C<ComboBox>("IeltsKind").SelectedIndex = 1; Click("IeltsSynonymsBtn");
            Check(Audio.Calls.Last().Text == "reserve", "synonym exercise starts with pronunciation");
            C<TextBox>("SynonymWordInput").Text = "reserve"; C<TextBox>("SynonymInput").Text = "book"; Click("SynonymCheckBtn");
            Click("SynonymNextBtn"); Check(Audio.Calls.Last().Text == "in advance", "correct synonym set advances to next question");
            C<ComboBox>("IeltsKind").SelectedIndex = 0;
            C<ComboBox>("IeltsKind").SelectedIndex = 1; Click("IeltsSynonymsBtn");
            Check(Audio.Calls.Last().Text == "reserve", "synonym exercise reopens after leaving without duplicate parent");
            C<ComboBox>("IeltsKind").SelectedIndex = 4; C<TextBox>("WritingDraft1").Text = "My saved translation"; await Task.Delay(600);
            Check(File.ReadAllText(Path.Combine(folder,"ielts-learning.json")).Contains("My saved translation"), "translation drafts persist automatically in isolated directory");
            await Snapshot("ielts-writing");
            C<ComboBox>("IeltsKind").SelectedIndex = 5; Check(C<StackPanel>("IeltsRows").Children.Count == 3, "grammar resources and full listening notes are accessible");
            window.SetUiLanguage("en"); Check(C<Button>("NavTyping").Content?.ToString() == "Typing practice", "new navigation follows UI language");
            C<ComboBox>("SettingsThemeCombo").SelectedIndex = 1; Click("NavTyping"); Click("TypingStartBtn"); await Task.Delay(120);
            window.Width = 840; window.Height = 600; await Snapshot("typing-dark-840");
            Check(C<TextBox>("TypingInput").IsEffectivelyEnabled && C<TextBox>("TypingInput").Bounds.Height > 20
                && C<TextBlock>("TypingMeaning").IsEffectivelyVisible, "borderless input and meaning remain usable at 840x600 dark theme");
            KeyPress(Key.Escape); KeyPress(Key.Escape, true); Click("NavIelts");
            C<ComboBox>("IeltsKind").SelectedIndex = 0; C<ComboBox>("IeltsOrder").SelectedIndex = 1;
            C<NumericUpDown>("IeltsCount").Value = 4; C<CheckBox>("IeltsAll").IsChecked = false;
            await (Task)Call("StartIeltsCardsAsync", new List<LearningWord> { new() { Id = "a", Words = ["atmosphere"] }, new() { Id = "b", Words = ["hydrosphere"] }, new() { Id = "c", Words = ["oxygen"] }, new() { Id = "d", Words = ["wood"] }, new() { Id = "e", Words = ["apple"] } })!;
            var randomDeck = (List<string>)typeof(MainWindow).GetField("_focusDeck", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var savedProgress = LearningProgress.Load(Path.Combine(folder, "ielts-learning.json"));
            var expectedRandom = LearningRound.Select(new[] { "atmosphere", "hydrosphere", "oxygen", "wood", "apple" }.Select(w => new LearningWord { Id = w, Words = [w] }), 4, false, true, savedProgress.LastShuffleSeed).Select(w => w.Word);
            Check(randomDeck.SequenceEqual(expectedRandom) && savedProgress.RoundRandom, "recognition applies random order and selected count using saved seed");
            Call("ExitWordFocus"); Click("NavTyping");
            var sourceWords = (List<LearningWord>)Call("ReadDictionaryTypingWords")!;
            Check(sourceWords.Count == 59026, "all 59026 local dictionary words available for typing");
            C<CheckBox>("TypingAll").IsChecked = true; C<ComboBox>("TypingOrder").SelectedIndex = 0;
            Call("OpenTypingWords", new List<LearningWord> { new() { Id = "fixture:colour", Words = ["colour", "color"] } }, "variant test");
            var session = (TypingSession)typeof(MainWindow).GetField("_typingSession",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            Check(session.Count == 2, "typing includes all spelling variants");
            C<TextBox>("TypingInput").Text = "colour";
            for (var attempt = 0; attempt < 20 && session.Cursor == 0; attempt++) await Task.Delay(50);
            C<TextBox>("TypingInput").Text = "color";
            for (var attempt = 0; attempt < 20 && !C<TextBlock>("TypingStats").IsEffectivelyVisible; attempt++) await Task.Delay(50);
            Check(C<TextBlock>("TypingStats").IsEffectivelyVisible && !C<TextBox>("TypingInput").IsEffectivelyVisible, "metrics appear only on the round completion screen");
            Check(C<TextBlock>("TypingStats").Text!.Contains("Accuracy")
                && C<TextBlock>("TypingStats").Text!.Contains("Retries")
                && !C<TextBlock>("TypingStats").Text!.Contains("准确率"),
                "typing completion statistics follow the selected English UI language");
            Click("TypingRestartBtn"); Check(session.Count == 2, "restart does not duplicate spelling variants");
            var store = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            store.AddWord("apple", "", "苹果", ""); store.ExecuteBatch([store.GetAllWords().Single(w => w.Word == "apple").Id], "today"); Call("RefreshWords"); Click("NavReview");
            Check(Audio.Calls.Last().Text == "apple", "review card automatically pronounces due word");
            Call("ExitWordFocus"); Click("NavIelts");
            var dictionary = typeof(MainWindow).GetField("_dictService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var gate = dictionary.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dictionary)!;
            Task cancelledCard;
            System.Threading.Monitor.Enter(gate);
            try
            {
                cancelledCard = (Task)Call("StartIeltsCardsAsync", new List<LearningWord> { new() { Id = "fixture:atmosphere", Words = ["atmosphere"] } })!;
                Click("NavSettings");
            }
            finally { System.Threading.Monitor.Exit(gate); }
            await cancelledCard;
            var navigationSafe = C<Control>("PageSettings").IsVisible && !(bool)typeof(MainWindow).GetField("_wordFocusActive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Call("ExitWordFocus"); Click("NavLookup");
            Task staleLookup;
            System.Threading.Monitor.Enter(gate);
            try
            {
                C<TextBox>("LookupInput").Text = "atmosphere";
                staleLookup = (Task)Call("PerformLookupAsync")!;
                C<TextBox>("LookupInput").Text = "apple";
                await (Task)Call("PerformLookupAsync")!;
            }
            finally { System.Threading.Monitor.Exit(gate); }
            await staleLookup;
            var expansion = typeof(MainWindow).GetField("_currentExpansion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            Check(navigationSafe && expansion == null && C<TextBlock>("ResultWordText").Text == "apple", $"pending IELTS card preserves newer navigation ({navigationSafe}); stale query preserves current expansion ({expansion == null})");
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            var dockQuitAllowed = false;
            desktop.ShutdownRequested += (_, _) => dockQuitAllowed = (bool)typeof(MainWindow).GetField("_isForceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            shutdownCompleted = desktop.TryShutdown();
            Check(shutdownCompleted && dockQuitAllowed && !window.IsVisible,
                "macOS app-icon Quit request fully closes Lexi instead of hiding the window");
            Console.WriteLine(string.Join("\n", report));
        }
        catch(Exception ex) { code = 1; File.WriteAllText(Path.Combine(folder,"learning-ui-error.txt"),ex.ToString()); Console.Error.WriteLine(ex); }
        finally
        {
            if (!shutdownCompleted && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(code);
        }
    }
}
