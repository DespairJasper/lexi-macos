namespace Lexi;

/// <summary>纯 FSRS 基线的排期结果（不含 Context 校准）。</summary>
public sealed record FsrsOutcome(
    FsrsCardState Card,
    double BaselineIntervalDays,
    double BaselineRetrievability,
    DateTime? BaselineDueAtUtc);

/// <summary>Context 对基线的残差校准量。<c>Applied=false</c> 时 <see cref="Delta"/> 必须为 0。</summary>
public readonly record struct ContextAdjustment(bool Applied, double Delta, ContextMode Mode)
{
    public static ContextAdjustment None(ContextMode mode) => new(false, 0, mode);
}

/// <summary>
/// 长期记忆调度器接口。<paramref name="rating"/> 只用 <see cref="StudyRating"/> 的三种取值，
/// 映射固定为 Known→Good、Unsure→Hard、Forgot→Again，不提供 Easy。
/// </summary>
public interface IMemoryScheduler
{
    /// <summary>纯 FSRS-6 更新：给出新的 D/S/state 与基线间隔。不做 Context 校准，不写库。</summary>
    FsrsOutcome Review(FsrsCardState? card, StudyRating rating, DateTime reviewedAtUtc);

    /// <summary>可回忆性 R(t)。<paramref name="elapsedDays"/> 必须 ≥ 0。</summary>
    double Retrievability(double stabilityDays, double elapsedDays);

    /// <summary>把 R 反解为间隔：求 t 使 R(t) = <paramref name="retention"/>。</summary>
    double IntervalForRetention(double stabilityDays, double retention);
}

/// <summary>
/// 决策组装与安全钳制。**全部为纯函数**：不读时钟、不写库、不依赖 FSRS 内部常数，
/// 因此可以在不引入任何公式假设的前提下单独测试边界行为。
/// </summary>
public static class MemoryScheduler
{
    /// <summary>有限性检查：拒绝 NaN / ±Inf。</summary>
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>
    /// 由基线间隔与 Context 校准量求最终间隔。
    /// 规则（用户需求 §7）：<c>final = clamp(contextInterval, base*0.5, base*1.5)</c>，
    /// 并叠加绝对边界 [<see cref="SchedulingConfig.MinimumIntervalDays"/>, <see cref="SchedulingConfig.MaximumIntervalDays"/>]。
    /// Context 未启用、ColdStart、Shadow、或 candidate 非有限 → 返回纯基线间隔。
    /// </summary>
    public static double ResolveFinalInterval(double baselineIntervalDays, double? contextCandidateDays, ContextAdjustment adjustment)
    {
        var baseline = ClampAbsolute(Sanitize(baselineIntervalDays, SchedulingConfig.MinimumIntervalDays));
        if (!IsContextApplied(adjustment, contextCandidateDays)) return baseline;
        var candidate = contextCandidateDays!.Value;
        var lower = baseline * SchedulingConfig.ClampLowerRatio;
        var upper = baseline * SchedulingConfig.ClampUpperRatio;
        // baseline 可能被绝对下界抬高，此时 lower/upper 需按同一基线计算（顺序：先相对夹取，再绝对夹取）。
        var clamped = Math.Min(Math.Max(candidate, lower), upper);
        return ClampAbsolute(clamped);
    }

    /// <summary>把非有限/非正数回退为 <paramref name="fallback"/>，再夹到绝对边界内。</summary>
    public static double Sanitize(double value, double fallback) =>
        IsFinite(value) && value > 0 ? value : fallback;

    private static double ClampAbsolute(double days) => Math.Min(
        Math.Max(days, SchedulingConfig.MinimumIntervalDays), SchedulingConfig.MaximumIntervalDays);

