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
        ReviewRememberBtn.Click += async (_, _) => await OnReviewRatingAsync(StudyRating.Known);
        ReviewUnsureBtn.Click += async (_, _) => await OnReviewRatingAsync(StudyRating.Unsure);
        ReviewUnfamiliarBtn.Click += async (_, _) => await OnReviewRatingAsync(StudyRating.Forgot);
        ReviewBackBtn.Click += (_, _) => ShowPage("lookup");
        ReviewUndoBtn.Click += (_, _) => UndoReviewFromLearningPage();
        ReviewMasterBtn.Click += async (_, _) => await MasterReviewFromLearningPageAsync();
        ReviewRestartBtn.Click += (_, _) => ShowPage("lookup");
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
        var words = GetPendingReviewWords();
        _reviewRound.Reset(words, StudyMode.Review);
        // 开轮（规格书 §1）：每个开轮点只开一次会话，随后装填本轮第一张卡。
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
        ReviewRestartBtn.Content = T("返回阅读");
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
