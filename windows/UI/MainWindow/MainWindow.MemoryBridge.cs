namespace Lexi;

public partial class MainWindow
{
    private sealed record MemoryPresentation(WordKey Key, string Id, string Identity, StudyStep Step);

    private LearningMemoryCoordinator? _learningMemory;
    private LearningMemoryCoordinator? _reviewMemory, _focusMemory, _planMemory;
    private Fsrs6Scheduler? _memoryScheduler;
    private CancellationTokenSource _memoryLifetime = new();
    private ILearningMemoryStore? _learningMemoryStore;
    private IVocabularyArchive? _learningMemoryArchive;
    private FsrsPersonalizationRuntime? _memoryParameterRuntime;
    private ContextCalibrator? _memoryContextTrainer;
    private bool _contextTrainingRunning;
    private DateTime? _contextCompletedAtUtc;
    private DateTime? _contextFailedAtUtc;
    private CrossStoreJournal? _memoryJournal;
    private MemoryPresentation? _reviewPresentation;
    private MemoryPresentation? _reviewLastPresentation;
    private bool _reviewLastCommitted;
    private MemoryPresentation? _focusPresentation;
    private MemoryPresentation? _focusLastPresentation;
    private bool _focusLastCommitted;
    private MemoryPresentation? _planPresentation;
    private MemoryPresentation? _planLastPresentation;
    private bool _planLastCommitted;
    private int _planStreakBefore;
    private bool _planRatingApplied;

    private LearningMemoryCoordinator Memory()
    {
        if (ReferenceEquals(_learningMemoryArchive, _vocabService) && _learningMemory != null)
            return _learningMemory;

        var store = ServiceFactory.OpenMemory(_vocabService);
        var journal = new CrossStoreJournal(store, Path.GetDirectoryName(store.DatabasePath)!);
        var replay = journal.Replay(DateTime.UtcNow);
        if (!replay.Succeeded) throw new IOException("待完成的学习进度尚未写回，请检查数据目录。" + replay.Error);
        if (replay.Applied > 0 && _learningHubPage != null) ReloadLearningPlans();
        var weights = new SchedulerWeights();
        var helper = MemoryResolveOptimizerHelperPath();
        var startup = FsrsPersonalizationStartup.Create(store, weights,
            _ => new FsrsParameterOptimizer(new FsrsOptimizerOptions { HelperPath = helper }));
        if (!startup.Consistent)
            throw new InvalidOperationException("长期记忆参数与已有卡片不一致，已暂停写入以保护学习数据。" + startup.Error);
        var scheduler = new Fsrs6Scheduler(weights);
        _memoryLifetime.Cancel();
        _memoryLifetime.Dispose();
        _memoryLifetime = new CancellationTokenSource();
        _contextTrainingRunning = false;
        _memoryScheduler = scheduler;
        _memoryParameterRuntime?.Invalidate();
        _memoryParameterRuntime = new FsrsPersonalizationRuntime(startup.Service,
            deferPublish: () => _restoring || !_databaseAvailable || _focusActive || _currentPage == "review"
                || _dailyLearningSession is { Round.IsFinished: false },
            diagnostic: message => Console.Error.WriteLine("[memory] " + message));
        _memoryContextTrainer = new ContextCalibrator(store, scheduler);
        _learningMemory = new LearningMemoryCoordinator(store, scheduler,
            new ContextFeatureProvider(store, scheduler), _memoryContextTrainer);
        _reviewMemory = _focusMemory = _planMemory = null;
        _learningMemoryStore = store;
        _memoryJournal = journal;
        _learningMemoryArchive = _vocabService;
        _reviewPresentation = _reviewLastPresentation = null;
        _focusPresentation = _focusLastPresentation = null;
        _planPresentation = _planLastPresentation = null;
        return _learningMemory;
    }

    // Separate coordinator state for each surface. Sharing the store/scheduler is safe;
    // sharing the current session/presentation dictionary crosses learning identities.
    private LearningMemoryCoordinator SurfaceMemory(ref LearningMemoryCoordinator? memory)
    {
        Memory();
        return memory ??= new LearningMemoryCoordinator(_learningMemoryStore!, _memoryScheduler!,
            new ContextFeatureProvider(_learningMemoryStore!, _memoryScheduler!), _memoryContextTrainer);
    }

    private static string MemoryResolveOptimizerHelperPath()
    {
        var configured = Environment.GetEnvironmentVariable("LEXI_FSRS_OPTIMIZER_BIN");
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured
            : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fsrs-optimizer.exe" : "fsrs-optimizer");
    }

