namespace Lexi;

/// <summary>
/// FSRS-6 的**纯数学**实现：无 IO、无时钟、无状态。所有公式逐条来自
/// <c>agents/audit-05-fsrs-upstream.md</c> §3.2（每条标注来源），禁止凭记忆改动。
///
/// <para>
/// 时间口径（audit §10 硬性要求 2）：一切间隔以**整天截断**为单位，且用整数 ticks 相减
/// （<see cref="ElapsedWholeDays"/>），不做任何浮点「天」累加。
/// </para>
/// <para>
/// 难度口径（audit §10 硬性要求 1）：<c>D0(4)</c> 的**未截断值**（−4.7716307）只用于均值回归内部，
/// 对外返回值一律 clamp 到 [1, 10]。
/// </para>
/// </summary>
public static class Fsrs6Model
{
    /// <summary>FSRS 评分常量：Again=1 / Hard=2 / Good=3 / Easy=4。</summary>
    public const int RatingAgain = 1;
    public const int RatingHard = 2;
    public const int RatingGood = 3;
    public const int RatingEasy = 4;

    /// <summary>
    /// 项目口径 <see cref="StudyRating"/> → FSRS 评分：Known→Good(3)、Unsure→Hard(2)、Forgot→Again(1)。
    /// 与 <c>CanonicalReview.Rating</c> 的注释口径一致，不提供 Easy。
    /// </summary>
    public static int ToFsrsRating(StudyRating rating) => rating switch
    {
        StudyRating.Known => RatingGood,
        StudyRating.Unsure => RatingHard,
        StudyRating.Forgot => RatingAgain,
        _ => throw new ArgumentOutOfRangeException(nameof(rating), rating, "未知的 StudyRating。"),
    };

    // ══ 时间轴 ══

    /// <summary>
    /// 两个 UTC 时刻之间的**整天数**，向下截断且不小于 0。
    /// 用整数 ticks 相减（audit §10 硬性要求 2 / §6.2 记录的坑：浮点小时累加会把 72 天压成 71.99999999999999）。
    /// </summary>
    public static long ElapsedWholeDays(DateTime fromUtc, DateTime toUtc)
    {
        var ticks = toUtc.Ticks - fromUtc.Ticks;
        if (ticks <= 0) return 0;
        return ticks / TimeSpan.TicksPerDay;
    }

    // ══ 权重派生常量 ══

    /// <summary>DECAY = −w20（audit §3.2 ①）。</summary>
    public static double DecayOf(IReadOnlyList<double> weights)
    {
        Validate(weights);
        return -weights[20];
    }

    /// <summary>FACTOR = 0.9^(1/DECAY) − 1（audit §3.2 ①）。默认权重下等于 <see cref="SchedulingConfig.Factor"/>。</summary>
    public static double FactorOf(IReadOnlyList<double> weights) => Math.Pow(0.9, 1.0 / DecayOf(weights)) - 1.0;

    // ══ 钳制 ══

    /// <summary>稳定性 clamp 到 [StabilityMin, StabilityMax]（fsrs-rs <c>S_MIN/S_MAX</c>）。</summary>
    public static double ClampStability(double stability) => Math.Clamp(stability, SchedulingConfig.StabilityMin, SchedulingConfig.StabilityMax);

    /// <summary>难度 clamp 到 [1, 10]（fsrs-rs <c>D_MIN/D_MAX</c>）。</summary>
    public static double ClampDifficulty(double difficulty) => Math.Clamp(difficulty, SchedulingConfig.DifficultyMin, SchedulingConfig.DifficultyMax);

    // ══ ① 遗忘曲线 ══

    /// <summary>
    /// R(t, S) = (1 + FACTOR · t / S)^DECAY。来源 audit §3.2 ①：
    /// wiki The-Algorithm「R(t,S) = (1 + factor·t/S)^(−w20)」；代码 <c>fsrs-rs/src/model_v6.rs::power_forgetting_curve_scalar</c>。
    /// </summary>
    public static double Retrievability(double stabilityDays, double elapsedDays) =>
        Retrievability(stabilityDays, elapsedDays, SchedulingConfig.Decay, SchedulingConfig.Factor);

