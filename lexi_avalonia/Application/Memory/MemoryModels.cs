namespace Lexi;

/// <summary>词身份来源。档案词、IELTS 目录词、以及既不在档案也不在目录中的裸词形。</summary>
public enum WordSource
{
    Archive,
    Ielts,
    Form,
}

/// <summary>词卡的学习模式（沿用 StudyMode 的语义，单独定义以便长期层不依赖轮内状态机）。</summary>
public enum LearningMode
{
    FirstLearn,
    Review,
}

/// <summary>原始交互事件的种类。追加写入，永不删除。</summary>
public enum InteractionEventKind
{
    /// <summary>卡片被呈现（含 Learn 卡只看答案；IsRecall=false 的不构成 retrieval）。</summary>
    Presented,
    /// <summary>一次作答。</summary>
    Rated,
    /// <summary>对上一次作答的改判（同一 presentation）。</summary>
    Revised,
    /// <summary>撤销上一次作答（同一 presentation 或跨 presentation）。</summary>
    Undone,
}

/// <summary>canonical 的来源口径。</summary>
public enum CanonicalOrigin
{
    /// <summary>复习（非新词）：取第一次真实 retrieval 的最终有效判断。</summary>
    FirstRetrieval,
    /// <summary>首次学习：本轮完成后按聚合策略归纳。</summary>
    FirstLearnAggregate,
}

/// <summary>Context 校准所处的阶段。</summary>
public enum ContextMode
{
    Disabled,
    ColdStart,
    Shadow,
    Active,
}

/// <summary>FSRS 卡片状态机的阶段。</summary>
public enum FsrsState
{
    New,
    Learning,
    Review,
    Relearning,
}

/// <summary>
/// 源感知的稳定词身份。<see cref="Key"/> 形如 <c>archive:&lt;uuid&gt;</c> / <c>ielts:&lt;catalogId&gt;</c> / <c>form:&lt;formC&gt;</c>。
/// 档案词用 word_archives.uuid（由词形派生、写入后不再重算），**不用 words.id**：rowid 在恢复备份后会重排。
/// </summary>
public readonly record struct WordKey(WordSource Source, string SourceId)
{
    public const string ArchivePrefix = "archive:";
    public const string IeltsPrefix = "ielts:";
    public const string FormPrefix = "form:";

    public string Key => Prefix + SourceId;

    public string Prefix => Source switch
    {
        WordSource.Archive => ArchivePrefix,
        WordSource.Ielts => IeltsPrefix,
        _ => FormPrefix,
    };

    /// <summary>
    /// 消费者侧的**策略提示**：只有档案来源的词有复习表面，因此只有它们应当被排入到期队列。
    /// <para>
    /// 注意 <see cref="ILearningMemoryStore.QueryDue"/> **有意不按本属性过滤**——持久层是纯数据查询，
    /// 不应内嵌产品策略；由 UI 侧把返回的 <c>WordKey</c> 反查回档案词条时自然丢弃非档案来源的卡。
    /// </para>
    /// </summary>
    public bool IsSchedulable => Source == WordSource.Archive;

    public static WordKey Archive(string uuid) => new(WordSource.Archive, uuid);
    public static WordKey Ielts(string catalogId) => new(WordSource.Ielts, catalogId);
    public static WordKey Form(string formC) => new(WordSource.Form, formC);

    public static WordKey Parse(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.StartsWith(ArchivePrefix, StringComparison.Ordinal)) return Archive(key[ArchivePrefix.Length..]);
        if (key.StartsWith(IeltsPrefix, StringComparison.Ordinal)) return Ielts(key[IeltsPrefix.Length..]);
        if (key.StartsWith(FormPrefix, StringComparison.Ordinal)) return Form(key[FormPrefix.Length..]);
        throw new FormatException($"无法解析的词身份：{key}");
    }

    public override string ToString() => Key;
}

/// <summary>一次学习会话。SessionId 由调用方生成并持久化，用于 canonical 的幂等键。</summary>
public sealed class LearningSession
{
    public string SessionId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public StudyMode Mode { get; set; } = StudyMode.Review;
    public WordSource PrimarySource { get; set; } = WordSource.Archive;
    public int PlannedWordCount { get; set; }
    /// <summary>计划来源时的计划 Id；否则空串。</summary>
    public string PlanId { get; set; } = "";
}

