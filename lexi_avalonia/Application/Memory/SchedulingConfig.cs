namespace Lexi;

/// <summary>
/// 长期排期与个人化的集中配置。这是唯一允许出现这些常量的地方——不要在多处散落魔数。
/// 版本号用于 SchedulerDecision 审计；算法或策略变化时必须同时升版本。
/// </summary>
public static class SchedulingConfig
{
    /// <summary>目标保持率。生产基线 0.90。</summary>
    public const double DesiredRetention = 0.90;

    /// <summary>Context 校准后的间隔相对纯 FSRS 基线的钳制倍率。</summary>
    public const double ClampLowerRatio = 0.5;
    public const double ClampUpperRatio = 1.5;

    /// <summary>绝对间隔边界（天）。</summary>
    public const double MinimumIntervalDays = 1.0;
    public const double MaximumIntervalDays = 36500.0;

    /// <summary>Again/Hard 之后的长期最小间隔（天）与同轮/同日回队保护。</summary>
    public const double MinimumIntervalAfterAgainDays = 1.0;
    public const double MinimumIntervalAfterHardDays = 1.0;

    /// <summary>
    /// 同日保护（<see cref="MemoryScheduler.ApplyMinimumInterval"/>）把到期时刻推到本地日界**之后**时，
    /// 越过日界的余量（秒）。用「严格晚于」而不是「正好落在零点」表达「不允许同一天再次到期」。
    /// </summary>
    public const double SameDayBoundaryMarginSeconds = 1.0;

    // —— 版本戳（写入 SchedulerDecision / FsrsCardState）——
    public const string SchedulerVersion = "sched-1";
    public const string TrajectorySchemaVersion = "traj-1";
    public const string ContextFeatureSchemaVersion = "ctx-feat-1";

    // —— Context 训练门槛（最少数据/跨度/正负样本/冷却）——
    public const int ContextMinimumSamples = 200;
    public const int ContextMinimumPositive = 30;
    public const int ContextMinimumNegative = 30;
    public const int ContextMinimumSpanDays = 14;
    public const int ContextTrainingCooldownMinutes = 30;
    public const double ContextMinimumLogLossImprovement = 0.005;
    public const double ContextDeteriorationGuard = 0.0;

    // —— Context 开关与训练资源预算（P6）——
    //
    // 开关是**编译期默认值**，运行期可关：ContextCalibratorOptions.Enabled 以它为默认。
    // 用「options 默认值」而不是「可变静态字段」是刻意的——后者会让 UI 与后台训练线程
    // 共享可变全局状态，测试之间也会互相污染。
    public const bool ContextEnabled = true;

    /// <summary>训练随机种子。优化器当前是**确定性**的（零初始化 + 全批量梯度下降，无采样/无打乱），
    /// 因此该值只作为可复现性审计字段写入 metrics；一旦引入任何随机成分必须用它初始化 Random。</summary>
    public const int ContextSeed = 20260301;

    /// <summary>时间序切分比例（过去训练 / 未来验证）。**禁止**随机切分。</summary>
    public const double ContextTrainFraction = 0.70;

    /// <summary>L2 正则强度（作用于非截距系数，不含截距）。</summary>
    public const double ContextL2Regularization = 0.01;

    /// <summary>梯度下降最大迭代次数与初始学习率（全批量；步长在损失上升时折半回退）。</summary>
    public const int ContextTrainingMaxIterations = 2000;
    public const double ContextTrainingLearningRate = 0.5;

    /// <summary>梯度收敛阈值（最大绝对分量）。</summary>
    public const double ContextGradientTolerance = 1e-7;

    /// <summary>单次训练的墙钟上限（秒）。超时 → 保留 last-good 并退回 Shadow。</summary>
    public const int ContextTrainingTimeoutSeconds = 30;

    /// <summary>
    /// 校准量 δ 的绝对值上限。超过即判定为「极端系数」→ 拒绝该模型、保留 last-good 并退回 Shadow。
    /// δ 是 log-odds 偏移：|δ| = 3 已能把 R=0.9 推到 0.996 / 0.42，再大就只能靠钳制兜底了。
    /// </summary>
    public const double ContextMaxAbsDelta = 3.0;

