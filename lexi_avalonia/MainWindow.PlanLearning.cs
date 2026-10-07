using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi;

public partial class MainWindow
{
    private DailyStudyPlanSession? _planLearningSession;
    private bool _planCardActive, _planTypingActive;
    private Border? _planSpellingOverlay;
    private TextBlock _planSpellingHeading = null!, _planSpellingSummary = null!;
    private ComboBox _planSpellingScope = null!, _planSpellingMode = null!;
    private Button _planSpellingStart = null!, _planSpellingSkip = null!;
    private DailyStudyPlan? _planSpellingPlan;
    private List<DailyStudyPlanWord> _planSpellingBatch = [];

    private void ConfigurePlanLearning()
    {
        if (_planSpellingOverlay != null) return;
        var body = new StackPanel { Spacing = 18, MaxWidth = 520 };
        _planSpellingHeading = ContentText("", 24);
        _planSpellingSummary = ContentText("", 14);
        _planSpellingScope = new ComboBox { Name = "PlanSpellingScope", HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        _planSpellingMode = new ComboBox { Name = "PlanSpellingMode", HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        _planSpellingStart = LearningButton("开始拼写", "PlanSpellingStartBtn", true);
        _planSpellingSkip = LearningButton("跳过", "PlanSpellingSkipBtn");
        _planSpellingScope.SelectionChanged += (_, _) => UpdatePlanSpellingSummary();
        _planSpellingStart.Click += (_, _) => StartPlanSpelling();
        _planSpellingSkip.Click += (_, _) => ClosePlanSpelling();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_planSpellingSkip); actions.Children.Add(_planSpellingStart);
        body.Children.Add(_planSpellingHeading); body.Children.Add(_planSpellingSummary);
        body.Children.Add(_planSpellingScope); body.Children.Add(_planSpellingMode); body.Children.Add(actions);
        var card = new Border { Classes = { "card" }, Child = body, Padding = new Thickness(28), CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28) };
        _planSpellingOverlay = new Border { Name = "PlanSpellingOverlay", Child = card, IsVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) };
        ((Grid)RootWindowBorder.Child!).Children.Add(_planSpellingOverlay);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_planSpellingOverlay.IsVisible && e.Key == Key.Escape)
            { e.Handled = true; ClosePlanSpelling(); }
        }, RoutingStrategies.Tunnel);
        RefreshPlanLearningLanguage();
    }

    private void RefreshPlanLearningLanguage()
    {
        if (_planSpellingOverlay == null) return;
        _planSpellingHeading.Text = T("拼写巩固（可跳过）");
        var scope = Math.Max(0, _planSpellingScope.SelectedIndex);
        _planSpellingScope.ItemsSource = new[] { T("本批次全部单词"), T("本批次曾选「忘记了」的单词") };
        _planSpellingScope.SelectedIndex = scope;
        var mode = Math.Max(0, _planSpellingMode.SelectedIndex);
        _planSpellingMode.ItemsSource = new[] { T("淡写"), T("默写") };
        _planSpellingMode.SelectedIndex = mode;
        _planSpellingStart.Content = T("开始拼写"); _planSpellingSkip.Content = T("跳过");
        UpdatePlanSpellingSummary();
        if (_planCardActive) RefreshPlanFocusLabels();
    }

    private void StartPlanCardRound(DailyStudyPlan plan)
    {
        if (!FocusCanNavigate) return;
        if (plan.Status == DailyStudyPlanStatus.Stopped) { SetStatus(T("计划已停止。")); return; }
        ExitWordFocus(); ShowPage("plans");
        var previousBatch = plan.CurrentBatchWordIds.ToList();
        var previousBatchOrder = plan.CurrentBatchRandomOrder;
        var session = new DailyStudyPlanSession(plan, DateOnly.FromDateTime(DateTime.Now), _focusRound);
        if (!SaveStudyPlans()) { plan.CurrentBatchWordIds = previousBatch; plan.CurrentBatchRandomOrder = previousBatchOrder; return; }
        _activeStudyPlan = plan;
        if (!session.Round.HasCurrent)
        {
            ShowPlanSpelling(plan, session.BatchWords);
            return;
        }
        // Capture the manager page before rendering the frozen card into the shared focus view.
        var snapshot = CaptureFocusSnapshot() with { Page = "plans" };
        _planLearningSession = session; _planCardActive = true;
        ++_focusEpoch; _focusUndo.Clear(); _focusDeck = session.BatchWords.Select(w => w.Id).ToList();
        // 开轮（规格书 §1）：计划卡整轮按「首次学习」记录（§9.2）；定稿由计划会话在保存成功后回调（§3）。
        MemoryBeginSession(StudyMode.FirstLearn,
            plan.Source == DailyStudyPlanSource.Archive ? WordSource.Archive : WordSource.Ielts,
            plan.Id, session.BatchWords.Count);
        session.OnWordCompleted = MemoryCommitPlanCard;
        session.OnRatingApplied = (id, rating, result) => MemoryRated(MemorySurface.Plan, id, rating,
            _planRatingRecognitionBefore, result.Streak, result.Completed ? StudyMode.FirstLearn : null);
        RenderPlanFocusWord(session.Round.Current);
        EnterWordFocus(snapshot);
        if (!_wordFocusActive) { EndPlanCardFocus(); return; }
        LookupPageHost.RowDefinitions = new RowDefinitions("Auto,*,Auto");
        _focusActions!.IsVisible = true;
        ResetFocusAnswer();
        ShowFocusLearnAnswer();
        PaintFocusStreak(_focusRound.CurrentStreak, _focusRound.CurrentTarget, animateNewest: false);
        ApplyLayout(); SpeakFocusedWord();
    }

    private void RenderPlanFocusWord(string id)
    {
        var word = _activeStudyPlan!.Words.Single(w => w.Id == id);
        ++_lookupVersion; _aiCts?.Cancel();
        LookupInput.Text = word.Word;
        ResultWordText.Text = word.Word; ResultPhoneticText.Text = word.Phonetic;
        ResultTranslationText.Text = word.Meaning; ResultDefinitionText.Text = word.Definition;
        RenderPartOfSpeech("", word.Meaning);
        _currentExpansion = new LlmResult();
        if (!string.IsNullOrWhiteSpace(word.Example)) _currentExpansion.Examples.Add(new ExampleItem(word.Example, ""));
        LookupResultCard.IsVisible = true; LookupEmptyCard.IsVisible = LookupNotFoundCard.IsVisible = false;
        AiDrawerToggleBtn.IsVisible = false;
        MemoryPresentPlanCard(_activeStudyPlan, id);
    }

    private Task LoadPlanFocusWordAsync()
    {
        if (!_planCardActive || !_focusRound.HasCurrent) return Task.CompletedTask;
        RenderPlanFocusWord(_focusRound.Current);
        ResetFocusAnswer(); _focusRatedWord = null;
        _focusEntryButton!.IsVisible = false;
        foreach (var decoration in _focusDecorations) decoration.IsVisible = false;
        PaintFocusStreak(_focusRound.CurrentStreak, _focusRound.CurrentTarget, animateNewest: false);
        if (_focusRound.CurrentStep == StudyStep.Learn) ShowFocusLearnAnswer();
        else RefreshFocusLearningLabels();
        _focusBackButton!.Focus(); SpeakFocusedWord();
        return Task.CompletedTask;
    }

    private void RefreshPlanFocusLabels()
    {
        if (!_planCardActive || _activeStudyPlan == null || _planLearningSession == null) return;
        _focusHeading!.Text = $"{_activeStudyPlan.Name} · {T("已完成")} {_activeStudyPlan.CompletedWordIds.Count}/{_activeStudyPlan.Words.Count}";
        _focusSaveButton!.IsVisible = _focusMasterButton!.IsVisible = false;
        _focusSaveButton.IsEnabled = _focusMasterButton.IsEnabled = false;
        _focusAiToggleButton!.IsVisible = false;
        _focusUndoButton!.IsEnabled = _planLearningSession.CanUndo && !_focusRatingBusy;
    }

    private void ShowPlanRating(StudyRating rating, string word, StudyCommitResult result)
    {
        _focusLastRating = rating; _focusRatedWord = word; _focusRated = true;
        _focusShownStreak = result.Streak; _focusShownTarget = result.Target; _focusShownCompleted = result.Completed;
        PaintFocusStreak(result.Streak, result.Target, animateNewest: true);
        ShowFocusRecallAnswer(); RenderStudyPlanLists();
    }

    private Task RatePlanFocusedWordAsync(StudyRating rating)
    {
        if (MemoryTryCompletePending()) return Task.CompletedTask;
        if (_planLearningSession == null) return Task.CompletedTask;
        _focusRatingBusy = true;
        try
        {
            var word = _focusRound.Current;
            // StudyRound 要到 Commit() 之后才更新连击：作答前的连击必须在 Rate 之前取（规格书 §2）。
            var recognitionBefore = _focusRound.CurrentStreak;
            _planRatingRecognitionBefore = recognitionBefore;
            if (_planLearningSession.Rate(rating, MemorySavePlanProgress) is { } result)
            {
                // 定稿（CommitWord）由 DailyStudyPlanSession 在计划 JSON 保存成功之后回调；这里只记录本次作答。
                MemorySaveRound();
                ShowPlanRating(rating, word, result);
            }
            else SetStatus(T("学习计划进度保存失败，请重试。"));
        }
        catch (PendingLearningWriteException pending)
        {
            _memoryPendingCompletion = () =>
            {
                ShowPlanRating(rating, pending.WordId, pending.Result);
                MemorySaveRound();
                SetStatus(T("已记下这次重逢。"));
            };
            SetStatus(T("学习计划进度保存失败，请重试。"));
        }
        finally { _focusRatingBusy = false; RefreshFocusLearningLabels(); }
        return Task.CompletedTask;
    }

    private Task ReclassifyPlanFocusAsync()
    {
        if (_planLearningSession == null || !_planLearningSession.CanUndo) return Task.CompletedTask;
        _focusRatingBusy = true;
        try
        {
            if (_planLearningSession.ReclassifyAsForgot(SaveStudyPlans) is { } result)
            {
                // 改判：同一 presentation 追加修正事件，presentationId 不变（规格书 §2）。
                MemoryRevised(MemorySurface.Plan, _focusRatedWord, _focusLastRating, StudyRating.Forgot);
                ShowPlanRating(StudyRating.Forgot, _focusRatedWord!, result);
            }
            else SetStatus(T("学习计划进度保存失败，请重试。"));
        }
        // 长期层失败必须可见：之前这里只有 finally，MemoryRevised 抛出会变成无人处理的异常，
        // 界面既没有报错、也看不出改判到底有没有生效。
        catch (Exception ex) { SetStatus(T("改判失败：") + ex.Message); }
        finally { _focusRatingBusy = false; RefreshFocusLearningLabels(); }
        return Task.CompletedTask;
    }

    private async Task UndoPlanFocusAsync()
    {
        if (_planLearningSession == null || !_planLearningSession.CanUndo || _focusRatingBusy) return;
        _focusRatingBusy = true;
        try
        {
            if (_planLearningSession.Undo(SaveStudyPlans))
            {
                // 撤销：追加 Undone 事件；该词若已在本会话定稿，再失效它的 canonical（规格书 §2 / §4）。
                // 计划 JSON 的撤销必须先做（MemoryUndone 要用撤销后的当前计划词），所以这里无法像
                // Review/Focus 那样把长期层当提交点；但失败同样绝不显示撤销成功——不渲染完成状态，
                // 只报失败，长期层保持原样。
                var undoneId = _focusRound.HasCurrent ? _focusRound.Current : null;
                MemoryUndone(MemorySurface.Plan, undoneId);
                RenderStudyPlanLists(); await LoadPlanFocusWordAsync();
            }
            else SetStatus(T("学习计划进度保存失败，请重试。"));
        }
        catch (Exception ex) { SetStatus(T("撤销失败：") + ex.Message); }
        finally { _focusRatingBusy = false; RefreshFocusLearningLabels(); }
    }

    private void EndPlanCardFocus()
    {
        _planCardActive = false; _planLearningSession = null; _activeStudyPlan = null;
        _focusSaveButton!.IsVisible = _focusMasterButton!.IsVisible = true;
        _focusSaveButton.IsEnabled = true;
    }

    private void FinishPlanCardRound()
    {
        if (_activeStudyPlan == null || _planLearningSession == null) return;
        var plan = _activeStudyPlan; var words = _planLearningSession.BatchWords;
        ExitWordFocus(); ShowPlanSpelling(plan, words);
    }

    private void ShowPlanSpelling(DailyStudyPlan plan, IReadOnlyList<DailyStudyPlanWord> words)
    {
        if (words.Count == 0) { _activeStudyPlan = null; return; }
        _planSpellingPlan = plan; _planSpellingBatch = words.ToList();
        _planSpellingScope.SelectedIndex = 0; _planSpellingMode.SelectedIndex = 0;
        RefreshPlanLearningLanguage();
        ((Border)_planSpellingOverlay!.Child!).Background = PlanModalBrush();
        _planSpellingOverlay.IsVisible = true; _planSpellingStart.Focus();
    }

    private List<DailyStudyPlanWord> SelectedPlanSpellingWords() => _planSpellingScope.SelectedIndex == 1
        ? _planSpellingBatch.Where(w => _planSpellingPlan?.ForgotWordIds.Contains(w.Id) == true).ToList()
        : _planSpellingBatch.ToList();

    private void UpdatePlanSpellingSummary()
    {
        if (_planSpellingPlan == null) return;
        var count = SelectedPlanSpellingWords().Count;
        _planSpellingSummary.Text = T("本批次已完成，是否继续拼写巩固？") + $"\n{_planSpellingPlan.Name} · {count}";
        if (count == 0) _planSpellingSummary.Text += "\n" + T("本批次没有选过「忘记了」的单词。");
        _planSpellingStart.IsEnabled = count > 0;
    }

    private void ClosePlanSpelling()
    {
        _planSpellingOverlay!.IsVisible = false;
        _planSpellingPlan = null; _planSpellingBatch = []; _activeStudyPlan = null;
    }

    private void StartPlanSpelling()
    {
        if (_planSpellingPlan == null) return;
        var words = SelectedPlanSpellingWords();
        if (words.Count == 0) return;
        var title = _planSpellingPlan.Name; var mode = _planSpellingMode.SelectedIndex;
        var selected = words.Select(w => new LearningWord { Id = w.Id, Words = [w.Word], Meaning = w.Meaning,
            Phonetic = w.Phonetic, Extra = w.Definition, Example = w.Example, AudioPath = w.AudioPath }).ToList();
        ClosePlanSpelling();
        ShowPage("typing");
        _planTypingActive = true; _typingMode.SelectedIndex = mode;
        BeginTypingRound(selected, title);
    }

    private void SpeakFocusedWord()
    {
        if (_planCardActive && _activeStudyPlan != null)
        {
            var word = _activeStudyPlan.Words.FirstOrDefault(w => w.Id == (_focusRated ? _focusRatedWord : _focusRound.HasCurrent ? _focusRound.Current : null));
            if (word != null) { _wordAudio.Play(word.Word, IeltsCatalog.ResolveAsset(word.AudioPath)); return; }
        }
        SpeakLearningText(ResultWordText.Text);
    }
}
