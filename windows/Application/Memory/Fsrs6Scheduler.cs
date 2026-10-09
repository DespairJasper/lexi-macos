namespace Lexi;

/// <summary>
/// 生产 FSRS-6 排期器（纯 C#，无外部依赖、无原生库）。
///
/// <para><b>生产配置：learning steps / relearning steps 为空</b>（<see cref="SchedulingConfig.DefaultLearningSteps"/>）。
/// 本项目的轮内强化由 <c>StudyRound</c> 的连击机制负责，长期层不接管（用户需求 §6）：
/// 每次 canonical 定稿只产生一个 ≥ 1 天的长期间隔，不产生 min 级学习步。</para>
///
/// <para><b>纯函数约定</b>：<see cref="Review"/> 不读时钟、不写库、不修改入参 <c>card</c>，
/// 只依赖传入的 <paramref name="reviewedAtUtc"/>。注入的时钟只通过 <see cref="UtcNow"/> /
/// <see cref="ReviewAtNow"/> 暴露给需要在「此刻」决策的调用方。</para>
///
/// <para><b>失败模式</b>：任何非有限或越界的中间值/输出一律抛 <see cref="InvalidOperationException"/>，
/// 绝不静默返回垃圾值——调用方据此回退到纯官方默认参数。</para>
/// </summary>
public sealed class Fsrs6Scheduler : IMemoryScheduler
{
    private readonly Func<DateTime> _clock;
    private readonly SchedulerWeights? _holder;
    private readonly Fsrs6Weights? _fixed;