    /// <summary>验证段（未来段）最少样本数——低于它不做任何资格判定，停在 ColdStart。</summary>
    public const int ContextMinimumValidationSamples = 20;

    /// <summary>进入 Active 所需的**独立确认段**最少样本数（迟滞：这段数据在资格判定时还不存在）。</summary>
    public const int ContextMinimumActiveConfirmationSamples = 20;

    /// <summary>rolling 中位延迟的采样窗口（最近的可靠作答条数）。</summary>
    public const int ContextLatencyWindowSamples = 200;

    /// <summary>rolling 中位延迟被认为「可靠」所需的最少样本数；不足时该特征按缺失标记。</summary>
    public const int ContextLatencyMinimumSamples = 5;

    /// <summary>写入 MetricsJson 的冻结预测（prequential evidence）条数上限，避免状态行无限膨胀。</summary>
    public const int ContextMaxLoggedPredictions = 200;

    /// <summary>模型版本前缀；完整版本 = 前缀 + 训练截止时刻（十六进制 ticks），可幂等重写。</summary>
    public const string ContextModelVersionPrefix = "ctx-lr-1";

    // —— FSRS 个人参数训练门槛与资源预算 ——
    //
    // 三个既有常量（下方）自 3.1.1 起草起就冻结在这里、语义明确但**一直没有读取者**；
    // 本轮的自动集成才第一次真正消费它们。新增常量一律与它们同源、同处，不允许在别处散落魔数。
    /// <summary>发起一轮自动训练所需的**有效 canonical 复习条数**下限（不是 item 数、不是词数）。</summary>
    public const int OptimizerMinimumReviews = 400;
    /// <summary>发起训练所需的**时间跨度**下限（首次与末次 <c>reviewed_at_utc</c> 之间，整天数）。</summary>
    public const int OptimizerMinimumSpanDays = 30;
    /// <summary>两次训练**尝试**之间的冷却（分钟）；失败同样受它约束（失败既不缩短也不延长冷却）。</summary>
    public const int OptimizerCooldownMinutes = 60;

    /// <summary>
    /// 距上次训练尝试之后**新增**的有效 canonical 条数下限。
    /// 门槛「数据量 / 跨度 / 新增量 / 冷却」四项里只有这一项会随时间自然满足——
    /// 没有它，一个已有 400 条但不再增长的历史会每次冷却被唤醒就重训一遍同一批数据。
    /// </summary>
    public const int OptimizerMinimumNewReviews = 100;

    /// <summary>单次训练的墙钟上限（秒）。超时 → 取消 → 保留 last-good，学习流程不受影响。</summary>
    public const int OptimizerTrainingTimeoutSeconds = 60;

    /// <summary>
    /// 个人参数模型在 <c>personalization_models</c> 里的 <c>model_kind</c> 取值。
    /// 与 <c>'context'</c> 同表不同 kind：既有的 <c>GetPersonalizationModel()</c> 默认只读 context，
    /// 因此个人参数**永远不会**被 Context 当成自己的模型读回去（那会静默清掉已通过资格的 Context 模型）。
    /// </summary>
    public const string FsrsParameterModelKind = "fsrs_params";

    /// <summary><c>personalization_models</c> 里 Context 校准模型的 <c>model_kind</c> 取值。</summary>
    public const string ContextModelKind = "context";

    /// <summary>
    /// 默认参数版本戳（<c>Fsrs6Weights.Source == "defaults"</c> 时恒定使用它）。
    /// 非 defaults 的权重一律用它 + 权重指纹派生（见 <c>FsrsPersonalization.ParameterVersionOf</c>），
    /// 因此「参数版本」与「实际权重」一一对应，换版必然换版本戳。
    /// </summary>
    public const string FsrsParameterVersionPrefix = "fsrs6-p1";

    /// <summary>训练尝试记账行的 <c>model_version</c> 前缀（与真正发布参数的版本戳严格区分）。</summary>
    public const string FsrsParameterAttemptVersionPrefix = "fsrs6-attempt";

