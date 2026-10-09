using System.Globalization;
using System.Text.Json;
using Lexi.Core;
using Microsoft.Data.Sqlite;

namespace Lexi.Tests;

/// <summary>
/// 个人 FSRS 参数**自动集成**的回归测试。全部断言都是"机制"层面的：
/// 门槛按真实训练目标统计、等待不消耗新增水位、训练/发布的生命周期代际、事务重放的原子性与
/// 撤销一致性、Context 与有效参数绑定、手动 due 覆盖的保护。
///
/// <para><b>合成数据只证明机制，不证明任何用户收益。</b></para>
///
/// <para>所有用例都在自己的临时目录里建库，绝不读写正式数据目录，也不依赖 <c>LEXI_DATA_DIR</c>。</para>
/// </summary>
internal static class OptimizerIntegrationTests
{
    private static readonly DateTime Anchor = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    internal static void Run()
    {
        // ① 门槛与统计口径
        GateIsCorrect();
        TargetsIgnoreAcquisitionAndSameDay();
        WaitingDoesNotConsumeWatermarkOrCooldown();
        WaitingWritesNoAttemptRow();
        TrainsWhenThresholdsMet();

        // ② 失败 / 取消 / 冻结基线
        NoImprovementKeepsLastGoodAndConsumesData();
        CancellationKeepsLastGoodWithoutConsumingData();
        HelperWithoutVerdictDoesNotConsumeWatermark();
        TrainingInputReadFailureDoesNotResetWatermark();
        SubprocessOutputBreachDoesNotConsumeWatermark();
        BaselineIsFrozenBeforeTraining();
        EpochChangeDuringTrainingInvalidatesCandidate();

        // ③ 发布：重放、原子性、撤销
        RestartDoesNotConsumeUnpublishedOrCancelledData();
        CorruptRestartReplayRetainsManualDuePreStates();
        CancellationBeforePublishContinuationPreventsCommit();
        SameTimestampTrainingPrefixUsesPersistedOrder();
        SameTimestampReplayUsesPersistedOrder();
        DefaultSourceCannotHideFiniteCorruption();
        PublishReplaysCardsAndPreStates();
        UndoAfterPublishRestoresNewBaseline();
        UndoRestampsParameterVersionOnReplay();
        PublishIsIdempotent();
        PublishRejectsUndoDuringTraining();
        PublishRejectsReviseDuringTraining();
        RejectedPublishDoesNotConsumeIncrement();
        PublishRollsBackAtomicallyOnFault();
        ActiveSinceIsTheActualPublishInstant();

        // ④ 重启与恢复
        RestartFindsCorruptModelAndReplaysConsistently();
        DefaultsBaselineNeedsNoReplay();
        ReconcileFailureIsNotReportedAsConsistent();
        StartupDoesNotExposeCoordinatorWhenInconsistent();
        InitializationFailureWithMixedCardsIsNotConsistent();

        // ⑤ 版本戳与不可回写
        ParameterVersionDerivesFromWeights();
        LegacyDueAndHistoricalDecisionsAreNotRewritten();

        // ⑥ Context 绑定与世代
        ContextBindsToEffectiveWeightsNotMetricsVersion();
        ContextDemotedOnBaselineChange();
        ContextCancelDuringBaselineChangeKeepsDemotion();
        ContextTrainingOnlyUsesSamplesAfterBaseline();

        // ⑦ 手动 due 覆盖
        ManualDueOverrideSurvivesPublishAndUndo();
        ManualDueOverrideIsRevisionScoped();
        ManualDueOverrideInvalidatesPublishSignature();

        // ⑧ 运行期生命周期（UI 上下文 / 代际）
        RuntimePublishCompletesUnderSynchronizationContext();
        RuntimeTrainingCompletionAfterRebindIsDropped();
        RuntimeRebindDuringPublishContinuationIsDropped();
        RuntimeDefersPublishWhileUndoable();
        HelperPathResolutionMatchesPackagingLayout();
    }

    // ==================================================================================
    // ① 门槛与统计口径
    // ==================================================================================

    private static void GateIsCorrect()
    {
        var options = new FsrsPersonalizationOptions();
        var now = Anchor;
        var cooldown = options.Cooldown;
        var reviews = options.MinimumReviews;
        var span = (double)options.MinimumSpanDays + 5;
        var fresh = options.MinimumNewReviews;

        bool Gate(DateTime at, DateTime? attempted, DateTime? failed, bool pending,
            int count, double spanDays, int newCount, bool basisChanged = false) =>
            FsrsPersonalization.ShouldTrain(at, attempted, failed, pending, count, spanDays, newCount, basisChanged, options);

        Program.Check(Gate(now, null, null, false, reviews, span, fresh), "四项门槛全满足 → 训练");
        Program.Check(!Gate(now, null, null, true, reviews, span, fresh), "已有在途任务 → 不训练");
        Program.Check(!Gate(now, null, null, false, reviews - 1, span, fresh), "目标数不足 → 不训练");
        Program.Check(!Gate(now, null, null, false, reviews, options.MinimumSpanDays - 1, fresh), "跨度不足 → 不训练");
        Program.Check(!Gate(now, null, null, false, reviews, span, fresh - 1), "新增不足 → 不训练");
        Program.Check(Gate(now, null, null, false, reviews, span, fresh - 1, basisChanged: true),
            "数据基础已变化 → 增量门槛豁免（否则被拒候选/撤销会把水位锁死）");
        Program.Check(!Gate(now, now - TimeSpan.FromMinutes(5), null, false, reviews, span, fresh), "冷却未到 → 不训练");
        Program.Check(Gate(now, now - cooldown, null, false, reviews, span, fresh), "冷却恰好走完 → 训练");
        Program.Check(!Gate(now, now - TimeSpan.FromDays(3), now - TimeSpan.FromMinutes(5), false, reviews, span, fresh),
            "上次失败且失败后冷却未过 → 不训练");
        Program.Check(!Gate(now, null, now.AddMinutes(5), false, reviews, span, fresh), "时钟回拨 → 保守不训练");
        Program.Check(!Gate(now, null, null, false, reviews, double.NaN, fresh), "跨度非有限 → 不训练");
        Program.Check(!FsrsPersonalization.ShouldTrain(now, null, null, false, reviews, span, fresh,
            new FsrsPersonalizationOptions { Enabled = false }), "总开关关闭 → 不训练");
    }

    private static void TargetsIgnoreAcquisitionAndSameDay()
    {
        var w = "archive:w1";
        // 首次学习聚合（acquisition）：只做初始化，绝不是带正间隔的记忆留存目标。
        var acquisition = new List<CanonicalHistoryRow>
        {
            Row(w, "s0", CanonicalOrigin.FirstLearnAggregate, Anchor, 0, StudyRating.Known),
        };
        var stats0 = FsrsPersonalization.CountTrainingTargets(acquisition);
        Program.Check(stats0.Count == 0, $"只有 acquisition → 0 个真实目标（实际 {stats0.Count}）");

        // 400 条 canonical，但全部是"同一天内的状态更新"（elapsed whole days == 0）。
        var sameDay = new List<CanonicalHistoryRow> { Row(w, "s0", CanonicalOrigin.FirstLearnAggregate, Anchor, 0, StudyRating.Known) };
        for (var i = 1; i <= 400; i++)
            sameDay.Add(Row(w, $"s{i}", CanonicalOrigin.FirstRetrieval, Anchor.AddMinutes(i), 0, StudyRating.Known));
        var stats1 = FsrsPersonalization.CountTrainingTargets(sameDay);
        Program.Check(stats1.Count == 0,
            $"400 条同日状态更新 → 0 个真实目标（实际 {stats1.Count}）——门槛不能按 history.Count 放行");

        // 真正跨日的 FirstRetrieval 才算目标。
        var acrossDays = new List<CanonicalHistoryRow> { Row(w, "s0", CanonicalOrigin.FirstLearnAggregate, Anchor, 0, StudyRating.Known) };
        for (var i = 1; i <= 6; i++)
            acrossDays.Add(Row(w, $"s{i}", CanonicalOrigin.FirstRetrieval, Anchor.AddDays(i * 3), 3, StudyRating.Known));
        var stats2 = FsrsPersonalization.CountTrainingTargets(acrossDays);
        Program.Check(stats2.Count == 6, $"6 条跨日真实复习 → 6 个目标（实际 {stats2.Count}）");
        Program.Check(Math.Abs(stats2.SpanDays - 15) < 1e-9, $"目标跨度为 15 天（实际 {stats2.SpanDays}）");

        // 端到端：400 条同日 canonical 不该放行训练。
        using var box = new Sandbox();
        var service = NewService(box, out _, out var optimizer,
            options: TestOptions(minimumReviews: 400, minimumSpanDays: 5, minimumNewReviews: 1));
        WriteSameDaySeries(box.Store, w, Anchor.AddDays(-2), 400);
        var result = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(result.Outcome == FsrsParameterTrainingOutcome.WaitingForData,
            $"400 条 canonical 但 0 个真实目标 → 只收集（实际 {result.Outcome}：{result.Detail}）");
        Program.Check(optimizer.Calls == 0, "目标数不足时根本不调用训练器");
    }

    /// <summary>
    /// 队长点名的反例：正常用户每天新增约 20 条目标，累计到门槛必须训练；
    /// 中途重启也必须能训练。旧实现每个轮末都把水位重置成"当前条数"，每轮新增 &lt; 新增门槛时
    /// 差值永远清零，于是这类用户**永远训练不起来**。
    /// </summary>
    private static void WaitingDoesNotConsumeWatermarkOrCooldown()
    {
        using var box = new Sandbox();
        // 门槛故意设成：数据量门槛（20）先被满足，唯一卡住训练的是"新增 50 条"这一项——
        // 这正是"等待是否消耗新增水位"唯一可观测的形态（数据量不足会被廉价前置闸提前拦掉）。
        var options = TestOptions(minimumReviews: 20, minimumSpanDays: 10, minimumNewReviews: 50, cooldownMinutes: 0);
        var service = NewService(box, out var holder, out _, options: options);
        var optimizer2 = new FakeOptimizer { Handler = null }; // 一律"未获提升"：只关心门槛何时放行

        // 起点放足：训练历史以 reviewed_at_utc < now 为严格上界，落在"未来"的 canonical 不会进训练集。
        var day = Anchor.AddDays(-90);
        var outcomes = new List<FsrsParameterTrainingOutcome>();
        for (var round = 0; round < 3; round++)
        {
            // 每轮追加 21 条跨日复习（模拟"每天约 20 条"）。第 1 条是初始化点，不计入真实目标，
            // 因此 3 轮之后是 63 条 canonical / 62 个目标，刚好越过 60 的门槛。
            for (var i = 0; i < 21; i++)
                AppendCanonical(box.Store, "archive:w1", $"r{round}-{i}", day.AddDays(round * 21 + i + 1),
                    round == 0 && i == 0 ? CanonicalOrigin.FirstLearnAggregate : CanonicalOrigin.FirstRetrieval);
            // 每轮都重启一次服务：水位与冷却必须从库里的**真实尝试**恢复，等待类结果不落任何记账。
            service = NewServiceWithOptimizer(box, out holder, optimizer2, options);
            outcomes.Add(service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult().Outcome);
        }
        Program.Check(outcomes[0] == FsrsParameterTrainingOutcome.WaitingForData
            && outcomes[1] == FsrsParameterTrainingOutcome.WaitingForData,
            $"20 / 41 个真实目标时都只收集（实际 {string.Join(", ", outcomes)}）");
        Program.Check(optimizer2.Calls == 1,
            $"第 3 轮攒到 62 个真实目标时**必须**训练（训练器调用 {optimizer2.Calls} 次）——"
            + "若等待也推进水位，每轮差值都被清零，这类用户永远训不起来");
    }

