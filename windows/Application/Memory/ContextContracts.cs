namespace Lexi;

/// <summary>
/// 作答前特征快照的输入（全部为「呈现该卡时」已可知的信息；**不得**包含本次作答结果）。
/// </summary>
/// <param name="WordKey">词身份（<see cref="Lexi.WordKey.Key"/> 形式）。</param>
/// <param name="SessionId">当前学习会话 Id。</param>
/// <param name="PresentationId">本次呈现 Id；与 <see cref="ContextFeatureSnapshot.PresentationId"/> 对齐。</param>
/// <param name="CapturedAtUtc">快照时刻（UTC）。恒早于该 presentation 的任何 Rated 事件。</param>
/// <param name="Mode">该词在本轮的长期模式；首次出现（全历史第 1 次呈现）为 FirstLearn。</param>
/// <param name="IsFirstAppearanceForSession">本 session 内该词第 1 次被呈现。</param>
/// <param name="SessionPosition">本 session 内该 presentation 的序号（1-based，按呈现计数）。</param>
/// <param name="SessionReviewedCount">本 session 内已经作答过的**不同** presentation 数（不含本次，因为本次尚未作答）。</param>
/// <param name="Card">呈现时从 store 读到的 FSRS 卡片状态（无卡为 null）。实现方必须只读。</param>
public sealed record ContextFeatureRequest(
    string WordKey, string SessionId, string PresentationId, DateTime CapturedAtUtc,
    LearningMode Mode, bool IsFirstAppearanceForSession,
    int SessionPosition, int SessionReviewedCount,
    FsrsCardState? Card);

/// <summary>
/// 特征向量。<see cref="Values"/> / <see cref="Names"/> / <see cref="Missing"/> 三者长度必须一致
/// （由 <see cref="LearningMemoryCoordinator"/> 在快照写入前断言，不一致直接抛）。
/// </summary>
/// <param name="Values">特征值；非有限值由协调器按缺失处理（置 0 并把 Missing 置 true）。</param>
/// <param name="Names">特征名（仅用于训练期对齐；快照只持久化值，不持久化名字）。</param>
/// <param name="Missing">缺失标记。</param>
public sealed record ContextFeatureVector(double[] Values, string[] Names, bool[] Missing)
{
    /// <summary>特征维数（= <see cref="Values"/> 长度）。</summary>
    public int Length => Values?.Length ?? 0;

    /// <summary>
    /// 把向量对齐到**规范特征顺序**。<see cref="Names"/> 不落库，应用重启后从
    /// <see cref="ContextFeatureSnapshot.FeaturesJson"/> 回读的向量名字数组是空的——
    /// 此时唯一能保证顺序正确的依据就是「schema 版本相同 ⇒ 顺序相同」。
    /// <para>
    /// 长度不一致（schema 换版/模型损坏）→ 抛 <see cref="InvalidDataException"/>，由调用方安全回退；
    /// 名字非空但与规范顺序不符 → 同样抛（宁可拒绝，也不按错位的列训练）。
    /// </para>
    /// </summary>
    public static ContextFeatureVector Align(ContextFeatureVector vector, IReadOnlyList<string> canonicalNames)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(canonicalNames);
        if (vector.Values is null || vector.Missing is null)
            throw new InvalidDataException("ContextFeatureVector 的 Values/Missing 为 null。");
        if (vector.Values.Length != canonicalNames.Count || vector.Missing.Length != canonicalNames.Count)
            throw new InvalidDataException(
                $"特征维数与规范 schema 不一致（实际 {vector.Values.Length}/{vector.Missing.Length}，规范 {canonicalNames.Count}）。");
        var names = vector.Names;
        if (names is { Length: > 0 })
        {
            if (names.Length != canonicalNames.Count)
                throw new InvalidDataException($"特征名字数组长度不一致（{names.Length} vs {canonicalNames.Count}）。");
            for (var i = 0; i < names.Length; i++)
                if (!string.Equals(names[i], canonicalNames[i], StringComparison.Ordinal))
                    throw new InvalidDataException($"特征名错位：位置 {i} 是「{names[i]}」，规范是「{canonicalNames[i]}」。");
        }
        return vector;
    }
}

/// <summary>一次 Context 训练 / 资格推进的结果（可审计；写入 <see cref="PersonalizationModelState.MetricsJson"/> 的摘要同源）。</summary>
/// <param name="Outcome">结果分类。</param>
/// <param name="Mode">训练结束后的阶段。</param>
/// <param name="ModelVersion">本轮训练产出的模型版本；未训练时为空串。</param>
/// <param name="Detail">人类可读的原因（不参与任何数值判断）。</param>
public sealed record ContextTrainingResult(ContextTrainingOutcome Outcome, ContextMode Mode, string ModelVersion, string Detail)
{
    /// <summary>未实现 / 未启用时的中性结果。</summary>
    public static ContextTrainingResult NotSupported { get; } =
        new(ContextTrainingOutcome.NotSupported, ContextMode.Disabled, "", "训练入口未实现。");
}

