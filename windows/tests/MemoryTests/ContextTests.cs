using System.Reflection;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Lexi;

namespace Lexi.Tests;

/// <summary>
/// context 组（P6）：作答前特征（<see cref="ContextFeatureProvider"/>）与残差校准器（<see cref="ContextCalibrator"/>）。
///
/// <para><b>这一组证明什么、不证明什么</b></para>
/// <list type="bullet">
/// <item>证明：特征不泄漏（时间上界可测）、训练/验证严格时间序切分、ColdStart→Shadow→Active 的**门机制**、
/// 安全回退与旋转门（rolling 变差退回 Shadow）、锁存求根的正确性、scaler 冻结、审计字段齐全。</item>
/// <item><b>不证明</b>：上下文校准对真实用户的收益。除「泄漏」与「端到端接线」两组用真实 SQLite store +
/// 真实协调器之外，其余都用**合成数据**，它们只能证明资格门的机制成立（合成数据下当然是通过的），
/// 不能替代真实用户数据上的 held-out 验证。</item>
/// </list>
///
/// 所有断言都在自己的临时目录里建库（<c>Path.GetTempPath()/lexi-context-test-&lt;guid&gt;</c>），
/// 结束时整目录删除；绝不读写真实用户数据目录。
/// </summary>
public static class ContextTests
{
    /// <summary>固定 UTC 锚点（避开本地日界，便于 reviewsToday 的断言与机器时区无关）。</summary>
    private static readonly DateTime Anchor = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();

    /// <summary>合成数据的基线可回忆性。0.5 ⇒ logit = 0 ⇒ 基线自身完全无信息，Context 的改善/恶化都能被放大观察。</summary>
    private const double SyntheticRetrievability = 0.5;

    /// <summary>承载合成信号的特征下标（<c>global_recall_rate_7d</c>）。</summary>
    private const int SignalFeatureIndex = 7;

    public static void Run()
    {
        FeatureVectorIsWellFormed();
        HistoryAfterCaptureIsInvisible();
        SameRoundFutureDoesNotLeakThroughCoordinator();
        TrainingSplitIsStrictlyTimeOrdered();
        ColdStartOnlyCollects();
        ShadowRecordsButNeverApplies();
        PublishedFrozenGateRejectsOldData();
        ActiveOnlyAfterIndependentHeldOutImprovement();
        RollingDeteriorationDemotesActiveToShadow();
        BrokenModelFallsBackAndKeepsLastGood();
        DisabledSwitchShortCircuitsEverything();
        SolveCandidateHitsDesiredRetention();
        SolveCandidateReturnsNullWhenNotMonotone();
        ScalerIsFrozenAtTrainingTime();
        MetricsJsonIsAuditable();
        TrainingTriggerGateIsCorrect();
    }

    // ==================================================================================
    // 1. 特征向量自身的契约
    // ==================================================================================

    private static void FeatureVectorIsWellFormed()
    {
        var names = ContextFeatureProvider.FeatureNames;
        Program.Check(names.Length == ContextFeatureProvider.FeatureCount && names.Length > 0,
            $"特征表非空且 FeatureCount 与名字表一致（{names.Length} 条）");
        Program.Check(names.Distinct(StringComparer.Ordinal).Count() == names.Length, "特征名互不重复");
        Program.Check(names.All(n => !string.IsNullOrWhiteSpace(n) && n == n.ToLowerInvariant()),
            "特征名一律为稳定的英文小写标识");

        using var box = new Sandbox();
        var provider = new ContextFeatureProvider(box.Store, new Fsrs6Scheduler());
        var vector = provider.Build(new ContextFeatureRequest(
            "archive:x", "", "p1", Anchor, LearningMode.Review, true, 1, 0, null));

        Program.Check(vector.Values.Length == names.Length && vector.Missing.Length == names.Length,
            "Build 的 Values / Missing 与名字表等长（协调器会断言三者一致）");
        Program.Check(vector.Values.All(MemoryScheduler.IsFinite), "所有特征值有限（非有限值无法进 JSON）");
        Program.Check(vector.Missing.Length == vector.Values.Length && names.Zip(vector.Names).All(p => p.First == p.Second),
            "Build 返回的名字数组就是规范顺序");

        // 无历史时「没有数据」必须表现为缺失，而不是伪造 0。
        var idx = IndexOf("prev_round_presentations");
        Program.Check(vector.Missing[idx], "没有上一已完成轮 → prev_round_presentations 标为缺失（不是 0）");
        Program.Check(vector.Values[idx] == 0, "缺失项的值填中性 0");
        Program.Check(!vector.Missing[IndexOf("reviews_today")], "reviews_today 是可靠的计数（0 有意义，不标缺失）");

        // Align：应用重启后回读的向量名字为空，只能靠「schema 相同 ⇒ 顺序相同」；
        // 维数不符必须**抛**而不是错位训练。
        var aligned = ContextFeatureVector.Align(vector, names);
        Program.Check(aligned.Length == names.Length, "Align 接受维数匹配的向量");
        var threw = false;
        try { ContextFeatureVector.Align(new ContextFeatureVector([1, 2], [], [false, false]), names); }
        catch (InvalidDataException) { threw = true; }
        Program.Check(threw, "Align 在维数与规范 schema 不符时抛 InvalidDataException（拒绝错位训练）");

        var misnamed = false;
        try { ContextFeatureVector.Align(new ContextFeatureVector(vector.Values, names.Reverse().ToArray(), vector.Missing), names); }
        catch (InvalidDataException) { misnamed = true; }
        Program.Check(misnamed, "Align 在名字顺序错位时抛 InvalidDataException");
    }

    // ==================================================================================
    // 2. 泄漏：快照时刻之后写入的历史结构性不可见
    // ==================================================================================

    private static void HistoryAfterCaptureIsInvisible()
    {
        var word = "archive:leak";
        var after = new Sandbox();
        var before = new Sandbox();
        try
        {
            // 同一条「上一个已完成轮」：A 沙箱写在校准时刻**之后**，B 沙箱写在**之前**。
            var reviewedLate = Anchor.AddHours(2);
            var completedLate = Anchor.AddHours(3);
            var reviewedEarly = Anchor.AddHours(-26);
            var completedEarly = Anchor.AddHours(-25);

            WriteRound(after.Store, word, "s-after", reviewedLate, completedLate, StudyRating.Known,
                CanonicalOrigin.FirstRetrieval, presentations: 5);
            WriteRound(before.Store, word, "s-before", reviewedEarly, completedEarly, StudyRating.Known,
                CanonicalOrigin.FirstRetrieval, presentations: 5);

            var providerAfter = new ContextFeatureProvider(after.Store, new Fsrs6Scheduler());
            var providerBefore = new ContextFeatureProvider(before.Store, new Fsrs6Scheduler());
            var vectorAfter = providerAfter.Build(RequestFor(word, Anchor, after.Store));
            var vectorBefore = providerBefore.Build(RequestFor(word, Anchor, before.Store));

            // —— 时刻之后的那一轮：整个 prev_round 组必须缺失，且不计入任何窗口统计 ——
            foreach (var name in PrevRoundNames)
                Program.Check(vectorAfter.Missing[IndexOf(name)],
                    $"写在校准时刻之后的上一轮摘要不可见：{name} 标为缺失");
            Program.Check(vectorAfter.Values[IndexOf("word_canonical_count")] == 0,
                "写在校准时刻之后的 canonical 不计入 word_canonical_count");
            Program.Check(vectorAfter.Values[IndexOf("canonical_count_7d")] == 0,
                "写在校准时刻之后的 canonical 不计入近 7 天 canonical 数");
            Program.Check(vectorAfter.Missing[IndexOf("global_recall_rate_7d")],
                "近 7 天没有合格真实复习 → 三分类率标为缺失（0% 与「无数据」必须可区分）");

            // —— 把同一条摘要/canonical 移到校准时刻之前：特征必须**变了**（说明它确实被用到了）——
            Program.Check(!vectorBefore.Missing[IndexOf("prev_round_presentations")],
                "移到校准时刻之前后，上一轮特征变为可得");
            Program.CheckClose(vectorBefore.Values[IndexOf("prev_round_presentations")], 5, 1e-12,
                "上一轮的 presentations 被如实读出");
            Program.Check(vectorBefore.Values[IndexOf("word_canonical_count")] == 1,
                "移到校准时刻之前的 canonical 计入 word_canonical_count");
            Program.Check(vectorBefore.Values[IndexOf("canonical_count_7d")] == 1,
                "移到校准时刻之前（26 小时前）的 canonical 计入近 7 天");
            Program.Check(vectorBefore.Values[IndexOf("global_recall_rate_7d")] == 1.0,
                "唯一一条真实复习是 Known → 近 7 天 recall 率 = 1");

            var differing = 0;
            for (var i = 0; i < vectorAfter.Values.Length; i++)
                if (Math.Abs(vectorAfter.Values[i] - vectorBefore.Values[i]) > 1e-12 || vectorAfter.Missing[i] != vectorBefore.Missing[i])
                    differing++;
            Program.Check(differing > 0,
                $"同一份历史在校准时刻前后两个位置产生**不同**的特征（差异维度 {differing}）——证明时间上界真的在起作用");

            // —— 撤销掉的 canonical 不是发生过的事实 ——
            // 用独立的沙箱与独立的词：窗口统计是**全局**口径，混在同一个库里会被别的词的 canonical 干扰；
            // 而且 InvalidateCanonical 有水位守卫（只允许撤销该词最新一条有效 canonical），
            // 所以这一轮必须带卡片写。
            using var undo = new Sandbox();
            var undoTarget = "archive:leak-undo";
            WriteRound(undo.Store, undoTarget, "s-undo", Anchor.AddHours(-3), Anchor.AddHours(-2),
                StudyRating.Known, CanonicalOrigin.FirstRetrieval, presentations: 3, withCard: true);
            var undoProvider = new ContextFeatureProvider(undo.Store, new Fsrs6Scheduler());
            var undoBefore = undoProvider.Build(RequestFor(undoTarget, Anchor, undo.Store));
            Program.Check(undoBefore.Values[IndexOf("word_canonical_count")] == 1
                && undoBefore.Values[IndexOf("canonical_count_7d")] == 1
                && undoBefore.Values[IndexOf("global_recall_rate_7d")] == 1.0,
                "撤销前该 canonical 同时计入词级与窗口统计（前提）");
            Program.Check(undo.Store.InvalidateCanonical(CanonicalReview.BuildId(undoTarget, "s-undo"), Anchor, "test"),
                "撤销调用成功（水位守卫通过）");
            var undoAfter = undoProvider.Build(RequestFor(undoTarget, Anchor, undo.Store));
            Program.Check(undoAfter.Values[IndexOf("word_canonical_count")] == 0
                && undoAfter.Values[IndexOf("canonical_count_7d")] == 0,
                "被撤销的 canonical 不计入历史（invalidated = 1 的行一律排除）");
            Program.Check(undoAfter.Missing[IndexOf("global_recall_rate_7d")],
                "撤销后该窗口没有合格真实复习 → 三分类率标为缺失（而不是 0%）");
        }
        finally
        {
            after.Dispose();
            before.Dispose();
        }
    }