    private void BeginReviewMemory(int count) => BeginReviewMemoryCore(count, true);

    private void BeginReviewMemoryCore(int count, bool allowRestore)
    {
        var memory = SurfaceMemory(ref _reviewMemory);
        TryTrainContext("review-start");
        if (allowRestore && RestoreLearningSurface("review", "review", memory)) return;
        memory.BeginSession(StudyMode.Review, WordSource.Archive, "", count);
        _reviewPresentation = _reviewLastPresentation = null;
        _reviewLastCommitted = false;
        PersistLearningSurface("review");
    }

    private void PresentReviewMemory(WordItem? word)
    {
        if (word == null || _reviewPresentation?.Identity == word.Id.ToString()) return;
        var key = ReviewKey(word);
        var presented = new MemoryPresentation(key, Guid.NewGuid().ToString("N"), word.Id.ToString(), StudyStep.Recall);
        SurfaceMemory(ref _reviewMemory).OnPresented(key, presented.Id, isRecall: true);
        _reviewPresentation = presented;
        PersistLearningSurface("review");
    }

    private void RateReviewMemory(StudyRating rating, int before, int after, bool completed)
    {
        var presented = _reviewPresentation ?? throw new InvalidOperationException("复习卡没有对应的呈现记录。");
        var memory = SurfaceMemory(ref _reviewMemory);
        _reviewLastPresentation = presented;
        _reviewLastCommitted = false;
        _reviewPresentation = null;
        memory.OnRated(presented.Key, presented.Id, rating, before, after, null,
            CaptureSurfaceCheckpoint("review", completed ? presented.Key : null));
        try
        {
            if (completed)
            {
                _ = memory.CommitWord(presented.Key, StudyMode.Review)
                    ?? throw new InvalidOperationException("复习轨迹未能生成长期排期。");
                _reviewLastCommitted = true;
                if (_reviewRound.IsFinished) NotifyMemoryRoundBoundary("review-completed");
            }
        }
        catch { UndoReviewMemory(); throw; }
        _reviewPresentation = null;
        PersistLearningSurface("review");
    }

    private void UndoReviewMemory()
    {
        var presented = _reviewLastPresentation ?? throw new InvalidOperationException("没有可撤销的复习轨迹。");
        var memory = SurfaceMemory(ref _reviewMemory);
        memory.OnUndone(presented.Key, presented.Id, canonicalId: _reviewLastCommitted
            ? CanonicalReview.BuildId(presented.Key.Key, memory.CurrentSessionId) : null);
        _reviewPresentation = presented;
        _reviewLastPresentation = null;
        _reviewLastCommitted = false;
    }

    private HashSet<long> DueArchiveIds()
    {
        var memory = Memory();
        var store = _learningMemoryStore as VocabularyService;
        if (store == null) throw new InvalidOperationException("长期复习词库未绑定。");
        var cardKeys = store.GetMemoryCardKeys();
        var dueKeys = memory.QueryDue(DateTime.UtcNow, int.MaxValue)
            .Select(card => card.WordKey).ToHashSet(StringComparer.Ordinal);
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        return _allWords.Where(word => word.Status == "learning" &&
            (dueKeys.Contains(WordKeyResolver.FromArchive(word).Key) ||
             (!cardKeys.Contains(WordKeyResolver.FromArchive(word).Key) && word.NextReviewDate != null &&
              string.CompareOrdinal(word.NextReviewDate, today) <= 0)))
            .Select(word => word.Id).ToHashSet();
    }

    private void BeginFocusMemory(StudyMode mode, int count)
    {
        var memory = SurfaceMemory(ref _focusMemory);
        var scope = FocusSurfaceScope();
        if (RestoreLearningSurface("focus", scope, memory)) return;
        memory.BeginSession(mode, _focusIeltsWords.Count>0?WordSource.Ielts:WordSource.Archive, "", count);
        _focusPresentation = _focusLastPresentation = null;
        _focusLastCommitted = false;
        PersistLearningSurface("focus");
    }

