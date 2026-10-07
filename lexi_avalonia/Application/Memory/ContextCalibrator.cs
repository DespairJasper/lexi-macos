using System.Text.Json;

namespace Lexi;

/// <summary>Context 校准器的可配置项。**门槛常量一律来自 <see cref="SchedulingConfig"/>**，这里只放开关与审计字段。</summary>
public sealed record ContextCalibratorOptions
{
    /// <summary>总开关；默认取 <see cref="SchedulingConfig.ContextEnabled"/>。关闭时 <see cref="ContextMode.Disabled"/> 且一律不校准。</summary>
    public bool Enabled { get; init; } = SchedulingConfig.ContextEnabled;

    /// <summary>训练随机种子（可配置、可审计）。</summary>
    public int Seed { get; init; } = SchedulingConfig.ContextSeed;

    /// <summary>单次训练墙钟上限；默认 <see cref="SchedulingConfig.ContextTrainingTimeoutSeconds"/>。</summary>
    public TimeSpan TrainingTimeout { get; init; } = TimeSpan.FromSeconds(SchedulingConfig.ContextTrainingTimeoutSeconds);
}

/// <summary>
/// 冻结的标准化器：均值 / 标准差 / 缺失填充值。**只在训练段拟合**，随后冻结应用到验证段与未来。
/// 验证段（以及任何生产样本）绝不允许参与均值/方差的计算——这是「训练期拟合」可测的含义。
/// </summary>
public sealed class ContextFeatureScaler
{
    /// <summary>标准差下界；低于它视为常量特征（<c>std := 1</c>，标准化后恒为 0）。</summary>
    private const double MinimumStd = 1e-9;

    public double[] Means { get; }
    public double[] StandardDeviations { get; }
    public double[] Fills { get; }

    private ContextFeatureScaler(double[] means, double[] standardDeviations, double[] fills)
    {
        Means = means;
        StandardDeviations = standardDeviations;
        Fills = fills;
    }

    public int Length => Means.Length;

    /// <summary>只在**训练段**拟合：缺失值不参与均值/方差（它们被填充值代替，标准化后为 0）。</summary>
    public static ContextFeatureScaler Fit(IReadOnlyList<double[]> values, IReadOnlyList<bool[]> missing, int dimension)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(missing);
        if (values.Count != missing.Count) throw new ArgumentException("values 与 missing 长度不一致。", nameof(missing));
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));

        var fills = new double[dimension];
        for (var j = 0; j < dimension; j++)
        {
            double sum = 0;
            var count = 0;
            for (var i = 0; i < values.Count; i++)
            {
                var v = values[i][j];
                if (missing[i][j] || !MemoryScheduler.IsFinite(v)) continue;
                sum += v;
                count++;
            }
            fills[j] = count > 0 ? sum / count : 0.0;
        }

        var means = new double[dimension];
        var stds = new double[dimension];
        for (var j = 0; j < dimension; j++)
        {
            double sum = 0, sumSquares = 0;
            for (var i = 0; i < values.Count; i++)
            {
                var v = missing[i][j] || !MemoryScheduler.IsFinite(values[i][j]) ? fills[j] : values[i][j];
                sum += v;
                sumSquares += v * v;
            }
            var n = Math.Max(1, values.Count);
            var mean = sum / n;
            var variance = Math.Max(0.0, sumSquares / n - mean * mean);
            var std = Math.Sqrt(variance);
            if (!MemoryScheduler.IsFinite(std) || std < MinimumStd)
            {
                std = 1.0;
                mean = fills[j]; // 常量特征：标准化后恒 0，模型不会给它权重。
            }
            means[j] = mean;
            stds[j] = std;
        }

        return new ContextFeatureScaler(means, stds, fills);
    }

    /// <summary>把原始特征标准化。缺失（或非有限）→ 用训练期填充值，再按训练期均值/标准差缩放。</summary>
    public double[] Standardize(IReadOnlyList<double> values, IReadOnlyList<bool>? missing)
    {
        if (values is null || values.Count != Length)
            throw new InvalidDataException($"特征维数 {values?.Count ?? -1} 与冻结标准化器的 {Length} 不一致。");
        var z = new double[Length];
        for (var j = 0; j < Length; j++)
        {
            var raw = values[j];
            var isMissing = (missing is not null && j < missing.Count && missing[j]) || !MemoryScheduler.IsFinite(raw);
            var v = isMissing ? Fills[j] : raw;
            z[j] = (v - Means[j]) / StandardDeviations[j];
        }
        return z;
    }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["featureSchemaVersion"] = SchedulingConfig.ContextFeatureSchemaVersion,
        ["means"] = Means,
        ["standardDeviations"] = StandardDeviations,
        ["fills"] = Fills,
    });

    public static ContextFeatureScaler? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 根不是对象（例如 `[]` / `[0.1,0.2]`）时 TryGetProperty 会抛 InvalidOperationException，
            // 而这里只捕获了 JsonException——于是一个"形状对不上"的状态行会把异常一路抛到
            // ContextCalibrator 的构造函数，再被 MainWindow 的兜底 catch 变成**整个长期记忆层停用**。
            // 形状不符只应该是"这份模型不可用"，不是"这一层坏了"。
            if (root.ValueKind != JsonValueKind.Object) return null;
            var means = ReadArray(root, "means");
            var stds = ReadArray(root, "standardDeviations");
            var fills = ReadArray(root, "fills");
            if (means is null || stds is null || fills is null) return null;
            if (means.Length != stds.Length || means.Length != fills.Length) return null;
            return new ContextFeatureScaler(means, stds, fills);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double[]? ReadArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array) return null;
        var list = new List<double>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number) return null;
            list.Add(item.GetDouble());
        }
        return [.. list];
    }
}

/// <summary>
/// 残差校准模型：<c>δ = β0 + βᵀZ</c>（Z 为标准化后的特征）。**不含 FSRS 结构**——
/// FSRS 的 log-odds 是训练与推理时都固定加在它前面的偏移量（见 <see cref="ContextCalibrator"/>）。
/// </summary>
public sealed class ContextLinearModel
{
    public string Version { get; }
    public double Intercept { get; }
    public double[] Weights { get; }

    public ContextLinearModel(string version, double intercept, double[] weights)
    {
        Version = version ?? "";
        Intercept = intercept;
        Weights = weights ?? [];
    }

    /// <summary>δ = β0 + βᵀZ。</summary>
    public double Score(IReadOnlyList<double> standardized)
    {
        if (standardized.Count != Weights.Length) throw new InvalidDataException("标准化向量维数与模型系数不一致。");
        var score = Intercept;
        for (var j = 0; j < Weights.Length; j++) score += Weights[j] * standardized[j];
        return score;
    }

    /// <summary>长度匹配且全部系数有限，才允许使用。</summary>
    public bool IsUsable(int dimension) =>
        Weights.Length == dimension
        && MemoryScheduler.IsFinite(Intercept)
        && Weights.All(MemoryScheduler.IsFinite);

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["version"] = Version,
        ["intercept"] = Intercept,
        ["weights"] = Weights,
    });

    public static ContextLinearModel? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null; // 同上：形状不符 = 不可用，不是崩
            if (!root.TryGetProperty("weights", out var weightsElement) || weightsElement.ValueKind != JsonValueKind.Array) return null;
            var weights = new double[weightsElement.GetArrayLength()];
            var i = 0;
            foreach (var item in weightsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number) return null;
                weights[i++] = item.GetDouble();
            }
            var intercept = root.TryGetProperty("intercept", out var bi) && bi.ValueKind == JsonValueKind.Number ? bi.GetDouble() : double.NaN;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new ContextLinearModel(version, intercept, weights);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>一段固定数据上的冻结评估（样本数 / 正负例 / LogLoss / Brier）。</summary>
public sealed record ContextEvaluation(int Samples, int Positives, int Negatives, double LogLoss, double Brier);

