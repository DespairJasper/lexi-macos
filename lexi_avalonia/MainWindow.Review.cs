using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Diagnostics;

namespace Lexi;

public partial class MainWindow
{
    // Keep the persisted post-rating revision so explicit mutations (undo/reschedule) can
    // make the word eligible again during the same calendar day.
    private readonly Dictionary<long, int> _reviewHandled = [];
    private readonly StudyRound<WordItem> _reviewRound = new();
    private List<WordItem> _reviewAvailableWords = [];
    private int _reviewBatchSize = 20;
    private bool _reviewUpdatingBatchControl;
    private DateTime _reviewDay = DateTime.Today;
    private WordItem? _reviewWord;
    private bool _reviewRevealed;
    private bool _reviewBusy;
    private int _reviewEpoch;
    private StudyRating _reviewLastRating = StudyRating.Known;
    private long? _reviewUndoWordId;
    private int _reviewShownStreak;
    private int _reviewShownTarget = StudyRound<WordItem>.RequiredStreak;
    private bool _reviewCompleted;

    private static StackPanel RatingContent(string label, string key, string color)
    {
        var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = label + "  " + key, FontSize = 14, FontWeight = FontWeight.Medium,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new Border
        {
            Width = 10, Height = 3, CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.Parse(color)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        return content;
    }

    private void BindReviewEvents()
    {
        // 揭示答案后按钮会被改名成「下一词 / 记错了」，而且焦点就在按钮上：Avalonia 的 Button
        // 会把聚焦时按下的 Space 消化成 Click，所以 Click 必须按当前状态分派到换卡/改判，
        // 不能一律回评分入口——那里有 _reviewRevealed 守卫，会把点击静默吞掉，表现为整页卡死。
        ReviewRememberBtn.Click += async (_, _) =>
        {
            if (_reviewRevealed) await AdvanceReviewAsync();
            else await OnReviewRatingAsync(StudyRating.Known);
        };
        ReviewUnsureBtn.Click += async (_, _) => await OnReviewRatingAsync(StudyRating.Unsure);
        ReviewUnfamiliarBtn.Click += async (_, _) =>
        {
            if (_reviewRevealed) await ReclassifyReviewAsync();
            else await OnReviewRatingAsync(StudyRating.Forgot);
        };
        ReviewBackBtn.Click += (_, _) => ShowPage("lookup");
        ReviewUndoBtn.Click += (_, _) => UndoReviewFromLearningPage();
        ReviewMasterBtn.Click += async (_, _) => await MasterReviewFromLearningPageAsync();
        ReviewRestartBtn.Click += (_, _) =>
        {
            if (_reviewRound.Total > 0 && _reviewRound.IsFinished) RenderReviewPreview();
            else ShowPage("lookup");
        };
        ReviewBatchCount.ValueChanged += (_, _) =>
        {
            if (_reviewUpdatingBatchControl) return;
            _reviewBatchSize = Math.Max(1, (int)Math.Round(ReviewBatchCount.Value ?? 20));
            UpdateReviewPreviewBatchText();
        };
        ReviewStartAllBtn.Click += (_, _) => StartReviewBatch(_reviewAvailableWords);
        PageReview.KeyDown += async (_, e) =>
        {
            if (e.Source is TextBox || _reviewBusy || !FocusCanNavigate) return;
            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Alt)
            { UndoReviewFromLearningPage(); e.Handled = true; return; }
            if (e.KeyModifiers != KeyModifiers.None) return;
            if (e.Key == Key.Escape) { ShowPage("lookup"); e.Handled = true; }
            else if (e.Key == Key.Delete) { e.Handled = true; await MasterReviewFromLearningPageAsync(); }
            else if (e.Key == Key.A) { SpeakLearningText(_reviewWord?.Word); e.Handled = true; }
            else if (e.Key == Key.S && _reviewRevealed) { SpeakLearningText(_reviewWord?.AiResult?.Examples.FirstOrDefault()?.English); e.Handled = true; }
            else if (!_reviewRevealed && e.Key is Key.Q or Key.W or Key.E)
            {
                e.Handled = true;
                await OnReviewRatingAsync(e.Key == Key.Q ? StudyRating.Known : e.Key == Key.W ? StudyRating.Unsure : StudyRating.Forgot);
            }
            else if (_reviewRevealed && e.Key is Key.Space or Key.Enter)
            { e.Handled = true; await AdvanceReviewAsync(); }
            else if (_reviewRevealed && e.Key == Key.E)
            { e.Handled = true; await ReclassifyReviewAsync(); }
        };
    }