    private static void SameRoundFutureDoesNotLeakThroughCoordinator()
    {
        using var box = new Sandbox();
        var clock = new FakeClock(Anchor);
        var scheduler = new Fsrs6Scheduler();
        var provider = new ContextFeatureProvider(box.Store, scheduler);
        var coordinator = new LearningMemoryCoordinator(box.Store, scheduler, provider, null, () => clock.Now);

        var word = WordKey.Archive("same-round");

        // 先造一轮真实历史（该词在此之前已经有卡片与一次已完成轮），让 prev_round 组非缺失。
        WriteRound(box.Store, word.Key, "s-prev", Anchor.AddDays(-9), Anchor.AddDays(-9).AddMinutes(3),
            StudyRating.Known, CanonicalOrigin.FirstRetrieval, presentations: 4, withCard: true);
        // 再补一条 Presented 事件：协调器按「全历史第 1 次出现」判定 FirstLearn，
        // 没有这条事件它会把本次当成首发学习（Mode 会变），场景就不叫「复习」了。
        box.Store.AppendEvent(new LearningInteractionEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            SessionId = "s-prev",
            WordKey = word.Key,
            PresentationId = "p-seed",
            OccurredAtUtc = Anchor.AddDays(-9),
            LearningMode = LearningMode.Review,
            Kind = InteractionEventKind.Presented,
            SessionAppearanceIndex = 1,
            WordAppearanceIndex = 1,
            IsFirstAppearanceForWord = true,
            IsRecall = true,
        });

        var sessionId = coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        var captured = new List<(string Pid, DateTime At)>();
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            var pid = $"p{i}";
            coordinator.OnPresented(word, pid, true);
            captured.Add((pid, clock.Now));
            coordinator.OnRated(word, pid, StudyRating.Known, i, i + 1, 900);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        // 作答前那一刻的卡片（本轮没有任何东西改过它）——重建请求必须用它，而不是定稿之后的卡片。
        var cardAtCapture = box.Store.GetCard(word.Key);
        var commit = coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(commit is not null && commit.Applied && !commit.AlreadyApplied, "本轮定稿成功");

        // 定稿确实把「本轮」写进了历史：canonical 落在第一次 retrieval 的时刻，摘要落在定稿时刻。
        var history = box.Store.LoadCanonicalHistory(null, clock.Now.AddSeconds(1), word.Key);
        Program.Check(history.Count == 2, $"定稿后该词有 2 条 canonical（上一轮 + 本轮），实际 {history.Count}");
        Program.Check(history.Any(r => r.ReviewedAtUtc == captured[0].At),
            "本轮的 canonical 恰好以第一次 retrieval 的时刻落库（因此它正好落在上界的边界上）");
        var summaries = box.Store.LoadCompletedSummaries(word.Key, clock.Now.AddSeconds(1));
        Program.Check(summaries.Count == 2, $"定稿后该词有 2 条已完成摘要，实际 {summaries.Count}");

        var snapshot = box.Store.GetSnapshotByPresentation(captured[0].Pid);
        Program.Check(snapshot is not null, "第一次 retrieval 的作答前快照已落库");
        var storedValues = JsonSerializer.Deserialize<double[]>(snapshot!.FeaturesJson)!;
        var storedMissing = JsonSerializer.Deserialize<bool[]>(snapshot.MissingFlagsJson)!;

        var rebuilt = provider.Build(new ContextFeatureRequest(
            word.Key, sessionId, captured[0].Pid, captured[0].At,
            LearningMode.Review, true, 1, 0, cardAtCapture));

        Program.Check(rebuilt.Values.Length == storedValues.Length,
            $"重建向量维数一致（{rebuilt.Values.Length}）");
        var maxDelta = rebuilt.Values.Zip(storedValues).Max(p => Math.Abs(p.First - p.Second));
        Program.Check(maxDelta <= 1e-12,
            $"整轮结束、本轮摘要与 canonical 都已落库之后重建的特征与作答前快照**逐位一致**（最大偏差 {maxDelta:R}）");
        Program.Check(rebuilt.Missing.SequenceEqual(storedMissing), "重建的缺失标记也与快照一致");

