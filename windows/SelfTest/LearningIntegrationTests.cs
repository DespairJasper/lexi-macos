using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.LogicalTree;

namespace Lexi;

public static class LearningIntegrationTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        var lines = new List<string>();
        var exit = 1;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            lines.Add("PASS " + label);
        }
        object? Call(string name, params object[] args) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        void Click(string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            var store = Field<IVocabularyArchive>("_vocabService");
            store.AddWord("apple", "/ˈæpəl/", "苹果", "a fruit");
            var word = store.GetAllWords().Single(w => w.Word == "apple");
            store.ExecuteBatch([word.Id], "today");
            Call("RefreshWords");
            window.FindControl<CheckBox>("ReduceMotionBox")!.IsChecked = true;
            Click("NavReview");
            Check(!window.FindControl<Border>("ReviewAnswer")!.IsVisible, "review starts with hidden answer");
            Click("ReviewRevealBtn");
            Click("ReviewUnsureBtn");
            var round = Field<StudyRound<long>>("_reviewRound");
            Check(round.CurrentTarget == 3 && round.Completed == 0, "unsure switches actual review queue to three recognitions");
            Click("ReviewRevealBtn"); Click("ReviewRememberBtn");
            Check(round.CurrentStreak == 1 && round.Completed == 0, "first recognition remains in queue");
            Click("ReviewRoundUndoBtn");
            Check(round.CurrentStreak == 0 && round.Completed == 0, "UI undo restores incomplete streak without negative count");
            Click("NavLookup"); Click("NavReview");
            Check(round.CurrentTarget == 3, "navigation retains the active review round");
            for (var i = 0; i < 3; i++) { Click("ReviewRevealBtn"); Click("ReviewRememberBtn"); }
            Check(round.IsFinished && store.GetAllWords().Single(w => w.Id == word.Id).ReviewCount == 1,
                "three recognitions complete and persist one scheduled review");
            Click("ReviewRoundUndoBtn");
            Check(!round.IsFinished && store.GetAllWords().Single(w => w.Id == word.Id).ReviewCount == 0,
                "undo completed review restores database and card queue");
            Call("ShowPage", "learning");
            await Task.Delay(100);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96,96));
            bitmap.Render(window); bitmap.Save(Path.Combine(folder, "learning-page.png"));
            await VerifyLearningPage(window, Check);
            exit = 0;
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "learning-integration-result.txt"), lines);
            window.ForceClose();
            (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
        }
    }

    private static async Task VerifyLearningPage(MainWindow window, Action<bool,string> check)
    {
        check(window.FindControl<Panel>("PagesHost")!.Children.Any(p => p.Name == "LearningHubPage" && p.IsVisible),
            "learning page is reachable through actual page host");
        object? Call(string name, params object[] args) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        Field<TextBox>("_planName").Text = "每日一词";
        Field<TextBox>("_planQuota").Text = "1";
        Field<CheckBox>("_planRandom").IsChecked = false;
        Call("CreateLearningPlan");
        var plans = Field<List<DailyStudyPlan>>("_learningPlans");
        check(plans.Count == 1 && plans[0].DailyWordCount == 1, "UI creates and saves archive plan");
        Call("OpenPlanCreator", DailyStudyPlanSource.Archive, null!);
        await Task.Delay(150);
        var creator = Field<ContentControl>("_planCreatorContent");
        check(creator.GetLogicalDescendants().OfType<TextBox>().Any(b => b.Name == "PlanCreatorSearch") &&
              creator.GetLogicalDescendants().OfType<NumericUpDown>().FirstOrDefault(b => b.Name == "PlanCreatorQuota")?.Value == 20,
            "plan creator exposes searchable selection and bounded daily quota");
        using (var creatorBitmap = new RenderTargetBitmap(new PixelSize((int)creator.Bounds.Width, (int)creator.Bounds.Height), new Vector(96, 96)))
        {
            creatorBitmap.Render(creator);
            creatorBitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, "plan-creator.png"));
        }
        Call("ClosePlanCreator");
        var plan = plans[0];
        Call("SetLearningPlanStopped", plan);
        plan = Field<List<DailyStudyPlan>>("_learningPlans")[0];
        check(plan.Status == DailyStudyPlanStatus.Stopped, "UI stops plan");
        Call("ResumeLearningPlan", plan);
        plan = Field<List<DailyStudyPlan>>("_learningPlans")[0];
        check(plan.Status == DailyStudyPlanStatus.Active, "UI resumes plan with original identity");
        Call("AdjustLearningPlan", plan, "调整后的计划", 1, false);
        plan = Field<List<DailyStudyPlan>>("_learningPlans")[0];
        check(plan.Name == "调整后的计划", "UI adjusts and saves plan");
        Call("StartDailyLearning", plan);
        var session = Field<DailyStudyPlanSession>("_dailyLearningSession");
        check(session.Round.CurrentStep == StudyStep.Learn, "plan opens actual learn card");
        Call("CompleteDailyLearn");
        void RevealAndRate(StudyRating rating)
        {
            typeof(MainWindow).GetField("_planAnswerVisible", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            Call("RateDailyLearning", rating);
        }
        RevealAndRate(StudyRating.Known);
        check(!session.Round.IsFinished && session.Round.CurrentStreak == 1, "plan UI requires three recognitions");
        await Task.Delay(100);
        using (var studyBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96,96)))
        {
            studyBitmap.Render(window);
            studyBitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, "study-active.png"));
        }
        var studyHost = Field<Grid>("_studyWorkspaceHost");
        check(studyHost.RowDefinitions.Count == 3 && studyHost.Children.OfType<ScrollViewer>().Single().Bounds.Height > 0,
            "study workspace keeps bounded content between fixed header and footer");
        window.FindControl<Button>("ThemeToggleBtn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        using (var studyBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96,96)))
        {
            studyBitmap.Render(window);
            studyBitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, "study-dark.png"));
        }
        window.FindControl<Button>("ThemeToggleBtn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Width = 760; window.Height = 520; await Task.Delay(100);
        typeof(MainWindow).GetField("_planAnswerVisible", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        Call("RenderDailyLearning"); await Task.Delay(100);
        using (var studyBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96,96)))
        {
            studyBitmap.Render(window);
            studyBitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, "study-narrow.png"));
        }
        var ratingButtons = studyHost.GetLogicalDescendants().OfType<Button>()
            .Where(b => b.Name is "StudyRateForgot" or "StudyRateUnsure" or "StudyRateKnown").ToList();
        check(ratingButtons.Count == 3 && ratingButtons.All(b => b.TranslatePoint(default, window) is { } p
            && p.X >= 0 && p.X + b.Bounds.Width <= window.Bounds.Width && p.Y + b.Bounds.Height <= window.Bounds.Height),
            "three neutral rating actions remain entirely visible at 760 by 520");
        window.Width = 1100; window.Height = 760;
        Call("ShowPage", "lookup"); Call("ShowPage", "learning");
        check(session.Round.CurrentStreak == 1, "plan streak survives navigation");
        RevealAndRate(StudyRating.Known); RevealAndRate(StudyRating.Known);
        check(session.Round.IsFinished, "plan card flow completes batch");
        var archive = Field<IVocabularyArchive>("_vocabService");
        var planKey = WordKeyResolver.FromArchive(archive.GetAllWords().Single(w => w.Word == "apple")).Key;
        check(ServiceFactory.OpenMemory(archive).GetCard(planKey)?.NextReviewAtUtc is not null,
            "completed plan word receives an FSRS due date");
        var path = Path.Combine(Path.GetDirectoryName(Field<IVocabularyArchive>("_vocabService").DatabasePath)!, "daily-plans.json");
        check(new DailyStudyPlanStore(path).Load()[0].Status == DailyStudyPlanStatus.Completed,
            "completed plan is saved to disk by UI");
        check(studyHost.GetLogicalDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "撤销上一次" && b.IsEnabled),
            "finished batch exposes an actual enabled undo action");
        Call("UndoDailyLearning");
        check(!session.Round.IsFinished && new DailyStudyPlanStore(path).Load()[0].Status == DailyStudyPlanStatus.Active,
            "UI undo restores both plan file and learning round");
        check(ServiceFactory.OpenMemory(archive).GetCard(planKey) == null,
            "plan undo invalidates its canonical review");
        Call("StartArchiveTyping", false);
        var typing = Field<TypingSession>("_learningTypingSession");
        await Task.Delay(150);
        using (var typingBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
        {
            typingBitmap.Render(window);
            typingBitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, "typing-active.png"));
        }
        Field<TextBox>("_learningTypingInput").Text = "ap"; Call("SubmitLearningTyping");
        check(typing.Outcome == TypingOutcome.Pending && typing.Cursor == 0, "dictation incomplete spelling cannot advance");
        Field<TextBox>("_learningTypingInput").Text = "axple"; Call("SubmitLearningTyping");
        check(typing.Outcome == TypingOutcome.Retry && typing.ErrorPositions.SequenceEqual([1]), "typing page grades wrong letters");
        Field<TextBox>("_learningTypingInput").Text = "apple"; Call("SubmitLearningTyping");
        check(typing.Outcome == TypingOutcome.Correct, "typing page accepts complete correct spelling");
        Call("SubmitLearningTyping");
        check(typing.Outcome == TypingOutcome.Finished && typing.Cursor == 1, "typing page reaches actual completion screen");
        Call("ReloadLearningPlans");
        check(Field<List<DailyStudyPlan>>("_learningPlans").Single().Name == "调整后的计划", "UI restores persisted plans after reopening store");
        window.Width = 840; window.Height = 600;
        await SnapshotNarrow(window);
    }

    private static async Task SnapshotNarrow(MainWindow window)
    {
        await Task.Delay(150);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96,96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, "learning-narrow.png"));
    }
}
