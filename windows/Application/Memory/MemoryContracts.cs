namespace Lexi;

/// <summary>定稿载荷：调用方组装，实现方在**单个 SQLite 事务**内写入全部组成部分。</summary>
public sealed record WordSessionCommit(
    LearningSession Session,
    WordSessionSummary Summary,
    CanonicalReview Canonical,
    FsrsPreState CardPreState,
    FsrsCardState CardAfter,
    SchedulerDecision Decision,
    IReadOnlyList<LearningInteractionEvent> PendingEvents,
    IReadOnlyList<ContextFeatureSnapshot> SnapshotsToSave,
    IReadOnlyList<ContextLabel> SnapshotLabels,
    IReadOnlyList<PendingMutation> OutboxMutations,
    MemorySessionCheckpoint? Checkpoint = null);

/// <summary>context 快照的标签：最终 Known=1、Unsure=1、Forgot=0（保留三分类，另存 confidentRecall）。</summary>
public sealed record ContextLabel(string SnapshotId, StudyRating Rating, bool ConfidentRecall);

/// <summary>跨存储变更的待应用条目（JSON 侧写不参与 SQLite 事务，用 outbox 保证可重放）。</summary>
public sealed record PendingMutation(string Kind, string PayloadJson);

public sealed record PendingMutationRow(long Id, string Kind, string PayloadJson, int Attempts);

/// <summary>One surface’s unfinished queue, identified by its existing durable learning session.</summary>
public sealed record MemorySessionCheckpoint(string Surface, string SessionId, string QueueJson, DateTime UpdatedAtUtc);

public sealed record WordSessionCommitResult(bool Applied, bool AlreadyApplied, string CanonicalId);

/// <summary>带标签的 Context 训练样本（快照 + 其结果）。</summary>
/// <remarks>
/// <see cref="FsrsRetrievabilityAtCapture"/> 是**额外**的 init 属性（不是位置参数），
/// 这样既有的 7 参数构造调用点（<c>tests/MemoryTests/CoordinatorTests.cs</c> 的内存替身）不需要改动。
/// 默认值 <see cref="double.NaN"/> = 「未知」；训练器把基线 R 非有限的样本按**不可评估**丢弃并计数，
/// 不会静默当成 1.0（那样会让基线看起来完美，从而伪造出 Context 的「改善」）。
/// </remarks>
public sealed record LabeledContextSample(
    string SnapshotId, string WordKey, DateTime CapturedAtUtc, double[] Features,
    string MissingFlagsJson, int Label, bool ConfidentRecall)
{
    /// <summary>作答前快照里的 FSRS 可回忆性 R（<see cref="ContextFeatureSnapshot.FsrsRetrievabilityAtCapture"/>）。</summary>
    public double FsrsRetrievabilityAtCapture { get; init; } = double.NaN;
}

/// <summary>
/// 有效 canonical（双截止后）的廉价范围摘要：条数与首末 <c>reviewed_at_utc</c>。
/// 条数是"真实训练目标数"的上界、跨度是目标跨度的上界，因此它足以**确定地否定**门槛，
/// 从而避免在每个轮末都整表物化历史。null（见 <see cref="ILearningMemoryStore.LoadCanonicalBounds"/>）= 不支持。
/// </summary>
public sealed record CanonicalHistoryBounds(int Count, DateTime? FirstReviewedAtUtc, DateTime? LastReviewedAtUtc);

/// <summary>
/// 一条**手动 due 覆盖**留痕：管理动作（stage / restart / 批量完成复习）把某条 canonical 之后卡片
/// 的到期日显式改成了 <paramref name="DueAtUtc"/>。
/// <para>它记录的是"管理事实"，与 canonical 记录的"学习事实"分开保存；参数换版整库重放时，
/// 只有锚点（word + canonical_id + revision）匹配的覆盖才会被重新施加，因此用户显式设置的日期
/// 不会被模型日期悄悄顶掉，而撤销回放到该锚点时覆盖也自然重新生效。</para>
/// </summary>
public sealed record FsrsManualDueOverride(
    string WordKey, string AnchorCanonicalId, long AnchorRevision, DateTime DueAtUtc);