/// <summary>进入 Active 所需的**独立确认段**结果（该段数据在资格判定时还不存在）。</summary>
public sealed record ContextConfirmation(int Samples, bool Passed, double LogLossImprovement, double BrierImprovement);

/// <summary>一轮训练的完整产物（同时是 MetricsJson 的来源）。</summary>
public sealed record ContextTrainingArtifacts(
    ContextLinearModel Model,
    ContextFeatureScaler Scaler,
    DateTime CutoffUtc,
    int TotalSamples,
    int EvaluableSamples,
    int DroppedSamples,
    int TrainSamples,
    int ValidationSamples,
    int Positives,
    int Negatives,
    double SpanDays,
    ContextEvaluation Train,
    ContextEvaluation Baseline,
    ContextEvaluation Context,
    bool GatePassed,
    ContextConfirmation? Confirmation,
    double MaxAbsDelta,
    double[][] FrozenPredictions)
{
    /// <summary>验证段（**未来**）上 LogLoss 的改善量；正值 = Context 更好。</summary>
    public double LogLossImprovement => Baseline.LogLoss - Context.LogLoss;

    /// <summary>验证段（**未来**）上 Brier 的改善量；正值 = Context 更好。</summary>
    public double BrierImprovement => Baseline.Brier - Context.Brier;
}

/// <summary>
/// Context 校准器（P6）：带 L2 正则的 logistic regression 残差校准，纯 C# 手写、无外部包、无云服务、数据不离开本机。
///
/// <para><b>模型</b>：<c>δ = β0 + βᵀZ</c>，<c>R_adjusted = sigmoid(logit(R_fsrs) + δ)</c>。
/// FSRS 是**结构 baseline**：训练时 <c>logit(R_i)</c> 作为固定偏移量进入线性打分，
/// 模型只学残差；推理时同一个偏移量原样加回。因此 Context 在结构上不可能「黑盒替换 FSRS」——
/// 它能做的只是把 FSRS 的 log-odds 推高或推低 δ。</para>
///
/// <para><b>三重安全边界</b>：① <c>|δ| ≤ SchedulingConfig.ContextMaxAbsDelta</c>（极端系数即拒绝该模型）；
/// ② 只有 <see cref="ContextMode.Active"/> 才可能产生非零 δ；③ 求出的候选间隔还要被
/// <see cref="MemoryScheduler.ResolveFinalInterval"/> 夹在 <c>[0.5, 1.5] × baseline</c> 内。</para>
///
/// <para><b>时间</b>：本层**不读时钟**。训练入口的「现在」由参数传入，样本的时间界限来自快照的
/// <see cref="ContextFeatureSnapshot.CapturedAtUtc"/>。因此「训练过去、验证未来」是可测的：
/// 训练段最大时刻严格小于验证段最小（见 <see cref="ContextTrainingArtifacts.CutoffUtc"/>）。</para>
///
/// <para><b>线程与取消</b>：所有 store 访问都在**调用线程**上完成（<c>VocabularyService</c> 只用一条
/// <c>SqliteConnection</c> 且不加锁，跨线程并发访问是不安全的）；只有纯数值部分放进
/// <c>Task.Run</c>，并带 <see cref="CancellationToken"/> 与墙钟超时。</para>
/// </summary>
public sealed class ContextCalibrator : IContextCalibrator
{
    /// <summary>logit/sigmoid 的数值钳制：R 恰好为 0 或 1 时 logit 会溢出，必须夹住。</summary>
    private const double ProbabilityEpsilon = 1e-6;

    /// <summary>logistic 回归训练的最少样本（一段至少要有这么多条才值得拟合）。</summary>
    private const int MinimumFittableSamples = 20;
    private const string EvaluationProtocol = "published-frozen-v2";

    private readonly object _gate = new();
    private readonly ILearningMemoryStore _store;
    private readonly IMemoryScheduler _scheduler;
    private readonly ContextCalibratorOptions _options;

    private ContextMode _mode;
    private ContextFeatureScaler? _scaler;
    private ContextLinearModel? _model;
    private DateTime? _pendingActivationCutoffUtc;
    private DateTime? _lastTrainedAtUtc;
    private DateTime _lastCutoffUtc;
    private string? _lastGoodModelVersion;
    private bool _training;
    private string _lastDetail = "";

    /// <summary>当前生效的 FSRS 参数版本戳（Context 模型的基线绑定依据）。</summary>
    private string _fsrsParameterVersion = SchedulingConfig.FsrsParameterVersionPrefix;

    /// <summary>当前 FSRS 基线的生效时刻；null = 官方 defaults 基线（不参与样本过滤）。</summary>
    private DateTime? _fsrsBaselineSinceUtc;

    /// <summary>
    /// 基线世代号：每次 <see cref="ApplyFsrsBaseline"/> 自增。
    /// 在途训练在开始时拍下它，收尾时若发现已变，就**丢弃本轮结果且不落库**——
    /// 否则一个在换版前启动、换版后才回来的训练会把旧基线下拟合的模型写回去，
    /// 正好覆盖掉刚刚完成的降资格。
    /// </summary>
    private long _fsrsBaselineGeneration;

    public ContextCalibrator(ILearningMemoryStore store, IMemoryScheduler scheduler, ContextCalibratorOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _options = options ?? new ContextCalibratorOptions();
        _mode = _options.Enabled ? ContextMode.ColdStart : ContextMode.Disabled;
        LoadFsrsBaseline();
        RestoreFromStore();
    }

    /// <summary>可调开关：调用方显式传 <see cref="ContextCalibratorOptions.Enabled"/> = false 即整体关闭。</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>最近一次训练 / 资格推进的可读原因（审计用，不参与任何数值判断）。</summary>
    public string LastTrainingDetail { get { lock (_gate) return _lastDetail; } }

    /// <summary>当前绑定的 FSRS 参数版本戳（换版后即为新版本）。只读，供集成层与测试断言。</summary>
    public string FsrsBaselineVersionProbe { get { lock (_gate) return _fsrsParameterVersion; } }

    /// <summary>当前 FSRS 基线的生效时刻；null = 官方 defaults 基线。</summary>
    public DateTime? FsrsBaselineSinceProbe { get { lock (_gate) return _fsrsBaselineSinceUtc; } }

    /// <summary>当前冻结的标准化器（训练期拟合）；未训练时为 null。</summary>
    public ContextFeatureScaler? FrozenScaler { get { lock (_gate) return _scaler; } }

    /// <summary>当前可用的残差模型；未训练 / 已损坏时为 null。</summary>
    public ContextLinearModel? CurrentModel { get { lock (_gate) return _model; } }

    /// <inheritdoc />
    public ContextMode Mode { get { lock (_gate) return _mode; } }

    // ==================================================================================
    // 状态恢复
    // ==================================================================================

