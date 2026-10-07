namespace Lexi;

/// <summary>
/// V1 特征提供者：把 <see cref="ContextFeatureRequest"/>（呈现该卡时**已可知**的信息）
/// 与其**作答前历史**（store 查询一律带 <c>&lt; CapturedAtUtc</c> 严格上界）编成具名特征向量。
///
/// <para><b>泄漏防线（三层，缺一不可）</b></para>
/// <list type="number">
/// <item><b>时间上界在 SQL 里。</b>本类不拼 SQL，只调用
/// <see cref="ILearningMemoryStore.LoadCanonicalHistory"/> /
/// <see cref="ILearningMemoryStore.LoadCompletedSummaries"/> /
/// <see cref="ILearningMemoryStore.LoadRecentResponseLatencies"/>，三条查询都以
/// <c>CapturedAtUtc</c> 为**严格小于**上界。「本轮」的历史只能取
/// <c>word_session_summaries.completed_at_utc IS NOT NULL AND &lt; CapturedAtUtc</c> 的最后一条，
/// 当前轮（尚未完成、或完成于快照之后）结构上不可能被选中。</item>
/// <item><b>作答结果不进特征。</b>本类只读 <see cref="ContextFeatureRequest"/>，其中不含任何
/// <c>Rated</c>/<c>Revised</c>/<c>Undone</c> 载荷；本次作答的 rating 从未出现在入参里。</item>
/// <item><b>查询失败按缺失。</b>任何一条历史查询抛异常都只会把**相关特征**标成
/// <see cref="ContextFeatureVector.Missing"/>（值 0），既不会抛断作答路径，也不会伪造中性历史。</item>
/// </list>
///
/// <para><b>V1 清单对照</b>：每条特征在 <see cref="FeatureNames"/> 的声明处标注它来自用户需求
/// §7 V1 清单的哪一项；无法可靠取得的项在 <c>Missing</c> 里显式标记（见各 <c>Set/Miss</c> 分支的注释）。</para>
/// </summary>
public sealed class ContextFeatureProvider : IContextFeatureProvider
{
    /// <summary>
    /// 规范特征顺序。**顺序即 schema**：一旦改动必须同时升
    /// <see cref="SchedulingConfig.ContextFeatureSchemaVersion"/>，否则旧快照会被按错位的列训练。
    /// <para>名字用稳定英文标识；写入一律按名字（<see cref="Builder.Set"/> / <see cref="Builder.Miss"/>），
    /// 不出现裸下标，因此顺序调整不会静默错位。</para>
    /// </summary>
    public static readonly string[] FeatureNames =
    [
        // —— 来自 request 的「作答前已知」信息（用户需求 §7 V1：sessionPosition / reviewedCount；Mode）——
        "mode_is_first_learn",                  // §7 V1 无独立条目：request.Mode 直接给出（首发学习 vs 复习）
        "is_first_appearance_for_session",      // §7 V1 无独立条目：request.IsFirstAppearanceForSession
        "session_position",                     // §7 V1「sessionPosition」
        "session_reviewed_count",               // §7 V1「reviewedCount」
        // —— §7 V1「学习时长」——
        "session_elapsed_minutes",
        // —— §7 V1「reviewsToday / 近 7 天 canonical 数」——
        "reviews_today",
        "canonical_count_7d",
        // —— §7 V1「7/30 天 recall/fuzzy/forgotten rates」（用户级窗口活动质量；口径见 Build）——
        "global_recall_rate_7d",
        "global_fuzzy_rate_7d",
        "global_forgotten_rate_7d",
        "global_recall_rate_30d",
        "global_fuzzy_rate_30d",
        "global_forgotten_rate_30d",
        // —— §7 V1「rollingMedianResponseLatency」——
        "rolling_median_latency_ms",
        // —— §7 V1「词已有 reviews/lapses/学习天数」——
        "word_reps",
        "word_lapses",
        "word_canonical_count",
        "word_learning_days",
        // —— §7 V1「当前 D/S/R」——
        "card_difficulty",
        "card_stability",
        "card_retrievability",
        // —— §7 V1「上一已完成轮的 presentations / Known / Fuzzy / Forgotten / resets / revisions(含修正方向) /
        //            maxKnownStreak / presentationsToMastery / timeToMastery / hadFuzzy / hadForgotten / hadRevision」——
        "prev_round_presentations",
        "prev_round_known",
        "prev_round_fuzzy",
        "prev_round_forgotten",
        "prev_round_resets",
        "prev_round_revisions",
        "prev_round_known_to_fuzzy",
        "prev_round_known_to_forgotten",
        "prev_round_fuzzy_to_forgotten",
        "prev_round_max_known_streak",
        "prev_round_presentations_to_mastery",
        "prev_round_time_to_mastery_minutes",
        "prev_round_had_fuzzy",
        "prev_round_had_forgotten",
        "prev_round_had_revision",
        // —— §7 V1「该词历史平均 mastery burden、fuzzy/forgotten/revision rates」——
        "word_completed_rounds",
        "word_fuzzy_rate_hist",
        "word_forgotten_rate_hist",
        "word_revision_rate_hist",
        "word_avg_mastery_burden",
    ];

