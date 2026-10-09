using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Diagnostics;

namespace Lexi;

public partial class MainWindow
{
    // Keep the persisted post-rating revision so explicit mutations (undo/reschedule) can
    // make the word eligible again during the same calendar day.
    private readonly Dictionary<long, int> _reviewHandled = [];
    private readonly StudyRound<long> _reviewRound = new();
    private StudyRound<long>.Checkpoint? _reviewUndo;
    private long _reviewUndoWord;
    private bool _reviewUndoPersisted;
    private bool _reviewRoundStarted;
    private Dictionary<long,int> _reviewVersions = [];
    private int _reviewUndoRevision;
    private DateTime _reviewDay = DateTime.Today;
    private WordItem? _reviewWord;
    private bool _reviewRevealed;
    private bool _reviewRestoreRevealed;
    private bool _reviewBusy;
    private int _reviewEpoch;

    private void BindReviewEvents()
    {
        ReviewRevealBtn.Click += (_, _) => RevealReview();
        ReviewRememberBtn.Click += async (_, _) => await RateReviewAsync(true);
        ReviewUnfamiliarBtn.Click += async (_, _) => await RateReviewAsync(false);
        ReviewUnsureBtn.Click += async (_, _) => await RateReviewRatingAsync(StudyRating.Unsure);
        ReviewRoundUndoBtn.Click += (_, _) => UndoRoundRating();

        ToolTip.SetTip(ReviewUnfamiliarBtn, "忘记 (← 或 1)");
        ToolTip.SetTip(ReviewUnsureBtn, "模糊 (↓ 或 2)");
        ToolTip.SetTip(ReviewRememberBtn, "认识 (→ 或 3)");
        ToolTip.SetTip(ReviewRevealBtn, "揭晓释义 (Space / Enter)");
        ToolTip.SetTip(ReviewRoundUndoBtn, "撤销 (Ctrl+Z)");
    }

    private void OpenReviewDeck()
    {
        try { OpenReviewDeckCore(); }
        catch (Exception ex)
        {
            _reviewWord = null; _reviewRoundStarted = false;
            ReviewCardHost.IsVisible = false; ReviewEmptyCard.IsVisible = true;
            ReviewRevealBtn.IsVisible = ReviewRatingBar.IsVisible = false;
            SetStatus("复习断点暂时无法恢复，原记录已保留：" + ex.Message);
        }
    }

    private void OpenReviewDeckCore()
    {
        ++_reviewEpoch;
        var due = GetPendingReviewWords();
        var changedExternally = _allWords.Count != _reviewVersions.Count || _allWords.Any(w => !_reviewVersions.TryGetValue(w.Id, out var revision) || revision != w.Archive.Revision);
        if (!_reviewRoundStarted || changedExternally || _reviewRound.IsFinished && due.Count > 0)
        {
            _reviewRound.Reset(due.Select(w => w.Id), StudyMode.Review);
            _reviewUndo = null;
            BeginReviewMemoryCore(due.Count, !_reviewRoundStarted);
            _reviewRoundStarted = true;
            CaptureReviewVersions();
        }
        RenderReviewCard();
    }

    private List<WordItem> GetPendingReviewWords()
    {
        if (_reviewDay != DateTime.Today) { _reviewHandled.Clear(); _reviewDay = DateTime.Today; _reviewRoundStarted = false; }
        var dueIds = DueArchiveIds();
        return _allWords.Where(w => dueIds.Contains(w.Id)
            && (!_reviewHandled.TryGetValue(w.Id, out var handledRevision) || handledRevision != w.Archive.Revision))
            .Concat(SourceDueWords().Where(w => !_reviewHandled.ContainsKey(w.Id)))
            .OrderBy(w => w.NextReviewDate).ThenBy(w => w.Id).ToList();
    }

    private void UpdateReviewBadge() => NavReviewBadge.Text = GetPendingReviewWords().Count.ToString();