/// <summary>
/// 作答前历史查询返回的一条 canonical（**只含未失效行**）。
/// <paramref name="Origin"/> 让特征层把「真实复习」与「首次学习聚合」分开：
/// 用户需求 §7.2 要求新词 acquisition / 轮内强化不得冒充真实长期 retrieval 进入未来回忆评价。
/// </summary>
public sealed record CanonicalHistoryRow(
    string WordKey, string SessionId, CanonicalOrigin Origin, StudyRating Rating,
    DateTime ReviewedAtUtc, DateTime CompletedAtUtc)
{
    /// <summary>
    /// 这条 canonical 的确定性幂等键（<c>cr:&lt;wordKey&gt;:&lt;sessionId&gt;</c>）。
    /// 是**额外**的 init 属性而不是位置参数：既有的位置构造调用点（测试替身）不需要改动。
    /// optimizer 的训练输入要求「同刻按稳定次序」排列，缺了它就只能退化成按时间 tie 的任意顺序，
    /// 于是同一份历史会产出不同的 item 序列——训练不可复现。
    /// </summary>
    public string CanonicalId { get; init; } = "";

    /// <summary>
    /// 这条 canonical 的修订号。手动 due 覆盖按 (word, canonical_id, revision) 锚定：
    /// 同一行被撤销后重新定稿（revive）或改判时 rowid 不变而 revision 会变，
    /// 只按 rowid 匹配会让新的一次学习事实误用旧的管理覆盖值。
    /// </summary>
    public long Revision { get; init; }
    /// <summary>本库 canonical rowid；0 表示未提供持久化提交次序。</summary>
    public long CanonicalSequence { get; init; }
}

/// <summary>
/// 长期记忆持久化端口。实现方（<c>VocabularyService</c>）负责事务、UNIQUE 约束与幂等。
/// 本接口不触碰 <c>words</c> 表的 stage/status/next_review_date——那些是旧排期的兼容投影。
/// </summary>
public interface ILearningMemoryStore
{
    string DatabasePath { get; }

    // —— 轨迹（append-only；重复 EventId 必须被忽略）——
    void AppendEvent(LearningInteractionEvent e);
    // Test doubles may use sequential defaults; production overrides this with one transaction.
    void AppendEventWithCheckpoint(LearningInteractionEvent e, MemorySessionCheckpoint checkpoint)
    { AppendEvent(e); SaveSessionCheckpoint(checkpoint); }
    IReadOnlyList<LearningInteractionEvent> LoadEvents(string sessionId);
    IReadOnlyList<LearningInteractionEvent> LoadEventsForWord(string wordKey);

    // —— 会话 ——
    void UpsertSession(LearningSession session);
    LearningSession? GetSession(string sessionId);

    MemorySessionCheckpoint? GetSessionCheckpoint(string surface) => null;
    void SaveSessionCheckpoint(MemorySessionCheckpoint checkpoint)
        => throw new NotSupportedException("此存储不支持学习会话检查点。");
    void DeleteSessionCheckpoint(string surface, string? sessionId = null)
        => throw new NotSupportedException("此存储不支持学习会话检查点。");

    // —— 定稿（单事务原子）——
    WordSessionCommitResult CommitWordSession(WordSessionCommit commit);

    // —— 撤销 / 修正：同一 logical canonical 的失效 + pre-state 回放 ——
    bool InvalidateCanonical(string canonicalId, DateTime atUtc, string reason);
    // Sequential compatibility fallback for test doubles; production implements a single transaction.
    bool InvalidateCanonicalWithEvent(string canonicalId, DateTime atUtc, string reason,
        LearningInteractionEvent undoEvent, MemorySessionCheckpoint? checkpoint = null)
    {
        if (!InvalidateCanonical(canonicalId, atUtc, reason)) return false;
        if (checkpoint is null) AppendEvent(undoEvent); else AppendEventWithCheckpoint(undoEvent, checkpoint);
        return true;
    }

    // —— FSRS ——
    bool InvalidateCanonicalWithMutations(string canonicalId, DateTime atUtc,
        LearningInteractionEvent undoEvent, MemorySessionCheckpoint? checkpoint,
        IReadOnlyList<PendingMutation> mutations)
        => throw new NotSupportedException("存储不支持原子撤销与跨存储进度。");
    void AppendEventWithMutations(LearningInteractionEvent e, MemorySessionCheckpoint? checkpoint,
        IReadOnlyList<PendingMutation> mutations)
        => throw new NotSupportedException("存储不支持原子事件与跨存储进度。");

    FsrsCardState? GetCard(string wordKey);
    bool HasCard(string wordKey);
    IReadOnlyList<DueCard> QueryDue(DateTime nowUtc, int limit);
    int CountDue(DateTime nowUtc);