    /// <inheritdoc cref="Retrievability(double, double)"/>
    public static double Retrievability(double stabilityDays, double elapsedDays, double decay, double factor)
    {
        if (!MemoryScheduler.IsFinite(stabilityDays) || stabilityDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(stabilityDays), stabilityDays, "稳定性必须是有限正数。");
        if (!MemoryScheduler.IsFinite(elapsedDays) || elapsedDays < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedDays), elapsedDays, "已过天数必须是有限非负数。");
        return Math.Pow(1.0 + factor * elapsedDays / stabilityDays, decay);
    }

    // ══ ② R 反解间隔 ══

    /// <summary>
    /// I(r, S) = S / FACTOR · (r^(1/DECAY) − 1)。来源 audit §3.2 ②：
    /// <c>fsrs-rs/src/model_v6.rs::next_interval_scalar</c>（clamp 到 [0, S_MAX]）。
    /// **注意**：官方「至少 1 天」的下限来自排程层（py-fsrs <c>_next_interval</c> 的 <c>max(round(v), 1)</c>），
    /// 不属于本公式；本方法返回未取整的连续天数，由调用方按需取整/夹取。
    /// </summary>
    public static double IntervalForRetention(double stabilityDays, double retention) =>
        IntervalForRetention(stabilityDays, retention, SchedulingConfig.Decay, SchedulingConfig.Factor);

    /// <inheritdoc cref="IntervalForRetention(double, double)"/>
    public static double IntervalForRetention(double stabilityDays, double retention, double decay, double factor)
    {
        if (!MemoryScheduler.IsFinite(stabilityDays) || stabilityDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(stabilityDays), stabilityDays, "稳定性必须是有限正数。");
        if (!MemoryScheduler.IsFinite(retention) || retention <= 0 || retention > 1)
            throw new ArgumentOutOfRangeException(nameof(retention), retention, "目标保持率必须落在 (0, 1] 内。");
        var interval = stabilityDays / factor * (Math.Pow(retention, 1.0 / decay) - 1.0);
        return Math.Clamp(interval, 0.0, SchedulingConfig.StabilityMax);
    }

    // ══ ③ 初始稳定性 ══

    /// <summary>
    /// S0(G) = w[G−1]（G = Again/Hard/Good/Easy → w0..w3）。来源 audit §3.2 ③：
    /// wiki The-Algorithm FSRS v4 节；代码 <c>fsrs-rs/src/model.rs:173-175</c>
    /// （<c>parameters[rating.saturating_sub(1).min(3)]</c>）。返回长度 4 的数组，索引 0..3 = Again..Easy。
    /// </summary>
    public static double[] InitialStability(IReadOnlyList<double> weights)
    {
        Validate(weights);
        return
        [
            ClampStability(weights[0]),
            ClampStability(weights[1]),
            ClampStability(weights[2]),
            ClampStability(weights[3]),
        ];
    }

    // ══ ④ 初始难度 ══

    /// <summary>
    /// D0(G) = w4 − e^(w5·(G−1)) + 1 的**未截断**值。来源 audit §3.2 ④：
    /// wiki The-Algorithm FSRS-5 节；代码 <c>fsrs-rs/src/model_v6.rs::init_difficulty_scalar</c>。
    ///
    /// <para>
    /// audit §10 硬性要求 1 / §3.2 ④ 的「关键陷阱」：<c>D0(4) = −4.7716307032</c> 远在 [1,10] 之外，
    /// 但均值回归必须以这个未截断值为目标（py-fsrs 写作 <c>_initial_difficulty(rating=Rating.Easy, clamp=False)</c>）。
    /// 只有对外返回值（<see cref="InitialDifficulty"/>）才 clamp。
    /// </para>
    /// </summary>
    public static double InitialDifficultyRaw(IReadOnlyList<double> weights, int rating)
    {
        Validate(weights);
        ValidateRating(rating);
        return weights[4] - Math.Exp(weights[5] * (rating - 1)) + 1.0;
    }

    /// <summary>
    /// D0(G) 的对外值：<see cref="InitialDifficultyRaw"/> 后 clamp 到 [1, 10]。
    /// 实测：D0 = [6.4133, 5.112170705601056, 2.118103970459016, 1.0]。
    /// </summary>
    public static double InitialDifficulty(IReadOnlyList<double> weights, int rating) =>
        ClampDifficulty(InitialDifficultyRaw(weights, rating));

    // ══ ⑤ 难度更新 ══

    /// <summary>
    /// ΔD(G) = −w6·(G−3)；D′ = D + ΔD·(10−D)/9（线性阻尼）；D″ = w7·D0(4)_raw + (1−w7)·D′（均值回归）；
    /// 返回 clamp(D″, 1, 10)。来源 audit §3.2 ⑤：wiki The-Algorithm FSRS-5 节「Linear Damping for the new
    /// difficulty after review」+ 代码 <c>fsrs-rs/src/model_v6.rs::next_difficulty_scalar / mean_reversion_scalar</c>。
    /// </summary>
    public static double NextDifficulty(IReadOnlyList<double> weights, double difficulty, int rating)
    {
        Validate(weights);
        ValidateRating(rating);
        if (!MemoryScheduler.IsFinite(difficulty))
            throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty, "难度必须是有限值。");

        var deltaDifficulty = -(weights[6] * (rating - 3));
        var damped = difficulty + (10.0 - difficulty) * deltaDifficulty / 9.0;
        var meanReversionTarget = InitialDifficultyRaw(weights, RatingEasy); // 未截断的 D0(4)
        var next = weights[7] * meanReversionTarget + (1.0 - weights[7]) * damped;
        return ClampDifficulty(next);
    }

    // ══ ⑥ 成功回忆后的稳定性 ══

    /// <summary>
    /// S′_S = S · ( e^(w8) · (11−D) · S^(−w9) · ( e^((1−R)·w10) − 1 ) · hard_penalty · easy_bonus + 1 )，
    /// hard_penalty = w15（G=Hard）/ 1，easy_bonus = w16（G=Easy）/ 1，结果 clamp 到 [S_MIN, S_MAX]。
    /// 来源 audit §3.2 ⑥：wiki The-Algorithm FSRS v4 节；代码 <c>fsrs-rs/src/model_v6.rs::stability_after_success_scalar</c>。
    /// 只接受 G ∈ {Hard, Good, Easy}；Again 走 <see cref="NextStabilityFailure"/>。
    /// </summary>
    public static double NextStabilitySuccess(
        IReadOnlyList<double> weights, double difficulty, double stability, double retrievability, int rating)
    {
        Validate(weights);
        if (rating == RatingAgain)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "Again 必须走 NextStabilityFailure。");
        ValidateRating(rating);
        ValidateState(difficulty, stability, retrievability);

        var hardPenalty = rating == RatingHard ? weights[15] : 1.0;
        var easyBonus = rating == RatingEasy ? weights[16] : 1.0;
        var next = stability * (
            Math.Exp(weights[8])
            * (11.0 - difficulty)
            * Math.Pow(stability, -weights[9])
            * (Math.Exp((1.0 - retrievability) * weights[10]) - 1.0)
            * hardPenalty
            * easyBonus
            + 1.0);
        return ClampStability(next);
    }

    // ══ ⑦ 遗忘后的稳定性 ══

    /// <summary>
    /// long_term = w11 · D^(−w12) · ((S+1)^w13 − 1) · e^((1−R)·w14)；
    /// short_term = S / e^(w17·w18)；S′_F = min(long_term, short_term)，clamp 到 [S_MIN, S_MAX]。
    /// 来源 audit §3.2 ⑦：wiki The-Algorithm 给出 long_term；short_term 上限项来自官方代码
    /// <c>fsrs-rs/src/model_v6.rs::stability_after_failure_scalar</c> 与 py-fsrs <c>_next_forget_stability</c>
    /// （wiki 未覆盖，审计标注为「代码来源已验证 / wiki 文档缺失」）。
    /// </summary>
    public static double NextStabilityFailure(
        IReadOnlyList<double> weights, double difficulty, double stability, double retrievability)
    {
        Validate(weights);
        ValidateState(difficulty, stability, retrievability);

        var shortTerm = stability / Math.Exp(weights[17] * weights[18]);
        var longTerm = weights[11]
            * Math.Pow(difficulty, -weights[12])
            * (Math.Pow(stability + 1.0, weights[13]) - 1.0)
            * Math.Exp((1.0 - retrievability) * weights[14]);
        return ClampStability(Math.Min(longTerm, shortTerm));
    }

    // ══ ⑧ 短期（同日）稳定性 ══

    /// <summary>
    /// SInc = e^( w17 · (G − 3 + w18) ) · S^(−w19)；S′ = S · (G ∈ {Hard,Good,Easy} ? max(SInc, 1.0) : SInc)，
    /// clamp 到 [S_MIN, S_MAX]。来源 audit §3.2 ⑧：wiki The-Algorithm **FSRS-6 节原文**
    /// （"In practice, we should ensure that SInc ≥ 1 when G ≥ 2"）。
    ///
    /// <para>
    /// audit §10 硬性要求 3：<c>max(·, 1.0)</c> 必须对 <b>G ∈ {2,3,4} 全部</b>生效——含 Hard。
    /// 依据 py-fsrs <c>_short_term_stability</c> 的 <c>if rating in (Rating.Hard, Rating.Good, Rating.Easy)</c>。
    /// （fsrs-rs 6.6.2 内 <c>stability_short_term_scalar</c> 用 <c>rating &gt;= 3</c>、而 <c>next_state_scalar</c>
    /// 内联版本用 <c>rating &gt;= 2</c>，审计已标注该不一致并判定以 <c>&gt;= 2</c> 为准。）
    /// Again(G=1) 不设下限，允许 S′ &lt; S——这正是「真的忘了」应有的效果。
    /// </para>
    /// </summary>
    public static double ShortTermStability(IReadOnlyList<double> weights, double stability, int rating)
    {
        Validate(weights);
        ValidateRating(rating);
        if (!MemoryScheduler.IsFinite(stability) || stability <= 0)
            throw new ArgumentOutOfRangeException(nameof(stability), stability, "稳定性必须是有限正数。");

        var increase = Math.Exp(weights[17] * (rating - 3 + weights[18])) * Math.Pow(stability, -weights[19]);
        // audit §10 硬性要求 3：Hard/Good/Easy 都享受 max(·, 1.0) 下限。
        if (rating >= RatingHard) increase = Math.Max(increase, 1.0);
        return ClampStability(stability * increase);
    }

    // ══ 校验 ══

    internal static void Validate(IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count != SchedulingConfig.FsrsParameterCount)
            throw new ArgumentException($"FSRS-6 权重必须是 {SchedulingConfig.FsrsParameterCount} 个，实际 {weights.Count} 个。", nameof(weights));
        for (var i = 0; i < weights.Count; i++)
        {
            if (!MemoryScheduler.IsFinite(weights[i]))
                throw new ArgumentException($"w{i} 非有限值：{weights[i]}。", nameof(weights));
        }
    }

    private static void ValidateRating(int rating)
    {
        if (rating is < RatingAgain or > RatingEasy)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "FSRS 评分必须是 1..4。");
    }

    private static void ValidateState(double difficulty, double stability, double retrievability)
    {
        if (!MemoryScheduler.IsFinite(difficulty))
            throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty, "难度必须是有限值。");
        if (!MemoryScheduler.IsFinite(stability) || stability <= 0)
            throw new ArgumentOutOfRangeException(nameof(stability), stability, "稳定性必须是有限正数。");
        if (!MemoryScheduler.IsFinite(retrievability) || retrievability < 0 || retrievability > 1)
            throw new ArgumentOutOfRangeException(nameof(retrievability), retrievability, "可回忆性必须落在 [0, 1] 内。");
    }
}

