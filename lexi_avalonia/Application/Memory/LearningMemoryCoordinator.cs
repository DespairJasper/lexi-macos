using System.Text.Json;

namespace Lexi;

/// <summary>
/// 长期记忆协调器：UI 与持久化之间那一层。负责把界面上顺序发生的
/// 「呈现 → 作答 → 改判 → 撤销 → 定稿」翻译成 append-only 事件流、作答前上下文快照、
/// canonical 定稿载荷与 FSRS 排期决策，并保证「一个 word × session 只有一次定稿」。
/// </summary>
/// <remarks>
/// <para>
/// <b>写入节奏（两条不同的路径，不要混）</b>
/// <list type="number">
/// <item>
/// <b>事件与快照：即时落库。</b><see cref="OnPresented"/> / <see cref="OnRated"/> / <see cref="OnRevised"/> /
/// <see cref="OnUndone"/> 每次都立刻 <see cref="ILearningMemoryStore.AppendEvent"/>，作答前快照立刻
/// <see cref="ILearningMemoryStore.SaveContextSnapshot"/>。真实点击必须在崩溃/断电前尽量落到磁盘，
/// 因此这些路径**不等待定稿事务**。
/// </item>
/// <item>
/// <b>定稿：单事务。</b><see cref="CommitWord"/> 只把 summary + canonical + FSRS card + decision + 标签
/// 这些**派生**产物作为一个原子载荷交给 <see cref="ILearningMemoryStore.CommitWordSession"/>。
/// 因为 raw events 已经在第 1 步落库，载荷里的
/// <see cref="WordSessionCommit.PendingEvents"/> 恒为空列表——见 <see cref="CommitWord"/> 内注释。
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>幂等</b>：<see cref="CanonicalReview.BuildId"/> 是确定性的 <c>cr:&lt;wordKey&gt;:&lt;sessionId&gt;</c>，
/// 重复定稿由 DB 的 UNIQUE 约束吸收（返回 <c>AlreadyApplied = true</c>），
/// **不使用内存 flag 作为幂等机制**。重复调用会重算一次纯函数 FSRS 基线，但不会重复施加副作用。
/// </para>
/// <para>
/// <b>线程</b>：UI 线程与后台训练线程可能并发调用，全部可变状态与 store 写入都在同一把 <c>lock</c> 内；
/// 不使用 <c>async</c>/<c>Task</c>。
/// </para>
/// </remarks>
public sealed class LearningMemoryCoordinator
{
    /// <summary>超过这个作答时长视为「人离开了」，延迟不可靠，一律记 null。</summary>
    public const int MaxReliableResponseLatencyMs = 30 * 60 * 1000;

    /// <summary>无特征提供者时写入快照的 schema 版本。</summary>
    private const string DisabledFeatureSchemaVersion = SchedulingConfig.ContextFeatureSchemaVersion;

    /// <summary>一个 presentation 内的可变状态（作答前的呈现信息 + 仍生效的判断栈）。</summary>
    private sealed class PresentationState
    {
        public string WordKey = "";
        public bool IsRecall = true;
        public LearningMode Mode = LearningMode.Review;
        public int SessionAppearanceIndex;
        public int WordAppearanceIndex;
        public bool IsFirstAppearanceForWord;
        /// <summary>是否由 <see cref="OnPresented"/> 建立（只有建立过才由本层负责计时）。</summary>
        public bool LatencyTracked;
        /// <summary>作答前构建的特征向量（快照的原始入参，避免 JSON 往返丢失 Names）。</summary>
        public ContextFeatureVector? Features;
        /// <summary>仍然生效的作答事件 Id 栈；与 <see cref="TrajectoryReducer"/> 的口径一致（栈顶 = 最终有效判断）。</summary>
        public readonly List<string> StandingEventIds = [];
    }

    private readonly object _gate = new();
    private readonly ILearningMemoryStore _store;
    private readonly IMemoryScheduler _scheduler;
    private readonly IContextFeatureProvider? _contextFeatures;
    private readonly IContextCalibrator? _contextCalibrator;
    private readonly Func<DateTime> _clock;

    private readonly Dictionary<string, int> _sessionAppearanceCount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _wordAppearanceCount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastRecognitionCount = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionRatedPresentations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PresentationState> _presentations = new(StringComparer.Ordinal);

    private LearningSession? _session;

    // —— 作答延迟：当前 presentation 的累计有效时长 ——
    private bool _latencyPaused;
    private DateTime? _segmentStartUtc;
    private double _accumulatedMs;