    // —— Context ——
    void SaveContextSnapshot(ContextFeatureSnapshot snapshot);
    ContextFeatureSnapshot? GetContextSnapshot(string snapshotId);
    ContextFeatureSnapshot? GetSnapshotByPresentation(string presentationId);
    void LabelSnapshots(IReadOnlyList<ContextLabel> labels, DateTime labeledAtUtc);
    IReadOnlyList<LabeledContextSample> LoadLabeledSamples(DateTime? cutoffUtc = null);

    // —— Context 作答前历史查询（P6 扩展；三条都**只读**且以 beforeUtc 为**严格上界**）——
    //
    // 之所以放在这里而不是让特征层自己拼 SQL：特征层不得依赖持久层实现，而「作答前可知」的证明
    // 必须落在**唯一**的地方——SQL 的 `WHERE ... < $before`。所有上界都是严格小于，
    // 因此任何在快照时刻之后写入的行都不可能进入特征（可测：ContextTests.NoLeakageAfterCapture）。
    //
    // 默认实现返回空集合，仅为让「不建模历史」的测试替身继续编译
    // （tests/MemoryTests/CoordinatorTests.cs 的 FakeMemoryStore 未覆写这三条）；
    // 生产实现（VocabularyService.Memory.cs）全部覆写。空结果在特征层一律按**缺失**处理
    // （Missing=true、值填 0），绝不伪造中性历史，也不把「查不到」当成「没有发生过」。

    /// <summary>
    /// 未失效 canonical，按 <paramref name="reviewedAtUtc"/> 升序。
    /// <paramref name="fromUtc"/> = null 表示不限下界；<paramref name="wordKey"/> = null 表示全部词。
    /// </summary>
    IReadOnlyList<CanonicalHistoryRow> LoadCanonicalHistory(DateTime? fromUtc, DateTime beforeUtc, string? wordKey) => [];

    /// <summary>
    /// 该词**已完成**（<c>completed_at_utc IS NOT NULL</c>）的 word×session 摘要，按完成时刻升序。
    /// 「上一已完成轮」= 其中满足 <c>completed_at_utc &lt; beforeUtc</c> 的最后一条；
    /// 当前轮（尚未完成或已完成于快照之后）永远不在结果里。
    /// </summary>
    IReadOnlyList<WordSessionSummary> LoadCompletedSummaries(string wordKey, DateTime beforeUtc) => [];

    /// <summary>
    /// 最近的**可靠**作答延迟（毫秒，降序取前 <paramref name="limit"/> 条）：
    /// 只取 <c>kind = 'Rated' AND is_recall = 1 AND response_latency_ms IS NOT NULL</c> 且
    /// <c>occurred_at_utc &lt; beforeUtc</c> 的事件——Learn 卡（is_recall = 0）不是真实作答，不能进延迟统计。
    /// </summary>
    IReadOnlyList<int> LoadRecentResponseLatencies(DateTime beforeUtc, int limit) => [];

    // —— 个人参数 / Context 模型 ——
    //
    // 下面两条是 3.1.1 的既有形状，语义 = <c>model_kind='context'</c>。个人 FSRS 参数绝不能走它们：
    // VocabularyService 的实现把 kind 写死成 'context'，直接拿它存权重会写出一行伪 Context 模型，
    // 下次 <see cref="GetPersonalizationModel()"/> 会把它当 Context 模型读回并解析失败，
    // 从而**静默清掉**用户已经通过资格验证的 Context 模型。
    PersonalizationModelState? GetPersonalizationModel();
    void SavePersonalizationModel(PersonalizationModelState state);

    /// <summary>
    /// 按 <paramref name="modelKind"/> 取最新一版模型。
    /// 默认实现只认 <c>'context'</c>（等价于无参重载），其余 kind 返回 null——
    /// 这样"不支持个人参数的测试替身"继续编译且行为不变，而生产实现覆写它。
    /// </summary>
    PersonalizationModelState? GetPersonalizationModel(string modelKind) =>
        string.Equals(modelKind, SchedulingConfig.ContextModelKind, StringComparison.Ordinal)
            ? GetPersonalizationModel()
            : null;

