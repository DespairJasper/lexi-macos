using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Lexi.Controls;
using Lexi.Features.Ielts;

namespace Lexi;

public static class WindowsRedesignTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var report = new List<string>();
        var exit = 1;
        object? Call(string name, params object[] args) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        object? Field(string name) => typeof(MainWindow).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        void Click(string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Snapshot(Visual visual, string name)
        {
            await Task.Delay(150);
            var control = (Control)visual; control.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height), new Vector(96,96));
            bitmap.Render(visual); bitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, name + ".png"));
        }
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            report.Add("PASS " + name);
        }
        try
        {
            var archive = (IVocabularyArchive)Field("_vocabService")!;
            archive.AddWord("sessionisolation", "/test/", "会话隔离", "fixture");
            Call("RefreshWords");
            var word = archive.GetAllWords().Single(w => w.Word == "sessionisolation");
            Call("BeginReviewMemory", 1);
            var review = (LearningMemoryCoordinator)(Field("_reviewMemory") ?? Field("_learningMemory"))!;
            var reviewId = review.CurrentSessionId;
            Call("PresentReviewMemory", word);
            var focusRound = new StudyRound<string>(); focusRound.Reset([word.Word], StudyMode.Review);
            Set("_focusRound", focusRound);
            Call("BeginFocusMemory", StudyMode.Review, 1);
            Call("RateReviewMemory", StudyRating.Known, 0, 1, true);
            var store = ServiceFactory.OpenMemory(archive);
            Check(store.LoadEvents(reviewId).Any(e => e.Kind == InteractionEventKind.Rated),
                "review rating remains in review session after focus starts");
            Check(store.GetCard(WordKeyResolver.FromArchive(word).Key) != null,
                "isolated review produces FSRS card");
            var key = WordKeyResolver.FromArchive(word).Key;
            var dueBefore = store.GetCard(key)!.NextReviewAtUtc;
            Check(archive.GetAllWords().Single(w => w.Id == word.Id).NextReviewDate == dueBefore?.ToLocalTime().ToString("yyyy-MM-dd"),
                "archive shows the actual FSRS date");
            archive.ExecuteBatch([word.Id], "today");
            Check(store.QueryDue(DateTime.UtcNow, 100).Any(c => c.WordKey == key), "manual today changes actual FSRS queue");
            Check(archive.UndoLastLearningAction(word.Id) && store.GetCard(key)!.NextReviewAtUtc == dueBefore,
                "manual today undo restores exact FSRS timestamp");
            archive.ExecuteBatch([word.Id], "stage", 2);
            Check(store.GetCard(key)!.NextReviewAtUtc?.ToLocalTime().ToString("yyyy-MM-dd") ==
                archive.GetAllWords().Single(w => w.Id == word.Id).NextReviewDate &&
                store.GetCard(key)!.NextReviewAtUtc?.ToLocalTime().Date == DateTime.Today.AddDays(VocabularyService.StageOffsets[2]),
                "explicit stage adjustment changes actual FSRS schedule");
            archive.ExecuteBatch([word.Id], "restart");
            Check(store.GetCard(key)!.NextReviewAtUtc?.ToLocalTime().Date == DateTime.Today.AddDays(1),
                "explicit restart changes actual FSRS schedule");
            Call("UndoReviewMemory");
            Check(store.GetCard(WordKeyResolver.FromArchive(word).Key) == null,
                "review undo restores its own FSRS pre-state");
            store.DeleteSessionCheckpoint("review", reviewId);
            archive.ExecuteBatch([word.Id], "today"); Call("RefreshWords");
            window.FindControl<CheckBox>("ReduceMotionBox")!.IsChecked = true;
            Click("NavReview"); Click("ReviewRevealBtn"); Click("ReviewUnsureBtn");
            Click("ReviewRevealBtn"); Click("ReviewRememberBtn");
            var actualRound = (StudyRound<long>)Field("_reviewRound")!;
            Check(actualRound.CurrentStreak == 1, "actual review contains partial streak before restart");
            Call("PersistLearningSurface", "review");
            var actualSessionId = ((LearningMemoryCoordinator)Field("_reviewMemory")!).CurrentSessionId;
            Set("_reviewMemory", null); Set("_reviewRoundStarted", false); Set("_reviewUndo", null);
            actualRound.Reset([], StudyMode.Review); Call("OpenReviewDeck");
            Check(actualRound.CurrentStreak == 1 && ((LearningMemoryCoordinator)Field("_reviewMemory")!).CurrentSessionId == actualSessionId,
                "fresh coordinator resumes persisted review queue and session identity");
            Click("ReviewRoundUndoBtn");
            Check(actualRound.CurrentStreak == 0 && actualRound.CurrentTarget == 3,
                "restored review undo reverses the last rating");

            var plan = DailyStudyPlanRules.Create("恢复测试", DailyStudyPlanSource.Archive, "档案",
                [new DailyStudyPlanWord { Id = word.Id.ToString(), Word = word.Word, Meaning = "测试" }], 1, false, 1);
            var daily = new DailyStudyPlanSession(plan, DateOnly.FromDateTime(DateTime.Today));
            daily.CompleteLearn(); daily.Rate(StudyRating.Known, () => true);
            var roundJson = daily.Round.CaptureJson(w => w); var undoJson = daily.CaptureUndoJson();
            var restored = new DailyStudyPlanSession(plan, DateOnly.FromDateTime(DateTime.Today));
            restored.Round.RestoreJson(roundJson, id => id); restored.RestoreUndoJson(undoJson);
            Check(restored.CanUndo && restored.Undo(() => true) && restored.Round.CurrentStreak == 0,
                "plan restart preserves streak and undo progress");
            daily.OnUndoApplied = () => throw new IOException("injected SQLite failure");
            var beforeUndoFailure = daily.Round.CaptureJson(w => w);
            try { daily.Undo(() => true); throw new InvalidOperationException("undo failure was not propagated"); }
            catch (IOException) { }
            Check(daily.Round.CaptureJson(w => w) == beforeUndoFailure && daily.CanUndo,
                "failed durable plan undo retains rating and retry state");

            var sourceKey = WordKey.Form("customsourcefixture");
            var sourceMemory = new LearningMemoryCoordinator(store, new Fsrs6Scheduler(),
                clock: () => DateTime.UtcNow.AddDays(-40));
            sourceMemory.BeginSession(StudyMode.FirstLearn, WordSource.Form, "", 1);
            sourceMemory.OnPresented(sourceKey, "source-fixture", true);
            sourceMemory.OnRated(sourceKey, "source-fixture", StudyRating.Known, 0, 3, 0);
            sourceMemory.CommitWord(sourceKey, StudyMode.FirstLearn);
            ((VocabularyService)store).SaveSourceWordDetails(sourceKey.Key, "customsourcefixture", "/custom/", "自定义离线缺词释义", "custom context");
            var sourceDue = (List<WordItem>)Call("SourceDueWords")!;
            var sourceWord = sourceDue.Single(w => w.Word == "customsourcefixture");
            Check(sourceWord.Id < 0 && sourceWord.Translation == "自定义离线缺词释义"
                && archive.GetAllWords().All(w => w.Word != "customsourcefixture"),
                "source review retains its meaning without silently inserting an archive word");

            Call("ShowPage", "lookup");
            window.FindControl<TextBlock>("ResultWordText")!.Text = word.Word;
            window.FindControl<TextBlock>("ResultTranslationText")!.Text = "测试";
            window.FindControl<Border>("LookupResultCard")!.IsVisible = true;
            Call("EnterFocusFromLookup"); Call("MasterFocus"); Call("UndoFocus");
            Check(archive.GetAllWords().Single(w => w.Id == word.Id).Status == "learning"
                && !((StudyRound<string>)Field("_focusRound")!).IsFinished,
                "master action undo restores archive without requiring a rating event");
            Call("ExitFocus");

            Call("ShowIeltsCatalog");
            var workspace = (IeltsWorkspaceControl)Field("_ieltsWorkspace")!;
            object? IeltsField(string name) => workspace.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workspace);
            var rows = ((StackPanel)IeltsField("_wordListPanel")!).Children.OfType<VocabularyRow>().ToList();
            Check(rows.Count > 0, "IELTS renders vocabulary archive rows");
            var first = rows[0]; first.SetSelected(true); first.SetExpanded(true);
            Check(first.IsSelected && first.IsExpanded, "word row selection and expansion coexist");
            var search = (TextBox)IeltsField("_wordSearchBox")!;
            search.Text = "no-match-fixture-93854";
            Check(((HashSet<string>)Field("_ieltsSelected")!).Contains(first.WordId), "filter preserves actual shared selection");
            search.Text = "";
            var refreshed = ((StackPanel)IeltsField("_wordListPanel")!).Children.OfType<VocabularyRow>().First();
            Check(refreshed.IsSelected, "clearing search restores selected word row"); refreshed.SetExpanded(true);
            foreach (var width in new[] {1100d, 840d, 760d})
            {
                window.Width = width; window.Height = width == 760 ? 520 : 760;
                await Snapshot(window, "ielts-" + (int)width);
                var scroll = (ScrollViewer)IeltsField("_wordScrollViewer")!;
                Check(scroll.Bounds.Height > 40 && scroll.Bounds.Height < window.Bounds.Height,
                    "bounded IELTS scroll at width " + width);
            }
            window.Width = 1100; window.Height = 760;
            Click("ThemeToggleBtn"); await Snapshot(window, "ielts-dark"); Click("ThemeToggleBtn");
            var section = IeltsCatalog.Load().Sections.Where(s => s.Kind == "vocabulary").MaxBy(s => s.Entries.Count)!;
            Call("OpenPlanCreator", DailyStudyPlanSource.Ielts, section);
            await Task.Delay(200);
            var creator = (ContentControl)Field("_planCreatorContent")!;
            Check(creator.GetVisualDescendants().OfType<CheckBox>().Count(c => c.IsEffectivelyVisible) <= 12,
                "large chapter " + section.Entries.Count + " words uses paginated visible selection");
            await Snapshot(creator, "plan-large-step1");
            var next = creator.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "下一步：安排计划 →");
            Check(next.IsEffectivelyVisible && next.IsEnabled && next.TranslatePoint(default, creator)!.Value.Y < creator.Bounds.Height,
                "large plan retains visible fixed next action");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Snapshot(creator, "plan-large-step2");
            window.Width = 760; window.Height = 520; await Snapshot(creator, "plan-narrow-step2"); Call("ClosePlanCreator");
            exit = 0;
        }
        catch (Exception ex) { report.Add("FAIL " + (ex.InnerException ?? ex)); }
        finally
        {
            File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!,
                "redesign-result.txt"), report);
            window.ForceClose();
            (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
        }
    }
}
