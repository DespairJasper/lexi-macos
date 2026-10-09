namespace Lexi;

/// <summary>计划专用学习队列。所有进度只保存到计划文件，保存失败恢复原评价和队列。</summary>
public sealed class DailyStudyPlanSession
{
    private readonly DailyStudyPlan _plan;
    private readonly DateOnly _date;
    private PlanProgress? _beforeLastRating;
    private StudyRound<string>.Checkpoint? _beforeLastRound;
    private sealed record PlanProgress(HashSet<string> Completed, HashSet<string> Forgot,
        DailyStudyPlanStatus Status, DateOnly? BatchDate);

    public DailyStudyPlanSession(DailyStudyPlan plan, DateOnly date, StudyRound<string>? round = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _date = date;
        Round = round ?? new StudyRound<string>();
        DailyStudyPlanRules.RestoreLegacyBatch(plan);
        var remaining = DailyStudyPlanRules.GetTodayWords(plan, date);
        if (remaining.Count > 0 && !plan.CurrentBatchWordIds.Any(id => !plan.CompletedWordIds.Contains(id)))
        {
            plan.CurrentBatchWordIds = remaining.Select(w => w.Id).ToList();
            plan.CurrentBatchRandomOrder = plan.RandomOrder;
        }
        if (plan.CurrentBatchWordIds.Count > 0) plan.CurrentBatchRandomOrder ??= plan.RandomOrder;
        Round.Reset(remaining.Select(w => w.Id), StudyMode.FirstLearn,
            shuffle: plan.CurrentBatchRandomOrder ?? plan.RandomOrder);
    }

    public Action<string>? OnWordCompleted { get; set; }
    public Action<string, StudyRating, StudyCommitResult>? OnRatingApplied { get; set; }
    public Action<string>? OnRatingFailed { get; set; }
    public Action? OnUndoApplied { get; set; }

    public StudyRound<string> Round { get; }
    public IReadOnlyList<DailyStudyPlanWord> BatchWords => _plan.CurrentBatchWordIds
        .Select(id => _plan.Words.Single(w => w.Id == id)).ToList();
    public bool CanUndo => _beforeLastRating != null && _beforeLastRound != null && Round.CanUndo;

    private sealed record DurableUndo(string Round, HashSet<string> Completed, HashSet<string> Forgot,
        DailyStudyPlanStatus Status, DateOnly? BatchDate);
    public string? CaptureUndoJson()
    {
        if (_beforeLastRating == null || _beforeLastRound == null) return null;
        var after = Round.CaptureCheckpoint();
        _beforeLastRound.Restore();
        var json = Round.CaptureJson(w => w);
        after.Restore();
        return System.Text.Json.JsonSerializer.Serialize(new DurableUndo(json, _beforeLastRating.Completed,
            _beforeLastRating.Forgot, _beforeLastRating.Status, _beforeLastRating.BatchDate));
    }

    public void RestoreUndoJson(string? json)
    {
        if (json == null || !Round.CanUndo) return;
        var saved = System.Text.Json.JsonSerializer.Deserialize<DurableUndo>(json)
            ?? throw new InvalidDataException("计划撤销断点为空。");
        var after = Round.CaptureCheckpoint();
        try
        {
            Round.RestoreJson(saved.Round, id => _plan.Words.Single(w => w.Id == id).Id);
            _beforeLastRound = Round.CaptureCheckpoint();
            _beforeLastRating = new(saved.Completed, saved.Forgot, saved.Status, saved.BatchDate);
        }
        finally { after.Restore(); }
    }

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
        var previousRoundUndo = _beforeLastRound;
        var wordId = Round.Current;
        var result = Round.Commit(rating);
        if (rating == StudyRating.Forgot) _plan.ForgotWordIds.Add(wordId);
        if (result.Completed && !DailyStudyPlanRules.CompleteWord(_plan, wordId, _date))
        {
            checkpoint.Restore();
            RestoreProgress(progress);
            return null;
        }
        var finalized = false;
        _beforeLastRating = progress; _beforeLastRound = checkpoint;
        try
        {
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
            OnRatingFailed?.Invoke(wordId);
            checkpoint.Restore(); RestoreProgress(progress); _beforeLastRating = previousUndo; _beforeLastRound = previousRoundUndo;
            return null;
        }
        _beforeLastRating = progress; _beforeLastRound = checkpoint;
        return result;
    }

    public bool Undo(Func<bool> save)
    {
        if (!CanUndo) return false;
        var checkpoint = Round.CaptureCheckpoint(); var current = CaptureProgress();
        _beforeLastRound!.Restore(); RestoreProgress(_beforeLastRating!);
        try { OnUndoApplied?.Invoke(); }
        catch { checkpoint.Restore(); RestoreProgress(current); throw; }
        if (!TrySave(save))
        {
            if (OnUndoApplied is not null)
            {
                _beforeLastRating = null; _beforeLastRound = null;
                throw new IOException("撤销已持久化，计划进度等待写回，请重试恢复。");
            }
            checkpoint.Restore(); RestoreProgress(current); return false;
        }
        _beforeLastRating = null; _beforeLastRound = null;
        return true;
    }

    public StudyCommitResult? ReclassifyAsForgot(Func<bool> save)
    {
        if (!CanUndo) return null;
        var checkpoint = Round.CaptureCheckpoint(); var current = CaptureProgress();
        _beforeLastRound!.Restore(); RestoreProgress(_beforeLastRating!);
        var id = Round.Current;
        var result = Round.Commit(StudyRating.Forgot);
        _plan.ForgotWordIds.Add(id);
        if (!TrySave(save)) { checkpoint.Restore(); RestoreProgress(current); return null; }
        return result;
    }

    private PlanProgress CaptureProgress() => new(new(_plan.CompletedWordIds, StringComparer.Ordinal),
        new(_plan.ForgotWordIds, StringComparer.Ordinal), _plan.Status, _plan.LastBatchCompletedDate);
    private void RestoreProgress(PlanProgress progress)
    {
        _plan.CompletedWordIds = new(progress.Completed, StringComparer.Ordinal);
        _plan.ForgotWordIds = new(progress.Forgot, StringComparer.Ordinal);
        _plan.Status = progress.Status; _plan.LastBatchCompletedDate = progress.BatchDate;
    }
    private static bool TrySave(Func<bool> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        try { return save(); }
        catch (Exception) { return false; }
    }
}
