using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexi;

/// <summary>
/// FSRS-6 的 21 个权重：内存表示 + 按 <c>dependency_decision.md</c>「参数持久化格式」的 JSON 往返。
///
/// <para>
/// 默认值来源（三处实读一致，见 <c>agents/audit-05-fsrs-upstream.md</c> §3.1）：
/// awesome-fsrs wiki <c>The-Algorithm.md</c> / <c>fsrs-rs src/inference_v6.rs::FSRS6_DEFAULT_PARAMETERS</c> /
/// <c>py-fsrs fsrs/scheduler.py::DEFAULT_PARAMETERS</c>。
/// </para>
/// <para>
/// clip 区间来源：<c>py-fsrs fsrs/scheduler.py</c> 的 <c>LOWER_BOUNDS_PARAMETERS</c> /
/// <c>UPPER_BOUNDS_PARAMETERS</c>（与 <c>fsrs-rs src/parameter_clipper_v6.rs</c> 的 21 项区间逐项一致，
/// 见 audit §5.5）。选 py-fsrs 口径是因为我方 golden 由 py-fsrs 6.3.2 生成。
/// </para>
/// <para>本类型无 IO、无时钟；<see cref="TryLoad"/> 只做字符串解析与校验。</para>
/// </summary>
public sealed class Fsrs6Weights
{
    /// <summary>算法标识（持久化字段 <c>algorithm</c>）。</summary>
    public const string Algorithm = "FSRS-6";

    /// <summary>参数个数（持久化字段 <c>parameterCount</c>）。</summary>
    public const int ParameterCount = 21;

    /// <summary>参数格式版本（持久化字段 <c>parameterVersion</c>）。</summary>
    public const int ParameterVersion = 1;

    /// <summary>权重来源：官方默认值。</summary>
    public const string SourceDefaults = "defaults";

    /// <summary>权重来源：个人历史训练（optimizer）产出。</summary>
    public const string SourceOptimized = "optimized";

    /// <summary>权重来源：上一次通过校验的可用值（新值被拒时保留）。</summary>
    public const string SourceLastGood = "last-good";

    /// <summary>
    /// 21 个官方默认权重（audit §3.1 逐项照抄，禁止改动）。
    /// 语义顺序：w0..w3 = S0(Again/Hard/Good/Easy)；w4/w5 = D0 常数项与指数系数；
    /// w6 = ΔD 线性阻尼；w7 = D 均值回归强度；w8..w10 = 成功回忆稳定性；w11..w14 = 遗忘稳定性；
    /// w15 = Hard 惩罚；w16 = Easy 奖励；w17..w19 = 短期（同日）稳定性；w20 = 遗忘曲线 decay（存正数）。
    /// </summary>
    public static readonly double[] OfficialDefaults =
    [
        0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001,
        1.8722, 0.1666, 0.796, 1.4835, 0.0614, 0.2629, 1.6483, 0.6014,
        1.8729, 0.5425, 0.0912, 0.0658, 0.1542,
    ];

    /// <summary>
    /// 21 项官方 clip 区间 (lower, upper)，逐项照抄 py-fsrs 6.3.2 的
    /// <c>LOWER_BOUNDS_PARAMETERS</c> / <c>UPPER_BOUNDS_PARAMETERS</c>（audit §5.5）。
    /// 索引与 <see cref="OfficialDefaults"/> 一一对应。
    /// </summary>
    public static readonly (double Lower, double Upper)[] OfficialClipBounds =
    [
        (0.001, 100.0), (0.001, 100.0), (0.001, 100.0), (0.001, 100.0), // w0..w3  S0  (STABILITY_MIN, INITIAL_STABILITY_MAX)
        (1.0, 10.0),                                                    // w4      D0 常数项
        (0.001, 4.0), (0.001, 4.0), (0.001, 0.75),                      // w5, w6, w7
        (0.0, 4.5), (0.0, 0.8), (0.001, 3.5),                           // w8, w9, w10
        (0.001, 5.0), (0.001, 0.25), (0.001, 0.9), (0.0, 4.0),          // w11..w14
        (0.0, 1.0), (1.0, 6.0),                                         // w15, w16
        (0.0, 2.0), (0.0, 2.0), (0.0, 0.8),                             // w17, w18, w19
        (0.1, 0.8),                                                     // w20     decay
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly double[] _weights;

    private Fsrs6Weights(double[] weights, string source, DateTime? optimizedAtUtc)
    {
        _weights = weights;
        Source = source;
        OptimizedAtUtc = optimizedAtUtc;
    }

    /// <summary>21 个权重（返回内部数组本身，调用方不得改写；需要副本请用 <see cref="ToArray"/>）。</summary>
    public IReadOnlyList<double> Weights => _weights;

    /// <summary>第 <paramref name="index"/> 个权重。</summary>
    public double this[int index] => _weights[index];

    /// <summary><c>defaults</c> | <c>optimized</c> | <c>last-good</c>。</summary>
    public string Source { get; }

    /// <summary>optimizer 产出时刻（UTC）；<c>defaults</c> 时为 null。</summary>
    public DateTime? OptimizedAtUtc { get; }

    /// <summary>官方默认权重（<c>source = "defaults"</c>）。</summary>
    public static Fsrs6Weights Defaults { get; } =
        new((double[])OfficialDefaults.Clone(), SourceDefaults, null);

    /// <summary>已知的 <c>source</c> 取值。</summary>
    public static bool IsKnownSource(string? source) =>
        source is SourceDefaults or SourceOptimized or SourceLastGood;

    /// <summary>
    /// 逐项校验：长度必须为 21；每项必须有限（非 NaN/±Inf）且落在官方 clip 区间内。
    /// </summary>
    public static bool TryValidate(double[]? weights, out string error)
    {
        if (weights is null) { error = "权重数组为 null。"; return false; }
        if (weights.Length != ParameterCount)
        {
            error = $"权重个数必须是 {ParameterCount}，实际 {weights.Length}。";
            return false;
        }
        for (var i = 0; i < weights.Length; i++)
        {
            var value = weights[i];
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                error = $"w{i} 非有限值：{value}。";
                return false;
            }
            var (lower, upper) = OfficialClipBounds[i];
            if (value < lower || value > upper)
            {
                error = $"w{i} = {value:R} 超出官方 clip 区间 [{lower:R}, {upper:R}]。";
                return false;
            }
        }
        error = "";
        return true;
    }