    private void RenderReviewCard(bool resetPose = true)
    {
        ApplyReviewCanvasTheme();
        var due = GetPendingReviewWords();
        if (!_reviewRoundStarted)
        {
            _reviewRound.Reset(due.Select(w => w.Id), StudyMode.Review);
            _reviewUndo = null;
            BeginReviewMemory(due.Count);
            _reviewRoundStarted = true;
            CaptureReviewVersions();
        }
        NavReviewBadge.Text = due.Count.ToString();
        _reviewWord = _reviewRound.HasCurrent ? ResolveReviewWord(_reviewRound.Current) : null;
        while (_reviewRound.HasCurrent && _reviewWord == null)
        {
            _reviewRound.CompleteCurrent();
            _reviewWord = _reviewRound.HasCurrent ? ResolveReviewWord(_reviewRound.Current) : null;
        }
        _reviewRevealed = false;
        ReviewRemainingText.Text = $"已完成 {_reviewRound.Completed} / {_reviewRound.Total} · 连续认识 {_reviewRound.CurrentStreak} / {_reviewRound.CurrentTarget}";
        ReviewRoundUndoBtn.IsEnabled = _reviewUndo != null;
        ReviewAnswer.IsVisible = ReviewRatingBar.IsVisible = false;
        ReviewMeaningText.Text = ""; ReviewDefinitionText.Text = "";
        ReviewRevealBtn.IsVisible = _reviewWord != null;
        ReviewCardHost.IsVisible = _reviewWord != null;
        ReviewEmptyCard.IsVisible = _reviewWord == null;
        ReviewBackOne.IsVisible = due.Count > 1;
        ReviewBackTwo.IsVisible = due.Count > 2;
        ReviewWordText.Text = _reviewWord?.Word ?? "";
        ReviewPhoneticText.Text = _reviewWord?.Phonetic ?? "";
        ReviewHintText.Text = "先回忆再揭晓。首次认识即可完成；模糊或忘记后需连续认识三次。";
        PresentReviewMemory(_reviewWord);
        if (_reviewRestoreRevealed) { _reviewRestoreRevealed = false; RevealReview(); }
        if (resetPose) SetReviewPose(0, 1);
        if (_reviewWord != null && _currentPage == "review") ReviewRevealBtn.Focus();
    }

    private void SetReviewPose(double x, double opacity)
    {
        if (ReviewCard.RenderTransform is not TranslateTransform) ReviewCard.RenderTransform = new TranslateTransform();
        ((TranslateTransform)ReviewCard.RenderTransform).X = x;
        ReviewCard.Opacity = opacity;
    }