    private void OpenReviewDeck()
    {
        ++_reviewEpoch;
        RenderReviewPreview();
    }

    private void RenderReviewPreview()
    {
        _reviewAvailableWords = GetPendingReviewWords();
        var dueCount = _reviewAvailableWords.Count;

        _reviewUpdatingBatchControl = true;
        ReviewBatchCount.Maximum = Math.Max(1, dueCount);
        ReviewBatchCount.Value = Math.Min(_reviewBatchSize, Math.Max(1, dueCount));
        _reviewUpdatingBatchControl = false;

        ReviewPreviewHeading.Text = PlanText("今日复习预览", "Today’s review preview");
        ReviewRemainingText.Text = dueCount == 0
            ? T("今日无到期单词")
            : PlanText($"今日有 {dueCount} 个待复习词", $"{dueCount} words are due today");
        ReviewPreviewSummary.Text = dueCount == 0
            ? PlanText("今天没有到期词。完成一批后，可回到这里继续选择下一批。", "No words are due today. Return here after a batch to choose what to review next.")
            : PlanText($"今天共有 {dueCount} 个到期词。选择每批数量后开始；计划入口只包含该计划中已学且到期的词。",
                $"{dueCount} words are due today. Choose a batch size; plan entries include only learned words from that plan that are due.");
        ReviewBatchHeading.Text = PlanText("每批复习词数", "Words per batch");
        ReviewBatchDescription.Text = PlanText("计划入口也使用这个批量数。", "The same batch size applies when reviewing a plan.");
        ReviewWordsHeading.Text = PlanText("待复习词预览", "Words due for review");
        ReviewWordPreview.Text = dueCount == 0
            ? PlanText("暂无待复习词。", "No words are due.")
            : string.Join(" · ", _reviewAvailableWords.Take(18).Select(w => w.Word));
        ReviewWordPreviewMore.Text = dueCount > 18
            ? PlanText($"另有 {dueCount - 18} 个词。", $"And {dueCount - 18} more.")
            : "";
        ReviewPlansHeading.Text = PlanText("按学习计划复习", "Review by study plan");

        var groups = ReviewWordsByPlan(_reviewAvailableWords, out var unassigned);
        ReviewPlansDescription.Text = groups.Count == 0 && unassigned.Count == 0
            ? PlanText("目前没有计划词条。", "There are no plan words due today.")
            : PlanText("按到期词数量从多到少排列。点击计划立即开始该计划复习；一个词属于多个计划时会分别计入，计划外词汇单列。",
                "Plans are sorted by due-word count. Click one to review its due words; words in multiple plans count in each, and unassigned words are listed separately.");
        ReviewPlanRows.Children.Clear();
        var index = 0;
        foreach (var group in groups)
        {
            var words = group.Words;
            var button = PlanButton(
                $"{group.Plan.Name}  ·  {words.Count} {PlanText("个到期词", "due")}",
                $"ReviewPlanDue{index++}", () => StartReviewBatch(words));
            button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            button.IsEnabled = _databaseAvailable && FocusCanNavigate;
            ReviewPlanRows.Children.Add(button);
        }
        if (unassigned.Count > 0)
        {
            var words = unassigned;
            var button = PlanButton(
                PlanText($"计划外词汇  ·  {words.Count} 个到期词", $"Unassigned words  ·  {words.Count} due"),
                "ReviewUnassignedDue", () => StartReviewBatch(words));
            button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            button.IsEnabled = _databaseAvailable && FocusCanNavigate;
            ReviewPlanRows.Children.Add(button);
        }

        ReviewStartAllBtn.IsEnabled = dueCount > 0 && _databaseAvailable && FocusCanNavigate;
        ReviewBatchCount.IsEnabled = dueCount > 0 && _databaseAvailable && FocusCanNavigate;
        ReviewPreviewHost.IsVisible = true;
        ReviewCardHost.IsVisible = false;
        ReviewEmptyCard.IsVisible = false;
        ReviewRatingBar.IsVisible = false;
        ReviewHintText.Text = "";
        ReviewUndoBtn.IsVisible = ReviewMasterBtn.IsVisible = false;
        UpdateReviewPreviewBatchText();
    }

