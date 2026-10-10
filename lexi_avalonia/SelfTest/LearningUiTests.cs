using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Microsoft.Data.Sqlite;

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
            // Plan creation and progress use their own manager, frozen words and JSON store.
            T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            async Task InvokeAsync(string name, params object[] args) => await (Task)Call(name, args)!;
            async Task SelectPlanWords(params string[] words)
            {
                Click("PlanClearSelectionBtn");
                foreach (var word in words)
                {
                    C<TextBox>("PlanWordSearch").Text = word;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                    C<StackPanel>("PlanWordChoices").Children.OfType<CheckBox>().Single(c =>
                        (c.Content as TextBlock)?.Text?.StartsWith(word + "  ·  ", StringComparison.Ordinal) == true).IsChecked = true;
                }
                C<TextBox>("PlanWordSearch").Text = "";
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            }
            async Task FinishPlanRound()
            {
                var planRound = Field<StudyRound<string>>("_focusRound");
                for (var guard = 0; guard < 80 && !planRound.IsFinished; guard++)
                {
                    if (planRound.CurrentStep == StudyStep.Learn) await InvokeAsync("CompleteFocusLearnAsync");
                    else { await InvokeAsync("RateFocusedWordAsync", StudyRating.Known); await InvokeAsync("AdvanceFocusAsync"); }
                }
                Check(planRound.IsFinished, "plan recall completes only after all words reach their known streak");
            }
            var planArchive = Field<IVocabularyArchive>("_vocabService");
            planArchive.AddWord("planfixture", "", "计划测试词", "fixture definition");
            Call("RefreshWords");
            Click("NavVocab");
            Check(!C<Grid>("PageStudyPlan").IsVisible
                && C<Button>("ArchivePlanCreateBtn").GetLogicalAncestors().Any(a => a == C<Grid>("PageStudyPlan"))
                && C<StackPanel>("ArchivePlanRows").GetLogicalAncestors().Any(a => a == C<Grid>("PageStudyPlan"))
                && !C<Grid>("PageVocab").GetLogicalDescendants().OfType<Control>().Any(c => c.Name is "ArchivePlanCreateBtn" or "ArchivePlanRows"),
                "archive page contains no embedded plan creation or plan list");
            Click("NavIelts");
            Check(!C<Grid>("PageStudyPlan").IsVisible
                && C<Button>("IeltsPlanCreateBtn").GetLogicalAncestors().Any(a => a == C<Grid>("PageStudyPlan"))
                && C<StackPanel>("IeltsPlanRows").GetLogicalAncestors().Any(a => a == C<Grid>("PageStudyPlan"))
                && !C<Grid>("PageIelts").GetLogicalDescendants().OfType<Control>().Any(c => c.Name is "IeltsPlanCreateBtn" or "IeltsPlanRows")
                && !window.GetLogicalDescendants().OfType<CheckBox>().Any(c => c.Name == "IeltsPlanSelectBox"),
                "IELTS page contains no embedded plans or removed plan-selection column");
            Click("NavPlans");
            Check(C<Grid>("PageStudyPlan").IsVisible && !C<Grid>("PageIelts").IsVisible && !C<Grid>("PageVocab").IsVisible
                && !C<Grid>("LookupPageHost").IsVisible && C<Button>("ArchivePlanCreateBtn").IsEffectivelyVisible,
                "dedicated plans navigation opens an exclusive native manager");
            Click("ArchivePlanCreateBtn");
            Check(C<Border>("PlanDialogOverlay").IsVisible && C<TextBlock>("PlanSourceSummary").Text!.Contains("已选 0 词"),
                "archive plan opens a checklist with no implicit whole-archive selection");
            C<TextBox>("PlanNameInput").Text = "empty fixture"; Click("PlanCreateConfirmBtn");
            Check(C<TextBlock>("PlanDialogError").Text!.Contains("选择"), "empty plan selection cannot be created");
            Click("PlanCancelBtn");
            Field<List<WordItem>>("_allWords").Single(w => w.Word == "planfixture").Selected = true;
            Click("ArchivePlanCreateBtn");
            Check(C<TextBlock>("PlanSourceSummary").Text!.Contains("已选 1 词"),
                "archive plan preselects the current archive checklist selection");
            await SelectPlanWords("planfixture");
            C<TextBox>("PlanNameInput").Text = "Archive fixture";
            C<NumericUpDown>("PlanDailyCountInput").Value = 1;
            Click("PlanCreateConfirmBtn");
            var planWord = planArchive.GetAllWords().Single(w => w.Word == "planfixture");
            using var planSql = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = planArchive.DatabasePath, Pooling = false }.ToString());
            planSql.Open();
            string ReviewState()
            {
                using var command = planSql.CreateCommand();
                command.CommandText = "SELECT stage || '|' || status || '|' || COALESCE(next_review_date,'') || '|' || COALESCE(last_reviewed_at,'') || '|' || review_count || '|' || (SELECT count(*) FROM review_logs WHERE word_id=$id) FROM words WHERE id=$id";
                command.Parameters.AddWithValue("$id", planWord.Id);
                return (string)command.ExecuteScalar()!;
            }
            var reviewBeforePlan = ReviewState();
            var plansPath = Path.Combine(folder, "daily-study-plans.json");
            Check(C<StackPanel>("ArchivePlanRows").Children.Count == 1 && File.Exists(plansPath),
                "archive checklist creates a persisted frozen plan");
            var protectedPlanJson = File.ReadAllText(plansPath);
            var readFailedField = typeof(MainWindow).GetField("_planReadFailed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            readFailedField.SetValue(window, true);
            Check(!(bool)Call("SaveStudyPlans")! && File.ReadAllText(plansPath) == protectedPlanJson,
                "plan read-failure guard protects the original JSON from overwrite");
            readFailedField.SetValue(window, false); Call("SetStatus", "计划已创建。");
            await Snapshot("plans-manager-light");
            C<ComboBox>("SettingsThemeCombo").SelectedIndex = 1;
            window.Width = 840; window.Height = 600; await Snapshot("plans-manager-dark-840");
            Check(C<Button>("ArchivePlanCreateBtn").IsEffectivelyVisible && C<StackPanel>("ArchivePlanRows").Bounds.Width > 100,
                "plan manager actions and list remain usable at 840x600 dark theme");
            C<ComboBox>("SettingsThemeCombo").SelectedIndex = 0; window.Width = 1000; window.Height = 780;
            C<CheckBox>("ReduceMotionBox").IsChecked = true;
            Click("PlanStartBtn"); await Task.Delay(100);
            var archiveRound = Field<StudyRound<string>>("_focusRound");
            Check(Field<bool>("_wordFocusActive") && C<TextBlock>("ResultWordText").Text == "planfixture"
                && C<Button>("FocusStartRecallBtn").IsEffectivelyVisible && !C<Grid>("PageStudyPlan").IsVisible,
                "plan starts the shared immersive first-learn card");
            await Snapshot("plans-flashcards");
            Check(!C<Button>("FocusSaveBtn").IsEffectivelyVisible && !C<Button>("FocusMasterBtn").IsEffectivelyVisible,
                "plan cards hide archive-save and master actions");
            KeyPress(Key.C); KeyPress(Key.Delete); await InvokeAsync("MasterFocusAsync");
            Check(ReviewState() == reviewBeforePlan && archiveRound.Completed == 0,
                "plan save and master shortcuts cannot modify SQLite or bypass plan recall");
            await InvokeAsync("CompleteFocusLearnAsync");
            await InvokeAsync("RateFocusedWordAsync", StudyRating.Forgot);
            Check(archiveRound.Forgot == 1 && ReviewState() == reviewBeforePlan,
                "plan forgotten rating records local recall progress without SQLite review writes");
            await InvokeAsync("AdvanceFocusAsync"); await InvokeAsync("CompleteFocusLearnAsync");
            for (var known = 1; known <= 3; known++)
            {
                KeyPress(Key.Q); KeyPress(Key.Q, true); await Task.Delay(60);
                Check(archiveRound.Known == known && archiveRound.Completed == (known == 3 ? 1 : 0)
                    && ReviewState() == reviewBeforePlan, "plan Q recognition requires three known rounds and leaves SQLite untouched: " + known);
                if (known < 3) await InvokeAsync("AdvanceFocusAsync");
            }
            await InvokeAsync("AdvanceFocusAsync");
            Check(C<Border>("PlanSpellingOverlay").IsVisible && C<Grid>("PageStudyPlan").IsVisible
                && new DailyStudyPlanStore(plansPath).Load().Single(p => p.Name == "Archive fixture").Status == DailyStudyPlanStatus.Completed,
                "completed plan returns to manager and offers optional spelling");
            await Snapshot("plans-spelling-prompt");
            Click("PlanSpellingSkipBtn");

            Click("IeltsPlanCreateBtn");
            Check(C<ComboBox>("PlanBookInput").ItemCount == 4 && C<ComboBox>("PlanUnitInput").ItemCount == 23
                && C<ComboBox>("PlanUnitInput").SelectedIndex == 1 && C<TextBlock>("PlanSourceSummary").Text!.Contains("已选"),
                "IELTS plan defaults to the current chapter and offers the whole book");
            for (var book = 0; book < 4; book++)
            {
                C<ComboBox>("PlanBookInput").SelectedIndex = book;
                C<ComboBox>("PlanUnitInput").SelectedIndex = 0;
                Check(((LearningSection)C<ComboBox>("PlanUnitInput").SelectedItem!).Title.Contains("整本词书")
                    && Field<List<DailyStudyPlanWord>>("_planCandidates").Count > 0,
                    "IELTS plan book selector supplies its complete frozen book: " + book);
            }
            C<ComboBox>("PlanBookInput").SelectedIndex = 0; C<ComboBox>("PlanUnitInput").SelectedIndex = 1;
            Click("PlanClearSelectionBtn"); C<TextBox>("PlanWordSearch").Text = "atmosphere"; Click("PlanSelectAllBtn");
            Check(C<TextBlock>("PlanSourceSummary").Text!.Contains("已选 1 词"), "plan select-all applies to the searched subset");
            await SelectPlanWords("atmosphere", "hydrosphere");
            Check(C<TextBlock>("PlanSourceSummary").Text!.Contains("已选 2 词"), "IELTS checklist summary identifies the selected subset");
            var selectedBeforeLanguage = new HashSet<string>(Field<HashSet<string>>("_planSelectedIds"));
            window.SetUiLanguage("en");
            Check(C<Button>("NavPlans").Content?.ToString() == "Study plans"
                && C<Button>("PlanSelectAllBtn").Content?.ToString() == "Select all"
                && C<ComboBox>("PlanBookInput").SelectedItem?.ToString() == "IELTS vocabulary"
                && ((IEnumerable<LearningSection>)C<ComboBox>("PlanUnitInput").ItemsSource!).First().Title == "Entire book"
                && Field<HashSet<string>>("_planSelectedIds").SetEquals(selectedBeforeLanguage),
                "open plan dialog translates navigation, selectors and whole-book label while preserving chosen words");
            window.SetUiLanguage("zh");
            Check(C<ComboBox>("PlanBookInput").SelectedItem?.ToString() == "词汇真经"
                && ((IEnumerable<LearningSection>)C<ComboBox>("PlanUnitInput").ItemsSource!).First().Title == "整本词书"
                && Field<HashSet<string>>("_planSelectedIds").SetEquals(selectedBeforeLanguage),
                "plan dialog restores Chinese labels without resetting its selected subset");
            C<TextBox>("PlanNameInput").Text = "IELTS fixture"; C<NumericUpDown>("PlanDailyCountInput").Value = 2;
            await Snapshot("plans-create-dialog");
            Check(C<TextBlock>("PlanEstimate").IsEffectivelyVisible && C<TextBlock>("PlanEstimate").Text!.Contains("预计 1 天")
                && C<ScrollViewer>("PlanDialogScroll").VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                "create dialog keeps estimated days visible with its scrollbar hidden");
            Click("PlanCreateConfirmBtn");
            var storedPlans = new DailyStudyPlanStore(plansPath).Load();
            Check(storedPlans.Count == 2 && storedPlans.Single(p => p.Name == "IELTS fixture").Words.Select(w => w.Word).SequenceEqual(["atmosphere", "hydrosphere"]),
                "IELTS plan freezes only checklist words independently of the archive plan");
            Click("IeltsPlanCreateBtn"); await SelectPlanWords("atmosphere"); C<TextBox>("PlanNameInput").Text = "IELTS overlap fixture";
            Click("PlanCreateConfirmBtn");
            Check(C<StackPanel>("PlanOverlapActions").IsVisible && C<TextBlock>("PlanDialogError").Text!.Contains("IELTS fixture")
                && C<TextBlock>("PlanDialogError").Text!.Contains("atmosphere"), "same-source overlap names its active plan and word");
            Click("PlanContinueOverlapBtn");
            Check(new DailyStudyPlanStore(plansPath).Load().Count == 3, "explicit continue creates overlapping plan");
            Click("IeltsPlanCreateBtn"); await SelectPlanWords("atmosphere"); C<TextBox>("PlanNameInput").Text = "cancelled overlap";
            Click("PlanCreateConfirmBtn"); Click("PlanCancelOverlapBtn");
            Check(new DailyStudyPlanStore(plansPath).Load().Count == 3, "cancel overlap leaves persisted plans unchanged");
            var liveIelts = Field<List<DailyStudyPlan>>("_studyPlans").Single(p => p.Name == "IELTS fixture");
            var errorsBeforePlan = new HashSet<string>(Field<LearningProgress>("_learningProgress").Errors);
            var archiveCountBeforePlan = planArchive.GetAllWords().Count;
            Call("StartStudyPlan", liveIelts); await Task.Delay(80);
            var ieltsPlanRound = Field<StudyRound<string>>("_focusRound");
            while (ieltsPlanRound.CurrentStep == StudyStep.Learn) await InvokeAsync("CompleteFocusLearnAsync");
            await InvokeAsync("RateFocusedWordAsync", StudyRating.Known);
            foreach (var width in new[] { 840, 1000, 1280 })
            {
                window.Width=width; await Task.Delay(100);
                var bar=C<Grid>("WordFocusActions"); var next=C<Button>("FocusNextBtn"); var wrong=C<Button>("FocusWrongBtn");
                var left=next.TranslatePoint(new Point(next.Bounds.Width/2,0),bar)!.Value.X;
                var right=wrong.TranslatePoint(new Point(wrong.Bounds.Width/2,0),bar)!.Value.X;
                Check(next.IsVisible && wrong.IsVisible && Math.Abs(left+right-bar.Bounds.Width)<1 && Math.Abs(left-bar.Bounds.Width/4)<1, "focus answer buttons are symmetric at width "+width);
            }
            await Snapshot("v321-focus-answer-symmetric");
            window.Width=1000;
            await InvokeAsync("ReclassifyFocusAsync");
            await Task.Delay(100);
            var singleBar=C<Grid>("WordFocusActions"); var singleNext=C<Button>("FocusNextBtn");
            Check(!C<Button>("FocusWrongBtn").IsVisible && Math.Abs(singleNext.TranslatePoint(new Point(singleNext.Bounds.Width/2,0),singleBar)!.Value.X-singleBar.Bounds.Width/2)<1, "single focus next action is centered after forgotten reclassification");
            await Snapshot("v321-focus-single-next-centered");
            Check(liveIelts.ForgotWordIds.Count == 1 && ieltsPlanRound.Known == 0 && ieltsPlanRound.Forgot == 1,
                "plan answer reclassification rolls back known and records forgotten word locally");
            await InvokeAsync("AdvanceFocusAsync");
            if (ieltsPlanRound.CurrentStep == StudyStep.Learn) await InvokeAsync("CompleteFocusLearnAsync");
            await InvokeAsync("RateFocusedWordAsync", StudyRating.Unsure); await InvokeAsync("AdvanceFocusAsync");
            if (ieltsPlanRound.CurrentStep == StudyStep.Learn) await InvokeAsync("CompleteFocusLearnAsync");
            var blocked = Path.Combine(folder, "blocked-plan-path"); File.WriteAllText(blocked, "block directory creation");
            var storeField = typeof(MainWindow).GetField("_studyPlanStore", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalPlanStore = storeField.GetValue(window);
            storeField.SetValue(window, new DailyStudyPlanStore(Path.Combine(blocked, "daily-study-plans.json")));
            var persistedBeforeFailure = File.ReadAllText(plansPath);
            // 评分路径的计划 JSON 现在由跨存储 journal 落盘，不再经过 _studyPlanStore：
            // 必须把**真实目标文件**占成目录才能真的注入交付失败（换 store 已经拦不住它）。
            var parkedPlans = plansPath + ".parked";
            File.Move(plansPath, parkedPlans, overwrite: true);
            Directory.CreateDirectory(plansPath);
            var cardBeforeFailure = C<TextBlock>("ResultWordText").Text;
            var knownBeforeFailure = ieltsPlanRound.Known; var forgotBeforeFailure = ieltsPlanRound.Forgot;
            var forgotIdsBeforeFailure = new HashSet<string>(liveIelts.ForgotWordIds);
            await InvokeAsync("RateFocusedWordAsync", StudyRating.Forgot);
            Check(liveIelts.CompletedWordIds.Count == 0 && liveIelts.Status == DailyStudyPlanStatus.Active
                && ieltsPlanRound.Known == knownBeforeFailure && ieltsPlanRound.Forgot == forgotBeforeFailure
                && liveIelts.ForgotWordIds.SetEquals(forgotIdsBeforeFailure) && C<TextBlock>("ResultWordText").Text == cardBeforeFailure,
                "failed plan rating save rolls back card, streak and forgotten set");
            // 真实不变量：交付失败必须把目标状态留在 outbox 里供重放（"文件没被动过"在这里不可观测：
            // 路径被占成目录，任何写入都不可能发生，只比较 parked 副本等于复述那次 File.Move）。
            using (var outbox = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = planArchive.DatabasePath, Pooling = false }.ToString()))
            {
                outbox.Open();
                using var command = outbox.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM mutation_outbox WHERE applied_at_utc IS NULL";
                Check(Convert.ToInt32(command.ExecuteScalar()) > 0,
                    "failed plan delivery keeps a replayable pending target in the outbox");
            }
            Directory.Delete(plansPath); File.Move(parkedPlans, plansPath);
            // 那次失败留下的待办目标按设计保留；恢复可写后先排空它，后续保存再写入最新状态。
            Call("MemoryReplayJournal");
            Call("StopStudyPlan", liveIelts);
            Check(liveIelts.Status == DailyStudyPlanStatus.Active, "failed stop save keeps plan active");
            storeField.SetValue(window, originalPlanStore);
            for (var guard = 0; guard < 40 && liveIelts.CompletedWordIds.Count == 0; guard++)
            {
                if (ieltsPlanRound.CurrentStep == StudyStep.Learn) await InvokeAsync("CompleteFocusLearnAsync");
                else { await InvokeAsync("RateFocusedWordAsync", StudyRating.Known); await InvokeAsync("AdvanceFocusAsync"); }
            }
            Check(liveIelts.CompletedWordIds.Count == 1
                && new DailyStudyPlanStore(plansPath).Load().Single(p => p.Id == liveIelts.Id).CompletedWordIds.Count == 1,
                "partial plan progress persists after the first completed word");
            var completedBeforeResume = liveIelts.CompletedWordIds.Single(); Call("ExitWordFocus");
            Call("StartStudyPlan", liveIelts); await Task.Delay(80);
            var resumedPlanRound = Field<StudyRound<string>>("_focusRound");
            Check(resumedPlanRound.Total == 1 && resumedPlanRound.Current != completedBeforeResume
                && liveIelts.CurrentBatchWordIds.Count == 2, "partial resume excludes completed cards and preserves the complete daily batch");
            await FinishPlanRound(); await InvokeAsync("AdvanceFocusAsync");
            Check(C<Border>("PlanSpellingOverlay").IsVisible && planArchive.GetAllWords().Count == archiveCountBeforePlan
                && Field<LearningProgress>("_learningProgress").Errors.SetEquals(errorsBeforePlan) && ReviewState() == reviewBeforePlan,
                "IELTS plan ratings and completion leave archive state and IELTS errors unchanged");
            C<NumericUpDown>("TypingCount").Value = 1; C<CheckBox>("TypingAll").IsChecked = false;
            C<NumericUpDown>("IeltsCount").Value = 1; C<CheckBox>("IeltsAll").IsChecked = false;
            C<ComboBox>("PlanSpellingScope").SelectedIndex = 0; C<ComboBox>("PlanSpellingMode").SelectedIndex = 0;
            Click("PlanSpellingStartBtn"); await Task.Delay(80);
            var allPlanTyping = Field<TypingSession>("_typingSession");
            var typingWordsField = typeof(TypingSession).GetField("_words", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var allPlanTypingWords = (List<LearningWord>)typingWordsField.GetValue(allPlanTyping)!;
            Check(allPlanTyping.Count == 2 && allPlanTyping.Hints && allPlanTypingWords.Select(w => w.Word).ToHashSet().SetEquals(["atmosphere", "hydrosphere"]),
                "plan full-batch hinted spelling uses every frozen batch word without global downsampling");
            var planTypingJsonBeforeNavigation = File.ReadAllText(plansPath);
            var typedBeforeNavigation = new HashSet<string>(Field<LearningProgress>("_learningProgress").Typed);
            Call("ShowPage", "settings"); Click("NavTyping");
            Check(C<Grid>("PageTyping").IsVisible && Field<Grid>("_typingSetupView").IsVisible
                && !Field<bool>("_typingPlaying") && !Field<bool>("_planTypingActive")
                && File.ReadAllText(plansPath) == planTypingJsonBeforeNavigation
                && Field<LearningProgress>("_learningProgress").Errors.SetEquals(errorsBeforePlan)
                && Field<LearningProgress>("_learningProgress").Typed.SetEquals(typedBeforeNavigation)
                && planArchive.GetAllWords().Count == archiveCountBeforePlan && ReviewState() == reviewBeforePlan,
                "leaving plan spelling then navigating back shows inactive setup without writing the frozen deck to global progress");
            Call("StartStudyPlan", liveIelts);
            Check(C<Border>("PlanSpellingOverlay").IsVisible, "completed plan can reopen its optional spelling prompt");
            C<ComboBox>("PlanSpellingScope").SelectedIndex = 1; C<ComboBox>("PlanSpellingMode").SelectedIndex = 1;
            Click("PlanSpellingStartBtn"); await Task.Delay(80);
            var forgotPlanTyping = Field<TypingSession>("_typingSession");
            var forgotPlanTypingWords = (List<LearningWord>)typingWordsField.GetValue(forgotPlanTyping)!;
            var forgottenBatchWords = liveIelts.Words.Where(w => liveIelts.CurrentBatchWordIds.Contains(w.Id) && liveIelts.ForgotWordIds.Contains(w.Id)).Select(w => w.Word).ToHashSet();
            Check(forgotPlanTyping.Count == forgottenBatchWords.Count && !forgotPlanTyping.Hints
                && forgotPlanTypingWords.Select(w => w.Word).ToHashSet().SetEquals(forgottenBatchWords),
                "plan forgotten-only dictation uses exactly the forgotten words in its daily batch");
            Click("TypingExitBtn"); C<CheckBox>("ReduceMotionBox").IsChecked = false;

            // Lifecycle actions are transactional and remain isolated from global learning progress.
            {
                var lifecycleErrors = new HashSet<string>(Field<LearningProgress>("_learningProgress").Errors);
                var lifecycleTyped = new HashSet<string>(Field<LearningProgress>("_learningProgress").Typed);
                var lifecycleArchiveCount = planArchive.GetAllWords().Count;
                string ArchiveLearningState()
                {
                    var rows = new List<object?[]>();
                    foreach (var query in new[] { "SELECT * FROM words ORDER BY id", "SELECT * FROM review_logs ORDER BY id", "SELECT * FROM review_snapshots ORDER BY log_id" })
                    {
                        using var command = planSql.CreateCommand(); command.CommandText = query;
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount)
                            .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
                    }
                    return System.Text.Json.JsonSerializer.Serialize(rows);
                }
                var lifecycleReview = ArchiveLearningState();
                var lifecycleDate = DateOnly.FromDateTime(DateTime.Now);
                var lifecycleWords = new[] { "oxygen", "wood", "apple", "water" }.Select((word, i) =>
                    new DailyStudyPlanWord { Id = "lifecycle:" + i, Word = word, Phonetic = "/fixture/",
                        Meaning = "冻结词义 " + i, Definition = "frozen definition " + i,
                        Example = "Frozen example " + i, AudioPath = "fixture-audio-" + i }).ToList();
                var lifecycle = DailyStudyPlanRules.Create("Lifecycle fixture", DailyStudyPlanSource.Ielts,
                    "Frozen lifecycle source", lifecycleWords, 2, false, 701);
                _ = new DailyStudyPlanSession(lifecycle, lifecycleDate.AddDays(-1));
                Check(DailyStudyPlanRules.CompleteWord(lifecycle, lifecycleWords[0].Id, lifecycleDate.AddDays(-1))
                    && DailyStudyPlanRules.CompleteWord(lifecycle, lifecycleWords[1].Id, lifecycleDate.AddDays(-1)),
                    "lifecycle fixture persists a previous completed daily batch");
                _ = new DailyStudyPlanSession(lifecycle, lifecycleDate);
                Check(DailyStudyPlanRules.CompleteWord(lifecycle, lifecycleWords[2].Id, lifecycleDate),
                    "lifecycle fixture has a partly completed current batch");
                lifecycle.ForgotWordIds.Add(lifecycleWords[3].Id);
                var otherSource = DailyStudyPlanRules.Create("Cross-source lifecycle fixture", DailyStudyPlanSource.Archive,
                    "Archive lifecycle source", lifecycleWords, 2, false, 702);
                Field<List<DailyStudyPlan>>("_studyPlans").AddRange([lifecycle, otherSource]);
                Check((bool)Call("SaveStudyPlans")!, "lifecycle fixtures are persisted before manager actions");
                Call("RenderStudyPlanLists"); Click("NavPlans");
                DailyStudyPlan Lifecycle() => Field<List<DailyStudyPlan>>("_studyPlans").Single(p => p.Id == lifecycle.Id);
                DailyStudyPlan PersistedLifecycle() => new DailyStudyPlanStore(plansPath).Load().Single(p => p.Id == lifecycle.Id);
                Grid PlanCard(string name) => C<Grid>("PageStudyPlan").GetLogicalDescendants().OfType<Button>()
                    .Single(b => b.Name == "PlanDetailBtn" && b.Content?.ToString() == name)
                    .GetLogicalAncestors().OfType<Grid>().First();
                Button CardButton(string name, string button) => PlanCard(name).GetLogicalDescendants().OfType<Button>().Single(b => b.Name == button);
                void ClickCard(string name, string button) => CardButton(name, button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void ExpandPlanHistory()
                {
                    foreach (var history in C<Grid>("PageStudyPlan").GetLogicalDescendants().OfType<Expander>()) history.IsExpanded = true;
                }
                string FrozenState(DailyStudyPlan plan) => System.Text.Json.JsonSerializer.Serialize(new
                {
                    plan.Id, plan.Source, plan.SourceLabel, plan.CreatedAt, plan.OriginalWordIds,
                    Words = plan.Words.OrderBy(w => w.Id, StringComparer.Ordinal),
                    Completed = plan.CompletedWordIds.OrderBy(id => id, StringComparer.Ordinal),
                    plan.CurrentBatchWordIds, plan.CurrentBatchRandomOrder,
                    Forgot = plan.ForgotWordIds.OrderBy(id => id, StringComparer.Ordinal), plan.LastBatchCompletedDate
                });
                var frozenLifecycle = FrozenState(Lifecycle());
                var originalWordOrder = Lifecycle().Words.Select(w => w.Id).ToList();
                var originalSeed = Lifecycle().ShuffleSeed;
                ClickCard(lifecycle.Name, "PlanAdjustBtn");
                Check(C<Border>("PlanDialogOverlay").IsVisible && C<TextBlock>("PlanDialogTitle").Text == "调整学习计划"
                    && C<Button>("PlanCreateConfirmBtn").Content?.ToString() == "保存调整"
                    && !C<StackPanel>("PlanWordScope").IsVisible
                    && C<TextBox>("PlanNameInput").Text == lifecycle.Name
                    && C<NumericUpDown>("PlanDailyCountInput").Value == 2 && C<CheckBox>("PlanRandomInput").IsChecked == false,
                    "active plan adjustment exposes only its current name, daily count and order");
                var beforeAdjustmentNavigation = File.ReadAllText(plansPath);
                Click("NavSettings");
                Check(Field<string>("_currentPage") == "plans" && C<Grid>("PageStudyPlan").IsVisible
                    && !C<ScrollViewer>("PageSettings").IsVisible && C<Border>("PlanDialogOverlay").IsVisible
                    && File.ReadAllText(plansPath) == beforeAdjustmentNavigation,
                    "background settings navigation cannot leave the plan manager while adjustment is open");
                C<TextBox>("PlanNameInput").Text = "Lifecycle adjusted"; C<NumericUpDown>("PlanDailyCountInput").Value = 1;
                await Snapshot("plans-adjust-dialog");
                Check(C<Border>("PlanDialogCard").Bounds.Height <= 440 && C<Button>("PlanCreateConfirmBtn").IsEffectivelyVisible,
                    "adjustment dialog stays compact with its save action visible");
                Click("PlanCreateConfirmBtn");
                Check(!C<Border>("PlanDialogOverlay").IsVisible && Lifecycle().Name == "Lifecycle adjusted" && Lifecycle().DailyWordCount == 1
                    && FrozenState(Lifecycle()) == frozenLifecycle && FrozenState(PersistedLifecycle()) == frozenLifecycle
                    && Lifecycle().Words.Select(w => w.Id).SequenceEqual(originalWordOrder) && Lifecycle().ShuffleSeed == originalSeed,
                    "name and count adjustment preserves frozen order, seed, batch, completed, forgotten and last date in memory and JSON");
                var adjustedJson = File.ReadAllText(plansPath);
                ClickCard(Lifecycle().Name, "PlanAdjustBtn"); C<TextBox>("PlanNameInput").Text = "Cancelled adjustment";
                Click("PlanCancelBtn");
                Check(Lifecycle().Name == "Lifecycle adjusted" && File.ReadAllText(plansPath) == adjustedJson,
                    "cancelling adjustment leaves both live and persisted plans unchanged");
                ClickCard(Lifecycle().Name, "PlanAdjustBtn"); C<TextBox>("PlanNameInput").Text = ""; Click("PlanCreateConfirmBtn");
                Check(C<Border>("PlanDialogOverlay").IsVisible && !string.IsNullOrWhiteSpace(C<TextBlock>("PlanDialogError").Text)
                    && File.ReadAllText(plansPath) == adjustedJson && FrozenState(Lifecycle()) == frozenLifecycle,
                    "invalid adjustment keeps the dialog open without changing frozen progress or JSON");
                C<TextBox>("PlanNameInput").Text = "Lifecycle random"; C<CheckBox>("PlanRandomInput").IsChecked = true;
                storeField.SetValue(window, new DailyStudyPlanStore(Path.Combine(blocked, "daily-study-plans.json")));
                try
                {
                    Click("PlanCreateConfirmBtn");
                    Check(C<Border>("PlanDialogOverlay").IsVisible && C<TextBlock>("PlanDialogError").Text!.Contains("保存失败")
                        && Lifecycle().Name == "Lifecycle adjusted" && !Lifecycle().RandomOrder
                        && FrozenState(Lifecycle()) == frozenLifecycle && Lifecycle().ShuffleSeed == originalSeed
                        && Lifecycle().Words.Select(w => w.Id).SequenceEqual(originalWordOrder) && File.ReadAllText(plansPath) == adjustedJson,
                        "failed adjustment rolls back every live field and preserves the original JSON");
                }
                finally { storeField.SetValue(window, originalPlanStore); }
                Click("PlanCreateConfirmBtn");
                Check(Lifecycle().Name == "Lifecycle random" && Lifecycle().RandomOrder && PersistedLifecycle().RandomOrder
                    && FrozenState(Lifecycle()) == frozenLifecycle && FrozenState(PersistedLifecycle()) == frozenLifecycle,
                    "order adjustment preserves all frozen word content and the existing batch order and progress");
                ClickCard(Lifecycle().Name, "PlanStopBtn"); ExpandPlanHistory();
                Check(Lifecycle().Status == DailyStudyPlanStatus.Stopped
                    && CardButton(Lifecycle().Name, "PlanResumeBtn").IsEffectivelyVisible
                    && CardButton(Lifecycle().Name, "PlanDeleteBtn").IsEffectivelyVisible
                    && !PlanCard(Lifecycle().Name).GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "PlanAdjustBtn"),
                    "stopped history cards expose resume and delete while active-only adjustment is absent");
                await Snapshot("plans-stopped-history");
                var stoppedJson = File.ReadAllText(plansPath);
                storeField.SetValue(window, new DailyStudyPlanStore(Path.Combine(blocked, "daily-study-plans.json")));
                try
                {
                    ClickCard(Lifecycle().Name, "PlanResumeBtn");
                    Check(Lifecycle().Status == DailyStudyPlanStatus.Stopped && File.ReadAllText(plansPath) == stoppedJson
                        && FrozenState(Lifecycle()) == frozenLifecycle,
                        "failed direct resume keeps stopped status, frozen batch and original JSON");
                }
                finally { storeField.SetValue(window, originalPlanStore); }
                Call("RenderStudyPlanLists"); ExpandPlanHistory(); ClickCard(Lifecycle().Name, "PlanResumeBtn");
                Check(!C<Border>("PlanActionOverlay").IsVisible && Lifecycle().Status == DailyStudyPlanStatus.Active
                    && PersistedLifecycle().Status == DailyStudyPlanStatus.Active
                    && FrozenState(Lifecycle()) == frozenLifecycle && FrozenState(PersistedLifecycle()) == frozenLifecycle,
                    "cross-source matching words allow direct resume while preserving batch, completed, forgotten and last date");
                Call("StartStudyPlan", Lifecycle()); await Task.Delay(80);
                Check(Field<StudyRound<string>>("_focusRound").Total == 1
                    && Field<StudyRound<string>>("_focusRound").Current == lifecycleWords[3].Id
                    && FrozenState(Lifecycle()) == frozenLifecycle,
                    "resumed plan continues only the unfinished word from its original current batch after daily-count adjustment");
                Call("ExitWordFocus"); Click("NavPlans"); Call("StopStudyPlan", Lifecycle());
                var sameSource = DailyStudyPlanRules.Create("Same-source lifecycle fixture", DailyStudyPlanSource.Ielts,
                    "Other IELTS lifecycle source", [lifecycleWords[3]], 1, false, 703);
                Field<List<DailyStudyPlan>>("_studyPlans").Add(sameSource);
                Check((bool)Call("SaveStudyPlans")!, "same-source overlap fixture is persisted");
                Call("RenderStudyPlanLists"); ExpandPlanHistory();
                var overlapJson = File.ReadAllText(plansPath);
                ClickCard(Lifecycle().Name, "PlanResumeBtn");
                Check(C<Border>("PlanActionOverlay").IsVisible
                    && C<TextBlock>("PlanActionMessage").Text!.Contains(sameSource.Name)
                    && C<TextBlock>("PlanActionMessage").Text!.Contains(lifecycleWords[3].Word)
                    && !C<TextBlock>("PlanActionMessage").Text!.Contains(otherSource.Name)
                    && Lifecycle().Status == DailyStudyPlanStatus.Stopped && File.ReadAllText(plansPath) == overlapJson,
                    "resume overlap warning names only same-source active plans and their overlapping words before writing");
                Click("PlanActionCancelBtn");
                Check(!C<Border>("PlanActionOverlay").IsVisible && Lifecycle().Status == DailyStudyPlanStatus.Stopped
                    && File.ReadAllText(plansPath) == overlapJson && FrozenState(Lifecycle()) == frozenLifecycle,
                    "cancelling overlap resume leaves stopped progress and original JSON unchanged");
                ClickCard(Lifecycle().Name, "PlanResumeBtn");
                storeField.SetValue(window, new DailyStudyPlanStore(Path.Combine(blocked, "daily-study-plans.json")));
                try
                {
                    Click("PlanActionConfirmBtn");
                    Check(C<Border>("PlanActionOverlay").IsVisible && C<TextBlock>("PlanActionError").Text!.Contains("保存失败")
                        && Lifecycle().Status == DailyStudyPlanStatus.Stopped && FrozenState(Lifecycle()) == frozenLifecycle
                        && File.ReadAllText(plansPath) == overlapJson,
                        "failed confirmed overlap resume keeps the warning open and rolls back status and JSON");
                }
                finally { storeField.SetValue(window, originalPlanStore); }
                Click("PlanActionConfirmBtn");
                Check(!C<Border>("PlanActionOverlay").IsVisible && Lifecycle().Status == DailyStudyPlanStatus.Active
                    && PersistedLifecycle().Status == DailyStudyPlanStatus.Active && FrozenState(Lifecycle()) == frozenLifecycle,
                    "explicit overlap continue resumes the existing plan without resetting progress");
                var activeJson = File.ReadAllText(plansPath);
                Call("DeleteStudyPlan", Lifecycle()); Call("DeleteStudyPlan", Field<List<DailyStudyPlan>>("_studyPlans").Single(p => p.Name == "Archive fixture"));
                Check(!C<Border>("PlanActionOverlay").IsVisible && File.ReadAllText(plansPath) == activeJson
                    && Field<List<DailyStudyPlan>>("_studyPlans").Any(p => p.Id == lifecycle.Id),
                    "delete protects active and completed plans without opening a confirmation");
                Call("StopStudyPlan", Lifecycle()); ExpandPlanHistory();
                var beforeDeleteJson = File.ReadAllText(plansPath);
                var retainedPlanIds = Field<List<DailyStudyPlan>>("_studyPlans").Where(p => p.Id != lifecycle.Id).Select(p => p.Id).ToHashSet();
                ClickCard(Lifecycle().Name, "PlanDeleteBtn");
                Check(C<Border>("PlanActionOverlay").IsVisible && C<TextBlock>("PlanActionMessage").Text!.Contains(Lifecycle().Name)
                    && File.ReadAllText(plansPath) == beforeDeleteJson,
                    "stopped deletion opens a named confirmation before changing data");
                Click("NavVocab");
                Check(Field<string>("_currentPage") == "plans" && C<Grid>("PageStudyPlan").IsVisible
                    && !C<Grid>("PageVocab").IsVisible && C<Border>("PlanActionOverlay").IsVisible
                    && File.ReadAllText(plansPath) == beforeDeleteJson && FrozenState(Lifecycle()) == frozenLifecycle,
                    "background archive navigation cannot bypass stopped-plan deletion confirmation or write progress");
                Click("PlanActionCancelBtn");
                Check(!C<Border>("PlanActionOverlay").IsVisible && File.ReadAllText(plansPath) == beforeDeleteJson
                    && FrozenState(Lifecycle()) == frozenLifecycle, "cancelled deletion preserves the stopped plan and its JSON");
                ClickCard(Lifecycle().Name, "PlanDeleteBtn");
                storeField.SetValue(window, new DailyStudyPlanStore(Path.Combine(blocked, "daily-study-plans.json")));
                try
                {
                    Click("PlanActionConfirmBtn");
                    Check(C<Border>("PlanActionOverlay").IsVisible && C<TextBlock>("PlanActionError").Text!.Contains("保存失败")
                        && Lifecycle().Status == DailyStudyPlanStatus.Stopped && FrozenState(Lifecycle()) == frozenLifecycle
                        && File.ReadAllText(plansPath) == beforeDeleteJson,
                        "failed confirmed deletion keeps the plan, frozen progress, JSON and retry confirmation intact");
                }
                finally { storeField.SetValue(window, originalPlanStore); }
                Click("PlanActionConfirmBtn");
                Check(!C<Border>("PlanActionOverlay").IsVisible
                    && Field<List<DailyStudyPlan>>("_studyPlans").Select(p => p.Id).ToHashSet().SetEquals(retainedPlanIds)
                    && new DailyStudyPlanStore(plansPath).Load().Select(p => p.Id).ToHashSet().SetEquals(retainedPlanIds),
                    "confirmed stopped deletion removes exactly that plan from memory and persisted JSON");
                Check(Field<LearningProgress>("_learningProgress").Errors.SetEquals(lifecycleErrors)
                    && Field<LearningProgress>("_learningProgress").Typed.SetEquals(lifecycleTyped)
                    && planArchive.GetAllWords().Count == lifecycleArchiveCount && ArchiveLearningState() == lifecycleReview,
                    "adjust, stop, resume and delete leave global Errors, Typed and every archive SQLite word and review record unchanged");
            }
            Click("NavIelts");
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
            // IELTS 单词卡入口会连续初始化两次（先查词页进卡片、再按词表重开一轮），
            // 必须只开一个学习会话，否则长期层会把一次进入记成两轮。
            var ieltsSessionArchive = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            int FirstLearnSessions()
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ieltsSessionArchive.DatabasePath, Pooling = false }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM learning_sessions WHERE mode='FirstLearn'";
                return Convert.ToInt32(command.ExecuteScalar());
            }
            var sessionsBeforeCards = FirstLearnSessions();
            await (Task)Call("StartIeltsCardsAsync", new List<LearningWord> { new() { Id = "test:atmosphere", Words = ["atmosphere"] }, new() { Id = "test:hydrosphere", Words = ["hydrosphere"] }, new() { Id = "test:oxygen", Words = ["oxygen"] }, new() { Id = "test:wood", Words = ["wood"] } })!;
            Check(FirstLearnSessions() == sessionsBeforeCards + 1, "one word-card entry opens exactly one learning session");
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