    public LearningMemoryCoordinator(
        ILearningMemoryStore store,
        IMemoryScheduler scheduler,
        IContextFeatureProvider? contextFeatures = null,
        IContextCalibrator? contextCalibrator = null,
        Func<DateTime>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _contextFeatures = contextFeatures;
        _contextCalibrator = contextCalibrator;
        _clock = clock ?? (static () => DateTime.UtcNow);
    }

    /// <summary>当前会话 Id；尚未开始时为空串。</summary>
    public string CurrentSessionId
    {
        get { lock (_gate) { return _session?.SessionId ?? ""; } }
    }

    // ==================================================================================
    // 会话
    // ==================================================================================

    /// <summary>开始一个新的学习会话并持久化；返回新的 SessionId。</summary>
    public string BeginSession(StudyMode mode, WordSource primarySource, string planId, int plannedWordCount)
    {
        lock (_gate)
        {
            var startedAtUtc = UtcNow();
            var session = new LearningSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                StartedAtUtc = startedAtUtc,
                Mode = mode,
                PrimarySource = primarySource,
                PlannedWordCount = plannedWordCount,
                PlanId = planId ?? "",
            };
            _session = session;

            _sessionAppearanceCount.Clear();
            _sessionRatedPresentations.Clear();
            _lastRecognitionCount.Clear();
            _presentations.Clear();
            _accumulatedMs = 0;
            _segmentStartUtc = _latencyPaused ? null : startedAtUtc;

            _store.UpsertSession(session);
            return session.SessionId;
        }
    }

    /// <summary>Resume real persisted events without writing a new presentation or rating.</summary>
    public bool ResumeSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            var session = _store.GetSession(sessionId);
            if (session is null || session.EndedAtUtc is not null) return false;
            var events = _store.LoadEvents(sessionId).OrderBy(e => e.OccurredAtUtc).ToList();
            var restored = new Dictionary<string, PresentationState>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var recognition = new Dictionary<string, int>(StringComparer.Ordinal);
            var rated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in events)
            {
                if (!string.Equals(e.SessionId, sessionId, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(e.PresentationId))
                    throw new InvalidDataException("恢复事件不属于目标会话。");
                if (!restored.TryGetValue(e.PresentationId, out var state))
                {
                    state = new PresentationState { WordKey = e.WordKey, IsRecall = e.IsRecall,
                        Mode = e.LearningMode, SessionAppearanceIndex = e.SessionAppearanceIndex,
                        WordAppearanceIndex = e.WordAppearanceIndex,
                        IsFirstAppearanceForWord = e.IsFirstAppearanceForWord,
                        LatencyTracked = false };
                    restored.Add(e.PresentationId, state);
                }
                if (state.WordKey != e.WordKey) throw new InvalidDataException("恢复呈现标识跨词重复。");
                if (e.Kind == InteractionEventKind.Presented)
                    counts[e.WordKey] = Math.Max(counts.GetValueOrDefault(e.WordKey), e.SessionAppearanceIndex);
                if (e.Kind is InteractionEventKind.Rated or InteractionEventKind.Revised && e.Response is not null)
                {
                    state.StandingEventIds.Add(e.EventId);
                    if (e.Kind == InteractionEventKind.Rated) rated.Add(e.PresentationId);
                    recognition[e.WordKey] = e.RecognitionCountAfter;
                }
                else if (e.Kind == InteractionEventKind.Undone)
                {
                    var index = string.IsNullOrEmpty(e.SupersedesEventId)
                        ? -1 : state.StandingEventIds.LastIndexOf(e.SupersedesEventId);
                    if (index >= 0) state.StandingEventIds.RemoveAt(index);
                    else if (state.StandingEventIds.Count > 0) state.StandingEventIds.RemoveAt(state.StandingEventIds.Count - 1);
                    recognition[e.WordKey] = e.RecognitionCountAfter;
                }
            }
            _session = session;
            _presentations.Clear(); foreach (var pair in restored) _presentations.Add(pair.Key, pair.Value);
            _sessionAppearanceCount.Clear(); foreach (var pair in counts) _sessionAppearanceCount.Add(pair.Key, pair.Value);
            _lastRecognitionCount.Clear(); foreach (var pair in recognition) _lastRecognitionCount.Add(pair.Key, pair.Value);
            _sessionRatedPresentations.Clear(); foreach (var id in rated) _sessionRatedPresentations.Add(id);
            // Whole-history counts are loaded lazily on the next real presentation; downtime is never latency.
            _wordAppearanceCount.Clear(); _accumulatedMs = 0; _segmentStartUtc = null;
            return true;
        }
    }

    // ==================================================================================
    // 作答延迟的暂停 / 恢复
    // ==================================================================================

    /// <summary>暂停作答延迟计时（睡眠、切后台、失焦）。暂停期间流逝的时间不计入。</summary>
    public void PauseLatency()
    {
        lock (_gate)
        {
            if (_latencyPaused) return;
            var now = UtcNow();
            if (_segmentStartUtc is { } start)
                _accumulatedMs += Math.Max(0.0, (now - start).TotalMilliseconds);
            _segmentStartUtc = null;
            _latencyPaused = true;
        }
    }

    /// <summary>恢复作答延迟计时。</summary>
    public void ResumeLatency()
    {
        lock (_gate)
        {
            if (!_latencyPaused) return;
            _latencyPaused = false;
            _segmentStartUtc = UtcNow();
        }
    }

    // ==================================================================================
    // 事件追加（即时落库）
    // ==================================================================================

    /// <summary>卡片呈现（作答前）。<paramref name="isRecall"/> = false 表示 Learn 卡只看答案，不构成 retrieval。</summary>
    public void OnPresented(WordKey key, string presentationId, bool isRecall)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        lock (_gate)
        {
            var session = RequireSession();
            var wordKey = key.Key;
            var now = UtcNow();

            // 计数先**算**出来放进 state，但不要在这里写回缓存：
            // 事件写库可能失败，若先把缓存前移，失败的这一次就被永久记进内存，
            // 之后每一条事件的 WordAppearanceIndex / SessionAppearanceIndex 都会偏大（评审 P1-9）。
            var sessionIndex = _sessionAppearanceCount.TryGetValue(wordKey, out var seen) ? seen + 1 : 1;

            // 全历史呈现次数：懒加载一次后按 wordKey 缓存，session 内继续 +1，不做全表扫描。
            var historical = _wordAppearanceCount.TryGetValue(wordKey, out var cached) ? cached : CountPresentedInHistory(wordKey);
            var wordIndex = historical + 1;

            // 与 StudyRound 的 isFirstLearn 口径一致：全历史第 1 次出现即首次学习。
            var mode = wordIndex == 1 ? LearningMode.FirstLearn : MapMode(session.Mode);
            var recognition = _lastRecognitionCount.TryGetValue(wordKey, out var last) ? last : 0;

            var state = new PresentationState
            {
                WordKey = wordKey,
                IsRecall = isRecall,
                Mode = mode,
                SessionAppearanceIndex = sessionIndex,
                WordAppearanceIndex = wordIndex,
                IsFirstAppearanceForWord = wordIndex == 1,
                LatencyTracked = true,
            };

            // 写库成功之后才提交内存状态（presentation 登记 + 两个计数）。失败时全部保持原样。
            _store.AppendEvent(BuildEvent(state, session, presentationId, now,
                InteractionEventKind.Presented, response: null, previousResponse: null, supersedes: null,
                recognition, recognition, latencyMs: null));

            _presentations[presentationId] = state;
            _sessionAppearanceCount[wordKey] = sessionIndex;
            _wordAppearanceCount[wordKey] = wordIndex;

            // 作答延迟从「呈现时刻」开始累计。
            _accumulatedMs = 0;
            _segmentStartUtc = _latencyPaused ? null : now;

            // 快照必须在**作答前**生成：它只能包含呈现时已知的信息。
            if (isRecall) CaptureSnapshot(state, session, presentationId, now);
        }
    }

    /// <summary>一次作答。<paramref name="recognitionBefore"/>/<paramref name="recognitionAfter"/> 来自 StudyRound 的连击计数。</summary>
    public void OnRated(WordKey key, string presentationId, StudyRating rating,
        int recognitionBefore, int recognitionAfter, int? latencyMs, MemorySessionCheckpoint? checkpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        lock (_gate)
        {
            var session = RequireSession();
            var wordKey = key.Key;
            var now = UtcNow();
            var state = Presentation(presentationId, wordKey, session);
            var latency = ResolveLatency(state, latencyMs, now);

            var e = BuildEvent(state, session, presentationId, now, InteractionEventKind.Rated,
                rating, previousResponse: null, supersedes: null, recognitionBefore, recognitionAfter, latency);
            if (checkpoint is null) _store.AppendEvent(e);
            else _store.AppendEventWithCheckpoint(e, checkpoint);
            state.StandingEventIds.Add(e.EventId);

            _lastRecognitionCount[wordKey] = recognitionAfter;
            _sessionRatedPresentations.Add(presentationId);
        }
    }

    /// <summary>改判上一次作答（同一 presentation）。</summary>
    public void OnRevised(WordKey key, string presentationId, StudyRating from, StudyRating to, int? latencyMs, MemorySessionCheckpoint? checkpoint = null,
        string? canonicalId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        lock (_gate)
        {
            var session = RequireSession();
            var wordKey = key.Key;
            var now = UtcNow();
            var state = Presentation(presentationId, wordKey, session);
            var recognition = _lastRecognitionCount.TryGetValue(wordKey, out var last) ? last : 0;
            var supersedes = state.StandingEventIds.Count > 0 ? state.StandingEventIds[^1] : null;

            var e = BuildEvent(state, session, presentationId, now, InteractionEventKind.Revised,
                to, from, supersedes, recognition, recognition, ResolveLatency(state, latencyMs, now));
            if (canonicalId is not null)
            {
                if (!_store.InvalidateCanonicalWithEvent(canonicalId, now, "revision", e, checkpoint))
                    throw new InvalidOperationException("长期复习记录已改变，不能修正此评分。");
            }
            else if (checkpoint is null) _store.AppendEvent(e);
            else _store.AppendEventWithCheckpoint(e, checkpoint);
            state.StandingEventIds.Add(e.EventId);
        }
    }

    /// <summary>撤销上一次作答（同一 presentation）。只追加事件，绝不删除任何历史。</summary>
    public void OnUndone(WordKey key, string presentationId, MemorySessionCheckpoint? checkpoint = null,
        string? canonicalId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        lock (_gate)
        {
            var session = RequireSession();
            var wordKey = key.Key;
            var now = UtcNow();
            var state = Presentation(presentationId, wordKey, session);
            var recognition = _lastRecognitionCount.TryGetValue(wordKey, out var last) ? last : 0;
            var supersedes = state.StandingEventIds.Count > 0 ? state.StandingEventIds[^1] : null;

            var e = BuildEvent(state, session, presentationId, now, InteractionEventKind.Undone,
                response: null, previousResponse: null, supersedes, recognition, recognition, latencyMs: null);
            if (canonicalId is not null)
            {
                if (!_store.InvalidateCanonicalWithEvent(canonicalId, now, "undo", e, checkpoint))
                    throw new InvalidOperationException("长期复习记录已改变，不能撤销此评分。");
            }
            else if (checkpoint is null) _store.AppendEvent(e);
            else _store.AppendEventWithCheckpoint(e, checkpoint);
            if (state.StandingEventIds.Count > 0) state.StandingEventIds.RemoveAt(state.StandingEventIds.Count - 1);
        }
    }

    // ==================================================================================
    // 定稿
    // ==================================================================================

    /// <summary>
    /// 单词本轮定稿：构建 summary + canonical + FSRS + 决策，**单事务**提交。返回是否真的应用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么 <see cref="WordSessionCommit.PendingEvents"/> 是空列表</b>：
    /// 本协调器在 <see cref="OnPresented"/> / <see cref="OnRated"/> / <see cref="OnRevised"/> / <see cref="OnUndone"/>
    /// 里已经逐条 <see cref="ILearningMemoryStore.AppendEvent"/> 落库（真实点击要尽快存盘，不能等到定稿）。
    /// 因此定稿事务**不承载** raw events，它只负责派生部分的原子性：
    /// summary / canonical / fsrs_cards / scheduler_decisions / 快照标签。
    /// 同理 <see cref="WordSessionCommit.SnapshotsToSave"/> 也为空（快照在呈现时即时写入），
    /// <see cref="WordSessionCommit.OutboxMutations"/> 也为空（本层不产生 JSON 侧变更；
    /// 计划 / IELTS 的协调由 UI 工作流在其调用点补 <see cref="PendingMutation"/> 并走
    /// <see cref="ILearningMemoryStore.EnqueueMutations"/>）。
    /// </para>
    /// <para>
    /// 幂等由 <see cref="CanonicalReview.CanonicalId"/>（= <c>cr:wordKey:sessionId</c>）在 store 侧保证：
    /// 第二次调用会拿到 <c>AlreadyApplied = true</c> 且不重复施加 FSRS 副作用。
    /// store 抛出的异常**原样向上抛**，绝不吞。
    /// </para>
    /// </remarks>
    public WordSessionCommitResult? CommitWord(WordKey key, StudyMode mode,
        string aggregationPolicyVersion = AggregationPolicy.Version,
        IReadOnlyList<PendingMutation>? outboxMutations = null,
        MemorySessionCheckpoint? checkpoint = null)
    {
        lock (_gate)
        {
            var session = RequireSession();
            var wordKey = key.Key;

            var events = _store.LoadEvents(session.SessionId)
                .Where(e => e is not null && string.Equals(e.WordKey, wordKey, StringComparison.Ordinal))
                .ToList();
            if (events.Count == 0) return null; // 本轮这个词没有任何轨迹 → 不写任何东西。

            var presentations = TrajectoryReducer.ProjectPresentations(events);
            var now = UtcNow();
            var summary = TrajectoryReducer.BuildSummary(wordKey, session.SessionId, presentations, now);

            // canonical 来源口径**取调用方给的模式**：调用方（StudyRound 侧）才知道该词本轮是 Learn 还是 Review。
            // 这里不按 WordAppearanceIndex 二次推断——否则「只有 Learn 卡、没有任何真实 retrieval」的轨迹
            // 会被当成 FirstLearn 聚合出一个凭空 Known 的 canonical，违反「Learn 卡不构成 retrieval」。
            var learningMode = mode == StudyMode.FirstLearn ? LearningMode.FirstLearn : LearningMode.Review;
            var canonical = TrajectoryReducer.ResolveCanonical(wordKey, session.SessionId, learningMode, presentations, summary, now);
            if (canonical is null) return null; // 没有任何有效真实 retrieval → 零写入。
            if (!string.IsNullOrWhiteSpace(aggregationPolicyVersion))
                canonical.AggregationPolicyVersion = aggregationPolicyVersion;

            // —— 同日跨 session：**不**做任何「每词每日一条」的折叠（裁定 O-2）——
            // 唯一性约束只在 word×session 层面（store 的 ux_canonical_active 保证重复定稿幂等）；
            // 同一个本地日、不同 session 各产生一条有效 canonical，各自更新一次 FSRS。
            // 第二条因 elapsed_days == 0 自动走 Fsrs6Model 的 short-term 稳定性公式
            // （Fsrs6Model.Review：elapsed is null || elapsed < 1）——这条路径正是官方 golden 里
            // 刻意混入的「同日子日间隔」场景覆盖的那条（691 次相邻复习中有 434 次间隔 < 24h，
            // 见 tests/LearningTests/Fixtures/PROVENANCE.md）；把它折叠掉等于绕过已被验证的路径，
            // 并会把当天真实发生过的 Again 静默抹掉。详见 .planning/upgrade-3.1.1/findings.md §Q。

            // —— FSRS：先把 pre-state 固定下来，并给 scheduler 一份克隆，防止实现方就地改写 store 的对象 ——
            var cardBefore = _store.GetCard(wordKey);
            var cardPreState = FsrsPreState.From(cardBefore);
            var outcome = _scheduler.Review(cardBefore?.Clone(), canonical.Rating, canonical.ReviewedAtUtc)
                ?? throw new InvalidOperationException("IMemoryScheduler.Review 返回了 null。");
            var schedulerCard = outcome.Card
                ?? throw new InvalidOperationException("FsrsOutcome.Card 为 null。");

            // —— Context 校准：固定使用**第一次真实 retrieval** 那一刻的历史特征 ——
            var anchor = presentations
                .Where(static p => p is not null && p.IsRecall && p.FinalValidatedResponse is not null)
                .OrderBy(static p => p.PresentedAtUtc)
                .FirstOrDefault();
            var anchorSnapshot = anchor is null ? null : _store.GetSnapshotByPresentation(anchor.PresentationId);

            var adjustment = ContextAdjustment.None(ContextMode.Disabled);
            double? candidate = null;
            if (_contextCalibrator is { } calibrator)
            {
                adjustment = ContextAdjustment.None(calibrator.Mode);
                if (anchorSnapshot is not null)
                {
                    // 优先用呈现时缓存的那份向量（无损）；应用重启后回退到快照 JSON。
                    var features = anchor is not null && _presentations.TryGetValue(anchor.PresentationId, out var ps)
                        ? ps.Features
                        : null;
                    features ??= DeserializeFeatures(anchorSnapshot) ?? EmptyVector;
                    adjustment = calibrator.Adjust(features, anchorSnapshot.FsrsRetrievabilityAtCapture);
                    if (adjustment.Applied)
                        candidate = calibrator.SolveCandidate(features, schedulerCard.Stability, SchedulingConfig.DesiredRetention);
                }
            }

            // —— 最终间隔 ——
            var finalInterval = MemoryScheduler.ResolveFinalInterval(outcome.BaselineIntervalDays, candidate, adjustment);
            // —— P1-3：同日保护按**本地日界**算，日界由本层（允许读时钟）算好后传给纯函数 ——
            finalInterval = MemoryScheduler.ApplyMinimumInterval(
                finalInterval, canonical.Rating, canonical.ReviewedAtUtc, ResolveLocalDayEnd(canonical.ReviewedAtUtc, now));
            var dueAtUtc = DateTime.SpecifyKind(canonical.ReviewedAtUtc.AddDays(finalInterval), DateTimeKind.Utc);

            var cardAfter = schedulerCard.Clone();
            cardAfter.WordKey = wordKey;
            cardAfter.NextReviewAtUtc = dueAtUtc;
            cardAfter.LastReviewAtUtc = canonical.ReviewedAtUtc;
            cardAfter.LastCanonicalRating = canonical.Rating;
            // 版本戳：以 scheduler 写进卡上的为准（Fsrs6Scheduler 会盖）；调度器没盖时回退到
            // SchedulingConfig 的冻结常量，保证 SchedulerDecision 的审计列永远不是空串。
            if (string.IsNullOrEmpty(cardAfter.FsrsAlgorithmVersion))
                cardAfter.FsrsAlgorithmVersion = SchedulingConfig.AlgorithmVersion;
            if (string.IsNullOrEmpty(cardAfter.FsrsLibraryVersion))
                cardAfter.FsrsLibraryVersion = SchedulingConfig.LibraryVersion;
            if (string.IsNullOrEmpty(cardAfter.FsrsParameterVersion))
                cardAfter.FsrsParameterVersion = SchedulingConfig.ParameterVersion;

            var decision = MemoryScheduler.BuildDecision(
                decisionId: Guid.NewGuid().ToString("N"),
                wordKey: wordKey,
                canonicalId: canonical.CanonicalId,
                timestampUtc: now,
                cardBefore: cardBefore ?? new FsrsCardState { WordKey = wordKey },
                baselineIntervalDays: MemoryScheduler.Sanitize(outcome.BaselineIntervalDays, SchedulingConfig.MinimumIntervalDays),
                baselineRetrievability: outcome.BaselineRetrievability,
                adjustment: adjustment,
                contextCandidateDays: candidate,
                finalIntervalDays: finalInterval,
                finalDueAtUtc: dueAtUtc,
                fsrsAlgorithmVersion: cardAfter.FsrsAlgorithmVersion,
                fsrsLibraryVersion: cardAfter.FsrsLibraryVersion,
                fsrsParameterVersion: cardAfter.FsrsParameterVersion,
                contextModelVersion: anchorSnapshot?.ModelVersion ?? "");

            IReadOnlyList<ContextLabel> labels = anchorSnapshot is null
                ? []
                : [new ContextLabel(anchorSnapshot.SnapshotId, canonical.Rating, canonical.Rating == StudyRating.Known)];

            var commit = new WordSessionCommit(
                Session: session,
                Summary: summary,
                Canonical: canonical,
                CardPreState: cardPreState,
                CardAfter: cardAfter,
                Decision: decision,
                PendingEvents: [],
                SnapshotsToSave: [],
                SnapshotLabels: labels,
                OutboxMutations: outboxMutations ?? [],
                Checkpoint: checkpoint);

            // 异常原样抛出：调用方据此判断「是否显示完成」，绝不在这里吞。
            return _store.CommitWordSession(commit);
        }
    }

    /// <summary>撤销已定稿的 canonical：同一 logical canonical 失效 + pre-state 回放。不删任何事件。</summary>
    public bool UndoCommit(string canonicalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalId);
        lock (_gate) return _store.InvalidateCanonical(canonicalId, UtcNow(), "user-undo");
    }

    // ==================================================================================
    // 到期队列透传
    // ==================================================================================

    /// <summary>到期队列（供 UI 替换旧的 GetPendingReviewWords）。</summary>
    public IReadOnlyList<DueCard> QueryDue(DateTime nowUtc, int limit)
    {
        lock (_gate) return _store.QueryDue(nowUtc, limit);
    }

    /// <summary>到期数量。</summary>
    public int CountDue(DateTime nowUtc)
    {
        lock (_gate) return _store.CountDue(nowUtc);
    }

    // ==================================================================================
    // 内部
    // ==================================================================================

    private LearningSession RequireSession() =>
        _session ?? throw new InvalidOperationException("尚未开始学习会话：请先调用 BeginSession。");

    private PresentationState Presentation(string presentationId, string wordKey, LearningSession session)
    {
        if (_presentations.TryGetValue(presentationId, out var existing))
        {
            if (!string.Equals(existing.WordKey, wordKey, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"presentationId「{presentationId}」已归属于 {existing.WordKey}，不能再用于 {wordKey}。");
            return existing;
        }
        // 调用方跳过了 OnPresented（例如恢复中的界面）：尽力补一个状态，不让事件丢失。
        var wordIndex = _wordAppearanceCount.TryGetValue(wordKey, out var cached) ? cached : 0;
        var recovery = new PresentationState
        {
            WordKey = wordKey,
            IsRecall = true,
            Mode = MapMode(session.Mode),
            SessionAppearanceIndex = _sessionAppearanceCount.TryGetValue(wordKey, out var seen) ? seen : 0,
            WordAppearanceIndex = wordIndex,
            IsFirstAppearanceForWord = wordIndex == 1,
            LatencyTracked = false,
        };
        _presentations[presentationId] = recovery;
        return recovery;
    }

    private int CountPresentedInHistory(string wordKey)
    {
        var events = _store.LoadEventsForWord(wordKey);
        var count = 0;
        foreach (var e in events)
            if (e is not null && e.Kind == InteractionEventKind.Presented) count++;
        return count;
    }

    private LearningInteractionEvent BuildEvent(PresentationState state, LearningSession session,
        string presentationId, DateTime now, InteractionEventKind kind,
        StudyRating? response, StudyRating? previousResponse, string? supersedes,
        int recognitionBefore, int recognitionAfter, int? latencyMs, MemorySessionCheckpoint? checkpoint = null) => new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            SessionId = session.SessionId,
            WordKey = state.WordKey,
            PresentationId = presentationId,
            OccurredAtUtc = now,
            LearningMode = state.Mode,
            Kind = kind,
            Response = response,
            PreviousResponse = previousResponse,
            SessionAppearanceIndex = state.SessionAppearanceIndex,
            WordAppearanceIndex = state.WordAppearanceIndex,
            RecognitionCountBefore = recognitionBefore,
            RecognitionCountAfter = recognitionAfter,
            IsFirstAppearanceForWord = state.IsFirstAppearanceForWord,
            ResponseLatencyMs = latencyMs,
            SupersedesEventId = supersedes,
            IsRecall = state.IsRecall,
        };

    /// <summary>
    /// 解析本次作答延迟。**本层计时的结果优先**（<see cref="PauseLatency"/> 的存在就是为了让本层拥有这个测量）；
    /// 只有在没有为该 presentation 建立过计时（调用方跳过了 <see cref="OnPresented"/>）时才回退到调用方给的
    /// <paramref name="callerLatencyMs"/>。累计时长 ≤ 0 或 &gt; 30 分钟 → null（不可靠就不记）。
    /// </summary>
    private int? ResolveLatency(PresentationState state, int? callerLatencyMs, DateTime now)
    {
        int? candidate;
        if (state.LatencyTracked)
        {
            var ms = _accumulatedMs + (_segmentStartUtc is { } start ? Math.Max(0.0, (now - start).TotalMilliseconds) : 0.0);
            candidate = (int)Math.Round(ms, MidpointRounding.AwayFromZero);
        }
        else
        {
            candidate = callerLatencyMs;
        }
        if (candidate is not { } value) return null;
        return value <= 0 || value > MaxReliableResponseLatencyMs ? null : value;
    }

    /// <summary>作答前快照：只用呈现时已知的信息，绝不含本次作答结果。</summary>
    private void CaptureSnapshot(PresentationState state, LearningSession session, string presentationId, DateTime now)
    {
        var card = _store.GetCard(state.WordKey);
        double difficulty = 0, stability = 0, retrievability = 1.0;
        if (card is not null)
        {
            difficulty = card.Difficulty;
            stability = card.Stability;
            // 无卡、无历史复习时刻、或 stability 不可用 → R = 1.0。
            if (card.LastReviewAtUtc is { } last && stability > 0 && MemoryScheduler.IsFinite(stability))
            {
                var elapsedDays = Math.Max(0.0, (now.Ticks - last.Ticks) / (double)TimeSpan.TicksPerDay);
                var wholeDays = Math.Floor(elapsedDays); // 整天截断
                var r = _scheduler.Retrievability(stability, wholeDays);
                if (MemoryScheduler.IsFinite(r)) retrievability = Math.Clamp(r, 0.0, 1.0);
            }
        }

        double[] values = [];
        string[] names = [];
        bool[] missing = [];
        var schemaVersion = DisabledFeatureSchemaVersion;
        var modelVersion = "";

        if (_contextFeatures is { } provider)
        {
            var request = new ContextFeatureRequest(
                WordKey: state.WordKey,
                SessionId: session.SessionId,
                PresentationId: presentationId,
                CapturedAtUtc: now,
                Mode: state.Mode,
                IsFirstAppearanceForSession: state.SessionAppearanceIndex == 1,
                SessionPosition: state.SessionAppearanceIndex,
                SessionReviewedCount: _sessionRatedPresentations.Count,
                Card: card?.Clone());
            var vector = provider.Build(request)
                ?? throw new InvalidOperationException($"IContextFeatureProvider（{provider.GetType().Name}）返回了 null 向量。");
            if (vector.Values is null || vector.Names is null || vector.Missing is null)
                throw new InvalidOperationException($"IContextFeatureProvider（{provider.GetType().Name}）返回了带 null 数组的向量。");
            if (vector.Values.Length != vector.Names.Length || vector.Values.Length != vector.Missing.Length)
                throw new InvalidOperationException(
                    $"ContextFeatureVector 三个数组长度不一致（Values={vector.Values.Length}, " +
                    $"Names={vector.Names.Length}, Missing={vector.Missing.Length}），provider={provider.GetType().Name}。");

            values = (double[])vector.Values.Clone();
            names = vector.Names;
            missing = (bool[])vector.Missing.Clone();
            // 非有限值无法进入 JSON（会抛）且对模型无意义：按缺失处理（值置 0，Missing 置 true）。
            for (var i = 0; i < values.Length; i++)
            {
                if (MemoryScheduler.IsFinite(values[i])) continue;
                values[i] = 0.0;
                missing[i] = true;
            }
            schemaVersion = provider.FeatureSchemaVersion;
            modelVersion = provider.ModelVersion;
        }

        state.Features = new ContextFeatureVector(values, names, missing);

        _store.SaveContextSnapshot(new ContextFeatureSnapshot
        {
            SnapshotId = Guid.NewGuid().ToString("N"),
            WordKey = state.WordKey,
            SessionId = session.SessionId,
            PresentationId = presentationId,
            CapturedAtUtc = now,
            FsrsDifficultyAtCapture = difficulty,
            FsrsStabilityAtCapture = stability,
            FsrsRetrievabilityAtCapture = retrievability,
            FeatureSchemaVersion = schemaVersion,
            ModelVersion = modelVersion,
            FeaturesJson = JsonSerializer.Serialize(values),
            MissingFlagsJson = JsonSerializer.Serialize(missing),
        });
    }

    private static readonly ContextFeatureVector EmptyVector = new([], [], []);

    /// <summary>应用重启后从快照 JSON 回读特征（Names 不持久化，因此导出为空名数组）。</summary>
    private static ContextFeatureVector? DeserializeFeatures(ContextFeatureSnapshot snapshot)
    {
        try
        {
            var values = JsonSerializer.Deserialize<double[]>(snapshot.FeaturesJson) ?? [];
            var missing = JsonSerializer.Deserialize<bool[]>(snapshot.MissingFlagsJson) ?? [];
            if (missing.Length != values.Length) missing = new bool[values.Length];
            return new ContextFeatureVector(values, new string[values.Length], missing);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 该次作答所在**本地日历日的右端点**（= 下一个本地零点）对应的 UTC 时刻。
    /// 只有当作答与「此刻」落在同一本地日历日时才做同日保护（跨日的迟到定稿不适用）。
    /// </summary>
    /// <remarks>
    /// 本层不是纯函数（时钟被约定为「已经返回 UTC」，本地时区取本机设置），
    /// 因此时区推断只允许出现在这里，<see cref="MemoryScheduler"/> 必须保持纯净。
    /// 「下一个本地零点」用本地墙钟表示后再换回 UTC：夏令时日一天不是 24 小时，
    /// 不能用「UTC 时刻 + 1 天」代替（评审 P1-3）。
    /// </remarks>
    private static DateTime? ResolveLocalDayEnd(DateTime reviewedAtUtc, DateTime nowUtc)
    {
        var reviewedLocal = reviewedAtUtc.ToLocalTime();
        if (reviewedLocal.Date != nowUtc.ToLocalTime().Date) return null;
        var nextLocalMidnight = reviewedLocal.Date.AddDays(1);
        return DateTime.SpecifyKind(nextLocalMidnight, DateTimeKind.Local).ToUniversalTime();
    }

    private static LearningMode MapMode(StudyMode mode) =>
        mode == StudyMode.FirstLearn ? LearningMode.FirstLearn : LearningMode.Review;

    /// <summary>时钟被约定为「已经返回 UTC」，因此只标记 Kind，不做时区换算（避免测试注入的假时钟被平移）。</summary>
    private DateTime UtcNow() => DateTime.SpecifyKind(_clock(), DateTimeKind.Utc);
}
