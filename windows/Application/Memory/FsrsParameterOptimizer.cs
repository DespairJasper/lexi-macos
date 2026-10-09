using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexi;

/// <summary>
/// Personal FSRS-6 parameter training boundary. History must contain the effective,
/// non-invalidated canonical reviews in chronological order for each word.
/// Implementations must validate on held-out later reviews before returning weights.
/// Context residual calibration is a separate model and does not implement this contract.
/// </summary>
public interface IFSRSParameterOptimizer
{
    /// <summary>Whether this implementation can actually train personal parameters.</summary>
    bool IsImplemented { get; }

    /// <summary>Train and validate personal parameters; never silently return defaults.</summary>
    Task<Fsrs6Weights> OptimizeAsync(
        IReadOnlyList<CanonicalReview> history,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 本地个人 FSRS-6 参数训练适配器。
/// 严格契约与安全机制：
/// 1. 结构化受控子进程（UseShellExecute=false, stdin/stdout 严格 JSON 契约, 有限超时清理预算防无界悬挂, RAYON_NUM_THREADS=1）。
/// 2. 严格核验原生 helper 的 response 元数据（version=1, protocol="fsrs-optimizer-v1", algorithm="FSRS-6",
///    model_version="5.2.0", git_sha="aca2838bfbdc6f15ca3f7a0c96a99fae466c9e9c", parameter_count=21, effective_seed=2023）。
/// 3. 全局 splitCutoffUtc 隔离：训练 item 前缀中所有 review 的 ReviewedAtUtc 与 CompletedAtUtc 均必须在 split 过去侧，杜绝跨 cutoff 定稿泄漏。
/// 4. 留出未来验证中，前置状态推进仅允许使用评估时刻前已定稿的历史（CompletedAtUtc &lt;= t_eval）；重叠/含糊序列明确剔除并计数。
/// 5. 候选必须同时与官方 defaults 和 last-good 在同一批留出集上进行双重比较，保存两组 LogLoss/Brier 指标并验证双重改善/防劣化。
/// 6. 严禁任意 Math.Clamp 掩盖超界；仅在 f32 边界极小舍入容差（RoundingTolerance）内明示投影，超界更甚者直接拒绝并抛出异常。
/// 7. 保留完整的最多 64 条训练前缀；超限目标显式排除并计数，未来验证仍用完整历史；总 review cells 与字节数有硬预算。
/// </summary>
public sealed class FsrsParameterOptimizer : IFSRSParameterOptimizer
{
    public const string ExpectedProtocol = "fsrs-optimizer-v1";
    public const string ExpectedAlgorithm = "FSRS-6";
    public const string ExpectedModelVersion = "5.2.0";
    public const string ExpectedGitSha = "aca2838bfbdc6f15ca3f7a0c96a99fae466c9e9c";
    public const int ExpectedParameterCount = 21;
    public const int OfficialApiSeed = 2023;
    public const int ExpectedMaxSequenceLength = 64;
    public const string ExpectedCompatibilityId = "fsrs6-hard-floor-2-v1";
    public const string ExpectedPatchSha256 = "4c52a7583e4c939a93641b1655872acc026186a9b5a5524afd7b83a5d1eed5bb";

    /// <summary>
    /// helper 的"已作出裁决、但训练没有超越默认权重"退出码（原生侧 <c>src/main.rs</c>：
    /// <c>evolved_count == 0</c> → <c>exit(3)</c>）。**只有这个非零退出码**代表这批数据确实被评估过；
    /// 其余非零码（1/2 = 请求契约或输入形状拒绝，崩溃/信号 = 环境故障）都没有产出可信裁决，
    /// 集成层必须据此区分"评估过"与"没评估过"，不能一律吃掉新增量水位。
    /// </summary>
    public const int EvaluatedRejectionExitCode = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsImplemented => true;

    public FsrsOptimizerOptions Options { get; }

    /// <summary>最近一次成功或失败验证的统计指标。</summary>
    public FsrsOptimizationMetrics? LastMetrics { get; private set; }

    /// <summary>可注入的执行器（优先于外部子进程），方便测试或受控调用。</summary>
    public Func<FsrsOptimizerRequest, CancellationToken, Task<FsrsOptimizerResponse>>? TestInvoker { get; set; }

    public FsrsParameterOptimizer() : this(new FsrsOptimizerOptions()) { }

    public FsrsParameterOptimizer(FsrsOptimizerOptions? options)
    {
        Options = options ?? new FsrsOptimizerOptions();
    }

    public FsrsParameterOptimizer(string? helperPath, FsrsOptimizerOptions? options = null)
    {
        Options = options ?? new FsrsOptimizerOptions();
        if (!string.IsNullOrWhiteSpace(helperPath))
        {
            Options.HelperPath = helperPath;
        }
    }

    public async Task<Fsrs6Weights> OptimizeAsync(
        IReadOnlyList<CanonicalReview> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();

        if (history.Count > Options.MaxInputReviews)
        {
            throw new FsrsOptimizationException(
                $"输入历史数量 {history.Count} 超出预算上限 {Options.MaxInputReviews}。");
        }

        // 1. 数据清洗：合法时间校验、过滤失效、Cutoff 截断与去重
        var validReviews = new List<CanonicalReview>(history.Count);
        foreach (var r in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (r.Invalidated) continue;
            if (string.IsNullOrWhiteSpace(r.WordKey)) continue;

            // 保守拒绝未定义时间
            if (r.ReviewedAtUtc == DateTime.MinValue || r.ReviewedAtUtc == DateTime.MaxValue ||
                r.CompletedAtUtc == DateTime.MinValue || r.CompletedAtUtc == DateTime.MaxValue)
            {
                continue;
            }

            if (Options.CompletedAtCutoff.HasValue && r.CompletedAtUtc >= Options.CompletedAtCutoff.Value)
            {
                continue;
            }

            if (r.Rating != StudyRating.Known && r.Rating != StudyRating.Unsure && r.Rating != StudyRating.Forgot)
            {
                throw new ArgumentException($"词卡 '{r.WordKey}' 包含非法的评级: {r.Rating}", nameof(history));
            }

            validReviews.Add(r);
        }

        if (validReviews.Count == 0)
        {
            throw new FsrsOptimizationException("清洗后没有有效的 canonical 复习记录，无法优化。");
        }

        // 去重与稳定排序：按 (WordKey, SessionId) 去重保留最高 Revision，避免 double count
        var deduplicatedBySession = validReviews
            .GroupBy(r => (r.WordKey, r.SessionId))
            .Select(g => g.OrderByDescending(r => r.Revision).ThenByDescending(r => r.CompletedAtUtc).First())
            .ToList();

        // 按词聚合，且词 Key 按 Ordinal 排序保证严格确定性
        var wordGroups = deduplicatedBySession
            .GroupBy(r => r.WordKey)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (
                WordKey: g.Key,
                Reviews: g.OrderBy(r => r.ReviewedAtUtc)
                          .ThenBy(r => r.CanonicalSequence > 0 ? 0 : 1)
                          .ThenBy(r => Math.Max(0, r.CanonicalSequence))
                          .ThenBy(r => r.CanonicalId, StringComparer.Ordinal)
                          .ToList()))
            .ToList();

        // 2. 识别每个词的预测目标（firstlearn 仅初始化，真正 Review 且 delta_t > 0 为 target）
        var allTargets = new List<ReviewTargetInfo>();
        foreach (var word in wordGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reviews = word.Reviews;
            for (var i = 0; i < reviews.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i == 0) continue; // 首条记录仅用于初始化状态

                var r = reviews[i];
                var elapsedDays = Fsrs6Model.ElapsedWholeDays(reviews[i - 1].ReviewedAtUtc, r.ReviewedAtUtc);
                // 必须是真实 Review 且经过至少 1 整天（正间隔）
                if (r.Origin == CanonicalOrigin.FirstRetrieval && elapsedDays > 0)
                {
                    allTargets.Add(new ReviewTargetInfo(word.WordKey, i, r.ReviewedAtUtc, r.CompletedAtUtc, r.Rating, elapsedDays));
                }
            }
        }

        var minRequiredTargets = Options.MinimumTrainReviews + Options.MinimumValidationReviews;
        if (allTargets.Count < minRequiredTargets)
        {
            throw new FsrsOptimizationException(
                $"有效目标复习数量不足 ({allTargets.Count})，至少需要 {minRequiredTargets} 条（训练 {Options.MinimumTrainReviews} + 验证 {Options.MinimumValidationReviews}）。");
        }

        // 3. 全局时间序切分：按时间 tie 严格不拆同 timestamp
        var sortedTargets = allTargets.OrderBy(t => t.ReviewedAtUtc).ToList();
        var idealTrainCount = (int)Math.Floor(sortedTargets.Count * Options.TrainFraction);
        if (idealTrainCount < Options.MinimumTrainReviews) idealTrainCount = Options.MinimumTrainReviews;
        if (sortedTargets.Count - idealTrainCount < Options.MinimumValidationReviews)
        {
            idealTrainCount = sortedTargets.Count - Options.MinimumValidationReviews;
        }

        if (idealTrainCount <= 0 || idealTrainCount >= sortedTargets.Count)
        {
            throw new FsrsOptimizationException("数据切分无法满足最小训练与验证区间。");
        }

        var splitTimeCandidate = sortedTargets[idealTrainCount - 1].ReviewedAtUtc;

        // 同 timestamp 严格归入过去训练段
        var trainTargets = sortedTargets.Where(t => t.ReviewedAtUtc <= splitTimeCandidate).ToList();
        var valTargets = sortedTargets.Where(t => t.ReviewedAtUtc > splitTimeCandidate).ToList();

        if (trainTargets.Count < Options.MinimumTrainReviews || valTargets.Count < Options.MinimumValidationReviews)
        {
            throw new FsrsOptimizationException(
                $"时间戳 tie 保护后训练样本 ({trainTargets.Count}) 或留出验证样本 ({valTargets.Count}) 不足门槛。");
        }

        var splitCutoffUtc = splitTimeCandidate;

        // 4. 构造训练请求载荷：
        // 严格杜绝 future 定稿泄漏：训练 item 前缀中每一条 review 的 ReviewedAtUtc 与 CompletedAtUtc 都必须 <= splitCutoffUtc
        var timedItems = new List<(CanonicalReview Target, FsrsOptimizerItem Item)>();
        var totalReviewCells = 0;
        var excludedLongSequenceTargets = 0;

        foreach (var word in wordGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reviews = word.Reviews;
            var latestPrefixCompletion = reviews[0].CompletedAtUtc;
            for (var i = 1; i < reviews.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetRev = reviews[i];
                if (targetRev.CompletedAtUtc > latestPrefixCompletion) latestPrefixCompletion = targetRev.CompletedAtUtc;
                if (targetRev.ReviewedAtUtc > splitCutoffUtc) break;
                if (targetRev.Origin != CanonicalOrigin.FirstRetrieval) continue;
                if (Fsrs6Model.ElapsedWholeDays(reviews[i - 1].ReviewedAtUtc, targetRev.ReviewedAtUtc) <= 0) continue;

                // 最长序列先过滤；累计最大定稿时间覆盖整个前缀，不做 O(n²) 扫描。
                if (i + 1 > Options.MaxSequenceLength)
                {
                    excludedLongSequenceTargets++;
                    continue;
                }
                if (latestPrefixCompletion > splitCutoffUtc) continue;

                var item = new FsrsOptimizerItem();
                for (var j = 0; j <= i; j++)
                {
                    uint deltaT = (j == 0)
                        ? 0
                        : (uint)Math.Max(0, Fsrs6Model.ElapsedWholeDays(reviews[j - 1].ReviewedAtUtc, reviews[j].ReviewedAtUtc));

                    item.Reviews.Add(new FsrsOptimizerReview
                    {
                        Rating = Fsrs6Model.ToFsrsRating(reviews[j].Rating),
                        DeltaT = deltaT,
                    });
                }

                timedItems.Add((targetRev, item));
                totalReviewCells += item.Reviews.Count;

                if (totalReviewCells > Options.MaxTotalReviewCells)
                {
                    throw new FsrsOptimizationException(
                        $"训练 item 总 review cells 达到 {totalReviewCells}，超出预算上限 {Options.MaxTotalReviewCells}。");
                }

                if (timedItems.Count > Options.MaxItemsBudget)
                {
                    throw new FsrsOptimizationException(
                        $"生成的训练 item 数量超过预算上限 {Options.MaxItemsBudget}。");
                }
            }
        }

        // 上游按输入位置分配近期权重，必须全局按目标时间排序。
        cancellationToken.ThrowIfCancellationRequested();
        var items = timedItems.OrderBy(x => x.Target.ReviewedAtUtc)
            .ThenBy(x => x.Target.CanonicalSequence > 0 ? 0 : 1)
            .ThenBy(x => Math.Max(0, x.Target.CanonicalSequence))
            .ThenBy(x => x.Target.CanonicalId, StringComparer.Ordinal)
            .Select(x => x.Item).ToList();

        if (items.Count < Options.MinimumTrainReviews)
        {
            throw new FsrsOptimizationException(
                $"有效训练样本数量不足 ({items.Count})，排除超过最大序列长度 {Options.MaxSequenceLength} 的目标 {excludedLongSequenceTargets} 条后未达最小训练门槛 {Options.MinimumTrainReviews}。");
        }

        var request = new FsrsOptimizerRequest
        {
            Version = 1,
            Algorithm = ExpectedAlgorithm,
            EnableShortTerm = true,
            Threads = 1,
            Seed = OfficialApiSeed,
            Items = items,
        };

        var requestJsonBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (requestJsonBytes.Length > Options.MaxSerializedInputBytes)
        {
            throw new FsrsOptimizationException(
                $"序列化请求大小 {requestJsonBytes.Length} 字节超出预算上限 {Options.MaxSerializedInputBytes} 字节。");
        }

        // 5. 调用子进程或测试注入执行器
        FsrsOptimizerResponse response;
        if (TestInvoker is not null)
        {
            response = await TestInvoker(request, cancellationToken);
        }
        else
        {
            response = await ExecuteHelperProcessAsync(requestJsonBytes, cancellationToken);
        }

        // 严格核验原生 helper response metadata
        VerifyHelperResponseMetadata(response);

        // 6. 校验 21 维有限值；严格拒绝超界，仅允许极小舍入容差内的明示投影
        var candidateArray = new double[ExpectedParameterCount];
        for (var i = 0; i < ExpectedParameterCount; i++)
        {
            var w = response.Weights![i];
            if (double.IsNaN(w) || double.IsInfinity(w))
            {
                throw new FsrsOptimizationException($"候选权重 w{i} 非有限数值: {w}");
            }

            var (lower, upper) = Fsrs6Weights.OfficialClipBounds[i];
            // 官方训练使用 f32；预算最多两个相邻 f32 ULP，配置只能进一步收紧。
            var lowerF32 = (float)lower;
            var upperF32 = (float)upper;
            var lowerTolerance = Math.Min(Options.RoundingTolerance,
                2.0 * Math.Max(Math.Abs((double)float.BitIncrement(lowerF32) - lowerF32),
                    Math.Abs((double)lowerF32 - float.BitDecrement(lowerF32))));
            var upperTolerance = Math.Min(Options.RoundingTolerance,
                2.0 * Math.Max(Math.Abs((double)float.BitIncrement(upperF32) - upperF32),
                    Math.Abs((double)upperF32 - float.BitDecrement(upperF32))));
            if (w < lower - lowerTolerance || w > upper + upperTolerance)
            {
                throw new FsrsOptimizationException(
                    $"候选权重 w{i}={w:R} 严格超出官方 clip 区间 [{lower:R}, {upper:R}]（超出 f32 边界容差 [{lowerTolerance:R}, {upperTolerance:R}]）。拒绝采用损坏响应。");
            }

            // 仅在极小舍入误差范围内投影
            candidateArray[i] = Math.Clamp(w, lower, upper);
        }

        if (!Fsrs6Weights.TryValidate(candidateArray, out var valError))
        {
            throw new FsrsOptimizationException($"候选权重校验失败: {valError}");
        }

        var candidateWeights = Fsrs6Weights.Create(candidateArray, Fsrs6Weights.SourceOptimized, DateTime.UtcNow);
        var defaultsWeights = Fsrs6Weights.Defaults;
        var lastGoodWeights = Options.LastGoodWeights ?? defaultsWeights;

        // 7. 在留出未来序列上推进卡片状态评估（同时评估 Defaults、LastGood 与 Candidate）
        var candidateScheduler = new Fsrs6Scheduler(candidateWeights);
        var defaultsScheduler = new Fsrs6Scheduler(defaultsWeights);
        var lastGoodScheduler = new Fsrs6Scheduler(lastGoodWeights);

        var candPredictions = new List<(double R, int Label)>(valTargets.Count);
        var defaultsPredictions = new List<(double R, int Label)>(valTargets.Count);
        var lastGoodPredictions = new List<(double R, int Label)>(valTargets.Count);

        var valTargetLookup = new HashSet<(string WordKey, int ReviewIndex)>(
            valTargets.Select(t => (t.WordKey, t.ReviewIndex)));

        var ambiguousOrOverlappingSequences = 0;

        foreach (var word in wordGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reviews = word.Reviews;
            FsrsCardState? candCard = null;
            FsrsCardState? defCard = null;
            FsrsCardState? lgCard = null;
            var latestPriorCompletion = DateTime.MinValue;

            for (var i = 0; i < reviews.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var r = reviews[i];
                var isValTarget = valTargetLookup.Contains((word.WordKey, i));

                if (isValTarget)
                {
                    // 累计最大定稿时刻：与逐条检查前置标签可见性等价。
                    var priorReviewIncomplete = latestPriorCompletion > r.ReviewedAtUtc;

                    if (priorReviewIncomplete)
                    {
                        // 前置定稿尚未完成，剔除此目标并计数，不可喂未来标签
                        ambiguousOrOverlappingSequences++;
                    }
                    else
                    {
                        var elapsedDays = Fsrs6Model.ElapsedWholeDays(reviews[i - 1].ReviewedAtUtc, r.ReviewedAtUtc);
                        var candR = candidateScheduler.Retrievability(candCard!.Stability, elapsedDays);
                        var defR = defaultsScheduler.Retrievability(defCard!.Stability, elapsedDays);
                        var lgR = lastGoodScheduler.Retrievability(lgCard!.Stability, elapsedDays);

                        var label = (r.Rating == StudyRating.Known || r.Rating == StudyRating.Unsure) ? 1 : 0;
                        candPredictions.Add((candR, label));
                        defaultsPredictions.Add((defR, label));
                        lastGoodPredictions.Add((lgR, label));
                    }
                }

                if (r.CompletedAtUtc > latestPriorCompletion) latestPriorCompletion = r.CompletedAtUtc;
                // 推进卡片状态
                candCard = candidateScheduler.Review(candCard, r.Rating, r.ReviewedAtUtc).Card;
                defCard = defaultsScheduler.Review(defCard, r.Rating, r.ReviewedAtUtc).Card;
                lgCard = lastGoodScheduler.Review(lgCard, r.Rating, r.ReviewedAtUtc).Card;
            }
        }

        // 8. 留出验证门槛检验与双基线指标计算
        var valSampleCount = candPredictions.Count;
        var positives = candPredictions.Count(p => p.Label == 1);
        var negatives = candPredictions.Count(p => p.Label == 0);

        if (valSampleCount < Options.MinimumValidationReviews ||
            positives < Options.MinimumPositiveReviews ||
            negatives < Options.MinimumNegativeReviews)
        {
            throw new FsrsOptimizationException(
                $"留出验证集样本类别平衡未达标: 样本={valSampleCount}(最少{Options.MinimumValidationReviews}), 正例={positives}(最少{Options.MinimumPositiveReviews}), 负例={negatives}(最少{Options.MinimumNegativeReviews})。");
        }

        var (candLogLoss, candBrier) = ComputeMetrics(candPredictions);
        var (defLogLoss, defBrier) = ComputeMetrics(defaultsPredictions);
        var (lgLogLoss, lgBrier) = ComputeMetrics(lastGoodPredictions);

        var defLogLossImp = defLogLoss - candLogLoss;
        var defBrierImp = defBrier - candBrier;

        var lgLogLossImp = lgLogLoss - candLogLoss;
        var lgBrierImp = lgBrier - candBrier;

        var compatId = response.CompatibilityId ?? response.CompatibilityRevision;
        LastMetrics = new FsrsOptimizationMetrics(
            TotalReviews: history.Count,
            ValidatedReviews: validReviews.Count,
            TrainReviews: items.Count,
            ValidationReviews: valSampleCount,
            ValidationPositives: positives,
            ValidationNegatives: negatives,
            DefaultsLogLoss: defLogLoss,
            DefaultsBrier: defBrier,
            DefaultsLogLossImprovement: defLogLossImp,
            DefaultsBrierImprovement: defBrierImp,
            LastGoodLogLoss: lgLogLoss,
            LastGoodBrier: lgBrier,
            LastGoodLogLossImprovement: lgLogLossImp,
            LastGoodBrierImprovement: lgBrierImp,
            CandidateLogLoss: candLogLoss,
            CandidateBrier: candBrier,
            HelperVersion: response.ModelVersion,
            HelperSha: response.GitSha,
            EffectiveSeed: response.EffectiveSeed!.Value,
            Duration: stopwatch.Elapsed,
            ExcludedLongSequenceTargetCount: excludedLongSequenceTargets,
            AmbiguousOrOverlappingSequenceCount: ambiguousOrOverlappingSequences,
            CompatibilityId: compatId,
            PatchSha256: response.PatchSha256,
            MaxSequenceLength: response.MaxSequenceLength!.Value,
            TruncatedLongSequenceCount: excludedLongSequenceTargets);

        // 改善判定：对 Defaults 必须满足最小改善门槛
        if (defLogLossImp < Options.MinimumLogLossImprovement || defBrierImp < Options.MinimumBrierImprovement)
        {
            throw new FsrsOptimizationException(
                $"候选参数未能超越官方 Defaults 基线。Defaults LogLoss 改善: {defLogLossImp:F6} (门槛 {Options.MinimumLogLossImprovement:F6}), Brier 改善: {defBrierImp:F6} (门槛 {Options.MinimumBrierImprovement:F6})。拒绝采用候选参数。");
        }

        // 若存在与 Defaults 不同的 LastGood 权重，候选还必须防劣化（不得比 LastGood 更差）
        if (Options.LastGoodWeights is not null && !Options.LastGoodWeights.ValueEquals(defaultsWeights))
        {
            if (lgLogLossImp < 0.0 || lgBrierImp < 0.0)
            {
                throw new FsrsOptimizationException(
                    $"候选参数未能超越 LastGood 基线（发生性能劣化）。LastGood LogLoss 改善: {lgLogLossImp:F6}, Brier 改善: {lgBrierImp:F6}。拒绝采用候选参数。");
            }
        }

        return candidateWeights;
    }

