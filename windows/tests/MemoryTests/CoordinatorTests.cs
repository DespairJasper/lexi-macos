namespace Lexi.Tests;

/// <summary>
/// coordinator 组：<see cref="LearningMemoryCoordinator"/> 的行为断言。
/// 全部使用内存假 store + 假 scheduler + 注入假时钟，不触碰真实数据库，不依赖 FSRS 具体公式。
/// </summary>
public static class CoordinatorTests
{
    public static void Run()
    {
        CanonicalProducedOnce();
        FirstRetrievalWinsOverLaterSuccess();
        TrajectoryShapesDiffer();
        LearnCardIsNotRetrieval();
        NoValidRetrievalWritesNothing();
        LatencyPauseAndReliability();
        UndoCommitInvalidatesWithoutDeletingEvents();
        SameWordAcrossSessionsSharesCard();
        ContextSnapshotCapturedBeforeRating();
        ContextDisabledPaths();
        CommitExceptionPropagates();
        ContextVectorContractEnforced();
        CommitPayloadShape();
        DueQueuePassthrough();
        EventTaggingRules();
        SameDaySecondSessionDoesNotEraseLapse();
        SameWordSameDayKeepsBothCanonicals();
        DecisionAuditMatchesCalibratorOutcome();
        AppendEventFailureDoesNotAdvanceAppearanceCounters();
    }

    // ==================================================================================
    // 1. 一个 word × session 只产生一个 canonical
    // ==================================================================================

    private static void CanonicalProducedOnce()
    {
        var h = new Harness();
        var word = ReviewWord(h, "canon-once");
        var sessionId = h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-1", 5);

        // 同一 session 内 3 次呈现 + 3 次认识
        for (var i = 0; i < 3; i++)
        {
            var pid = $"p{i}";
            h.Coordinator.OnPresented(word, pid, true);
            h.Clock.Advance(TimeSpan.FromSeconds(3));
            h.Coordinator.OnRated(word, pid, StudyRating.Known, i, i + 1, null);
        }

        var first = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(first is not null, "3 次认识后定稿成功");
        Program.Check(first!.Applied && !first.AlreadyApplied, "首次定稿 Applied=true / AlreadyApplied=false");
        Program.Check(h.Store.CommitWordSessionCalls == 1,
            "3 次作答只产生 1 次定稿事务（不是每次作答都定稿）");
        Program.Check(h.Scheduler.ReviewCalls == 1, "FSRS 只被调用一次");
        Program.Check(h.Store.CardApplyCount == 1, "FSRS 副作用只被施加一次");
        Program.Check(first.CanonicalId == CanonicalReview.BuildId(word.Key, sessionId),
            "CanonicalId = cr:<wordKey>:<sessionId>");
        Program.Check(h.Store.LastCommit!.Summary.TotalPresentations == 3, "summary 覆盖本 session 的 3 次呈现");

        var second = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(second is not null && second.AlreadyApplied && !second.Applied,
            "同一 word × session 第二次定稿 → AlreadyApplied=true");
        Program.Check(second!.CanonicalId == first.CanonicalId, "重复定稿得到同一个 CanonicalId（确定性幂等键）");
        Program.Check(h.Store.CardApplyCount == 1, "重复定稿不重复施加 FSRS 副作用（由 store 的 canonical 幂等保证）");
        Program.Check(h.Store.CanonicalRowCount(word.Key) == 1 && h.Store.ValidCanonicalCount(word.Key) == 1,
            "同一 word×session 重复定稿后仍只有一个有效 canonical（ux_canonical_active 回归）");
    }

    // ==================================================================================
    // 2. 首次评分不可被后续覆盖
    // ==================================================================================

    private static void FirstRetrievalWinsOverLaterSuccess()
    {
        // 2a：首评 Unsure，之后 3 次 Known
        var a = new Harness();
        var wordA = ReviewWord(a, "first-unsure");
        a.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        a.Coordinator.OnPresented(wordA, "p1", true);
        a.Coordinator.OnRated(wordA, "p1", StudyRating.Unsure, 0, 0, null);
        for (var i = 0; i < 3; i++)
        {
            var pid = $"k{i}";
            a.Clock.Advance(TimeSpan.FromSeconds(5));
            a.Coordinator.OnPresented(wordA, pid, true);
            a.Clock.Advance(TimeSpan.FromSeconds(3));
            a.Coordinator.OnRated(wordA, pid, StudyRating.Known, 0, 1, null);
        }
        var resultA = a.Coordinator.CommitWord(wordA, StudyMode.Review);
        Program.Check(resultA is not null, "首评 Unsure 后仍能定稿");
        Program.Check(a.Store.LastCommit!.Canonical.Rating == StudyRating.Unsure,
            "Review 模式 canonical 取第一次真实 retrieval 的终判 Unsure，不被后续 3 次 Known 覆盖");
        Program.Check(a.Store.LastCommit.Canonical.Origin == CanonicalOrigin.FirstRetrieval,
            "Origin = FirstRetrieval");
        Program.Check(a.Store.LastCommit.Canonical.SourcePresentationId == "p1", "canonical 指向第一次 retrieval");
        Program.Check(a.Store.LastCommit.Summary.FinalKnownCount == 3, "summary 仍如实记录后续 3 次认识");

        // 2b：判别性轨迹——首评 Known，之后模糊。首答口径给 Known，聚合口径会给 Unsure。
        var b = new Harness();
        var wordB = ReviewWord(b, "first-known");
        b.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        b.Coordinator.OnPresented(wordB, "p1", true);
        b.Coordinator.OnRated(wordB, "p1", StudyRating.Known, 0, 1, null);
        for (var i = 0; i < 3; i++)
        {
            var pid = $"u{i}";
            b.Clock.Advance(TimeSpan.FromSeconds(5));
            b.Coordinator.OnPresented(wordB, pid, true);
            b.Clock.Advance(TimeSpan.FromSeconds(3));
            b.Coordinator.OnRated(wordB, pid, StudyRating.Unsure, 0, 0, null);
        }
        var resultB = b.Coordinator.CommitWord(wordB, StudyMode.Review);
        Program.Check(resultB is not null, "首评 Known 后仍能定稿");
        Program.Check(b.Store.LastCommit!.Canonical.Rating == StudyRating.Known,
            "首次评分不可被后续覆盖：首评 Known → canonical Known（聚合口径会给出 Unsure）");
    }

    // ==================================================================================
    // 3. 直接认识 vs 认识后改判忘记
    // ==================================================================================

    private static void TrajectoryShapesDiffer()
    {
        var h = new Harness();
        var direct = ReviewWord(h, "traj-direct");
        var revised = ReviewWord(h, "traj-revised");
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 2);

        h.Coordinator.OnPresented(direct, "d1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(4));
        h.Coordinator.OnRated(direct, "d1", StudyRating.Known, 0, 1, null);
        var r1 = h.Coordinator.CommitWord(direct, StudyMode.Review);

        h.Coordinator.OnPresented(revised, "r1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(4));
        h.Coordinator.OnRated(revised, "r1", StudyRating.Known, 0, 1, null);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Coordinator.OnRevised(revised, "r1", StudyRating.Known, StudyRating.Forgot, null);
        var r2 = h.Coordinator.CommitWord(revised, StudyMode.Review);

        Program.Check(r1 is not null && r2 is not null, "两条轨迹都能定稿");
        Program.Check(h.Store.Commits.Count == 2, "两个词各定稿一次");

        var summaryDirect = h.Store.CommitFor(direct.Key)!.Summary;
        var summaryRevised = h.Store.CommitFor(revised.Key)!.Summary;
        Program.Check(summaryDirect.KnownToForgotten == 0 && summaryRevised.KnownToForgotten == 1,
            "认识后改判忘记 → KnownToForgotten=1，直接认识 → 0");
        Program.Check(summaryDirect.HadResponseRevision == false && summaryRevised.HadResponseRevision,
            "改判轨迹标记 HadResponseRevision");
        Program.Check(summaryDirect.FinalKnownCount == 1 && summaryRevised.FinalForgottenCount == 1,
            "两条轨迹的最终分布不同");
        Program.Check(h.Store.CommitFor(direct.Key)!.Canonical.Rating == StudyRating.Known
            && h.Store.CommitFor(revised.Key)!.Canonical.Rating == StudyRating.Forgot,
            "两条轨迹产生不同的 canonical");
    }

    // ==================================================================================
    // 4. Learn 卡不计入 retrieval
    // ==================================================================================

    private static void LearnCardIsNotRetrieval()
    {
        var h = new Harness();
        var word = ReviewWord(h, "learn-card");
        h.Coordinator.BeginSession(StudyMode.FirstLearn, WordSource.Archive, "", 1);

        // 只有 Learn 卡（答案可见），即使"作答"也不构成 retrieval
        h.Coordinator.OnPresented(word, "L1", false);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "L1", StudyRating.Known, 0, 1, null);

        var onlyLearn = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(onlyLearn is null, "只有 Learn 事件 → 无有效 retrieval → CommitWord 返回 null");
        Program.Check(h.Store.CommitWordSessionCalls == 0, "只有 Learn 事件时定稿事务零写入");
        Program.Check(h.Store.GetCard(word.Key) is null, "只有 Learn 事件时不产生 FSRS 卡");

        // 混入真实 Recall
        for (var i = 0; i < 3; i++)
        {
            var pid = $"R{i}";
            h.Clock.Advance(TimeSpan.FromSeconds(5));
            h.Coordinator.OnPresented(word, pid, true);
            h.Clock.Advance(TimeSpan.FromSeconds(3));
            h.Coordinator.OnRated(word, pid, StudyRating.Known, i, i + 1, null);
        }
        var mixed = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(mixed is not null, "混入真实 Recall 后可以定稿");
        var commit = h.Store.LastCommit!;
        Program.Check(commit.Summary.TotalPresentations == 4, "Learn 卡仍计入呈现总数（4）");
        Program.Check(commit.Summary.FinalKnownCount == 3, "FinalKnownCount 只统计真实 Recall（3）");
        Program.Check(commit.Canonical.Origin == CanonicalOrigin.FirstRetrieval && commit.Canonical.SourcePresentationId == "R0",
            "canonical 指向第一个真实 Recall，而不是 Learn 卡");
        Program.Check(commit.Canonical.Rating == StudyRating.Known, "canonical = Known");
    }

