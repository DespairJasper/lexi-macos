namespace Lexi;

/// <summary>
/// 轨迹归约：把 append-only 的原始交互事件投影成 presentation → summary → canonical 三层。
/// 纯函数、无 IO、不读时钟（时间一律来自事件或参数），因此可用固定输入完整回归。
/// </summary>
/// <remarks>
/// <para>
/// 三层口径（自上而下逐层收窄）：
/// <list type="bullet">
/// <item><b>presentation</b>：一次卡片呈现，含该次呈现上的全部作答与修正；</item>
/// <item><b>summary</b>：一个 word × session 的计数摘要，可由 raw history 重建；</item>
/// <item><b>canonical</b>：一个 word × session 的唯一长期信号，只被 FSRS 消费一次。</item>
/// </list>
/// </para>
/// <para>
/// <b>核心口径</b>：<c>IsRecall == false</c> 的 Learn 卡（答案可见的“只看答案”）不构成 retrieval。
/// 它计入 <see cref="WordSessionSummary.TotalPresentations"/>，但不参与 canonical 取值、
/// 不计入 Known/Fuzzy/Forgotten 分布、不参与连击与重置统计。只有真实回忆才是记忆证据。
/// </para>
/// </remarks>
public static class TrajectoryReducer
{
    /// <summary>
    /// 把原始事件按 PresentationId 分组投影成 presentation。组间按 PresentedAtUtc 升序（同一时刻保持事件流原序），
    /// 组内按 (OccurredAtUtc, 事件流原序) 稳定排序。
    /// </summary>
    /// <remarks>
    /// 无 PresentationId 的事件无法归组，直接忽略（正常写入路径不会产生）；不抛异常，保证 UI 投影永远可用。
    /// </remarks>
    public static List<PresentationAttempt> ProjectPresentations(IEnumerable<LearningInteractionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var groups = new Dictionary<string, List<LearningInteractionEvent>>(StringComparer.Ordinal);
        var orderOfFirstSeen = new List<string>();
        foreach (var e in events)
        {
            if (e is null || string.IsNullOrEmpty(e.PresentationId)) continue;
            // 复合分组键：一个 presentation 只属于「某个 session 里的某个词」。
            // 只按 presentationId 分组时，同一 id 被复用到两个词/两个 session 上会静默串词
            // （把 A 的作答算成 B 的），因此这里显式带上 sessionId 与 wordKey。
            var groupKey = e.SessionId + "\u0000" + e.WordKey + "\u0000" + e.PresentationId;
            if (!groups.TryGetValue(groupKey, out var bucket))
            {
                bucket = [];
                groups[groupKey] = bucket;
                orderOfFirstSeen.Add(groupKey);
            }
            bucket.Add(e);
        }

        var seenIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderOfFirstSeen.Count; i++) seenIndex[orderOfFirstSeen[i]] = i;

