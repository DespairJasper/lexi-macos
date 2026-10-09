using System.Text.Json.Serialization;

namespace Lexi;

/// <summary>
/// 请求给 FSRS-6 optimizer helper 的 stdin JSON 载荷。
/// 契约（design-contract.md 冻结规范与 5.2.0 API 对齐）：
/// version: 1, algorithm: "FSRS-6", enable_short_term: true, threads: 1, seed: 2023,
/// items: [{ reviews: [{ rating: 1..4, delta_t: uint days }] }]
/// </summary>
public sealed class FsrsOptimizerRequest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = Fsrs6Weights.Algorithm;

    [JsonPropertyName("items")]
    public List<FsrsOptimizerItem> Items { get; set; } = [];

    [JsonPropertyName("enable_short_term")]
    public bool EnableShortTerm { get; set; } = true;

    [JsonPropertyName("threads")]
    public int Threads { get; set; } = 1;

    /// <summary>官方 5.2.0 API 固定使用 seed 2023。</summary>
    [JsonPropertyName("seed")]
    public int Seed { get; set; } = 2023;
}

/// <summary>
/// 单个训练序列项：包含到某个预测目标为止的卡片完整复习历史前缀。
/// </summary>
public sealed class FsrsOptimizerItem
{
    [JsonPropertyName("reviews")]
    public List<FsrsOptimizerReview> Reviews { get; set; } = [];
}

/// <summary>
/// 序列中的单次复习记录。
/// </summary>
public sealed class FsrsOptimizerReview
{
    /// <summary>FSRS 评分：Again=1, Hard=2, Good=3, Easy=4。</summary>
    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    /// <summary>自上一次复习以来的整天数（向下截断）。首条必须为 0。</summary>
    [JsonPropertyName("delta_t")]
    public uint DeltaT { get; set; }
}

/// <summary>
/// optimizer helper 从 stdout 输出的 JSON 响应。
/// 严格匹配官方 5.2.0 native helper 格式：
/// { version: int 1, algorithm: "FSRS-6", protocol: "fsrs-optimizer-v1", model_version: "5.2.0",
///   git_sha: "aca2838bfbdc6f15ca3f7a0c96a99fae466c9e9c", parameter_count: 21, weights: [...] }
/// </summary>
public sealed class FsrsOptimizerResponse
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("model_version")]
    public string? ModelVersion { get; set; }

    [JsonPropertyName("git_sha")]
    public string? GitSha { get; set; }

    [JsonPropertyName("parameter_count")]
    public int ParameterCount { get; set; }

    [JsonPropertyName("weights")]
    public double[]? Weights { get; set; }

    [JsonPropertyName("effective_seed")]
    public int? EffectiveSeed { get; set; }

    [JsonPropertyName("items_count")]
    public int? ItemsCount { get; set; }

    [JsonPropertyName("evolved_count")]
    public int? EvolvedCount { get; set; }

    [JsonPropertyName("max_sequence_length")]
    public int? MaxSequenceLength { get; set; }

    [JsonPropertyName("compatibility_id")]
    public string? CompatibilityId { get; set; }

    [JsonPropertyName("compatibility_revision")]
    public string? CompatibilityRevision { get; set; }

    [JsonPropertyName("patch_sha256")]
    public string? PatchSha256 { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object>? ExtraMetadata { get; set; }
}

/// <summary>
/// 优化器配置与资源预算。
/// </summary>
public sealed class FsrsOptimizerOptions
{
    /// <summary>
    /// 原生 helper 可执行文件路径。若为 null，将依次检查环境变量、默认打包路径和开发环境探测路径。
    /// </summary>
    public string? HelperPath { get; set; }