    // Keep final property values rather than letting an animation clock revert to the
    // old visible card. Content is replaced only at opacity zero, then eased in.
    private async Task<bool> MoveReviewCardAsync(int epoch, double fromX, double toX, bool entering)
    {
        var duration = entering ? 190d : 150d;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (epoch != _reviewEpoch || _currentPage != "review") return false;
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration);
            var ease = entering ? 1 - Math.Pow(1 - t, 3) : t * t;
            SetReviewPose(fromX + (toX - fromX) * ease, entering ? ease : 1 - ease);
            if (t >= 1) return true;
            await Task.Delay(16);
        }
    }

    private void RevealReview()
    {
        if (_reviewWord == null || _reviewBusy || _reviewRevealed) return;
        _reviewRevealed = true;
        ReviewMeaningText.Text = _reviewWord.Translation;
        ReviewDefinitionText.Text = _reviewWord.Definition;
        ReviewDefinitionText.IsVisible = !string.IsNullOrWhiteSpace(_reviewWord.Definition);
        ReviewAnswer.IsVisible = ReviewRatingBar.IsVisible = true;
        ReviewRevealBtn.IsVisible = false;
        ReviewHintText.Text = "按刚才的回忆判断，不必勉强。";
        ReviewRememberBtn.Focus();
        PersistLearningSurface("review");
    }

    private Task RateReviewAsync(bool remembered) => RateReviewRatingAsync(remembered ? StudyRating.Known : StudyRating.Forgot);

    private async Task RateReviewRatingAsync(StudyRating rating)
    {
        if (_reviewWord == null || !_reviewRevealed || _reviewBusy || _restoring || !_databaseAvailable) return;
        if (_reviewDay != DateTime.Today) { OpenReviewDeck(); SetStatus("日期已变化，今日复习队列已刷新，请重新回忆。"); return; }
        _reviewBusy = true;
        ReviewRatingBar.IsEnabled = false;
        var word = _reviewWord;
        var epoch = _reviewEpoch;
        var checkpoint = _reviewRound.CaptureCheckpoint();
        var memoryRated = false;
        try
        {
            var before = _reviewRound.CurrentStreak;
            var result = _reviewRound.Commit(rating);
            RateReviewMemory(rating, before, result.Streak, result.Completed);
            memoryRated = true;
            _reviewUndo = checkpoint;
            _reviewUndoWord = word.Id;
            _reviewUndoPersisted = false;
            RefreshWords();
            CaptureReviewVersions();
            var updated = ResolveReviewWord(word.Id);
            _reviewUndoRevision = updated?.Archive.Revision ?? -1;
            if (result.Completed && updated != null) _reviewHandled[word.Id] = updated.Archive.Revision;
            UpdateReviewBadge();
            if (!ReduceMotionBox.IsChecked.GetValueOrDefault() && _currentPage == "review" && epoch == _reviewEpoch)
            {
                var direction = rating == StudyRating.Known ? 1 : -1;
                if (!await MoveReviewCardAsync(epoch, 0, direction * 48, false)) return;
                RenderReviewCard(resetPose: false);
                SetReviewPose(-direction * 32, 0);
                if (_reviewWord != null && !await MoveReviewCardAsync(epoch, -direction * 32, 0, true)) return;
                SetReviewPose(0, 1);
            }
            else if (epoch == _reviewEpoch) RenderReviewCard();
            PersistLearningSurface("review");
            SetStatus(result.Completed ? "已完成本轮并更新 FSRS 排期。" : rating == StudyRating.Forgot ? "连击已清零，将在本轮重学。" : rating == StudyRating.Unsure ? "连击减一，将在本轮稍后再出现。" : "认识次数已记录，继续累计三次。" );
        }
        catch (Exception ex)
        {
            if (memoryRated) { try { UndoReviewMemory(); } catch (Exception undoError) { SetStatus("复习回滚失败：" + undoError.Message); return; } }
            checkpoint.Restore(); _reviewBusy = false; RenderReviewCard(); RevealReview();
            SetStatus("本次复习未完成，请重试：" + ex.Message);
        }
        finally { _reviewBusy = false; ReviewRatingBar.IsEnabled = true; }
    }

    private void UndoRoundRating()
    {
        if (_reviewUndo == null || _reviewBusy || _restoring || !_databaseAvailable) return;
        try
        {
            var latest = ResolveReviewWord(_reviewUndoWord);
            if (latest == null || _reviewUndoWord >= 0 && latest.Archive.Revision != _reviewUndoRevision)
            {
                _reviewUndo = null;
                RefreshWords(); OpenReviewDeck();
                SetStatus("该词已有新的档案或排期变更，本轮已刷新，旧评价不能撤销。");
                return;
            }
            UndoReviewMemory();
            if (_reviewUndoPersisted && !_vocabService.UndoLastReview(_reviewUndoWord))
                throw new InvalidOperationException("该词已有新的变更，无法撤销本次评价。");
            _reviewUndo.Restore();
            _reviewHandled.Remove(_reviewUndoWord);
            _reviewUndo = null;
            RefreshWords(); CaptureReviewVersions(); RenderReviewCard();
            PersistLearningSurface("review");
            SetStatus("已撤销上一次评价与对应排期变更。");
        }
        catch (Exception ex) { SetStatus("撤销未完成：" + ex.Message); }
    }

    private void CaptureReviewVersions() => _reviewVersions = _allWords.ToDictionary(w => w.Id, w => w.Archive.Revision);
}
