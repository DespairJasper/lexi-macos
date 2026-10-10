namespace Lexi;

/// <summary>计划专用学习队列。所有进度只保存到计划文件，保存失败恢复原评价和队列。</summary>
public sealed class DailyStudyPlanSession
{
    private readonly DailyStudyPlan _plan;
    private readonly DateOnly _date;
    private PlanProgress? _beforeLastRating;
    private StudyRound<string>.Checkpoint? _beforeLastRound;
    private readonly List<string> _batchIds;
    private readonly string _activityId;
    private sealed record PlanProgress(HashSet<string> Completed, HashSet<string> Forgot,
        DailyStudyPlanStatus Status, DateOnly? BatchDate, DateOnly? CurrentDate,
        HashSet<DateOnly> Dates, List<DailyStudyPlanActivity> Activities);

    public DailyStudyPlanSession(DailyStudyPlan plan, DateOnly date, StudyRound<string>? round = null,
        StudyMode mode = StudyMode.FirstLearn, IReadOnlyList<DailyStudyPlanWord>? reviewWords = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _date = date;
        Round = round ?? new StudyRound<string>();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
        IReadOnlyList<DailyStudyPlanWord> remaining;
        if (mode == StudyMode.Review)
        {
            var scope = reviewWords ?? DailyStudyPlanRules.GetTodayBatch(plan, date);
            if (scope.Count == 0 && plan.Status == DailyStudyPlanStatus.Completed) scope = plan.Words;
            remaining = scope.Where(w => plan.CompletedWordIds.Contains(w.Id) && plan.Words.Any(p => p.Id == w.Id))
                .DistinctBy(w => w.Id).ToList();
            _batchIds = remaining.Select(w => w.Id).ToList();
        }
        else
        {
            DailyStudyPlanRules.RestoreLegacyBatch(plan);
            remaining = DailyStudyPlanRules.GetTodayWords(plan, date);
            if (remaining.Count > 0 && !plan.CurrentBatchWordIds.Any(id => !plan.CompletedWordIds.Contains(id)))
            {
                plan.CurrentBatchWordIds = remaining.Select(w => w.Id).ToList();
                plan.CurrentBatchRandomOrder = plan.RandomOrder;
            }
            if (remaining.Count > 0) plan.CurrentBatchDate = date;
            if (plan.CurrentBatchWordIds.Count > 0) plan.CurrentBatchRandomOrder ??= plan.RandomOrder;
            _batchIds = plan.CurrentBatchWordIds.ToList();
        }
        Round.Reset(remaining.Select(w => w.Id), mode,
            shuffle: plan.CurrentBatchRandomOrder ?? plan.RandomOrder);
        _activityId = Guid.NewGuid().ToString("N");
        if (remaining.Count > 0) plan.Activities.Add(new DailyStudyPlanActivity { Id = _activityId,
            Date = date, Kind = mode == StudyMode.Review ? "review" : "firstlearn",
            StartedAtUtc = DateTime.UtcNow, WordCount = remaining.Count });
    }

    /// <summary>
    /// 单词本轮定稿回调（长期记忆层的提交屏障，规格书 §3）。默认 null：
    /// 直接构造本类型的纯逻辑使用方（含既有 UI 测试）完全不受影响。
    /// </summary>
    public Action<string>? OnWordCompleted { get; set; }
    public Action<string, StudyRating, StudyCommitResult>? OnRatingApplied { get; set; }

    public StudyRound<string> Round { get; }
    public StudyMode Mode { get; }
    public IReadOnlyList<DailyStudyPlanWord> BatchWords => _batchIds
        .Select(id => _plan.Words.Single(w => w.Id == id)).ToList();
    public bool CanUndo => _beforeLastRating != null && _beforeLastRound != null && Round.CanUndo;

    public void CompleteLearn()
    {
        Round.CompleteLearn();
        _beforeLastRating = null; _beforeLastRound = null;
    }