    /// <summary>
    /// 从 <see cref="PersonalizationModelState"/> 恢复。模型损坏 / 特征 schema 换版 /
    /// 维数不匹配 → **不复用**并退回 ColdStart（下次训练重新过资格门槛，见用户需求 §8）。
    /// </summary>
    private void RestoreFromStore()
    {
        PersonalizationModelState? state;
        try { state = _store.GetPersonalizationModel(); }
        catch (Exception ex) { _lastDetail = "个人化模型读取失败：" + ex.Message; return; }
        if (state is null) return;

        _lastTrainedAtUtc = state.TrainedAtUtc;
        _lastCutoffUtc = state.TrainCutoffUtc;
        _lastGoodModelVersion = state.LastGoodModelVersion;
        if (!_options.Enabled) return;

        ContextLinearModel? model;
        ContextFeatureScaler? scaler;
        bool schemaOk;
        string recordedBaseline;
        try
        {
            model = ContextLinearModel.FromJson(state.CoefficientsJson);
            scaler = ContextFeatureScaler.FromJson(state.ScalerJson);
            schemaOk = ReadSchemaVersion(state.MetricsJson) == SchedulingConfig.ContextFeatureSchemaVersion;
            recordedBaseline = ReadMetricString(state.MetricsJson, SchedulingConfig.FsrsParameterVersionMetricKey)
                ?? SchedulingConfig.FsrsParameterVersionPrefix;
        }
        catch (Exception ex)
        {
            // 兜底：解析路径上任何未预期的异常都只意味着"这份模型不可用"。
            // 让它冒泡到构造函数之外会一路被 MainWindow 的兜底 catch 变成"整层长期记忆停用"——
            // 一条坏状态行不该有这种杀伤力。
            _mode = ContextMode.ColdStart;
            _lastDetail = $"已存的 Context 模型解析失败（{ex.GetType().Name}），退回 ColdStart 重新过资格。";
            return;
        }

        if (model is null || scaler is null || !model.IsUsable(ContextFeatureProvider.FeatureCount)
            || scaler.Length != ContextFeatureProvider.FeatureCount || !schemaOk)
        {
            _mode = ContextMode.ColdStart;
            _lastDetail = "已存的 Context 模型不可用（损坏 / 换版 / 维数不匹配），退回 ColdStart 重新过资格。";
            return;
        }

        // —— 基线绑定：模型是在**哪一套 FSRS 权重**下学出来的 ——
        // 权重换版后历史快照里的 R 全部来自旧基线，继续沿用这个模型等于让 δ 对应两个基线。
        // 缺失该键的行按冻结默认版本解释（换版前落库的行全是默认基线下训练的）。
        if (!string.Equals(recordedBaseline, _fsrsParameterVersion, StringComparison.Ordinal))
        {
            _mode = ContextMode.ColdStart;
            _lastDetail = $"已存的 Context 模型绑定在 FSRS 参数版本 {recordedBaseline} 上，"
                + $"与当前基线 {_fsrsParameterVersion} 不一致，退回 ColdStart 重新过资格。";
            return;
        }

        _model = model;
        _scaler = scaler;
        // 旧协议把训练切分点当发布点，不能继承其 Active 资格。
        var protocolOk = ReadMetricString(state.MetricsJson, "evaluationProtocol") == EvaluationProtocol;
        _mode = protocolOk && state.Status == ContextMode.Active ? ContextMode.Active : ContextMode.Shadow;
        _pendingActivationCutoffUtc = protocolOk ? ReadPendingCutoff(state.MetricsJson) : null;
        _lastDetail = $"已从 store 恢复 Context 模型 {model.Version}（{_mode}）。";
    }

    // ==================================================================================
    // FSRS 基线绑定（换版 → 重新资格）
    // ==================================================================================

