using System.Text.Json;

namespace Lexi;

public partial class MainWindow
{
    private sealed record SurfaceCheckpoint(int Version, string Scope, string Round,
        MemoryPresentation? Current, MemoryPresentation? Last, bool LastCommitted,
        bool Revealed, bool Rated, StudyRating LastRating, string? RatedWord,
        string PreviousPage, string Translation, string? PendingKey = null,
        StudyMode PendingMode = StudyMode.Review, string? PlanJson = null, string? PlanUndo = null,
        long UndoWord = 0, bool UndoPersisted = false, int UndoRevision = -1,
        long? FocusUndoArchiveId = null, int? FocusUndoRevision = null, bool FocusUndoIsRating = true,
        Dictionary<long, int>? ReviewVersions = null);

    private MemorySessionCheckpoint? CaptureSurfaceCheckpoint(string surface, WordKey? pending = null,
        StudyMode pendingMode = StudyMode.Review)
    {
        LearningMemoryCoordinator? memory;
        SurfaceCheckpoint saved;
        if (surface == "review")
        {
            memory = _reviewMemory;
            saved = new(1, "review", _reviewRound.CaptureJson(id => id.ToString()),
                _reviewPresentation, _reviewLastPresentation, _reviewLastCommitted,
                _reviewRevealed, false, StudyRating.Known, null, "review", "",
                ReviewVersions: new Dictionary<long, int>(_reviewVersions));
        }
        else if (surface == "focus" && _focusRound != null)
        {
            memory = _focusMemory;
            var scope = FocusSurfaceScope();
            saved = new(1, scope, _focusRound.CaptureJson(w => w),
                _focusPresentation, _focusLastPresentation, _focusLastCommitted,
                _focusAnswerVisible, _focusRated, _focusLastRating, _focusRatedWord,
                _focusPreviousPage, _focusOriginalTranslation);
        }
        else if (surface == "plan" && _dailyLearningSession != null && _activeLearningPlan != null)
        {
            memory = _planMemory;
            saved = new(1, _activeLearningPlan.Id, _dailyLearningSession.Round.CaptureJson(w => w),
                _planPresentation, _planLastPresentation, _planLastCommitted,
                _planAnswerVisible, false, StudyRating.Known, null, "learning", "");
        }
        else return null;
        saved = saved with { PendingKey = pending?.Key, PendingMode = pendingMode,
            PlanJson = surface == "plan" ? JsonSerializer.Serialize(_activeLearningPlan) : null,
            PlanUndo = surface == "plan" ? _dailyLearningSession?.CaptureUndoJson() : null,
            UndoWord = _reviewUndoWord, UndoPersisted = _reviewUndoPersisted, UndoRevision = _reviewUndoRevision,
            FocusUndoArchiveId = _focusUndoArchiveId, FocusUndoRevision = _focusUndoRevision,
            FocusUndoIsRating = _focusUndoIsRating };
        if (memory == null || string.IsNullOrWhiteSpace(memory.CurrentSessionId)) return null;
        return new(surface, memory.CurrentSessionId, JsonSerializer.Serialize(saved), DateTime.UtcNow);
    }

    private void PersistLearningSurface(string surface)
    {
        if (_learningMemoryStore == null) return;
        var checkpoint = CaptureSurfaceCheckpoint(surface);
        if (checkpoint != null) _learningMemoryStore.SaveSessionCheckpoint(checkpoint);
    }

    private void PersistPausedSurfaces()
    {
        foreach (var surface in new[] { "review", "focus", "plan" })
            try { PersistLearningSurface(surface); }
            catch (Exception ex) { SetStatus("学习断点保存失败，当前内存进度保留：" + ex.Message); }
    }