    /// <summary>
    /// 按 <paramref name="modelKind"/> 写入一版模型。默认实现只支持 <c>'context'</c>，
    /// 其余 kind 明确抛 <see cref="NotSupportedException"/>（**不静默丢弃**，否则发布会被误判成功）。
    /// </summary>
    void SavePersonalizationModel(PersonalizationModelState state, string modelKind)
    {
        if (!string.Equals(modelKind, SchedulingConfig.ContextModelKind, StringComparison.Ordinal))
            throw new NotSupportedException("此存储不支持按 model_kind 写入个人化模型：" + modelKind);
        SavePersonalizationModel(state);
    }

    /// <summary>
    /// 按 <paramref name="modelKind"/> 取最近 <paramref name="limit"/> 行，**新的在前**。
    /// 个人参数需要它做两件既有端口做不到的事：① 重启后找回 last-good；② 读训练尝试记账行
    /// （冷却与新增量门槛必须跨重启生效，否则每次启动都会立刻重训一遍）。
    /// 默认实现返回空集合：不实现该能力的替身一律表现为「没有任何个人参数历史」。
    /// </summary>
    IReadOnlyList<PersonalizationModelState> LoadPersonalizationModels(string modelKind, int limit) => [];

    /// <summary>
    /// 是否**存在**参数版本戳不等于 <paramref name="parameterVersion"/> 的 FSRS 卡片。
    /// 用于重启一致性核对：D/S 是模型内部充分统计量，与产出它的权重版本必须同源；
    /// 一旦库里存在别的版本戳的卡片，就必须先用目标权重一致重放，而不是默默混用。
    /// 默认实现返回 false（没有卡片可核对的替身表现为「一致」）。
    /// </summary>
    bool HasCardsOutsideParameterVersion(string parameterVersion) => false;

    /// <summary>
    /// 当前**有效 canonical 集合**的廉价一致性签名（实现自定，只要同集合同签名、集合变了签名必变）。
    /// 供"训练快照 → 发布"之间的乐观并发核对：训练读到的是一份快照，发布时库可能已经变了
    /// （用户又定稿了一条、或撤销了一条），此时用旧快照重放会漏掉刚发生的 canonical。
    /// 默认实现返回空串 = 「无法核对」，调用方不得据此认为一致。
    /// </summary>
    string ComputeCanonicalSignature() => "";

    /// <summary>
    /// 有效 canonical（双截止后）的条数与首末时刻；不支持时返回 null（调用方回落到完整读取）。
    /// </summary>
    CanonicalHistoryBounds? LoadCanonicalBounds(DateTime beforeUtc) => null;

    /// <summary>全部手动 due 覆盖（每 (word, anchor, revision) 取最新一条）。默认空集合。</summary>
    IReadOnlyList<FsrsManualDueOverride> LoadManualDueOverrides() => [];

    /// <summary>
    /// 有有效 canonical、但当前卡 due 与其最后一条 canonical 的决策隐含 due 不一致、
    /// 且没有手动覆盖留痕的卡片数。**只用于把"无法归因的 due 差异"说出来**，绝不据此猜测。
    /// </summary>
    int CountCardsWithUnattributedDue() => 0;

    /// <summary>该库是否出现过任何一次个人参数发布。迁移判定用（只有从未发布过的库才有可判定的旧库语义）。</summary>
    bool HasPublishedPersonalParameters() => false;

    /// <summary>
    /// **单事务**发布个人 FSRS 参数：写参数模型状态行 + 逐词用新权重重放全部有效 canonical
    /// 得到的卡片 + 同事务重写每条 canonical 的可撤销 pre-state 列 + 一致性签名核对。
    /// 任一步失败 → 整体 ROLLBACK 并抛出，调用方据此保留内存中的旧权重（last-good / defaults）。
    /// 默认实现明确抛 <see cref="NotSupportedException"/>：不支持原子发布的存储不得被当成"发布成功"。
    /// </summary>
    FsrsParameterPublishResult PublishFsrsParameters(FsrsParameterPublication publication) =>
        throw new NotSupportedException("此存储不支持个人 FSRS 参数的事务发布。");

    // —— 决策日志 ——
    void SaveSchedulerDecision(SchedulerDecision decision);
    IReadOnlyList<SchedulerDecision> LoadRecentDecisions(string wordKey, int limit);

    // —— outbox（跨存储协调；**不声称跨存储原子**，只保证可重放 + 最终一致）——
    void EnqueueMutations(IReadOnlyList<PendingMutation> mutations);
    IReadOnlyList<PendingMutationRow> LoadPendingMutations();
    void MarkMutationApplied(long id, DateTime atUtc);
    void RecordMutationFailure(long id, string error);
}