        // 反向证据：这条断言不是「什么都没变」的空转——本轮摘要确实存在，只是被上界挡住了。
        var summaryVisibleAfterTheFact = box.Store.LoadCompletedSummaries(word.Key, clock.Now.AddSeconds(1));
        Program.Check(summaryVisibleAfterTheFact[^1].SessionId == sessionId,
            "本轮摘要在定稿之后确实存在于 store（只是上界让它对作答前特征不可见）");
        var noPrev = provider.Build(new ContextFeatureRequest(
            word.Key, sessionId, "p-none", Anchor.AddDays(-30), LearningMode.Review, true, 1, 0, null));
        Program.Check(noPrev.Missing[IndexOf("prev_round_presentations")],
            "在更早时刻构造时上一轮还不存在 → 该特征标为缺失（说明缺失不是由重建路径伪造的）");
    }

    // ==================================================================================
    // 3. 训练 / 验证的时间序切分
    // ==================================================================================

    private static void TrainingSplitIsStrictlyTimeOrdered()
    {
        using var box = new Sandbox();
        var rows = SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, invertFromIndex: int.MaxValue);
        SeedDataset(box.Store, "split", rows);

        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        var result = calibrator.TrainAsync(Anchor.AddDays(120)).GetAwaiter().GetResult();
        Program.Check(result.Outcome == ContextTrainingOutcome.Trained,
            $"样本满足门槛 → 训练成功（实际 {result.Outcome}：{result.Detail}）");

        var metrics = MetricsOf(box.Store);
        var cutoff = DateTime.Parse(metrics.GetProperty("cutoffUtc").GetString()!, null,
            System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        var trainSamples = metrics.GetProperty("trainSamples").GetInt32();
        var validationSamples = metrics.GetProperty("validationSamples").GetInt32();

        Program.Check(trainSamples + validationSamples == rows.Count,
            $"训练段 + 验证段 = 全部可评估样本（{trainSamples} + {validationSamples} = {rows.Count}）");
        Program.Check(cutoff == rows[trainSamples].At,
            "训练/验证的时间界限恰是验证段第一条样本的时刻（按时间序切分，不是按行号随机切）");

        // 逐条断言：训练段**全部**早于验证段**最小**时刻（严格小于，不是 ≤）。
        var trainMax = rows.Take(trainSamples).Max(r => r.At);
        var validationMin = rows.Skip(trainSamples).Min(r => r.At);
        Program.Check(trainMax < cutoff && cutoff == validationMin,
            $"训练段最大时刻（{trainMax:O}）严格早于验证段最小时刻（{validationMin:O}）");
        Program.Check(rows.Take(trainSamples).All(r => r.At < cutoff) && rows.Skip(trainSamples).All(r => r.At >= cutoff),
            "训练段每一条都 < cutoff，验证段每一条都 ≥ cutoff（没有任何一条跨界）");
        Program.CheckClose(validationSamples / (double)rows.Count, 1 - SchedulingConfig.ContextTrainFraction, 0.02,
            "验证段占比 ≈ 1 − ContextTrainFraction（时间序切分）");
    }

    // ==================================================================================
    // 4. Cold Start：只收集
    // ==================================================================================

    private static void ColdStartOnlyCollects()
    {
        using var box = new Sandbox();
        SeedDataset(box.Store, "cold", SyntheticSeries(12, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue));

        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        var result = calibrator.TrainAsync(Anchor.AddDays(30)).GetAwaiter().GetResult();

        Program.Check(result.Outcome == ContextTrainingOutcome.ColdStart, $"样本不足 → ColdStart（{result.Detail}）");
        Program.Check(calibrator.Mode == ContextMode.ColdStart, "Mode == ColdStart");
        Program.Check(box.Store.GetPersonalizationModel() is null, "ColdStart 不写任何模型行（只收集）");

        var vector = ContextFeatureProviderFor(box).Build(RequestFor("archive:cold", Anchor.AddDays(30), box.Store));
        var adjustment = calibrator.Adjust(vector, SyntheticRetrievability);
        Program.Check(!adjustment.Applied, "ColdStart 的 Adjust.Applied == false");
        Program.Check(adjustment.Delta == 0, "ColdStart 的 Adjust.Delta == 0");
        Program.Check(adjustment.Mode == ContextMode.ColdStart, "Adjust 回报 ColdStart");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 50, adjustment), 10, 1e-12,
            "ColdStart 下最终间隔 = 纯 FSRS 基线");
        Program.Check(calibrator.SolveCandidate(vector, 10, SchedulingConfig.DesiredRetention) is null,
            "ColdStart 不求根（返回 null → 回退基线）");
    }

    // ==================================================================================
    // 5. Shadow：训练并记录，但实际排期仍纯 FSRS
    // ==================================================================================

    private static void ShadowRecordsButNeverApplies()
    {
        using var box = new Sandbox();
        SeedDataset(box.Store, "shadow", SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue));

        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        var result = calibrator.TrainAsync(Anchor.AddDays(120)).GetAwaiter().GetResult();

        Program.Check(result.Outcome == ContextTrainingOutcome.Trained, $"门槛满足 → 完成一次训练（{result.Detail}）");
        Program.Check(calibrator.Mode == ContextMode.Shadow,
            "首次通过资格门只能进 Shadow——数量门槛**不是**自动激活条件");

        var metrics = MetricsOf(box.Store);
        Program.Check(metrics.GetProperty("gatePassed").GetBoolean(),
            "该轮资格门确实通过（否则下面的断言会变成空转）");
        Program.Check(metrics.GetProperty("baselineLogLoss").GetDouble() > 0
            && metrics.GetProperty("contextLogLoss").GetDouble() > 0
            && metrics.GetProperty("baselineBrier").GetDouble() > 0
            && metrics.GetProperty("contextBrier").GetDouble() > 0,
            "Shadow 阶段记录了 baseline 与 context 两套预测的评估值");
        var predictions = metrics.GetProperty("predictions");
        Program.Check(predictions.GetArrayLength() > 0,
            "Shadow 阶段记录了逐条的 [baseline, context, outcome] 冻结预测");

        var vector = ContextFeatureProviderFor(box).Build(RequestFor("archive:shadow", Anchor.AddDays(120), box.Store));
        var adjustment = calibrator.Adjust(vector, SyntheticRetrievability);
        Program.Check(!adjustment.Applied, "Shadow 的 Adjust.Applied == false（实际排期仍纯 FSRS）");
        Program.Check(adjustment.Delta == 0, "Shadow 的 Delta == 0");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 50, adjustment), 10, 1e-12,
            "Shadow 下最终间隔 = 纯 FSRS 基线");
        Program.Check(calibrator.SolveCandidate(vector, 10, SchedulingConfig.DesiredRetention) is null,
            "Shadow 不产生候选间隔");
    }

    // ==================================================================================
    // 6. Active 必须由之后的 held-out 观测批准
    // ==================================================================================

    private static void PublishedFrozenGateRejectsOldData()
    {
        using var box = new Sandbox();
        SeedDataset(box.Store, "publish", SyntheticSeries(240, 6, SignalFeatureIndex, int.MaxValue));
        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        var published = Anchor.AddDays(120);
        calibrator.TrainAsync(published).GetAwaiter().GetResult();
        var version = calibrator.CurrentModel!.Version;
        var coefficients = calibrator.CurrentModel.ToJson();
        var scaler = calibrator.FrozenScaler!.ToJson();
        SeedDataset(box.Store, "old-extra", SyntheticSeries(2, 6, SignalFeatureIndex, int.MaxValue, startIndex: 240));
        calibrator.TrainAsync(published.AddHours(3)).GetAwaiter().GetResult();
        Program.Check(calibrator.Mode == ContextMode.Shadow, "旧验证数据加两条不能批准 Active");
        var restarted = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        restarted.TrainAsync(published.AddHours(6)).GetAwaiter().GetResult();
        Program.Check(restarted.Mode == ContextMode.Shadow, "重启保留候选发布边界，不重复批准旧数据");
        SeedDataset(box.Store, "future", SyntheticSeries(160, 6, SignalFeatureIndex, int.MaxValue, startIndex: 481));
        restarted.TrainAsync(Anchor.AddDays(161)).GetAwaiter().GetResult();
        Program.Check(restarted.Mode == ContextMode.Active, "真正发布后样本批准冻结候选 Active");
        Program.Check(restarted.CurrentModel!.Version == version, "确认评估沿用候选模型版本");
        Program.Check(restarted.CurrentModel.ToJson() == coefficients, "确认段不重新拟合系数");
        Program.Check(restarted.FrozenScaler!.ToJson() == scaler, "确认段不重新拟合 scaler");
        Program.Check(MetricsOf(box.Store).GetProperty("confirmation").GetProperty("samples").GetInt32() == 160,
            "确认段只含发布后的160条，排除旧242条");
    }

    private static void ActiveOnlyAfterIndependentHeldOutImprovement()
    {
        // —— (a) context 确实更好 → 第二轮由**独立确认段**批准进入 Active ——
        using (var box = new Sandbox())
        {
            var first = SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue);
            SeedDataset(box.Store, "act", first);
            var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());

            var round1 = calibrator.TrainAsync(Anchor.AddDays(60)).GetAwaiter().GetResult();
            Program.Check(round1.Outcome == ContextTrainingOutcome.Trained && calibrator.Mode == ContextMode.Shadow,
                "第一轮：通过资格门但只进 Shadow");
            var firstCutoff = DateTime.Parse(MetricsOf(box.Store).GetProperty("cutoffUtc").GetString()!,
                null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            Program.Check(MetricsOf(box.Store).GetProperty("gateCutoffUtc").ValueKind == JsonValueKind.String,
                "第一轮把 gate 通过的时间界限记为「待确认」（迟滞的锚点）");

            // 之后新产生的数据（时刻全部晚于第一轮的 cutoff）——这才是「独立确认段」。
            var extra = SyntheticSeries(160, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue,
                startIndex: first.Count);
            SeedDataset(box.Store, "act2", extra);

            var round2 = calibrator.TrainAsync(Anchor.AddDays(120).AddHours(3)).GetAwaiter().GetResult();
            Program.Check(round2.Outcome == ContextTrainingOutcome.Trained, $"第二轮训练完成（{round2.Detail}）");
            Program.Check(calibrator.Mode == ContextMode.Active,
                "第二轮：独立确认段上的 held-out 改善成立 → 进入 Active");
            var metrics = MetricsOf(box.Store);
            var confirmation = metrics.GetProperty("confirmation");
            Program.Check(confirmation.ValueKind == JsonValueKind.Object && confirmation.GetProperty("passed").GetBoolean(),
                "确认段明确记录为通过");
            Program.Check(confirmation.GetProperty("samples").GetInt32() >= SchedulingConfig.ContextMinimumActiveConfirmationSamples,
                $"确认段样本数 {confirmation.GetProperty("samples").GetInt32()} ≥ {SchedulingConfig.ContextMinimumActiveConfirmationSamples}");
            var confirmCutoff = DateTime.Parse(metrics.GetProperty("cutoffUtc").GetString()!, null,
                System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            Program.Check(firstCutoff == confirmCutoff,
                "冻结确认不改变原模型训练截止时刻");
            var evaluatedAt = DateTime.Parse(metrics.GetProperty("evaluationAsOfUtc").GetString()!, null,
                System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            Program.Check(evaluatedAt == Anchor.AddDays(120).AddHours(3), "独立记录确认评估发生时刻");
            Program.Check(box.Store.GetPersonalizationModel()!.TrainCutoffUtc == firstCutoff,
                "持久化TrainCutoffUtc保留原训练截止");
            Program.Check(metrics.GetProperty("logLossImprovement").GetDouble() >= SchedulingConfig.ContextMinimumLogLossImprovement
                && metrics.GetProperty("brierImprovement").GetDouble() >= SchedulingConfig.ContextMinimumLogLossImprovement,
                "LogLoss 与 Brier 都达到改善门槛");

            var vector = ContextFeatureProviderFor(box).Build(RequestFor("archive:act", Anchor.AddDays(120).AddHours(3), box.Store));
            var adjustment = calibrator.Adjust(vector, SyntheticRetrievability);
            Program.Check(adjustment.Applied, "Active 的 Adjust.Applied == true");
            Program.Check(adjustment.Delta != 0, $"Active 的 Delta != 0（实际 {adjustment.Delta:R}）");
            Program.Check(adjustment.Mode == ContextMode.Active, "Adjust 回报 Active");
            Program.Check(Math.Abs(adjustment.Delta) <= SchedulingConfig.ContextMaxAbsDelta,
                $"|Δ| 不超过 {SchedulingConfig.ContextMaxAbsDelta}（极端系数保护）");

            // 校准后的间隔落在 [0.5, 1.5] × 基线内（协调器侧的同一函数）。
            var candidate = calibrator.SolveCandidate(vector, 10, SchedulingConfig.DesiredRetention);
            Program.Check(candidate is not null && candidate > 0, "Active 下给出有限的候选间隔");
            var final = MemoryScheduler.ResolveFinalInterval(10, candidate, adjustment);
            Program.Check(final >= 10 * SchedulingConfig.ClampLowerRatio - 1e-9
                && final <= 10 * SchedulingConfig.ClampUpperRatio + 1e-9,
                $"最终间隔被夹在 [0.5, 1.5] × 基线内（candidate={candidate:R}, final={final:R}）");
            Program.Check(final != 10, $"该样本上校准真的改变了间隔（final={final:R} ≠ baseline=10）");
        }

        // —— (b) context 更差 → 绝不进入 Active ——
        using (var box = new Sandbox())
        {
            // 训练段（前 70%）里信号与结果一致，验证段（后 30%）里两者相关方向相反：
            // 这正是「训练过去、验证未来」才会暴露的问题；随机 80/20 会把两段混在一起而看不出。
            var rows = SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, invertFromIndex: 168);
            SeedDataset(box.Store, "bad", rows);
            var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());

            var round1 = calibrator.TrainAsync(Anchor.AddDays(60)).GetAwaiter().GetResult();
            Program.Check(round1.Outcome == ContextTrainingOutcome.Trained, $"训练仍完成（{round1.Detail}）");
            Program.Check(MetricsOf(box.Store).GetProperty("logLossImprovement").GetDouble() < 0,
                "未来段上 Context 明显更差（改善量为负）");
            Program.Check(!MetricsOf(box.Store).GetProperty("gatePassed").GetBoolean(), "资格门不通过");
            Program.Check(calibrator.Mode == ContextMode.Shadow, "不进 Active（停在 Shadow）");

            SeedDataset(box.Store, "bad2", SyntheticSeries(160, stepHours: 6, featureIndex: SignalFeatureIndex,
                invertFromIndex: 0, startIndex: rows.Count));
            calibrator.TrainAsync(Anchor.AddDays(120).AddHours(3)).GetAwaiter().GetResult();
            Program.Check(calibrator.Mode != ContextMode.Active, "第二轮数据同样更差 → 依旧不是 Active");

            var vector = ContextFeatureProviderFor(box).Build(RequestFor("archive:bad", Anchor.AddDays(120), box.Store));
            Program.Check(!calibrator.Adjust(vector, SyntheticRetrievability).Applied,
                "未进入 Active → 实际排期仍纯 FSRS");
        }
    }

    // ==================================================================================
    // 7. 回退：rolling 变差 / 模型损坏 / 总开关
    // ==================================================================================

    private static void RollingDeteriorationDemotesActiveToShadow()
    {
        using var box = new Sandbox();
        var rows = SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue);
        SeedDataset(box.Store, "roll", rows);
        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());

        calibrator.TrainAsync(Anchor.AddDays(60)).GetAwaiter().GetResult();
        SeedDataset(box.Store, "roll2", SyntheticSeries(160, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue, startIndex: rows.Count));
        calibrator.TrainAsync(Anchor.AddDays(100)).GetAwaiter().GetResult();
        Program.Check(calibrator.Mode == ContextMode.Active, "先进入 Active");

        // 之后的新数据上 Context 变差（信号与结果反向）。
        SeedDataset(box.Store, "roll3", SyntheticSeries(160, stepHours: 6, featureIndex: SignalFeatureIndex,
            invertFromIndex: 0, startIndex: rows.Count + 160));
        var demoted = calibrator.TrainAsync(Anchor.AddDays(140)).GetAwaiter().GetResult();

        Program.Check(demoted.Outcome == ContextTrainingOutcome.Trained, "第三轮训练本身完成");
        Program.Check(calibrator.Mode == ContextMode.Shadow, "rolling 验证变差 → Active 退回 Shadow");
        Program.Check(box.Store.GetPersonalizationModel()!.Status == ContextMode.Shadow,
            "退回 Shadow 已落库（重启后不会又变回 Active）");
        Program.Check(MetricsOf(box.Store).GetProperty("logLossImprovement").GetDouble() < 0,
            "退化轮次的改善量为负（确实触发了不恶化 guard）");

        var vector = ContextFeatureProviderFor(box).Build(RequestFor("archive:roll", Anchor.AddDays(140), box.Store));
        Program.Check(!calibrator.Adjust(vector, SyntheticRetrievability).Applied, "退回 Shadow 后不再校准");
    }

    private static void BrokenModelFallsBackAndKeepsLastGood()
    {
        // —— (a) 已落库的模型损坏（NaN 系数 / schema 换版）→ 不猜、不硬用，退回 ColdStart ——
        using (var box = new Sandbox())
        {
            SeedDataset(box.Store, "corrupt", SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue));
            var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
            calibrator.TrainAsync(Anchor.AddDays(120)).GetAwaiter().GetResult();
            var good = box.Store.GetPersonalizationModel()!;
            Program.Check(good.Status != ContextMode.Disabled, "训练写出了一版模型");

            box.Store.SavePersonalizationModel(new PersonalizationModelState
            {
                ModelVersion = "ctx-lr-1:corrupt",
                TrainedAtUtc = Anchor.AddDays(121),
                TrainCutoffUtc = good.TrainCutoffUtc,
                Status = ContextMode.Active,
                CoefficientsJson = "{\"version\":\"x\",\"intercept\":0,\"weights\":[NaN,NaN]}",
                ScalerJson = good.ScalerJson,
                MetricsJson = good.MetricsJson,
                LastGoodModelVersion = good.ModelVersion,
            });
            box.Reopen();
            var restored = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
            Program.Check(restored.Mode == ContextMode.ColdStart,
                "系数损坏（维数不符 / 非有限）→ 不恢复为 Active，退回 ColdStart 重新过资格");
            Program.Check(restored.CurrentModel is null, "损坏的模型没有被载入");

            box.Store.SavePersonalizationModel(new PersonalizationModelState
            {
                ModelVersion = "ctx-lr-1:schemadrift",
                TrainedAtUtc = Anchor.AddDays(122),
                TrainCutoffUtc = good.TrainCutoffUtc,
                Status = ContextMode.Active,
                CoefficientsJson = good.CoefficientsJson,
                ScalerJson = good.ScalerJson,
                MetricsJson = good.MetricsJson.Replace(SchedulingConfig.ContextFeatureSchemaVersion, "ctx-feat-0"),
                LastGoodModelVersion = good.ModelVersion,
            });
            box.Reopen();
            var drifted = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
            Program.Check(drifted.Mode == ContextMode.ColdStart, "特征 schema 换版 → 重新验证资格（不沿用旧的 Active）");
        }

        // —— (b) 训练产出极端系数 → 拒绝该模型（保留 last-good 的路径见下一条） ——
        using (var box = new Sandbox())
        {
            // 特征与结果**完全可分**（所有样本都跟随信号）→ 极大似然会把权重推到 |δ| ≈ 7；
            // L2 与 |δ| 上限共同把它挡在门外。标准化会消掉信号幅度，所以这里没必要调大 signal。
            SeedDataset(box.Store, "extreme", SyntheticSeries(260, stepHours: 6,
                featureIndex: SignalFeatureIndex, invertFromIndex: int.MaxValue, noiseFree: true));
            var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
            var result = calibrator.TrainAsync(Anchor.AddDays(120)).GetAwaiter().GetResult();

            Program.Check(result.Outcome == ContextTrainingOutcome.SafeFallback,
                $"可分数据上的极端系数 → SafeFallback（实际 {result.Outcome}）");
            Program.Check(result.Detail.Contains("极端系数", StringComparison.Ordinal),
                $"拒绝原因指明是极端系数：{result.Detail}");
            Program.Check(calibrator.Mode != ContextMode.Active, "极端系数模型绝不会进而 Active");
            Program.Check(calibrator.CurrentModel is null, "被拒绝的模型没有留在内存里");
            Program.Check(box.Store.GetPersonalizationModel() is null,
                "此前没有可用模型 → 不写任何模型行（宁可不校准，也不落一个极端系数的模型）");
            Program.Check(!calibrator.Adjust(new ContextFeatureVector(new double[ContextFeatureProvider.FeatureCount],
                ContextFeatureProvider.FeatureNames, new bool[ContextFeatureProvider.FeatureCount]),
                SyntheticRetrievability).Applied,
                "拒绝之后一律不校准（回退纯 FSRS）");
        }

        // —— (c) 训练失败/取消：保留 last-good 并退回 Shadow ——
        using (var box = new Sandbox())
        {
            // 先取消一次（此前没有模型）→ 不留半成品。
            SeedDataset(box.Store, "cancel", SyntheticSeries(240, stepHours: 6,
                featureIndex: SignalFeatureIndex, invertFromIndex: int.MaxValue));
            var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                var cancelled = calibrator.TrainAsync(Anchor.AddDays(120), preCancelled.Token).GetAwaiter().GetResult();
                Program.Check(cancelled.Outcome == ContextTrainingOutcome.SafeFallback,
                    $"预取消的 token → SafeFallback（实际 {cancelled.Outcome}）");
                Program.Check(calibrator.Mode != ContextMode.Active, "取消后不是 Active");
                Program.Check(box.Store.GetPersonalizationModel() is null,
                    "没有任何可用模型时不写模型行（不留半成品）");
            }

            // 正常训练到 Active，记下 last-good。
            calibrator.TrainAsync(Anchor.AddDays(121)).GetAwaiter().GetResult();
            SeedDataset(box.Store, "cancel2", SyntheticSeries(160, stepHours: 6,
                featureIndex: SignalFeatureIndex, invertFromIndex: int.MaxValue, startIndex: 488));
            calibrator.TrainAsync(Anchor.AddDays(162)).GetAwaiter().GetResult();
            Program.Check(calibrator.Mode == ContextMode.Active, "先进入 Active 并留下 last-good 模型");
            var lastGoodVersion = calibrator.CurrentModel!.Version;
            var lastGoodCoefficients = calibrator.CurrentModel!.ToJson();

            // 再取消一次：必须是「保留模型 + 退回 Shadow」，而不是把模型弄丢。
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                var cancelled = calibrator.TrainAsync(Anchor.AddDays(163), preCancelled.Token)
                    .GetAwaiter().GetResult();
                Program.Check(cancelled.Outcome == ContextTrainingOutcome.SafeFallback, "第二次取消同样走安全回退");
            }
            Program.Check(calibrator.Mode == ContextMode.Shadow, "训练失败 → 退回 Shadow");
            Program.Check(calibrator.CurrentModel is not null && calibrator.CurrentModel.Version == lastGoodVersion,
                $"last-good 模型仍在内存里（{calibrator.CurrentModel?.Version}）");
            Program.Check(calibrator.CurrentModel!.ToJson() == lastGoodCoefficients, "last-good 的系数一字未改");
            var state = box.Store.GetPersonalizationModel()!;
            Program.Check(state.Status == ContextMode.Shadow, "退回 Shadow 已落库（重启后不会又变回 Active）");
            Program.Check(state.LastGoodModelVersion == lastGoodVersion,
                $"LastGoodModelVersion 指向 last-good（{state.LastGoodModelVersion}）");
            Program.Check(state.CoefficientsJson.Contains("intercept", StringComparison.Ordinal),
                "落库的状态行带着 last-good 的系数，而不是空数组");
            Program.Check(!calibrator.Adjust(new ContextFeatureVector(new double[ContextFeatureProvider.FeatureCount],
                ContextFeatureProvider.FeatureNames, new bool[ContextFeatureProvider.FeatureCount]),
                SyntheticRetrievability).Applied,
                "退回 Shadow 后不再校准");
        }
    }

    private static void DisabledSwitchShortCircuitsEverything()
    {
        using var box = new Sandbox();
        SeedDataset(box.Store, "off", SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue));
        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler(),
            new ContextCalibratorOptions { Enabled = false });

        Program.Check(SchedulingConfig.ContextEnabled, "SchedulingConfig.ContextEnabled 的生产默认值是 true");
        Program.Check(calibrator.Mode == ContextMode.Disabled, "Enabled = false → Mode == Disabled");

        var vector = ContextFeatureProviderFor(box).Build(RequestFor("archive:off", Anchor.AddDays(120), box.Store));
        var adjustment = calibrator.Adjust(vector, SyntheticRetrievability);
        Program.Check(!adjustment.Applied && adjustment.Delta == 0 && adjustment.Mode == ContextMode.Disabled,
            "Disabled → 一律 None(Disabled)，Delta == 0");
        Program.Check(calibrator.SolveCandidate(vector, 10, SchedulingConfig.DesiredRetention) is null, "Disabled 不求根");

        var result = calibrator.TrainAsync(Anchor.AddDays(120)).GetAwaiter().GetResult();
        Program.Check(result.Outcome == ContextTrainingOutcome.Disabled, "Disabled 时训练入口直接短路");
        Program.Check(box.Store.GetPersonalizationModel() is null, "Disabled 时不训练、不落库");
        Program.CheckClose(MemoryScheduler.ResolveFinalInterval(10, 50, adjustment), 10, 1e-12,
            "Disabled → final = 纯 FSRS");
    }

    // ==================================================================================
    // 8. 锁存 / 求根
    // ==================================================================================

    private static void SolveCandidateHitsDesiredRetention()
    {
        using var box = new Sandbox();
        var rows = SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue);
        SeedDataset(box.Store, "solve", rows);
        var scheduler = new Fsrs6Scheduler();
        var calibrator = new ContextCalibrator(box.Store, scheduler);
        calibrator.TrainAsync(Anchor.AddDays(60)).GetAwaiter().GetResult();
        SeedDataset(box.Store, "solve2", SyntheticSeries(160, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue, startIndex: rows.Count));
        calibrator.TrainAsync(Anchor.AddDays(120).AddHours(3)).GetAwaiter().GetResult();
        Program.Check(calibrator.Mode == ContextMode.Active, "先进入 Active（否则不求根）");

        // 固定一组「合法历史特征」；求解期间只有 t 变。
        var features = new double[ContextFeatureProvider.FeatureCount];
        features[SignalFeatureIndex] = 1.0;
        var vector = new ContextFeatureVector(features, ContextFeatureProvider.FeatureNames,
            new bool[ContextFeatureProvider.FeatureCount]);

        var delta = calibrator.Adjust(vector, SyntheticRetrievability).Delta;
        Program.Check(delta != 0, $"δ 非零（实际 {delta:R}）");

        const double stabilityDays = 12.0;
        var solved = calibrator.SolveCandidate(vector, stabilityDays, SchedulingConfig.DesiredRetention);
        Program.Check(solved is not null, "Active + 固定历史特征 → 求根成功");
        var t = solved!.Value;
        Program.Check(MemoryScheduler.IsFinite(t) && t > 0 && t <= SchedulingConfig.MaximumIntervalDays,
            $"解落在 (0, {SchedulingConfig.MaximumIntervalDays}] 天内（实际 {t:R}）");

        // 用**同一个 δ** 与同一个稳定性重算 R_adjusted(t)：必须落回目标保持率。
        var recomputed = ContextCalibrator.AdjustedRetrievability(scheduler.Retrievability(stabilityDays, t), delta);
        Program.CheckClose(recomputed, SchedulingConfig.DesiredRetention, 1e-3,
            "锁存：R_adjusted(t) 重算结果 ≈ 0.90");

        // 单调性：t 增大 ⇒ R_adjusted 严格下降（二分括号区间成立的依据）。
        var r0 = ContextCalibrator.AdjustedRetrievability(scheduler.Retrievability(stabilityDays, 0), delta);
        var rMid = ContextCalibrator.AdjustedRetrievability(scheduler.Retrievability(stabilityDays, t), delta);
        var rBig = ContextCalibrator.AdjustedRetrievability(
            scheduler.Retrievability(stabilityDays, SchedulingConfig.MaximumIntervalDays), delta);
        Program.Check(r0 >= SchedulingConfig.DesiredRetention && rBig <= SchedulingConfig.DesiredRetention
            && r0 > rMid && rMid > rBig,
            $"adjustedRetrievability(t) 严格单调递减且把目标保持率括在中间（{r0:R} > {rMid:R} > {rBig:R}）");

        // 未进入 Active 的实现（无模型）必须返回 null。
        using var coldBox = new Sandbox();
        var cold = new ContextCalibrator(coldBox.Store, scheduler);
        Program.Check(cold.SolveCandidate(vector, stabilityDays, SchedulingConfig.DesiredRetention) is null,
            "非 Active 状态下 SolveCandidate 返回 null（→ 协调器回退纯 FSRS）");
    }

    private static void SolveCandidateReturnsNullWhenNotMonotone()
    {
        using var box = new Sandbox();
        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());

        Program.Check(calibrator.SolveAdjustedInterval(_ => 0.5, 0.9) is null,
            "常数函数不跨越目标 → null（不抛）");
        Program.Check(calibrator.SolveAdjustedInterval(t => 0.5 + 0.4 * Math.Min(1.0, t / 100.0), 0.9) is null,
            "非单调递减（随 t 上升）→ null");
        Program.Check(calibrator.SolveAdjustedInterval(_ => double.NaN, 0.9) is null, "非有限值 → null");
        Program.Check(calibrator.SolveAdjustedInterval(t => 1.0 / (1.0 + t / 20.0), 0.9) is not null,
            "单调递减且跨越目标 → 有解（对照：不是恒返回 null）");
        Program.Check(calibrator.SolveAdjustedInterval(t => 1.0 / (1.0 + t / 20.0), 1.0) is null,
            "目标保持率 1 非法 → null");
        Program.Check(calibrator.SolveAdjustedInterval(t => 1.0 / (1.0 + t / 20.0), 0.5)
                is { } t && Math.Abs(t - 20.0) <= 1e-2,
            "对照：r(t)=1/(1+t/20) 上求 0.5 得 t ≈ 20");

        // 协调器侧：Active 但 stability 非法 → 回退而不是产出垃圾。
        Program.Check(calibrator.SolveCandidate(
                new ContextFeatureVector(new double[ContextFeatureProvider.FeatureCount],
                    ContextFeatureProvider.FeatureNames, new bool[ContextFeatureProvider.FeatureCount]),
                double.NaN, SchedulingConfig.DesiredRetention) is null,
            "稳定性非有限 → null");
        Program.Check(calibrator.SolveCandidate(
                new ContextFeatureVector(new double[ContextFeatureProvider.FeatureCount],
                    ContextFeatureProvider.FeatureNames, new bool[ContextFeatureProvider.FeatureCount]),
                0, SchedulingConfig.DesiredRetention) is null,
            "稳定性为零 → null");
    }

    // ==================================================================================
    // 9. scaler 只在训练期拟合
    // ==================================================================================

    private static void ScalerIsFrozenAtTrainingTime()
    {
        using var box = new Sandbox();
        var rows = SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue);
        SeedDataset(box.Store, "scaler", rows);

        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        var result = calibrator.TrainAsync(Anchor.AddDays(120)).GetAwaiter().GetResult();
        Program.Check(result.Outcome == ContextTrainingOutcome.Trained, $"训练完成（{result.Detail}）");

        var metrics = MetricsOf(box.Store);
        var trainSamples = metrics.GetProperty("trainSamples").GetInt32();
        var validationSamples = metrics.GetProperty("validationSamples").GetInt32();
        var scaler = calibrator.FrozenScaler;
        Program.Check(scaler is not null, "训练后公开了冻结的标准化器");

        // —— 训练段与验证段各自独立算一遍均值，断言冻结的是训练段那一份 ——
        var trainValues = rows.Take(trainSamples).Select(r => r.Features[SignalFeatureIndex]).ToArray();
        var validationValues = rows.Skip(trainSamples).Take(validationSamples).Select(r => r.Features[SignalFeatureIndex]).ToArray();
        var trainMean = trainValues.Average();
        var trainStd = Math.Sqrt(trainValues.Select(v => (v - trainMean) * (v - trainMean)).Average());
        var validationMean = validationValues.Average();

        Program.Check(Math.Abs(trainMean - validationMean) > 1e-6,
            $"前提：训练段与验证段的均值确实不同（{trainMean:R} vs {validationMean:R}），否则断言会空转");
        Program.CheckClose(scaler!.Means[SignalFeatureIndex], trainMean, 1e-9,
            "冻结的均值来自**训练段**");
        Program.CheckClose(scaler.StandardDeviations[SignalFeatureIndex], trainStd, 1e-9,
            "冻结的标准差来自训练段");

        // —— 把验证集的一条样本的特征放大 10 倍：标准化必须仍用训练期参数 ——
        var probe = rows[trainSamples].Features.ToArray();
        var probeMissing = new bool[ContextFeatureProvider.FeatureCount];
        probe[SignalFeatureIndex] *= 10.0;
        var probeVector = new ContextFeatureVector(probe, ContextFeatureProvider.FeatureNames, probeMissing);

        var model = calibrator.CurrentModel!;
        var validationStd = Math.Sqrt(validationValues.Select(v => (v - validationMean) * (v - validationMean)).Average());

        // (1) 直接看标准化结果：放大 10 倍之后，该维仍按**训练期**均值/方差缩放。
        var standardised = scaler.Standardize(probe, probeMissing);
        var expectedZ = (probe[SignalFeatureIndex] - trainMean) / trainStd;
        var wrongZ = (probe[SignalFeatureIndex] - validationMean) / validationStd;
        Program.CheckClose(standardised[SignalFeatureIndex], expectedZ, 1e-12,
            "放大 10 倍后该维的标准化值 = (10x − 训练段均值) / 训练段标准差");
        Program.Check(Math.Abs(expectedZ - wrongZ) > 1e-2,
            $"前提：用验证段均值/方差会得到**不同**的值（{expectedZ:R} vs {wrongZ:R}），断言不是空转");
        for (var j = 0; j < standardised.Length; j++)
        {
            if (j == SignalFeatureIndex) continue;
            Program.CheckClose(standardised[j], (probe[j] - scaler.Means[j]) / scaler.StandardDeviations[j], 1e-12,
                $"第 {j} 维也按冻结的均值/方差缩放");
            break; // 其余维与第 0 维同源，不必逐条重复
        }

        // (2) 端到端：δ 也必须走同一条冻结路径。这里用「轻微偏移」的探针，
        //     因为放大 10 倍会让 |δ| 超过 ContextMaxAbsDelta（那是另一条保护，见 BrokenModelFallsBackAndKeepsLastGood）。
        var gentle = rows[trainSamples].Features.ToArray();
        gentle[SignalFeatureIndex] += 0.3 * trainStd;
        var gentleVector = new ContextFeatureVector(gentle, ContextFeatureProvider.FeatureNames, probeMissing);

        var withTrainStats = model.Intercept;
        var withValidationStats = model.Intercept;
        for (var j = 0; j < model.Weights.Length; j++)
        {
            var mean = j == SignalFeatureIndex ? validationMean : scaler.Means[j];
            var std = j == SignalFeatureIndex ? validationStd : scaler.StandardDeviations[j];
            withTrainStats += model.Weights[j] * ((gentle[j] - scaler.Means[j]) / scaler.StandardDeviations[j]);
            withValidationStats += model.Weights[j] * ((gentle[j] - mean) / std);
        }

        var actual = calibrator.DeltaFor(gentleVector);
        Program.Check(actual is not null, "探针向量能得到 δ");
        Program.CheckClose(actual!.Value, withTrainStats, 1e-12,
            "δ 用的是**训练期**均值/方差（与手工按训练期参数重算的结果一致）");
        Program.Check(Math.Abs(actual.Value - withValidationStats) > 1e-9,
            $"δ 明显不同于「按验证段重算均值/方差」的结果（说明没有在验证段上重新拟合）：{actual.Value:R} vs {withValidationStats:R}");

        // 缺失值用训练期填充值，而不是 0、也不是验证段的均值。
        var missingProbe = rows[trainSamples].Features.ToArray();
        var missingFlags = new bool[ContextFeatureProvider.FeatureCount];
        missingFlags[SignalFeatureIndex] = true;
        var missingVector = new ContextFeatureVector(missingProbe, ContextFeatureProvider.FeatureNames, missingFlags);
        var expectedMissing = model.Intercept;
        for (var j = 0; j < model.Weights.Length; j++)
        {
            var raw = j == SignalFeatureIndex ? scaler.Fills[j] : missingProbe[j];
            expectedMissing += model.Weights[j] * ((raw - scaler.Means[j]) / scaler.StandardDeviations[j]);
        }
        Program.CheckClose(calibrator.DeltaFor(missingVector)!.Value, expectedMissing, 1e-12,
            "缺失特征用训练期的填充值（＝训练期观测均值）而不是 0");
        Program.CheckClose(scaler.Fills[SignalFeatureIndex], trainMean, 1e-9, "填充值就是训练段的观测均值");
    }

    // ==================================================================================
    // 10. 审计字段
    // ==================================================================================

    private static void MetricsJsonIsAuditable()
    {
        using var box = new Sandbox();
        var rows = SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue);
        SeedDataset(box.Store, "audit", rows);

        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler(),
            new ContextCalibratorOptions { Seed = 424242 });
        calibrator.TrainAsync(Anchor.AddDays(120)).GetAwaiter().GetResult();

        var state = box.Store.GetPersonalizationModel();
        Program.Check(state is not null, "训练后 personalization_models 里有 Context 行");
        Program.Check(state!.ModelVersion.StartsWith(SchedulingConfig.ContextModelVersionPrefix, StringComparison.Ordinal),
            $"模型版本带族前缀（{state.ModelVersion}）");
        Program.Check(state.TrainCutoffUtc.Kind == DateTimeKind.Utc, "训练截止时刻是 UTC");
        Program.Check(state.Status == ContextMode.Shadow, "首次训练后的状态是 Shadow");

        var m = MetricsOf(box.Store);
        Program.Check(m.GetProperty("seed").GetInt32() == 424242, "metrics 记录 seed");
        Program.Check(m.GetProperty("featureSchemaVersion").GetString() == SchedulingConfig.ContextFeatureSchemaVersion,
            "metrics 记录特征 schema 版本");
        foreach (var name in new[]
                 {
                     "cutoffUtc", "trainSamples", "validationSamples", "positives", "negatives", "spanDays",
                     "trainLogLoss", "trainBrier", "baselineLogLoss", "contextLogLoss", "baselineBrier", "contextBrier",
                     "logLossImprovement", "brierImprovement", "gatePassed", "maxAbsDelta", "predictions",
                 })
        {
            Program.Check(m.TryGetProperty(name, out _), $"metrics 含审计字段 {name}");
        }

        var cutoff = DateTime.Parse(m.GetProperty("cutoffUtc").GetString()!, null,
            System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        Program.Check(cutoff >= rows.Min(r => r.At) && cutoff <= rows.Max(r => r.At),
            "训练截止时刻落在样本时间跨度内");
        Program.CheckClose(m.GetProperty("spanDays").GetDouble(),
            (rows[^1].At - rows[0].At).TotalDays, 1e-6, "spanDays 等于样本实际跨度");
        Program.Check(m.GetProperty("positives").GetInt32() >= SchedulingConfig.ContextMinimumPositive
            && m.GetProperty("negatives").GetInt32() >= SchedulingConfig.ContextMinimumNegative,
            "正负例计数满足门槛");
        Program.Check(m.GetProperty("trainSamples").GetInt32() + m.GetProperty("validationSamples").GetInt32()
            == m.GetProperty("evaluableSamples").GetInt32(), "training/validation 计数自洽");
        Program.Check(m.GetProperty("predictions").GetArrayLength()
            == Math.Min(m.GetProperty("validationSamples").GetInt32(), SchedulingConfig.ContextMaxLoggedPredictions),
            "冻结预测条数 = min(验证段样本数, 上限)");
        Program.Check(m.GetProperty("predictions")[0].GetArrayLength() == 3,
            "冻结预测逐条含 [baseline, context, outcome] 三项");
        Program.CheckClose(m.GetProperty("logLossImprovement").GetDouble(),
            m.GetProperty("baselineLogLoss").GetDouble() - m.GetProperty("contextLogLoss").GetDouble(), 1e-12,
            "改善量 = baseline − context（可复算）");
        Program.Check(m.GetProperty("maxAbsDelta").GetDouble() <= SchedulingConfig.ContextMaxAbsDelta,
            "记录下来的 |δ| 峰值不超过上限");
    }

    // ==================================================================================
    // 11. 训练触发的调度资格（WS-I：把 P6 的训练入口接到一轮开始 / 一轮结束）
    // ==================================================================================

    /// <summary>
    /// 触发调度器 <c>MainWindow.ShouldTrainContext</c> 的资格判定，以及它与校准器自身冷却闸的一致性。
    ///
    /// <para><b>为什么用反射</b>：判定函数是 <c>internal static</c>，而 <c>Lexi.csproj</c> 里
    /// <b>没有</b> <c>InternalsVisibleTo MemoryTests</c>（该文件不在本工作流的所有权内，不得修改）。
    /// 反射调用前**先断言方法确实存在**：改名或改签名会让这一条直接失败，而不是悄悄退化成空转通过。</para>
    ///
    /// <para><b>这里覆盖什么</b>：冷却、在途任务、样本门槛、失败后的冷却、时钟回拨这五类判定的**取值**，
    /// 以及触发闸与校准器内部闸读的是同一个常量（两道闸不会各自为政）。</para>
    ///
    /// <para><b>这里不覆盖什么（如实声明）</b>：UI 层的完整触发流程（<c>MainWindow.MemoryTryTrainContext</c>
    /// 读样本 → 起后台任务 → 回 UI 线程落库 → 复位标志）需要真实 <c>MainWindow</c> 实例，
    /// 而 <c>MemoryTests</c> 不启动 Avalonia，构造不了窗口；因此「训练异常 / 取消 / 超时不影响学习流程的
    /// 任何可见状态」这一条**在单元层未覆盖**，其等价保证由 <c>BrokenModelFallsBackAndKeepsLastGood</c>
    /// （校准器层的取消 / 安全回退）与 <c>--learning-test</c> 的真实运行共同承担。
    /// 「不在每次评价时触发」由代码结构保证（触发点只在开轮 / 轮末 / 离开学习页三处），同样未在单元层断言。</para>
    /// </summary>
    private static void TrainingTriggerGateIsCorrect()
    {
        var method = typeof(MainWindow).GetMethod("ShouldTrainContext",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Program.Check(method is not null,
            "触发调度器的资格判定存在（MainWindow.ShouldTrainContext，internal static）");
        if (method is null) return;

        Program.Check(method.ReturnType == typeof(bool), "资格判定返回 bool");
        Program.Check(method.GetParameters().Length == 5,
            $"资格判定接受 5 个入参（now / 上次正常结束 / 上次失败 / 是否有在途任务 / 样本数），实际 {method.GetParameters().Length}");
        Program.Check(method.GetParameters()[4].ParameterType == typeof(int),
            "第 5 个入参是样本数（int）——样本门槛必须参与判定，不能只看冷却");

        bool Should(DateTime at, DateTime? completedAt, DateTime? failedAt, bool pending, int samples) =>
            (bool)method.Invoke(null, [at, completedAt, failedAt, pending, samples])!;

        var now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var cooldown = TimeSpan.FromMinutes(SchedulingConfig.ContextTrainingCooldownMinutes);
        var enough = SchedulingConfig.ContextMinimumSamples;

        // —— 四类「不训练」——
        Program.Check(!Should(now, now - TimeSpan.FromMinutes(5), null, false, enough),
            "冷却未到（距上次训练 5 分钟 < 30）→ false");
        Program.Check(!Should(now, null, null, true, enough),
            "已有训练在途 → false（绝不并发第二次）");
        Program.Check(!Should(now, null, null, false, SchedulingConfig.ContextMinimumSamples - 1),
            $"样本不足（{SchedulingConfig.ContextMinimumSamples - 1} < {SchedulingConfig.ContextMinimumSamples}）→ false（保持 ColdStart，只收集）");
        Program.Check(!Should(now, now - TimeSpan.FromDays(3), now.AddMinutes(-5), false, enough),
            "上次失败且冷却未过（即使距上次正常结束已 3 天）→ false");

        // —— 「训练」：冷却走完 + 无在途 + 样本足够 ——
        Program.Check(Should(now, null, null, false, enough),
            "从未训练过且样本足够 → true");
        Program.Check(Should(now, now - cooldown, null, false, enough),
            "冷却恰好走完（= 30 分钟）→ true（边界含等号）");
        Program.Check(Should(now, now - TimeSpan.FromDays(3), now - cooldown, false, enough),
            "上次失败但失败后已过冷却 → true（失败不延长冷却）");
        Program.Check(!Should(now, now - cooldown + TimeSpan.FromSeconds(1), null, false, enough),
            "冷却差 1 秒 → false");

        // —— 三重条件必须同时满足：只满足两个仍然不训练 ——
        Program.Check(!Should(now, now.AddMinutes(-5), now.AddMinutes(-5), true, SchedulingConfig.ContextMinimumSamples - 1),
            "冷却已到但既有在途任务又样本不足 → false（三个条件是 AND）");

        // —— 时钟回拨：宁可少训一次，也不因为系统时间异常而反复全表读 ——
        Program.Check(!Should(now, null, now.AddMinutes(5), false, enough),
            "上次失败时刻落在未来（时钟回拨）→ false（保守跳过，不做补偿）");

        // —— 与校准器自身的冷却闸一致：两道闸读同一个常量，不会各自为政 ——
        using var box = new Sandbox();
        SeedDataset(box.Store, "trigger", SyntheticSeries(240, stepHours: 6, featureIndex: SignalFeatureIndex, int.MaxValue));
        var calibrator = new ContextCalibrator(box.Store, new Fsrs6Scheduler());
        var first = calibrator.TrainAsync(Anchor.AddDays(120)).GetAwaiter().GetResult();
        Program.Check(first.Outcome == ContextTrainingOutcome.Trained,
            $"第一轮训练真的完成了（实际 {first.Outcome}）——否则下面的一致性断言会空转");
        var lastTrainedAt = Anchor.AddDays(120);
        Program.Check(Should(lastTrainedAt + cooldown.Add(TimeSpan.FromSeconds(1)), lastTrainedAt, null, false, enough),
            "触发闸认为冷却已过时，校准器那边也恰好允许再训（同一个 ContextTrainingCooldownMinutes）");
        var tooSoon = calibrator.TrainAsync(lastTrainedAt + cooldown - TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
        Program.Check(tooSoon.Outcome == ContextTrainingOutcome.Cooldown,
            $"校准器自身的冷却闸同样拒绝提前训练（实际 {tooSoon.Outcome}）");
    }

    // ==================================================================================
    // 合成数据（**只用于验证资格门机制**，不代表任何真实用户收益）
    // ==================================================================================

    /// <summary>一条合成样本。</summary>
    private sealed record Row(DateTime At, double[] Features, StudyRating Label);

    /// <summary>
    /// 合成样本序列：时间等距；承载信号的特征取 ±<paramref name="signal"/>；
    /// **75%** 的样本「结果跟随信号」，其余 25% 反向。三个位都由 <see cref="Bit"/> 从 index 上确定性导出，
    /// 且互相独立——这一点很关键：
    /// <list type="bullet">
    /// <item>跟随比例必须真的是 75%。若写成 50%，信号对结果就毫无信息量（模型只学到噪声，
    /// 在「更好」的场景里也通不过资格门）。</item>
    /// <item>跟随位与信号位必须独立。若两者相关（例如用 <c>index % 4</c> 同时决定它们），
    /// 数据会退化成近似可分，极大似然把 |δ| 推到远超上限，那时测到的就是极端系数保护而不是资格门。</item>
    /// </list>
    /// <paramref name="noiseFree"/> = true 时所有样本都跟随信号（完全可分，专门用来触发极端系数保护）。
    /// <paramref name="invertFromIndex"/> 之后的样本把跟随关系**整体反转**——
    /// 用来构造「训练段成立、未来段不成立」的未来分布变化。
    /// </summary>
    private static List<Row> SyntheticSeries(
        int count, double stepHours, int featureIndex, int invertFromIndex,
        int startIndex = 0, double signal = 1.0, bool noiseFree = false)
    {
        var rows = new List<Row>(count);
        for (var i = 0; i < count; i++)
        {
            var index = startIndex + i;
            var at = Anchor.AddHours(stepHours * index);
            var features = new double[ContextFeatureProvider.FeatureCount];
            var value = Bit(index, 0) ? signal : -signal;
            features[featureIndex] = value;
            // P(跟随) = P(bit1 ∨ bit2) = 0.75
            var follows = noiseFree || Bit(index, 1) || Bit(index, 2);
            var positive = (value > 0) == follows;
            if (index >= invertFromIndex) positive = !positive;
            rows.Add(new Row(at, features, positive ? StudyRating.Known : StudyRating.Forgot));
        }
        return rows;
    }

    /// <summary>确定性伪随机位（无系统随机源、无种子状态；同一个 index 永远给同一位）。</summary>
    private static bool Bit(int index, int shift)
    {
        unchecked
        {
            var h = (uint)index * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return ((h >> shift) & 1) == 1;
        }
    }

    private static void SeedDataset(ILearningMemoryStore store, string prefix, IReadOnlyList<Row> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var id = $"{prefix}-{i:D5}";
            var missing = new bool[ContextFeatureProvider.FeatureCount];
            store.SaveContextSnapshot(new ContextFeatureSnapshot
            {
                SnapshotId = id,
                WordKey = "archive:seed",
                SessionId = "s-" + id,
                PresentationId = "p-" + id,
                CapturedAtUtc = rows[i].At,
                FsrsDifficultyAtCapture = 5.0,
                FsrsStabilityAtCapture = 10.0,
                FsrsRetrievabilityAtCapture = SyntheticRetrievability,
                FeatureSchemaVersion = SchedulingConfig.ContextFeatureSchemaVersion,
                ModelVersion = SchedulingConfig.ContextModelVersionPrefix,
                FeaturesJson = JsonSerializer.Serialize(rows[i].Features),
                MissingFlagsJson = JsonSerializer.Serialize(missing),
            });
        }
        // 合成夹具必须满足生产采样资格：有效FirstRetrieval canonical锚点与逐条已知标签。
        // 只写本测试的隔离SQLite，避免把无canonical的快照当作真实长期训练样本。
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var tx = connection.BeginTransaction();
        for (var i = 0; i < rows.Count; i++)
        {
            var id = $"{prefix}-{i:D5}";
            var labelAt = rows[i].At.AddSeconds(1).ToString("O");
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO canonical_reviews(canonical_id,word_key,session_id,source_presentation_id,
                    origin,rating,reviewed_at_utc,completed_at_utc)
                VALUES($cid,'archive:seed',$sid,$pid,'FirstRetrieval',$rating,$reviewed,$completed);
                UPDATE context_snapshots SET label=$rating,confident_recall=$confident,labeled_at_utc=$completed
                WHERE snapshot_id=$snapshot;
                """;
            command.Parameters.AddWithValue("$cid", CanonicalReview.BuildId("archive:seed", "s-" + id));
            command.Parameters.AddWithValue("$sid", "s-" + id);
            command.Parameters.AddWithValue("$pid", "p-" + id);
            command.Parameters.AddWithValue("$rating", rows[i].Label.ToString());
            command.Parameters.AddWithValue("$reviewed", rows[i].At.AddMilliseconds(500).ToString("O"));
            command.Parameters.AddWithValue("$completed", labelAt);
            command.Parameters.AddWithValue("$confident", rows[i].Label == StudyRating.Known ? 1 : 0);
            command.Parameters.AddWithValue("$snapshot", id);
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ==================================================================================
    // 小工具
    // ==================================================================================

    private static readonly string[] PrevRoundNames =
    [
        "prev_round_presentations", "prev_round_known", "prev_round_fuzzy", "prev_round_forgotten",
        "prev_round_resets", "prev_round_revisions", "prev_round_known_to_fuzzy",
        "prev_round_known_to_forgotten", "prev_round_fuzzy_to_forgotten", "prev_round_max_known_streak",
        "prev_round_presentations_to_mastery", "prev_round_time_to_mastery_minutes",
        "prev_round_had_fuzzy", "prev_round_had_forgotten", "prev_round_had_revision",
    ];

    private static int IndexOf(string name) => Array.IndexOf(ContextFeatureProvider.FeatureNames, name);

    private static ContextFeatureProvider ContextFeatureProviderFor(Sandbox box) =>
        new(box.Store, new Fsrs6Scheduler());

    private static ContextFeatureRequest RequestFor(string wordKey, DateTime atUtc, ILearningMemoryStore store) =>
        new(wordKey, "", "p-probe", atUtc, LearningMode.Review, false, 1, 0, store.GetCard(wordKey));

    private static JsonElement MetricsOf(ILearningMemoryStore store)
    {
        var state = store.GetPersonalizationModel();
        Program.Check(state is not null, "personalization_models 里有 Context 模型行");
        using var doc = JsonDocument.Parse(state!.MetricsJson);
        return doc.RootElement.Clone();
    }

    /// <summary>直接写一条 canonical（不经过协调器）：用于精确控制「历史发生在什么时刻」。</summary>
    private static void WriteRound(
        ILearningMemoryStore store, string wordKey, string sessionId,
        DateTime reviewedAtUtc, DateTime completedAtUtc, StudyRating rating,
        CanonicalOrigin origin, int presentations, bool withCard = false)
    {
        var summary = new WordSessionSummary
        {
            WordKey = wordKey,
            SessionId = sessionId,
            FirstPresentedAtUtc = reviewedAtUtc.AddMinutes(-2),
            CompletedAtUtc = completedAtUtc,
            FirstInitialResponse = rating,
            FirstValidatedResponse = rating,
            TotalPresentations = presentations,
            FinalKnownCount = rating == StudyRating.Known ? presentations : 0,
            FinalFuzzyCount = rating == StudyRating.Unsure ? presentations : 0,
            FinalForgottenCount = rating == StudyRating.Forgot ? presentations : 0,
            MaxKnownStreak = rating == StudyRating.Known ? presentations : 0,
            PresentationsToMastery = rating == StudyRating.Known ? presentations : 0,
            TimeToMasteryMs = rating == StudyRating.Known ? 45_000 : null,
            HadFuzzy = rating == StudyRating.Unsure,
            HadForgotten = rating == StudyRating.Forgot,
        };
        var canonical = new CanonicalReview
        {
            CanonicalId = CanonicalReview.BuildId(wordKey, sessionId),
            WordKey = wordKey,
            SessionId = sessionId,
            Origin = origin,
            Rating = rating,
            ReviewedAtUtc = reviewedAtUtc,
            CompletedAtUtc = completedAtUtc,
            AggregationPolicyVersion = AggregationPolicy.Version,
        };
        store.CommitWordSession(new WordSessionCommit(
            Session: null!, Summary: summary, Canonical: canonical,
            CardPreState: null!,
            CardAfter: withCard
                ? new FsrsCardState
                {
                    WordKey = wordKey,
                    Difficulty = 5.0,
                    Stability = 10.0,
                    Reps = 1,
                    Lapses = rating == StudyRating.Forgot ? 1 : 0,
                    State = FsrsState.Review,
                    LastReviewAtUtc = reviewedAtUtc,
                    NextReviewAtUtc = reviewedAtUtc.AddDays(10),
                    LastCanonicalRating = rating,
                    FsrsAlgorithmVersion = SchedulingConfig.AlgorithmVersion,
                    FsrsLibraryVersion = SchedulingConfig.LibraryVersion,
                    FsrsParameterVersion = SchedulingConfig.ParameterVersion,
                }
                : null!,
            Decision: null!, PendingEvents: [], SnapshotsToSave: [], SnapshotLabels: [], OutboxMutations: []));
    }

    private sealed class FakeClock
    {
        public FakeClock(DateTime now) => Now = now;
        public DateTime Now { get; private set; }
        public void Advance(TimeSpan delta) => Now = Now.Add(delta);
    }

    /// <summary>自己的临时目录 + 临时库；结束时整目录删除，绝不碰真实用户数据目录。</summary>
    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; }
        public string DbPath { get; }

        private VocabularyService? _service;

        public Sandbox()
        {
            Dir = Path.Combine(Path.GetTempPath(), "lexi-context-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbPath = Path.Combine(Dir, "vocab.sqlite3");
            Open();
        }

        public VocabularyService Service => _service ?? throw new InvalidOperationException("沙箱服务未打开。");

        public ILearningMemoryStore Store { get; private set; } = null!;

        public void Open()
        {
            if (_service != null) return;
            var archive = ServiceFactory.OpenArchive(DbPath);
            Store = ServiceFactory.OpenMemory(archive);
            _service = (VocabularyService)archive;
        }

        /// <summary>关闭并重开（模拟应用重启后从 store 恢复模型状态）。</summary>
        public void Reopen()
        {
            Close();
            Open();
        }

        public void Close()
        {
            _service?.Dispose();
            _service = null;
        }

        public void Dispose()
        {
            Close();
            try { Directory.Delete(Dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
