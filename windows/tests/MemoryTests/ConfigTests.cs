namespace Lexi.Tests;

/// <summary>
/// config 组：<see cref="SchedulingConfig"/> 常量与 <see cref="MemoryScheduler"/> 纯函数边界。
/// 不含任何 FSRS 公式假设——这里只钉住「钳制 / clamp / 二分 / 最小间隔 / 非有限值拒绝」这些安全行为。
/// </summary>
public static class ConfigTests
{
    public static void Run()
    {
        DesiredRetentionIsProductionValue();
        ResolveFinalIntervalClamps();
        ResolveFinalIntervalRejectsBadCandidates();
        ResolveFinalIntervalSanitizesBaseline();
        SanitizeAndFiniteChecks();
        BisectionConverges();
        BisectionReturnsNullOutsideRange();
        ApplyMinimumIntervalFloors();
        AbsoluteBoundsAreEnforced();
        SameDayMinimumIntervalGuard();
        DecisionAuditIsSelfConsistent();
    }

    private static void DesiredRetentionIsProductionValue()
    {
        Program.Check(SchedulingConfig.DesiredRetention == 0.90, "SchedulingConfig.DesiredRetention == 0.90");
        Program.Check(SchedulingConfig.ClampLowerRatio == 0.5 && SchedulingConfig.ClampUpperRatio == 1.5,
            "Context 钳制倍率 = 0.5 / 1.5");
        Program.Check(SchedulingConfig.MinimumIntervalDays == 1.0 && SchedulingConfig.MaximumIntervalDays == 36500.0,
            "绝对间隔边界 = [1, 36500] 天");
        Program.Check(SchedulingConfig.MaximumIntervalDays == 36500.0, "S_MAX 跟随 py-fsrs 口径 36500 天");
        Program.Check(SchedulingConfig.MinimumIntervalAfterAgainDays >= SchedulingConfig.MinimumIntervalDays
            && SchedulingConfig.MinimumIntervalAfterHardDays >= SchedulingConfig.MinimumIntervalDays,
            "Again/Hard 的长期最小间隔不低于绝对下界");
        // §Q 裁定 O-2：唯一性约束只存在于 word×session 层面（store 的 ux_canonical_active），
        // 不存在「每词每日一条」的常量——用反射钉住它不得被重新引入。
        Program.Check(typeof(SchedulingConfig).GetField("MaxCanonicalPerWordPerDay") is null,
            "SchedulingConfig 不再声明「每词每日至多一条 canonical」的常量（同日跨 session 的 canonical 允许并存）");
        Program.Check(SchedulingConfig.ContextFeatureSchemaVersion == "ctx-feat-1"
            && SchedulingConfig.TrajectorySchemaVersion == "traj-1"
            && SchedulingConfig.SchedulerVersion == "sched-1",
            "版本戳常量");
        Program.Check(SchedulingConfig.ContextMinimumSamples == 200
            && SchedulingConfig.ContextMinimumPositive == 30
            && SchedulingConfig.ContextMinimumNegative == 30
            && SchedulingConfig.ContextMinimumSpanDays == 14,
            "Context 训练门槛（样本 / 正负例 / 跨度）");
        Program.Check(AggregationPolicy.Version == "agg-1", "聚合策略版本 = agg-1");
    }