    private List<(DailyStudyPlan Plan, List<WordItem> Words)> ReviewWordsByPlan(
        IReadOnlyList<WordItem> dueWords, out List<WordItem> unassigned)
    {
        var keysByWordId = new Dictionary<long, string>();
        foreach (var word in dueWords)
            if (MemoryTryReviewKey(word, out var key)) keysByWordId[word.Id] = key.Key;

        var dueArchiveIds = dueWords.Where(word => word.Id > 0).Select(word => word.Id).ToHashSet();
        var dueKeys = keysByWordId.Values.ToHashSet(StringComparer.Ordinal);
        var assignedKeys = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<(DailyStudyPlan Plan, List<WordItem> Words)>();
        foreach (var plan in _studyPlans)
        {
            var planDueKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var word in plan.Words)
            {
                if (!plan.CompletedWordIds.Contains(word.Id)) continue;
                if (plan.Source == DailyStudyPlanSource.Archive &&
                    (!long.TryParse(word.Id, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var archiveId) || !dueArchiveIds.Contains(archiveId)))
                    continue;
                if (!MemoryTryPlanKey(plan, word.Id, out var key)) continue;
                if (dueKeys.Contains(key.Key)) planDueKeys.Add(key.Key);
            }
            if (planDueKeys.Count == 0) continue;
            var words = dueWords.Where(w => keysByWordId.TryGetValue(w.Id, out var key) && planDueKeys.Contains(key)).ToList();
            if (words.Count == 0) continue;
            assignedKeys.UnionWith(planDueKeys);
            groups.Add((plan, words));
        }

        groups = groups.OrderByDescending(group => group.Words.Count)
            .ThenBy(group => group.Plan.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        unassigned = dueWords.Where(word => !keysByWordId.TryGetValue(word.Id, out var key) || !assignedKeys.Contains(key)).ToList();
        return groups;
    }

    private void UpdateReviewPreviewBatchText()
    {
        var available = _reviewAvailableWords.Count;
        var batch = Math.Min(available, Math.Max(1, (int)Math.Round(ReviewBatchCount.Value ?? _reviewBatchSize)));
        ReviewStartAllBtn.Content = PlanText($"开始复习本批 · {batch} / {available} 词", $"Start this batch · {batch} / {available} words");
    }

    private void StartReviewBatch(IReadOnlyList<WordItem> scope)
    {
        if (scope.Count == 0 || !FocusCanNavigate || !_databaseAvailable) return;
        var batchSize = Math.Min(scope.Count, Math.Max(1, (int)Math.Round(ReviewBatchCount.Value ?? _reviewBatchSize)));
        var words = scope.Take(batchSize).ToList();
        if (words.Count == 0) return;

        ++_reviewEpoch;
        _reviewRound.Reset(words, StudyMode.Review);
        // The memory layer keeps the existing review identity and recovery behavior; only this round's queue is scoped.
        MemoryBeginSession(StudyMode.Review, WordSource.Archive, "", words.Count);
        MemoryPresentReviewCard();
        RenderReviewCard();
    }