    /// <summary>名 → 下标。构造期校重名，避免「两个名字指同一列」被静默接受。</summary>
    private static readonly Dictionary<string, int> FeatureIndex = BuildIndex();

    /// <summary>特征维数（= <see cref="FeatureNames"/>.Length）。</summary>
    public static int FeatureCount => FeatureNames.Length;

    private readonly ILearningMemoryStore _store;
    private readonly IMemoryScheduler _scheduler;

    public ContextFeatureProvider(ILearningMemoryStore store, IMemoryScheduler scheduler)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    /// <inheritdoc />
    public string FeatureSchemaVersion => SchedulingConfig.ContextFeatureSchemaVersion;

    /// <summary>
    /// 特征/模型族版本。它是**族标识**，不是某一轮训练产出的版本——
    /// 训练产出的版本在 <see cref="PersonalizationModelState.ModelVersion"/>。
    /// </summary>
    public string ModelVersion => SchedulingConfig.ContextModelVersionPrefix;

    // ==================================================================================
    // 构建
    // ==================================================================================

    /// <inheritdoc />
    public ContextFeatureVector Build(ContextFeatureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var captureUtc = NormalizeUtc(request.CapturedAtUtc);
        var wordKey = request.WordKey ?? "";
        var b = new Builder();

        // —— ① 直接来自 request（全部在作答前已知）——
        b.Set("mode_is_first_learn", request.Mode == LearningMode.FirstLearn ? 1.0 : 0.0);
        b.Set("is_first_appearance_for_session", request.IsFirstAppearanceForSession ? 1.0 : 0.0);
        b.Set("session_position", request.SessionPosition);
        b.Set("session_reviewed_count", request.SessionReviewedCount);

        // —— ② 学习时长：会话起点 → 快照时刻。会话记录缺失（或起点晚于快照）→ 缺失标记。——
        var session = string.IsNullOrWhiteSpace(request.SessionId) ? null : Try(() => _store.GetSession(request.SessionId));
        if (session is null)
        {
            b.Miss("session_elapsed_minutes");
        }
        else
        {
            var minutes = (captureUtc - NormalizeUtc(session.StartedAtUtc)).TotalMinutes;
            if (MemoryScheduler.IsFinite(minutes) && minutes >= 0) b.Set("session_elapsed_minutes", minutes);
            else b.Miss("session_elapsed_minutes");
        }

        // —— ③ 全局窗口活动：一次取回近 30 天，7 天与「本日」在内存里切 ——
        // 30 天窗口是全局查询的下界，足以覆盖本日（本地下界距快照最多约 26 小时）与 7 天窗口。
        var windowStart = captureUtc.AddDays(-30);
        var global = Try(() => _store.LoadCanonicalHistory(windowStart, captureUtc, null)) ?? [];

        // 「近 7 天 canonical 数」/「reviewsToday」是**活动量**口径：含首次学习聚合的 canonical 也算一条。
        b.Set("canonical_count_7d", global.Count(r => r.ReviewedAtUtc >= captureUtc.AddDays(-7)));
        var (dayStartUtc, _) = LocalDayBounds(captureUtc);
        b.Set("reviews_today", global.Count(r => r.ReviewedAtUtc >= dayStartUtc && r.ReviewedAtUtc < captureUtc));

        // 7/30 天三分类率是**回忆质量**口径：只统计真实复习（origin = FirstRetrieval）。
        // 用户需求 §7.2 明确要求新词 acquisition（FirstLearnAggregate）不得冒充真实长期 retrieval。
        // 也正因为口径是 canonical（一个 word×session 一行），轮内重复呈现**不会**被加倍计权（§7.4）。
        SetRates(b, "7d", global, captureUtc.AddDays(-7), captureUtc);
        SetRates(b, "30d", global, windowStart, captureUtc);

        // —— ④ rolling 中位作答延迟（只含可靠的真实作答）——
        var latencies = Try(() => _store.LoadRecentResponseLatencies(captureUtc, SchedulingConfig.ContextLatencyWindowSamples)) ?? [];
        if (latencies.Count < SchedulingConfig.ContextLatencyMinimumSamples)
            b.Miss("rolling_median_latency_ms"); // 样本太少 → 中位数不可靠，缺失而不是猜
        else
            b.Set("rolling_median_latency_ms", Median(latencies));

        // —— ⑤ 词级历史（全部时间，仍以快照时刻为严格上界）——
        var wordHistory = Try(() => _store.LoadCanonicalHistory(null, captureUtc, wordKey)) ?? [];
        // 「词已有 reviews」= 真实复习数（不含首次学习聚合，口径同上）
        b.Set("word_canonical_count", wordHistory.Count(r => r.Origin == CanonicalOrigin.FirstRetrieval));
        // 「学习天数」= 该词有过 canonical 的不同 UTC 日数（含首次学习日：它衡量的是接触跨度，不是回忆质量）
        b.Set("word_learning_days", wordHistory.Select(r => r.ReviewedAtUtc.Date).Distinct().Count());

        // —— ⑥ 当前卡片状态 ——
        var card = request.Card;
        if (card is null)
        {
            // 无卡 = 从未形成长期记忆：reviews/lapses 确实是 0（事实，不是猜测）。
            b.Set("word_reps", 0);
            b.Set("word_lapses", 0);
            // D/S 无定义 → 缺失；R 沿用「无上次复习 → 1.0」的既有约定，
            // 与 ContextFeatureSnapshot.FsrsRetrievabilityAtCapture（协调器 CaptureSnapshot）**逐位一致**，
            // 否则训练时的基线偏移量与服务时的特征会指向两个不同的 R。
            b.Miss("card_difficulty");
            b.Miss("card_stability");
            b.Set("card_retrievability", 1.0);
        }
        else
        {
            b.Set("word_reps", card.Reps);
            b.Set("word_lapses", card.Lapses);
            if (MemoryScheduler.IsFinite(card.Difficulty)) b.Set("card_difficulty", card.Difficulty);
            else b.Miss("card_difficulty");
            if (MemoryScheduler.IsFinite(card.Stability)) b.Set("card_stability", card.Stability);
            else b.Miss("card_stability");
            b.Set("card_retrievability", CurrentRetrievability(card, captureUtc));
        }

        // —— ⑦ 上一已完成轮 + 该词历史平均负担 ——
        var summaries = Try(() => _store.LoadCompletedSummaries(wordKey, captureUtc)) ?? [];
        SetPreviousRound(b, summaries);
        SetWordHistoryAggregates(b, summaries);

        return b.Done(FeatureNames);
    }