    private static void ResolveFinalIntervalClamps()
    {
        var applied = new ContextAdjustment(true, 0.1, ContextMode.Active);
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 12, applied), 12, 1e-12, "候选在钳制区间内 → 原样采用");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 100, applied), 15, 1e-12, "候选过大 → clamp 到 base×1.5");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 1, applied), 5, 1e-12, "候选过小 → clamp 到 base×0.5");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 15, applied), 15, 1e-12, "候选正好等于上界 → 保留");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 5, applied), 5, 1e-12, "候选正好等于下界 → 保留");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(20, 1000, applied), 30, 1e-12, "上界随基线缩放（20×1.5=30）");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(0.4, 100, applied), 1.5, 1e-12,
            "基线被绝对下界抬到 1 天后，相对钳制按同一基线计算（1×1.5）");
    }

    private static void ResolveFinalIntervalRejectsBadCandidates()
    {
        var applied = new ContextAdjustment(true, 0.1, ContextMode.Active);
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, null, applied), 10, 1e-12, "没有候选 → 纯基线");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, double.NaN, applied), 10, 1e-12, "候选 NaN → 纯基线");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, double.PositiveInfinity, applied), 10, 1e-12, "候选 +Inf → 纯基线");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, double.NegativeInfinity, applied), 10, 1e-12, "候选 -Inf → 纯基线");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, -5, applied), 10, 1e-12, "候选为负 → 纯基线");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 0, applied), 10, 1e-12, "候选为 0 → 纯基线");

        var disabled = ContextAdjustment.None(ContextMode.Disabled);
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 50, disabled), 10, 1e-12,
            "Applied=false → 即使给了候选也返回纯基线");
        var cold = ContextAdjustment.None(ContextMode.ColdStart);
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 50, cold), 10, 1e-12, "ColdStart → 纯基线");
        var shadow = ContextAdjustment.None(ContextMode.Shadow);
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 50, shadow), 10, 1e-12, "Shadow → 纯基线");
        Program.Check(ContextAdjustment.None(ContextMode.Active).Delta == 0, "None 的 Delta 必须为 0");
        Program.Check(!ContextAdjustment.None(ContextMode.Active).Applied, "None 的 Applied = false");
    }

    private static void ResolveFinalIntervalSanitizesBaseline()
    {
        var applied = new ContextAdjustment(true, 0.1, ContextMode.Active);
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(double.NaN, null, applied), 1.0, 1e-12,
            "基线 NaN → 回退绝对下界 1 天");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(0, null, applied), 1.0, 1e-12, "基线 0 → 回退 1 天");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(-7, null, applied), 1.0, 1e-12, "基线为负 → 回退 1 天");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(double.PositiveInfinity, null, applied), 1.0, 1e-12,
            "基线 +Inf → 回退绝对下界 1 天（非有限值不做上界推断）");
        Program.CheckClose(MemoryScheduler.Sanitize(double.PositiveInfinity, SchedulingConfig.MinimumIntervalDays), 1.0, 1e-12,
            "Sanitize(+Inf) → fallback");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(1e9, null, applied), 36500.0, 1e-12,
            "超大基线被夹到 MaximumIntervalDays");
    }

    private static void SanitizeAndFiniteChecks()
    {
        Program.Check(MemoryScheduler.IsFinite(0) && MemoryScheduler.IsFinite(-3.5) && MemoryScheduler.IsFinite(double.MaxValue),
            "IsFinite 接受所有有限值");
        Program.Check(!MemoryScheduler.IsFinite(double.NaN), "IsFinite 拒绝 NaN");
        Program.Check(!MemoryScheduler.IsFinite(double.PositiveInfinity) && !MemoryScheduler.IsFinite(double.NegativeInfinity),
            "IsFinite 拒绝 ±Inf");
        Program.CheckClose(MemoryScheduler.Sanitize(5, 1), 5, 1e-12, "Sanitize 保留合法正数");
        Program.CheckClose(MemoryScheduler.Sanitize(0, 1), 1, 1e-12, "Sanitize 把 0 换成 fallback");
        Program.CheckClose(MemoryScheduler.Sanitize(-2, 1), 1, 1e-12, "Sanitize 把负数换成 fallback");
        Program.CheckClose(MemoryScheduler.Sanitize(double.NaN, 1), 1, 1e-12, "Sanitize 把 NaN 换成 fallback");
    }

    private static void BisectionConverges()
    {
        // r(t) = 1/(1+t/10)：t=10 时 R=0.5
        static double R(double t) => 1.0 / (1.0 + t / 10.0);
        var solved = MemoryScheduler.SolveIntervalByBisection(R, 0.5, 0.0, 1000.0);
        Program.Check(solved is not null, "单调递减且跨越目标 → 求根成功");
        Program.CheckClose(solved!.Value, 10.0, 1e-3, "二分收敛到真解 t=10");
        Program.CheckClose(R(solved.Value), 0.5, 1e-4, "解处 R ≈ 目标保持率");

        var withRetention = MemoryScheduler.SolveIntervalByBisection(R, SchedulingConfig.DesiredRetention, 0.0, 1000.0);
        Program.Check(withRetention is not null, "生产目标保持率 0.90 可解");
        Program.CheckClose(R(withRetention!.Value), SchedulingConfig.DesiredRetention, 1e-4,
            "解处 R ≈ 0.90");

        var tight = MemoryScheduler.SolveIntervalByBisection(R, 0.5, 0.0, 20.0, maxIterations: 200, toleranceDays: 1e-6);
        Program.Check(tight is not null && Math.Abs(tight.Value - 10.0) <= 1e-4, "更小的容差仍收敛");

        var iterations = 0;
        MemoryScheduler.SolveIntervalByBisection(t =>
        {
            iterations++;
            return R(t);
        }, 0.5, 0.0, 1000.0, maxIterations: 3, toleranceDays: 1e-12);
        Program.Check(iterations <= 5, $"迭代上限被尊重（一次探测 + 至多 3 次二分，实际 {iterations}）");
    }

    private static void BisectionReturnsNullOutsideRange()
    {
        static double R(double t) => 1.0 / (1.0 + t / 10.0);
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, 0.5, 10.0, 5.0) is null, "下界 > 上界 → null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, 0.5, 5.0, 5.0) is null, "下界 == 上界 → null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, 0.5, -1.0, 100.0) is null, "下界为负 → null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, 0.5, double.NaN, 100.0) is null, "下界 NaN → null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, 0.99, 0.0, 0.1) is null,
            "上界处的 R 仍高于目标（rHigh > retention）→ null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, 0.001, 100.0, 1000.0) is null,
            "上界处的 R 仍高于目标（rHigh > retention，缩小目标也救不回来）→ null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, 0.0, 0.0, 100.0) is null, "目标保持率 0 → null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, 1.0, 0.0, 100.0) is null, "目标保持率 1 → null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(R, double.NaN, 0.0, 100.0) is null, "目标保持率 NaN → null");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(_ => double.NaN, 0.5, 0.0, 100.0) is null,
            "被求根函数返回非有限值 → null（不抛）");
        Program.Check(MemoryScheduler.SolveIntervalByBisection(_ => 0.9, 0.5, 0.0, 100.0) is null,
            "被求根函数是常数且不跨越目标（常数 0.9 vs 目标 0.5）→ null（不抛）");
        var threw = false;
        try { MemoryScheduler.SolveIntervalByBisection(null!, 0.5); }
        catch (ArgumentNullException) { threw = true; }
        Program.Check(threw, "被求根函数为 null → ArgumentNullException");
    }

    private static void ApplyMinimumIntervalFloors()
    {
        // 这一组只钉「评分下限 / 绝对边界」：localDayEndUtc = null（同日保护另见 SameDayMinimumIntervalGuard）。
        // 原断言用旧的 bool 重载（其 true 分支正是评审 P1-3 判定的死参数）表达；口径逐条不变，
        // 只有一条例外：「同一本地日内的保护不会缩短间隔（只会抬高）」原本传 true，而它只是因为参数是死的才成立，
        // 现改为直接钉住「旧重载必须 fail-loud」＋「传 false 行为不变」。
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(0.01, StudyRating.Forgot, default, null),
            SchedulingConfig.MinimumIntervalAfterAgainDays, 1e-12, "Again 之后有最小间隔下限");
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(0.01, StudyRating.Unsure, default, null),
            SchedulingConfig.MinimumIntervalAfterHardDays, 1e-12, "Hard（Unsure）之后有最小间隔下限");
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(0.01, StudyRating.Known, default, null),
            SchedulingConfig.MinimumIntervalDays, 1e-12, "Known 也不能低于绝对下界");
        Program.Check(MemoryScheduler.ApplyMinimumInterval(0.01, StudyRating.Forgot, default, null) >= SchedulingConfig.MinimumIntervalDays,
            "Again 的下限不低于绝对下界");
        Program.Check(MemoryScheduler.ApplyMinimumInterval(0.01, StudyRating.Unsure, default, null) >= SchedulingConfig.MinimumIntervalDays,
            "Hard 的下限不低于绝对下界");
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(30, StudyRating.Known, default, null), 30, 1e-12, "不低于下限时不改动");
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(double.NaN, StudyRating.Known, default, null),
            SchedulingConfig.MinimumIntervalDays, 1e-12, "非有限间隔 → 回退到下界");
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(-3, StudyRating.Forgot, default, null),
            SchedulingConfig.MinimumIntervalAfterAgainDays, 1e-12, "负间隔 → 回退到 Again 下限");

        var legacyBoolThrew = false;
        try { MemoryScheduler.ApplyMinimumInterval(30, StudyRating.Known, true); }
        catch (ArgumentException) { legacyBoolThrew = true; }
        Program.Check(legacyBoolThrew, "旧布尔重载在 isSameLocalDay=true 时 fail-loud，而不是静默不做同日保护");
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(30, StudyRating.Known, false), 30, 1e-12,
            "旧布尔重载传 false 时行为不变（golden 运行器的既有调用点）");
    }

    private static void AbsoluteBoundsAreEnforced()
    {
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(1e9, StudyRating.Known, default, null),
            SchedulingConfig.MaximumIntervalDays, 1e-12, "超大间隔夹到 MaximumIntervalDays");
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(double.PositiveInfinity, StudyRating.Forgot, default, null),
            SchedulingConfig.MinimumIntervalAfterAgainDays, 1e-12,
            "+Inf 间隔 → Sanitize 先回退到评级下限（不会当成超长间隔）");

        // BuildDecision 的入参守卫
        var card = new FsrsCardState { WordKey = "archive:x", Difficulty = 5, Stability = 10 };
        var none = ContextAdjustment.None(ContextMode.Disabled);
        var at = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var badInterval = false;
        try
        {
            MemoryScheduler.BuildDecision("d", "archive:x", "cr:archive:x:s", at, card, 10, 0.9, none, null, 0, at, "a", "l", "p", "");
        }
        catch (ArgumentOutOfRangeException) { badInterval = true; }
        Program.Check(badInterval, "BuildDecision 拒绝非正最终间隔");

        var badBaseline = false;
        try
        {
            MemoryScheduler.BuildDecision("d", "archive:x", "cr:archive:x:s", at, card, double.NaN, 0.9, none, null, 10, at, "a", "l", "p", "");
        }
        catch (ArgumentOutOfRangeException) { badBaseline = true; }
        Program.Check(badBaseline, "BuildDecision 拒绝非有限基线间隔");

        var badKind = false;
        try
        {
            MemoryScheduler.BuildDecision("d", "archive:x", "cr:archive:x:s", at, card, 10, 0.9, none, null, 10,
                DateTime.SpecifyKind(at, DateTimeKind.Unspecified), "a", "l", "p", "");
        }
        catch (ArgumentException) { badKind = true; }
        Program.Check(badKind, "BuildDecision 要求 finalDueAtUtc 是 UTC");

        var decision = MemoryScheduler.BuildDecision("d1", "archive:x", "cr:archive:x:s", at, card, 10, 0.9, none, null, 10, at, "alg", "lib", "par", "ctx");
        Program.Check(decision.BaselineDifficulty == 5 && decision.BaselineStability == 10, "决策记录基线 D/S");
        Program.Check(decision.ContextDelta is null && decision.ContextCandidateIntervalDays is null,
            "未应用校准时 Delta / candidate 记为 null");
        Program.Check(decision.DesiredRetention == SchedulingConfig.DesiredRetention
            && decision.SchedulerVersion == SchedulingConfig.SchedulerVersion
            && decision.TrajectorySchemaVersion == SchedulingConfig.TrajectorySchemaVersion
            && decision.ContextFeatureSchemaVersion == SchedulingConfig.ContextFeatureSchemaVersion
            && decision.AggregationPolicyVersion == AggregationPolicy.Version,
            "决策集中注入版本戳");
        Program.Check(decision.FsrsAlgorithmVersion == "alg" && decision.FsrsLibraryVersion == "lib"
            && decision.FsrsParameterVersion == "par" && decision.ContextModelVersion == "ctx",
            "决策透传 FSRS / Context 版本戳");
        Program.Check(decision.ContextMode == ContextMode.Disabled, "决策记录 ContextMode");
    }

    // ==================================================================================
    // P1-3：同日最小间隔保护必须是**可观测**的，不能是死参数
    // ==================================================================================

    private static void SameDayMinimumIntervalGuard()
    {
        // 语义定义：若 reviewedAtUtc + intervalDays <= localDayEndUtc（到期时刻落在同一本地日历日内），
        // 则把间隔抬到「到本地日界的时长 + 1 秒」，使到期时刻严格晚于本地日界；否则保持原间隔。
        // 对 3 个 rating × 1001 个间隔共 3003 组穷举下面三条：
        //   1) 保护必须至少对一组输入生效（旧实现是死参数，3003 组零差异）；
        //   2) 保护只能抬高、绝不缩短；
        //   3) 保护后到期时刻绝不落在同一个本地日历日内。
        // 用本机时区把「本地日历日」的两个端点换算成 UTC——与生产调用方
        // （LearningMemoryCoordinator.ResolveLocalDayEnd）同一口径，因此断言与机器时区无关。
        var dayStart = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var dayEnd = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var samples = 0;
        var pushed = 0;
        var shrunk = 0;
        var stillSameDay = 0;
        foreach (var rating in new[] { StudyRating.Known, StudyRating.Unsure, StudyRating.Forgot })
        {
            for (var i = -500; i <= 500; i++)
            {
                var days = i / 100.0;
                samples++;
                var guarded = MemoryScheduler.ApplyMinimumInterval(days, rating, dayStart, dayEnd);
                var unguarded = MemoryScheduler.ApplyMinimumInterval(days, rating, dayStart, null);
                if (guarded > unguarded) pushed++;
                if (guarded < unguarded) shrunk++;
                if (dayStart.AddDays(guarded).ToLocalTime().Date == dayStart.ToLocalTime().Date) stillSameDay++;
            }
        }
        Program.Check(samples == 3003 && pushed > 0,
            $"同日保护必须对至少一组输入生效（{samples} 组中生效 {pushed} 组）");
        Program.Check(shrunk == 0, $"同日保护只抬高、绝不缩短间隔（{samples} 组中被缩短 {shrunk} 组）");
        Program.Check(stillSameDay == 0,
            $"保护后没有任何一次到期落在同一个本地日历日内（{samples} 组中仍有 {stillSameDay} 组）");

        // —— 夏令时回拨日：本地日历日长 25 小时 ——
        // 2026-11-01 America/New_York：本地 00:00 = 04:00Z（EDT），次日本地 00:00 = 05:00Z（EST）。
        // 从本地零点起算的「+1 天」正好落回同一个本地日历日 —— 旧实现（Math.Max(value, 1.0)）防不住。
        var dstReviewedAt = new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc);
        var dstDayEnd = new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc);
        var dstDayLength = (dstDayEnd - dstReviewedAt).TotalDays;             // 25 小时 = 1.0416666…
        Program.CheckClose(dstDayLength, 25.0 / 24.0, 1e-12, "前提：该本地日历日长 25 小时");
        var dstInterval = MemoryScheduler.ApplyMinimumInterval(1.0, StudyRating.Known, dstReviewedAt, dstDayEnd);
        Program.Check(dstInterval > dstDayLength,
            $"DST 25 小时日：间隔被推到本地日界之后（{dstInterval:R} > {dstDayLength:R}）");
        Program.Check(dstReviewedAt.AddDays(dstInterval) > dstDayEnd,
            "DST 25 小时日：到期时刻严格晚于本地日界");

        // —— 已经越过日界的间隔不被缩短 ——
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(30, StudyRating.Known, dayStart, dayEnd), 30, 1e-12,
            "到期已经在本地日界之后 → 间隔原样保留");
        // —— 日界为空 / 陈旧（早于作答）时只做下限，不做同日保护 ——
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(0.2, StudyRating.Known, dayStart, null), 1.0, 1e-12,
            "没有日界时只应用绝对下界");
        Program.CheckClose(MemoryScheduler.ApplyMinimumInterval(0.2, StudyRating.Known, dayStart, dayStart.AddDays(-1)),
            1.0, 1e-12, "日界早于作答（陈旧输入）时不触发保护，只应用下界");
    }

    // ==================================================================================
    // P1-6：SchedulerDecision 的 Δ / candidate / final 三者必须自洽
    // ==================================================================================

    private static void DecisionAuditIsSelfConsistent()
    {
        var card = new FsrsCardState { WordKey = "archive:x", Difficulty = 5, Stability = 10 };
        var at = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var applied = new ContextAdjustment(true, 0.25, ContextMode.Active);

        // (a) 校准器声明 Applied=true，但 SolveCandidate 走默认实现（返回 null）→ 没有任何候选间隔。
        //     此时 Δ 根本没有参与求解，落库的 context_delta 会让日志读成「应用了 Δ 但间隔没变」。
        var noCandidate = MemoryScheduler.BuildDecision("d-nc", "archive:x", "cr:archive:x:s", at, card,
            10, 0.9, applied, contextCandidateDays: null, finalIntervalDays: 10, finalDueAtUtc: at.AddDays(10),
            "alg", "lib", "par", "");
        Program.Check(noCandidate.ContextDelta is null,
            $"没有候选间隔时不得落 context_delta（实际 {noCandidate.ContextDelta?.ToString() ?? "null"}）");
        Program.Check(noCandidate.ContextCandidateIntervalDays is null,
            $"没有候选间隔时 context_candidate_interval_days 必须为 null（实际 {noCandidate.ContextCandidateIntervalDays?.ToString() ?? "null"}）");

        // (b) Applied=false → Δ 与 candidate 都必须为 null（无论调用方传了什么）。
        var notApplied = MemoryScheduler.BuildDecision("d-na", "archive:x", "cr:archive:x:s", at, card,
            10, 0.9, ContextAdjustment.None(ContextMode.Active), contextCandidateDays: 12, finalIntervalDays: 10,
            finalDueAtUtc: at.AddDays(10), "alg", "lib", "par", "");
        Program.Check(notApplied.ContextDelta is null && notApplied.ContextCandidateIntervalDays is null,
            "Applied=false → context_delta 与 candidate 都为 null");

        // (c) 真的生效 → 两者都有值，且 final 必须等于「candidate 经 clamp 规则」的结果。
        var effective = MemoryScheduler.BuildDecision("d-ef", "archive:x", "cr:archive:x:s", at, card,
            10, 0.9, applied, contextCandidateDays: 12, finalIntervalDays: 12, finalDueAtUtc: at.AddDays(12),
            "alg", "lib", "par", "");
        Program.Check(effective.ContextDelta == 0.25 && effective.ContextCandidateIntervalDays == 12,
            "Δ 生效时 Δ 与 candidate 都落库");
        Program.CheckClose(
            MemoryScheduler.ResolveFinalInterval(effective.BaselineIntervalDays, effective.ContextCandidateIntervalDays, applied),
            effective.FinalIntervalDays, 1e-12,
            "final 是 candidate 按 clamp 规则算出来的结果（允许等于 baseline，但必须可解释）");
    }
}