    /// <summary>
    /// 构造。默认使用官方 21 个默认权重与 <see cref="DateTime.UtcNow"/>；
    /// 测试应注入固定权重与固定时钟。
    /// </summary>
    public Fsrs6Scheduler(Fsrs6Weights? weights = null, Func<DateTime>? clock = null)
    {
        _fixed = weights ?? Fsrs6Weights.Defaults;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// 共享持有者构造：<b>生产装配必须走这一条</b>。
    /// 协调器（保持期排期）、<c>ContextFeatureProvider</c>（算快照 R）与 <c>ContextCalibrator</c>
    /// （解候选间隔）三处**共用同一个 <see cref="SchedulerWeights"/> 实例**，因此一次换版对三者同时可见；
    /// 若各持一份权重，换版后会出现"排期用新权重、快照 R 用旧权重"的静默不一致。
    /// </summary>
    public Fsrs6Scheduler(SchedulerWeights holder, Func<DateTime>? clock = null)
    {
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>本实例使用的权重（含 source / 版本信息）。挂持有者时每次读取都取当前版本。</summary>
    public Fsrs6Weights Weights => _holder?.Current ?? _fixed!;

    /// <summary>注入时钟的当前 UTC 时刻。仅用于 <see cref="ReviewAtNow"/> 等便捷入口。</summary>
    public DateTime UtcNow => NormalizeUtc(_clock());

    /// <summary>用注入时钟的当前时刻执行一次 <see cref="Review"/>（已有 canonical 时刻时应直接调用 Review）。</summary>
    public FsrsOutcome ReviewAtNow(FsrsCardState? card, StudyRating rating) => Review(card, rating, UtcNow);

    /// <inheritdoc />
    public FsrsOutcome Review(FsrsCardState? card, StudyRating rating, DateTime reviewedAtUtc)
    {
        var at = NormalizeUtc(reviewedAtUtc);
        var fsrsRating = Fsrs6Model.ToFsrsRating(rating);
        var weights = Weights.Weights;
        var decay = Fsrs6Model.DecayOf(weights);
        var factor = Fsrs6Model.FactorOf(weights);

        var isNew = card is null || card.State == FsrsState.New;

        double stability;
        double difficulty;
        double baselineRetrievability;
        long lapses;
        long elapsedDays = 0;

        if (isNew)
        {
            // 首次复习：S = S0(G) = w[G−1]（audit §3.2 ③），D = D0(G)（audit §3.2 ④，对外值已 clamp）。
            stability = Fsrs6Model.ClampStability(weights[fsrsRating - 1]);
            difficulty = Fsrs6Model.InitialDifficulty(weights, fsrsRating);
            lapses = rating == StudyRating.Forgot ? 1 : 0;

            // 此时没有「上一次复习」，R(t) 无定义（不参与任何公式）。约定返回 1.0：
            // 与 R(S,0)=1 一致、保持 [0,1] 值域、且让审计记录里的 BaselineRetrievability 可读；
            // 注意 py-fsrs 的 get_card_retrievability 在 last_review 为 null 时返回 0——两者都不进入公式，
            // 本实现选择 1.0 以避免把「无信息」误读成「完全遗忘」。
            baselineRetrievability = 1.0;
        }
        else
        {
            ValidateExistingCard(card!);
            var previousStability = card!.Stability;
            var previousDifficulty = card.Difficulty;
            var lastReview = card.LastReviewAtUtc is { } last ? NormalizeUtc(last) : at;

            // audit §10 硬性要求 2 / §3.2 ⑨：整天截断，整数 ticks 相减。
            elapsedDays = Fsrs6Model.ElapsedWholeDays(lastReview, at);
            var r = Fsrs6Model.Retrievability(previousStability, elapsedDays, decay, factor);
            baselineRetrievability = r;

            if (elapsedDays == 0)
            {
                // 同日复习（audit §3.2 ⑧⑨）：短期稳定性公式，Hard 也享受 max(·,1.0) 下限。
                stability = Fsrs6Model.ShortTermStability(weights, previousStability, fsrsRating);
            }
            else if (fsrsRating == Fsrs6Model.RatingAgain)
            {
                stability = Fsrs6Model.NextStabilityFailure(weights, previousDifficulty, previousStability, r);
            }
            else
            {
                // Hidden/Unsure(Hard) 与 Known(Good) 走同一个成功公式，区别只在 rating 常量（w15 Hard 惩罚）。
                stability = Fsrs6Model.NextStabilitySuccess(weights, previousDifficulty, previousStability, r, fsrsRating);
            }
            stability = Fsrs6Model.ClampStability(stability);
            difficulty = Fsrs6Model.NextDifficulty(weights, previousDifficulty, fsrsRating);
            lapses = card.Lapses + (rating == StudyRating.Forgot ? 1 : 0);
        }

        // 基线间隔：R 反解 → 既有纯函数收口（Sanitize + 相对/绝对钳制在 ApplyMinimumInterval 内完成，
        // 不在这里另写一套边界逻辑）。
        var rawInterval = Fsrs6Model.IntervalForRetention(stability, SchedulingConfig.DesiredRetention, decay, factor);
        // 本层只产出纯 FSRS 的**基线**间隔（评分下限 + 绝对边界）。
        // 「同一本地日历日不再排入到期队列」是**本地日历**语义，需要本地日界，由协调器在
        // 组装最终间隔时用 ApplyMinimumInterval(…, reviewedAtUtc, localDayEndUtc) 施加——
        // 纯函数排期层不读时钟、不做时区推断（评审 P1-3）。
        var baselineIntervalDays = MemoryScheduler.ApplyMinimumInterval(rawInterval, rating, default, null);

        if (!MemoryScheduler.IsFinite(stability) || stability < SchedulingConfig.StabilityMin || stability > SchedulingConfig.StabilityMax)
            throw new InvalidOperationException($"FSRS-6 产出的稳定性非法：{stability:R}。");
        if (!MemoryScheduler.IsFinite(difficulty) || difficulty < SchedulingConfig.DifficultyMin || difficulty > SchedulingConfig.DifficultyMax)
            throw new InvalidOperationException($"FSRS-6 产出的难度非法：{difficulty:R}。");
        if (!MemoryScheduler.IsFinite(baselineIntervalDays) || baselineIntervalDays <= 0)
            throw new InvalidOperationException($"FSRS-6 产出的基线间隔非法：{baselineIntervalDays:R}。");
        if (!MemoryScheduler.IsFinite(baselineRetrievability) || baselineRetrievability < 0 || baselineRetrievability > 1)
            throw new InvalidOperationException($"FSRS-6 产出的可回忆性非法：{baselineRetrievability:R}。");

        var dueAt = DateTime.SpecifyKind(at.AddDays(baselineIntervalDays), DateTimeKind.Utc);

        var nextCard = new FsrsCardState
        {
            // WordKey 与 LastAppliedCanonicalSeq 属于持久化层的身份与幂等水位，排期层不改写。
            WordKey = card?.WordKey ?? "",
            LastAppliedCanonicalSeq = card?.LastAppliedCanonicalSeq,
            Stability = stability,
            Difficulty = difficulty,
            Reps = (card?.Reps ?? 0) + 1,
            Lapses = lapses,
            State = StateFor(rating),
            LastReviewAtUtc = at,
            NextReviewAtUtc = dueAt,
            LastCanonicalRating = rating,
            FsrsAlgorithmVersion = SchedulingConfig.AlgorithmVersion,
            FsrsLibraryVersion = SchedulingConfig.LibraryVersion,
            // 参数版本戳**从当前权重派生**（defaults 恒为 SchedulingConfig.ParameterVersion）。
            // 写死编译期常量会让换版后的 fsrs_cards / scheduler_decisions 继续声称自己是 fsrs6-p1——
            // 审计列撒谎，且重启一致性核对永远判"一致"。
            FsrsParameterVersion = FsrsPersonalization.ParameterVersionOf(Weights),
        };

        return new FsrsOutcome(nextCard, baselineIntervalDays, baselineRetrievability, dueAt);
    }

    /// <inheritdoc />
    public double Retrievability(double stabilityDays, double elapsedDays) =>
        Fsrs6Model.Retrievability(
            stabilityDays, elapsedDays, Fsrs6Model.DecayOf(Weights.Weights), Fsrs6Model.FactorOf(Weights.Weights));

    /// <inheritdoc />
    public double IntervalForRetention(double stabilityDays, double retention) =>
        Fsrs6Model.IntervalForRetention(
            stabilityDays, retention, Fsrs6Model.DecayOf(Weights.Weights), Fsrs6Model.FactorOf(Weights.Weights));

    /// <summary>
    /// 评级的长期状态语义（steps 为空时的口径，依据见下方说明）：
    /// Known→<see cref="FsrsState.Review"/>、Unsure→<see cref="FsrsState.Learning"/>、
    /// Forgot→<see cref="FsrsState.Relearning"/>。
    ///
    /// <para>
    /// 依据与边界：(a) 这是本升级任务书明确要求的映射；(b) py-fsrs 在 learning_steps 为空时会把**所有**评级
    /// 直接送入 Review 态（`len(self.learning_steps) == 0` 分支），因此本映射**不是** py-fsrs 的步骤机语义，
    /// 而是把 State 当作「上一次 canonical 是否demonstrably 记住了」的标记；(c) 该标记**不参与任何 D/S 数学**
    /// ——已用 692 行 golden 实测：D/S 轨迹与 state 无关（同一组时间戳+评级在 steps 为空/非空下产出完全相同的
    /// D/S）。因此该映射不会影响排期数值，只影响 UI/统计口径与 FsrsPreState 回放。
    /// </para>
    /// </summary>
    private static FsrsState StateFor(StudyRating rating) => rating switch
    {
        StudyRating.Known => FsrsState.Review,
        StudyRating.Unsure => FsrsState.Learning,
        _ => FsrsState.Relearning,
    };

    private static void ValidateExistingCard(FsrsCardState card)
    {
        if (!MemoryScheduler.IsFinite(card.Stability) || card.Stability <= 0)
            throw new InvalidOperationException($"卡片 {card.WordKey} 的稳定性非法（必须是有限正数）：{card.Stability:R}。");
        if (!MemoryScheduler.IsFinite(card.Difficulty)
            || card.Difficulty < SchedulingConfig.DifficultyMin
            || card.Difficulty > SchedulingConfig.DifficultyMax)
            throw new InvalidOperationException($"卡片 {card.WordKey} 的难度非法（必须落在 [1,10]）：{card.Difficulty:R}。");
    }

    /// <summary>统一到 UTC：Utc 原样；Local 转换；Unspecified 按 UTC 解释（不猜测本机时区）。</summary>
    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