    // ==================================================================================
    // 分段计算
    // ==================================================================================

    /// <summary>一个窗口的三分类率。窗口内**没有**合格真实复习 → 三条率全部缺失（0% 与「无数据」必须可区分）。</summary>
    private static void SetRates(Builder b, string suffix, IReadOnlyList<CanonicalHistoryRow> rows, DateTime fromUtc, DateTime toUtc)
    {
        var total = 0;
        var known = 0;
        var unsure = 0;
        var forgot = 0;
        foreach (var row in rows)
        {
            if (row.Origin != CanonicalOrigin.FirstRetrieval) continue;
            if (row.ReviewedAtUtc < fromUtc || row.ReviewedAtUtc >= toUtc) continue;
            total++;
            switch (row.Rating)
            {
                case StudyRating.Known: known++; break;
                case StudyRating.Unsure: unsure++; break;
                default: forgot++; break;
            }
        }

        if (total == 0)
        {
            b.Miss("global_recall_rate_" + suffix);
            b.Miss("global_fuzzy_rate_" + suffix);
            b.Miss("global_forgotten_rate_" + suffix);
            return;
        }

        b.Set("global_recall_rate_" + suffix, known / (double)total);
        b.Set("global_fuzzy_rate_" + suffix, unsure / (double)total);
        b.Set("global_forgotten_rate_" + suffix, forgot / (double)total);
    }