    private void PresentFocusMemory(string word, StudyStep step)
    {
        var sourceWord=_focusIeltsWords.GetValueOrDefault(word);
        var identity = sourceWord==null?WordKeyResolver.FormC(word):word;
        if (_focusPresentation is { } current && current.Identity == identity && current.Step == step) return;
        var archive = _allWords.FirstOrDefault(item => WordKeyResolver.FormC(item.Word) == identity);
        var key = sourceWord!=null?WordKeyResolver.FromIelts(sourceWord):archive == null ? WordKeyResolver.FromForm(word) : WordKeyResolver.FromArchive(archive);
        if(sourceWord!=null)((VocabularyService)_learningMemoryStore!).SaveSourceWordDetails(key.Key,sourceWord.Word,sourceWord.Phonetic??"",sourceWord.Meaning??"",sourceWord.Example??"");
        if (archive == null && WordKeyResolver.FormC(ResultWordText.Text ?? "") == identity)
            ((VocabularyService)_learningMemoryStore!).SaveSourceWordDetails(key.Key, word,
                ResultPhoneticText.Text ?? "", _focusOriginalTranslation, ResultDefinitionText.Text ?? "");
        var presented = new MemoryPresentation(key, Guid.NewGuid().ToString("N"), identity, step);
        SurfaceMemory(ref _focusMemory).OnPresented(key, presented.Id, step == StudyStep.Recall);
        _focusPresentation = presented;
        PersistLearningSurface("focus");
    }

    private void RateFocusMemory(StudyRating rating, int before, int after, bool completed, StudyMode mode)
    {
        var presented = _focusPresentation ?? throw new InvalidOperationException("专注卡没有对应的呈现记录。");
        var memory = SurfaceMemory(ref _focusMemory);
        _focusLastPresentation = presented;
        _focusLastCommitted = false;
        _focusPresentation = null;
        memory.OnRated(presented.Key, presented.Id, rating, before, after, null,
            CaptureSurfaceCheckpoint("focus", completed ? presented.Key : null, mode));
        try
        {
            if (completed)
            {
                _ = memory.CommitWord(presented.Key, mode)
                    ?? throw new InvalidOperationException("专注轨迹未能生成长期排期。");
                _focusLastCommitted = true;
                if (_focusRound?.IsFinished == true) NotifyMemoryRoundBoundary("focus-completed");
            }
        }
        catch { UndoFocusMemory(); throw; }
        _focusPresentation = null;
        PersistLearningSurface("focus");
    }

    private void UndoFocusMemory()
    {
        var presented = _focusLastPresentation ?? throw new InvalidOperationException("没有可撤销的专注轨迹。");
        var memory = SurfaceMemory(ref _focusMemory);
        memory.OnUndone(presented.Key, presented.Id, canonicalId: _focusLastCommitted
            ? CanonicalReview.BuildId(presented.Key.Key, memory.CurrentSessionId) : null);
        _focusPresentation = presented;
        _focusLastPresentation = null;
        _focusLastCommitted = false;
    }

    private void BeginPlanMemory(DailyStudyPlan plan, DailyStudyPlanSession session)
    {
        var source = plan.Source == DailyStudyPlanSource.Ielts ? WordSource.Ielts : WordSource.Archive;
        TryTrainContext("plan-start");
        var memory = SurfaceMemory(ref _planMemory);
        if (!RestoreLearningSurface("plan", plan.Id, memory))
        {
            memory.BeginSession(StudyMode.FirstLearn, source, plan.Id, session.Round.Total);
            _planPresentation = _planLastPresentation = null;
            _planLastCommitted = false;
        }
        session.OnRatingApplied = (_, rating, result) => RatePlanMemory(rating, result);
        session.OnWordCompleted = CommitPlanMemory;
        session.OnRatingFailed = _ => { if (_planRatingApplied) UndoPlanMemory(); };
        session.OnUndoApplied = () => UndoPlanMemory(persistProgress: true);
    }

    private void PresentPlanMemory(DailyStudyPlan plan, string wordId, StudyStep step)
    {
        if (_planPresentation is { } current && current.Identity == wordId && current.Step == step) return;
        var word = plan.Words.Single(w => w.Id == wordId);
        var key = WordKeyResolver.ResolvePlanWord(plan, word,
            id => _allWords.FirstOrDefault(item => item.Id.ToString() == id));
        var presented = new MemoryPresentation(key, Guid.NewGuid().ToString("N"), wordId, step);
        SurfaceMemory(ref _planMemory).OnPresented(key, presented.Id, step == StudyStep.Recall);
        _planPresentation = presented;
        PersistLearningSurface("plan");
    }

    private void RatePlanMemory(StudyRating rating, StudyCommitResult result)
    {
        var presented = _planPresentation ?? throw new InvalidOperationException("计划卡没有对应的呈现记录。");
        _planRatingApplied = true;
        _planLastPresentation = presented;
        _planLastCommitted = false;
        _planPresentation = null;
        SurfaceMemory(ref _planMemory).OnRated(presented.Key, presented.Id, rating, _planStreakBefore,
            result.Streak, null, CaptureSurfaceCheckpoint("plan", result.Completed ? presented.Key : null, StudyMode.FirstLearn));
    }