    private List<WordItem> GetPendingReviewWords()
    {
        if (_reviewDay != DateTime.Today) { _reviewHandled.Clear(); _reviewDay = DateTime.Today; }
        // 到期资格 = 规格书 §9.1 的并集口径（见 MemoryPendingReviewWords）
        // + 非档案 source 到期卡（教材 / 词形，见 MemorySourceDueWords）。排序仍是 due 升序、再按 Id。
        var due = MemoryPendingReviewWords();
        var sourceDue = MemorySourceDueWords();
        var queue = sourceDue.Count == 0 ? due : due.Concat(sourceDue).ToList();
        return queue
            .Where(w => !_reviewHandled.TryGetValue(w.Id, out var handledRevision) || handledRevision != w.Archive.Revision)
            .OrderBy(w => w.NextReviewDate).ThenBy(w => w.Id).ToList();
    }

    private void UpdateReviewBadge() => NavReviewBadge.Text = GetPendingReviewWords().Count.ToString();

    // 本轮进度：分母固定为本轮词数，不因模糊/忘记重新排队而变化。
    private void UpdateReviewProgressText() => ReviewRemainingText.Text = _reviewRound.Total == 0
        ? T("今日无到期单词")
        : TF($"已完成 {_reviewRound.Completed}/{_reviewRound.Total} · 剩余 {_reviewRound.Remaining}");

    // 评价按钮的中英文标签也随界面语言刷新，行内下划线颜色保持不变。
    private void ApplyReviewActionLabels()
    {
        ReviewRestartBtn.Content = _reviewRound.Total > 0 && _reviewRound.IsFinished
            ? PlanText("选择下一批", "Choose next batch")
            : T("返回阅读");
        ReviewMasterBtn.Content = T("熟") + "  Del";
        ReviewRememberBtn.Content = RatingContent(T(_reviewRevealed ? "下一词" : "认识"), _reviewRevealed ? "Space" : "Q", "#268D98");
        ReviewUnsureBtn.Content = RatingContent(T("模糊"), "W", "#D6AD4E");
        ReviewUnfamiliarBtn.Content = RatingContent(T(_reviewRevealed ? "记错了" : "忘记了"), "E", "#CF7E7C");
        var wrongVisible = !_reviewRevealed || _reviewLastRating != StudyRating.Forgot;
        ReviewUnsureBtn.IsVisible = !_reviewRevealed;
        ReviewUnfamiliarBtn.IsVisible = wrongVisible;
        // Keep six columns so hidden controls retain valid indices in every state.
        // Three ratings occupy two each; answer actions occupy equal halves.
        ReviewRatingBar.ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*");
        Grid.SetColumn(ReviewRememberBtn, 0);
        Grid.SetColumn(ReviewUnsureBtn, 2);
        Grid.SetColumn(ReviewUnfamiliarBtn, _reviewRevealed ? 3 : 4);
        Grid.SetColumnSpan(ReviewRememberBtn, _reviewRevealed ? (wrongVisible ? 3 : 6) : 2);
        Grid.SetColumnSpan(ReviewUnsureBtn, 2);
        Grid.SetColumnSpan(ReviewUnfamiliarBtn, _reviewRevealed ? 3 : 2);
    }