    private static void VerifyHelperResponseMetadata(FsrsOptimizerResponse response)
    {
        if (response.Version != 1)
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 version 错误: {response.Version} (预期 1)");
        }

        if (!string.Equals(response.Protocol, ExpectedProtocol, StringComparison.Ordinal))
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 protocol 不匹配: '{response.Protocol}' (预期 '{ExpectedProtocol}')");
        }

        if (!string.Equals(response.Algorithm, ExpectedAlgorithm, StringComparison.Ordinal))
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 algorithm 不匹配: '{response.Algorithm}' (预期 '{ExpectedAlgorithm}')");
        }

        if (!string.Equals(response.ModelVersion, ExpectedModelVersion, StringComparison.Ordinal))
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 model_version 不匹配: '{response.ModelVersion}' (预期 '{ExpectedModelVersion}')");
        }

        if (!string.Equals(response.GitSha, ExpectedGitSha, StringComparison.Ordinal))
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 git_sha 不匹配: '{response.GitSha}' (预期 '{ExpectedGitSha}')");
        }

        if (response.ParameterCount != ExpectedParameterCount)
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 parameter_count 错误: {response.ParameterCount} (预期 {ExpectedParameterCount})");
        }

        if (response.Weights is null || response.Weights.Length != ExpectedParameterCount)
        {
            throw new FsrsOptimizerUnavailableException(
                $"Helper 响应 weights 数组非法（长度 {response.Weights?.Length ?? 0}，预期 {ExpectedParameterCount}）。");
        }

        // 契约核验：严格核验 effective_seed、max_sequence_length、compatibility 补丁标识与 patch_sha256
        if (!response.EffectiveSeed.HasValue)
        {
            throw new FsrsOptimizerUnavailableException("Helper 响应缺失 effective_seed 字段。");
        }
        if (response.EffectiveSeed.Value != OfficialApiSeed)
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 effective_seed 错误: {response.EffectiveSeed.Value} (预期 {OfficialApiSeed})");
        }

        if (!response.MaxSequenceLength.HasValue)
        {
            throw new FsrsOptimizerUnavailableException("Helper 响应缺失 max_sequence_length 字段。");
        }
        if (response.MaxSequenceLength.Value != ExpectedMaxSequenceLength)
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 max_sequence_length 错误: {response.MaxSequenceLength.Value} (预期 {ExpectedMaxSequenceLength})");
        }

        var compatId = response.CompatibilityId ?? response.CompatibilityRevision;
        if (string.IsNullOrWhiteSpace(compatId))
        {
            throw new FsrsOptimizerUnavailableException("Helper 响应缺失 compatibility 兼容补丁标识。旧版未打补丁 helper 拒绝使用。");
        }
        if (!string.Equals(compatId, ExpectedCompatibilityId, StringComparison.Ordinal))
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 compatibility_id '{compatId}' 与预期 '{ExpectedCompatibilityId}' 不匹配。");
        }

        if (string.IsNullOrWhiteSpace(response.PatchSha256))
        {
            throw new FsrsOptimizerUnavailableException("Helper 响应缺失 patch_sha256 字段。旧版未打补丁 helper 拒绝使用。");
        }
        if (!string.Equals(response.PatchSha256, ExpectedPatchSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FsrsOptimizerUnavailableException($"Helper 响应 patch_sha256 '{response.PatchSha256}' 与预期 '{ExpectedPatchSha256}' 不匹配。");
        }
    }

    private async Task<FsrsOptimizerResponse> ExecuteHelperProcessAsync(
        byte[] requestJsonBytes,
        CancellationToken cancellationToken)
    {
        var helperPath = ResolveHelperPath();
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
        {
            throw new FileNotFoundException(
                $"FSRS optimizer helper 可执行文件不存在。路径: '{helperPath ?? "<null>"}'",
                helperPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.Environment["RAYON_NUM_THREADS"] = "1";
        psi.Environment["RUST_BACKTRACE"] = "1";

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = new CancellationTokenSource(Options.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        var token = linkedCts.Token;

        try
        {
            if (!process.Start())
            {
                throw new FsrsOptimizerUnavailableException($"未能启动 helper 进程: {helperPath}");
            }
        }
        catch (Exception ex) when (ex is not FsrsOptimizationException)
        {
            throw new FsrsOptimizerUnavailableException($"启动 helper 进程异常: {ex.Message}", ex);
        }

        // 正常退出时同步取消必须立即终止 helper，不依赖 UI 的异步清理续体。
        using var killOnCancellation = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

        Exception? pipeError = null;
        async Task<byte[]> ReadGuardedAsync(Stream stream, int limit)
        {
            try { return await ReadStreamWithLimitAsync(stream, limit, token); }
            catch (FsrsOptimizationException ex)
            {
                Interlocked.CompareExchange(ref pipeError, ex, null);
                linkedCts.Cancel();
                throw;
            }
        }
        try
        {
            var stdoutTask = ReadGuardedAsync(process.StandardOutput.BaseStream, Options.MaxStdoutBytes);
            var stderrTask = ReadGuardedAsync(process.StandardError.BaseStream, Options.MaxStderrBytes);

            await process.StandardInput.BaseStream.WriteAsync(requestJsonBytes, token);
            await process.StandardInput.BaseStream.FlushAsync(token);
            process.StandardInput.Close();

            var stdoutBytes = await stdoutTask;
            var stderrBytes = await stderrTask;

            await process.WaitForExitAsync(token);

            if (process.ExitCode != 0)
            {
                var errText = System.Text.Encoding.UTF8.GetString(stderrBytes);
                // 只有协议定义的裁决码（exit 3 = "训练完成但未超越默认权重"）代表数据被评估过；
                // 其它非零码一律按"未产出裁决"处理，不消耗新增量水位。
                if (process.ExitCode != EvaluatedRejectionExitCode)
                {
                    throw new FsrsOptimizerUnavailableException(
                        $"FSRS optimizer helper 未产出裁决（退出码 {process.ExitCode}）: {errText}");
                }
                throw new FsrsOptimizationException(
                    $"FSRS optimizer helper 退出码非零 ({process.ExitCode}): {errText}");
            }

            var response = JsonSerializer.Deserialize<FsrsOptimizerResponse>(stdoutBytes, JsonOptions);
            if (response is null)
            {
                throw new FsrsOptimizerUnavailableException("解析 helper stdout JSON 失败，反序列化为 null。");
            }
            return response;
        }
        catch (OperationCanceledException)
        {
            await CleanupProcessTreeAsync(process, Options.CleanupWaitBudget);

            if (pipeError is { } exceeded) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exceeded).Throw();

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"FSRS optimizer helper 超时 ({Options.Timeout.TotalSeconds}s)。已受控终止子进程。");
            }
            throw;
        }
        catch (Exception ex)
        {
            await CleanupProcessTreeAsync(process, Options.CleanupWaitBudget);
            if (ex is FsrsOptimizationException or TimeoutException or FileNotFoundException) throw;
            throw new FsrsOptimizerUnavailableException($"执行 optimizer helper 异常: {ex.Message}", ex);
        }
    }

    private string? ResolveHelperPath()
    {
        if (!string.IsNullOrWhiteSpace(Options.HelperPath))
        {
            return Options.HelperPath;
        }

        var defaultProductionPath = Path.Combine(AppContext.BaseDirectory, "fsrs-optimizer");
        if (File.Exists(defaultProductionPath))
        {
            return defaultProductionPath;
        }

        var env = Environment.GetEnvironmentVariable("LEXI_FSRS_OPTIMIZER_BIN");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../native/fsrs-optimizer/bin/fsrs-optimizer")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../native/fsrs-optimizer/bin/fsrs-optimizer")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../native/fsrs-optimizer/bin/fsrs-optimizer")),
        };

        var found = candidates.FirstOrDefault(File.Exists);
        return found ?? defaultProductionPath;
    }

    private static async Task CleanupProcessTreeAsync(Process process, TimeSpan cleanupBudget)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略进程退出竞争
        }

        try
        {
            using var waitCts = new CancellationTokenSource(cleanupBudget);
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch
        {
            // 清理超时绝不无界等待
        }
    }

    private static async Task<byte[]> ReadStreamWithLimitAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                throw new FsrsOptimizerUnavailableException($"Helper 进程管道输出超过字节预算上限 ({maxBytes} bytes)。");
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static (double LogLoss, double Brier) ComputeMetrics(List<(double R, int Label)> predictions)
    {
        double totalLogLoss = 0;
        double totalBrier = 0;
        foreach (var (r, y) in predictions)
        {
            var p = Math.Clamp(r, 1e-7, 1.0 - 1e-7);
            totalLogLoss += -(y * Math.Log(p) + (1 - y) * Math.Log(1 - p));
            var diff = p - y;
            totalBrier += diff * diff;
        }
        return (totalLogLoss / predictions.Count, totalBrier / predictions.Count);
    }

    private readonly record struct ReviewTargetInfo(
        string WordKey, int ReviewIndex, DateTime ReviewedAtUtc, DateTime CompletedAtUtc, StudyRating Rating, long ElapsedDays);
}