/// <summary>训练 / 资格推进的结果分类。全部是**可审计的终态**，没有「悄悄什么都不做」的分支。</summary>
public enum ContextTrainingOutcome
{
    /// <summary>该实现不支持训练（默认接口实现）。</summary>
    NotSupported,
    /// <summary>Context 被关闭（<see cref="SchedulingConfig.ContextEnabled"/> = false 或调用方显式关闭）。</summary>
    Disabled,
    /// <summary>冷却窗口内，本轮跳过（不是失败）。</summary>
    Cooldown,
    /// <summary>资格门槛未满足 → 保持 ColdStart，只收集。</summary>
    ColdStart,
    /// <summary>训练成功并落库（可能停在 Shadow，也可能进入 Active）。</summary>
    Trained,
    /// <summary>训练被取消 / 超时 / 数值非法 → 保留 last-good 并退回 Shadow。</summary>
    SafeFallback,
}

/// <summary>
/// 作答前特征提供者。**只允许**使用 <see cref="ContextFeatureRequest"/> 中「作答前可知」的信息；
/// 不得读取任何本次作答结果、本轮后续轨迹、或未来时刻的状态（包括当前时间）。
/// </summary>
public interface IContextFeatureProvider
{
    /// <summary>特征 schema 版本；写入 <see cref="SchedulerDecision.ContextFeatureSchemaVersion"/> 与快照。</summary>
    string FeatureSchemaVersion { get; }

    /// <summary>模型版本；写入 <see cref="SchedulerDecision.ContextModelVersion"/> 与快照。</summary>
    string ModelVersion { get; }

    /// <summary>只使用 request 中「作答前可知」的信息；不得读取任何本次作答结果或本轮未来轨迹。</summary>
    ContextFeatureVector Build(ContextFeatureRequest request);
}

/// <summary>
/// Context 校准器：把「作答前特征 + FSRS 基线可回忆性」映射为一个相对基线的间隔校准量。
/// 具体实现属于 Context 工作流；本层只定义接口并**不做任何自研求根**。
/// </summary>
public interface IContextCalibrator
{
    /// <summary>当前所处阶段；写入 <see cref="SchedulerDecision.ContextMode"/>。</summary>
    ContextMode Mode { get; }

    /// <summary>
    /// 给出校准量。<paramref name="fsrsRetrievability"/> 是**该快照的历史**可回忆性
    /// （取自 <see cref="ContextFeatureSnapshot.FsrsRetrievabilityAtCapture"/>，不是作答后的重算值）。
    /// </summary>
    ContextAdjustment Adjust(ContextFeatureVector features, double fsrsRetrievability);

    /// <summary>
    /// 可选扩展：Active 模式下求解「校准后保持率 = <paramref name="desiredRetention"/>」的候选间隔（天）。
    /// <para>
    /// 默认返回 null → 协调器不做 Context 校准、回退纯 FSRS 基线。
    /// 实现方若要求根，应把「固定历史特征 + 候选 t」合成单调递减的 adjustedRetrievability 委托，
    /// 然后调用 <see cref="MemoryScheduler.SolveIntervalByBisection"/>；**不要在协调器里实现求根**。
    /// </para>
    /// </summary>
    /// <param name="features">该次真实 retrieval 的**历史**特征（作答前快照）。</param>
    /// <param name="stabilityDays">本次 FSRS 更新后的 stability（候选间隔的锚点）。</param>
    /// <param name="desiredRetention">目标保持率（生产为 <see cref="SchedulingConfig.DesiredRetention"/>）。</param>
    double? SolveCandidate(ContextFeatureVector features, double stabilityDays, double desiredRetention) => null;

    /// <summary>
    /// 训练 / 资格推进入口（P6 扩展）。**时间由参数传入，实现不得自行读时钟**。
    /// <para>默认实现 = 不支持训练（返回 <see cref="ContextTrainingResult.NotSupported"/>），
    /// 因此既有的测试替身（FakeCalibrator）不需要改动。</para>
    /// <para>约定：调用方（会话结束 / 空闲）负责在**不与 store 写并发**的前提下调用；
    /// 实现内部允许 <c>Task.Run</c>，但必须可取消且带超时，绝不阻塞 UI 线程。</para>
    /// </summary>
    Task<ContextTrainingResult> TrainAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(ContextTrainingResult.NotSupported);

    /// <summary>
    /// 个人 FSRS 参数换版后的**基线变更通知**（默认空实现，测试替身不需要改动）。
    /// <para>
    /// 为什么必须存在：Context 模型学的是相对 FSRS 的残差 δ，而它训练时用的基线
    /// <c>logit(R)</c> 来自**快照落盘那一刻**的 FSRS 权重（<c>ContextFeatureSnapshot.FsrsRetrievabilityAtCapture</c>）。
    /// 权重一换，历史快照里的 R 全是旧基线、新样本是新基线，同一个 δ 就对应两个不同基线。
    /// 因此换版必须让 Context **退到重新资格**：旧 Active 一律不得沿用，后续训练/独立确认
    /// 只允许使用当前基线生效之后捕获的快照。
    /// </para>
    /// <para>约定：调用方（发布事务提交成功之后）在**不与 store 写并发**的前提下调用。</para>
    /// </summary>
    void ApplyFsrsBaseline(string parameterVersion, DateTime activeSinceUtc) { }
}