/// <summary>追加写入的原始交互事件。所有修正与撤销都以新事件表达，历史不可删除。</summary>
public sealed class LearningInteractionEvent
{
    /// <summary>稳定幂等键（Guid "N"）。DB UNIQUE 约束，重复写入被忽略。</summary>
    public string EventId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string WordKey { get; set; } = "";
    public string PresentationId { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
    public LearningMode LearningMode { get; set; }
    public InteractionEventKind Kind { get; set; }
    /// <summary>本次事件携带的判断；<see cref="InteractionEventKind.Presented"/> 时为 null。</summary>
    public StudyRating? Response { get; set; }
    /// <summary>Revised/Undone 时被覆盖的先前判断。</summary>
    public StudyRating? PreviousResponse { get; set; }
    /// <summary>本 session 内该词第几次被呈现（1-based）。</summary>
    public int SessionAppearanceIndex { get; set; }
    /// <summary>全历史该词第几次被呈现（1-based）。</summary>
    public int WordAppearanceIndex { get; set; }
    public int RecognitionCountBefore { get; set; }
    public int RecognitionCountAfter { get; set; }
    public bool IsFirstAppearanceForWord { get; set; }
    /// <summary>可靠时的作答延迟（毫秒）。剔除睡眠/后台/失焦后仍不可靠时为 null。</summary>
    public int? ResponseLatencyMs { get; set; }
    /// <summary>Revision/Undo 指向被修正的事件。</summary>
    public string? SupersedesEventId { get; set; }
    /// <summary>该 presentation 是否为真实回忆（Learn 卡只看答案 → false）。</summary>
    public bool IsRecall { get; set; } = true;
}

/// <summary>由 <see cref="LearningInteractionEvent"/> 投影出的一次呈现（同一个 presentation 可以有多次修正）。</summary>
public sealed class PresentationAttempt
{
    public string PresentationId { get; set; } = "";
    public string WordKey { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTime PresentedAtUtc { get; set; }
    public StudyRating? InitialResponse { get; set; }
    public StudyRating? FinalValidatedResponse { get; set; }
    public int RevisionCount { get; set; }
    public List<StudyRating> RevisionPath { get; set; } = [];
    public bool HadResponseRevision { get; set; }
    public int? LatencyMs { get; set; }
    public bool IsRecall { get; set; } = true;
    public int SessionAppearanceIndex { get; set; }
    public bool IsFirstAppearanceForWord { get; set; }
}

/// <summary>一个 word × session 的轨迹摘要。计数基于各 presentation 的最终判断，可由 raw history 重建。</summary>
public sealed class WordSessionSummary
{
    public string WordKey { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTime FirstPresentedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public StudyRating? FirstInitialResponse { get; set; }
    public StudyRating? FirstValidatedResponse { get; set; }
    public int TotalPresentations { get; set; }
    public int FinalKnownCount { get; set; }
    public int FinalFuzzyCount { get; set; }
    public int FinalForgottenCount { get; set; }
    public int ResetCount { get; set; }
    public int ResponseRevisionCount { get; set; }
    public int KnownToFuzzy { get; set; }
    public int KnownToForgotten { get; set; }
    public int FuzzyToForgotten { get; set; }
    public bool HadFuzzy { get; set; }
    public bool HadForgotten { get; set; }
    public bool HadResponseRevision { get; set; }
    public int MaxKnownStreak { get; set; }
    public int PresentationsToMastery { get; set; }
    public long? TimeToMasteryMs { get; set; }
}

/// <summary>一个 word × session 的唯一长期信号。</summary>
public sealed class CanonicalReview
{
    /// <summary>本库 canonical rowid；0 表示未提供持久化提交次序。</summary>
    public long CanonicalSequence { get; set; }
    /// <summary>确定性幂等键：<c>cr:&lt;wordKey&gt;:&lt;sessionId&gt;</c>。</summary>
    public string CanonicalId { get; set; } = "";
    public string WordKey { get; set; } = "";
    public string SessionId { get; set; } = "";
    /// <summary>Review 模式下指向第一次真实 retrieval 的 presentation。</summary>
    public string? SourcePresentationId { get; set; }
    public CanonicalOrigin Origin { get; set; }
    /// <summary>Known→Good / Unsure→Hard / Forgot→Again。不加 Easy。</summary>
    public StudyRating Rating { get; set; }
    /// <summary>Review 模式 = 第一次真实 retrieval 的时刻；FirstLearn = 该词首次呈现时刻。</summary>
    public DateTime ReviewedAtUtc { get; set; }
    /// <summary>定稿时刻。</summary>
    public DateTime CompletedAtUtc { get; set; }
    public string AggregationPolicyVersion { get; set; } = AggregationPolicy.Version;
    public int Revision { get; set; }
    public bool Invalidated { get; set; }
    public string? SupersedesCanonicalId { get; set; }

    public static string BuildId(string wordKey, string sessionId) => $"cr:{wordKey}:{sessionId}";
}

/// <summary>聚合策略版本与规则。改动规则必须升版本，旧 canonical 保留其版本号。</summary>
public static class AggregationPolicy
{
    public const string Version = "agg-1";