    // ==================================================================================
    // 5. 无有效 retrieval → null 且零写入
    // ==================================================================================

    private static void NoValidRetrievalWritesNothing()
    {
        var h = new Harness();
        var word = ReviewWord(h, "no-retrieval");
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        h.Coordinator.OnPresented(word, "p1", true); // 呈现了但用户没作答

        var appendsBefore = h.Store.AppendEventCalls;
        var result = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(result is null, "呈现但未作答 → 无有效 retrieval → 返回 null");
        Program.Check(h.Store.CommitWordSessionCalls == 0, "零写入：CommitWordSession 未被调用");
        Program.Check(h.Store.AppendEventCalls == appendsBefore, "零写入：CommitWord 不追加任何事件");
        Program.Check(h.Store.SnapshotCount == 1, "零写入：只留下呈现时那一张快照（作答前快照不受影响）");

        var unknown = h.Coordinator.CommitWord(WordKey.Archive("never-seen"), StudyMode.Review);
        Program.Check(unknown is null, "本轮完全没有轨迹的词 → 返回 null");
        Program.Check(h.Store.CommitWordSessionCalls == 0, "完全没有轨迹时同样零写入");
    }

    // ==================================================================================
    // 6. 作答延迟
    // ==================================================================================

    private static void LatencyPauseAndReliability()
    {
        var h = new Harness();
        var word = ReviewWord(h, "latency");
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);

        // 6a：暂停期间不计入
        h.Coordinator.OnPresented(word, "p1", true);
        h.Coordinator.PauseLatency();
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Coordinator.ResumeLatency();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Coordinator.OnRated(word, "p1", StudyRating.Known, 0, 1, null);
        Program.Check(h.Store.LastEvent!.ResponseLatencyMs == 2000,
            $"暂停的 10 分钟不计入，只记 2 秒（实际 {h.Store.LastEvent.ResponseLatencyMs}）");

        // 6b：超过 30 分钟视为离开 → null
        h.Coordinator.OnPresented(word, "p2", true);
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        h.Coordinator.OnRated(word, "p2", StudyRating.Known, 1, 2, null);
        Program.Check(h.Store.LastEvent!.ResponseLatencyMs is null, "累计 > 30 分钟 → null（不可靠就不记）");

        // 6c：累计为 0 → null
        h.Coordinator.OnPresented(word, "p3", true);
        h.Coordinator.OnRated(word, "p3", StudyRating.Known, 2, 3, null);
        Program.Check(h.Store.LastEvent!.ResponseLatencyMs is null, "累计 0ms → null");

        // 6d：调用方显式传值（没有经过 OnPresented → 本层无计时，回退到调用方值）
        h.Coordinator.OnRated(word, "p9", StudyRating.Known, 3, 4, 1500);
        Program.Check(h.Store.LastEvent!.ResponseLatencyMs == 1500, "无本层计时时回退到调用方给出的延迟");
        h.Coordinator.OnRated(word, "p10", StudyRating.Known, 3, 4, 0);
        Program.Check(h.Store.LastEvent!.ResponseLatencyMs is null, "调用方给出 0ms → 仍记 null");
        h.Coordinator.OnRated(word, "p11", StudyRating.Known, 3, 4, 31 * 60 * 1000);
        Program.Check(h.Store.LastEvent!.ResponseLatencyMs is null, "调用方给出 >30 分钟 → 仍记 null");