    private bool RestoreLearningSurface(string surface, string scope, LearningMemoryCoordinator memory)
    {
        var checkpoint = _learningMemoryStore?.GetSessionCheckpoint(surface);
        if (checkpoint == null) return false;
        var saved = JsonSerializer.Deserialize<SurfaceCheckpoint>(checkpoint.QueueJson)
            ?? throw new InvalidDataException("学习断点内容为空。");
        if (saved.Version != 1) throw new InvalidDataException("学习断点版本无法恢复，已保留原记录。");
        if (saved.Scope != scope) return false;
        if (surface == "review" && saved.ReviewVersions is { Count: > 0 } versions &&
            (_allWords.Count != versions.Count || _allWords.Any(w =>
                !versions.TryGetValue(w.Id, out var revision) || revision != w.Archive.Revision)))
        {
            _learningMemoryStore!.DeleteSessionCheckpoint(surface, checkpoint.SessionId);
            return false;
        }
        using var roundDocument = JsonDocument.Parse(saved.Round);
        var roundRoot = roundDocument.RootElement;
        var finished = roundRoot.GetProperty("Completed").GetInt32() == roundRoot.GetProperty("Total").GetInt32();
        if (!memory.ResumeSession(checkpoint.SessionId)) return false;
        if (saved.PendingKey is { } pendingKey)
        {
            if (surface == "plan" && saved.PlanJson is { } planJson)
            {
                var progress = JsonSerializer.Deserialize<DailyStudyPlan>(planJson)!;
                _activeLearningPlan!.CompletedWordIds = progress.CompletedWordIds;
                _activeLearningPlan.ForgotWordIds = progress.ForgotWordIds;
                _activeLearningPlan.Status = progress.Status;
                _activeLearningPlan.LastBatchCompletedDate = progress.LastBatchCompletedDate;
                var mutation = CrossStoreJournal.CreateJsonSnapshot("daily-plans.json", JsonSerializer.Serialize(_learningPlans));
                memory.CommitWord(WordKey.Parse(pendingKey), saved.PendingMode, outboxMutations: [mutation]);
                if (!ReplayMemoryJournal()) throw new IOException("计划断点等待进度写回。");
            }
            else memory.CommitWord(WordKey.Parse(pendingKey), saved.PendingMode);
            saved = saved with { PendingKey = null, LastCommitted = true };
            _learningMemoryStore!.SaveSessionCheckpoint(checkpoint with { QueueJson = JsonSerializer.Serialize(saved) });
        }
        if (finished)
        {
            _learningMemoryStore!.DeleteSessionCheckpoint(surface, checkpoint.SessionId);
            return false;
        }
        if (surface == "review")
        {
            SourceDueWords();
            _reviewRound.RestoreJson(saved.Round, id =>
            {
                if (!long.TryParse(id, out var numeric) || ResolveReviewWord(numeric) == null)
                    throw new InvalidDataException("断点词条已不存在：" + id);
                return numeric;
            });
            _reviewPresentation = saved.Current; _reviewLastPresentation = saved.Last;
            _reviewLastCommitted = saved.LastCommitted; _reviewRevealed = saved.Revealed;
            _reviewRestoreRevealed = saved.Revealed;
            if (_reviewRound.CanUndo && saved.Last != null)
            {
                var after = _reviewRound.CaptureCheckpoint();
                var undo = _reviewRound.UndoLast()!.Value;
                _reviewUndo = _reviewRound.CaptureCheckpoint(); after.Restore();
                _reviewUndoWord = undo.Word; _reviewUndoPersisted = saved.UndoPersisted;
                _reviewUndoRevision = saved.UndoRevision;
            }
        }
        else if (surface == "focus")
        {
            _focusRound!.RestoreJson(saved.Round, id => id);
            _focusPresentation = saved.Current; _focusLastPresentation = saved.Last;
            _focusLastCommitted = saved.LastCommitted; _focusAnswerVisible = saved.Revealed;
            _focusRated = saved.Rated; _focusLastRating = saved.LastRating;
            _focusRatedWord = saved.RatedWord;
            _focusOriginalTranslation = saved.Translation;
            if (_focusRound.CanUndo)
            {
                var after = _focusRound.CaptureCheckpoint(); _focusRound.UndoLast();
                _focusUndo = _focusRound.CaptureCheckpoint(); after.Restore();
                _focusUndoArchiveId = saved.FocusUndoArchiveId; _focusUndoRevision = saved.FocusUndoRevision;
                _focusUndoIsRating = saved.FocusUndoIsRating;
            }
        }
        else
        {
            _dailyLearningSession!.Round.RestoreJson(saved.Round, id =>
                _activeLearningPlan!.Words.Single(w => w.Id == id).Id);
            _planPresentation = saved.Current; _planLastPresentation = saved.Last;
            _planLastCommitted = saved.LastCommitted; _planAnswerVisible = saved.Revealed;
            _dailyLearningSession.RestoreUndoJson(saved.PlanUndo);
        }
        return true;
    }
}