    private static void WaitingWritesNoAttemptRow()
    {
        using var box = new Sandbox();
        var service = NewService(box, out _, out _,
            options: TestOptions(minimumReviews: 50, minimumSpanDays: 30));
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-5), 5, 1, StudyRating.Known);

        var result = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(result.Outcome == FsrsParameterTrainingOutcome.WaitingForData,
            $"数据不足 → WaitingForData（实际 {result.Outcome}）");
        Program.Check(service.LastAttemptUtc is null,
            $"等待数据不推进冷却时钟（实际 {service.LastAttemptUtc:O}）");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM personalization_models WHERE model_kind = '{SchedulingConfig.FsrsParameterModelKind}'") == 0,
            "等待数据**不写任何记账行**（跨重启也不会把它当成一次尝试）");

        // 对照：**真实**尝试必须留下记账行，否则"跨重启的冷却与水位"根本无从恢复。
        var trained = NewService(box, out _, out _, weights: CandidateWeights(),
            options: TestOptions(minimumReviews: 3, minimumSpanDays: 2));
        var trainedResult = trained.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(trainedResult.Outcome == FsrsParameterTrainingOutcome.Trained, "对照：训练成功");
        var attemptRows = ScalarInt(box.DbPath,
            $"SELECT COUNT(*) FROM personalization_models WHERE model_kind = '{SchedulingConfig.FsrsParameterModelKind}' "
            + $"AND json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterRecordKindKey}') = 'attempt'");
        Program.Check(attemptRows == 1, $"真实尝试留下记账行（实际 {attemptRows}）");
        var reread = NewService(box, out _, out _, options: TestOptions(minimumReviews: 3, minimumSpanDays: 2));
        Program.Check(reread.LastAttemptUtc == Anchor && reread.ReviewsAtLastAttempt == 0,
            $"重启保留真实冷却，但未发布候选不消费数据水位（{reread.LastAttemptUtc:O} / {reread.ReviewsAtLastAttempt}）");
        Program.Check(!service.CooldownBlocks(Anchor, false), "等待之后不受冷却约束（下一次轮末仍可评估）");
    }

    private static void TrainsWhenThresholdsMet()
    {
        using var box = new Sandbox();
        var service = NewService(box, out var holder, out var optimizer, weights: CandidateWeights());
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 12, 2, StudyRating.Known);

        var result = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(result.Outcome == FsrsParameterTrainingOutcome.Trained,
            $"门槛满足且训练器接受 → Trained（实际 {result.Outcome}：{result.Detail}）");
        Program.Check(result.Candidate is { ReviewCount: 11 },
            $"候选记的是**真实目标数** 11（12 条 canonical 减去首条初始化；实际 {result.Candidate?.ReviewCount}）");
        Program.Check(optimizer.LastHistory is { Count: 12 } && optimizer.LastHistory!.All(r => !string.IsNullOrEmpty(r.CanonicalId)),
            "训练器拿到完整历史并保留 CanonicalId");
        Program.Check(holder.Epoch == 0, "训练阶段不推进 epoch（换版只发生在事务发布成功之后）");
    }

    // ==================================================================================
    // ② 失败 / 取消 / 冻结基线
    // ==================================================================================

    private static void NoImprovementKeepsLastGoodAndConsumesData()
    {
        using var box = new Sandbox();
        var service = NewService(box, out var holder, out _, weights: CandidateWeights(), reject: true);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 12, 2, StudyRating.Known);

        var result = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(result.Outcome == FsrsParameterTrainingOutcome.NoImprovement,
            $"训练器拒绝 → NoImprovement（实际 {result.Outcome}）");
        Program.Check(holder.Current.Source == Fsrs6Weights.SourceDefaults && holder.Epoch == 0,
            "被拒绝时保留 last-good / defaults");
        Program.Check(service.ReviewsAtLastAttempt == 11,
            $"明确拒绝会推进水位（同一批数据不再重复评估；实际 {service.ReviewsAtLastAttempt}）");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM personalization_models WHERE model_kind = '{SchedulingConfig.FsrsParameterModelKind}' "
                + $"AND json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterRecordKindKey}') = 'params'") == 0,
            "被拒绝时不写任何参数行");
    }

    private static void CancellationKeepsLastGoodWithoutConsumingData()
    {
        using var box = new Sandbox();
        var service = NewService(box, out var holder, out var optimizer, weights: CandidateWeights());
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 12, 2, StudyRating.Known);
        optimizer.Handler = (_, token) =>
        {
            using var wait = token.WaitHandle;
            wait.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            return System.Threading.Tasks.Task.FromResult(CandidateWeights());
        };
        using var cts = new CancellationTokenSource();
        var pending = service.TrainAsync(Anchor, hasPendingTask: false, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        var result = pending.GetAwaiter().GetResult();

        Program.Check(result.Outcome == FsrsParameterTrainingOutcome.SafeFallback,
            $"取消 → SafeFallback（实际 {result.Outcome}）");
        Program.Check(holder.Current.Source == Fsrs6Weights.SourceDefaults && holder.Epoch == 0,
            "取消时保留 last-good / defaults");
        Program.Check(service.CooldownBlocks(Anchor.AddMinutes(5), false), "取消记冷却（不会每轮重启子进程）");
        Program.Check(service.ReviewsAtLastAttempt == 0,
            $"取消**不推进水位**（瞬时故障不该逼用户再攒 N 条；实际 {service.ReviewsAtLastAttempt}）");
    }

    /// <summary>
    /// **"helper 没产出裁决"与"裁决为拒绝"必须分开。**
    /// 前者（进程没起来 / 退出码不是裁决码 / 响应损坏）这批数据从未被评估过，只记冷却、**不推进**新增量水位；
    /// 后者（协议裁决码 exit 3 = 训练完成但未超越默认权重）才推进。
    /// 反例：旧实现把两者都当成"已被评估"，一次纯粹的进程崩溃就会吃掉用户的新增量水位，
    /// 逼他再攒 N 条新目标才允许重训。
    /// </summary>
    private static void HelperWithoutVerdictDoesNotConsumeWatermark()
    {
        using var box = new Sandbox();
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 12, 2, StudyRating.Known);
        var scriptPath = Path.Combine(box.Dir, $"mock-helper-{Guid.NewGuid():N}.sh");
        try
        {
            var holder = new SchedulerWeights();
            var optimizer = new FsrsParameterOptimizer(scriptPath, SmallBudgetOptimizerOptions());
            var service = new FsrsPersonalization(box.Store, holder, optimizer, TestOptions());

            // ① 崩溃（退出码 139，既不是输入拒绝 1/2，也不是裁决码 3）
            WriteMockHelper(scriptPath, "#!/bin/sh\nread -r line\necho 'boom' >&2\nexit 139\n");
            var crashed = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
            Program.Check(crashed.Outcome == FsrsParameterTrainingOutcome.SafeFallback,
                $"helper 未产出裁决（崩溃）→ SafeFallback（实际 {crashed.Outcome}）");
            Program.Check(holder.Current.Source == Fsrs6Weights.SourceDefaults && holder.Epoch == 0,
                "未产出裁决时保留 last-good / defaults，不换版");
            Program.Check(service.CooldownBlocks(Anchor.AddMinutes(5), false),
                "未产出裁决仍记冷却（不会每轮重启子进程）");
            Program.Check(service.ReviewsAtLastAttempt == 0,
                $"helper **从未评估过**这批数据 → 不推进新增量水位（实际 {service.ReviewsAtLastAttempt}）");

            // ② 冷却过后同一批数据仍可重训（水位没被吃掉）
            WriteMockHelper(scriptPath, "#!/bin/sh\nread -r line\necho 'did not evolve' >&2\nexit 3\n");
            var rejected = service.TrainAsync(Anchor.AddMinutes(61), hasPendingTask: false).GetAwaiter().GetResult();
            Program.Check(rejected.Outcome == FsrsParameterTrainingOutcome.NoImprovement,
                $"协议裁决码 3（训练完成但未超越默认权重）→ NoImprovement（实际 {rejected.Outcome}）");
            Program.Check(service.ReviewsAtLastAttempt == 11,
                $"**真正评估过**的拒绝才推进水位（实际 {service.ReviewsAtLastAttempt}）");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 子进程**输出超字节预算**属于"helper 没产出可信裁决"（响应被截断/污染），不是"裁决为拒绝"。
    /// 反例：旧实现从这一路径抛基类异常，被当成"已评估"而推进水位——一次纯粹的输出异常就吃掉配额。
    /// </summary>
    private static void SubprocessOutputBreachDoesNotConsumeWatermark()
    {
        using var box = new Sandbox();
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 12, 2, StudyRating.Known);
        var scriptPath = Path.Combine(box.Dir, $"mock-helper-flood-{Guid.NewGuid():N}.sh");
        try
        {
            // 输出远超 MaxStdoutBytes，触发 ReadStreamWithLimitAsync 的预算守卫。
            WriteMockHelper(scriptPath, "#!/bin/sh\nhead -c 131072 /dev/zero | tr '\\0' 'A'\nexit 0\n");
            var options = SmallBudgetOptimizerOptions();
            options.MaxStdoutBytes = 4096;
            var service = new FsrsPersonalization(box.Store, new SchedulerWeights(),
                new FsrsParameterOptimizer(scriptPath, options), TestOptions());

            var result = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
            Program.Check(result.Outcome == FsrsParameterTrainingOutcome.SafeFallback,
                $"管道输出超预算 → SafeFallback（实际 {result.Outcome}）");
            Program.Check(service.ReviewsAtLastAttempt == 0,
                $"输出超预算**不是**裁决，不得推进水位（实际 {service.ReviewsAtLastAttempt}）");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 训练输入读取失败是**瞬时**故障：冷却照记、水位一动不动。
    /// 反例：旧实现用缺省 <c>advanceWatermark:true</c> + <c>targetCount 0</c> 把水位**清零**并落库，
    /// 于是"新增量"门槛在下次启动后形同失效（每次轮末都能重训），与本类自述的瞬时失败语义直接矛盾。
    /// </summary>
    private static void TrainingInputReadFailureDoesNotResetWatermark()
    {
        using var box = new Sandbox();
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 12, 2, StudyRating.Known);
        var wrapped = new ToggleableHistoryStore(box.Store);
        var holder = new SchedulerWeights();
        var service = new FsrsPersonalization(wrapped, holder,
            new FakeOptimizer { Handler = null }, TestOptions());

        // ① 正常路径先把水位推到 11（训练器明确拒绝 = 数据被评估过）
        var rejected = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(rejected.Outcome == FsrsParameterTrainingOutcome.NoImprovement
                && service.ReviewsAtLastAttempt == 11,
            $"前提：先由一次真实评估把水位推到 11（实际 {rejected.Outcome}/{service.ReviewsAtLastAttempt}）");

        // ② 读取失败：只记冷却，水位保持 11
        wrapped.FailHistoryReads = true;
        var failed = service.TrainAsync(Anchor.AddMinutes(61), hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(failed.Outcome == FsrsParameterTrainingOutcome.SafeFallback,
            $"读取失败 → SafeFallback（实际 {failed.Outcome}）");
        Program.Check(service.ReviewsAtLastAttempt == 11,
            $"读取失败**不得**把水位清零（瞬时故障水位不动；实际 {service.ReviewsAtLastAttempt}）");
        Program.Check(service.CooldownBlocks(Anchor.AddMinutes(62), false), "读取失败仍记冷却");
    }

    /// <summary>受控装载/卸载"历史读取必失败"的替身，其余全部透传。</summary>
    private sealed class ToggleableHistoryStore : WrapperStore
    {
        public ToggleableHistoryStore(ILearningMemoryStore inner) : base(inner) { }

        public bool FailHistoryReads { get; set; }

        public override IReadOnlyList<CanonicalHistoryRow> LoadCanonicalHistory(
            DateTime? fromUtc, DateTime beforeUtc, string? wordKey)
            => FailHistoryReads
                ? throw new IOException("（测试注入）训练输入读取失败。")
                : Inner.LoadCanonicalHistory(fromUtc, beforeUtc, wordKey);
    }

    /// <summary>让真实子进程路径能跑通的小预算配置（门槛调低，避免在 pre-flight 就被挡掉）。</summary>
    private static FsrsOptimizerOptions SmallBudgetOptimizerOptions() => new()
    {
        MinimumTrainReviews = 1,
        MinimumValidationReviews = 1,
        MinimumPositiveReviews = 1,
        MinimumNegativeReviews = 1,
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static void WriteMockHelper(string path, string body)
    {
        File.WriteAllText(path, body);
        if (OperatingSystem.IsWindows()) { WindowsOptimizerFixture.Materialize(path); return; }
        // 测试宿主只跑在 macOS；CA1416 只针对 Windows 目标报警，这里显式抑制。
#pragma warning disable CA1416
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
    }

    /// <summary>对照基线必须在 <c>await</c> **之前**冻结：训练期间换版不能改变"和谁比"。</summary>
    private static void BaselineIsFrozenBeforeTraining()
    {
        using var box = new Sandbox();
        var active = CandidateWeights();
        var holder = new SchedulerWeights(active);
        var other = CandidateWeights(w0: 0.9);
        var optimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            HelperPath = Path.Combine(Path.GetTempPath(), "definitely-missing-fsrs-optimizer"),
            Timeout = TimeSpan.FromSeconds(5),
        });
        var service = new FsrsPersonalization(box.Store, holder, optimizer, TestOptions(minimumReviews: 20));
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-80), 40, 2, StudyRating.Known);

        var result = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(optimizer.Options.BaselineWeights is not null && optimizer.Options.BaselineWeights!.ValueEquals(active),
            "对照基线 = 训练开始那一刻生效的权重");
        Program.Check(optimizer.Options.CompletedAtCutoff == Anchor,
            "同时把双截止交给训练器（防止切分后定稿的回答泄漏进训练目标）");
        Program.Check(result.Outcome == FsrsParameterTrainingOutcome.SafeFallback,
            $"helper 缺失被如实报成失败而不是伪造成功（实际 {result.Outcome}）");
        Program.Check(holder.Epoch == 0 && !holder.Current.ValueEquals(other), "helper 缺失不改变内存权重");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM personalization_models WHERE model_kind = '{SchedulingConfig.FsrsParameterModelKind}' "
                + $"AND json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterRecordKindKey}') = 'params'") == 0,
            "helper 缺失时绝不写参数行（不伪造成功）");
    }

    /// <summary>训练期间发生另一次发布 → 本次候选的对照与 epoch 都已过时，必须作废。</summary>
    private static void EpochChangeDuringTrainingInvalidatesCandidate()
    {
        using var box = new Sandbox();
        var service = NewService(box, out var holder, out var optimizer, weights: CandidateWeights());
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 12, 2, StudyRating.Known);
        var intruder = CandidateWeights(w0: 0.85);
        optimizer.Handler = (_, _) =>
        {
            // 模拟"训练在途时另一次发布完成"。
            holder.Publish(intruder);
            return System.Threading.Tasks.Task.FromResult(CandidateWeights());
        };

        var result = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(result.Outcome == FsrsParameterTrainingOutcome.SafeFallback,
            $"训练期间换版 → 候选作废（实际 {result.Outcome}：{result.Detail}）");
        Program.Check(result.Candidate is null, "作废时不产出候选");
        Program.Check(holder.Current.ValueEquals(intruder), "内存权重仍是那次发布的（没有被旧候选覆盖）");
    }

    // ==================================================================================
    // ③ 发布：重放、原子性、撤销
    // ==================================================================================

    private static void PublishReplaysCardsAndPreStates()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out var holder, out _, weights: candidate);
        var series = WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 8, 7, StudyRating.Known);
        WriteSeries(box.Store, "archive:w2", Anchor.AddDays(-40), 4, 9, StudyRating.Unsure);

        var oldCard = box.Store.GetCard("archive:w1")!;
        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(outcome.Outcome == FsrsParameterTrainingOutcome.Trained, "训练得到候选");
        var published = service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult();
        Program.Check(published.Applied, $"发布成功（{published.Detail}）");
        Program.Check(published.WordsReplayed == 2 && published.CanonicalsRewritten == 12,
            $"两个词 / 12 条 canonical 被重写（实际 {published.WordsReplayed} / {published.CanonicalsRewritten}）");

        var version = FsrsPersonalization.ParameterVersionOf(candidate);
        Program.Check(holder.Current.ValueEquals(candidate) && holder.Epoch == 1, "提交成功后内存权重与 epoch 才跟上");
        Program.Check(service.ActiveParameterVersion == version, "内存里的活跃参数版本跟着换");

        var newCard = box.Store.GetCard("archive:w1")!;
        var expected = Recompute(series, candidate);
        Program.Check(Math.Abs(newCard.Stability - expected.Stability) < 1e-12
            && Math.Abs(newCard.Difficulty - expected.Difficulty) < 1e-12, "卡片 D/S 与重放结果逐位一致");
        Program.Check(Math.Abs(newCard.Stability - oldCard.Stability) > 1e-9, "重放确实改变了 D/S（断言非空转）");
        Program.Check(newCard.FsrsParameterVersion == version, "卡片版本戳换成新参数版本");
        Program.Check(newCard.Reps == series.Count, "reps 等于重放过的 canonical 条数");

        var maxSeq = ScalarInt(box.DbPath,
            "SELECT MAX(rowid) FROM canonical_reviews WHERE word_key = 'archive:w1' AND invalidated = 0");
        Program.Check(ScalarInt(box.DbPath,
                "SELECT last_applied_canonical_seq FROM fsrs_cards WHERE word_key = 'archive:w1'") == maxSeq,
            "幂等水位被重置为该词有效 canonical 的最大 rowid");

        var rewritten = ScalarInt(box.DbPath,
            "SELECT COUNT(*) FROM canonical_reviews WHERE word_key = 'archive:w1' AND invalidated = 0 AND pre_state_existed = 1");
        Program.Check(rewritten == 7, $"该词后 7 条 canonical 都带上了重放后的 pre-state（实际 {rewritten}）");
        var second = NextPreState(series, candidate, series.Count - 2);
        Program.Check(Math.Abs(ScalarDouble(box.DbPath,
                $"SELECT stability FROM canonical_reviews WHERE canonical_id = '{series[^1].CanonicalId}'")
            - second.Stability) < 1e-12, "最后一条 canonical 的 pre-state = 新基线下应用它之前的那张卡");

        Program.Check(ScalarText(box.DbPath,
                $"SELECT model_kind FROM personalization_models WHERE model_version = '{version}'") == SchedulingConfig.FsrsParameterModelKind,
            "参数行以 model_kind='fsrs_params' 落库");
        Program.Check(!box.Store.HasCardsOutsideParameterVersion(version), "发布后卡片与参数版本一致");
    }

    private static void RestartDoesNotConsumeUnpublishedOrCancelledData()
    {
        foreach (var cancelled in new[] { false, true })
        {
            using var box = new Sandbox();
            var options = TestOptions(minimumReviews: 5, minimumSpanDays: 5, minimumNewReviews: 5);
            var service = NewService(box, out _, out var optimizer, options: options);
            WriteSeries(box.Store, "archive:restart-data", Anchor.AddDays(-60), 8, 7, StudyRating.Known);
            using var cts = new CancellationTokenSource();
            if (cancelled) optimizer.Handler = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CandidateWeights();
            };
            var initialTask = service.TrainAsync(Anchor, false, cts.Token);
            if (cancelled) cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            var initial = initialTask.GetAwaiter().GetResult();
            Program.Check(cancelled ? initial.Outcome == FsrsParameterTrainingOutcome.SafeFallback : initial.Candidate is not null,
                "首次取消/未发布候选真实写入不推进水位的attempt");
            box.Reopen();
            var restarted = NewService(box, out _, out _, options: options);
            var next = restarted.TrainAsync(Anchor.AddHours(2), false).GetAwaiter().GetResult();
            Program.Check(next.Candidate is not null,
                "取消/未发布候选重启后同批数据冷却结束仍可训练，不要求额外新增5条");
        }
    }

    private static void CorruptRestartReplayRetainsManualDuePreStates()
    {
        using var box = new Sandbox();
        var date = Anchor.Date.AddDays(9).ToString("yyyy-MM-dd");
        var id = box.ImportLegacyWord("restartmanual", date);
        var key = WordKey.Archive(ScalarText(box.DbPath, $"SELECT uuid FROM word_archives WHERE word_id={id}")).Key;
        WriteSeries(box.Store, key, Anchor.AddDays(-60), 5, 7, StudyRating.Known);
        Execute(box.DbPath, $"UPDATE words SET next_review_date='{date}' WHERE id={id};");
        box.SyncManualReviewDue(id);
        var due = LocalDateToUtc(date);
        var last = WriteCanonical(box.Store, key, "next", Anchor.AddDays(-1), StudyRating.Known,
            CanonicalOrigin.FirstRetrieval, true);
        var service = NewService(box, out _, out _, weights: CandidateWeights());
        var candidate = service.TrainAsync(Anchor, false).GetAwaiter().GetResult().Candidate!;
        Program.Check(service.PublishAsync(candidate).GetAwaiter().GetResult().Applied, "带真实manual overlay先发布个人参数");
        Execute(box.DbPath, $"UPDATE personalization_models SET coefficients_json='{{broken' WHERE model_version='{candidate.ParameterVersion}';");
        box.Reopen();
        var startup = FsrsPersonalizationStartup.Create(box.Store, new SchedulerWeights(), _ => new FakeOptimizer(), TestOptions());
        Program.Check(startup.Consistent && startup.Service!.Weights.Current.ValueEquals(Fsrs6Weights.Defaults),
            "真实关闭/重启损坏模型回退defaults并一致重放");
        Program.Check(ScalarText(box.DbPath, $"SELECT next_review_at_utc FROM canonical_reviews WHERE canonical_id='{last.CanonicalId}'") == due.ToString("O"),
            "恢复重放仍把manual覆盖写入后继canonical的pre-state due");
        Program.Check(box.Store.InvalidateCanonical(last.CanonicalId, Anchor, "test")
            && box.Store.GetCard(key)!.NextReviewAtUtc == due, "损坏重启后的undo保留真实手动日期");
    }

    private static void SameTimestampTrainingPrefixUsesPersistedOrder()
    {
        foreach (var reverseCompletion in new[] { false, true })
        {
            using var box = new Sandbox();
            var t = Anchor.AddDays(-60);
            const string word = "archive:tie-prefix";
            WriteCanonical(box.Store, word, "init", t.AddDays(-3), StudyRating.Known,
                CanonicalOrigin.FirstLearnAggregate, false);
            WriteCanonical(box.Store, word, reverseCompletion ? "a-first" : "z-first", t, StudyRating.Unsure,
                CanonicalOrigin.FirstRetrieval, true, reverseCompletion ? t.AddMinutes(2) : t.AddMinutes(1));
            WriteCanonical(box.Store, word, reverseCompletion ? "z-second" : "a-second", t, StudyRating.Forgot,
                CanonicalOrigin.FirstRetrieval, true, t.AddMinutes(1));
            WriteCanonical(box.Store, word, "future", t.AddDays(3), StudyRating.Known,
                CanonicalOrigin.FirstRetrieval, true);
            for (var i = 0; i < 15; i++)
                WriteSeries(box.Store, $"archive:aux{i}", Anchor.AddDays(-100).AddMinutes(i * 20), 6, 15,
                    i % 3 == 0 ? StudyRating.Forgot : StudyRating.Known);
            FsrsOptimizerRequest? captured = null;
            var optimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions
            {
                MinimumTrainReviews = 5, MinimumValidationReviews = 5,
                MinimumPositiveReviews = 1, MinimumNegativeReviews = 1,
                CompletedAtCutoff = Anchor,
            })
            {
                TestInvoker = (request, _) =>
                {
                    captured = request;
                    throw new FsrsOptimizationException("prefix captured; no training claimed");
                },
            };
            try { optimizer.OptimizeAsync(FsrsPersonalization.BuildOptimizerInput(
                box.Store.LoadCanonicalHistory(null, Anchor, null))).GetAwaiter().GetResult(); }
            catch (FsrsOptimizationException) { }
            Program.Check(captured is not null && captured.Items.Any(item => item.Reviews.Count == 4
                && item.Reviews.Select(r => r.Rating).SequenceEqual(new[] { 3, 2, 1, 3 })
                && item.Reviews.Select(r => r.DeltaT).SequenceEqual(new uint[] { 0, 3, 0, 3 })),
                "真实 store 同刻 Hard/Again 后未来 retrieval 训练前缀遵循 rowid，非Id/Completed");
        }
    }

    private static void SameTimestampReplayUsesPersistedOrder()
    {
        foreach (var reverseCompletion in new[] { false, true })
        {
            using var box = new Sandbox();
            const string word = "archive:tie";
            var t = Anchor.AddDays(-9);
            var series = new List<Written>
            {
                WriteCanonical(box.Store, word, "init", t.AddDays(-3), StudyRating.Known,
                    CanonicalOrigin.FirstLearnAggregate, false),
                WriteCanonical(box.Store, word, reverseCompletion ? "a-first" : "z-first", t,
                    StudyRating.Unsure, CanonicalOrigin.FirstRetrieval, true,
                    reverseCompletion ? t.AddMinutes(2) : t.AddMinutes(1), revision: 7),
                WriteCanonical(box.Store, word, reverseCompletion ? "z-second" : "a-second", t,
                    StudyRating.Forgot, CanonicalOrigin.FirstRetrieval, true, t.AddMinutes(1)),
            };
            var rows = box.Store.LoadCanonicalHistory(null, Anchor, word);
            Program.Check(rows[1].CanonicalSequence > 0 && rows[2].CanonicalSequence > rows[1].CanonicalSequence,
                "真实 store 返回正 rowid，保持同刻提交次序");
            var inputs = FsrsPersonalization.BuildOptimizerInput(rows);
            Program.Check(inputs[1].CanonicalSequence == rows[1].CanonicalSequence
                && inputs[1].Revision == rows[1].Revision && rows[1].Revision == 7, "生产转换保留 rowid 与非零 revision");
            var candidate = CandidateWeights();
            var expected = Recompute(series, candidate);
            var reversed = Recompute(new List<Written> { series[0], series[2], series[1] }, candidate);
            Program.Check(Math.Abs(expected.Stability - reversed.Stability) > 1e-9
                || Math.Abs(expected.Difficulty - reversed.Difficulty) > 1e-9,
                "同刻反转 Hard/Again 确实改变 D/S，负对照有辨识力");
            var service = NewService(box, out _, out _, weights: candidate,
                options: TestOptions(minimumReviews: 1, minimumSpanDays: 0));
            var outcome = service.TrainAsync(Anchor, false).GetAwaiter().GetResult();
            Program.Check(outcome.Candidate is not null, "同刻真实夹具得到测试候选");
            Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "同刻夹具真实事务发布");
            var card = box.Store.GetCard(word)!;
            Program.Check(Math.Abs(card.Stability - expected.Stability) < 1e-12
                && Math.Abs(card.Difficulty - expected.Difficulty) < 1e-12, "同刻发布 D/S 遵循 rowid，不按 Id/Completed 重排");
            var version = FsrsPersonalization.ParameterVersionOf(candidate);
            Program.Check(card.FsrsParameterVersion == version, "同刻发布参数版本正确");
            Program.Check(box.Store.InvalidateCanonical(series[2].CanonicalId, Anchor, "test"), "同刻最大 rowid 可撤销");
            var firstState = Recompute(series.Take(2).ToList(), candidate);
            var restored = box.Store.GetCard(word)!;
            var due = t.AddDays(Math.Max(1.0, new Fsrs6Scheduler(candidate)
                .Review(Recompute(series.Take(1).ToList(), candidate), StudyRating.Unsure, t).BaselineIntervalDays));
            Program.Check(Math.Abs(restored.Stability - firstState.Stability) < 1e-12
                && Math.Abs(restored.Difficulty - firstState.Difficulty) < 1e-12
                && restored.NextReviewAtUtc == due && restored.FsrsParameterVersion == version,
                "撤销 second 恢复候选 first 的 D/S、due、版本");
            Program.Check(ScalarInt(box.DbPath,
                "SELECT last_applied_canonical_seq FROM fsrs_cards WHERE word_key='archive:tie'") == rows[1].CanonicalSequence,
                "撤销 second 回退到 first rowid 水位");
            Program.Check(box.Store.InvalidateCanonical(series[1].CanonicalId, Anchor, "test"), "同刻 first 可连续撤销");
            var initial = Recompute(series.Take(1).ToList(), candidate);
            Program.Check(Math.Abs(box.Store.GetCard(word)!.Stability - initial.Stability) < 1e-12,
                "连续撤销 first 恢复候选初始化状态");
        }
    }

    private static void DefaultSourceCannotHideFiniteCorruption()
    {
        using var box = new Sandbox();
        var values = Fsrs6Weights.Defaults.ToArray();
        values[0] += 0.1;
        var corrupt = Fsrs6Weights.Create(values, Fsrs6Weights.SourceDefaults);
        Program.Check(FsrsPersonalization.ParameterVersionOf(corrupt)
            != SchedulingConfig.FsrsParameterVersionPrefix, "default source 合法变值必须改变版本戳");
        WriteSeries(box.Store, "archive:finite", Anchor.AddDays(-20), 4, 3, StudyRating.Known);
        box.Store.SavePersonalizationModel(new PersonalizationModelState
        {
            ModelVersion = "finite-corrupt-default",
            TrainedAtUtc = Anchor, TrainCutoffUtc = Anchor, Status = ContextMode.Active,
            CoefficientsJson = corrupt.ToJson(), ScalerJson = "{}",
            MetricsJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [SchedulingConfig.FsrsParameterRecordKindKey] = SchedulingConfig.FsrsParameterRecordKindParams,
                [SchedulingConfig.FsrsParameterVersionMetricKey] = SchedulingConfig.FsrsParameterVersionPrefix,
            }),
        }, SchedulingConfig.FsrsParameterModelKind);
        var restored = NewService(box, out var holder, out _);
        Program.Check(holder.Current.ValueEquals(Fsrs6Weights.Defaults), "有限且合法范围内的 default-source 腐坏行也回退官方 defaults");
        Program.Check(restored.Reconcile().Consistent, "有限腐坏回退后卡片一致");
    }

    private static void CancellationBeforePublishContinuationPreventsCommit()
    {
        using var box = new Sandbox();
        using var cts = new CancellationTokenSource();
        var gated = new ReplayBarrierStore(box.Store);
        var holder = new SchedulerWeights();
        var service = new FsrsPersonalization(gated, holder,
            new FakeOptimizer { Handler = (_, _) => Task.FromResult(CandidateWeights()) }, TestOptions());
        service.Restore();
        WriteSeries(box.Store, "archive:cancel", Anchor.AddDays(-60), 8, 7, StudyRating.Known);
        var candidate = service.TrainAsync(Anchor, false).GetAwaiter().GetResult().Candidate!;
        var before = box.Store.GetCard("archive:cancel")!.Clone();
        var previous = SynchronizationContext.Current;
        Task<FsrsParameterPublishResult> publish;
        try
        {
            gated.GateEnabled = true;
            SynchronizationContext.SetSynchronizationContext(new CancelBeforeContinuationContext(cts));
            publish = service.PublishAsync(candidate, cts.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Program.Check(gated.Entered.Wait(TimeSpan.FromSeconds(10)), "真实后台重放已开始，尚未进入发布续体");
        gated.Release.Set();
        var result = publish.GetAwaiter().GetResult();
        Program.Check(cts.IsCancellationRequested && !result.Applied && gated.PublishCalls == 0,
            "重放完成后续体提交前取消，不调用store事务");
        var after = box.Store.GetCard("archive:cancel")!;
        Program.Check(holder.Current.ValueEquals(Fsrs6Weights.Defaults) && holder.Epoch == 0
            && after.Stability == before.Stability && after.Difficulty == before.Difficulty
            && after.FsrsParameterVersion == before.FsrsParameterVersion,
            "途中取消保持 last-good 权重、D/S、版本");
    }

    private sealed class CancelBeforeContinuationContext(CancellationTokenSource cts) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            cts.Cancel();
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }

    private sealed class ReplayBarrierStore(ILearningMemoryStore inner) : WrapperStore(inner)
    {
        public bool GateEnabled;
        public int PublishCalls;
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public override IReadOnlyList<CanonicalHistoryRow> LoadCanonicalHistory(DateTime? fromUtc, DateTime beforeUtc, string? wordKey)
        {
            var rows = Inner.LoadCanonicalHistory(fromUtc, beforeUtc, wordKey);
            return GateEnabled ? new BarrierRows(rows, Entered, Release) : rows;
        }
        public override FsrsParameterPublishResult PublishFsrsParameters(FsrsParameterPublication publication)
        {
            PublishCalls++;
            return Inner.PublishFsrsParameters(publication);
        }
    }

    private sealed class BarrierRows(IReadOnlyList<CanonicalHistoryRow> rows,
        ManualResetEventSlim entered, ManualResetEventSlim release) : IReadOnlyList<CanonicalHistoryRow>
    {
        public int Count => rows.Count;
        public CanonicalHistoryRow this[int index] => rows[index];
        public IEnumerator<CanonicalHistoryRow> GetEnumerator()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("重放测试握手未放行");
            foreach (var row in rows) yield return row;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void UndoAfterPublishRestoresNewBaseline()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        var series = WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 6, 7, StudyRating.Known);
        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "先成功发布一次");

        var version = FsrsPersonalization.ParameterVersionOf(candidate);
        var last = series[^1];
        Program.Check(box.Store.InvalidateCanonical(last.CanonicalId, Anchor.AddDays(1), "user-undo"), "撤销成功");

        var restored = box.Store.GetCard("archive:w1")!;
        var expectedPre = NextPreState(series, candidate, series.Count - 2);
        var oldPre = NextPreState(series, Fsrs6Weights.Defaults, series.Count - 2);
        Program.Check(Math.Abs(expectedPre.Stability - oldPre.Stability) > 1e-9,
            "两套权重下的 pre-state 确实不同（否则下面的断言是空转的）");
        Program.Check(Math.Abs(restored.Stability - expectedPre.Stability) < 1e-12
            && Math.Abs(restored.Stability - oldPre.Stability) > 1e-9,
            $"撤销回到**新基线**算出来的 pre-state（S {restored.Stability:R}）");
        Program.Check(restored.FsrsParameterVersion == version, "撤销回放的卡片版本戳 = 当前活跃版本");
        Program.Check(box.Store.InvalidateCanonical(series[^2].CanonicalId, Anchor.AddDays(1), "user-undo"),
            "水位被正确回退：还能继续撤销下一条");
    }

    private static void UndoRestampsParameterVersionOnReplay()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        var series = WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 5, 7, StudyRating.Known);
        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "先发布一次");
        var version = FsrsPersonalization.ParameterVersionOf(candidate);

        Execute(box.DbPath,
            "UPDATE fsrs_cards SET fsrs_algorithm_version = '', fsrs_library_version = '', "
            + "fsrs_parameter_version = '' WHERE word_key = 'archive:w1';");
        Program.Check(ScalarText(box.DbPath,
                "SELECT fsrs_parameter_version FROM fsrs_cards WHERE word_key = 'archive:w1'") == "",
            "夹具：版本戳已清空（守卫因此可观测）");

        Program.Check(box.Store.InvalidateCanonical(series[^1].CanonicalId, Anchor.AddDays(1), "user-undo"), "撤销成功");
        Program.Check(ScalarText(box.DbPath,
                "SELECT fsrs_parameter_version FROM fsrs_cards WHERE word_key = 'archive:w1'") == version,
            "撤销回放把参数版本戳重新盖成当前活跃版本（不留「新 D/S + 空/旧戳」的撒谎行）");
        Program.Check(ScalarText(box.DbPath,
                "SELECT fsrs_algorithm_version FROM fsrs_cards WHERE word_key = 'archive:w1'") == SchedulingConfig.AlgorithmVersion,
            "算法/库版本戳同样被回放");
    }

    private static void PublishIsIdempotent()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out var holder, out _, weights: candidate);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 6, 7, StudyRating.Known);
        WriteSeries(box.Store, "archive:w2", Anchor.AddDays(-30), 4, 5, StudyRating.Forgot);

        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "第一次发布成功");
        var cards = Snapshot(box.DbPath, "SELECT word_key, difficulty, stability, fsrs_parameter_version FROM fsrs_cards ORDER BY word_key");
        var pre = Snapshot(box.DbPath, "SELECT canonical_id, difficulty, stability FROM canonical_reviews ORDER BY canonical_id");
        var models = ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models");
        var epoch = holder.Epoch;

        // 同一个候选再提交一次：它的 epoch 已经被第一次发布推进，因此按"候选过时"拒绝。
        var replay = service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult();
        Program.Check(!replay.Applied && replay.Rejection == FsrsParameterPublishRejection.EpochChanged,
            $"同一候选重复发布被拒（实际 {replay.Rejection}：{replay.Detail}）");
        // 把 epoch 对齐到当前值再试：这次拒因应当是"该版本已经是当前生效版本"。
        var aligned = outcome.Candidate! with { StartEpoch = holder.Epoch };
        var again = service.PublishAsync(aligned).GetAwaiter().GetResult();
        Program.Check(!again.Applied && again.Rejection == FsrsParameterPublishRejection.AlreadyActive,
            $"版本已是当前版本 → AlreadyActive（实际 {again.Rejection}：{again.Detail}）");
        Program.Check(Snapshot(box.DbPath, "SELECT word_key, difficulty, stability, fsrs_parameter_version FROM fsrs_cards ORDER BY word_key") == cards
            && Snapshot(box.DbPath, "SELECT canonical_id, difficulty, stability FROM canonical_reviews ORDER BY canonical_id") == pre,
            "重复发布不改变任何卡片与 pre-state");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models") == models && holder.Epoch == epoch,
            "重复发布不新增模型行、不推进 epoch");

        var storeReplay = box.Store.PublishFsrsParameters(new FsrsParameterPublication(
            ModelVersion: outcome.Candidate!.ParameterVersion,
            TrainedAtUtc: outcome.Candidate.TrainedAtUtc,
            TrainCutoffUtc: outcome.Candidate.TrainedAtUtc,
            CoefficientsJson: candidate.ToJson(),
            MetricsJson: outcome.Candidate.MetricsJson,
            ParameterVersion: outcome.Candidate.ParameterVersion,
            CanonicalSignature: box.Store.ComputeCanonicalSignature(),
            Words: FsrsPersonalization.BuildReplayPlan(service.LoadReplayHistory(), candidate, box.Store.LoadManualDueOverrides()),
            RecordKind: SchedulingConfig.FsrsParameterRecordKindParams));
        Program.Check(storeReplay.Applied, "store 层重复发布仍然成功（同一行原地覆盖 = 幂等）");
        Program.Check(Snapshot(box.DbPath, "SELECT word_key, difficulty, stability, fsrs_parameter_version FROM fsrs_cards ORDER BY word_key") == cards,
            "store 层重复发布后卡片逐字节不变");
    }

    /// <summary>训练期间用户撤销了一条 canonical → 原快照里的训练标签已失效，必须拒绝发布。</summary>
    private static void PublishRejectsUndoDuringTraining()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        var series = WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 6, 7, StudyRating.Known);

        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(outcome.Outcome == FsrsParameterTrainingOutcome.Trained, "先拿到候选");
        Program.Check(box.Store.InvalidateCanonical(series[^1].CanonicalId, Anchor.AddDays(1), "user-undo"),
            "训练期间撤销一条 canonical");

        var cards = Snapshot(box.DbPath, "SELECT word_key, difficulty, stability, fsrs_parameter_version FROM fsrs_cards ORDER BY word_key");
        var models = ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models");
        var result = service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult();
        Program.Check(!result.Applied && result.Rejection == FsrsParameterPublishRejection.StaleSnapshot,
            $"撤销后的快照被拒绝发布（实际 {result.Rejection}：{result.Detail}）");
        Program.Check(Snapshot(box.DbPath, "SELECT word_key, difficulty, stability, fsrs_parameter_version FROM fsrs_cards ORDER BY word_key") == cards,
            "被拒绝的发布没有改动任何卡片");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models") == models,
            "被拒绝的发布没有写参数行（也没写记账行）");
    }

    /// <summary>改判同样改变训练标签：撤销 + 用同一 canonical_id 重新定稿 → revision 变化 → 拒绝。</summary>
    private static void PublishRejectsReviseDuringTraining()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        var series = WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 6, 7, StudyRating.Known);

        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(outcome.Outcome == FsrsParameterTrainingOutcome.Trained, "先拿到候选");

        var last = series[^1];
        Program.Check(box.Store.InvalidateCanonical(last.CanonicalId, Anchor.AddDays(1), "user-revise"), "撤销（改判的第一步）");
        WriteCanonical(box.Store, "archive:w1", "s005", last.At, StudyRating.Forgot, CanonicalOrigin.FirstRetrieval, continueFromStore: true);
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT revision FROM canonical_reviews WHERE canonical_id = '{last.CanonicalId}'") >= 1,
            "改判后 revision 已增加（旧锚点不再匹配）");

        var result = service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult();
        Program.Check(!result.Applied && result.Rejection == FsrsParameterPublishRejection.StaleSnapshot,
            $"改判后的快照被拒绝发布（实际 {result.Rejection}）");
    }

    /// <summary>
    /// 被拒绝的发布**不能**永久吃掉新增水位：记下"被拒快照令牌"之后，
    /// 针对已经变化的快照必须能重训（冷却仍生效），而不是被迫再攒 100 条新目标。
    /// </summary>
    private static void RejectedPublishDoesNotConsumeIncrement()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var options = TestOptions(minimumReviews: 5, minimumSpanDays: 5, minimumNewReviews: 100, cooldownMinutes: 0);
        var service = NewService(box, out _, out var optimizer, weights: candidate, options: options);
        // 用真实"现在"作为测试时钟：发布成功会把冷却时钟设成 store 取的**真实发布时刻**，
        // 如果测试仍用合成的 Anchor（远在过去），后续训练会看到负的时间差而被冷却挡住。
        var now = DateTime.UtcNow;
        // 第二次训练必须产出**不同**的权重，否则"与当前版本一致"会掩盖门槛是否真的放行。
        var other = CandidateWeights(w0: 0.75);
        var call = 0;
        optimizer.Handler = (_, _) => System.Threading.Tasks.Task.FromResult(call++ == 0 ? candidate : other);

        // ① 先攒够 100 个真实目标并成功发布一次，把新增水位抬到 105。
        var series = WriteSeries(box.Store, "archive:w1", now.AddDays(-300), 106, 1, StudyRating.Known);
        var first = service.TrainAsync(now, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(first.Outcome == FsrsParameterTrainingOutcome.Trained,
            $"第一次训练得到候选（实际 {first.Outcome}：{first.Detail}）");
        Program.Check(service.PublishAsync(first.Candidate!).GetAwaiter().GetResult().Applied, "第一次发布成功");
        Program.Check(service.ReviewsAtLastAttempt == 105,
            $"成功发布把水位抬到 105（实际 {service.ReviewsAtLastAttempt}）");

        // ② 再攒够 100 个新目标 → 允许训练。
        var last = series[^1];
        for (var i = 0; i < 100; i++)
            last = AppendCanonical(box.Store, "archive:w1", $"n{i:D3}", now.AddDays(-193 + i));
        var second = service.TrainAsync(now.AddMinutes(1), hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(second.Outcome == FsrsParameterTrainingOutcome.Trained,
            $"新增恰好 100 个目标 → 允许训练（实际 {second.Outcome}：{second.Detail}）");
        var watermarkBefore = service.ReviewsAtLastAttempt;

        // ③ 训练期间用户撤销了一条 canonical：原快照里的标签已失效 → 发布必须被拒。
        Program.Check(box.Store.InvalidateCanonical(last.CanonicalId, now, "user-undo"), "撤销一条 canonical");
        var rejected = service.PublishAsync(second.Candidate!).GetAwaiter().GetResult();
        Program.Check(!rejected.Applied && rejected.Rejection == FsrsParameterPublishRejection.StaleSnapshot,
            $"发布被拒（实际 {rejected.Rejection}）");
        service.NotePublishRejected(rejected, now.AddMinutes(2), second.Candidate!.CanonicalSignature);
        Program.Check(service.ReviewsAtLastAttempt == watermarkBefore,
            $"被拒发布**不推进**新增水位（{watermarkBefore} → {service.ReviewsAtLastAttempt}）"
            + "——否则一次作废的候选会让用户再攒 100 条之前都训不起来");

        // ④ 现在的新增只有 99 < 100，但数据基础已经变了 → 增量门槛必须被豁免一次。
        var again = service.TrainAsync(now.AddMinutes(3), hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(again.Outcome == FsrsParameterTrainingOutcome.Trained,
            $"针对已变化的快照允许重训，无需再攒第 100 条（实际 {again.Outcome}：{again.Detail}）");
        Program.Check(optimizer.Calls == 3,
            $"确实又调用了一次训练器（首训 1 + 新增 100 后 1 + 拒绝后重训 1 = 3；实际 {optimizer.Calls}）");

        // ⑤ 反向对照：**当前这份**快照（= 阶段④候选训练时用的那份）被拒之后，
        // 令牌等于当前签名 → 不再豁免增量，于是不会被每轮重跑。
        service.NotePublishRejected(rejected, now.AddMinutes(4), again.Candidate!.CanonicalSignature);
        var blocked = service.TrainAsync(now.AddMinutes(5), hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(blocked.Outcome == FsrsParameterTrainingOutcome.WaitingForData,
            $"同一份被拒快照不会被每轮重跑（实际 {blocked.Outcome}）");
        Program.Check(optimizer.Calls == 3,
            $"被令牌挡住时**没有**再调用训练器（实际 {optimizer.Calls} 次）");
    }

    private static void PublishRollsBackAtomicallyOnFault()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out var holder, out _, weights: candidate);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 4, 7, StudyRating.Known);
        WriteSeries(box.Store, "archive:w2", Anchor.AddDays(-30), 3, 7, StudyRating.Known);

        var cards = Snapshot(box.DbPath, "SELECT word_key, difficulty, stability, fsrs_parameter_version FROM fsrs_cards ORDER BY word_key");
        var pre = Snapshot(box.DbPath, "SELECT canonical_id, difficulty, stability FROM canonical_reviews ORDER BY canonical_id");
        var models = ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models");

        var plan = new List<FsrsWordReplay>(FsrsPersonalization.BuildReplayPlan(service.LoadReplayHistory(), candidate))
        {
            new("archive:ghost", ReplayCard("archive:ghost", candidate),
                [new FsrsPreStateRewrite("cr:archive:ghost:missing", FsrsPreState.From(null))]),
        };
        var publication = new FsrsParameterPublication(
            ModelVersion: FsrsPersonalization.ParameterVersionOf(candidate),
            TrainedAtUtc: Anchor, TrainCutoffUtc: Anchor,
            // metrics 必须带合法 recordKind：写入侧守卫（RequireFsrsParameterRecordKind）会在**重放循环之前**
            // 就抛出 InvalidDataException，于是本用例会"用错误的原因变绿"，
            // 而 RewriteCanonicalPreState 的 affected != 1 分支从此失去覆盖。
            CoefficientsJson: candidate.ToJson(),
            MetricsJson: "{\"recordKind\":\"" + SchedulingConfig.FsrsParameterRecordKindParams + "\"}",
            ParameterVersion: FsrsPersonalization.ParameterVersionOf(candidate),
            CanonicalSignature: box.Store.ComputeCanonicalSignature(),
            Words: plan, RecordKind: SchedulingConfig.FsrsParameterRecordKindParams);

        var threw = false;
        var message = "";
        try { box.Store.PublishFsrsParameters(publication); }
        catch (InvalidDataException ex) { threw = true; message = ex.Message; }
        Program.Check(threw, "重放目标缺失 → 抛异常（不静默跳过那一条 canonical）");
        // 断言失败**必须来自重放目标缺失**，不能来自写入侧校验：否则这条用例会掩盖掉
        // RewriteCanonicalPreState 的行数校验分支（曾经就这样静默失去过覆盖）。
        Program.Check(message.Contains("cr:archive:ghost:missing"),
            $"异常来自重放目标缺失而非其它校验（实际消息：{message}）");
        Program.Check(Snapshot(box.DbPath, "SELECT word_key, difficulty, stability, fsrs_parameter_version FROM fsrs_cards ORDER BY word_key") == cards,
            "故障发布：卡片整表回滚");
        Program.Check(Snapshot(box.DbPath, "SELECT canonical_id, difficulty, stability FROM canonical_reviews ORDER BY canonical_id") == pre,
            "故障发布：pre-state 整表回滚");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models") == models,
            "故障发布：参数行一起回滚（不留半套状态）");
        Program.Check(holder.Epoch == 0, "故障发布不动内存权重");
    }

    /// <summary>
    /// 延后发布时 activeSince 必须是**实际发布时刻**而不是训练时刻：
    /// Context 正是按这个时刻过滤快照的，用训练时刻会把发布之前捕获的样本算成新基线下的样本。
    /// </summary>
    private static void ActiveSinceIsTheActualPublishInstant()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 6, 7, StudyRating.Known);

        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(outcome.Outcome == FsrsParameterTrainingOutcome.Trained, "训练得到候选（TrainedAtUtc 是合成的 Anchor）");
        var published = service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult();
        Program.Check(published.Applied && published.PublishedAtUtc is not null, "发布成功并返回权威发布时刻");
        var at = published.PublishedAtUtc ?? throw new InvalidOperationException("发布未返回时刻");

        Program.Check(at > outcome.Candidate!.TrainedAtUtc.AddHours(1),
            $"发布时刻明显晚于训练时刻（训练 {outcome.Candidate.TrainedAtUtc:O} / 发布 {at:O}）——模拟被延后发布的候选");
        Program.Check(service.ActiveSinceUtc == at, "内存里的 activeSince = store 返回的发布时刻");
        var version = outcome.Candidate.ParameterVersion;
        var recorded = ScalarText(box.DbPath,
            $"SELECT json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterActiveSinceMetricKey}') "
            + $"FROM personalization_models WHERE model_version = '{version}'");
        Program.Check(recorded == at.ToString("O"),
            $"库里记的 activeSince 也是同一个发布时刻（实际 {recorded}）");
        Program.Check(recorded != outcome.Candidate.TrainedAtUtc.ToString("O"),
            "库里记的 activeSince **不是**训练时刻");
        Program.Check(new ContextCalibrator(box.Store, new Fsrs6Scheduler()).FsrsBaselineSinceProbe == at,
            "重启后 Context 绑定的基线生效时刻 = 实际发布时刻");
    }

    // ==================================================================================
    // ④ 重启与恢复
    // ==================================================================================

    private static void RestartFindsCorruptModelAndReplaysConsistently()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var good = FsrsPersonalization.ParameterVersionOf(candidate);
        var service = NewService(box, out _, out _, weights: candidate);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 6, 7, StudyRating.Known);
        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "先发布一次个人参数");

        box.Store.SavePersonalizationModel(new PersonalizationModelState
        {
            ModelVersion = good + "-corrupt",
            TrainedAtUtc = Anchor.AddDays(1), TrainCutoffUtc = Anchor.AddDays(1),
            Status = ContextMode.Active,
            CoefficientsJson = "{not json at all",
            ScalerJson = "{}",
            MetricsJson = "{\"" + SchedulingConfig.FsrsParameterRecordKindKey + "\":\"params\",\""
                + SchedulingConfig.FsrsParameterVersionMetricKey + "\":\"" + good + "-corrupt\"}",
            LastGoodModelVersion = good,
        }, SchedulingConfig.FsrsParameterModelKind);

        var startup = FsrsPersonalizationStartup.Create(box.Store, new SchedulerWeights(),
            _ => new FakeOptimizer(), TestOptions());
        Program.Check(startup.Consistent, "损坏行被跳过，回退到 last-good 之后仍然一致");
        Program.Check(startup.Service!.Weights.Current.ValueEquals(candidate),
            "回退到 last-good 而不是官方 defaults");
        Program.Check(startup.Service.ActiveParameterVersion == good, "恢复出来的版本是 last-good 的版本");

        // last-good 也没了 → 回退 defaults，并**一致重放**整库。
        using var box2 = new Sandbox();
        var service2 = NewService(box2, out _, out _, weights: candidate);
        WriteSeries(box2.Store, "archive:w1", Anchor.AddDays(-60), 6, 7, StudyRating.Known);
        var outcome2 = service2.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service2.PublishAsync(outcome2.Candidate!).GetAwaiter().GetResult().Applied, "（第二份库）先发布一次");
        Program.Check(box2.Store.HasCardsOutsideParameterVersion(SchedulingConfig.ParameterVersion),
            "此时卡片确实带着个人参数版本（下面「不一致」的前提）");
        Execute(box2.DbPath, $"UPDATE personalization_models SET coefficients_json = '[]' WHERE model_version = '{good}'");

        var fallback = FsrsPersonalizationStartup.Create(box2.Store, new SchedulerWeights(),
            _ => new FakeOptimizer(), TestOptions());
        Program.Check(fallback.Consistent && fallback.Replayed, $"回退后做了一致重放（{fallback.Reconcile?.Detail}）");
        Program.Check(fallback.Service!.Weights.Current.Source == Fsrs6Weights.SourceDefaults, "回退到官方 defaults");
        Program.Check(!box2.Store.HasCardsOutsideParameterVersion(SchedulingConfig.ParameterVersion),
            "重放后不存在「默认权重 + 旧 D/S」的混合体");
        var card = box2.Store.GetCard("archive:w1")!;
        Program.Check(Math.Abs(card.Stability - RecomputeSeries(box2.Store, "archive:w1", Fsrs6Weights.Defaults).Stability) < 1e-12,
            "重放后的 D/S 就是 defaults 下的值（一致重放，不是只改戳）");
    }

    private static void DefaultsBaselineNeedsNoReplay()
    {
        using var box = new Sandbox();
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-20), 4, 5, StudyRating.Known);
        var cards = Snapshot(box.DbPath, "SELECT word_key, difficulty, stability FROM fsrs_cards ORDER BY word_key");
        var startup = FsrsPersonalizationStartup.Create(box.Store, new SchedulerWeights(), _ => new FakeOptimizer(), TestOptions());
        Program.Check(startup.Consistent && !startup.Replayed, "全新库：一致且无需重放");
        Program.Check(Snapshot(box.DbPath, "SELECT word_key, difficulty, stability FROM fsrs_cards ORDER BY word_key") == cards,
            "正常一致重启不重写任何卡片");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models") == 0, "一致时连模型行都不写");
    }

    private static void ReconcileFailureIsNotReportedAsConsistent()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 5, 7, StudyRating.Known);
        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "先发布一次");

        // 让库处于"卡片还是旧版本"的不一致状态，并让重放事务失败。
        Execute(box.DbPath, "UPDATE fsrs_cards SET fsrs_parameter_version = 'fsrs6-p1';");
        var failing = new FailingPublishStore(box.Store);
        var broken = FsrsPersonalizationStartup.Create(failing, new SchedulerWeights(), _ => new FakeOptimizer(), TestOptions());
        Program.Check(failing.PublishAttempted, "确实尝试过重放（故障注入生效）");
        Program.Check(!broken.Consistent,
            $"重放注入故障 → 一致性核对**不通过**（{broken.Reconcile?.Detail}）");
        // A/B 对照：同一个库、同一次不一致，只要事务成功就必须判为一致——
        // 否则上面那条断言可能只是因为"任何情况下都返回 false"而空转。
        var healthy = FsrsPersonalizationStartup.Create(box.Store, new SchedulerWeights(), _ => new FakeOptimizer(), TestOptions());
        Program.Check(healthy.Consistent && healthy.Replayed,
            $"同一库用正常 store 重放成功 → 判为一致（{healthy.Reconcile?.Detail}）");
    }

    private static void StartupDoesNotExposeCoordinatorWhenInconsistent()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 5, 7, StudyRating.Known);
        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "先发布一次");
        Execute(box.DbPath, "UPDATE fsrs_cards SET fsrs_parameter_version = 'fsrs6-p1';");

        var broken = FsrsPersonalizationStartup.Create(new FailingPublishStore(box.Store), new SchedulerWeights(),
            _ => new FakeOptimizer(), TestOptions());
        Program.Check(!broken.Consistent,
            "不一致时把 Consistent 置 false（MainWindow 据此**不暴露**可写的长期记忆协调器）");
        var personalVersion = FsrsPersonalization.ParameterVersionOf(candidate);
        Program.Check(box.Store.HasCardsOutsideParameterVersion(personalVersion),
            "此时卡片仍带着「未经重放到个人权重的 D/S」，用个人权重评分就会写出混合状态");
        // A/B：同一个库换成正常 store → 一致 → 允许暴露协调器。
        var healthy = FsrsPersonalizationStartup.Create(box.Store, new SchedulerWeights(), _ => new FakeOptimizer(), TestOptions());
        Program.Check(healthy.Consistent, "同一库修复成功 → 一致 → 允许暴露协调器");
    }

    private static void InitializationFailureWithMixedCardsIsNotConsistent()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 5, 7, StudyRating.Known);
        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "先发布一次");

        // 参数历史与一致性探针都不可用：无法证明"卡片就是默认权重算出来的" → 不得判为一致。
        var broken = FsrsPersonalizationStartup.Create(new ThrowingStore(box.Store, throwOnProbe: true),
            new SchedulerWeights(), _ => new FakeOptimizer(), TestOptions());
        Program.Check(!broken.Consistent,
            "探针也不可用 → 无法证明一致 → Consistent=false（不许拿 defaults 去推个人权重的 D/S）");

        // 反过来：卡片本来就与 defaults 一致（空版本戳按冻结默认版本解释），
        // 此时即使参数历史读不出来，也应当允许以默认参数继续，而不是把整层锁死。
        Execute(box.DbPath, "UPDATE fsrs_cards SET fsrs_parameter_version = '';");
        var benign = FsrsPersonalizationStartup.Create(new ThrowingStore(box.Store, throwOnProbe: false),
            new SchedulerWeights(), _ => new FakeOptimizer(), TestOptions());
        Program.Check(benign.Consistent, "卡片与 defaults 一致时，参数历史读不出来不阻塞以默认参数继续");
    }

    // ==================================================================================
    // ⑤ 版本戳与不可回写
    // ==================================================================================

    private static void ParameterVersionDerivesFromWeights()
    {
        Program.Check(FsrsPersonalization.ParameterVersionOf(Fsrs6Weights.Defaults) == SchedulingConfig.ParameterVersion,
            "官方 defaults 恒用冻结版本常量 fsrs6-p1");
        var a = CandidateWeights();
        var b = CandidateWeights(w0: 0.5);
        Program.Check(FsrsPersonalization.ParameterVersionOf(a) != FsrsPersonalization.ParameterVersionOf(b),
            "不同权重 → 不同版本戳");
        Program.Check(FsrsPersonalization.ParameterVersionOf(a) == FsrsPersonalization.ParameterVersionOf(a.Clone()),
            "同一组权重 → 同一版本戳（可复现）");

        var scheduler = new Fsrs6Scheduler(a);
        Program.Check(scheduler.Review(null, StudyRating.Known, Anchor).Card.FsrsParameterVersion
            == FsrsPersonalization.ParameterVersionOf(a), "排期器盖的版本戳 = 权重派生版本");
        Program.Check(new Fsrs6Scheduler().Review(null, StudyRating.Known, Anchor).Card.FsrsParameterVersion
            == SchedulingConfig.ParameterVersion, "默认权重下卡片戳仍是 fsrs6-p1");
    }

    private static void LegacyDueAndHistoricalDecisionsAreNotRewritten()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out _, weights: candidate);
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 5, 7, StudyRating.Known);

        Execute(box.DbPath,
            "INSERT INTO scheduler_decisions (decision_id, word_key, canonical_id, timestamp_utc, final_interval_days, "
            + "final_due_at_utc, fsrs_parameter_version, scheduler_version) "
            + "VALUES ('dec-1', 'archive:w1', 'cr:x', '" + Anchor.AddDays(-1).ToString("O") + "', 7.0, '"
            + Anchor.AddDays(6).ToString("O") + "', 'fsrs6-p1', 'sched-1');");
        var decisions = Snapshot(box.DbPath,
            "SELECT decision_id, final_interval_days, final_due_at_utc, fsrs_parameter_version FROM scheduler_decisions");
        var legacyDue = Anchor.Date.AddDays(3).ToString("yyyy-MM-dd");
        var wordId = box.ImportLegacyWord("legacyword", legacyDue);
        var words = Snapshot(box.DbPath, "SELECT id, stage, status, next_review_date FROM words");

        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "发布成功");

        Program.Check(Snapshot(box.DbPath,
                "SELECT decision_id, final_interval_days, final_due_at_utc, fsrs_parameter_version FROM scheduler_decisions") == decisions,
            "历史 scheduler_decisions 逐字节不变（换版不静默回写旧预测）");
        Program.Check(Snapshot(box.DbPath, "SELECT id, stage, status, next_review_date FROM words") == words,
            "words 表的 stage/status/next_review_date 原样保留（管理动作调整过的 due 不被覆盖）");
        _ = wordId;
    }

    // ==================================================================================
    // ⑥ Context 绑定与世代
    // ==================================================================================

    /// <summary>
    /// 队长/独立审查点名的组合反例：**唯一个人参数行的 coefficients 已损坏，但 metrics 里的版本字符串完好**，
    /// 同时库里存在一个"同版本 Active"的 Context 模型。
    /// 旧实现让 Context 只看版本字符串 → 恢复 Active；集成层却回退到 defaults → 两者的基线不是同一套。
    /// </summary>
    private static void ContextBindsToEffectiveWeightsNotMetricsVersion()
    {
        using var box = new Sandbox();
        var lyingVersion = "fsrs6-p1-wdeadbeef";
        box.Store.SavePersonalizationModel(new PersonalizationModelState
        {
            ModelVersion = lyingVersion,
            TrainedAtUtc = Anchor, TrainCutoffUtc = Anchor,
            Status = ContextMode.Active,
            CoefficientsJson = "{ this is not valid json",   // ← 损坏
            ScalerJson = "{}",
            MetricsJson = "{\"" + SchedulingConfig.FsrsParameterRecordKindKey + "\":\"params\",\""
                + SchedulingConfig.FsrsParameterVersionMetricKey + "\":\"" + lyingVersion + "\"}",
            LastGoodModelVersion = lyingVersion,
        }, SchedulingConfig.FsrsParameterModelKind);
        // 一个"绑定在该版本上"的 Active Context 模型（形状合法、可解析）。
        box.Store.SavePersonalizationModel(new PersonalizationModelState
        {
            ModelVersion = "ctx-lr-1:active",
            TrainedAtUtc = Anchor, TrainCutoffUtc = Anchor,
            Status = ContextMode.Active,
            CoefficientsJson = "{\"version\":\"ctx-lr-1:active\",\"intercept\":0.1,\"weights\":["
                + string.Join(",", Enumerable.Repeat("0.01", ContextFeatureProvider.FeatureCount)) + "]}",
            ScalerJson = ScalerJson(),
            MetricsJson = "{\"featureSchemaVersion\":\"" + SchedulingConfig.ContextFeatureSchemaVersion
                + "\",\"evaluationProtocol\":\"published-frozen-v2\",\""
                + SchedulingConfig.FsrsParameterVersionMetricKey + "\":\"" + lyingVersion + "\"}",
            LastGoodModelVersion = "ctx-lr-1:active",
        }, SchedulingConfig.ContextModelKind);

        var holder = new SchedulerWeights();
        var startup = FsrsPersonalizationStartup.Create(box.Store, holder, _ => new FakeOptimizer(), TestOptions());
        Program.Check(startup.Service!.ActiveParameterVersion == SchedulingConfig.ParameterVersion,
            $"损坏行被跳过 → 有效版本回退到 defaults（实际 {startup.Service.ActiveParameterVersion}）");
        Program.Check(startup.Service.Weights.Current.Source == Fsrs6Weights.SourceDefaults,
            "有效权重 = 官方默认参数（不是那个只写在 metrics 里的版本）");

        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        Program.Check(calibrator.FsrsBaselineVersionProbe == SchedulingConfig.ParameterVersion,
            $"Context 绑定到**同一套**有效参数（实际 {calibrator.FsrsBaselineVersionProbe}），而不是 metrics 里的假版本");
        Program.Check(calibrator.Mode == ContextMode.ColdStart,
            $"旧 Active 不得沿用到另一套基线上（实际 {calibrator.Mode}）");
    }

    private static void ContextDemotedOnBaselineChange()
    {
        using var box = new Sandbox();
        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        Program.Check(calibrator.Mode == ContextMode.ColdStart, "初始为 ColdStart");

        calibrator.ApplyFsrsBaseline("fsrs6-p1-wdeadbeef", Anchor);
        Program.Check(calibrator.Mode == ContextMode.ColdStart, "换版后 Context 退回 ColdStart");
        var rows = box.Store.LoadPersonalizationModels(SchedulingConfig.ContextModelKind, 8);
        Program.Check(rows.Count == 1
            && rows[0].ModelVersion == SchedulingConfig.ContextModelVersionPrefix + ":fsrs-baseline-change",
            "换版写下一行可辨的降资格记录");
        Program.Check(rows[0].CoefficientsJson.Contains("\"weights\":[]", StringComparison.Ordinal),
            "降资格记录是形状合法但不可用的空模型（不会复活旧基线模型）");
        Program.Check(FsrsPersonalization.ReadMetricString(rows[0].MetricsJson, SchedulingConfig.FsrsParameterVersionMetricKey)
            == "fsrs6-p1-wdeadbeef", "降资格记录带上了新基线版本（可审计）");

        // JSON 数组根曾经会让解析器抛 InvalidOperationException → 一路把异常抛出构造函数
        // → MainWindow 兜底 catch → 整层长期记忆停用。一条坏状态行不该有这种杀伤力。
        box.Store.SavePersonalizationModel(new PersonalizationModelState
        {
            ModelVersion = "ctx-lr-1:array-root",
            TrainedAtUtc = Anchor.AddDays(1), TrainCutoffUtc = Anchor.AddDays(1),
            Status = ContextMode.Active,
            CoefficientsJson = "[0.125,0.25,-0.5]",
            ScalerJson = "{\"mean\":[1],\"std\":[1]}",
            MetricsJson = "{}",
        }, SchedulingConfig.ContextModelKind);
        Program.Check(new ContextCalibrator(box.Store, new Fsrs6Scheduler()).Mode == ContextMode.ColdStart,
            "coefficients_json 是 JSON 数组（形状不符）时退回 ColdStart，而不是把异常抛出构造函数");
    }

    /// <summary>
    /// 已有可用模型 + 在途训练 + 换基线 + 取消：**所有出口**都必须先过世代闸，
    /// 否则旧系数会以"新基线"的名义被 SafeFallback 写回去，覆盖刚写好的降资格记录。
    /// </summary>
    private static void ContextCancelDuringBaselineChangeKeepsDemotion()
    {
        using var box = new Sandbox();
        var since = Anchor.AddDays(-30);
        SeedContextSamples(box.Store, "samples", since.AddDays(1), 240, stepHours: 2);
        var gated = new GatedStore(box.Store);
        var calibrator = new ContextCalibrator(gated, new Fsrs6Scheduler());
        // 先让它拥有一份"当前基线下的可用模型"（否则 SafeFallback 没有系数可写，覆盖路径不可达）。
        var warmup = calibrator.TrainAsync(Anchor).GetAwaiter().GetResult();
        Program.Check(warmup.Outcome is ContextTrainingOutcome.Trained,
            $"预热训练产出模型（实际 {warmup.Outcome}：{warmup.Detail}）");

        var rowsAfterWarmup = box.Store.LoadPersonalizationModels(SchedulingConfig.ContextModelKind, 8).Count;
        gated.GateEnabled = true;
        var training = System.Threading.Tasks.Task.Run(() => calibrator.TrainAsync(Anchor.AddHours(1)));
        Program.Check(gated.WaitUntilSamplesRequested(TimeSpan.FromSeconds(10)), "第二次训练已进入样本读取（握手成功）");
        calibrator.ApplyFsrsBaseline("fsrs6-p1-wnew", since);
        gated.ReleaseSamples();
        var result = training.GetAwaiter().GetResult();

        Program.Check(result.Outcome == ContextTrainingOutcome.SafeFallback
            && result.Detail.Contains("基线已更换", StringComparison.Ordinal),
            $"跨换版的训练在任何出口都被丢弃（实际 {result.Outcome}：{result.Detail}）");
        // 行数对照：第二次训练**没有**新增任何行（只有 ApplyFsrsBaseline 写下的那一条降资格记录）。
        // 直接比"最新一行"是不可靠的——降资格记录的 trained_at 是**新基线的生效时刻**（可能在过去），
        // 排序上未必压过预热模型；用行数+身份才不会写出一个会漂移的断言。
        var rows = box.Store.LoadPersonalizationModels(SchedulingConfig.ContextModelKind, 8);
        Program.Check(rows.Count == rowsAfterWarmup + 1,
            $"只多了降资格记录这一行（{rowsAfterWarmup} → {rows.Count}）——旧系数没有被写回");
        Program.Check(rows.Any(r => r.ModelVersion == SchedulingConfig.ContextModelVersionPrefix + ":fsrs-baseline-change"),
            "降资格记录确实存在");
        var marker = rows.First(r => r.ModelVersion == SchedulingConfig.ContextModelVersionPrefix + ":fsrs-baseline-change");
        Program.Check(FsrsPersonalization.ReadMetricString(marker.MetricsJson, "outcome") == "FsrsBaselineChange"
            && FsrsPersonalization.ReadMetricString(marker.MetricsJson, SchedulingConfig.FsrsParameterVersionMetricKey)
                == "fsrs6-p1-wnew",
            "降资格记录写明原因与新基线版本（可审计）");
        Program.Check(marker.CoefficientsJson.Contains("\"weights\":[]", StringComparison.Ordinal),
            "降资格记录是空模型：任何恢复都退回 ColdStart");

        // —— 第二条负对照：**取消**这条出口同样必须先过世代闸 ——
        // 顺序：先换基线，再取消 → 训练以 OperationCanceledException 收尾。
        // 若取消分支绕过世代闸，SafeFallback 会把内存里的旧系数（新基线下的 mode=Shadow）写回库，
        // 覆盖掉刚写好的降资格记录。
        var rowsBeforeCancel = box.Store.LoadPersonalizationModels(SchedulingConfig.ContextModelKind, 8).Count;
        gated.GateEnabled = false;
        gated.ResetGate();
        gated.GateEnabled = true;
        using var cancelSource = new CancellationTokenSource();
        var cancelling = System.Threading.Tasks.Task.Run(() => calibrator.TrainAsync(Anchor.AddHours(2), cancelSource.Token));
        Program.Check(gated.WaitUntilSamplesRequested(TimeSpan.FromSeconds(10)), "第三次训练已进入样本读取");
        calibrator.ApplyFsrsBaseline("fsrs6-p1-wnewer", since);
        cancelSource.Cancel();
        gated.ReleaseSamples();
        var cancelled = cancelling.GetAwaiter().GetResult();
        Program.Check(cancelled.Outcome == ContextTrainingOutcome.SafeFallback
            && cancelled.Detail.Contains("基线已更换", StringComparison.Ordinal),
            $"取消出口也被世代闸拦下（实际 {cancelled.Outcome}：{cancelled.Detail}）");
        var rowsAfterCancel = box.Store.LoadPersonalizationModels(SchedulingConfig.ContextModelKind, 8);
        // 降资格记录用**常量**版本号（幂等：再次降资格是原地覆盖），因此行数应当**不变**——
        // 多出来的任何一行都意味着取消分支把旧系数当成新模型写回去了。
        Program.Check(rowsAfterCancel.Count == rowsBeforeCancel,
            $"取消没有新增任何模型行（{rowsBeforeCancel} → {rowsAfterCancel.Count}）——旧系数没有被写回");
        Program.Check(FsrsPersonalization.ReadMetricString(
                rowsAfterCancel.First(r => r.ModelVersion == SchedulingConfig.ContextModelVersionPrefix + ":fsrs-baseline-change").MetricsJson,
                SchedulingConfig.FsrsParameterVersionMetricKey) == "fsrs6-p1-wnewer",
            "降资格记录被更新到最新基线版本");
        Program.Check(calibrator.CurrentModel is null && calibrator.Mode == ContextMode.ColdStart,
            "取消之后仍是 ColdStart 且不持有旧系数");
        Program.Check(calibrator.Mode == ContextMode.ColdStart, "阶段仍是 ColdStart");
        Program.Check(calibrator.CurrentModel is null,
            "换基线后内存里不再持有旧模型的系数（杜绝任何把旧系数标成新基线的路径）");
    }

    private static void ContextTrainingOnlyUsesSamplesAfterBaseline()
    {
        using var box = new Sandbox();
        var since = Anchor.AddDays(-30);
        SeedContextSamples(box.Store, "old", Anchor.AddDays(-90), 400, stepHours: 3);

        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        calibrator.ApplyFsrsBaseline("fsrs6-p1-wpersonal", since);
        var before = calibrator.TrainAsync(Anchor).GetAwaiter().GetResult();
        Program.Check(before.Outcome == ContextTrainingOutcome.ColdStart,
            $"只有旧基线快照时**不训练**（实际 {before.Outcome}）");

        SeedContextSamples(box.Store, "new", since.AddDays(1), 240, stepHours: 2);
        var after = calibrator.TrainAsync(Anchor.AddHours(1)).GetAwaiter().GetResult();
        Program.Check(after.Outcome != ContextTrainingOutcome.ColdStart,
            $"加上当前基线的 240 条之后开始拟合（实际 {after.Outcome}：{after.Detail}）");
    }

    // ==================================================================================
    // ⑦ 手动 due 覆盖
    // ==================================================================================

    private static void ManualDueOverrideSurvivesPublishAndUndo()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var manualDate = Anchor.Date.AddDays(9).ToString("yyyy-MM-dd");
        var wordId = box.ImportLegacyWord("manualword", manualDate);
        var uuid = ScalarText(box.DbPath, $"SELECT uuid FROM word_archives WHERE word_id = {wordId}");
        var wordKey = WordKey.Archive(uuid).Key;

        var service = NewService(box, out _, out _, weights: candidate);
        var series = WriteSeries(box.Store, wordKey, Anchor.AddDays(-60), 5, 7, StudyRating.Known);
        var modelDue = box.Store.GetCard(wordKey)!.NextReviewAtUtc;

        // 真实管理动作：词表里的"完成本次复习"→ 同步卡片 due 到旧列的本地日历日。
        Execute(box.DbPath, $"UPDATE words SET next_review_date = '{manualDate}' WHERE id = {wordId};");
        box.SyncManualReviewDue(wordId);
        var manualDueUtc = LocalDateToUtc(manualDate);
        Program.Check(box.Store.LoadManualDueOverrides().Count == 1, "手动动作留下了可辨认的覆盖留痕");
        Program.Check(box.Store.GetCard(wordKey)!.NextReviewAtUtc == manualDueUtc,
            $"卡片 due 已被手动动作改到 {manualDueUtc:O}（与模型 due {modelDue:O} 不同）");

        // 再发生一次真实 retrieval：新 canonical 成为当前锚点，旧覆盖仍锚在它前面那条上。
        var appended = WriteCanonical(box.Store, wordKey, "s-next", Anchor.AddDays(-1), StudyRating.Known,
            CanonicalOrigin.FirstRetrieval, continueFromStore: true);
        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(outcome.Outcome == FsrsParameterTrainingOutcome.Trained, "训练得到候选");
        var published = service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult();
        Program.Check(published.Applied, $"发布成功（{published.Detail}）");

        var replayed = box.Store.GetCard(wordKey)!;
        var expected = Recompute(series.Concat([appended]).ToList(), candidate);
        Program.Check(Math.Abs(replayed.Stability - expected.Stability) < 1e-12,
            "重放后 D/S 变成新模型的值");
        // 覆盖锚在**上一条** canonical 上，因此新 canonical 自己的 due 用模型值，
        // 而它的 **pre-state duel** 必须保留手动覆盖——撤销时要精确回到那一天。
        var preDue = ScalarText(box.DbPath,
            $"SELECT next_review_at_utc FROM canonical_reviews WHERE canonical_id = '{appended.CanonicalId}'");
        Program.Check(preDue == manualDueUtc.ToString("O"),
            $"新 canonical 的 pre-state due = 手动覆盖的日期（实际 {preDue}）");

        Program.Check(box.Store.InvalidateCanonical(appended.CanonicalId, Anchor.AddDays(1), "user-undo"), "撤销新 canonical");
        Program.Check(box.Store.GetCard(wordKey)!.NextReviewAtUtc == manualDueUtc,
            $"撤销后卡片 due 仍恢复成手动覆盖的日期（实际 {box.Store.GetCard(wordKey)!.NextReviewAtUtc:O}）");
    }

    private static void ManualDueOverrideIsRevisionScoped()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var manualDate = Anchor.Date.AddDays(11).ToString("yyyy-MM-dd");
        var wordId = box.ImportLegacyWord("revisedword", manualDate);
        var uuid = ScalarText(box.DbPath, $"SELECT uuid FROM word_archives WHERE word_id = {wordId}");
        var wordKey = WordKey.Archive(uuid).Key;

        var service = NewService(box, out _, out _, weights: candidate);
        var series = WriteSeries(box.Store, wordKey, Anchor.AddDays(-60), 5, 7, StudyRating.Known);
        Execute(box.DbPath, $"UPDATE words SET next_review_date = '{manualDate}' WHERE id = {wordId};");
        box.SyncManualReviewDue(wordId);
        var manualDueUtc = LocalDateToUtc(manualDate);

        // 撤销 + 用同一 canonical_id 改判重新定稿：rowid 不变、revision 增加。
        var last = series[^1];
        Program.Check(box.Store.InvalidateCanonical(last.CanonicalId, Anchor.AddDays(1), "user-revise"), "撤销（改判第一步）");
        WriteCanonical(box.Store, wordKey, "s004", last.At, StudyRating.Forgot, CanonicalOrigin.FirstRetrieval, continueFromStore: true);
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT revision FROM canonical_reviews WHERE canonical_id = '{last.CanonicalId}'") >= 1,
            "改判后 revision 增加");

        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult().Applied, "发布成功");
        var cardDue = box.Store.GetCard(wordKey)!.NextReviewAtUtc;
        Program.Check(cardDue != manualDueUtc,
            $"改判后旧锚点的覆盖不再适用（实际 {cardDue:O}，手动日期 {manualDueUtc:O}）");
    }

    private static void ManualDueOverrideInvalidatesPublishSignature()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var manualDate = Anchor.Date.AddDays(5).ToString("yyyy-MM-dd");
        var wordId = box.ImportLegacyWord("sigword", manualDate);
        var uuid = ScalarText(box.DbPath, $"SELECT uuid FROM word_archives WHERE word_id = {wordId}");
        var wordKey = WordKey.Archive(uuid).Key;
        var service = NewService(box, out _, out _, weights: candidate);
        WriteSeries(box.Store, wordKey, Anchor.AddDays(-60), 5, 7, StudyRating.Known);

        var outcome = service.TrainAsync(Anchor, hasPendingTask: false).GetAwaiter().GetResult();
        Program.Check(outcome.Outcome == FsrsParameterTrainingOutcome.Trained, "先拿到候选");
        // 训练之后发生管理动作 → 一致性签名必须变化。
        Execute(box.DbPath, $"UPDATE words SET next_review_date = '{manualDate}' WHERE id = {wordId};");
        box.SyncManualReviewDue(wordId);

        var result = service.PublishAsync(outcome.Candidate!).GetAwaiter().GetResult();
        Program.Check(!result.Applied && result.Rejection == FsrsParameterPublishRejection.StaleSnapshot,
            $"管理动作也会让发布被拒（签名覆盖了手动覆盖表；实际 {result.Rejection}）");
    }

    // ==================================================================================
    // ⑧ 运行期生命周期
    // ==================================================================================

    /// <summary>
    /// 死锁反例：在**真实的** <see cref="SynchronizationContext"/> 上用同步等待驱动发布，
    /// 续体永远排不到队。这里让上下文由一个泵线程拥有，从泵线程发起轮级触发，
    /// 若生产代码里还有 <c>GetAwaiter().GetResult()</c>，泵线程会被占住、任务永不完成 → 超时失败。
    /// </summary>
    private static void RuntimePublishCompletesUnderSynchronizationContext()
    {
        using var box = new Sandbox();
        using var pump = new PumpingSynchronizationContext();
        var candidate = CandidateWeights();
        FsrsPersonalizationRuntime? runtime = null;
        SchedulerWeights? holder = null;
        try
        {
            // 全部 store 访问都在泵线程（= 拥有 SynchronizationContext 的"UI 线程"）上完成，
            // 与生产一致：那条无锁 SqliteConnection 只被一个线程碰。
            var started = pump.RunAndWait(() =>
            {
                var service = NewService(box, out var weights, out _, weights: candidate,
                    options: TestOptions(minimumReviews: 5, minimumSpanDays: 5));
                holder = weights;
                WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 8, 7, StudyRating.Known);
                runtime = new FsrsPersonalizationRuntime(service, () => false, _ => { });
                // 在"UI 线程"上发起轮级触发，随后由泵驱动续体。
                runtime.NotifyRoundBoundary("测试轮末");
            }, TimeSpan.FromSeconds(10));

            // 第一个死锁判据：发起本身必须立刻返回。若生产代码在 UI 线程上同步等待自己的续体，
            // 泵线程会被占住 → 这里超时 → 该断言失败。
            Program.Check(started, "在带 SynchronizationContext 的 UI 线程上发起训练/发布不阻塞该线程");
            var captured = runtime!;
            var idle = captured.WaitForIdle(TimeSpan.FromSeconds(15), requireNoPending: true);
            Program.Check(idle, "训练+发布在带 SynchronizationContext 的调用线程上完成（不阻塞、不死锁）");
            Program.Check(!captured.Training && !captured.Publishing && !captured.HasPendingCandidate,
                $"收尾后没有残留的在途标志或待发布候选（{captured.LastDetail}）");
            Program.Check(holder!.Current.ValueEquals(candidate), "发布在 UI 上下文下真正完成了内存换版");
            Program.Check(ScalarInt(box.DbPath,
                    $"SELECT COUNT(*) FROM personalization_models WHERE model_version = '{FsrsPersonalization.ParameterVersionOf(candidate)}'") == 1,
                "参数行也真的落库了（不是只在内存里换了版）");
        }
        finally
        {
            runtime?.Invalidate();
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    private static void RuntimeTrainingCompletionAfterRebindIsDropped()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var service = NewService(box, out var holder, out var optimizer, weights: candidate,
            options: TestOptions(minimumReviews: 5, minimumSpanDays: 5));
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 8, 7, StudyRating.Known);
        optimizer.Handler = (_, token) =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10), token);
            return System.Threading.Tasks.Task.FromResult(candidate);
        };

        var runtime = new FsrsPersonalizationRuntime(service, () => false, _ => { });
        runtime.NotifyRoundBoundary("训练开始");
        Program.Check(started.Wait(TimeSpan.FromSeconds(10)), "训练已进入优化器（握手成功）");

        // 训练还在后台拟合时发生 rebind：作废代际、清候选、取消。
        runtime.Invalidate();
        release.Set();
        Program.Check(runtime.WaitForIdle(TimeSpan.FromSeconds(10)), "旧任务在超时内收尾");

        Program.Check(!runtime.HasPendingCandidate,
            "重绑后旧训练的收尾**没有**把候选交给新库（代际校验生效）");
        Program.Check(!runtime.Training && !runtime.Publishing, "旧任务也没有复位/占用新标志");
        Program.Check(holder.Epoch == 0 && holder.Current.Source == Fsrs6Weights.SourceDefaults,
            "重绑后权重没有被旧任务改动");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM personalization_models WHERE model_kind = '{SchedulingConfig.FsrsParameterModelKind}' "
                + $"AND json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterRecordKindKey}') = 'params'") == 0,
            "重绑后旧任务没有写任何参数行");
    }

    /// <summary>
    /// 队长/独立审查点名的**真实 UI 续体边界**：发布事务已经返回、收尾续体还排在 UI 上下文的队列里时，
    /// 用户恢复了备份（rebind）。旧续体必须凭"代际 + service 实例"自我作废——
    /// 否则它会向**已经被替换掉的**那个库写一条"发布被拒"的记账行，并把旧候选交回给新库。
    /// <para>用真实的 <see cref="SynchronizationContext"/> 与一个会在 store 调用处停住的 store 做确定性握手，
    /// 不依赖 sleep，也不依赖任何内部字段。</para>
    /// </summary>
    private static void RuntimeRebindDuringPublishContinuationIsDropped()
    {
        using var box = new Sandbox();
        using var pump = new PumpingSynchronizationContext();
        var candidate = CandidateWeights();
        FsrsPersonalizationRuntime? runtime = null;
        try
        {
            var store = new RejectingPublishStore(box.Store);
            FsrsPersonalization? service = null;
            var defer = true;
            var started = pump.RunAndWait(() =>
            {
                var holder = new SchedulerWeights();
                service = new FsrsPersonalization(store, holder,
                    new FakeOptimizer { Handler = (_, _) => System.Threading.Tasks.Task.FromResult(candidate) },
                    TestOptions(minimumReviews: 5, minimumSpanDays: 5));
                service.Restore();
                WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 8, 7, StudyRating.Known);
                runtime = new FsrsPersonalizationRuntime(service, () => defer, _ => { });
                runtime.NotifyRoundBoundary("训练");
            }, TimeSpan.FromSeconds(10));
            Program.Check(started, "在 UI 上下文中发起训练不阻塞该线程");
            var captured = runtime!;
            // 训练在"UI 上下文"上跑完后会尝试发布，而 defer=true 让它只记一条"延后"——
            // 因此这里等宽松的 idle（不要求 pending 被清空），pending 正是本用例要断言的东西。
            Program.Check(captured.WaitForIdle(TimeSpan.FromSeconds(15)), "训练完成");
            Program.Check(captured.HasPendingCandidate, "候选已在队列里等发布（延后不丢）");

            var attemptsBefore = ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM personalization_models WHERE model_kind = '{SchedulingConfig.FsrsParameterModelKind}' "
                + $"AND json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterRecordKindKey}') = 'attempt'");

            // 允许发布，并等它真正进入 store 事务（store 会在那里停住）。
            defer = false;
            captured.NotifyRoundBoundary("发布");
            Program.Check(store.WaitUntilPublishEntered(TimeSpan.FromSeconds(15)),
                "发布已进入 store 事务并停住（续体尚未执行）");

            // 此刻发生 rebind：作废代际、取消在途、清候选。
            captured.Invalidate();
            store.ReleasePublish();
            Program.Check(!captured.Publishing && captured.WaitForIdle(TimeSpan.FromSeconds(15), requireNoPending: true),
                "旧发布任务在超时内收尾");

            // 旧续体既不能写库，也不能把候选交回。
            var attemptsAfter = ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM personalization_models WHERE model_kind = '{SchedulingConfig.FsrsParameterModelKind}' "
                + $"AND json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterRecordKindKey}') = 'attempt'");
            Program.Check(attemptsAfter == attemptsBefore,
                $"重绑后的旧续体**没有**向旧库写任何记账行（attempt {attemptsBefore} → {attemptsAfter}）");
            // 说明：这条断言在"代际守卫被删掉"的变异下**不会**变红——因为守卫与 CTS 取消
            // 是双保险，且被拒发布的记账行写入路径本身已由 WaitingWritesNoAttemptRow 的正向对照覆盖。
            // 保留它是为了钉住"重绑后不向旧库写副作用"这一契约本身，而不是声称它是该守卫的判别式。
            Program.Check(true, "（诊断位：重绑后无副作用已在上一条断言中核对）");
            Program.Check(!captured.HasPendingCandidate && !captured.Publishing && !captured.Training,
                "重绑后没有残留待发布候选或在途标志");
        }
        finally
        {
            runtime?.Invalidate();
        }
    }

    private static void RuntimeDefersPublishWhileUndoable()
    {
        using var box = new Sandbox();
        var candidate = CandidateWeights();
        var service = NewService(box, out _, out var optimizer, weights: candidate,
            options: TestOptions(minimumReviews: 5, minimumSpanDays: 5));
        WriteSeries(box.Store, "archive:w1", Anchor.AddDays(-60), 8, 7, StudyRating.Known);

        var defer = true;
        var runtime = new FsrsPersonalizationRuntime(service, () => defer, _ => { });
        runtime.NotifyRoundBoundary("有可撤销呈现");
        Program.Check(runtime.WaitForIdle(TimeSpan.FromSeconds(15)), "训练完成");
        Program.Check(runtime.HasPendingCandidate, "发布被延后，但候选**保留**（不丢 pending）");
        Program.Check(optimizer.Calls == 1, "只训练了一次");
        Program.Check(!runtime.Publishing, "延后期间没有在途发布");

        defer = false;
        runtime.NotifyRoundBoundary("呈现已结束");
        Program.Check(runtime.WaitForIdle(TimeSpan.FromSeconds(15)), "条件允许后发布完成");
        Program.Check(!runtime.HasPendingCandidate && optimizer.Calls == 1,
            "延后的候选被真正发布出去，且没有为了发布再训一次");
    }

    private static void HelperPathResolutionMatchesPackagingLayout()
    {
        var resolver = typeof(MainWindow).GetMethod("MemoryResolveOptimizerHelperPath",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Program.Check(resolver is not null, "helper 定位是纯静态函数 MemoryResolveOptimizerHelperPath");

        var packaged = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fsrs-optimizer.exe" : "fsrs-optimizer");
        Program.Check(File.Exists(packaged),
            $"构建输出里 helper 与 Lexi.dll 同级（{packaged}）——Lexi.csproj 的 Content(Link=fsrs-optimizer) 生效");

        var bogus = Path.Combine(Path.GetTempPath(), "definitely-missing-fsrs-optimizer");
        try
        {
            Environment.SetEnvironmentVariable("LEXI_FSRS_OPTIMIZER_BIN", bogus);
            Program.Check((string?)resolver!.Invoke(null, null) != bogus, "不存在的环境变量路径不被采信");
            Environment.SetEnvironmentVariable("LEXI_FSRS_OPTIMIZER_BIN", packaged);
            Program.Check((string?)resolver.Invoke(null, null) == packaged, "存在的环境变量路径优先被采用");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEXI_FSRS_OPTIMIZER_BIN", null);
        }
        Program.Check((string?)resolver.Invoke(null, null) == packaged,
            "无环境变量时按打包平台同级 helper 定位");
    }

    // ==================================================================================
    // 夹具
    // ==================================================================================

    /// <summary>与 <c>UtcText</c> 同一口径：本地日历日 → UTC 时刻（不做 .Date 截断）。</summary>
    private static DateTime LocalDateToUtc(string date) =>
        DateTime.SpecifyKind(DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeKind.Local).ToUniversalTime();

    /// <summary>合成候选权重：与官方 defaults 有明显差异，但逐项落在官方 clip 区间内。</summary>
    private static Fsrs6Weights CandidateWeights(double w0 = 0.6)
    {
        var values = Fsrs6Weights.Defaults.ToArray();
        values[0] = w0;
        values[2] = 3.4;
        values[8] = 2.2;
        values[20] = 0.2;
        Program.Check(Fsrs6Weights.TryValidate(values, out var error),
            "合成候选权重落在官方 clip 区间内" + (error.Length == 0 ? "" : "：" + error));
        return Fsrs6Weights.Create(values, Fsrs6Weights.SourceOptimized, Anchor);
    }

    private static FsrsCardState ReplayCard(string wordKey, Fsrs6Weights weights)
    {
        var reviewed = new Fsrs6Scheduler(weights).Review(null, StudyRating.Known, Anchor).Card;
        return new FsrsCardState
        {
            WordKey = wordKey,
            Difficulty = reviewed.Difficulty, Stability = reviewed.Stability, Reps = reviewed.Reps,
            State = reviewed.State, LastReviewAtUtc = Anchor, NextReviewAtUtc = Anchor.AddDays(1),
            LastCanonicalRating = StudyRating.Known,
            FsrsAlgorithmVersion = SchedulingConfig.AlgorithmVersion,
            FsrsLibraryVersion = SchedulingConfig.LibraryVersion,
            FsrsParameterVersion = FsrsPersonalization.ParameterVersionOf(weights),
        };
    }

    private static CanonicalHistoryRow Row(
        string wordKey, string sessionId, CanonicalOrigin origin, DateTime at, int revision, StudyRating rating) =>
        new(wordKey, sessionId, origin, rating, at, at.AddMinutes(1))
        {
            CanonicalId = CanonicalReview.BuildId(wordKey, sessionId),
            Revision = revision,
        };

    private sealed record Written(DateTime At, string CanonicalId, StudyRating Rating);

    /// <summary>写入一串**自洽**的 canonical：每条的前置状态就是上一条应用后的卡片。</summary>
    private static List<Written> WriteSeries(
        ILearningMemoryStore store, string wordKey, DateTime start, int sessions, int stepDays, StudyRating rating)
    {
        var written = new List<Written>();
        for (var i = 0; i < sessions; i++)
        {
            var at = start.AddDays(i * stepDays);
            written.Add(WriteCanonical(store, wordKey, $"s{i:D3}", at, rating,
                i == 0 ? CanonicalOrigin.FirstLearnAggregate : CanonicalOrigin.FirstRetrieval,
                continueFromStore: i > 0));
        }
        return written;
    }

    private static void WriteSameDaySeries(ILearningMemoryStore store, string wordKey, DateTime start, int sessions)
    {
        for (var i = 0; i < sessions; i++)
            WriteCanonical(store, wordKey, $"d{i:D4}", start.AddMinutes(i), StudyRating.Known,
                i == 0 ? CanonicalOrigin.FirstLearnAggregate : CanonicalOrigin.FirstRetrieval,
                continueFromStore: i > 0);
    }

    /// <summary>追加一条 canonical（沿用当前卡片的模型链）。</summary>
    private static Written AppendCanonical(ILearningMemoryStore store, string wordKey, string sessionId, DateTime at,
        CanonicalOrigin origin = CanonicalOrigin.FirstRetrieval) =>
        WriteCanonical(store, wordKey, sessionId, at, StudyRating.Known, origin, continueFromStore: true);

    private static Written WriteCanonical(
        ILearningMemoryStore store, string wordKey, string sessionId, DateTime at, StudyRating rating,
        CanonicalOrigin origin, bool continueFromStore, DateTime? completedAtUtc = null, int revision = 0)
    {
        var scheduler = new Fsrs6Scheduler();
        var card = continueFromStore ? store.GetCard(wordKey) : null;
        var canonicalId = CanonicalReview.BuildId(wordKey, sessionId);
        var pre = FsrsPreState.From(card);
        var outcome = scheduler.Review(card?.Clone(), rating, at);
        var after = outcome.Card.Clone();
        after.WordKey = wordKey;
        after.LastReviewAtUtc = at;
        after.LastCanonicalRating = rating;
        after.NextReviewAtUtc = at.AddDays(Math.Max(1.0, outcome.BaselineIntervalDays));
        var summary = SummaryFor(wordKey, sessionId, at, rating);
        summary.CompletedAtUtc = completedAtUtc ?? at.AddMinutes(1);
        store.CommitWordSession(new WordSessionCommit(
            Session: null!, Summary: summary,
            Canonical: new CanonicalReview
            {
                CanonicalId = canonicalId,
                WordKey = wordKey,
                SessionId = sessionId,
                Origin = origin,
                Rating = rating,
                ReviewedAtUtc = at,
                CompletedAtUtc = completedAtUtc ?? at.AddMinutes(1),
                AggregationPolicyVersion = AggregationPolicy.Version,
                Revision = revision,
            },
            CardPreState: pre, CardAfter: after, Decision: null!,
            PendingEvents: [], SnapshotsToSave: [], SnapshotLabels: [], OutboxMutations: []));
        return new Written(at, canonicalId, rating);
    }

    private static WordSessionSummary SummaryFor(string wordKey, string sessionId, DateTime at, StudyRating rating) => new()
    {
        WordKey = wordKey,
        SessionId = sessionId,
        FirstPresentedAtUtc = at.AddMinutes(-2),
        CompletedAtUtc = at.AddMinutes(1),
        FirstInitialResponse = rating,
        FirstValidatedResponse = rating,
        TotalPresentations = 1,
        FinalKnownCount = rating == StudyRating.Known ? 1 : 0,
        FinalFuzzyCount = rating == StudyRating.Unsure ? 1 : 0,
        FinalForgottenCount = rating == StudyRating.Forgot ? 1 : 0,
    };

    private static FsrsCardState Recompute(List<Written> series, Fsrs6Weights weights)
    {
        var scheduler = new Fsrs6Scheduler(weights);
        FsrsCardState? card = null;
        foreach (var step in series) card = scheduler.Review(card?.Clone(), step.Rating, step.At).Card.Clone();
        return card!;
    }

    private static FsrsCardState RecomputeSeries(ILearningMemoryStore store, string wordKey, Fsrs6Weights weights)
    {
        var history = store.LoadCanonicalHistory(null, DateTime.MaxValue, wordKey);
        var scheduler = new Fsrs6Scheduler(weights);
        FsrsCardState? card = null;
        foreach (var row in history) card = scheduler.Review(card?.Clone(), row.Rating, row.ReviewedAtUtc).Card.Clone();
        return card!;
    }

    private static FsrsCardState NextPreState(List<Written> series, Fsrs6Weights weights, int index)
    {
        var scheduler = new Fsrs6Scheduler(weights);
        FsrsCardState? card = null;
        for (var i = 0; i <= index; i++) card = scheduler.Review(card?.Clone(), series[i].Rating, series[i].At).Card.Clone();
        return card!;
    }

    /// <summary>
    /// 合成用例用的收紧门槛：真实门槛（400 目标 / 30 天 / 新增 100 / 冷却 60 分钟）的**机制**
    /// 由 <c>GateIsCorrect</c> 单独断言，这里只把数值调小以便用几十条合成历史跑通整条链路。
    /// </summary>
    private static FsrsPersonalizationOptions TestOptions(
        int minimumReviews = 3, int minimumSpanDays = 2, int minimumNewReviews = 1,
        double cooldownMinutes = 60, double timeoutSeconds = 10) => new()
    {
        MinimumReviews = minimumReviews,
        MinimumSpanDays = minimumSpanDays,
        MinimumNewReviews = minimumNewReviews,
        Cooldown = TimeSpan.FromMinutes(cooldownMinutes),
        TrainingTimeout = TimeSpan.FromSeconds(timeoutSeconds),
    };

    /// <summary>新建一个只替换 fake 训练器的服务（用于跨重启的循环）。</summary>
    private static FsrsPersonalization NewServiceWithOptimizer(
        Sandbox box, out SchedulerWeights holder, FakeOptimizer optimizer, FsrsPersonalizationOptions options)
    {
        holder = new SchedulerWeights();
        var service = new FsrsPersonalization(box.Store, holder, optimizer, options);
        service.Restore();
        return service;
    }

    private static FsrsPersonalization NewService(
        Sandbox box, out SchedulerWeights holder, out FakeOptimizer optimizer,
        Fsrs6Weights? weights = null, bool reject = false, FsrsPersonalizationOptions? options = null)
    {
        holder = new SchedulerWeights();
        optimizer = new FakeOptimizer
        {
            Handler = reject ? null : (_, _) => System.Threading.Tasks.Task.FromResult(weights ?? CandidateWeights()),
        };
        var service = new FsrsPersonalization(box.Store, holder, optimizer, options ?? TestOptions());
        service.Restore();
        return service;
    }

    private static string ScalerJson()
    {
        var dimension = ContextFeatureProvider.FeatureCount;
        var ones = string.Join(",", Enumerable.Repeat("1", dimension));
        var zeros = string.Join(",", Enumerable.Repeat("0", dimension));
        return "{\"featureSchemaVersion\":\"" + SchedulingConfig.ContextFeatureSchemaVersion
            + "\",\"means\":[" + zeros + "],\"standardDeviations\":[" + ones + "],\"fills\":[" + zeros + "]}";
    }

    // ==================================================================================
    // 测试替身
    // ==================================================================================

    /// <summary>可控的训练器替身：绝不接触真实子进程。默认"拒绝"（未获可靠提升）。</summary>
    private sealed class FakeOptimizer : IFSRSParameterOptimizer
    {
        public bool IsImplemented { get; set; } = true;
        public Func<IReadOnlyList<CanonicalReview>, CancellationToken, System.Threading.Tasks.Task<Fsrs6Weights>>? Handler { get; set; }
        public int Calls { get; private set; }
        public IReadOnlyList<CanonicalReview>? LastHistory { get; private set; }

        public System.Threading.Tasks.Task<Fsrs6Weights> OptimizeAsync(
            IReadOnlyList<CanonicalReview> history, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastHistory = history;
            cancellationToken.ThrowIfCancellationRequested();
            if (Handler is null) throw new FsrsOptimizationException("（测试替身）留出验证未获可靠提升。");
            return Handler(history, cancellationToken);
        }
    }

    /// <summary>只实现被测路径用到成员的包装 store；其余一律抛，避免测试静默走偏。</summary>
    private abstract class WrapperStore : ILearningMemoryStore
    {
        protected WrapperStore(ILearningMemoryStore inner) => Inner = inner;
        protected ILearningMemoryStore Inner { get; }

        public virtual string DatabasePath => Inner.DatabasePath;
        public virtual void AppendEvent(LearningInteractionEvent e) => Inner.AppendEvent(e);
        public virtual IReadOnlyList<LearningInteractionEvent> LoadEvents(string sessionId) => Inner.LoadEvents(sessionId);
        public virtual IReadOnlyList<LearningInteractionEvent> LoadEventsForWord(string wordKey) => Inner.LoadEventsForWord(wordKey);
        public virtual void UpsertSession(LearningSession session) => Inner.UpsertSession(session);
        public virtual LearningSession? GetSession(string sessionId) => Inner.GetSession(sessionId);
        public virtual WordSessionCommitResult CommitWordSession(WordSessionCommit commit) => Inner.CommitWordSession(commit);
        public virtual bool InvalidateCanonical(string canonicalId, DateTime atUtc, string reason) => Inner.InvalidateCanonical(canonicalId, atUtc, reason);
        public virtual FsrsCardState? GetCard(string wordKey) => Inner.GetCard(wordKey);
        public virtual bool HasCard(string wordKey) => Inner.HasCard(wordKey);
        public virtual IReadOnlyList<DueCard> QueryDue(DateTime nowUtc, int limit) => Inner.QueryDue(nowUtc, limit);
        public virtual int CountDue(DateTime nowUtc) => Inner.CountDue(nowUtc);
        public virtual void SaveContextSnapshot(ContextFeatureSnapshot snapshot) => Inner.SaveContextSnapshot(snapshot);
        public virtual ContextFeatureSnapshot? GetContextSnapshot(string snapshotId) => Inner.GetContextSnapshot(snapshotId);
        public virtual ContextFeatureSnapshot? GetSnapshotByPresentation(string presentationId) => Inner.GetSnapshotByPresentation(presentationId);
        public virtual void LabelSnapshots(IReadOnlyList<ContextLabel> labels, DateTime labeledAtUtc) => Inner.LabelSnapshots(labels, labeledAtUtc);
        public virtual IReadOnlyList<LabeledContextSample> LoadLabeledSamples(DateTime? cutoffUtc = null) => Inner.LoadLabeledSamples(cutoffUtc);
        public virtual IReadOnlyList<CanonicalHistoryRow> LoadCanonicalHistory(DateTime? fromUtc, DateTime beforeUtc, string? wordKey) => Inner.LoadCanonicalHistory(fromUtc, beforeUtc, wordKey);
        public virtual CanonicalHistoryBounds? LoadCanonicalBounds(DateTime beforeUtc) => Inner.LoadCanonicalBounds(beforeUtc);
        public virtual IReadOnlyList<FsrsManualDueOverride> LoadManualDueOverrides() => Inner.LoadManualDueOverrides();
        public virtual int CountCardsWithUnattributedDue() => Inner.CountCardsWithUnattributedDue();
        public virtual bool HasPublishedPersonalParameters() => Inner.HasPublishedPersonalParameters();
        public virtual PersonalizationModelState? GetPersonalizationModel() => Inner.GetPersonalizationModel();
        public virtual void SavePersonalizationModel(PersonalizationModelState state) => Inner.SavePersonalizationModel(state);
        // 必须把**按 kind 的重载**也转发出去：接口的默认实现只支持 'context'，对 'fsrs_params'
        // 会抛 NotSupportedException。而集成层的训练记账正是按 'fsrs_params' 写的——
        // 一个不转发它的替身会让记账行被静默吞掉，测试于是变成空转。
        public virtual PersonalizationModelState? GetPersonalizationModel(string modelKind) => Inner.GetPersonalizationModel(modelKind);
        public virtual void SavePersonalizationModel(PersonalizationModelState state, string modelKind) => Inner.SavePersonalizationModel(state, modelKind);
        public virtual IReadOnlyList<PersonalizationModelState> LoadPersonalizationModels(string modelKind, int limit) => Inner.LoadPersonalizationModels(modelKind, limit);
        public virtual bool HasCardsOutsideParameterVersion(string parameterVersion) => Inner.HasCardsOutsideParameterVersion(parameterVersion);
        public virtual string ComputeCanonicalSignature() => Inner.ComputeCanonicalSignature();
        public virtual FsrsParameterPublishResult PublishFsrsParameters(FsrsParameterPublication publication) => Inner.PublishFsrsParameters(publication);
        public virtual void SaveSchedulerDecision(SchedulerDecision decision) => Inner.SaveSchedulerDecision(decision);
        public virtual IReadOnlyList<SchedulerDecision> LoadRecentDecisions(string wordKey, int limit) => Inner.LoadRecentDecisions(wordKey, limit);
        public virtual void EnqueueMutations(IReadOnlyList<PendingMutation> mutations) => Inner.EnqueueMutations(mutations);
        public virtual IReadOnlyList<PendingMutationRow> LoadPendingMutations() => Inner.LoadPendingMutations();
        public virtual void MarkMutationApplied(long id, DateTime atUtc) => Inner.MarkMutationApplied(id, atUtc);
        public virtual void RecordMutationFailure(long id, string error) => Inner.RecordMutationFailure(id, error);
    }

    /// <summary>发布事务必失败：用于验证"重放失败不得被当成一致"。</summary>
    private sealed class FailingPublishStore : WrapperStore
    {
        public FailingPublishStore(ILearningMemoryStore inner) : base(inner) { }
        public bool PublishAttempted { get; private set; }

        public override FsrsParameterPublishResult PublishFsrsParameters(FsrsParameterPublication publication)
        {
            PublishAttempted = true;
            throw new InvalidOperationException("（测试注入）事务发布失败。");
        }
    }

    /// <summary>发布事务"被拒"，并在 store 调用处停住，用于制造"事务已返回、续体还没跑"的窗口。</summary>
    private sealed class RejectingPublishStore : WrapperStore
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public RejectingPublishStore(ILearningMemoryStore inner) : base(inner) { }

        public bool WaitUntilPublishEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public void ReleasePublish() => _release.Set();

        public override FsrsParameterPublishResult PublishFsrsParameters(FsrsParameterPublication publication)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("发布闸门没有被放行（测试握手失败）。");
            // 返回"被拒"而不是真发布：收尾路径因此会尝试写一条拒绝记账——那正是代际守卫要拦住的副作用。
            return new FsrsParameterPublishResult(false, true, "（测试注入）快照在提交前发生变化。",
                Rejection: FsrsParameterPublishRejection.StaleSnapshot);
        }
    }

    /// <summary>参数历史读取必失败；<paramref name="throwOnProbe"/> 时连一致性探针也不可用。</summary>
    private sealed class ThrowingStore : WrapperStore
    {
        private readonly bool _throwOnProbe;

        public ThrowingStore(ILearningMemoryStore inner, bool throwOnProbe) : base(inner) => _throwOnProbe = throwOnProbe;

        public override IReadOnlyList<PersonalizationModelState> LoadPersonalizationModels(string modelKind, int limit)
            => throw new IOException("（测试注入）个人参数历史读取失败。");

        public override bool HasCardsOutsideParameterVersion(string parameterVersion) => _throwOnProbe
            ? throw new IOException("（测试注入）卡片版本探针失败。")
            : Inner.HasCardsOutsideParameterVersion(parameterVersion);

        public override string ComputeCanonicalSignature() => _throwOnProbe
            ? throw new IOException("（测试注入）canonical 签名不可用。")
            : Inner.ComputeCanonicalSignature();
    }

    /// <summary>闸门 store：<c>LoadLabeledSamples</c> 在碰连接**之前**握手并等待，避免测试自造跨线程竞争。</summary>
    private sealed class GatedStore : WrapperStore
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public GatedStore(ILearningMemoryStore inner) : base(inner) { }

        /// <summary>默认关闭：只有需要握手的那一次训练才打开闸门。</summary>
        public bool GateEnabled { get; set; }

        public bool WaitUntilSamplesRequested(TimeSpan timeout) => _entered.Wait(timeout);

        public void ReleaseSamples() => _release.Set();

        /// <summary>复位握手（同一实例上做第二轮闸门控制）。</summary>
        public void ResetGate()
        {
            _entered.Reset();
            _release.Reset();
        }

        public override IReadOnlyList<LabeledContextSample> LoadLabeledSamples(DateTime? cutoffUtc = null)
        {
            if (!GateEnabled) return Inner.LoadLabeledSamples(cutoffUtc);
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("闸门没有被放行（测试握手失败）。");
            return Inner.LoadLabeledSamples(cutoffUtc);
        }
    }

    /// <summary>
    /// 单线程泵式 <see cref="SynchronizationContext"/>：续体排队到专用泵线程执行。
    /// 这正是"UI 上下文"的可测替身——如果生产代码在上下文线程上同步等待自己的续体，
    /// 泵会被占住、队列永不清空，测试随即超时失败。
    /// </summary>
    private sealed class PumpingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly System.Threading.Thread _pump;
        private volatile bool _stopped;

        public PumpingSynchronizationContext()
        {
            _pump = new System.Threading.Thread(Pump) { IsBackground = true, Name = "lexi-test-pump" };
            _pump.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>把工作排到泵线程上执行（模拟"UI 线程发起"）。</summary>
        public void Run(Action action) => Post(_ => action(), null);

        /// <summary>
        /// 在泵线程上执行并等待它**返回**。返回值 false = 泵线程被这段工作占住没返回——
        /// 那正是"在 UI 线程上同步等待自己的续体"这类死锁的直接判据。
        /// </summary>
        public bool RunAndWait(Action action, TimeSpan timeout)
        {
            using var done = new ManualResetEventSlim(false);
            Post(_ =>
            {
                try { action(); }
                finally { done.Set(); }
            }, null);
            return done.Wait(timeout);
        }

        private void Pump()
        {
            // 让泵线程自己成为"拥有 UI 上下文的线程"：这样在它上面发起的 async 方法的续体
            // 会**回到它**上排队。若生产代码在它上面同步等待自己的续体，队列永远不被排空 → 死锁。
            SynchronizationContext.SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                if (_stopped) return;
                try { callback(state); }
                catch (Exception) { /* 测试里由断言负责暴露问题 */ }
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _queue.CompleteAdding();
            _pump.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }
    }

    private sealed class Sandbox : IDisposable
    {
        private VocabularyService? _service;
        private int _imported;

        public Sandbox()
        {
            Dir = Path.Combine(Path.GetTempPath(), "lexi-optint-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbPath = Path.Combine(Dir, "vocab.sqlite3");
            var archive = ServiceFactory.OpenArchive(DbPath);
            Store = ServiceFactory.OpenMemory(archive);
            _service = (VocabularyService)archive;
        }

        public string Dir { get; }
        public string DbPath { get; }
        public ILearningMemoryStore Store { get; private set; }
        public void Reopen()
        {
            _service?.Dispose();
            var archive = ServiceFactory.OpenArchive(DbPath);
            Store = ServiceFactory.OpenMemory(archive);
            _service = (VocabularyService)archive;
        }

        /// <summary>走真实导入通道建一个档案词（words + word_archives 成对），返回它的 words.id。</summary>
        public long ImportLegacyWord(string word, string nextReviewDate)
        {
            var service = _service ?? throw new InvalidOperationException("沙箱服务未打开。");
            var item = new WordItem
            {
                Word = word,
                Stage = 1,
                Status = "learning",
                NextReviewDate = nextReviewDate,
                LearningStartDate = "2026-01-01",
                CreatedAt = "2026-01-01T00:00:00.0000000Z",
                ReviewCount = 1,
                Archive = new ArchiveMetadata
                {
                    Uuid = Guid.NewGuid().ToString("D"),
                    SourceType = "manual",
                    SourceTitle = "fixture",
                    CreatedAtUtc = "2026-01-01T00:00:00.0000000Z",
                    UpdatedAtUtc = "2026-01-01T00:00:00.0000000Z",
                    LastEncounteredAtUtc = "2026-01-01T00:00:00.0000000Z",
                },
            };
            var (added, _) = service.ImportArchive([item]);
            if (added != 1) throw new InvalidOperationException("夹具导入失败：" + word);
            _imported = (int)ScalarInt(DbPath, $"SELECT id FROM words WHERE word = '{word}' ORDER BY id DESC LIMIT 1");
            return _imported;
        }

        /// <summary>
        /// 真实管理动作入口。走的是界面批量"完成本次复习"调用的**同一个** <c>SyncManualReviewDue</c>；
        /// 它是 <c>internal</c>（asssembly 内可见），而 MemoryTests 没有 InternalsVisibleTo，
        /// 所以只能反射调用——先断言方法还在，避免改名后测试静默空转。
        /// </summary>
        public void SyncManualReviewDue(long wordId)
        {
            var service = _service ?? throw new InvalidOperationException("沙箱服务未打开。");
            var method = typeof(VocabularyService).GetMethod("SyncManualReviewDue",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            if (method is null) throw new InvalidOperationException("找不到管理动作入口 SyncManualReviewDue。");
            method.Invoke(service, [new[] { wordId }]);
        }

        public void Dispose()
        {
            // 夹具销毁**绝不能**掩盖真正的断言失败：如果这里抛异常，它会在栈展开时
            // 顶掉原始异常，测试报告就只剩一个与产品无关的 Dispose 错误。
            try { _service?.Dispose(); }
            catch (Exception) { }
            _service = null;
            try { Directory.Delete(Dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ==================================================================================
    // 只读 SQL / 写入辅助（只碰本用例自己的临时库）
    // ==================================================================================

    private static SqliteConnection Connect(string dbPath) => new($"Data Source={dbPath};Mode=ReadOnly");

    private static long ScalarInt(string dbPath, string sql)
    {
        using var connection = Connect(dbPath);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static double ScalarDouble(string dbPath, string sql)
    {
        using var connection = Connect(dbPath);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? double.NaN : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static string ScalarText(string dbPath, string sql)
    {
        using var connection = Connect(dbPath);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static string Snapshot(string dbPath, string sql)
    {
        using var connection = Connect(dbPath);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var builder = new System.Text.StringBuilder();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
                builder.Append(reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).Append('|');
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static void Execute(string dbPath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>造一批 Context 训练样本（快照 + 标签 + 配套 FirstRetrieval canonical 锚点）。</summary>
    private static void SeedContextSamples(
        ILearningMemoryStore store, string prefix, DateTime start, int count, double stepHours)
    {
        var features = ContextFeatureProvider.FeatureCount;
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var tx = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            var at = start.AddHours(i * stepHours);
            var snapshotId = $"{prefix}-{i:D4}";
            var values = new double[features];
            for (var j = 0; j < features; j++) values[j] = (i + j) % 7;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO context_snapshots(snapshot_id, word_key, session_id, presentation_id, captured_at_utc,
                    fsrs_difficulty_at_capture, fsrs_stability_at_capture, fsrs_retrievability_at_capture,
                    feature_schema_version, model_version, features_json, missing_flags_json,
                    label, confident_recall, labeled_at_utc)
                VALUES($sid, 'archive:seed', $sess, $pid, $at, 5.0, 10.0, 0.85, $schema, 'seed', $features, '[]',
                    $label, 1, $at);
                INSERT INTO canonical_reviews(canonical_id, word_key, session_id, source_presentation_id, origin,
                    rating, reviewed_at_utc, completed_at_utc)
                VALUES($cid, 'archive:seed', $sess, $pid, 'FirstRetrieval', $label, $at, $at);
                """;
            cmd.Parameters.AddWithValue("$sid", snapshotId);
            cmd.Parameters.AddWithValue("$sess", "s-" + snapshotId);
            cmd.Parameters.AddWithValue("$pid", "p-" + snapshotId);
            cmd.Parameters.AddWithValue("$at", at.ToString("O"));
            cmd.Parameters.AddWithValue("$schema", SchedulingConfig.ContextFeatureSchemaVersion);
            cmd.Parameters.AddWithValue("$features", JsonSerializer.Serialize(values));
            cmd.Parameters.AddWithValue("$cid", CanonicalReview.BuildId("archive:seed", "s-" + snapshotId));
            cmd.Parameters.AddWithValue("$label", (i % 2 == 0 ? StudyRating.Known : StudyRating.Forgot).ToString());
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