        var projected = new List<(string GroupKey, PresentationAttempt Attempt)>(groups.Count);
        foreach (var id in orderOfFirstSeen)
        {
            var group = groups[id];
            // OrderBy 是稳定排序，List.Sort 不是；组内要求「同 (OccurredAtUtc) 时保持事件流原序」，
            // 因此必须用 OrderBy。
            var ordered = group.OrderBy(static e => e.OccurredAtUtc).ToList();
            projected.Add((id, ProjectGroup(ordered[0].PresentationId, ordered)));
        }
        // 组间按首现时刻排序；同一时刻用首现顺序兜底，保证结果稳定可复现。
        projected.Sort((a, b) =>
        {
            var byTime = a.Attempt.PresentedAtUtc.CompareTo(b.Attempt.PresentedAtUtc);
            return byTime != 0 ? byTime : seenIndex[a.GroupKey].CompareTo(seenIndex[b.GroupKey]);
        });
        return projected.Select(static p => p.Attempt).ToList();
    }

    private static PresentationAttempt ProjectGroup(string presentationId, List<LearningInteractionEvent> group)
    {
        var anchor = group[0];
        var presented = group.FirstOrDefault(static e => e.Kind == InteractionEventKind.Presented) ?? anchor;
        var firstRated = group.FirstOrDefault(static e => e.Kind == InteractionEventKind.Rated);

        var attempt = new PresentationAttempt
        {
            PresentationId = presentationId,
            WordKey = anchor.WordKey,
            SessionId = anchor.SessionId,
            PresentedAtUtc = presented.OccurredAtUtc,
            IsRecall = presented.IsRecall,
            SessionAppearanceIndex = presented.SessionAppearanceIndex,
            IsFirstAppearanceForWord = presented.IsFirstAppearanceForWord,
            LatencyMs = firstRated?.ResponseLatencyMs,
        };

        // 仍然生效的判断栈：(EventId, Rating)。Rated/Revised 入栈，Undone 出栈；栈顶即“最终有效判断”。
        var standing = new List<(string EventId, StudyRating Rating)>();
        var revisionCount = 0;
        foreach (var e in group)
        {
            switch (e.Kind)
            {
                case InteractionEventKind.Rated:
                    if (e.Response is not { } rated) continue;
                    attempt.InitialResponse ??= rated;
                    standing.Add((e.EventId, rated));
                    break;
                case InteractionEventKind.Revised:
                    revisionCount++;
                    if (e.Response is { } revised) standing.Add((e.EventId, revised));
                    break;
                case InteractionEventKind.Undone:
                    Undo(standing, e.SupersedesEventId);
                    break;
            }
        }

        attempt.RevisionCount = revisionCount;
        attempt.HadResponseRevision = revisionCount > 0;
        // RevisionPath 记录仍然生效的判断序列：正常情况即 [初判, ...仍在生效的改判]；
        // 被 Undone 撤销的判断同时移出，保证末项与 FinalValidatedResponse 一致。
        attempt.RevisionPath = standing.Select(static s => s.Rating).ToList();
        attempt.FinalValidatedResponse = standing.Count > 0 ? standing[^1].Rating : null;
        return attempt;
    }

    /// <summary>
    /// 撤销一条判断：优先按 SupersedesEventId 精确定位（可能撤销的不是最后一条）；标注缺失或定位不到时，
    /// 退回“撤销最后一条仍然生效的判断”。栈空则忽略。
    /// </summary>
    private static void Undo(List<(string EventId, StudyRating Rating)> standing, string? supersedesEventId)
    {
        if (standing.Count == 0) return;
        if (!string.IsNullOrEmpty(supersedesEventId))
        {
            for (var i = standing.Count - 1; i >= 0; i--)
            {
                if (string.Equals(standing[i].EventId, supersedesEventId, StringComparison.Ordinal))
                {
                    standing.RemoveAt(i);
                    return;
                }
            }
        }
        standing.RemoveAt(standing.Count - 1);
    }

    /// <summary>
    /// 由 presentation 列表构建 word × session 摘要。
    /// </summary>
    /// <param name="completedAtUtc">
    /// 定稿时刻。为 null 时 <see cref="WordSessionSummary.PresentationsToMastery"/> 记 0、
    /// <see cref="WordSessionSummary.TimeToMasteryMs"/> 记 null（供尚未定稿的实时查询使用）。
    /// </param>
    public static WordSessionSummary BuildSummary(string wordKey, string sessionId,
        IReadOnlyList<PresentationAttempt> presentations, DateTime? completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(presentations);
        var summary = new WordSessionSummary
        {
            WordKey = wordKey,
            SessionId = sessionId,
            CompletedAtUtc = completedAtUtc,
        };

        var ordered = presentations.Where(static p => p is not null).OrderBy(static p => p.PresentedAtUtc).ToList();
        if (ordered.Count == 0) return summary;

        // 含 Learn 卡：最早的呈现时刻是“这个词本轮第一次出现在眼前”。
        summary.FirstPresentedAtUtc = ordered[0].PresentedAtUtc;
        summary.TotalPresentations = ordered.Count;

        var streak = 0;
        var lastRetrievalIndex = -1;
        var firstRetrievalSeen = false;
        for (var index = 0; index < ordered.Count; index++)
        {
            var p = ordered[index];
            // 修正计数不区分 Learn/Recall（Learn 卡没有作答，实际恒为 0）。
            summary.ResponseRevisionCount += Math.Max(0, p.RevisionCount);
            if (p.RevisionCount > 0) summary.HadResponseRevision = true;

            if (!p.IsRecall) continue;
            lastRetrievalIndex = index;
            var final = p.FinalValidatedResponse;
            if (!firstRetrievalSeen)
            {
                firstRetrievalSeen = true;
                // 第一个**真实 retrieval** 的初判/终判（第一张 Learn 卡不算）。
                summary.FirstInitialResponse = p.InitialResponse;
                summary.FirstValidatedResponse = final;
            }
            if (final is not { } rating) continue;

            switch (rating)
            {
                case StudyRating.Known:
                    summary.FinalKnownCount++;
                    streak++;
                    break;
                case StudyRating.Unsure:
                    summary.FinalFuzzyCount++;
                    summary.HadFuzzy = true;
                    streak = Math.Max(0, streak - 1);
                    break;
                default:
                    summary.FinalForgottenCount++;
                    summary.HadForgotten = true;
                    summary.ResetCount++;
                    streak = 0;
                    break;
            }
            if (streak > summary.MaxKnownStreak) summary.MaxKnownStreak = streak;

            // 修正方向：一律按“初判 → 终判”，只统计真实 retrieval，反转（Fuzzy→Known 等）不计数。
            if (p.InitialResponse is not { } initial) continue;
            if (initial == StudyRating.Known && rating == StudyRating.Unsure) summary.KnownToFuzzy++;
            else if (initial == StudyRating.Known && rating == StudyRating.Forgot) summary.KnownToForgotten++;
            else if (initial == StudyRating.Unsure && rating == StudyRating.Forgot) summary.FuzzyToForgotten++;
        }

        if (completedAtUtc is { } completed)
        {
            if (lastRetrievalIndex >= 0) summary.PresentationsToMastery = lastRetrievalIndex + 1;
            summary.TimeToMasteryMs = (long)(completed - summary.FirstPresentedAtUtc).TotalMilliseconds;
        }
        return summary;
    }

    /// <summary>
    /// canonical 解析：一个 word × session 只产出一个 <see cref="CanonicalReview"/>，
    /// 幂等键由 <see cref="CanonicalReview.BuildId"/> 决定。无有效记忆证据时返回 null。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Review</b>：取按呈现顺序**第一个真实 retrieval** 的最终有效判断，记 <see cref="CanonicalOrigin.FirstRetrieval"/>。
    /// 首答（哪怕 Hard/Again）一旦定下就不再被后续强化成功覆盖——这正是“第一次是否真的记住了”的口径；
    /// 被完全撤销（<see cref="PresentationAttempt.FinalValidatedResponse"/> 为 null）的 presentation 视为无效，继续往后找。</item>
    /// <item><b>FirstLearn</b>：本轮完成后按 <see cref="AggregationPolicy.Aggregate"/> 归纳，记
    /// <see cref="CanonicalOrigin.FirstLearnAggregate"/>，不指向单个 presentation。</item>
    /// </list>
    /// </remarks>
    public static CanonicalReview? ResolveCanonical(string wordKey, string sessionId,
        LearningMode mode, IReadOnlyList<PresentationAttempt> presentations,
        WordSessionSummary summary, DateTime completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(presentations);
        ArgumentNullException.ThrowIfNull(summary);

        // 没有任何真实 retrieval（例如只有 Learn 卡「只看答案」的轨迹，或全部作答都被撤销）
        // 就不产生长期信号。否则会把用户从未回忆过的词写成「记得」并推后间隔。
        // 该门槛对两种 mode 都成立：Review 分支下面还会再找一次「第一次真实 retrieval」。
        if (!presentations.Any(static p => p is not null && p.IsRecall && p.FinalValidatedResponse is not null))
            return null;

        var canonical = new CanonicalReview
        {
            CanonicalId = CanonicalReview.BuildId(wordKey, sessionId),
            WordKey = wordKey,
            SessionId = sessionId,
            CompletedAtUtc = completedAtUtc,
            AggregationPolicyVersion = AggregationPolicy.Version,
            Revision = 1,
            Invalidated = false,
        };

        if (mode == LearningMode.Review)
        {
            var first = presentations
                .Where(static p => p is not null && p.IsRecall && p.FinalValidatedResponse is not null)
                .OrderBy(static p => p.PresentedAtUtc)
                .FirstOrDefault();
            if (first is null) return null;
            canonical.Origin = CanonicalOrigin.FirstRetrieval;
            canonical.Rating = first.FinalValidatedResponse!.Value;
            canonical.ReviewedAtUtc = first.PresentedAtUtc;
            canonical.SourcePresentationId = first.PresentationId;
            return canonical;
        }

        canonical.Origin = CanonicalOrigin.FirstLearnAggregate;
        canonical.Rating = AggregationPolicy.Aggregate(summary);
        canonical.SourcePresentationId = null;
        canonical.ReviewedAtUtc = summary.FirstPresentedAtUtc;
        return canonical;
    }

    /// <summary>由事件流一步得到摘要（供 UI 层实时展示，尚未定稿时 <paramref name="completedAtUtc"/> 传 null）。</summary>
    public static WordSessionSummary BuildSummaryFromEvents(string wordKey, string sessionId,
        IEnumerable<LearningInteractionEvent> events, DateTime? completedAtUtc)
        => BuildSummary(wordKey, sessionId, ProjectPresentations(events), completedAtUtc);
}