    /// <summary>首次学习完成后的聚合：无模糊无忘记→Known；有模糊无忘记→Unsure；任一忘记→Forgot。</summary>
    public static StudyRating Aggregate(WordSessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.FinalForgottenCount > 0) return StudyRating.Forgot;
        if (summary.FinalFuzzyCount > 0) return StudyRating.Unsure;
        return StudyRating.Known;
    }
}

/// <summary>每个词持久化的 FSRS 卡片状态。一次 canonical 只应用一次。</summary>
public sealed class FsrsCardState
{
    public string WordKey { get; set; } = "";
    public double Difficulty { get; set; }
    public double Stability { get; set; }
    public long Reps { get; set; }
    public long Lapses { get; set; }
    public FsrsState State { get; set; } = FsrsState.New;
    public DateTime? LastReviewAtUtc { get; set; }
    public DateTime? NextReviewAtUtc { get; set; }
    public StudyRating? LastCanonicalRating { get; set; }
    public string FsrsAlgorithmVersion { get; set; } = "";
    public string FsrsLibraryVersion { get; set; } = "";
    public string FsrsParameterVersion { get; set; } = "";
    /// <summary>幂等水位：已应用到第几条 canonical 序号。null 表示从未应用。</summary>
    public long? LastAppliedCanonicalSeq { get; set; }

    public FsrsCardState Clone() => (FsrsCardState)MemberwiseClone();
}

/// <summary>到期队列中的一项。</summary>
public sealed record DueCard(string WordKey, DateTime NextReviewAtUtc, double Difficulty, double Stability);

/// <summary>作答前保存的不可变特征快照。</summary>
public sealed class ContextFeatureSnapshot
{
    public string SnapshotId { get; set; } = "";
    public string WordKey { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string PresentationId { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public double FsrsDifficultyAtCapture { get; set; }
    public double FsrsStabilityAtCapture { get; set; }
    public double FsrsRetrievabilityAtCapture { get; set; }
    public string FeatureSchemaVersion { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public string FeaturesJson { get; set; } = "{}";
    public string MissingFlagsJson { get; set; } = "[]";
    public StudyRating? Label { get; set; }
    public bool? ConfidentRecall { get; set; }
    public DateTime? LabeledAtUtc { get; set; }
}

/// <summary>个人化模型状态（Context 与 FSRS 个人参数共用一张状态行）。</summary>
public sealed class PersonalizationModelState
{
    public string ModelVersion { get; set; } = "";
    public DateTime TrainedAtUtc { get; set; }
    public DateTime TrainCutoffUtc { get; set; }
    public ContextMode Status { get; set; } = ContextMode.ColdStart;
    public string CoefficientsJson { get; set; } = "[]";
    public string ScalerJson { get; set; } = "{}";
    public string MetricsJson { get; set; } = "{}";
    public string? LastGoodModelVersion { get; set; }
}

/// <summary>每次长期排期决策的完整审计记录。</summary>
public sealed class SchedulerDecision
{
    public string DecisionId { get; set; } = "";
    public string WordKey { get; set; } = "";
    public string CanonicalId { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public double BaselineIntervalDays { get; set; }
    public double BaselineRetrievability { get; set; }
    public double BaselineDifficulty { get; set; }
    public double BaselineStability { get; set; }
    public ContextMode ContextMode { get; set; }
    public double? ContextDelta { get; set; }
    public double? ContextCandidateIntervalDays { get; set; }
    public double FinalIntervalDays { get; set; }
    public DateTime FinalDueAtUtc { get; set; }
    public double DesiredRetention { get; set; }
    public string FsrsAlgorithmVersion { get; set; } = "";
    public string FsrsLibraryVersion { get; set; } = "";
    public string FsrsParameterVersion { get; set; } = "";
    public string AggregationPolicyVersion { get; set; } = "";
    public string TrajectorySchemaVersion { get; set; } = "";
    public string ContextFeatureSchemaVersion { get; set; } = "";
    public string ContextModelVersion { get; set; } = "";
    public string SchedulerVersion { get; set; } = "";
}

/// <summary>canonical 定稿时保存的 FSRS 前置状态，用于撤销时精确回放（不重算历史）。</summary>
public sealed class FsrsPreState
{
    public double Difficulty { get; set; }
    public double Stability { get; set; }
    public long Reps { get; set; }
    public long Lapses { get; set; }
    public FsrsState State { get; set; } = FsrsState.New;
    public DateTime? LastReviewAtUtc { get; set; }
    public DateTime? NextReviewAtUtc { get; set; }
    public StudyRating? LastCanonicalRating { get; set; }
    public bool Existed { get; set; }

    public static FsrsPreState From(FsrsCardState? card) => card is null
        ? new FsrsPreState { Existed = false }
        : new FsrsPreState
        {
            Existed = true,
            Difficulty = card.Difficulty,
            Stability = card.Stability,
            Reps = card.Reps,
            Lapses = card.Lapses,
            State = card.State,
            LastReviewAtUtc = card.LastReviewAtUtc,
            NextReviewAtUtc = card.NextReviewAtUtc,
            LastCanonicalRating = card.LastCanonicalRating,
        };
}