    /// <summary>
    /// 上一已完成轮（<paramref name="summaries"/> 里最后一条，已由 SQL 保证
    /// <c>completed_at_utc IS NOT NULL AND &lt; CapturedAtUtc</c>）。
    /// 没有任何已完成轮 → 整组缺失：轮内计数为 0 与「还没有过上一轮」是两件事。
    /// </summary>
    private static void SetPreviousRound(Builder b, IReadOnlyList<WordSessionSummary> summaries)
    {
        string[] prevNames =
        [
            "prev_round_presentations", "prev_round_known", "prev_round_fuzzy", "prev_round_forgotten",
            "prev_round_resets", "prev_round_revisions", "prev_round_known_to_fuzzy",
            "prev_round_known_to_forgotten", "prev_round_fuzzy_to_forgotten", "prev_round_max_known_streak",
            "prev_round_presentations_to_mastery", "prev_round_time_to_mastery_minutes",
            "prev_round_had_fuzzy", "prev_round_had_forgotten", "prev_round_had_revision",
        ];

        if (summaries.Count == 0)
        {
            foreach (var name in prevNames) b.Miss(name);
            return;
        }

        var prev = summaries[^1];
        b.Set("prev_round_presentations", prev.TotalPresentations);
        b.Set("prev_round_known", prev.FinalKnownCount);
        b.Set("prev_round_fuzzy", prev.FinalFuzzyCount);
        b.Set("prev_round_forgotten", prev.FinalForgottenCount);
        b.Set("prev_round_resets", prev.ResetCount);
        b.Set("prev_round_revisions", prev.ResponseRevisionCount);
        b.Set("prev_round_known_to_fuzzy", prev.KnownToFuzzy);
        b.Set("prev_round_known_to_forgotten", prev.KnownToForgotten);
        b.Set("prev_round_fuzzy_to_forgotten", prev.FuzzyToForgotten);
        b.Set("prev_round_max_known_streak", prev.MaxKnownStreak);
        b.Set("prev_round_presentations_to_mastery", prev.PresentationsToMastery);
        // time_to_mastery 在「本轮没有达成过精通」时为 null —— 那是**没有观测到**，不是 0 秒。
        if (prev.TimeToMasteryMs is { } ms && MemoryScheduler.IsFinite(ms) && ms >= 0)
            b.Set("prev_round_time_to_mastery_minutes", ms / 60000.0);
        else
            b.Miss("prev_round_time_to_mastery_minutes");
        b.Set("prev_round_had_fuzzy", prev.HadFuzzy ? 1.0 : 0.0);
        b.Set("prev_round_had_forgotten", prev.HadForgotten ? 1.0 : 0.0);
        b.Set("prev_round_had_revision", prev.HadResponseRevision ? 1.0 : 0.0);
    }

    /// <summary>
    /// 该词历史平均 mastery burden 与 fuzzy/forgotten/revision 率。
    /// 分母是所有已完成轮的 <c>TotalPresentations</c> 之和——因此这些率是**每个呈现**的率，
    /// 轮内重复呈现按当时真实发生的次数计入（它们本来就是被观测到的呈现，不是窗口统计的重复计权）。
    /// </summary>
    private static void SetWordHistoryAggregates(Builder b, IReadOnlyList<WordSessionSummary> summaries)
    {
        b.Set("word_completed_rounds", summaries.Count);
        if (summaries.Count == 0)
        {
            b.Miss("word_fuzzy_rate_hist");
            b.Miss("word_forgotten_rate_hist");
            b.Miss("word_revision_rate_hist");
            b.Miss("word_avg_mastery_burden");
            return;
        }

        long presentations = 0, fuzzy = 0, forgotten = 0, revisions = 0;
        double masterySum = 0;
        var masteryRounds = 0;
        foreach (var s in summaries)
        {
            presentations += s.TotalPresentations;
            fuzzy += s.FinalFuzzyCount;
            forgotten += s.FinalForgottenCount;
            revisions += s.ResponseRevisionCount;
            if (s.PresentationsToMastery > 0)
            {
                masterySum += s.PresentationsToMastery;
                masteryRounds++;
            }
        }

        if (presentations > 0)
        {
            b.Set("word_fuzzy_rate_hist", fuzzy / (double)presentations);
            b.Set("word_forgotten_rate_hist", forgotten / (double)presentations);
            b.Set("word_revision_rate_hist", revisions / (double)presentations);
        }
        else
        {
            b.Miss("word_fuzzy_rate_hist");
            b.Miss("word_forgotten_rate_hist");
            b.Miss("word_revision_rate_hist");
        }

        if (masteryRounds > 0) b.Set("word_avg_mastery_burden", masterySum / masteryRounds);
        else b.Miss("word_avg_mastery_burden"); // 从未有过「达到精通」的轮 → 无观测，不是 0
    }