    private void RenderReviewCard(bool resetPose = true)
    {
        StopLearningSpeech();
        ReviewPreviewHost.IsVisible = false;
        ReviewUndoBtn.IsVisible = ReviewMasterBtn.IsVisible = true;
        NavReviewBadge.Text = GetPendingReviewWords().Count.ToString();
        _reviewWord = _reviewRound.HasCurrent ? _reviewRound.Current : null;
        _reviewRevealed = false;
        UpdateReviewProgressText();
        ApplyReviewActionLabels();
        ReviewAnswer.IsVisible = false;
        ReviewRatingBar.IsVisible = _reviewWord != null;
        ReviewUndoBtn.IsEnabled = _reviewUndoWordId != null && _reviewRound.CanUndo;
        foreach (var button in new[] { ReviewRememberBtn, ReviewUnsureBtn, ReviewUnfamiliarBtn }) button.IsEnabled = true;
        ReviewMeaningText.Text = ""; ReviewDefinitionText.Text = "";
        ReviewDetails.Children.Clear();
        ToolTip.SetTip(ReviewPronounceBtn, T("朗读单词") + "  A");
        if (_reviewWord == null)
        {
            ReviewCardHost.IsVisible = false;
            ReviewEmptyCard.IsVisible = true;
            ReviewStreakBars.IsVisible = false;
            ReviewHintText.Text = "";
            if (_reviewRound.Total > 0)
            {
                ReviewEmptyTitle.Text = T("本轮完成");
                ReviewEmptyBody.Text = TF($"本轮 {_reviewRound.Total} 个词全部通过「认识」 · 模糊 {_reviewRound.Unsure} · 忘记 {_reviewRound.Forgot}");
                ReviewRestartBtn.IsVisible = true;
            }
            else
            {
                ReviewEmptyTitle.Text = T("暂无到期单词");
                ReviewEmptyBody.Text = T("今天安排复习的单词已经全部完成，明天再来吧。");
                ReviewRestartBtn.IsVisible = false;
            }
            return;
        }
        ReviewCardHost.IsVisible = true;
        ReviewEmptyCard.IsVisible = false;
        ReviewRestartBtn.IsVisible = false;
        ReviewHintText.Text = T("瞬间想起词义，选「认识」 · 思考后想起词义，选「模糊」 · 想不起来选「忘记了」");
        ReviewWordText.Text = _reviewWord.Word;
        ReviewPhoneticText.Text = _reviewWord.Phonetic;
        _reviewShownStreak = _reviewRound.CurrentStreak;
        _reviewShownTarget = _reviewRound.CurrentTarget;
        _reviewCompleted = false;
        ReviewStreakBars.IsVisible = true;
        PaintStreakBars(ReviewStreakBars, _reviewShownStreak, _reviewShownTarget, animateNewest: false);
        if (resetPose) SetReviewPose(0, 1);
        if (_currentPage == "review")
        { ReviewRememberBtn.Focus(); SpeakLearningText(_reviewWord.Word); }
    }

    private void SetReviewPose(double y, double opacity) => Motion.SetPose(ReviewCard, Motion.Pose(0, y, 1), opacity);

    // 卡片纵向换位：旧卡上移淡出，新卡从下方上浮淡入（与不背单词的卡片流一致）。
    private async Task<bool> MoveReviewCardAsync(int epoch, double fromY, double toY, bool entering)
    {
        if (epoch != _reviewEpoch || _currentPage != "review") return false;
        SetReviewPose(fromY, entering ? 0 : 1);
        await Motion.ToPoseAsync(ReviewCard, Motion.Pose(0, toY, 1), entering ? 1 : 0,
            entering ? Motion.Standard : Motion.Fast, entering ? Motion.Enter : Motion.Exit);
        return epoch == _reviewEpoch && _currentPage == "review";
    }

    private void RevealReviewAnswer()
    {
        if (_reviewWord == null) return;
        _reviewRevealed = true;
        ReviewMeaningText.Text = _reviewWord.Translation;
        ReviewDefinitionText.Text = _reviewWord.Definition;
        ReviewDefinitionText.IsVisible = string.IsNullOrWhiteSpace(_reviewWord.Translation) && !string.IsNullOrWhiteSpace(_reviewWord.Definition);
        ReviewAnswer.IsVisible = ReviewRatingBar.IsVisible = true;
        ApplyReviewActionLabels();
        ReviewStreakBars.IsVisible = true;
        PaintStreakBars(ReviewStreakBars, _reviewShownStreak, _reviewShownTarget, animateNewest: true);
        ReviewHintText.Text = _reviewLastRating switch
        {
            StudyRating.Known => _reviewCompleted
                ? T("已记为认识，本轮完成。")
                : TF($"已记为认识（{_reviewShownStreak}/{_reviewShownTarget}），本轮还会再出现。"),
            StudyRating.Unsure => T("已记为模糊，稍后本轮还会再出现一次。"),
            _ => T("已记为忘记，本轮会重新学习再测一次。")
        };
        AddLearningDetails(ReviewDetails, _reviewWord.AiResult ?? (_ieltsCatalog?.Find(_reviewWord.Word) is { } entry ? CreateIeltsExpansion(entry) : null));
        ReviewRememberBtn.Focus();

        foreach (var control in new Control[] { ReviewAnswer, ReviewRatingBar })
        {
            Motion.SetPose(control, Motion.Pose(0, 8, 1), 0);
            _ = Motion.ToPoseAsync(control, Motion.Rest, 1, Motion.Standard, Motion.Enter);
        }
    }