    /// <summary>子进程超时时间（默认 30 秒）。超时将强制终止子进程树。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>子进程取消或超时后的有限清理等待时间（默认 3 秒），避免无界悬挂。</summary>
    public TimeSpan CleanupWaitBudget { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>stdout 最大读取字节数预算（默认 64 KB），防无界内存泄漏与恶意注入。</summary>
    public int MaxStdoutBytes { get; set; } = 64 * 1024;

    /// <summary>stderr 最大读取字节数预算（默认 64 KB）。</summary>
    public int MaxStderrBytes { get; set; } = 64 * 1024;

    /// <summary>输入历史 review 数量上限（默认 50,000）。</summary>
    public int MaxInputReviews { get; set; } = 50_000;

    /// <summary>生成 item 序列上限预算（默认 50,000）。</summary>
    public int MaxItemsBudget { get; set; } = 50_000;

    /// <summary>生成 item 中总 review cells 数量预算（默认 100,000）。</summary>
    public int MaxTotalReviewCells { get; set; } = 100_000;

    /// <summary>序列化后的 JSON 输入字节预算（默认 2 MB）。</summary>
    public int MaxSerializedInputBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>单个 item 序列的最长 review 条数（官方 5.2 内部上限为 64）。</summary>
    public int MaxSequenceLength { get; set; } = 64;

    /// <summary>时间切分训练集比例（默认 0.70 即 70% 过去用于训练，30% 未来用于留出验证）。</summary>
    public double TrainFraction { get; set; } = 0.70;

    /// <summary>定稿时刻截断点（防尚未完成的历史泄漏）。大于等于该时刻的记录被排除。</summary>
    public DateTime? CompletedAtCutoff { get; set; }

    /// <summary>训练段最少有效目标复习数（默认 10）。</summary>
    public int MinimumTrainReviews { get; set; } = 10;

    /// <summary>留出验证段最少样本数（默认 20）。</summary>
    public int MinimumValidationReviews { get; set; } = 20;

    /// <summary>留出验证段最少正样本数（Recall Success, Label=1，默认 5）。</summary>
    public int MinimumPositiveReviews { get; set; } = 5;

    /// <summary>留出验证段最少负样本数（Recall Failure, Label=0，默认 5）。</summary>
    public int MinimumNegativeReviews { get; set; } = 5;

    /// <summary>相对于基线的 LogLoss 最小改善量门槛（默认 0.001）。</summary>
    public double MinimumLogLossImprovement { get; set; } = 0.001;

    /// <summary>相对于基线的 Brier 最小改善量门槛（默认 0.0 即不劣于基线）。</summary>
    public double MinimumBrierImprovement { get; set; } = 0.0;

    /// <summary>
    /// f32 边界舍入允许的极小容差（默认 1e-4）。仅在此容差内的超界被明示投影，超界更甚者直接拒绝。
    /// </summary>
    public double RoundingTolerance { get; set; } = 1e-4;

    /// <summary>上一次通过验证的可用权重（last-good）。验证时将同时对 defaults 和 last-good 对照。</summary>
    public Fsrs6Weights? LastGoodWeights { get; set; }

    /// <summary>兼容别名：等价于 LastGoodWeights。</summary>
    public Fsrs6Weights? BaselineWeights
    {
        get => LastGoodWeights;
        set => LastGoodWeights = value;
    }

    /// <summary>请求中携带的随机种子（官方 5.2.0 API 固定使用 2023）。</summary>
    public int Seed { get; set; } = 2023;
}

/// <summary>
/// 优化与验证执行统计指标（同时保留对 Defaults 与 LastGood 的双重评估）。
/// </summary>
public sealed record FsrsOptimizationMetrics(
    int TotalReviews,
    int ValidatedReviews,
    int TrainReviews,
    int ValidationReviews,
    int ValidationPositives,
    int ValidationNegatives,
    double DefaultsLogLoss,
    double DefaultsBrier,
    double DefaultsLogLossImprovement,
    double DefaultsBrierImprovement,
    double LastGoodLogLoss,
    double LastGoodBrier,
    double LastGoodLogLossImprovement,
    double LastGoodBrierImprovement,
    double CandidateLogLoss,
    double CandidateBrier,
    string? HelperVersion,
    string? HelperSha,
    int? EffectiveSeed,
    TimeSpan Duration,
    int ExcludedLongSequenceTargetCount = 0,
    int AmbiguousOrOverlappingSequenceCount = 0,
    string? CompatibilityId = null,
    string? PatchSha256 = null,
    int? MaxSequenceLength = null,
    int TruncatedLongSequenceCount = 0)
{
    // 向后兼容既有代码的属性别名
    public double BaselineLogLoss => LastGoodLogLoss;
    public double BaselineBrier => LastGoodBrier;
    public double LogLossImprovement => LastGoodLogLossImprovement;
    public double BrierImprovement => LastGoodBrierImprovement;
}

/// <summary>
/// 留出集评估度量结果。
/// </summary>
public sealed record FsrsEvaluationResult(
    int Samples,
    int Positives,
    int Negatives,
    double LogLoss,
    double Brier);

/// <summary>
/// 优化异常：训练进程失败、超时、格式错误、或留出验证未获可靠提升时抛出。
/// </summary>
public class FsrsOptimizationException : Exception
{
    public FsrsOptimizationException(string message) : base(message) { }
    public FsrsOptimizationException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>
/// 训练器**没有对这批数据作出裁决**：helper 进程没起来、退出码不是协议定义的裁决码、
/// 响应无法解析、响应 metadata 与固定版本/补丁不符、或执行期出现未预期异常。
/// <para>派生自 <see cref="FsrsOptimizationException"/>，因此既有调用方的 <c>catch</c> 与断言不受影响；
/// 区别只体现在集成层的**水位语义**上：没有裁决 = 这批数据从未被评估过，不得因此推进"新增量"水位，
/// 否则一次纯粹的进程/环境故障就会让用户被迫再攒 N 条新目标才能重训。</para>
/// <para>边界：helper 以退出码 3 结束（训练完成但未超越默认权重）是**裁决**，属 <see cref="FsrsOptimizationException"/>。</para>
/// </summary>
public sealed class FsrsOptimizerUnavailableException : FsrsOptimizationException
{
    public FsrsOptimizerUnavailableException(string message) : base(message) { }
    public FsrsOptimizerUnavailableException(string message, Exception? innerException) : base(message, innerException) { }
}