    /// <summary>
    /// 用二分法求 <c>adjustedR(t) ≈ desiredRetention</c> 的 t（天）。
    /// <paramref name="adjustedRetrievability"/> 是「给定间隔天数 → 校准后可回忆性」的单调递减函数
    /// （由调用方用当前 canonical 时刻的**固定合法历史特征**构造，因此候选 t 只是它的入参）。
    /// 求根失败（区间外、非单调、非有限、迭代上限内未收敛）时返回 null，由调用方回退到纯基线。
    /// </summary>
    public static double? SolveIntervalByBisection(
        Func<double, double> adjustedRetrievability,
        double desiredRetention,
        double lowerDays = 0.0,
        double upperDays = SchedulingConfig.MaximumIntervalDays,
        int maxIterations = 200,
        double toleranceDays = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(adjustedRetrievability);
        if (!IsFinite(desiredRetention) || desiredRetention is <= 0 or >= 1) return null;
        if (!IsFinite(lowerDays) || !IsFinite(upperDays) || lowerDays < 0 || upperDays <= lowerDays) return null;

        var low = lowerDays;
        var high = upperDays;
        var rLow = adjustedRetrievability(low);
        var rHigh = adjustedRetrievability(high);
        if (!IsFinite(rLow) || !IsFinite(rHigh)) return null;
        // 要求 r 随 t 递减：rLow ≥ retention ≥ rHigh
        if (rLow < desiredRetention || rHigh > desiredRetention) return null;

        for (var i = 0; i < maxIterations && high - low > toleranceDays; i++)
        {
            var mid = 0.5 * (low + high);
            var rMid = adjustedRetrievability(mid);
            if (!IsFinite(rMid)) return null;
            if (rMid >= desiredRetention) low = mid;
            else high = mid;
        }
        var result = 0.5 * (low + high);
        return IsFinite(result) && result > 0 ? result : null;
    }