    // ==================================================================================
    // 工具
    // ==================================================================================

    /// <summary>
    /// 当前可回忆性：与协调器 <c>CaptureSnapshot</c> 完全同口径（整天截断、无上次复习则 1.0）。
    /// 用注入的 <see cref="IMemoryScheduler"/> 而不是硬编码曲线，保证将来 FSRS 个人参数生效时这里跟随。
    /// </summary>
    private double CurrentRetrievability(FsrsCardState card, DateTime captureUtc)
    {
        if (card.LastReviewAtUtc is not { } last) return 1.0;
        if (!MemoryScheduler.IsFinite(card.Stability) || card.Stability <= 0) return 1.0;
        var elapsedDays = Fsrs6Model.ElapsedWholeDays(NormalizeUtc(last), captureUtc);
        var r = _scheduler.Retrievability(card.Stability, elapsedDays);
        return MemoryScheduler.IsFinite(r) ? Math.Clamp(r, 0.0, 1.0) : 1.0;
    }

    /// <summary>作答所在**本地日历日**的左端点（UTC）。时钟不被读取——一切以 <paramref name="captureUtc"/> 为锚。</summary>
    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBounds(DateTime captureUtc)
    {
        var localDate = captureUtc.ToLocalTime().Date;
        return (
            DateTime.SpecifyKind(localDate, DateTimeKind.Local).ToUniversalTime(),
            DateTime.SpecifyKind(localDate.AddDays(1), DateTimeKind.Local).ToUniversalTime());
    }

    private static double Median(IReadOnlyList<int> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>历史查询失败 → 返回 null，由调用方把相关特征按缺失标记（作答路径不得因此中断）。</summary>
    private static T? Try<T>(Func<T?> query) where T : class
    {
        try { return query(); }
        catch (Exception) { return null; }
    }

    private static Dictionary<string, int> BuildIndex()
    {
        var map = new Dictionary<string, int>(FeatureNames.Length, StringComparer.Ordinal);
        for (var i = 0; i < FeatureNames.Length; i++)
        {
            if (!map.TryAdd(FeatureNames[i], i))
                throw new InvalidOperationException($"Context 特征名重复：「{FeatureNames[i]}」。");
        }
        return map;
    }

    /// <summary>UTC 归一：Utc 原样、Local 换算、Unspecified 按 UTC 解释（与项目其余层同一约定）。</summary>
    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// 写值器：按名字写入，并在 <see cref="Done"/> 时断言**每一个**特征都被显式写过。
    /// 这条断言是「不凑数」的机器保证——新增一个名字却忘了填，会直接抛而不是静默留 0。
    /// </summary>
    private sealed class Builder
    {
        private readonly double[] _values = new double[FeatureNames.Length];
        private readonly bool[] _missing = new bool[FeatureNames.Length];
        private readonly bool[] _written = new bool[FeatureNames.Length];

        public void Set(string name, double value)
        {
            var i = Index(name);
            if (!MemoryScheduler.IsFinite(value))
            {
                // 非有限值无法进入 JSON（协调器也会把它按缺失处理）：这里就地按缺失落定，保持口径一致。
                _values[i] = 0.0;
                _missing[i] = true;
            }
            else
            {
                _values[i] = value;
                _missing[i] = false;
            }
            _written[i] = true;
        }

        public void Miss(string name)
        {
            var i = Index(name);
            _values[i] = 0.0;
            _missing[i] = true;
            _written[i] = true;
        }

        public ContextFeatureVector Done(string[] names)
        {
            for (var i = 0; i < _written.Length; i++)
                if (!_written[i])
                    throw new InvalidOperationException($"Context 特征「{names[i]}」没有被赋值（既未 Set 也未 Miss）。");
            return new ContextFeatureVector(_values, names, _missing);
        }

        private static int Index(string name) =>
            FeatureIndex.TryGetValue(name, out var i)
                ? i
                : throw new InvalidOperationException($"未登记的特征名：「{name}」。");
    }
}