    private void CommitPlanMemory(string wordId)
    {
        var presented = _planLastPresentation;
        if (presented == null || presented.Identity != wordId)
            throw new InvalidOperationException("计划词与最后一次作答不一致。");
        var mutation = CrossStoreJournal.CreateJsonSnapshot("daily-plans.json",
            System.Text.Json.JsonSerializer.Serialize(_learningPlans));
        _ = SurfaceMemory(ref _planMemory).CommitWord(presented.Key, StudyMode.FirstLearn, outboxMutations: [mutation])
            ?? throw new InvalidOperationException("计划词尚无有效回忆，无法完成。");
        _planLastCommitted = true;
        PersistLearningSurface("plan");
        if (_dailyLearningSession?.Round.IsFinished == true) NotifyMemoryRoundBoundary("plan-completed");
    }

    private void UndoPlanMemory(bool persistProgress = false)
    {
        var presented = _planLastPresentation;
        if (presented == null) return;
        var memory = SurfaceMemory(ref _planMemory);
        var mutations = persistProgress ? new[] { CrossStoreJournal.CreateJsonSnapshot("daily-plans.json",
            System.Text.Json.JsonSerializer.Serialize(_learningPlans)) } : null;
        var previous = _planPresentation;
        var previousLast = _planLastPresentation;
        var committed = _planLastCommitted;
        _planPresentation = presented;
        _planLastPresentation = null;
        _planLastCommitted = false;
        try
        {
            memory.OnUndone(presented.Key, presented.Id, CaptureSurfaceCheckpoint("plan"),
                canonicalId: committed ? CanonicalReview.BuildId(presented.Key.Key, memory.CurrentSessionId) : null,
                outboxMutations: mutations);
        }
        catch { _planPresentation = previous; _planLastPresentation = previousLast; _planLastCommitted = committed; throw; }
        _planPresentation = presented;
        _planLastPresentation = null;
        _planLastCommitted = false;
        _planRatingApplied = false;
        if (persistProgress) ReplayMemoryJournal();
    }

    private bool ReplayMemoryJournal()
    {
        if (_memoryJournal == null) return true;
        var result = _memoryJournal.Replay(DateTime.UtcNow);
        if (result.Succeeded) return true;
        SetStatus("学习进度正在等待写回：" + result.Error);
        return false;
    }

    private void NotifyMemoryRoundBoundary(string trigger)
    {
        _memoryParameterRuntime?.NotifyRoundBoundary(trigger);
        TryTrainContext(trigger);
    }

    internal static bool ShouldTrainContext(DateTime nowUtc, DateTime? lastCompletedAtUtc,
        DateTime? lastFailedAtUtc, bool hasPendingTask, int evaluableSampleCount)
    {
        if (hasPendingTask || evaluableSampleCount < SchedulingConfig.ContextMinimumSamples) return false;
        var cooldown = TimeSpan.FromMinutes(SchedulingConfig.ContextTrainingCooldownMinutes);
        return !(lastCompletedAtUtc is { } completed && nowUtc - completed < cooldown)
            && !(lastFailedAtUtc is { } failed && nowUtc - failed < cooldown);
    }

    private async void TryTrainContext(string trigger)
    {
        var trainer = _memoryContextTrainer;
        var store = _learningMemoryStore;
        if (trainer == null || store == null) return;
        var now = DateTime.UtcNow;
        if (!ShouldTrainContext(now, _contextCompletedAtUtc, _contextFailedAtUtc,
                _contextTrainingRunning, SchedulingConfig.ContextMinimumSamples)) return;
        int count;
        try { count = store.LoadLabeledSamples().Count; }
        catch (Exception ex) { Console.Error.WriteLine("[memory] context samples: " + ex.Message); return; }
        if (!ShouldTrainContext(now, _contextCompletedAtUtc, _contextFailedAtUtc, _contextTrainingRunning, count)) return;
        _contextTrainingRunning = true;
        var lifetime = _memoryLifetime;
        try
        {
            await trainer.TrainAsync(now, lifetime.Token);
            if (ReferenceEquals(lifetime, _memoryLifetime)) _contextCompletedAtUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(lifetime, _memoryLifetime)) _contextFailedAtUtc = DateTime.UtcNow;
            Console.Error.WriteLine("[memory] " + trigger + " context training: " + ex.Message);
        }
        finally { if (ReferenceEquals(lifetime, _memoryLifetime)) _contextTrainingRunning = false; }
    }
}