    private Task OnReviewRatingAsync(StudyRating rating)
    {
        if (MemoryTryCompletePending()) return Task.CompletedTask;
        if (_reviewWord == null || _reviewRevealed || _reviewBusy || _restoring || !_databaseAvailable || !FocusCanNavigate)
            return Task.CompletedTask;
        _reviewBusy = true;
        ReviewRatingBar.IsEnabled = false;
        try
        {
            var word = _reviewWord;
            // StudyRound 要到 Commit() 之后才更新连击：作答前的连击必须在提交前取（规格书 §2）。
            var recognitionBefore = _reviewRound.CurrentStreak;
            var checkpoint = _reviewRound.CaptureCheckpoint();
            var result = _reviewRound.Commit(rating);
            try
            {
            try { ApplyReviewRating(word, rating, result.Completed); }
            catch { _reviewRound.UndoLast(); throw; }
            // 长期记忆层（旁路）：写在兼容投影之后；失败只降级，不改变上面的轮内状态与下面的提示文案。
            var reviewIdentity = MemoryReviewIdentity(word);
            MemoryRated(MemorySurface.Review, reviewIdentity, rating, recognitionBefore, result.Streak,
                result.Completed ? StudyMode.Review : null);
            if (result.Completed) MemoryCommitCard(MemorySurface.Review, reviewIdentity, StudyMode.Review);
            }
            catch
            {
                checkpoint.Restore();
                if (_reviewUndoWordId == word.Id)
                {
                    if (MemoryIsArchiveWord(word)) _vocabService.UndoLastLearningAction(word.Id);
                    _reviewUndoWordId = null;
                    RefreshWords();
                }
                // Keep the rejected real click in raw history, but remove its standing outcome.
                if (MemoryTryPresentedCard(MemorySurface.Review, MemoryReviewIdentity(word), out var failedCard))
                    _memory?.OnUndone(failedCard.Key, failedCard.PresentationId);
                throw;
            }
            MemorySaveRound();
            _reviewLastRating = rating;
            _reviewShownStreak = result.Streak;
            _reviewShownTarget = result.Target;
            _reviewCompleted = result.Completed;
            if (result.Completed)
            {
                var updated = _allWords.FirstOrDefault(w => w.Id == word.Id);
                if (updated != null) _reviewHandled[word.Id] = updated.Archive.Revision;
            }
            // 先把评分条恢复可用再揭示：RevealReviewAnswer 会把键盘焦点交给「下一词」按钮，
            // 而焦点不会落到（继承到）已禁用状态的控件上——旧顺序让这个 Focus() 静默失败，
            // 表现为鼠标能点「下一词」，聚焦按钮上的空格键却没人接收。
            ReviewRatingBar.IsEnabled = true;
            RevealReviewAnswer();
            SetStatus(rating switch
            {
                StudyRating.Known => result.Completed
                    ? T("已记下这次重逢。")
                    : TF($"已记为认识（{result.Streak}/{result.Target}），本轮还会再出现。"),
                StudyRating.Unsure => T("已记为模糊，本轮稍后再见。"),
                _ => T("已记为忘记，稍后重新学习。")
            });
        }
        catch (Exception ex) { ReviewHintText.Text = T("本次复习未完成，请重试：") + ex.Message; SetStatus(ReviewHintText.Text); }
        finally { _reviewBusy = false; ReviewRatingBar.IsEnabled = true; }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 写旧列：stage / status / next_review_date 是**兼容投影 + 既有界面显示**（T4）。
    /// 评分路径保留这些调用——既有 UI 与既有 UI 测试都依赖它们；但排期的唯一来源是 fsrs_cards：
    /// 一旦该词有了 FSRS 卡，旧列（含 MarkUnsure / MarkForgot / ExecuteBatch("review")）就不再决定 due。
    /// </summary>
    private void ApplyReviewRating(WordItem word, StudyRating rating, bool completed)
    {
        _reviewUndoWordId = null;
        // 兼容投影只写档案词：教材/词形临时卡没有 words 行，负 id 打到档案上只会污染无关数据。
        // 它们的排期完全由长期记忆层的 FSRS 卡承担（规格书 §5）。
        if (MemoryIsArchiveWord(word))
        {
            if (rating == StudyRating.Known)
            {
                // 中间的认识只累计连击，只有本轮真正完成时才推进一次调度。
                if (!completed) return;
                _vocabService.ExecuteBatch([word.Id], "review");
            }
            else if (rating == StudyRating.Unsure) _vocabService.MarkUnsure(word.Id);
            else _vocabService.MarkForgot(word.Id);
        }
        _reviewUndoWordId = word.Id;
        RefreshWords();
        UpdateReviewBadge();
    }

    private async Task AdvanceReviewAsync()
    {
        if (_reviewBusy || !FocusCanNavigate) return;
        _reviewBusy = true;
        try { await AdvanceReviewCoreAsync(); }
        finally { _reviewBusy = false; }
    }

    // 换卡：旧卡上移淡出，新卡自下方上浮淡入。调用方负责 _reviewBusy 互斥。
    private async Task AdvanceReviewCoreAsync()
    {
        var epoch = _reviewEpoch;
        if (_reviewRound.IsFinished)
        {
            if (!ReduceMotionBox.IsChecked.GetValueOrDefault() && _currentPage == "review")
                await MoveReviewCardAsync(epoch, 0, -14, entering: false);
            if (epoch == _reviewEpoch && _currentPage == "review") { MemoryPresentReviewCard(); RenderReviewCard(); }
            // 一轮结束（T1 触发时机 ②）：只做一次资格检查，真正训练与否由冷却与样本门槛决定。
            MemoryTryTrainContext("复习轮结束");
            return;
        }
        if (!ReduceMotionBox.IsChecked.GetValueOrDefault() && _currentPage == "review")
        {
            if (!await MoveReviewCardAsync(epoch, 0, -14, entering: false)) return;
            MemoryPresentReviewCard();
            RenderReviewCard(resetPose: false);
            if (!await MoveReviewCardAsync(epoch, 14, 0, entering: true)) return;
            SetReviewPose(0, 1);
        }
        else if (epoch == _reviewEpoch) { MemoryPresentReviewCard(); RenderReviewCard(); }
    }

    private Task ReclassifyReviewAsync()
    {
        if (_reviewBusy || _reviewWord == null || _reviewLastRating == StudyRating.Forgot || !FocusCanNavigate)
            return Task.CompletedTask;
        _reviewBusy = true;
        try
        {
            var word = _reviewWord;
            if (MemoryIsArchiveWord(word))
            {
                if (_reviewUndoWordId == word.Id && !_vocabService.UndoLastLearningAction(word.Id))
                    return Task.CompletedTask;
                _vocabService.MarkForgot(word.Id);
            }
            _reviewUndoWordId = word.Id;
            _reviewRound.UndoLast();
            if (_reviewRound.HasCurrent)
            {
                var forgot = _reviewRound.Commit(StudyRating.Forgot);
                _reviewShownStreak = forgot.Streak;
                _reviewShownTarget = forgot.Target;
                _reviewCompleted = false;
            }
            // 改判：在同一 presentation 上追加一条修正事件，presentationId 保持不变（规格书 §2）。
            MemoryRevised(MemorySurface.Review, MemoryReviewIdentity(word), _reviewLastRating, StudyRating.Forgot);
            _reviewLastRating = StudyRating.Forgot;
            RefreshWords();
            RevealReviewAnswer();
            ReviewHintText.Text = T("已改判为忘记，稍后重新学习。");
            SetStatus(T("已改判为忘记，稍后重新学习。"));
        }
        catch (Exception ex) { ReviewHintText.Text = T("改判失败：") + ex.Message; }
        finally { _reviewBusy = false; }
        return Task.CompletedTask;
    }

    private void UndoReviewFromLearningPage()
    {
        // 必须"有明确的被撤销词"才动手：重启恢复出来的轮里 _reviewUndoWordId 是空的（它只在内存里），
        // 而 _reviewRound.CanUndo 可能是 true；此时若继续，撤销会落到"当前显示的卡"上——
        // 那可能根本不是刚被评分的那个词（按钮此时也是禁用的，键盘路径必须与它一致）。
        if (_reviewBusy || _reviewUndoWordId is null || !_reviewRound.CanUndo || !FocusCanNavigate) return;
        try
        {
            var undoId = _reviewUndoWordId;
            // source 临时卡不在 _allWords 里，必须走登记表解析，否则撤销会静默跳过长期层。
            var undoneWord = MemoryReviewWordByIdentity(undoId);
            // 长期层是**提交点**：先撤销持久轨迹（Undone 事件）并失效已定稿的 canonical（规格书 §2 / §4），
            // 它成功之后才改旧列与轮内撤销记录。顺序反过来的话，旧列和轮先变化而长期层写失败时，
            // 界面已经"撤销成功"却没有失效 canonical；而且旧列的撤销快照已被消耗，重试只会静默什么都不做。
            var checkpoint = _reviewRound.CaptureCheckpoint();
            _reviewRound.UndoLast();
            if (undoneWord is not null)
            {
                try { MemoryUndone(MemorySurface.Review, MemoryReviewIdentity(undoneWord)); }
                catch { checkpoint.Restore(); throw; }
            }
            if (undoId is { } id)
            {
                // 长期层已经撤销；旧列只是兼容投影。返回 false 表示没有可回退的评分日志（异常状态），
                // 记诊断但不谎报撤销失败——canonical 已经按用户意图失效。
                if (MemoryIsArchiveWord(undoneWord) && !_vocabService.UndoLastLearningAction(id))
                    MemoryDiagnostic("撤销：旧列没有可回退的评分日志（id=" + id + "），长期层撤销已完成。");
                _reviewHandled.Remove(id);
            }
            _reviewUndoWordId = null;
            // 撤销后这张卡重新装填：作为一次新的呈现（旧的呈现已被 Undone 清空，不再产生 canonical）。
            MemoryPresentReviewCard();
            RefreshWords(); RenderReviewCard();
        }
        catch (Exception ex) { ReviewHintText.Text = T("撤销失败：") + ex.Message; }
    }

    private async Task MasterReviewFromLearningPageAsync()
    {
        if (_reviewBusy || _reviewWord == null || !FocusCanNavigate) return;
        _reviewBusy = true;
        try
        {
            var word = _reviewWord;
            // source 临时卡没有档案行：只把这张卡在本轮出队（不出队就会立刻又被装填回来）。
            if (MemoryIsArchiveWord(word)) _vocabService.ExecuteBatch([word.Id], "master");
            _reviewUndoWordId = word.Id;
            RefreshWords();
            var updated = _allWords.FirstOrDefault(w => w.Id == word.Id);
            _reviewHandled[word.Id] = (updated ?? word).Archive.Revision;
            if (!_reviewRevealed && _reviewRound.HasCurrent && _reviewRound.CurrentStep == StudyStep.Recall)
                _reviewRound.CompleteCurrent();
            // 出队改了轮内状态，必须立刻落盘：否则重启时持久 checkpoint 还描述着"掌握前"的轮，
            // 会把已经掌握出队的卡又装回来。
            MemorySaveRound();
            UpdateReviewBadge();
            await AdvanceReviewCoreAsync();
        }
        catch (Exception ex) { ReviewHintText.Text = T("本次复习未完成，请重试：") + ex.Message; }
        finally { _reviewBusy = false; }
    }
}
