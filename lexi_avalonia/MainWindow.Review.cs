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
        _reviewRound.Reset(GetPendingReviewWords(), StudyMode.Review);
        RenderReviewCard();
    }

    private List<WordItem> GetPendingReviewWords()
    {
        if (_reviewDay != DateTime.Today) { _reviewHandled.Clear(); _reviewDay = DateTime.Today; }
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        return _allWords.Where(w => w.Status == "learning" && w.NextReviewDate != null
            && string.CompareOrdinal(w.NextReviewDate, today) <= 0
            && (!_reviewHandled.TryGetValue(w.Id, out var handledRevision) || handledRevision != w.Archive.Revision))
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
        // 列数固定为 3：隐藏的按钮仍然参与网格测量，缩列会让 Grid 越界崩溃。
        ReviewRatingBar.ColumnDefinitions = new ColumnDefinitions("*,*,*");
        Grid.SetColumn(ReviewRememberBtn, 0);
        Grid.SetColumn(ReviewUnsureBtn, _reviewRevealed ? 2 : 1);
        Grid.SetColumn(ReviewUnfamiliarBtn, 2);
        Grid.SetColumnSpan(ReviewRememberBtn, _reviewRevealed ? 2 : 1);
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
        if (_reviewWord == null || _reviewRevealed || _reviewBusy || _restoring || !_databaseAvailable || !FocusCanNavigate)
            return Task.CompletedTask;
        _reviewBusy = true;
        ReviewRatingBar.IsEnabled = false;
        try
        {
            var word = _reviewWord;
            var result = _reviewRound.Commit(rating);
            try { ApplyReviewRating(word, rating, result.Completed); }
            catch { _reviewRound.UndoLast(); throw; }
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

    private void ApplyReviewRating(WordItem word, StudyRating rating, bool completed)
    {
        _reviewUndoWordId = null;
        if (rating == StudyRating.Known)
        {
            // 中间的认识只累计连击，只有本轮真正完成时才推进一次调度。
            if (!completed) return;
            _vocabService.ExecuteBatch([word.Id], "review");
        }
        else if (rating == StudyRating.Unsure) _vocabService.MarkUnsure(word.Id);
        else _vocabService.MarkForgot(word.Id);
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
            if (epoch == _reviewEpoch && _currentPage == "review") RenderReviewCard();
            return;
        }
        if (!ReduceMotionBox.IsChecked.GetValueOrDefault() && _currentPage == "review")
        {
            if (!await MoveReviewCardAsync(epoch, 0, -14, entering: false)) return;
            RenderReviewCard(resetPose: false);
            if (!await MoveReviewCardAsync(epoch, 14, 0, entering: true)) return;
            SetReviewPose(0, 1);
        }
        else if (epoch == _reviewEpoch) RenderReviewCard();
    }

    private Task ReclassifyReviewAsync()
    {
        if (_reviewBusy || _reviewWord == null || _reviewLastRating == StudyRating.Forgot || !FocusCanNavigate)
            return Task.CompletedTask;
        _reviewBusy = true;
        try
        {
            var word = _reviewWord;
            if (_reviewUndoWordId == word.Id && !_vocabService.UndoLastLearningAction(word.Id))
                return Task.CompletedTask;
            _vocabService.MarkForgot(word.Id);
            _reviewUndoWordId = word.Id;
            _reviewRound.UndoLast();
            if (_reviewRound.HasCurrent)
            {
                var forgot = _reviewRound.Commit(StudyRating.Forgot);
                _reviewShownStreak = forgot.Streak;
                _reviewShownTarget = forgot.Target;
                _reviewCompleted = false;
            }
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
        if (_reviewBusy || !_reviewRound.CanUndo || !FocusCanNavigate) return;
        try
        {
            if (_reviewUndoWordId is { } id)
            {
                if (!_vocabService.UndoLastLearningAction(id)) return;
                _reviewHandled.Remove(id);
            }
            _reviewUndoWordId = null;
            _reviewRound.UndoLast();
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
            _vocabService.ExecuteBatch([word.Id], "master");
            _reviewUndoWordId = word.Id;
            RefreshWords();
            var updated = _allWords.FirstOrDefault(w => w.Id == word.Id);
            if (updated != null) _reviewHandled[word.Id] = updated.Archive.Revision;
            if (!_reviewRevealed && _reviewRound.HasCurrent && _reviewRound.CurrentStep == StudyStep.Recall)
                _reviewRound.CompleteCurrent();
            UpdateReviewBadge();
            await AdvanceReviewCoreAsync();
        }
        catch (Exception ex) { ReviewHintText.Text = T("本次复习未完成，请重试：") + ex.Message; }
        finally { _reviewBusy = false; }
    }
}