    /// <summary>校验一组权重是否可作为 FSRS-6 权重使用。</summary>
    public static bool IsWithinClipBounds(int index, double value)
    {
        if (index < 0 || index >= ParameterCount) return false;
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        var (lower, upper) = OfficialClipBounds[index];
        return value >= lower && value <= upper;
    }

    /// <summary>构造实例；不合法时抛 <see cref="ArgumentException"/>。</summary>
    public static Fsrs6Weights Create(double[] weights, string source = SourceOptimized, DateTime? optimizedAtUtc = null)
    {
        if (!TryCreate(weights, source, optimizedAtUtc, out var result, out var error))
            throw new ArgumentException(error, nameof(weights));
        return result!;
    }

    /// <summary>构造实例；不合法时返回 false 并给出原因（不抛异常）。</summary>
    public static bool TryCreate(
        double[]? weights, string? source, DateTime? optimizedAtUtc,
        out Fsrs6Weights? result, out string error)
    {
        result = null;
        if (!IsKnownSource(source))
        {
            error = $"未知的 source：{source ?? "<null>"}；允许 {SourceDefaults} | {SourceOptimized} | {SourceLastGood}。";
            return false;
        }
        if (!TryValidate(weights, out error)) return false;
        result = new Fsrs6Weights((double[])weights!.Clone(), source!, NormalizeUtc(optimizedAtUtc));
        error = "";
        return true;
    }

    /// <summary>
    /// 从 <c>dependency_decision.md</c> §参数持久化格式 的 JSON 形状加载。
    /// 形状：<c>{ algorithm, parameterCount, parameterVersion, weights[21], source, optimizedAtUtc }</c>。
    /// algorithm / parameterCount / parameterVersion / source / 权重合法性 任一不符 → 返回 false 并给出原因。
    /// </summary>
    public static bool TryLoad(string? json, out Fsrs6Weights? result, out string error)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "参数 JSON 为空。"; return false; }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = "参数 JSON 解析失败：" + ex.Message;
            return false;
        }

        if (payload is null) { error = "参数 JSON 反序列化为 null。"; return false; }
        if (!string.Equals(payload.Algorithm, Algorithm, StringComparison.Ordinal))
        {
            error = $"algorithm 必须是 \"{Algorithm}\"，实际 \"{payload.Algorithm ?? "<null>"}\"。";
            return false;
        }
        if (payload.ParameterCount != ParameterCount)
        {
            error = $"parameterCount 必须是 {ParameterCount}，实际 {payload.ParameterCount}。";
            return false;
        }
        if (payload.ParameterVersion != ParameterVersion)
        {
            error = $"parameterVersion 必须是 {ParameterVersion}，实际 {payload.ParameterVersion}。";
            return false;
        }
        if (!TryCreate(payload.Weights, payload.Source, payload.OptimizedAtUtc, out result, out error)) return false;
        return true;
    }

    /// <summary>按文档形状序列化（camelCase）。</summary>
    public string ToJson(bool indented = false)
    {
        var payload = new Payload
        {
            Algorithm = Algorithm,
            ParameterCount = ParameterCount,
            ParameterVersion = ParameterVersion,
            Weights = (double[])_weights.Clone(),
            Source = Source,
            OptimizedAtUtc = OptimizedAtUtc,
        };
        return indented
            ? JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonOptions) { WriteIndented = true })
            : JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>权重数组的副本。</summary>
    public double[] ToArray() => (double[])_weights.Clone();

    /// <summary>深拷贝（权重数组独立）。</summary>
    public Fsrs6Weights Clone() => new((double[])_weights.Clone(), Source, OptimizedAtUtc);

    /// <summary>与另一组权重逐项相等（按位比较 double；用于往返一致性断言）。</summary>
    public bool ValueEquals(Fsrs6Weights? other)
    {
        if (other is null) return false;
        if (!string.Equals(Source, other.Source, StringComparison.Ordinal)) return false;
        if (OptimizedAtUtc != other.OptimizedAtUtc) return false;
        for (var i = 0; i < ParameterCount; i++)
        {
            if (!_weights[i].Equals(other._weights[i])) return false;
        }
        return true;
    }

    public override string ToString() =>
        $"FSRS-6[{Source}]{string.Join(", ", _weights.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}";

    private static DateTime? NormalizeUtc(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
    };

    /// <summary>持久化 DTO。属性名经 camelCase 策略后与 dependency_decision.md 的字段名逐字对应。</summary>
    private sealed class Payload
    {
        public string? Algorithm { get; set; }
        public int ParameterCount { get; set; }
        public int ParameterVersion { get; set; }
        public double[]? Weights { get; set; }
        public string? Source { get; set; }
        public DateTime? OptimizedAtUtc { get; set; }
    }
}