/// <summary>
/// py-fsrs 6.3.2 参考状态机里的一张卡。状态取值 1=Learning / 2=Review / 3=Relearning，
/// 与 golden <c>expected.csv</c> 的 <c>state</c> 列一致。
/// </summary>
public sealed class Fsrs6ReferenceCard
{
    public const int StateLearning = 1;
    public const int StateReview = 2;
    public const int StateRelearning = 3;

    /// <summary>1=Learning / 2=Review / 3=Relearning。</summary>
    public int State { get; set; } = StateLearning;

    /// <summary>学习步序号；Review 态为 null。新建卡在 Learning 态从第 0 步开始（对齐 py-fsrs 的 <c>Card()</c>）。</summary>
    public int? Step { get; set; } = 0;

    public double? Stability { get; set; }
    public double? Difficulty { get; set; }
    public DateTime? LastReviewAtUtc { get; set; }
    public DateTime DueAtUtc { get; set; }
    public long Reps { get; set; }
    public long Lapses { get; set; }

    public Fsrs6ReferenceCard Clone() => (Fsrs6ReferenceCard)MemberwiseClone();
}

/// <summary>
/// **仅供 golden 向量验证**的参考运行器：逐行转写 py-fsrs 6.3.2 的 <c>Scheduler.review_card</c>
/// （含 Learning / Review / Relearning 三态与 learning/relearning steps）。
///
/// <para>
/// 生产路径**不使用**它：<see cref="Fsrs6Scheduler"/> 的 learning/relearning steps 恒为空
/// （见 <see cref="SchedulingConfig.DefaultLearningSteps"/>）。它存在的唯一理由是把官方 golden
/// <c>scenarios.json</c>（learning_steps=[1min,10min]、relearning_steps=[10min]）
/// 与 py-fsrs <c>test_basic.py</c> 的官方间隔向量复现出来，从而锁死本实现的公式与时间口径。
/// </para>
/// <para>
/// 该运行器与生产路径共享同一套 D/S 公式（<see cref="Fsrs6Model"/>），只有「下一步排到哪」的
/// 状态机不同——因此 golden 的 stability / difficulty 在两条路径上是同一个数（已实测 692/692）。
/// </para>
/// </summary>
public static class Fsrs6Reference
{
    /// <summary>官方 py-fsrs 默认 learning steps：<c>[1min, 10min]</c>。</summary>
    public static readonly TimeSpan[] OfficialLearningSteps = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)];

    /// <summary>官方 py-fsrs 默认 relearning steps：<c>[10min]</c>。</summary>
    public static readonly TimeSpan[] OfficialRelearningSteps = [TimeSpan.FromMinutes(10)];

    /// <summary>
    /// 按 py-fsrs <c>review_card</c> 更新 <paramref name="card"/> 的一张副本并返回。
    /// steps 是**显式参数**，不读取任何全局配置。
    /// </summary>
    public static Fsrs6ReferenceCard Review(
        IReadOnlyList<double> weights,
        Fsrs6ReferenceCard card,
        int rating,
        DateTime reviewAtUtc,
        IReadOnlyList<TimeSpan> learningSteps,
        IReadOnlyList<TimeSpan> relearningSteps,
        double desiredRetention = SchedulingConfig.DesiredRetention,
        int maximumInterval = (int)SchedulingConfig.MaximumIntervalDays)
    {
        Fsrs6Model.Validate(weights);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(learningSteps);
        ArgumentNullException.ThrowIfNull(relearningSteps);

        var decay = Fsrs6Model.DecayOf(weights);
        var factor = Fsrs6Model.FactorOf(weights);
        var next = card.Clone();
        var elapsed = next.LastReviewAtUtc is { } last ? Fsrs6Model.ElapsedWholeDays(last, reviewAtUtc) : (long?)null;

        double NextIntervalDays(double stability)
        {
            var raw = (stability / factor) * (Math.Pow(desiredRetention, 1.0 / decay) - 1.0);
            var rounded = (long)Math.Round(raw, MidpointRounding.ToEven);
            return Math.Clamp(rounded, 1, maximumInterval);
        }

        TimeSpan GraduateToReview()
        {
            next.State = Fsrs6ReferenceCard.StateReview;
            next.Step = null;
            return TimeSpan.FromDays(NextIntervalDays(next.Stability!.Value));
        }

        // —— py-fsrs: 先按「同日 / 非同日」更新 D/S，再做步骤机 ——
        void UpdateMemory()
        {
            if (next.Stability is null || next.Difficulty is null)
            {
                next.Stability = Fsrs6Model.ClampStability(weights[rating - 1]);
                next.Difficulty = Fsrs6Model.InitialDifficulty(weights, rating);
                return;
            }
            var stability = next.Stability.Value;
            var difficulty = next.Difficulty.Value;
            if (elapsed is null || elapsed < 1)
            {
                next.Stability = Fsrs6Model.ShortTermStability(weights, stability, rating);
            }
            else
            {
                var r = Fsrs6Model.Retrievability(stability, Math.Max(0, elapsed.Value), decay, factor);
                next.Stability = rating == Fsrs6Model.RatingAgain
                    ? Fsrs6Model.NextStabilityFailure(weights, difficulty, stability, r)
                    : Fsrs6Model.NextStabilitySuccess(weights, difficulty, stability, r, rating);
            }
            next.Difficulty = Fsrs6Model.NextDifficulty(weights, difficulty, rating);
        }

        TimeSpan interval;
        switch (next.State)
        {
            case Fsrs6ReferenceCard.StateLearning:
                UpdateMemory();
                var learningStep = next.Step ?? 0;
                // py-fsrs：空 steps，或 steps 已用完且不是 Again → 直接毕业到 Review。
                if (learningSteps.Count == 0 || (learningStep >= learningSteps.Count && rating >= Fsrs6Model.RatingHard))
                {
                    interval = GraduateToReview();
                }
                else if (rating == Fsrs6Model.RatingAgain)
                {
                    next.Step = 0;
                    interval = learningSteps[0];
                }
                else if (rating == Fsrs6Model.RatingHard)
                {
                    // step 保持不变
                    if (learningStep == 0 && learningSteps.Count == 1) interval = learningSteps[0] * 1.5;
                    else if (learningStep == 0 && learningSteps.Count >= 2) interval = (learningSteps[0] + learningSteps[1]) / 2.0;
                    else interval = learningSteps[learningStep];
                }
                else if (rating == Fsrs6Model.RatingGood)
                {
                    if (learningStep + 1 == learningSteps.Count) interval = GraduateToReview();
                    else { next.Step = learningStep + 1; interval = learningSteps[next.Step.Value]; }
                }
                else
                {
                    interval = GraduateToReview();
                }
                break;

            case Fsrs6ReferenceCard.StateReview:
                UpdateMemory();
                if (rating == Fsrs6Model.RatingAgain)
                {
                    if (relearningSteps.Count == 0) interval = TimeSpan.FromDays(NextIntervalDays(next.Stability!.Value));
                    else { next.State = Fsrs6ReferenceCard.StateRelearning; next.Step = 0; interval = relearningSteps[0]; }
                }
                else
                {
                    interval = TimeSpan.FromDays(NextIntervalDays(next.Stability!.Value));
                }
                break;

            case Fsrs6ReferenceCard.StateRelearning:
                UpdateMemory();
                var relearningStep = next.Step ?? 0;
                if (relearningSteps.Count == 0 || (relearningStep >= relearningSteps.Count && rating >= Fsrs6Model.RatingHard))
                {
                    interval = GraduateToReview();
                }
                else if (rating == Fsrs6Model.RatingAgain)
                {
                    next.Step = 0;
                    interval = relearningSteps[0];
                }
                else if (rating == Fsrs6Model.RatingHard)
                {
                    if (relearningStep == 0 && relearningSteps.Count == 1) interval = relearningSteps[0] * 1.5;
                    else if (relearningStep == 0 && relearningSteps.Count >= 2) interval = (relearningSteps[0] + relearningSteps[1]) / 2.0;
                    else interval = relearningSteps[relearningStep];
                }
                else if (rating == Fsrs6Model.RatingGood)
                {
                    if (relearningStep + 1 == relearningSteps.Count) interval = GraduateToReview();
                    else { next.Step = relearningStep + 1; interval = relearningSteps[next.Step.Value]; }
                }
                else
                {
                    interval = GraduateToReview();
                }
                break;

            default:
                throw new InvalidOperationException($"未知的参考状态机状态：{next.State}。");
        }

        next.DueAtUtc = reviewAtUtc + interval;
        next.LastReviewAtUtc = reviewAtUtc;
        next.Reps++;
        if (rating == Fsrs6Model.RatingAgain) next.Lapses++;
        return next;
    }
}