    /// <summary>
    /// 读出当前生效的 FSRS 参数版本与它的生效时刻。**只读**、绝不抛：
    /// 读不到就按官方 defaults 基线处理（与"从未换过版"的行为逐字节一致）。
    /// </summary>
    private void LoadFsrsBaseline()
    {
        IReadOnlyList<PersonalizationModelState> rows;
        try { rows = _store.LoadPersonalizationModels(SchedulingConfig.FsrsParameterModelKind, SchedulingConfig.FsrsParameterHistoryScanLimit); }
        catch (Exception ex)
        {
            _lastDetail = "FSRS 基线读取失败（" + ex.Message + "），按官方默认参数处理。";
            return;
        }
        // **必须**复用集成层的同一个解析器：只看 metrics 里的版本字符串会信任一个
        // "版本写得对、coefficients 已经损坏"的行——那个版本根本没有生效（集成层会回退到
        // last-good/defaults），于是 Context 会把旧 Active 模型沿用到一套不同的权重上，
        // 历史快照 R 与当前基线不再是同一个东西。同一个解析器 = 两者永远一致。
        var effective = FsrsPersonalization.ResolveEffectiveWeights(rows);
        _fsrsParameterVersion = effective.ParameterVersion;
        _fsrsBaselineSinceUtc = effective.ActiveSinceUtc;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 语义：**降资格**，不是"把模型改一改继续用"。
    /// ① 世代号自增，让在途训练的结果失效（收尾时丢弃且不落库）；
    /// ② 内存退回 <see cref="ContextMode.ColdStart"/> 并清掉挂起的资格判定点——
    ///    旧 Active 不得在新基线下沿用，旧 Shadow 的冻结模型也不再具备"发布后观测"的资格；
    /// ③ 落库一行**空模型**的降资格记录（<c>ContextLinearModel.FromJson("[]")</c> 为 null），
    ///    这样重启后不会按旧的 Active/Shadow 状态把旧模型恢复回来；
    /// ④ 记录新基线版本与生效时刻，后续训练与独立确认只取该时刻之后捕获的快照。
    /// </remarks>
    public void ApplyFsrsBaseline(string parameterVersion, DateTime activeSinceUtc)
    {
        if (string.IsNullOrWhiteSpace(parameterVersion)) return;
        var since = NormalizeUtc(activeSinceUtc);
        lock (_gate)
        {
            _fsrsBaselineGeneration++;
            _fsrsParameterVersion = parameterVersion;
            _fsrsBaselineSinceUtc = since;
            _pendingActivationCutoffUtc = null;
            // 旧模型与标准化器必须**丢掉**，不能只改状态：
            // 它们是在另一套基线 R 上拟合出来的，任何一条"回退时保留 last-good"的路径
            // （SafeFallback 会把内存里的模型写成 Shadow）都会把旧系数标到新基线上。
            // 保留 LastGoodModelVersion 供审计，但不再持有可被写回的系数。
            _model = null;
            _scaler = null;
            _mode = _options.Enabled ? ContextMode.ColdStart : ContextMode.Disabled;
            _lastDetail = $"FSRS 基线已更换为 {parameterVersion}（{since:O}），Context 退回 ColdStart 重新过资格。";
        }
        PersistBaselineChange(parameterVersion, since);
    }

    private void PersistBaselineChange(string parameterVersion, DateTime since)
    {
        try
        {
            _store.SavePersonalizationModel(new PersonalizationModelState
            {
                ModelVersion = BaselineChangeModelVersion,
                TrainedAtUtc = since,
                TrainCutoffUtc = since,
                Status = ContextMode.ColdStart,
                // 形状合法但**不可用**的模型：weights 为空 → IsUsable(FeatureCount) 为 false → ColdStart。
                // 刻意不写 "[]"：数组根会让 FromJson 的 TryGetProperty 抛异常（已另行加固），
                // 而"明确不可用"比"靠异常兜底"更可读、更可测。比"保留旧系数 + 只改状态"安全得多——
                // 后者一旦有人放宽状态判定就会把旧基线模型复活。
                CoefficientsJson = "{\"version\":\"\",\"intercept\":0,\"weights\":[]}",
                ScalerJson = "{}",
                MetricsJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["featureSchemaVersion"] = SchedulingConfig.ContextFeatureSchemaVersion,
                    ["evaluationProtocol"] = EvaluationProtocol,
                    ["seed"] = _options.Seed,
                    ["outcome"] = "FsrsBaselineChange",
                    ["reason"] = "FSRS 参数换版：Context 基线变更，退回 ColdStart 重新过资格。",
                    ["baselineChangedAtUtc"] = since.ToString("O"),
                    [SchedulingConfig.FsrsParameterVersionMetricKey] = parameterVersion,
                    [SchedulingConfig.FsrsParameterActiveSinceMetricKey] = since.ToString("O"),
                }),
                LastGoodModelVersion = null,
            }, SchedulingConfig.ContextModelKind);
        }
        catch (Exception ex)
        {
            lock (_gate) _lastDetail += "（降资格落库失败：" + ex.Message + "）";
        }
    }

    // ==================================================================================
    // 推理
    // ==================================================================================

    /// <inheritdoc />
    public ContextAdjustment Adjust(ContextFeatureVector features, double fsrsRetrievability)
    {
        var mode = Mode;
        if (mode != ContextMode.Active) return ContextAdjustment.None(mode);
        var delta = DeltaFor(features);
        // 系数异常 / 维数不匹配 → 本次不校准（决不给一个不可解释的 δ）。
        if (delta is null) return ContextAdjustment.None(mode);
        return new ContextAdjustment(true, delta.Value, ContextMode.Active);
    }

    /// <summary>
    /// 当前冻结模型在该特征向量上的 δ（**与 <see cref="Adjust"/> 内部同一条路径**）。
    /// 未训练 / 维数不匹配 / 非有限 / 超过 <see cref="SchedulingConfig.ContextMaxAbsDelta"/> → null。
    /// </summary>
    public double? DeltaFor(ContextFeatureVector features)
    {
        if (features is null) return null;
        ContextLinearModel? model;
        ContextFeatureScaler? scaler;
        lock (_gate) { model = _model; scaler = _scaler; }
        if (model is null || scaler is null) return null;

        ContextFeatureVector aligned;
        try { aligned = ContextFeatureVector.Align(features, ContextFeatureProvider.FeatureNames); }
        catch (InvalidDataException) { return null; }

        double delta;
        try { delta = model.Score(scaler.Standardize(aligned.Values, aligned.Missing)); }
        catch (InvalidDataException) { return null; }
        if (!MemoryScheduler.IsFinite(delta) || Math.Abs(delta) > SchedulingConfig.ContextMaxAbsDelta) return null;
        return delta;
    }

    /// <summary>
    /// Active 模式下求 <c>R_adjusted(t) = desiredRetention</c> 的 t（天）。
    /// <para>
    /// <b>单调性</b>：δ 由**固定的历史特征**给出，与候选 t 无关；<c>R_fsrs(t)</c> 在
    /// <c>S &gt; 0</c> 时对 t 严格递减（FSRS 幂律遗忘曲线）；<c>logit</c> 与 <c>sigmoid</c> 都是严格单调增，
    /// 因此 <c>t ↦ sigmoid(logit(R_fsrs(t)) + δ)</c> 在 <c>t ∈ [0, 36500]</c> 上**严格单调递减**，
    /// 括号区间为 <c>[adjusted(0) ≈ 1, adjusted(36500) ≈ 0]</c>，对 0.90 一定跨越。
    /// 非单调 / 非有限 / 迭代不收敛 → 返回 null（协调器随即回退纯 FSRS 基线）。
    /// </para>
    /// </summary>
    public double? SolveCandidate(ContextFeatureVector features, double stabilityDays, double desiredRetention)
    {
        if (Mode != ContextMode.Active) return null;
        if (!MemoryScheduler.IsFinite(stabilityDays) || stabilityDays <= 0) return null;
        var delta = DeltaFor(features);
        if (delta is null) return null;
        return SolveAdjustedInterval(
            t => AdjustedRetrievability(_scheduler.Retrievability(stabilityDays, t), delta.Value),
            desiredRetention);
    }

    /// <summary>
    /// 求根本身（**唯一**调用 <see cref="MemoryScheduler.SolveIntervalByBisection"/> 的地方；本层不自研求根）。
    /// 抽成公开方法是为了让「非单调 / 不跨越目标 → null」这条失败路径可以被直接断言。
    /// </summary>
    public double? SolveAdjustedInterval(Func<double, double> adjustedRetrievability, double desiredRetention) =>
        MemoryScheduler.SolveIntervalByBisection(
            adjustedRetrievability, desiredRetention, 0.0, SchedulingConfig.MaximumIntervalDays);

    /// <summary><c>R_adjusted = sigmoid(logit(R_fsrs) + δ)</c>。</summary>
    public static double AdjustedRetrievability(double fsrsRetrievability, double delta) =>
        Sigmoid(Logit(fsrsRetrievability) + delta);

    // ==================================================================================
    // 训练 / 资格推进
    // ==================================================================================

    /// <summary>
    /// 训练一轮并推进资格状态。**时间由参数传入**（本层不读时钟）。
    /// <list type="bullet">
    /// <item><see cref="ContextTrainingOutcome.Disabled"/>：总开关关闭。</item>
    /// <item><see cref="ContextTrainingOutcome.Cooldown"/>：距上次训练不足 <see cref="SchedulingConfig.ContextTrainingCooldownMinutes"/> 分钟。</item>
    /// <item><see cref="ContextTrainingOutcome.ColdStart"/>：资格门槛未满足（只收集，不训练，不落库）。</item>
    /// <item><see cref="ContextTrainingOutcome.Trained"/>：拟合成功并落库（可能停在 Shadow，也可能进入 Active）。</item>
    /// <item><see cref="ContextTrainingOutcome.SafeFallback"/>：取消 / 超时 / 数值非法 → 保留 last-good 并退回 Shadow。</item>
    /// </list>
    /// </summary>
    public async Task<ContextTrainingResult> TrainAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            lock (_gate) { _mode = ContextMode.Disabled; _lastDetail = "Context 已关闭。"; }
            return new ContextTrainingResult(ContextTrainingOutcome.Disabled, ContextMode.Disabled, "", "Context 已关闭。");
        }

        var now = NormalizeUtc(nowUtc);
        ContextMode previousMode;
        DateTime? pending;
        ContextLinearModel? frozenModel;
        ContextFeatureScaler? frozenScaler;
        DateTime frozenTrainCutoff;
        DateTime? baselineSince;
        long baselineGeneration;
        lock (_gate)
        {
            if (_training) return new ContextTrainingResult(ContextTrainingOutcome.Cooldown, _mode, "", "已有一次训练在进行中。");
            if (_lastTrainedAtUtc is { } last
                && (now - last).TotalMinutes < SchedulingConfig.ContextTrainingCooldownMinutes)
            {
                return new ContextTrainingResult(ContextTrainingOutcome.Cooldown, _mode, "",
                    $"距上次训练不足 {SchedulingConfig.ContextTrainingCooldownMinutes} 分钟。");
            }
            previousMode = _mode;
            pending = _pendingActivationCutoffUtc;
            frozenModel = _model;
            frozenScaler = _scaler;
            frozenTrainCutoff = _lastCutoffUtc;
            baselineSince = _fsrsBaselineSinceUtc;
            baselineGeneration = _fsrsBaselineGeneration;
            _training = true;
        }

        try
        {
            IReadOnlyList<LabeledContextSample> samples;
            try { samples = _store.LoadLabeledSamples(now); }
            catch (Exception ex)
            {
                // 与其余出口一样先过世代闸：换版之后的任何收尾都不得写库。
                if (StaleGeneration(baselineGeneration, out var staleOnRead)) return staleOnRead;
                return SafeFallback("训练样本读取失败：" + ex.Message, now);
            }

            // 只有纯数值部分离开调用线程；store 访问自始至终在调用线程上。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.TrainingTimeout);
            FitOutcome outcome;
            try
            {
                var fitRequest = new FitRequest(samples, now, _options.Seed, previousMode, pending, frozenModel, frozenScaler, frozenTrainCutoff)
                {
                    FsrsBaselineSinceUtc = baselineSince,
                    FsrsBaselineGeneration = baselineGeneration,
                };
                // 故意**不加** .ConfigureAwait(false)：本方法后续要经 store 落库，而 VocabularyService
                // 只有一条**无锁**的 SqliteConnection，必须在调用它的那个线程上继续（UI 线程）。
                // 加了 ConfigureAwait(false) 会把续跑丢到线程池 → 跨线程使用同一条连接。
                // 纯数值部分已经在 Task.Run 里，不占用 UI 线程。
                outcome = await Task.Run(() => Fit(fitRequest, timeout.Token), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // **所有**出口（含取消与异常）都必须先过世代闸——这正是之前漏掉的那条路径：
                // 基线已换、旧世代训练被取消 → 直接走 SafeFallback 会把内存里的旧系数
                // 以"新基线"的名义写成 Shadow，覆盖刚写好的降资格记录。
                if (StaleGeneration(baselineGeneration, out var staleOnCancel)) return staleOnCancel;
                return SafeFallback("训练被取消或超过墙钟预算。", now);
            }
            catch (Exception ex)
            {
                if (StaleGeneration(baselineGeneration, out var staleOnError)) return staleOnError;
                return SafeFallback("训练失败：" + ex.GetType().Name + "：" + ex.Message, now);
            }

            // —— 世代检查：换版发生在本次训练启动之后 → 本轮结果作废，且**不落库** ——
            // 落库会用一个旧基线下拟合的模型覆盖掉刚刚写好的降资格记录，等于把重新资格撤销掉。
            if (StaleGeneration(baselineGeneration, out var stale)) return stale;

            if (!outcome.Trained || outcome.Artifacts is null)
                return outcome.RequiresSafeFallback
                    ? SafeFallback(outcome.Reason, now)
                    : ApplyColdStart(outcome.Reason, now);

            return ApplyTrained(outcome.Artifacts, previousMode, pending, now);
        }
        finally
        {
            lock (_gate) _training = false;
        }
    }

    /// <summary>
    /// 统一的世代闸：本次训练冻结的世代若已不等于当前世代，本轮的任何结果都不得改变状态、
    /// 不得落库（换基线已经写下了权威的降资格记录）。返回 true 时 <paramref name="rejected"/>
    /// 是可以直接返回给调用方的结果。
    /// </summary>
    private bool StaleGeneration(long frozenGeneration, out ContextTrainingResult rejected)
    {
        lock (_gate)
        {
            if (_fsrsBaselineGeneration == frozenGeneration) { rejected = null!; return false; }
            rejected = new ContextTrainingResult(ContextTrainingOutcome.SafeFallback, _mode, "",
                "训练期间 FSRS 基线已更换，本轮结果已丢弃（不改状态、不落库）。");
            return true;
        }
    }

    private ContextTrainingResult ApplyColdStart(string reason, DateTime now)
    {
        lock (_gate)
        {
            // 已有可用模型时「门槛未满足」不得把它踢掉：保持原状态与模型，只更新说明。
            if (_model is null) _mode = ContextMode.ColdStart;
            _lastDetail = reason;
            _lastTrainedAtUtc = now; // 不值得为了「门槛仍未满足」每次作答都全表读一遍
            return new ContextTrainingResult(ContextTrainingOutcome.ColdStart, _mode, _model?.Version ?? "", reason);
        }
    }

    private ContextTrainingResult ApplyTrained(
        ContextTrainingArtifacts artifacts, ContextMode previousMode, DateTime? pending, DateTime now)
    {
        // 新拟合模型只进入 Shadow；资格只能由同一冻结模型发布后的观测授予。
        var isFrozenEvaluation = artifacts.Confirmation is not null;
        ContextMode newMode;
        DateTime? newPending;
        string version;
        if (isFrozenEvaluation)
        {
            var deteriorated = artifacts.LogLossImprovement < -SchedulingConfig.ContextDeteriorationGuard
                || artifacts.BrierImprovement < -SchedulingConfig.ContextDeteriorationGuard;
            newMode = previousMode == ContextMode.Active
                ? (deteriorated ? ContextMode.Shadow : ContextMode.Active)
                : (artifacts.Confirmation!.Passed ? ContextMode.Active : ContextMode.Shadow);
            version = artifacts.Model.Version;
            // 一次确认失败后，下一轮重拟合；Active 的 rolling 窗口从本次评估之后开始。
            newPending = newMode == ContextMode.Active ? now : null;
        }
        else
        {
            newMode = ContextMode.Shadow;
            version = BuildModelVersion(now);
            newPending = artifacts.GatePassed ? now : null;
        }
        var confirmation = artifacts.Confirmation;
        var model = new ContextLinearModel(version, artifacts.Model.Intercept, artifacts.Model.Weights);
        string fsrsVersion;
        DateTime? fsrsSince;
        lock (_gate) { fsrsVersion = _fsrsParameterVersion; fsrsSince = _fsrsBaselineSinceUtc; }
        var metrics = BuildMetricsJson(artifacts, newMode, newPending, now, fsrsVersion, fsrsSince);

        lock (_gate)
        {
            _model = model;
            _scaler = artifacts.Scaler;
            _mode = newMode;
            _pendingActivationCutoffUtc = newPending;
            _lastTrainedAtUtc = now;
            _lastCutoffUtc = artifacts.CutoffUtc;
            _lastGoodModelVersion = version;
            _lastDetail = $"训练完成：{newMode}（GatePassed={artifacts.GatePassed}，确认段={confirmation?.Samples ?? 0} 条）。";
        }

        Persist(new PersonalizationModelState
        {
            ModelVersion = version,
            TrainedAtUtc = now,
            TrainCutoffUtc = artifacts.CutoffUtc,
            Status = newMode,
            CoefficientsJson = model.ToJson(),
            ScalerJson = artifacts.Scaler.ToJson(),
            MetricsJson = metrics,
            LastGoodModelVersion = version,
        });

        return new ContextTrainingResult(ContextTrainingOutcome.Trained, newMode, version, LastTrainingDetail);
    }

    private ContextTrainingResult SafeFallback(string reason, DateTime now)
    {
        ContextLinearModel? model;
        ContextFeatureScaler? scaler;
        DateTime cutoff;
        string? lastGood;
        lock (_gate)
        {
            model = _model;
            scaler = _scaler;
            cutoff = _lastCutoffUtc;
            // 保留上一个可用模型（内存里那份），把状态退回 Shadow：不删模型、不删审计。
            lastGood = model?.Version ?? _lastGoodModelVersion;
            if (model is not null) _mode = ContextMode.Shadow;
            _lastTrainedAtUtc = now;
            _lastGoodModelVersion = lastGood;
            _lastDetail = reason;
        }

        if (model is null || scaler is null || lastGood is null)
            return new ContextTrainingResult(ContextTrainingOutcome.SafeFallback, Mode, "", reason);

        // 把「退回 Shadow」这件事落库，否则重启后会按旧的 Active 状态恢复。
        Persist(new PersonalizationModelState
        {
            ModelVersion = SafeFallbackModelVersion,
            TrainedAtUtc = now,
            TrainCutoffUtc = cutoff,
            Status = ContextMode.Shadow,
            CoefficientsJson = model.ToJson(),
            ScalerJson = scaler.ToJson(),
            MetricsJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["featureSchemaVersion"] = SchedulingConfig.ContextFeatureSchemaVersion,
                ["seed"] = _options.Seed,
                ["evaluationProtocol"] = EvaluationProtocol,
                [SchedulingConfig.FsrsParameterVersionMetricKey] = _fsrsParameterVersion,
                [SchedulingConfig.FsrsParameterActiveSinceMetricKey] = _fsrsBaselineSinceUtc?.ToString("O"),
                ["outcome"] = "SafeFallback",
                ["reason"] = reason,
                ["trainedAtUtc"] = now.ToString("O"),
                ["lastGoodModelVersion"] = lastGood,
            }),
            LastGoodModelVersion = lastGood,
        });
        return new ContextTrainingResult(ContextTrainingOutcome.SafeFallback, ContextMode.Shadow, lastGood, reason);
    }

    private void Persist(PersonalizationModelState state)
    {
        try { _store.SavePersonalizationModel(state); }
        catch (Exception ex)
        {
            lock (_gate) _lastDetail += "（模型落库失败：" + ex.Message + "，本次仅在内存生效）";
        }
    }

    // ==================================================================================
    // 纯数值：切分 / 拟合 / 评估
    // ==================================================================================

    private sealed record FitRequest(
        IReadOnlyList<LabeledContextSample> Samples, DateTime NowUtc, int Seed,
        ContextMode PreviousMode, DateTime? PendingActivationCutoffUtc,
        ContextLinearModel? FrozenModel, ContextFeatureScaler? FrozenScaler, DateTime FrozenTrainCutoffUtc)
    {
        /// <summary>当前 FSRS 基线的生效时刻；null = defaults 基线，不做样本过滤。</summary>
        public DateTime? FsrsBaselineSinceUtc { get; init; }

        /// <summary>训练开始时的基线世代号（收尾时用于丢弃跨换版的结果）。</summary>
        public long FsrsBaselineGeneration { get; init; }
    }

    /// <summary>
    /// 一轮拟合的结果。<paramref name="RequiresSafeFallback"/> 把两类「没训练出模型」区分开：
    /// <list type="bullet">
    /// <item>false = 数据还不够 / 不满足门槛 → ColdStart，只收集，**不算失败**，
    /// 已有模型（若有）原样保留。</item>
    /// <item>true = 训练本身出问题（不收敛、非有限参数、极端系数）→ 必须走
    /// <see cref="SafeFallback"/>：保留 last-good、退回 Shadow、并把这件事落库。</item>
    /// </list>
    /// 把两者混为一谈会让「极端系数」悄悄退化成「数据不够」，last-good 也就没人保了。
    /// </summary>
    private sealed record FitOutcome(bool Trained, string Reason, ContextTrainingArtifacts? Artifacts, bool RequiresSafeFallback);

    private FitOutcome Fit(FitRequest request, CancellationToken ct)
    {
        var dimension = ContextFeatureProvider.FeatureCount;
        var evaluable = new List<Sample>();
        var dropped = 0;

        foreach (var raw in request.Samples)
        {
            ct.ThrowIfCancellationRequested();
            // 基线过滤：换版之前捕获的快照里，R 是**上一套 FSRS 权重**算出来的。
            // 把它和当前基线的样本混在一起训练，会让同一个 δ 对应两个不同基线，
            // 系统性污染模型偏移。整条丢弃（不是当成缺失、更不是当成中性值）。
            if (request.FsrsBaselineSinceUtc is { } baselineSince && raw.CapturedAtUtc < baselineSince)
            {
                dropped++;
                continue;
            }
            var sample = Sample.TryCreate(raw, dimension);
            if (sample is null)
            {
                // 丢弃原因只有两类，且都是「这条样本无法评估校准」而不是「它不好看」：
                //   ① 特征维数/缺失标记与当前 schema 不匹配（换版或损坏）；
                //   ② 基线 R 非有限，或恰好等于 0/1 —— 后者是「无上次复习」的占位约定值 1.0，
                //      拿它当「几乎必定记得」会把基线美化，从而伪造出 Context 的改善。
                dropped++;
                continue;
            }
            evaluable.Add(sample);
        }

        if (request.PendingActivationCutoffUtc is { } published
            && request.FrozenModel is { } frozenModel && request.FrozenScaler is { } frozenScaler)
        {
            // 严格晚于模型发布且早于本次评估；过去已验证数据不能再次充当新证据。
            var fresh = evaluable.Where(s => s.CapturedAtUtc > published && s.CapturedAtUtc < request.NowUtc)
                .OrderBy(s => s.CapturedAtUtc).ThenBy(s => s.SnapshotId, StringComparer.Ordinal).ToList();
            if (fresh.Count < SchedulingConfig.ContextMinimumActiveConfirmationSamples
                || !fresh.Any(s => s.Label == 1) || !fresh.Any(s => s.Label == 0))
                return new FitOutcome(false, "等待冻结模型发布后的独立正负样本。", null, false);
            ct.ThrowIfCancellationRequested();
            var baseline = EvaluateBaseline(fresh);
            var context = Evaluate(fresh, frozenScaler, frozenModel);
            var maxDelta = MaxAbsDelta([], fresh, frozenScaler, frozenModel);
            if (!MemoryScheduler.IsFinite(maxDelta) || maxDelta > SchedulingConfig.ContextMaxAbsDelta)
                return new FitOutcome(false, "冻结模型在新观测上产生非法或极端校准量。", null, true);
            var ll = baseline.LogLoss - context.LogLoss;
            var bs = baseline.Brier - context.Brier;
            var passed = ll >= SchedulingConfig.ContextMinimumLogLossImprovement
                && bs >= SchedulingConfig.ContextMinimumLogLossImprovement;
            var confirmed = new ContextConfirmation(fresh.Count, passed, ll, bs);
            var pos = fresh.Count(s => s.Label == 1);
            return new FitOutcome(true, "冻结模型的独立未来观测评估完成。",
                new ContextTrainingArtifacts(frozenModel, frozenScaler, request.FrozenTrainCutoffUtc,
                    request.Samples.Count, evaluable.Count, dropped, 0, fresh.Count,
                    pos, fresh.Count - pos, (fresh[^1].CapturedAtUtc - fresh[0].CapturedAtUtc).TotalDays,
                    context, baseline, context, passed, confirmed, maxDelta,
                    FreezePredictions(fresh, frozenScaler, frozenModel)), false);
        }

        if (evaluable.Count < SchedulingConfig.ContextMinimumSamples)
            return new FitOutcome(false, $"可评估样本 {evaluable.Count} < {SchedulingConfig.ContextMinimumSamples}（丢弃 {dropped}）。", null, false);

        var positives = evaluable.Count(s => s.Label == 1);
        var negatives = evaluable.Count - positives;
        if (positives < SchedulingConfig.ContextMinimumPositive || negatives < SchedulingConfig.ContextMinimumNegative)
            return new FitOutcome(false,
                $"正例 {positives} / 负例 {negatives} 未达 {SchedulingConfig.ContextMinimumPositive} / {SchedulingConfig.ContextMinimumNegative}。", null, false);

        evaluable.Sort(static (a, b) =>
        {
            var byTime = a.CapturedAtUtc.CompareTo(b.CapturedAtUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.SnapshotId, b.SnapshotId);
        });

        var spanDays = (evaluable[^1].CapturedAtUtc - evaluable[0].CapturedAtUtc).TotalDays;
        if (spanDays < SchedulingConfig.ContextMinimumSpanDays)
            return new FitOutcome(false,
                $"跨度 {spanDays:F2} 天 < {SchedulingConfig.ContextMinimumSpanDays} 天。", null, false);

        // —— 时间序切分（**禁止**随机 80/20）：训练永远是过去，验证永远是未来 ——
        var target = (int)Math.Round(evaluable.Count * SchedulingConfig.ContextTrainFraction, MidpointRounding.AwayFromZero);
        target = Math.Clamp(target, 1, evaluable.Count - 1);
        var cutoffUtc = evaluable[target].CapturedAtUtc;
        // 与截止时刻**同刻**的样本一律划到验证段：保证「训练段最大时刻 < 验证段最小」是严格不等式。
        var train = evaluable.Where(s => s.CapturedAtUtc < cutoffUtc).ToList();
        var validation = evaluable.Where(s => s.CapturedAtUtc >= cutoffUtc).ToList();
        if (train.Count < MinimumFittableSamples || validation.Count < SchedulingConfig.ContextMinimumValidationSamples)
            return new FitOutcome(false,
                $"切分后训练 {train.Count} / 验证 {validation.Count} 条不足（验证段下限 {SchedulingConfig.ContextMinimumValidationSamples}）。", null, false);

        var scaler = ContextFeatureScaler.Fit(
            train.Select(s => s.Features).ToList(),
            train.Select(s => s.Missing).ToList(),
            dimension);

        ct.ThrowIfCancellationRequested();
        var model = FitLogistic(train, scaler, request.Seed, ct);
        if (model is null) return new FitOutcome(false, "逻辑回归未收敛或产生非有限参数。", null, true);

        var trainEvaluation = Evaluate(train, scaler, model);
        var baselineEvaluation = EvaluateBaseline(validation);
        var contextEvaluation = Evaluate(validation, scaler, model);
        if (baselineEvaluation.Samples != validation.Count || contextEvaluation.Samples != validation.Count)
            return new FitOutcome(false, "验证段存在无法评估的样本。", null, true);

        var maxAbsDelta = MaxAbsDelta(train, validation, scaler, model);
        if (!MemoryScheduler.IsFinite(maxAbsDelta) || maxAbsDelta > SchedulingConfig.ContextMaxAbsDelta)
            return new FitOutcome(false,
                $"校准量绝对值 {maxAbsDelta:F3} 超过上限 {SchedulingConfig.ContextMaxAbsDelta}（极端系数）→ 拒绝该模型并保留 last-good。", null, true);

        var logLossImprovement = baselineEvaluation.LogLoss - contextEvaluation.LogLoss;
        var brierImprovement = baselineEvaluation.Brier - contextEvaluation.Brier;
        var gatePassed = validation.Count >= SchedulingConfig.ContextMinimumValidationSamples
            && validation.Any(s => s.Label == 1) && validation.Any(s => s.Label == 0)
            && logLossImprovement >= SchedulingConfig.ContextMinimumLogLossImprovement
            && brierImprovement >= SchedulingConfig.ContextMinimumLogLossImprovement;

        ContextConfirmation? confirmation = null;

        var artifacts = new ContextTrainingArtifacts(
            Model: model,
            Scaler: scaler,
            CutoffUtc: cutoffUtc,
            TotalSamples: request.Samples.Count,
            EvaluableSamples: evaluable.Count,
            DroppedSamples: dropped,
            TrainSamples: train.Count,
            ValidationSamples: validation.Count,
            Positives: positives,
            Negatives: negatives,
            SpanDays: spanDays,
            Train: trainEvaluation,
            Baseline: baselineEvaluation,
            Context: contextEvaluation,
            GatePassed: gatePassed,
            Confirmation: confirmation,
            MaxAbsDelta: maxAbsDelta,
            FrozenPredictions: FreezePredictions(validation, scaler, model));

        return new FitOutcome(true, "拟合完成。", artifacts, false);
    }

    /// <summary>
    /// 带**固定偏移量**的 logistic 回归：<c>score_i = logit(R_i) + β0 + βᵀZ_i</c>。
    /// 全批量梯度下降 + L2（不含截距）+ 步长折半回退；零初始化、无采样、无打乱 → 完全确定性。
    /// </summary>
    private static ContextLinearModel? FitLogistic(IReadOnlyList<Sample> train, ContextFeatureScaler scaler, int seed, CancellationToken ct)
    {
        var n = train.Count;
        var d = ContextFeatureProvider.FeatureCount;
        var z = new double[n][];
        var offset = new double[n];
        var y = new int[n];
        for (var i = 0; i < n; i++)
        {
            z[i] = scaler.Standardize(train[i].Features, train[i].Missing);
            offset[i] = Logit(train[i].BaselineRetrievability);
            y[i] = train[i].Label;
        }

        var weights = new double[d];
        var intercept = 0.0;
        var rate = SchedulingConfig.ContextTrainingLearningRate;
        var l2 = SchedulingConfig.ContextL2Regularization;
        var loss = Loss(z, offset, y, weights, intercept, l2);
        if (!MemoryScheduler.IsFinite(loss)) return null;

        var gradient = new double[d];
        for (var iteration = 0; iteration < SchedulingConfig.ContextTrainingMaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var gb = 0.0;
            Array.Clear(gradient);
            for (var i = 0; i < n; i++)
            {
                var p = Sigmoid(offset[i] + intercept + Dot(weights, z[i]));
                var error = p - y[i];
                gb += error;
                var row = z[i];
                for (var j = 0; j < d; j++) gradient[j] += error * row[j];
            }
            gb /= n;
            var maxGradient = Math.Abs(gb);
            for (var j = 0; j < d; j++)
            {
                gradient[j] = gradient[j] / n + l2 * weights[j];
                var magnitude = Math.Abs(gradient[j]);
                if (magnitude > maxGradient) maxGradient = magnitude;
            }
            if (!MemoryScheduler.IsFinite(maxGradient)) return null;
            if (maxGradient < SchedulingConfig.ContextGradientTolerance) break;

            var accepted = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var candidateWeights = new double[d];
                for (var j = 0; j < d; j++) candidateWeights[j] = weights[j] - rate * gradient[j];
                var candidateIntercept = intercept - rate * gb;
                var candidateLoss = Loss(z, offset, y, candidateWeights, candidateIntercept, l2);
                if (MemoryScheduler.IsFinite(candidateLoss) && candidateLoss <= loss)
                {
                    weights = candidateWeights;
                    intercept = candidateIntercept;
                    loss = candidateLoss;
                    accepted = true;
                    break;
                }
                rate *= 0.5;
                if (rate < 1e-12) break;
            }
            if (!accepted) break;
        }

        if (!MemoryScheduler.IsFinite(intercept) || weights.Any(w => !MemoryScheduler.IsFinite(w))) return null;
        // 种子只作为可复现性审计字段（当前优化器没有随机成分）；保留参数是为了将来换随机变体时不必改签名。
        _ = seed;
        return new ContextLinearModel(SchedulingConfig.ContextModelVersionPrefix, intercept, weights);
    }

    private static double Loss(double[][] z, double[] offset, int[] y, double[] weights, double intercept, double l2)
    {
        var n = z.Length;
        double total = 0;
        for (var i = 0; i < n; i++)
        {
            var p = Sigmoid(offset[i] + intercept + Dot(weights, z[i]));
            total += -(y[i] * Math.Log(p) + (1 - y[i]) * Math.Log(1 - p));
        }
        double penalty = 0;
        foreach (var w in weights) penalty += w * w;
        return total / n + l2 / 2.0 * penalty;
    }

    private static double MaxAbsDelta(IReadOnlyList<Sample> train, IReadOnlyList<Sample> validation, ContextFeatureScaler scaler, ContextLinearModel model)
    {
        var max = 0.0;
        foreach (var sample in train.Concat(validation))
        {
            var delta = model.Score(scaler.Standardize(sample.Features, sample.Missing));
            if (!MemoryScheduler.IsFinite(delta)) return double.PositiveInfinity;
            max = Math.Max(max, Math.Abs(delta));
        }
        return max;
    }

    /// <summary>纯 FSRS 基线预测：<c>p = R</c>。</summary>
    private static ContextEvaluation EvaluateBaseline(IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0) return new ContextEvaluation(0, 0, 0, double.NaN, double.NaN);
        double logLoss = 0, brier = 0;
        var positives = 0;
        foreach (var s in samples)
        {
            var p = Clamp(s.BaselineRetrievability);
            logLoss += -(s.Label * Math.Log(p) + (1 - s.Label) * Math.Log(1 - p));
            brier += (p - s.Label) * (p - s.Label);
            if (s.Label == 1) positives++;
        }
        return new ContextEvaluation(samples.Count, positives, samples.Count - positives, logLoss / samples.Count, brier / samples.Count);
    }

    /// <summary>Context 预测：<c>p = sigmoid(logit(R) + δ)</c>。</summary>
    private static ContextEvaluation Evaluate(IReadOnlyList<Sample> samples, ContextFeatureScaler scaler, ContextLinearModel model)
    {
        if (samples.Count == 0) return new ContextEvaluation(0, 0, 0, double.NaN, double.NaN);
        double logLoss = 0, brier = 0;
        var positives = 0;
        foreach (var s in samples)
        {
            var p = Clamp(AdjustedRetrievability(s.BaselineRetrievability, model.Score(scaler.Standardize(s.Features, s.Missing))));
            logLoss += -(s.Label * Math.Log(p) + (1 - s.Label) * Math.Log(1 - p));
            brier += (p - s.Label) * (p - s.Label);
            if (s.Label == 1) positives++;
        }
        return new ContextEvaluation(samples.Count, positives, samples.Count - positives, logLoss / samples.Count, brier / samples.Count);
    }

    /// <summary>冻结的 prequential 预测（验证段，逐条 baseline/context/outcome），保留最近 N 条。</summary>
    private static double[][] FreezePredictions(IReadOnlyList<Sample> validation, ContextFeatureScaler scaler, ContextLinearModel model)
    {
        var take = Math.Min(validation.Count, SchedulingConfig.ContextMaxLoggedPredictions);
        var result = new double[take][];
        for (var i = 0; i < take; i++)
        {
            var s = validation[validation.Count - take + i];
            result[i] =
            [
                Clamp(s.BaselineRetrievability),
                Clamp(AdjustedRetrievability(s.BaselineRetrievability, model.Score(scaler.Standardize(s.Features, s.Missing)))),
                s.Label,
            ];
        }
        return result;
    }

    // ==================================================================================
    // 可审计的 MetricsJson
    // ==================================================================================

    private string BuildMetricsJson(ContextTrainingArtifacts a, ContextMode mode, DateTime? pendingCutoff,
        DateTime evaluatedAtUtc, string fsrsParameterVersion, DateTime? fsrsBaselineSinceUtc)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["featureSchemaVersion"] = SchedulingConfig.ContextFeatureSchemaVersion,
            ["evaluationProtocol"] = EvaluationProtocol,
            ["modelFamily"] = SchedulingConfig.ContextModelVersionPrefix,
            // 这套 Context 模型是在哪一套 FSRS 权重（哪个基线）下学出来的。
            // 恢复时逐字比对：不一致就退回 ColdStart 重新过资格（见 RestoreFromStore）。
            [SchedulingConfig.FsrsParameterVersionMetricKey] = fsrsParameterVersion,
            [SchedulingConfig.FsrsParameterActiveSinceMetricKey] = fsrsBaselineSinceUtc?.ToString("O"),
            ["mode"] = mode.ToString(),
            ["seed"] = _options.Seed,
            ["cutoffUtc"] = a.CutoffUtc.ToString("O"),
            ["evaluationAsOfUtc"] = evaluatedAtUtc.ToString("O"),
            ["frozenEvaluation"] = a.Confirmation is not null,
            ["totalSamples"] = a.TotalSamples,
            ["evaluableSamples"] = a.EvaluableSamples,
            ["droppedSamples"] = a.DroppedSamples,
            ["trainSamples"] = a.TrainSamples,
            ["validationSamples"] = a.ValidationSamples,
            ["positives"] = a.Positives,
            ["negatives"] = a.Negatives,
            ["spanDays"] = a.SpanDays,
            ["trainLogLoss"] = a.Train.LogLoss,
            ["trainBrier"] = a.Train.Brier,
            ["baselineLogLoss"] = a.Baseline.LogLoss,
            ["contextLogLoss"] = a.Context.LogLoss,
            ["baselineBrier"] = a.Baseline.Brier,
            ["contextBrier"] = a.Context.Brier,
            ["logLossImprovement"] = a.LogLossImprovement,
            ["brierImprovement"] = a.BrierImprovement,
            ["minimumLogLossImprovement"] = SchedulingConfig.ContextMinimumLogLossImprovement,
            ["minimumSamples"] = SchedulingConfig.ContextMinimumSamples,
            ["minimumPositive"] = SchedulingConfig.ContextMinimumPositive,
            ["minimumNegative"] = SchedulingConfig.ContextMinimumNegative,
            ["minimumSpanDays"] = SchedulingConfig.ContextMinimumSpanDays,
            ["gatePassed"] = a.GatePassed,
            ["gateCutoffUtc"] = pendingCutoff?.ToString("O"),
            ["confirmation"] = a.Confirmation is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["samples"] = a.Confirmation.Samples,
                    ["passed"] = a.Confirmation.Passed,
                    ["logLossImprovement"] = a.Confirmation.LogLossImprovement,
                    ["brierImprovement"] = a.Confirmation.BrierImprovement,
                },
            ["maxAbsDelta"] = a.MaxAbsDelta,
            ["maxAbsDeltaLimit"] = SchedulingConfig.ContextMaxAbsDelta,
            // 冻结（prequential）预测：验证段逐条的 [baseline, context, outcome]，
            // 即 shadow 阶段要求记录的「baseline/context 两套预测 + 真实 outcome」。
            ["predictions"] = a.FrozenPredictions,
        });

    // ==================================================================================
    // 工具
    // ==================================================================================

    private static string BuildModelVersion(DateTime cutoffUtc) =>
        $"{SchedulingConfig.ContextModelVersionPrefix}:{cutoffUtc.Ticks:x}";

    private const string SafeFallbackModelVersion = SchedulingConfig.ContextModelVersionPrefix + ":safe-fallback";

    /// <summary>FSRS 基线变更（换版）时写下的降资格记录版本；与 <c>:safe-fallback</c> 同形，独立可辨。</summary>
    private const string BaselineChangeModelVersion = SchedulingConfig.ContextModelVersionPrefix + ":fsrs-baseline-change";

    private static string? ReadSchemaVersion(string? metricsJson)
    {
        if (string.IsNullOrWhiteSpace(metricsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metricsJson);
            return doc.RootElement.TryGetProperty("featureSchemaVersion", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadMetricString(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static DateTime? ReadPendingCutoff(string? metricsJson)
    {
        if (string.IsNullOrWhiteSpace(metricsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metricsJson);
            if (!doc.RootElement.TryGetProperty("gateCutoffUtc", out var v) || v.ValueKind != JsonValueKind.String) return null;
            return DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? NormalizeUtc(parsed)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Dot(double[] weights, double[] row)
    {
        var sum = 0.0;
        for (var j = 0; j < weights.Length; j++) sum += weights[j] * row[j];
        return sum;
    }

    private static double Sigmoid(double x) => x >= 0
        ? 1.0 / (1.0 + Math.Exp(-x))
        : Math.Exp(x) / (1.0 + Math.Exp(x));

    private static double Logit(double probability)
    {
        var p = Clamp(probability);
        return Math.Log(p / (1.0 - p));
    }

    private static double Clamp(double probability) =>
        Math.Clamp(probability, ProbabilityEpsilon, 1.0 - ProbabilityEpsilon);

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>一条可评估的训练样本。</summary>
    private sealed record Sample(
        string SnapshotId, DateTime CapturedAtUtc, double[] Features, bool[] Missing, int Label, double BaselineRetrievability)
    {
        public static Sample? TryCreate(LabeledContextSample raw, int dimension)
        {
            if (raw.Features is null || raw.Features.Length != dimension) return null;
            var missing = ParseMissing(raw.MissingFlagsJson, dimension);
            if (missing is null) return null;
            if (raw.Label is not (0 or 1)) return null;
            var r = raw.FsrsRetrievabilityAtCapture;
            if (!MemoryScheduler.IsFinite(r) || r <= ProbabilityEpsilon || r >= 1.0 - ProbabilityEpsilon) return null;
            return new Sample(raw.SnapshotId, NormalizeUtc(raw.CapturedAtUtc), raw.Features, missing, raw.Label, r);
        }

        private static bool[]? ParseMissing(string? json, int dimension)
        {
            if (string.IsNullOrWhiteSpace(json)) return new bool[dimension];
            try
            {
                var parsed = JsonSerializer.Deserialize<bool[]>(json);
                if (parsed is null) return new bool[dimension];
                if (parsed.Length == dimension) return parsed;
                if (parsed.Length == 0) return new bool[dimension];
                return null; // 长度不符 = 换版或损坏 → 丢弃该样本（不猜）
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