    /// <summary>
    /// 组装一条完整的 <see cref="SchedulerDecision"/> 审计记录。
    /// 所有版本戳集中在这里注入，避免散落在调用点。
    /// </summary>
    public static SchedulerDecision BuildDecision(
        string decisionId,
        string wordKey,
        string canonicalId,
        DateTime timestampUtc,
        FsrsCardState cardBefore,
        double baselineIntervalDays,
        double baselineRetrievability,
        ContextAdjustment adjustment,
        double? contextCandidateDays,
        double finalIntervalDays,
        DateTime finalDueAtUtc,
        string fsrsAlgorithmVersion,
        string fsrsLibraryVersion,
        string fsrsParameterVersion,
        string contextModelVersion)
    {
        if (!IsFinite(finalIntervalDays) || finalIntervalDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(finalIntervalDays), finalIntervalDays, "最终间隔必须是有限正数。");
        if (!IsFinite(baselineIntervalDays) || baselineIntervalDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(baselineIntervalDays), baselineIntervalDays, "基线间隔必须是有限正数。");
        if (finalDueAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("finalDueAtUtc 必须是 UTC。", nameof(finalDueAtUtc));

        // 审计自洽（评审 P1-6）：context_delta 与 context_candidate_interval_days 要么都有值、要么都为 null。
        // Applied=true 但候选间隔为 null（例如校准器没有覆写 SolveCandidate 的默认实现）时，
        // Δ 根本没有参与求解；此时落 context_delta 会让决策日志读成「应用了一个 Δ、间隔却没变」。
        var contextApplied = IsContextApplied(adjustment, contextCandidateDays);

        return new SchedulerDecision
        {
            DecisionId = decisionId,
            WordKey = wordKey,
            CanonicalId = canonicalId,
            TimestampUtc = timestampUtc,
            BaselineIntervalDays = baselineIntervalDays,
            BaselineRetrievability = baselineRetrievability,
            BaselineDifficulty = cardBefore.Difficulty,
            BaselineStability = cardBefore.Stability,
            ContextMode = adjustment.Mode,
            ContextDelta = contextApplied ? adjustment.Delta : null,
            ContextCandidateIntervalDays = contextApplied ? contextCandidateDays : null,
            FinalIntervalDays = finalIntervalDays,
            FinalDueAtUtc = finalDueAtUtc,
            DesiredRetention = SchedulingConfig.DesiredRetention,
            FsrsAlgorithmVersion = fsrsAlgorithmVersion,
            FsrsLibraryVersion = fsrsLibraryVersion,
            FsrsParameterVersion = fsrsParameterVersion,
            AggregationPolicyVersion = AggregationPolicy.Version,
            TrajectorySchemaVersion = SchedulingConfig.TrajectorySchemaVersion,
            ContextFeatureSchemaVersion = SchedulingConfig.ContextFeatureSchemaVersion,
            ContextModelVersion = contextModelVersion,
            SchedulerVersion = SchedulingConfig.SchedulerVersion,
        };
    }

    /// <summary>
    /// 长期最小间隔保护：防止 Again/Hard 之后立即回队导致同一轮/同一日无限循环。
    /// </summary>
    /// <param name="reviewedAtUtc">本次作答时刻（UTC）——同日保护必须以它为锚点。</param>
    /// <param name="localDayEndUtc">
    /// 该次作答所在**本地日历日的右端点**（= 下一个本地零点）对应的 UTC 时刻，由调用方按本机时区
    /// 算好后传入；本层是纯函数，**不读时钟、不做时区推断**。<c>null</c> 表示不做同日保护
    /// （例如 golden 回归里只关心 FSRS 基线间隔）。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>为什么必须有本地日界</b>：<c>Math.Max(value, MinimumIntervalDays)</c> 只是「至少 1 天」。
    /// 在夏令时回拨日，本地日历日长达 25 小时，作答于本地 00:30 时「+24 小时」会落回**同一个**
    /// 本地日历日的 23:30——「同一天内不允许再次排入到期队列」在那一天不成立。
    /// 因此这里显式比较「到期时刻」与「本地日界」，而不是比较天数与常数。
    /// </para>
    /// <para>
    /// 语义（可验证的定义）：若 <c>reviewedAtUtc + intervalDays ≤ localDayEndUtc</c>，
    /// 则把间隔抬到 <c>(localDayEndUtc − reviewedAtUtc) + SameDayBoundaryMarginSeconds</c>，
    /// 使到期时刻**严格晚于**本地日界；否则保持原间隔。间隔只会被抬高，绝不被缩短。
    /// </para>
    /// </remarks>
    public static double ApplyMinimumInterval(
        double intervalDays, StudyRating rating, DateTime reviewedAtUtc, DateTime? localDayEndUtc)
    {
        var floor = RatingFloor(rating);
        var value = Math.Max(Sanitize(intervalDays, floor), floor);

        if (localDayEndUtc is { } dayEnd)
        {
            var span = (dayEnd - reviewedAtUtc).TotalDays;
            // span < 0 说明调用方给的日界在作答之前（陈旧输入）→ 不做保护，只保留下限。
            if (IsFinite(span) && span >= 0 && value <= span)
                value = span + SchedulingConfig.SameDayBoundaryMarginSeconds / 86400.0;
        }

        return ClampAbsolute(value);
    }

    /// <summary>
    /// <b>兼容重载：只应用评分下限与绝对边界，不做同日保护。</b>
    /// 布尔量无法表达「本地日界」，因此在 <paramref name="isSameLocalDay"/> 为 true 时本重载直接抛，
    /// 而不是静默地假装做过保护（那正是评审 P1-3 认定的缺陷）。
    /// 保留它只为一个既有调用方（golden 回归运行器 <c>tests/LearningTests</c>，恒传 false）；
    /// 生产代码请使用带 <c>reviewedAtUtc</c> / <c>localDayEndUtc</c> 的重载。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="isSameLocalDay"/> 为 true。</exception>
    public static double ApplyMinimumInterval(double intervalDays, StudyRating rating, bool isSameLocalDay)
    {
        if (isSameLocalDay)
            throw new ArgumentException(
                "该重载没有本地日界，无法实现同日保护；请改用 ApplyMinimumInterval(intervalDays, rating, reviewedAtUtc, localDayEndUtc)。",
                nameof(isSameLocalDay));
        return ApplyMinimumInterval(intervalDays, rating, default, null);
    }

    /// <summary>评分对应的最小间隔下限（天）。</summary>
    private static double RatingFloor(StudyRating rating) => rating switch
    {
        StudyRating.Forgot => SchedulingConfig.MinimumIntervalAfterAgainDays,
        StudyRating.Unsure => SchedulingConfig.MinimumIntervalAfterHardDays,
        _ => SchedulingConfig.MinimumIntervalDays,
    };

    /// <summary>
    /// Context 校准量是否**真的参与**了间隔求解：既要求校准器声明 Applied，
    /// 又要求它给出了有限正数的候选间隔。<see cref="ResolveFinalInterval"/> 与
    /// <see cref="BuildDecision"/> 必须用同一个判定——否则审计记录会出现
    /// 「落了 context_delta，却没有候选间隔、最终间隔也没变」的自相矛盾（评审 P1-6）。
    /// </summary>
    public static bool IsContextApplied(ContextAdjustment adjustment, double? contextCandidateDays) =>
        adjustment.Applied && contextCandidateDays is { } candidate && IsFinite(candidate) && candidate > 0;
}