        // 6e：暂停/恢复对每个新 presentation 重新起算
        h.Coordinator.OnPresented(word, "p4", true);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Coordinator.OnRated(word, "p4", StudyRating.Known, 3, 4, null);
        Program.Check(h.Store.LastEvent!.ResponseLatencyMs == 1000, "新 presentation 的计时独立起算");
    }

    // ==================================================================================
    // 7. UndoCommit
    // ==================================================================================

    private static void UndoCommitInvalidatesWithoutDeletingEvents()
    {
        var h = new Harness();
        var word = ReviewWord(h, "undo");
        var sessionId = h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        h.Coordinator.OnPresented(word, "p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "p1", StudyRating.Known, 0, 1, null);

        var result = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(result is not null && result.Applied, "定稿成功");
        Program.Check(h.Store.GetCard(word.Key) is not null, "定稿后 FSRS 卡已写入");
        var eventsBeforeUndo = h.Store.AppendEventCalls;

        var undone = h.Coordinator.UndoCommit(result!.CanonicalId);
        Program.Check(undone, "UndoCommit 返回 store 的结果（true）");
        Program.Check(h.Store.InvalidateCanonicalCalls == 1, "调用了 store.InvalidateCanonical 一次");
        Program.Check(h.Store.LastInvalidateReason == "user-undo", "失效原因 = user-undo");
        Program.Check(h.Store.LastInvalidateCanonicalId == result.CanonicalId, "失效的是同一个 CanonicalId");
        Program.Check(h.Store.LastInvalidateAtUtc == h.Clock.Now, "失效时刻来自注入时钟");
        Program.Check(h.Store.AppendEventCalls == eventsBeforeUndo, "撤销不删除、也不追加任何事件");
        Program.Check(h.Store.EventCount == 3, $"raw events 全部保留：1 条 seed + Presented + Rated = {h.Store.EventCount}");
        Program.Check(h.Store.PreStateRestoreCount == 1, "用 canonical 里保存的 pre-state 精确回放");
        Program.Check(h.Store.GetCard(word.Key) is null, "定稿前没有卡 → 撤销后卡被移除（不是被清零）");
        Program.Check(h.Store.Commits[0].Canonical.Invalidated, "canonical 标记为 invalidated");
        Program.Check(h.Coordinator.CurrentSessionId == sessionId, "撤销会话不变");
    }

    // ==================================================================================
    // §Q / 裁定 O-2：同词同日跨 session 两条 canonical 并存；第二条走 elapsed_days == 0 的
    // short-term 路径，且当天较早那次 Again 造成的 lapse 不被抹掉。
    // ==================================================================================

    private static void SameDaySecondSessionDoesNotEraseLapse()
    {
        var h = new Harness();
        h.Clock.Now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var startedAt = h.Clock.Now;
        var word = ReviewWord(h, "same-day-lapse");

        // session 1：唯一一次作答是 Forgot → 记一次 lapse
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-day", 2);
        h.Coordinator.OnPresented(word, "lap-s1p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Coordinator.OnRated(word, "lap-s1p1", StudyRating.Forgot, 0, 0, null);
        var first = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(first is not null && first.Applied, "当天第一次定稿成功（Again）");
        var afterFirst = h.Store.GetCard(word.Key)!;
        Program.Check(afterFirst.Lapses == 1, $"第一次 Again 记下 1 次 lapse（实际 {afterFirst.Lapses}）");

        // session 2：同一本地日历日、30 分钟之后，作答 Known
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        Program.Check(h.Clock.Now.ToLocalTime().Date == startedAt.ToLocalTime().Date,
            "测试前提：两次定稿落在同一个本地日历日");
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-day", 2);
        h.Coordinator.OnPresented(word, "lap-s2p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Coordinator.OnRated(word, "lap-s2p1", StudyRating.Known, 0, 1, null);
        var second = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(second is not null && second.Applied, "同一天第二次定稿成功（不丢用户的第二次学习）");

        // —— 核心：session1 的 lapse 不被 session2 抹掉 ——
        var card = h.Store.GetCard(word.Key)!;
        Program.Check(card.Lapses >= 1, $"session1 的 Again 没有被 session2 抹掉（Lapses 实际 {card.Lapses}）");
        Program.Check(card.Lapses == 1, $"Lapses 恰好 1（第二条 Known 既不清零也不再累加）（实际 {card.Lapses}）");

        // —— 第二张卡是在第一条 canonical 之后的卡状态上继续演进的 ——
        Program.Check(card.Reps == 2, $"两次 canonical 各推进一次 Reps（实际 {card.Reps}）");
        Program.CheckClose(card.Stability, afterFirst.Stability + h.Scheduler.BaselineForKnown, 1e-12,
            $"第二条 canonical 从第一条之后的稳定性继续演进（{afterFirst.Stability}+{h.Scheduler.BaselineForKnown}）");

        // —— 没有失效、没有 pre-state 回放 ——
        Program.Check(h.Store.InvalidateCanonicalCalls == 0,
            $"同日第二次定稿不再失效当天较早那条（实际调用 InvalidateCanonical {h.Store.InvalidateCanonicalCalls} 次）");
        Program.Check(h.Store.PreStateRestoreCount == 0,
            $"没有发生任何 pre-state 回放（实际 {h.Store.PreStateRestoreCount} 次）");

        // —— 第二条走 elapsed_days == 0 的 short-term 路径（喂给调度器的 pre-state 落在同一天）——
        var calls = h.Scheduler.Calls;
        Program.Check(calls.Count == 2, $"调度器被调用两次（实际 {calls.Count}）");
        var secondCall = calls[1];
        Program.Check(secondCall.Card is not null && secondCall.Card.Lapses == 1,
            "第二次 FSRS 更新拿到的 pre-state 已经带着 session1 的 lapse");
        Program.Check(secondCall.Card!.LastReviewAtUtc is { } lastReview
                      && lastReview.ToLocalTime().Date == secondCall.ReviewedAtUtc.ToLocalTime().Date,
            "第二次更新的 elapsed_days == 0：pre-state 的 LastReviewAtUtc 与本次作答落在同一个本地日");
        Program.Check(secondCall.ReviewedAtUtc - secondCall.Card.LastReviewAtUtc!.Value < TimeSpan.FromDays(1),
            $"…且两者相隔不足 24 小时（整天截断后 elapsed_days = 0，实际 {(secondCall.ReviewedAtUtc - secondCall.Card.LastReviewAtUtc!.Value).TotalMinutes} 分钟）");
    }

    // ==================================================================================
    // 8. 同词多入口共享
    // ==================================================================================

    private static void SameWordAcrossSessionsSharesCard()
    {
        var h = new Harness();
        var word = ReviewWord(h, "shared");

        var session1 = h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        h.Coordinator.OnPresented(word, "p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "p1", StudyRating.Known, 0, 1, null);
        var first = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(first!.CanonicalId == CanonicalReview.BuildId(word.Key, session1), "session1 的 canonical 键");

        var replay = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(replay!.AlreadyApplied && replay.CanonicalId == first.CanonicalId,
            "同一 session 内重复定稿仍复用同一个 canonical");

        var session2 = h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        Program.Check(session2 != session1, "BeginSession 产生新的 SessionId");
        h.Coordinator.OnPresented(word, "p2", true);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "p2", StudyRating.Known, 0, 1, null);
        // seed 历史 1 条 Presented → session1 的 p1 是第 2 次，session2 的 p2 是第 3 次
        Program.Check(h.Store.FindEvent("p1", InteractionEventKind.Presented)!.WordAppearanceIndex == 2,
            "session1 的呈现是全历史第 2 次（缓存从 store 懒加载）");
        Program.Check(h.Store.LastEvent!.WordAppearanceIndex == 3,
            $"同一 WordKey 跨 session 继续累加「全历史第几次被呈现」（实际 {h.Store.LastEvent.WordAppearanceIndex}）");
        Program.Check(!h.Store.LastEvent.IsFirstAppearanceForWord, "第二次出现不再是首次出现");

        var second = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(second!.CanonicalId != first.CanonicalId, "不同 session → 不同的 CanonicalId");
        Program.Check(second.CanonicalId == CanonicalReview.BuildId(word.Key, session2), "session2 的 canonical 键");
        Program.Check(h.Store.AppliedCanonicalCount == 2, "两次定稿都真的落库（不是复用同一个 canonical 行）");
        Program.Check(h.Store.CardApplyCount == 2, "两个 session 各施加一次 FSRS");
        Program.Check(h.Store.GetCard(word.Key)!.Reps == 2, "同一个词共享同一张 FSRS 卡（跨 session 累加）");
        Program.Check(h.Store.LastCommit!.Summary.TotalPresentations == 1, "summary 只覆盖本 session 的呈现");
    }

    // ==================================================================================
    // 9. Context 快照在作答前生成
    // ==================================================================================

    private static void ContextSnapshotCapturedBeforeRating()
    {
        var features = new FakeContextFeatures();
        var calibrator = new FakeCalibrator
        {
            Mode = ContextMode.Active,
            Apply = true,
            Delta = 0.2,
            Candidate = 100, // 远超 base*1.5，用来验证 clamp
        };
        var h = new Harness(features, calibrator);
        var word = ReviewWord(h, "ctx-snapshot");
        h.Store.SeedCard(new FsrsCardState
        {
            WordKey = word.Key,
            Difficulty = 4.5,
            Stability = 10,
            Reps = 2,
            State = FsrsState.Review,
            LastReviewAtUtc = h.Clock.Now.AddDays(-5),
        });

        var sessionId = h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        h.Coordinator.OnPresented(word, "p1", true);

        var snapshot = h.Store.GetSnapshotByPresentation("p1");
        Program.Check(snapshot is not null, "OnPresented 之后、OnRated 之前快照已落库");
        Program.Check(snapshot!.PresentationId == "p1" && snapshot.SessionId == sessionId && snapshot.WordKey == word.Key,
            "快照关联到 presentation / session / word");
        Program.Check(snapshot.CapturedAtUtc == h.Clock.Now, "CapturedAtUtc = 呈现时刻");
        Program.Check(snapshot.FsrsDifficultyAtCapture == 4.5 && snapshot.FsrsStabilityAtCapture == 10,
            "快照取呈现时的卡状态（D=4.5 / S=10）");
        Program.CheckClose(snapshot.FsrsRetrievabilityAtCapture, h.Scheduler.Retrievability(10, 5), 1e-12,
            "R 用整天截断的 elapsedDays 计算");
        Program.Check(snapshot.FeaturesJson == "[1,2]" && snapshot.MissingFlagsJson == "[false,false]",
            "FeaturesJson / MissingFlagsJson 序列化");
        Program.Check(snapshot.FeatureSchemaVersion == "ctx-feat-1" && snapshot.ModelVersion == "test-model-1",
            "快照写入 provider 的 schema / model 版本");
        Program.Check(features.Requests.Count == 1, "呈现时调用了一次特征提供者");
        Program.Check(features.Requests[0].SessionReviewedCount == 0, "作答前生成：SessionReviewedCount=0");
        Program.Check(features.Requests[0].IsFirstAppearanceForSession,
            "本 session 内第一次呈现该词 → IsFirstAppearanceForSession=true（与全历史首次不同）");
        Program.Check(features.Requests[0].SessionPosition == 1, "SessionPosition = 本 session 内第 1 次呈现");
        Program.Check(features.Requests[0].Mode == LearningMode.Review, "已出现过的词 → Review");
        Program.Check(features.Requests[0].Card is not null && features.Requests[0].Card!.Difficulty == 4.5,
            "请求里带上呈现时的卡状态");

        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "p1", StudyRating.Known, 0, 1, null);
        var result = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(result is not null, "定稿成功");

        var cardAfter = h.Store.GetCard(word.Key)!;
        Program.Check(cardAfter.Stability == 40, $"定稿后卡被 FSRS 更新（S=10+30=40，实际 {cardAfter.Stability}）");
        Program.Check(snapshot.FsrsStabilityAtCapture == 10 && snapshot.FsrsDifficultyAtCapture == 4.5,
            "定稿不修改已落库的快照；快照仍是作答前的状态");

        Program.Check(calibrator.Adjusts.Count == 1, "只对第一次真实 retrieval 校准一次");
        Program.CheckClose(calibrator.Adjusts[0].R, snapshot.FsrsRetrievabilityAtCapture, 1e-12,
            "校准用的是快照的历史可回忆性，不是作答后的重算值");
        Program.Check(calibrator.Adjusts[0].Features.Values.Length == 2
            && calibrator.Adjusts[0].Features.Values[0] == 1.0,
            "校准用的是作答前那份特征向量");
        Program.Check(calibrator.SolveStability == 40, "候选间隔以本次 FSRS 更新后的 stability 为锚点");
        Program.CheckClose(calibrator.SolveRetention, SchedulingConfig.DesiredRetention, 1e-12,
            "候选间隔的目标保持率 = SchedulingConfig.DesiredRetention");

        var commit = h.Store.LastCommit!;
        var decision = commit.Decision;
        Program.Check(decision.ContextMode == ContextMode.Active, "决策记录 ContextMode=Active");
        Program.Check(decision.ContextDelta == 0.2, "决策记录 ContextDelta");
        Program.Check(decision.ContextCandidateIntervalDays == 100, "决策记录候选间隔");
        Program.CheckClose(decision.BaselineIntervalDays, 30, 1e-12, "纯 FSRS 基线 = 30 天");
        Program.CheckClose(decision.FinalIntervalDays, 45, 1e-12, "候选间隔被 clamp 到 base×1.5 = 45 天");
        Program.Check(decision.ContextModelVersion == "test-model-1", "决策记录 ContextModelVersion");
        Program.Check(decision.FsrsParameterVersion == "params-test-1", "决策记录 FSRS 参数版本（取自 FSRS 卡）");
        Program.Check(decision.FsrsAlgorithmVersion == "fsrs-6-test" && decision.FsrsLibraryVersion == "test-1",
            "决策记录 FSRS 算法 / 库版本");
        Program.Check(decision.AggregationPolicyVersion == AggregationPolicy.Version
            && decision.SchedulerVersion == SchedulingConfig.SchedulerVersion
            && decision.TrajectorySchemaVersion == SchedulingConfig.TrajectorySchemaVersion
            && decision.ContextFeatureSchemaVersion == SchedulingConfig.ContextFeatureSchemaVersion,
            "决策集中注入所有版本戳");
        Program.Check(decision.DesiredRetention == SchedulingConfig.DesiredRetention, "决策记录目标保持率");

        Program.Check(commit.SnapshotLabels.Count == 1
            && commit.SnapshotLabels[0].SnapshotId == snapshot.SnapshotId
            && commit.SnapshotLabels[0].Rating == StudyRating.Known
            && commit.SnapshotLabels[0].ConfidentRecall,
            "第一次真实 retrieval 的快照被打上 canonical 标签（Known / ConfidentRecall）");
        Program.Check(h.Store.LabelSnapshotsCalls == 1, "标签随定稿事务写入");
        var labeled = h.Store.GetContextSnapshot(snapshot.SnapshotId)!;
        Program.Check(labeled.Label == StudyRating.Known && labeled.ConfidentRecall == true && labeled.LabeledAtUtc is not null,
            "快照行上确实写下了标签");
    }

    // ==================================================================================
    // 10. Context 关闭时的路径
    // ==================================================================================

    private static void ContextDisabledPaths()
    {
        // 10a：没有特征提供者 → 仍然写快照，但 FeaturesJson 为空数组
        var h = new Harness();
        var word = ReviewWord(h, "ctx-off");
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        h.Coordinator.OnPresented(word, "p1", true);

        var snapshot = h.Store.GetSnapshotByPresentation("p1");
        Program.Check(snapshot is not null, "没有特征提供者时仍然写一条快照");
        Program.Check(snapshot!.FeatureSchemaVersion == "ctx-feat-1", "无 provider 时 schema 版本 = ctx-feat-1");
        Program.Check(snapshot.ModelVersion == "", "无 provider 时模型版本为空串");
        Program.Check(snapshot.FeaturesJson == "[]", "无 provider 时 FeaturesJson = []");
        Program.Check(snapshot.MissingFlagsJson == "[]", "无 provider 时 MissingFlagsJson = []");
        Program.Check(snapshot.FsrsDifficultyAtCapture == 0 && snapshot.FsrsStabilityAtCapture == 0,
            "无卡时 D/S 记 0");
        Program.Check(snapshot.FsrsRetrievabilityAtCapture == 1.0, "无卡时 R 记 1.0");

        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "p1", StudyRating.Known, 0, 1, null);
        var result = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(result is not null, "无校准器时仍能定稿");
        var decision = h.Store.LastCommit!.Decision;
        Program.Check(decision.ContextMode == ContextMode.Disabled, "无校准器 → ContextMode=Disabled");
        Program.Check(decision.ContextDelta is null && decision.ContextCandidateIntervalDays is null,
            "未应用校准 → Delta / candidate 均为 null");
        Program.CheckClose(decision.FinalIntervalDays, 30, 1e-12, "未应用校准 → 最终间隔 = 纯 FSRS 基线");
        Program.Check(h.Store.SnapshotCount == 1, "自始至终只有一条快照");
    }

    // ==================================================================================
    // 11. 异常传播
    // ==================================================================================

    private static void CommitExceptionPropagates()
    {
        var h = new Harness();
        var word = ReviewWord(h, "boom");
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        h.Coordinator.OnPresented(word, "p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "p1", StudyRating.Known, 0, 1, null);

        h.Store.CommitException = new InvalidOperationException("磁盘写入失败（测试注入）");
        var threw = false;
        try
        {
            h.Coordinator.CommitWord(word, StudyMode.Review);
        }
        catch (InvalidOperationException ex)
        {
            threw = ex.Message == "磁盘写入失败（测试注入）";
        }
        Program.Check(threw, "store.CommitWordSession 抛出的异常原样向外抛，不被吞掉");

        h.Store.CommitException = null;
        var retry = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(retry is not null && retry.Applied, "失败后重试仍可定稿（幂等键未被消费）");

        var began = false;
        try { new LearningMemoryCoordinator(new FakeMemoryStore(), new FakeScheduler()).OnPresented(word, "x", true); }
        catch (InvalidOperationException) { began = true; }
        Program.Check(began, "未 BeginSession 就呈现 → 抛出 InvalidOperationException");
    }

    // ==================================================================================
    // 12. 特征向量契约
    // ==================================================================================

    private static void ContextVectorContractEnforced()
    {
        var h = new Harness(new FakeContextFeatures
        {
            Builder = _ => new ContextFeatureVector([1.0], ["a", "b"], [false]),
        });
        var word = ReviewWord(h, "ctx-bad-vector");
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        var mismatched = false;
        try { h.Coordinator.OnPresented(word, "p1", true); }
        catch (InvalidOperationException ex) { mismatched = ex.Message.Contains("长度不一致"); }
        Program.Check(mismatched, "Values/Names/Missing 长度不一致 → 立即抛出");

        // 非有限值按缺失处理，且仍能序列化
        var h2 = new Harness(new FakeContextFeatures
        {
            Builder = _ => new ContextFeatureVector([double.NaN, 2.0, double.PositiveInfinity], ["a", "b", "c"], [false, false, false]),
        });
        var word2 = ReviewWord(h2, "ctx-nan");
        h2.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        h2.Coordinator.OnPresented(word2, "p1", true);
        var snapshot = h2.Store.GetSnapshotByPresentation("p1")!;
        Program.Check(snapshot.FeaturesJson == "[0,2,0]", $"非有限特征值置 0（实际 {snapshot.FeaturesJson}）");
        Program.Check(snapshot.MissingFlagsJson == "[true,false,true]",
            $"非有限特征值标记为缺失（实际 {snapshot.MissingFlagsJson}）");
    }

    // ==================================================================================
    // 13. 定稿载荷形状
    // ==================================================================================

    private static void CommitPayloadShape()
    {
        var h = new Harness();
        var word = ReviewWord(h, "payload");
        var sessionId = h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-9", 12);
        h.Coordinator.OnPresented(word, "p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "p1", StudyRating.Known, 0, 1, null);
        var completedAt = h.Clock.Now;
        h.Coordinator.CommitWord(word, StudyMode.Review);

        var commit = h.Store.LastCommit!;
        Program.Check(commit.Session.SessionId == sessionId && commit.Session.PlanId == "plan-9"
            && commit.Session.PlannedWordCount == 12 && commit.Session.Mode == StudyMode.Review,
            "载荷带上完整的学习会话");
        Program.Check(commit.Summary.CompletedAtUtc == completedAt, "summary 的定稿时刻来自注入时钟");
        Program.Check(commit.PendingEvents.Count == 0,
            "PendingEvents 为空：raw events 已在 On* 里即时落库，定稿事务只负责派生部分的原子性");
        Program.Check(commit.SnapshotsToSave.Count == 0,
            "SnapshotsToSave 为空：快照已在 OnPresented 即时落库");
        Program.Check(commit.OutboxMutations.Count == 0,
            "OutboxMutations 为空：本层不产生 JSON 侧变更（由 UI 调用点补 PendingMutation）");
        Program.Check(commit.CardPreState.Existed == false, "新词的 pre-state 标记为不存在");
        Program.Check(commit.CardAfter.NextReviewAtUtc == commit.Decision.FinalDueAtUtc,
            "CardAfter.NextReviewAtUtc = 决策的最终到期时刻");
        Program.Check(commit.CardAfter.NextReviewAtUtc!.Value.Kind == DateTimeKind.Utc, "到期时刻 Kind = Utc");
        Program.Check(commit.CardAfter.LastReviewAtUtc == commit.Canonical.ReviewedAtUtc, "LastReviewAtUtc = canonical 时刻");
        Program.Check(commit.CardAfter.LastCanonicalRating == commit.Canonical.Rating, "LastCanonicalRating = canonical 评级");
        Program.Check(commit.CardAfter.WordKey == word.Key, "CardAfter 绑定到正确的 WordKey");
        Program.Check(commit.Decision.CanonicalId == commit.Canonical.CanonicalId, "决策与 canonical 一一对应");
        Program.Check(commit.Decision.WordKey == word.Key, "决策绑定到正确的 WordKey");
        Program.Check(commit.Decision.TimestampUtc == completedAt, "决策时刻来自注入时钟");
        Program.Check(commit.Summary.WordKey == word.Key
            && commit.Summary.SessionId == sessionId, "summary 绑定到 word × session");
        Program.Check(h.Store.Decisions.Count == 1, "决策已随事务写入");

        // 调度器没有盖版本戳时，回退到 SchedulingConfig 的冻结常量（审计列不为空串）
        var h2 = new Harness();
        h2.Scheduler.StampVersions = false;
        var word2 = ReviewWord(h2, "payload-nostamp");
        h2.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        h2.Coordinator.OnPresented(word2, "p1", true);
        h2.Clock.Advance(TimeSpan.FromSeconds(3));
        h2.Coordinator.OnRated(word2, "p1", StudyRating.Known, 0, 1, null);
        h2.Coordinator.CommitWord(word2, StudyMode.Review);
        var noStamp = h2.Store.LastCommit!;
        Program.Check(noStamp.Decision.FsrsAlgorithmVersion == SchedulingConfig.AlgorithmVersion
            && noStamp.Decision.FsrsLibraryVersion == SchedulingConfig.LibraryVersion
            && noStamp.Decision.FsrsParameterVersion == SchedulingConfig.ParameterVersion,
            "调度器未盖版本戳 → 回退到 SchedulingConfig 冻结常量（AlgorithmVersion / LibraryVersion / ParameterVersion）");
        Program.Check(noStamp.CardAfter.FsrsParameterVersion == SchedulingConfig.ParameterVersion
            && noStamp.CardAfter.FsrsAlgorithmVersion == SchedulingConfig.AlgorithmVersion,
            "同一个回退值也写进 FSRS 卡，避免卡与决策版本戳不一致");
    }

    // ==================================================================================
    // 14. 到期队列透传
    // ==================================================================================

    private static void DueQueuePassthrough()
    {
        var h = new Harness();
        var word = ReviewWord(h, "due");
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        Program.Check(h.Coordinator.CountDue(h.Clock.Now) == 0, "未定稿前到期队列为空");
        Program.Check(h.Coordinator.QueryDue(h.Clock.Now, 10).Count == 0, "未定稿前查询到期队列为空");

        h.Coordinator.OnPresented(word, "p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Coordinator.OnRated(word, "p1", StudyRating.Known, 0, 1, null);
        h.Coordinator.CommitWord(word, StudyMode.Review);
        var dueAt = h.Store.LastCommit!.Decision.FinalDueAtUtc;

        Program.Check(h.Coordinator.CountDue(h.Clock.Now) == 0, "未到期的词不在到期队列里");
        Program.Check(h.Coordinator.CountDue(dueAt) == 1, "到期后计数为 1");
        var due = h.Coordinator.QueryDue(dueAt, 10);
        Program.Check(due.Count == 1 && due[0].WordKey == word.Key, "到期队列透传 store 的结果");
    }

    // ==================================================================================
    // 15. 事件标注规则
    // ==================================================================================

    private static void EventTaggingRules()
    {
        var h = new Harness();
        var firstEver = WordKey.Archive("tag-first");
        var seen = WordKey.Archive("tag-seen");
        h.Store.SeedPresented(seen.Key, "prior-session", h.Clock.Now.AddDays(-7));
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 2);

        h.Coordinator.OnPresented(firstEver, "a1", true);
        h.Coordinator.OnPresented(seen, "b1", true);

        var a = h.Store.FindEvent("a1", InteractionEventKind.Presented)!;
        var b = h.Store.FindEvent("b1", InteractionEventKind.Presented)!;
        Program.Check(a.WordAppearanceIndex == 1 && a.IsFirstAppearanceForWord,
            "全历史第 1 次呈现 → WordAppearanceIndex=1 / IsFirstAppearanceForWord");
        Program.Check(a.LearningMode == LearningMode.FirstLearn,
            "全历史第 1 次出现 → 事件 LearningMode=FirstLearn（与 StudyRound 的 isFirstLearn 口径一致）");
        Program.Check(a.SessionAppearanceIndex == 1, "SessionAppearanceIndex 从 1 开始");
        Program.Check(a.ResponseLatencyMs is null && a.Response is null, "Presented 事件不带作答与延迟");
        Program.Check(a.RecognitionCountBefore == 0 && a.RecognitionCountAfter == 0, "Presented 事件填当前连击值（0）");
        Program.Check(a.EventId.Length == 32, "EventId 是 Guid \"N\"（32 位十六进制）");

        Program.Check(b.WordAppearanceIndex == 2 && !b.IsFirstAppearanceForWord,
            "已有历史的词 → WordAppearanceIndex=2");
        Program.Check(b.LearningMode == LearningMode.Review, "已有历史的词 → 事件 LearningMode=Review（沿用 session mode）");

        // 作答 / 改判 / 撤销继承 presentation 的 IsRecall，并正确维护 SupersedesEventId
        var learn = WordKey.Archive("tag-learn");
        h.Coordinator.OnPresented(learn, "c1", false);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Coordinator.OnRated(learn, "c1", StudyRating.Known, 0, 1, null);
        var rated = h.Store.FindEvent("c1", InteractionEventKind.Rated)!;
        Program.Check(!rated.IsRecall, "Rated 事件从该 presentation 的 Presented 事件继承 IsRecall=false");
        Program.Check(rated.Response == StudyRating.Known && rated.PreviousResponse is null, "Rated 事件带判断");
        Program.Check(h.Store.SnapshotFor("c1") is null, "Learn 卡不产生 Context 快照（不参与训练）");

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Coordinator.OnRevised(learn, "c1", StudyRating.Known, StudyRating.Forgot, null);
        var revised = h.Store.FindEvent("c1", InteractionEventKind.Revised)!;
        Program.Check(revised.PreviousResponse == StudyRating.Known && revised.Response == StudyRating.Forgot,
            "Revised 事件记录 from/to");
        Program.Check(revised.SupersedesEventId == rated.EventId, "Revised 指向被修正的事件");

        var countBeforeUndo = h.Store.EventCount;
        h.Coordinator.OnUndone(learn, "c1");
        var undone = h.Store.FindEvent("c1", InteractionEventKind.Undone)!;
        Program.Check(undone.SupersedesEventId == revised.EventId, "Undone 指向被撤销的那条判断");
        Program.Check(undone.Response is null, "Undone 自身不带判断");
        Program.Check(h.Store.EventCount == countBeforeUndo + 1,
            $"Undone 只追加一条事件，不删除任何历史（{countBeforeUndo} → {h.Store.EventCount}）");
        Program.Check(h.Store.FindEvent("c1", InteractionEventKind.Rated) is not null
            && h.Store.FindEvent("c1", InteractionEventKind.Revised) is not null,
            "被撤销的作答与改判仍留在事件流里");

        // 全历史呈现计数被缓存，不会每次扫全表
        var historyCallsBefore = h.Store.LoadEventsForWordCalls;
        h.Coordinator.OnPresented(seen, "b2", true);
        Program.Check(h.Store.LoadEventsForWordCalls == historyCallsBefore,
            "已缓存的词再次呈现不重新扫全表（WordAppearanceIndex 走内存缓存）");
        Program.Check(h.Store.FindEvent("b2", InteractionEventKind.Presented)!.WordAppearanceIndex == 3,
            "缓存继续递增：第 3 次呈现");
    }

    // ==================================================================================
    // §Q / 裁定 O-2：同一本地日历日、**不同 session** 的两条 canonical 都保持有效
    // （强制「每词每日一条」是主 agent 自己发明的约束，已撤销）
    // ==================================================================================

    private static void SameWordSameDayKeepsBothCanonicals()
    {
        var h = new Harness();
        h.Clock.Now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var startedAt = h.Clock.Now;
        var word = ReviewWord(h, "same-local-day");

        // session 1
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-day", 2);
        h.Coordinator.OnPresented(word, "day1p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Coordinator.OnRated(word, "day1p1", StudyRating.Known, 0, 1, null);
        var first = h.Coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(first is not null && first.Applied, "当天第一次定稿成功");

        // session 2（同一本地日历日，30 分钟后）
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        Program.Check(h.Clock.Now.ToLocalTime().Date == startedAt.ToLocalTime().Date,
            "测试前提：两次定稿落在同一个本地日历日");
        var session2 = h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-day", 2);
        h.Coordinator.OnPresented(word, "day2p1", true);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Coordinator.OnRated(word, "day2p1", StudyRating.Forgot, 0, 0, null);
        var second = h.Coordinator.CommitWord(word, StudyMode.Review);

        Program.Check(second is not null && second.Applied, "同一天第二个 session 的定稿仍然成功（不丢用户的第二次学习）");
        Program.Check(second!.CanonicalId == CanonicalReview.BuildId(word.Key, session2),
            "第二条 canonical 的键按它自己的 session 生成（cr:wordKey:sessionId）");
        Program.Check(second.CanonicalId != first!.CanonicalId, "两次定稿是不同的 canonical 身份（word×session 各自一条）");
        Program.Check(h.Store.CanonicalRowCount(word.Key) == 2,
            $"canonical_reviews 里两行都在（实际 {h.Store.CanonicalRowCount(word.Key)}）");
        Program.Check(h.Store.ValidCanonicalCount(word.Key) == 2,
            $"两条 canonical 都 invalidated=0（有效 {h.Store.ValidCanonicalCount(word.Key)} / 共 {h.Store.CanonicalRowCount(word.Key)}）");
        Program.Check(h.Store.InvalidateCanonicalCalls == 0,
            $"同日第二次定稿不失效任何 canonical（实际调用 {h.Store.InvalidateCanonicalCalls} 次）");
        Program.Check(h.Store.GetCard(word.Key) is not null, "卡片仍然存在");
        Program.Check(h.Store.GetCard(word.Key)!.Reps == 2,
            $"两个 session 各自推进一次 FSRS（Reps 实际 {h.Store.GetCard(word.Key)!.Reps}）");
        Program.Check(h.Store.GetCard(word.Key)!.LastCanonicalRating == StudyRating.Forgot,
            "卡片反映的是当天最后一条有效 canonical 的判断");
        Program.Check(h.Store.EventCountFor(word.Key) == 5,
            $"轨迹完全保留：seed + 两次呈现 + 两次作答 = {h.Store.EventCountFor(word.Key)} 条事件");
    }

    // ==================================================================================
    // P1-6：定稿落库的 SchedulerDecision 必须与校准器的真实结果自洽
    // ==================================================================================

    private static void DecisionAuditMatchesCalibratorOutcome()
    {
        // (a) Applied=true 但 SolveCandidate 用默认实现（返回 null）→ Δ 没参与求解
        var noSolver = new FakeCalibratorWithoutSolver { Apply = true, Delta = 0.25, Mode = ContextMode.Active };
        var a = new Harness(null, noSolver);
        var wordA = ReviewWord(a, "audit-nocand");
        a.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        a.Coordinator.OnPresented(wordA, "p1", true);
        a.Clock.Advance(TimeSpan.FromSeconds(4));
        a.Coordinator.OnRated(wordA, "p1", StudyRating.Known, 0, 1, null);
        Program.Check(a.Coordinator.CommitWord(wordA, StudyMode.Review) is not null, "定稿成功（无候选间隔）");
        var noCandidate = a.Store.LastCommit!.Decision;
        Program.Check(noCandidate.ContextDelta is null && noCandidate.ContextCandidateIntervalDays is null,
            "Applied=true 但拿不到候选间隔 → Δ 与 candidate 都不落库（不能记成「应用了 Δ 却没变」）");
        Program.CheckClose(noCandidate.FinalIntervalDays, noCandidate.BaselineIntervalDays, 1e-12,
            "此时 final 就是纯基线（与「未应用」的记录一致）");

        // (b) Applied=false → 两者都为 null
        var off = new FakeCalibrator { Apply = false, Delta = 0.25, Candidate = 12 };
        var b = new Harness(null, off);
        var wordB = ReviewWord(b, "audit-off");
        b.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        b.Coordinator.OnPresented(wordB, "p1", true);
        b.Clock.Advance(TimeSpan.FromSeconds(4));
        b.Coordinator.OnRated(wordB, "p1", StudyRating.Known, 0, 1, null);
        Program.Check(b.Coordinator.CommitWord(wordB, StudyMode.Review) is not null, "定稿成功（校准未生效）");
        var notApplied = b.Store.LastCommit!.Decision;
        Program.Check(notApplied.ContextDelta is null && notApplied.ContextCandidateIntervalDays is null,
            "Applied=false → Δ 与 candidate 都为 null");

        // (c) 真的生效 → 三者自洽：candidate 有值、Δ 有值、final = clamp(candidate)
        var on = new FakeCalibrator { Apply = true, Delta = 0.25, Candidate = 12 };
        var c = new Harness(null, on);
        var wordC = ReviewWord(c, "audit-on");
        c.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        c.Coordinator.OnPresented(wordC, "p1", true);
        c.Clock.Advance(TimeSpan.FromSeconds(4));
        c.Coordinator.OnRated(wordC, "p1", StudyRating.Known, 0, 1, null);
        Program.Check(c.Coordinator.CommitWord(wordC, StudyMode.Review) is not null, "定稿成功（校准生效）");
        var effective = c.Store.LastCommit!.Decision;
        Program.Check(effective.ContextDelta == 0.25 && effective.ContextCandidateIntervalDays == 12,
            "Δ 生效 → Δ 与 candidate 都落库");
        var expected = MemoryScheduler.ResolveFinalInterval(
            effective.BaselineIntervalDays, effective.ContextCandidateIntervalDays, new ContextAdjustment(true, 0.25, ContextMode.Active));
        Program.CheckClose(effective.FinalIntervalDays, expected, 1e-12,
            "落库的 final 就是 candidate 经 clamp 规则得到的结果");
    }

    // ==================================================================================
    // P1-9：写库失败不得让内存呈现计数前移
    // ==================================================================================

    private static void AppendEventFailureDoesNotAdvanceAppearanceCounters()
    {
        var h = new Harness();
        var word = WordKey.Archive("idx-fail"); // 不预置历史：期望「全历史第 1 次呈现」
        h.Coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);

        h.Store.FailNextAppendEvents = 1;
        var threw = false;
        try { h.Coordinator.OnPresented(word, "f1", true); }
        catch (IOException) { threw = true; }
        Program.Check(threw, "注入的写失败原样抛出（不吞）");
        Program.Check(h.Store.AppendedCount == 0, "失败的呈现没有落库");

        h.Coordinator.OnPresented(word, "f2", true);
        var e = h.Store.FindEvent("f2", InteractionEventKind.Presented)!;
        Program.Check(e.WordAppearanceIndex == 1,
            $"写失败后内存呈现计数没有前移（实际 WordAppearanceIndex={e.WordAppearanceIndex}）");
        Program.Check(e.IsFirstAppearanceForWord, "…仍被标记为全历史第 1 次呈现");
        Program.Check(e.SessionAppearanceIndex == 1,
            $"…session 内也仍是第 1 次呈现（实际 {e.SessionAppearanceIndex}）");
    }

    // ==================================================================================
    // 测试脚手架
    // ==================================================================================

    private sealed class Harness
    {
        public FakeMemoryStore Store { get; } = new();
        public FakeScheduler Scheduler { get; } = new();
        public FakeClock Clock { get; } = new();
        public LearningMemoryCoordinator Coordinator { get; }

        public Harness(IContextFeatureProvider? features = null, IContextCalibrator? calibrator = null)
            => Coordinator = new LearningMemoryCoordinator(Store, Scheduler, features, calibrator, () => Clock.Now);
    }

    /// <summary>造一个「已经出现过」的词（预置一条历史 Presented 事件），使 Review 语义成立。</summary>
    private static WordKey ReviewWord(Harness h, string id)
    {
        var key = WordKey.Archive(id);
        h.Store.SeedPresented(key.Key, "prior-session", h.Clock.Now.AddDays(-3));
        return key;
    }

    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan delta) => Now += delta;
    }

    /// <summary>固定、可预测的假 scheduler：不依赖 FSRS 公式，只验证协调器的编排。</summary>
    private sealed class FakeScheduler : IMemoryScheduler
    {
        public int ReviewCalls { get; private set; }

        /// <summary>
        /// 每次 <see cref="Review"/> 的入参快照（卡按值克隆）。
        /// 用来断言「第二条 canonical 走 elapsed_days == 0 的 short-term 路径」——
        /// 即本次作答与入参卡上的 LastReviewAtUtc 落在同一整天。
        /// </summary>
        public List<(FsrsCardState? Card, StudyRating Rating, DateTime ReviewedAtUtc)> Calls { get; } = [];
        public double BaselineForKnown { get; set; } = 30;
        public double BaselineForUnsure { get; set; } = 10;
        public double BaselineForForgot { get; set; } = 5;
        /// <summary>false 时模拟「不盖版本戳」的调度器，用来验证协调器的回退。</summary>
        public bool StampVersions { get; set; } = true;

        public FsrsOutcome Review(FsrsCardState? card, StudyRating rating, DateTime reviewedAtUtc)
        {
            ReviewCalls++;
            Calls.Add((card?.Clone(), rating, reviewedAtUtc));
            var baseline = rating switch
            {
                StudyRating.Known => BaselineForKnown,
                StudyRating.Unsure => BaselineForUnsure,
                _ => BaselineForForgot,
            };
            // 返回新对象，绝不就地改写调用方传进来的卡
            var next = new FsrsCardState
            {
                WordKey = card?.WordKey ?? "",
                Difficulty = (card?.Difficulty ?? 5) + (rating == StudyRating.Forgot ? 1 : 0),
                Stability = (card?.Stability ?? 0) + baseline,
                Reps = (card?.Reps ?? 0) + 1,
                Lapses = (card?.Lapses ?? 0) + (rating == StudyRating.Forgot ? 1 : 0),
                State = FsrsState.Review,
                LastReviewAtUtc = reviewedAtUtc,
                NextReviewAtUtc = reviewedAtUtc,
                LastCanonicalRating = rating,
                FsrsAlgorithmVersion = StampVersions ? "fsrs-6-test" : "",
                FsrsLibraryVersion = StampVersions ? "test-1" : "",
                FsrsParameterVersion = StampVersions ? "params-test-1" : "",
            };
            return new FsrsOutcome(next, baseline, 0.9, reviewedAtUtc.AddDays(baseline));
        }

        public double Retrievability(double stabilityDays, double elapsedDays)
            => stabilityDays <= 0 ? 1.0 : 1.0 / (1.0 + Math.Max(0, elapsedDays) / stabilityDays);

        public double IntervalForRetention(double stabilityDays, double retention)
            => stabilityDays * (1.0 / retention - 1.0);
    }

    private sealed class FakeContextFeatures : IContextFeatureProvider
    {
        public string FeatureSchemaVersion => "ctx-feat-1";
        public string ModelVersion => "test-model-1";
        public Func<ContextFeatureRequest, ContextFeatureVector>? Builder { get; set; }
        public List<ContextFeatureRequest> Requests { get; } = [];

        public ContextFeatureVector Build(ContextFeatureRequest request)
        {
            Requests.Add(request);
            return Builder?.Invoke(request) ?? new ContextFeatureVector([1.0, 2.0], ["a", "b"], [false, false]);
        }
    }

    private sealed class FakeCalibrator : IContextCalibrator
    {
        public ContextMode Mode { get; set; } = ContextMode.Active;
        public bool Apply { get; set; } = true;
        public double Delta { get; set; }
        public double? Candidate { get; set; }
        public List<(ContextFeatureVector Features, double R)> Adjusts { get; } = [];
        public double SolveStability { get; private set; } = double.NaN;
        public double SolveRetention { get; private set; } = double.NaN;

        public ContextAdjustment Adjust(ContextFeatureVector features, double fsrsRetrievability)
        {
            Adjusts.Add((features, fsrsRetrievability));
            return Apply ? new ContextAdjustment(true, Delta, Mode) : ContextAdjustment.None(Mode);
        }

        public double? SolveCandidate(ContextFeatureVector features, double stabilityDays, double desiredRetention)
        {
            SolveStability = stabilityDays;
            SolveRetention = desiredRetention;
            return Candidate;
        }
    }

    /// <summary>声明 Applied=true 但**不覆写** SolveCandidate（用默认实现返回 null）的校准器——P1-6 的触发条件。</summary>
    private sealed class FakeCalibratorWithoutSolver : IContextCalibrator
    {
        public ContextMode Mode { get; set; } = ContextMode.Active;
        public bool Apply { get; set; } = true;
        public double Delta { get; set; }

        public ContextAdjustment Adjust(ContextFeatureVector features, double fsrsRetrievability) =>
            Apply ? new ContextAdjustment(true, Delta, Mode) : ContextAdjustment.None(Mode);
    }

    /// <summary>内存假 store：实现全部端口成员，canonical_id 去重，FSRS 副作用至多施加一次。</summary>
    private sealed class FakeMemoryStore : ILearningMemoryStore
    {
        private readonly List<LearningInteractionEvent> _events = [];
        private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LearningSession> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FsrsCardState> _cards = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ContextFeatureSnapshot> _snapshots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _snapshotByPresentation = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WordSessionCommit> _commits = new(StringComparer.Ordinal);
        private readonly List<long> _appliedMutations = [];
        private readonly Dictionary<long, (string Kind, string Payload, int Attempts)> _pending = [];
        private long _nextMutationId = 1;

        public string DatabasePath => ":memory:";

        public int AppendEventCalls { get; private set; }
        public int AppendedCount { get; private set; }
        public int CommitWordSessionCalls { get; private set; }
        public int CardApplyCount { get; private set; }
        public int InvalidateCanonicalCalls { get; private set; }
        public int PreStateRestoreCount { get; private set; }
        public int LabelSnapshotsCalls { get; private set; }
        public int LoadEventsForWordCalls { get; private set; }
        public Exception? CommitException { get; set; }
        public WordSessionCommit? LastCommit { get; private set; }
        public string? LastInvalidateReason { get; private set; }
        public string? LastInvalidateCanonicalId { get; private set; }
        public DateTime LastInvalidateAtUtc { get; private set; }
        public List<WordSessionCommit> Commits { get; } = [];
        public List<SchedulerDecision> Decisions { get; } = [];
        public int EventCount => _events.Count;
        public int SnapshotCount => _snapshots.Count;
        public int AppliedCanonicalCount => _commits.Count;
        public LearningInteractionEvent? LastEvent => _events.Count > 0 ? _events[^1] : null;

        // —— 测试注入 ——

        public void SeedPresented(string wordKey, string sessionId, DateTime atUtc)
        {
            var e = new LearningInteractionEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                WordKey = wordKey,
                PresentationId = $"seed:{Guid.NewGuid():N}",
                OccurredAtUtc = atUtc,
                LearningMode = LearningMode.Review,
                Kind = InteractionEventKind.Presented,
                SessionAppearanceIndex = 1,
                WordAppearanceIndex = 1,
                IsFirstAppearanceForWord = true,
                IsRecall = true,
            };
            _eventIds.Add(e.EventId);
            _events.Add(e);
        }

        public void SeedCard(FsrsCardState card) => _cards[card.WordKey] = card.Clone();

        /// <summary>该词仍然有效（未失效）的 canonical 条数——P1-4 的观测点。</summary>
        public int ValidCanonicalCount(string wordKey) =>
            _commits.Values.Count(c => c.Canonical.WordKey == wordKey && !c.Canonical.Invalidated);

        /// <summary>该词的 canonical 定稿次数（含已失效的）。</summary>
        public int CanonicalRowCount(string wordKey) =>
            _commits.Values.Count(c => c.Canonical.WordKey == wordKey);

        /// <summary>该词的事件条数。</summary>
        public int EventCountFor(string wordKey) => _events.Count(e => e.WordKey == wordKey);

        public WordSessionCommit? CommitFor(string wordKey) =>
            Commits.Find(c => c.Canonical.WordKey == wordKey);

        public ContextFeatureSnapshot? SnapshotFor(string presentationId) =>
            _snapshotByPresentation.TryGetValue(presentationId, out var id) ? _snapshots[id] : null;

        public LearningInteractionEvent? FindEvent(string presentationId, InteractionEventKind kind) =>
            _events.Find(e => e.PresentationId == presentationId && e.Kind == kind);

        // —— ILearningMemoryStore ——

        /// <summary>测试注入：接下来 N 次 AppendEvent 抛 IOException（模拟写库失败）。</summary>
        public int FailNextAppendEvents { get; set; }

        public void AppendEvent(LearningInteractionEvent e)
        {
            AppendEventCalls++;
            if (FailNextAppendEvents > 0)
            {
                FailNextAppendEvents--;
                throw new IOException("注入的 AppendEvent 写失败");
            }
            if (!_eventIds.Add(e.EventId)) return; // 重复 EventId 必须被忽略（幂等）
            _events.Add(e);
            AppendedCount++;
        }

        public IReadOnlyList<LearningInteractionEvent> LoadEvents(string sessionId) =>
            _events.Where(e => e.SessionId == sessionId).ToList();

        public IReadOnlyList<LearningInteractionEvent> LoadEventsForWord(string wordKey)
        {
            LoadEventsForWordCalls++;
            return _events.Where(e => e.WordKey == wordKey).ToList();
        }

        public void UpsertSession(LearningSession session) => _sessions[session.SessionId] = session;

        public LearningSession? GetSession(string sessionId) =>
            _sessions.TryGetValue(sessionId, out var s) ? s : null;

        public WordSessionCommitResult CommitWordSession(WordSessionCommit commit)
        {
            CommitWordSessionCalls++;
            if (CommitException is not null) throw CommitException;
            LastCommit = commit;
            Commits.Add(commit);

            if (_commits.ContainsKey(commit.Canonical.CanonicalId))
                return new WordSessionCommitResult(false, true, commit.Canonical.CanonicalId);

            _commits[commit.Canonical.CanonicalId] = commit;
            _cards[commit.CardAfter.WordKey] = commit.CardAfter.Clone(); // FSRS 副作用只施加一次
            CardApplyCount++;
            _sessions[commit.Session.SessionId] = commit.Session;
            Decisions.Add(commit.Decision);
            if (commit.SnapshotLabels.Count > 0) LabelSnapshots(commit.SnapshotLabels, commit.Canonical.CompletedAtUtc);
            if (commit.PendingEvents.Count > 0)
                foreach (var e in commit.PendingEvents) AppendEvent(e);
            if (commit.OutboxMutations.Count > 0) EnqueueMutations(commit.OutboxMutations);
            return new WordSessionCommitResult(true, false, commit.Canonical.CanonicalId);
        }

        public bool InvalidateCanonical(string canonicalId, DateTime atUtc, string reason)
        {
            InvalidateCanonicalCalls++;
            LastInvalidateCanonicalId = canonicalId;
            LastInvalidateAtUtc = atUtc;
            LastInvalidateReason = reason;
            if (!_commits.TryGetValue(canonicalId, out var commit)) return false;
            commit.Canonical.Invalidated = true;
            PreStateRestoreCount++;
            var pre = commit.CardPreState;
            if (pre.Existed)
            {
                _cards[commit.Canonical.WordKey] = new FsrsCardState
                {
                    WordKey = commit.Canonical.WordKey,
                    Difficulty = pre.Difficulty,
                    Stability = pre.Stability,
                    Reps = pre.Reps,
                    Lapses = pre.Lapses,
                    State = pre.State,
                    LastReviewAtUtc = pre.LastReviewAtUtc,
                    NextReviewAtUtc = pre.NextReviewAtUtc,
                    LastCanonicalRating = pre.LastCanonicalRating,
                };
            }
            else
            {
                _cards.Remove(commit.Canonical.WordKey);
            }
            return true;
        }

        public FsrsCardState? GetCard(string wordKey) =>
            _cards.TryGetValue(wordKey, out var c) ? c.Clone() : null;

        public bool HasCard(string wordKey) => _cards.ContainsKey(wordKey);

        public IReadOnlyList<DueCard> QueryDue(DateTime nowUtc, int limit) =>
            _cards.Values
                .Where(c => c.NextReviewAtUtc is not null && c.NextReviewAtUtc.Value <= nowUtc)
                .OrderBy(c => c.NextReviewAtUtc!.Value)
                .Take(limit)
                .Select(c => new DueCard(c.WordKey, c.NextReviewAtUtc!.Value, c.Difficulty, c.Stability))
                .ToList();

        public int CountDue(DateTime nowUtc) => QueryDue(nowUtc, int.MaxValue).Count;

        public void SaveContextSnapshot(ContextFeatureSnapshot snapshot)
        {
            _snapshots[snapshot.SnapshotId] = snapshot;
            if (!string.IsNullOrEmpty(snapshot.PresentationId))
                _snapshotByPresentation[snapshot.PresentationId] = snapshot.SnapshotId;
        }

        public ContextFeatureSnapshot? GetContextSnapshot(string snapshotId) =>
            _snapshots.TryGetValue(snapshotId, out var s) ? s : null;

        public ContextFeatureSnapshot? GetSnapshotByPresentation(string presentationId) =>
            _snapshotByPresentation.TryGetValue(presentationId, out var id) ? _snapshots[id] : null;

        public void LabelSnapshots(IReadOnlyList<ContextLabel> labels, DateTime labeledAtUtc)
        {
            LabelSnapshotsCalls++;
            foreach (var label in labels)
            {
                if (!_snapshots.TryGetValue(label.SnapshotId, out var s)) continue;
                s.Label = label.Rating;
                s.ConfidentRecall = label.ConfidentRecall;
                s.LabeledAtUtc = labeledAtUtc;
            }
        }

        public IReadOnlyList<LabeledContextSample> LoadLabeledSamples(DateTime? cutoffUtc = null) =>
            _snapshots.Values
                .Where(s => s.Label is not null && (cutoffUtc is null || s.CapturedAtUtc >= cutoffUtc))
                .Select(s => new LabeledContextSample(s.SnapshotId, s.WordKey, s.CapturedAtUtc,
                    System.Text.Json.JsonSerializer.Deserialize<double[]>(s.FeaturesJson) ?? [],
                    s.MissingFlagsJson, (int)s.Label!.Value, s.ConfidentRecall ?? false))
                .ToList();

        private PersonalizationModelState? _model;
        public PersonalizationModelState? GetPersonalizationModel() => _model;
        public void SavePersonalizationModel(PersonalizationModelState state) => _model = state;

        public void SaveSchedulerDecision(SchedulerDecision decision) => Decisions.Add(decision);

        public IReadOnlyList<SchedulerDecision> LoadRecentDecisions(string wordKey, int limit) =>
            Decisions.Where(d => d.WordKey == wordKey).Reverse().Take(limit).ToList();

        public void EnqueueMutations(IReadOnlyList<PendingMutation> mutations)
        {
            foreach (var m in mutations) _pending[_nextMutationId++] = (m.Kind, m.PayloadJson, 0);
        }

        public IReadOnlyList<PendingMutationRow> LoadPendingMutations() =>
            _pending.OrderBy(p => p.Key)
                .Select(p => new PendingMutationRow(p.Key, p.Value.Kind, p.Value.Payload, p.Value.Attempts))
                .ToList();

        public void MarkMutationApplied(long id, DateTime atUtc)
        {
            _pending.Remove(id);
            _appliedMutations.Add(id);
        }

        public void RecordMutationFailure(long id, string error)
        {
            if (_pending.TryGetValue(id, out var row)) _pending[id] = (row.Kind, row.Payload, row.Attempts + 1);
        }
    }
}