    /// <summary><c>metrics_json</c> 里区分「发布参数行」与「尝试记账行」的键与取值。</summary>
    public const int FsrsParameterHistoryScanLimit = 200;
    public const string FsrsParameterRecordKindKey = "recordKind";
    public const string FsrsParameterRecordKindParams = "params";
    public const string FsrsParameterRecordKindAttempt = "attempt";

    /// <summary><c>metrics_json</c> 里记录参数版本戳的键（Context 的基线绑定与重启一致性核对都读它）。</summary>
    public const string FsrsParameterVersionMetricKey = "fsrsParameterVersion";

    /// <summary><c>metrics_json</c> 里记录该参数版本生效时刻（UTC ISO-8601）的键。</summary>
    public const string FsrsParameterActiveSinceMetricKey = "activeSinceUtc";

    // ══ FSRS-6 算法常量 ══
    // 全部来自 agents/audit-05-fsrs-upstream.md §3（每条带官方来源 URL），逐项实读转录，
    // 并经 692 行官方 golden 与 py-fsrs test_basic 向量复核；禁止凭记忆改动。

    /// <summary>稳定性下界。取 py-fsrs 6.3.2 的 <c>STABILITY_MIN = 0.001</c>（dependency_decision §决策 3：
    /// 我方 golden 由 py-fsrs 6.3.2 生成，而 fsrs-rs 6.6.2 的 0.0001 与之不一致且未验证）。</summary>
    public const double StabilityMin = 0.001;

    /// <summary>稳定性上界。与 <see cref="MaximumIntervalDays"/> 同值（fsrs-rs <c>S_MAX = 36500.0</c>）。</summary>
    public const double StabilityMax = 36500.0;

    /// <summary>难度边界（fsrs-rs <c>D_MIN / D_MAX</c>）。</summary>
    public const double DifficultyMin = 1.0;
    public const double DifficultyMax = 10.0;

    /// <summary>遗忘曲线 decay = −w20（audit §3.2 ①）；公式写作 <c>DECAY = −w20</c>。</summary>
    public const double Decay = -0.1542;

    /// <summary>遗忘曲线 factor = 0.9^(1/DECAY) − 1（audit §3.2 ① 的派生常量，实算值 0.9803464944134797）。
    /// 用 <c>static readonly</c> 而非 <c>const</c>：const 无法在编译期调用 Math.Pow。</summary>
    public static readonly double Factor = Math.Pow(0.9, 1.0 / Decay) - 1.0;

    /// <summary>FSRS-6 参数个数（audit §5.6：三处独立确认 21）。</summary>
    public const int FsrsParameterCount = 21;

    /// <summary>写入 <c>FsrsCardState.FsrsAlgorithmVersion</c> 的算法标识。</summary>
    public const string AlgorithmVersion = "FSRS-6";

    /// <summary>写入 <c>FsrsCardState.FsrsLibraryVersion</c> 的库标识。本项目为自研纯 C# 实现（无外部包）。</summary>
    public const string LibraryVersion = "lexi-fsrs6-core/1";

    /// <summary>写入 <c>FsrsCardState.FsrsParameterVersion</c> 的参数版本戳；
    /// 与 <c>Fsrs6Weights.ParameterVersion</c>（JSON 字段 parameterVersion = 1）对应。</summary>
    public const string ParameterVersion = "fsrs6-p1";

    /// <summary>
    /// learning steps 的**生产默认值：空**。本项目的长期层不接管轮内强化（用户需求 §6：
    /// 轮内连击由 StudyRound 负责），因此长期排期直接进入 Review，不产生 min 级学习步。
    /// 仅 golden 验证用的参考运行器会显式传入非空 steps（<c>Fsrs6Model.ReferenceReview</c>）。
    /// </summary>
    public static readonly TimeSpan[] DefaultLearningSteps = [];

    /// <inheritdoc cref="DefaultLearningSteps"/>
    public static readonly TimeSpan[] DefaultRelearningSteps = [];
}