    public StudyCommitResult? Rate(StudyRating rating, Func<bool> save)
    {
        var checkpoint = Round.CaptureCheckpoint();
        var progress = CaptureProgress();
        var previousUndo = _beforeLastRating;
        var wordId = Round.Current;
        var result = Round.Commit(rating);
        if (rating == StudyRating.Forgot) _plan.ForgotWordIds.Add(wordId);
        if (result.Completed && Mode == StudyMode.FirstLearn && !DailyStudyPlanRules.CompleteWord(_plan, wordId, _date))
        {
            checkpoint.Restore();
            RestoreProgress(progress);
            return null;
        }
        var finalized = false;
        // A real response counts as a study date even when the word needs another pass.
        _plan.LearningDates.Add(_date);
        UpdateActivity();
        try
        {
            // Persist the final real response before reducing it to a canonical.
            OnRatingApplied?.Invoke(wordId, rating, result);
            if (result.Completed && OnWordCompleted is not null) { OnWordCompleted(wordId); finalized = true; }
            if (!TrySave(save)) throw new IOException("Plan progress could not be saved.");
        }
        catch (Exception error)
        {
            if (finalized)
            {
                _beforeLastRating = progress; _beforeLastRound = checkpoint;
                throw new PendingLearningWriteException(wordId, result, error);
            }
            checkpoint.Restore(); RestoreProgress(progress); _beforeLastRating = previousUndo;
            return null;
        }
        _beforeLastRating = progress; _beforeLastRound = checkpoint;
        return result;
    }

    public bool Undo(Func<bool> save, Action<string>? applyMemory = null)
    {
        if (!CanUndo) return false;
        var checkpoint = Round.CaptureCheckpoint(); var current = CaptureProgress();
        _beforeLastRound!.Restore(); RestoreProgress(_beforeLastRating!);
        if (!TrySave(save)) { checkpoint.Restore(); RestoreProgress(current); return false; }
        try { applyMemory?.Invoke(Round.Current); }
        catch { checkpoint.Restore(); RestoreProgress(current); TrySave(save); throw; }
        _beforeLastRating = null; _beforeLastRound = null;
        return true;
    }

    public StudyCommitResult? ReclassifyAsForgot(Func<bool> save, Action<string>? applyMemory = null)
    {
        if (!CanUndo) return null;
        var checkpoint = Round.CaptureCheckpoint(); var current = CaptureProgress();
        _beforeLastRound!.Restore(); RestoreProgress(_beforeLastRating!);
        var id = Round.Current;
        var result = Round.Commit(StudyRating.Forgot);
        _plan.ForgotWordIds.Add(id);
        _plan.LearningDates.Add(_date);
        UpdateActivity();
        if (!TrySave(save)) { checkpoint.Restore(); RestoreProgress(current); return null; }
        try { applyMemory?.Invoke(id); }
        catch { checkpoint.Restore(); RestoreProgress(current); TrySave(save); throw; }
        return result;
    }

    private PlanProgress CaptureProgress() => new(new(_plan.CompletedWordIds, StringComparer.Ordinal),
        new(_plan.ForgotWordIds, StringComparer.Ordinal), _plan.Status, _plan.LastBatchCompletedDate,
        _plan.CurrentBatchDate, new(_plan.LearningDates), _plan.Activities.Select(CloneActivity).ToList());
    private void RestoreProgress(PlanProgress progress)
    {
        _plan.CompletedWordIds = new(progress.Completed, StringComparer.Ordinal);
        _plan.ForgotWordIds = new(progress.Forgot, StringComparer.Ordinal);
        _plan.Status = progress.Status; _plan.LastBatchCompletedDate = progress.BatchDate;
        _plan.CurrentBatchDate = progress.CurrentDate; _plan.LearningDates = new(progress.Dates);
        _plan.Activities = progress.Activities.Select(CloneActivity).ToList();
    }
    private void UpdateActivity()
    {
        var activity = _plan.Activities.FirstOrDefault(a => a.Id == _activityId);
        if (activity != null) activity.CompletedWordCount = Round.Completed;
    }
    private static DailyStudyPlanActivity CloneActivity(DailyStudyPlanActivity a) => new() { Id = a.Id,
        Date = a.Date, Kind = a.Kind, StartedAtUtc = a.StartedAtUtc, WordCount = a.WordCount,
        CompletedWordCount = a.CompletedWordCount };
    private static bool TrySave(Func<bool> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        try { return save(); }
        catch (Exception) { return false; }
    }
}
