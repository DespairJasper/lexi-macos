using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lexi;

/// <summary>
/// 个人 FSRS-6 参数的集成层：自动门槛 → 后台训练 → 一致性核对 → **单事务重放发布** → Context 退资格。
///
/// <para><b>它不是什么</b>：本类不拟合、不切分、不做留出验证——那些是
/// <see cref="IFSRSParameterOptimizer"/> 的职责（helper 只拟合训练前缀，C# 侧在未参与训练的未来段上验证，
/// 未获可靠提升必须以异常拒绝，绝不返回 defaults 冒充训练）。本类只负责「什么时候训、训出来的东西
/// 能不能真的换上去、换上去之后整库是不是仍然自洽」。</para>
///
/// <para><b>为什么必须有它</b>：D/S 是 FSRS 模型内部的充分统计量，与产出它的 21 个权重**同源**才有意义。
/// 只把新权重塞进排期器（前向生效）而把已有的 <c>fsrs_cards</c> 与每条 canonical 的可撤销 pre-state
/// 留在旧权重下，会得到「新权重 + 旧 D/S」的混合体：后续复习从一对没有依据的 D/S 继续推，
/// 而撤销回放又会把卡片打回旧基线。因此换版必须是**一次覆盖全库的重放**，
/// 且必须与参数模型状态行落在**同一个 SQLite 事务**里（见 <see cref="ILearningMemoryStore.PublishFsrsParameters"/>）。</para>
///
/// <para><b>失败语义</b>：门槛未满足、无可靠提升、取消、超时、重放异常、epoch 变化、
/// canonical 快照变化——任一情况都**保留 last-good / defaults**，绝不部分切换。
/// 内存中的权重只在 store 事务提交成功之后才更新。</para>
/// </summary>
public sealed class FsrsPersonalization
{
    private readonly ILearningMemoryStore _store;
    private readonly SchedulerWeights _holder;
    private readonly IFSRSParameterOptimizer _optimizer;
    private readonly FsrsPersonalizationOptions _options;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();

    /// <summary>当前生效的参数版本戳（来自 store；无个人参数时为 <see cref="SchedulingConfig.FsrsParameterVersionPrefix"/>）。</summary>
    private string _activeParameterVersion = SchedulingConfig.FsrsParameterVersionPrefix;

    /// <summary>当前基线的生效时刻；null = 官方 defaults 基线（没有"从某刻起"的概念，不参与样本过滤）。</summary>
    private DateTime? _activeSinceUtc;

    /// <summary>上一次训练**尝试**的时刻（成功、无提升、门槛未满足都算一次尝试）。</summary>
    private DateTime? _lastAttemptUtc;
    private DateTime? _lastFailedUtc;
    /// <summary>上次**真实**训练尝试时的合格目标数（增量门槛的水位；等待/禁用/冷却一律不推进）。</summary>
    private int _reviewsAtLastAttempt;

    /// <summary>
    /// 上一次**因快照变化被拒绝发布**时的 canonical 快照签名。
    /// 它的作用不是"记住失败"，而是"允许一次针对变动快照的重训"：只要签名已经不同于它，
    /// 增量门槛就被豁免一次（数据基础变了，旧水位不再有意义）；相同则按常规门槛走，
    /// 避免同一份失败快照被每轮重跑。null = 没有待处理的拒绝。
    /// </summary>
    private string? _rejectedSnapshotSignature;

    private string _lastAttemptDetail = "";

    public FsrsPersonalization(
        ILearningMemoryStore store,
        SchedulerWeights holder,
        IFSRSParameterOptimizer optimizer,
        FsrsPersonalizationOptions? options = null,
        Func<DateTime>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
        _optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
        _options = options ?? new FsrsPersonalizationOptions();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public SchedulerWeights Weights => _holder;

    /// <summary>本实例生效的开关与门槛（只读；测试/诊断据此断言，不另行造常量）。</summary>
    public FsrsPersonalizationOptions Options => _options;

    /// <summary>训练器是否真的能训练。false = 只有 defaults，这是**契约允许**的状态，不是错误。</summary>
    public bool OptimizerImplemented => _optimizer.IsImplemented;

    public string ActiveParameterVersion { get { lock (_gate) return _activeParameterVersion; } }

    /// <summary>当前基线的生效时刻（UTC）；null = 官方 defaults 基线。</summary>
    public DateTime? ActiveSinceUtc { get { lock (_gate) return _activeSinceUtc; } }

    public DateTime? LastAttemptUtc { get { lock (_gate) return _lastAttemptUtc; } }

    public DateTime? LastFailedUtc { get { lock (_gate) return _lastFailedUtc; } }

    public int ReviewsAtLastAttempt { get { lock (_gate) return _reviewsAtLastAttempt; } }

    public string LastAttemptDetail { get { lock (_gate) return _lastAttemptDetail; } }

    // ==================================================================================
    // 门槛（纯判定：不读时钟、不碰 store、无副作用）
    // ==================================================================================

    /// <summary>
    /// 数据量 / 跨度 / 新增量 / 冷却四项门槛的**纯判定**。全部条件同时成立才返回 true。
    /// <list type="bullet">
    /// <item>已有训练在途 → false（绝不并发第二次）。</item>
    /// <item>有效 canonical 条数 &lt; <see cref="FsrsPersonalizationOptions.MinimumReviews"/> → false（只收集）。</item>
    /// <item>首末 <c>reviewed_at_utc</c> 跨度 &lt; <see cref="FsrsPersonalizationOptions.MinimumSpanDays"/> → false。</item>
    /// <item>距上次尝试的新增条数 &lt; <see cref="FsrsPersonalizationOptions.MinimumNewReviews"/> → false
    /// （否则数据不再增长时会被冷却反复唤醒、用同一批数据重复训练）。</item>
    /// <item>距上次尝试（成功或失败）不足 <see cref="FsrsPersonalizationOptions.Cooldown"/> → false。</item>
    /// </list>
    /// <para>时钟回拨时 <c>now - last</c> 为负，会落在「不足冷却」一侧 → 保守地不训练（不做补偿）：
    /// 宁可少训一次，也不因为系统时间异常而反复全表读。</para>
    /// </summary>
    public static bool ShouldTrain(
        DateTime nowUtc, DateTime? lastAttemptUtc, DateTime? lastFailedUtc, bool hasPendingTask,
        int reviewCount, double spanDays, int newReviewsSinceLastAttempt,
        FsrsPersonalizationOptions options)
        => ShouldTrain(nowUtc, lastAttemptUtc, lastFailedUtc, hasPendingTask,
            reviewCount, spanDays, newReviewsSinceLastAttempt, basisChanged: false, options);

    /// <summary>
    /// 与上一条同义，额外接受 <paramref name="basisChanged"/>：训练快照的基础（canonical 集合或手动覆盖）
    /// 已经不同于上次尝试时的那一份。
    /// <para>为什么需要它：增量门槛（新增 N 条才重训）在两种情况下会把人锁死——
    /// ① 发布被拒（快照在训练期间变了）后，同一份已经变化的快照永远凑不满增量；
    /// ② 撤销让目标数**下降**到旧水位以下，差值变成负数，越等越远。
    /// 数据基础已经变了，旧水位就不再是有意义的比较基准，因此豁免一次增量要求；
    /// 冷却仍然生效，所以不会退化成"每轮重跑"。</para>
    /// </summary>
    public static bool ShouldTrain(
        DateTime nowUtc, DateTime? lastAttemptUtc, DateTime? lastFailedUtc, bool hasPendingTask,
        int reviewCount, double spanDays, int newReviewsSinceLastAttempt, bool basisChanged,
        FsrsPersonalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return false;
        if (hasPendingTask) return false;
        // 条数与跨度都按**真实训练目标**统计（与训练器的 target 规则一致），不是 history.Count。
        if (reviewCount < options.MinimumReviews) return false;
        if (!MemoryScheduler.IsFinite(spanDays) || spanDays < options.MinimumSpanDays) return false;
        if (newReviewsSinceLastAttempt < options.MinimumNewReviews && !basisChanged) return false;
        if (lastAttemptUtc is { } attempted && nowUtc - attempted < options.Cooldown) return false;
        if (lastFailedUtc is { } failed && nowUtc - failed < options.Cooldown) return false;
        return true;
    }

    /// <summary>
    /// 当前训练快照的基础是否已经不同于上次尝试时的那一份。
    /// <list type="bullet">
    /// <item>目标数下降（撤销/失效造成）：旧水位已经没有比较意义 → 允许重训。</item>
    /// <item>上次发布因快照变化被拒，且**当前签名已经不同于那份被拒快照** → 允许一次针对变动快照的重训；
    /// 签名仍相同则不放行（"用 snapshot token 防同一失败快照每轮重跑"）。</item>
    /// </list>
    /// </summary>
    private bool BasisChanged(int targetCount, string signature)
    {
        lock (_gate)
        {
            if (targetCount < _reviewsAtLastAttempt) return true;
            return _rejectedSnapshotSignature is not null
                && !string.Equals(_rejectedSnapshotSignature, signature, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// **廉价前置闸**：只开关 / 在途 / 冷却——这三项光凭内存就能断定，不需要碰数据库。
    /// 调用方先过它，可以避免每一次轮末都为"数据量够不够"而全表扫一遍历史。
    /// 注意它**不是**完整门槛：返回 false 只代表"冷却与开关允许"，数据量仍要另判。
    /// </summary>
    public bool CooldownBlocks(DateTime nowUtc, bool hasPendingTask)
    {
        if (!_options.Enabled) return true;
        if (hasPendingTask) return true;
        var now = NormalizeUtc(nowUtc);
        lock (_gate)
        {
            if (_lastAttemptUtc is { } attempted && now - attempted < _options.Cooldown) return true;
            if (_lastFailedUtc is { } failed && now - failed < _options.Cooldown) return true;
        }
        return false;
    }

    /// <summary>廉价前置闸拒绝时的可审计原因（只进诊断）。</summary>
    public string CooldownDetail(DateTime nowUtc, bool hasPendingTask)
    {
        if (!_options.Enabled) return "个人参数训练已关闭";
        if (hasPendingTask) return "已有一轮训练在途";
        var now = NormalizeUtc(nowUtc);
        DateTime? attempted, failed;
        lock (_gate) { attempted = _lastAttemptUtc; failed = _lastFailedUtc; }
        if (failed is { } failedAt && now - failedAt < _options.Cooldown)
            return $"距上次训练失败 {Math.Max(0, (now - failedAt).TotalMinutes):F0} 分钟，"
                + $"未过 {_options.Cooldown.TotalMinutes:F0} 分钟冷却";
        if (attempted is { } attemptedAt && now - attemptedAt < _options.Cooldown)
            return $"距上次训练 {Math.Max(0, (now - attemptedAt).TotalMinutes):F0} 分钟，"
                + $"未过 {_options.Cooldown.TotalMinutes:F0} 分钟冷却";
        return "冷却已过";
    }

    /// <summary>「为什么这次不训练」的可审计说明（只进诊断，绝不进界面）。</summary>
    public string SkipReason(DateTime nowUtc, bool hasPendingTask, int reviewCount, double spanDays, int newReviews)
    {
        if (!_options.Enabled) return "个人参数训练已关闭";
        if (hasPendingTask) return "已有一轮训练在途";
        if (reviewCount < _options.MinimumReviews)
            return $"有效复习 {reviewCount} < {_options.MinimumReviews}（保持默认参数，只收集）";
        if (spanDays < _options.MinimumSpanDays)
            return $"历史跨度 {spanDays:F0} 天 < {_options.MinimumSpanDays} 天（只收集）";
        if (newReviews < _options.MinimumNewReviews)
            return $"新增复习 {newReviews} < {_options.MinimumNewReviews}（同一批数据不重复训练）";
        var last = LastAttemptUtc;
        var failed = LastFailedUtc;
        if (failed is { } failedAt && nowUtc - failedAt < _options.Cooldown)
            return $"距上次训练失败 {Math.Max(0, (nowUtc - failedAt).TotalMinutes):F0} 分钟，"
                + $"未过 {_options.Cooldown.TotalMinutes:F0} 分钟冷却";
        if (last is { } attempted && nowUtc - attempted < _options.Cooldown)
            return $"距上次训练 {Math.Max(0, (nowUtc - attempted).TotalMinutes):F0} 分钟，"
                + $"未过 {_options.Cooldown.TotalMinutes:F0} 分钟冷却";
        return "满足训练条件";
    }

    // ==================================================================================
    // 版本戳
    // ==================================================================================

    /// <summary>
    /// 参数版本戳：**按权重内容确定**，因此「换版」必然换戳、审计列不会撒谎。
    /// 官方 defaults 恒定使用冻结常量 <see cref="SchedulingConfig.FsrsParameterVersionPrefix"/>（历史兼容：
    /// 换版前落库的卡片全是这个戳，重启核对时不能被判成"版本不一致"而去重放一遍）。
    /// </summary>
    public static string ParameterVersionOf(Fsrs6Weights? weights)
    {
        if (weights is null) return SchedulingConfig.FsrsParameterVersionPrefix;
        if (string.Equals(weights.Source, Fsrs6Weights.SourceDefaults, StringComparison.Ordinal)
            && weights.ValueEquals(Fsrs6Weights.Defaults))
            return SchedulingConfig.FsrsParameterVersionPrefix;
        return SchedulingConfig.FsrsParameterVersionPrefix + "-w" + WeightsFingerprint(weights);
    }

    /// <summary>21 个权重的稳定指纹（"R" 往返格式 + UTF-8 + SHA-256 前 16 个十六进制字符）。
    /// 用 "R" 而不是默认 <c>ToString()</c>：后者受当前区域设置影响，会让同一组权重在不同机器上得到不同版本戳。</summary>
    public static string WeightsFingerprint(Fsrs6Weights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var canonical = string.Join(",", weights.ToArray()
            .Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    // ==================================================================================
    // 启动恢复：从 store 恢复参数与阈值记账，并做一次一致性核对
    // ==================================================================================

    /// <summary>
    /// 从 store 恢复：① 最近一次发布的有效参数（解析失败 → last-good → defaults）；
    /// ② 训练尝试记账（冷却与新增量门槛必须**跨重启**生效，否则每次启动都会立刻重训一遍）。
    /// 本方法只读，不改库；一致化重放在 <see cref="Reconcile"/> 里做。
    /// </summary>
    public FsrsParameterRestoreResult Restore()
    {
        var detail = new StringBuilder();
        Fsrs6Weights weights = Fsrs6Weights.Defaults;
        var version = SchedulingConfig.FsrsParameterVersionPrefix;
        DateTime? activeSince = null;
        string? helperVersion = null;
        string? helperSha = null;

        PersonalizationModelState? published = null;
        IReadOnlyList<PersonalizationModelState> rows;
        try { rows = _store.LoadPersonalizationModels(SchedulingConfig.FsrsParameterModelKind, _options.HistoryScanLimit); }
        catch (Exception ex)
        {
            rows = [];
            detail.Append($"个人参数历史读取失败（{ex.GetType().Name}：{ex.Message}）；按 defaults 继续。");
        }

        var resolved = ResolveEffectiveWeights(rows);
        helperVersion = resolved.HelperVersion;
        helperSha = resolved.HelperSha;
        weights = resolved.Weights;
        version = resolved.ParameterVersion;
        activeSince = resolved.ActiveSinceUtc;
        published = resolved.Row;
        detail.Append(resolved.Detail);

        // 训练记账：只认**真实**尝试行。旧版本把"等待数据/冷却/未实现"也写成记账行，
        // 那些行会伪装成"上次尝试"，让增量水位与冷却在没有真正训练过的情况下被推进——
        // 按 counted 标记 + outcome 双重排除，跨重启也不会复活这个 bug。
        foreach (var row in rows)
        {
            var kind = ReadMetricString(row.MetricsJson, SchedulingConfig.FsrsParameterRecordKindKey);
            if (!string.Equals(kind, SchedulingConfig.FsrsParameterRecordKindAttempt, StringComparison.Ordinal)) continue;
            if (!ReadMetricBool(row.MetricsJson, "counted")) continue;
            _lastAttemptUtc = row.TrainedAtUtc;
            // 冷却读取最近一次真实尝试；增量水位另按是否真正消费数据恢复。
            _lastAttemptDetail = ReadMetricString(row.MetricsJson, "detail");
            if (ReadMetricBool(row.MetricsJson, "failed")) _lastFailedUtc = row.TrainedAtUtc;
            var rejected = ReadMetricString(row.MetricsJson, "rejectedSignature");
            if (!string.IsNullOrEmpty(rejected)) _rejectedSnapshotSignature = rejected;
            break;
        }

        _reviewsAtLastAttempt = 0;
        foreach (var row in rows)
        {
            var kind = ReadMetricString(row.MetricsJson, SchedulingConfig.FsrsParameterRecordKindKey);
            if (kind == SchedulingConfig.FsrsParameterRecordKindAttempt && ReadMetricBool(row.MetricsJson, "counted"))
            {
                if (!string.IsNullOrEmpty(ReadMetricString(row.MetricsJson, "reviewWatermark")))
                {
                    _reviewsAtLastAttempt = ReadMetricInt(row.MetricsJson, "reviewWatermark");
                    break;
                }
                if (ReadMetricBool(row.MetricsJson, "advanceWatermark"))
                {
                    _reviewsAtLastAttempt = ReadMetricInt(row.MetricsJson, "reviewCount");
                    break;
                }
                // 旧 counted/advance=false 行只记冷却，不吞掉未发布或取消的训练数据。
            }
            else if (TryReadPublishedWeights(row, out _)
                && !string.IsNullOrEmpty(ReadMetricString(row.MetricsJson, "trainingTargetCount")))
            {
                _reviewsAtLastAttempt = ReadMetricInt(row.MetricsJson, "trainingTargetCount");
                break;
            }
        }

        lock (_gate)
        {
            _activeParameterVersion = version;
            _activeSinceUtc = activeSince;
        }
        _holder.PublishDefaultIfUnset(weights);
        return new FsrsParameterRestoreResult(
            weights, version, activeSince, helperVersion, helperSha, detail.ToString(),
            PublishedParametersFound: published is not null,
            IsDefaultBaseline: weights.Source == Fsrs6Weights.SourceDefaults);
    }

    /// <summary>
    /// 重启一致性核对：若库里存在参数版本戳与当前基线不符的卡片，就用当前基线权重**一致重放**一遍
    /// （同一事务重写 cards 与 pre-state），使「权重 ↔ D/S ↔ 版本戳」三者重新同源。
    /// 无差异时**不做任何写入**（正常路径的常数代价 = 一次 COUNT）。
    /// </summary>
    public FsrsParameterReconcileResult Reconcile(CancellationToken cancellationToken = default)
    {
        string version;
        Fsrs6Weights weights;
        lock (_gate) { version = _activeParameterVersion; }
        weights = _holder.Current;

        bool inconsistent;
        try { inconsistent = _store.HasCardsOutsideParameterVersion(version); }
        catch (Exception ex)
        {
            return new FsrsParameterReconcileResult(false, false,
                $"一致性核对失败（{ex.GetType().Name}：{ex.Message}）；不重放、不改动任何卡片。");
        }
        if (!inconsistent)
            return new FsrsParameterReconcileResult(true, false, $"卡片与参数版本 {version} 一致，无需重放。");

        try
        {
            var history = LoadReplayHistory();
            var overrides = _store.LoadManualDueOverrides();
            var signature = _store.ComputeCanonicalSignature();
            var plan = BuildReplayPlan(history, weights, overrides, cancellationToken);
            var publication = new FsrsParameterPublication(
                ModelVersion: version,
                TrainedAtUtc: DateTime.SpecifyKind(_clock(), DateTimeKind.Utc),
                TrainCutoffUtc: _activeSinceUtc ?? DateTime.SpecifyKind(_clock(), DateTimeKind.Utc),
                CoefficientsJson: weights.ToJson(),
                MetricsJson: BuildReconcileMetrics(version, weights, history.Count),
                ParameterVersion: version,
                CanonicalSignature: signature,
                Words: plan,
                RecordKind: SchedulingConfig.FsrsParameterRecordKindParams);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _store.PublishFsrsParameters(publication);
            if (!result.Applied)
                return new FsrsParameterReconcileResult(false, false,
                    "一致性重放被拒绝（canonical 快照在核对与提交之间发生变化）；卡片保持不变。");
            return new FsrsParameterReconcileResult(true, true,
                $"已用参数版本 {version} 一致重放 {result.WordsReplayed} 个词 / {result.CanonicalsRewritten} 条 canonical。");
        }
        catch (Exception ex)
        {
            return new FsrsParameterReconcileResult(false, false,
                $"一致性重放失败（{ex.GetType().Name}：{ex.Message}）；卡片与参数版本可能不同源，已停止自动训练。");
        }
    }

    /// <summary>
    /// **唯一**的有效个人参数解析口径：最近一行能真正解析出合法权重的发布行。
    /// <para>Context 与集成层必须用同一个解析器：Context 若只看 metrics 里的版本字符串，
    /// 就会在"coefficients 损坏但 metrics 版本完好"时绑定到一个**根本没生效**的基线，
    /// 从而把旧 Active 模型沿用到实际已经回退的权重上。</para>
    /// </summary>
    public static FsrsEffectiveParameters ResolveEffectiveWeights(IReadOnlyList<PersonalizationModelState> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        PersonalizationModelState? published = null;
        PersonalizationModelState? lastGood = null;
        foreach (var row in rows)
        {
            var kind = FsrsPersonalization.ReadMetricString(row.MetricsJson, SchedulingConfig.FsrsParameterRecordKindKey);
            if (string.Equals(kind, SchedulingConfig.FsrsParameterRecordKindAttempt, StringComparison.Ordinal)) continue;
            if (published is null && TryReadPublishedWeights(row, out _)) published = row;
            if (lastGood is null) lastGood = row;
        }

        if (published is not null && TryReadPublishedWeights(published, out var parsed))
        {
            var version = ParameterVersionOf(parsed);
            var since = ReadMetricUtc(published.MetricsJson, SchedulingConfig.FsrsParameterActiveSinceMetricKey)
                ?? published.TrainedAtUtc;
            return new FsrsEffectiveParameters(parsed!, version, since, published,
                ReadMetricString(published.MetricsJson, "helperVersion"),
                ReadMetricString(published.MetricsJson, "helperSha"),
                $"已恢复参数版本 {version}（{parsed!.Source}）。");
        }

        if (lastGood is not null && TryReadPublishedWeights(lastGood, out var recovered))
        {
            var version = ParameterVersionOf(recovered);
            var since = ReadMetricUtc(lastGood.MetricsJson, SchedulingConfig.FsrsParameterActiveSinceMetricKey)
                ?? lastGood.TrainedAtUtc;
            return new FsrsEffectiveParameters(recovered!, version, since, lastGood, null, null,
                "最近一次发布的个人参数不可解析，已回退到 last-good 参数；");
        }

        return new FsrsEffectiveParameters(Fsrs6Weights.Defaults, SchedulingConfig.FsrsParameterVersionPrefix,
            null, null, null, null,
            rows.Count > 0 ? "个人参数模型不可解析且找不到可用 last-good，已回退到官方默认参数；" : "");
    }

    private static bool TryReadPublishedWeights(PersonalizationModelState row, out Fsrs6Weights? weights)
    {
        weights = null;
        var kind = ReadMetricString(row.MetricsJson, SchedulingConfig.FsrsParameterRecordKindKey);
        if (string.Equals(kind, SchedulingConfig.FsrsParameterRecordKindAttempt, StringComparison.Ordinal)) return false;
        if (!Fsrs6Weights.TryLoad(row.CoefficientsJson, out weights, out _)) return false;
        if (string.Equals(weights!.Source, Fsrs6Weights.SourceDefaults, StringComparison.Ordinal)
            && !weights.ValueEquals(Fsrs6Weights.Defaults)) return false;
        var version = ParameterVersionOf(weights);
        // 版本戳是**按权重派生**的：行里记录的版本与权重算出来的不一致，说明行被改过或权重被换过，
        // 这种行不能当作可信基线（宁可走 last-good / defaults + 一致性重放）。
        var recorded = ReadMetricString(row.MetricsJson, SchedulingConfig.FsrsParameterVersionMetricKey);
        if (!string.IsNullOrEmpty(recorded) && !string.Equals(recorded, version, StringComparison.Ordinal)) return false;
        return true;
    }

    private string BuildReconcileMetrics(string version, Fsrs6Weights weights, int canonicalCount)
    {
        var payload = new Dictionary<string, object?>
        {
            [SchedulingConfig.FsrsParameterRecordKindKey] = SchedulingConfig.FsrsParameterRecordKindParams,
            [SchedulingConfig.FsrsParameterVersionMetricKey] = version,
            ["weightsHash"] = WeightsFingerprint(weights),
            ["weightsSource"] = weights.Source,
            ["reconcile"] = true,
            ["canonicalCount"] = canonicalCount,
            ["replayProtocol"] = ReplayProtocol,
            ["trainedAtUtc"] = _clock().ToString("O"),
        };
        return JsonSerializer.Serialize(payload);
    }

    // ==================================================================================
    // 训练
    // ==================================================================================

    /// <summary>
    /// 跑一轮自动训练（**到候选为止，不发布**）。时间由参数传入，本层不读时钟。
    /// <list type="bullet">
    /// <item><see cref="FsrsParameterTrainingOutcome.Disabled"/>：开关关闭。</item>
    /// <item><see cref="FsrsParameterTrainingOutcome.NotImplemented"/>：训练器未实现（合法的中间状态）。</item>
    /// <item><see cref="FsrsParameterTrainingOutcome.Cooldown"/>：冷却 / 在途，本轮跳过（不是失败）。</item>
    /// <item><see cref="FsrsParameterTrainingOutcome.WaitingForData"/>：门槛未满足，只收集。</item>
    /// <item><see cref="FsrsParameterTrainingOutcome.NoImprovement"/>：训练器明确拒绝（未获可靠提升、样本不足等）。</item>
    /// <item><see cref="FsrsParameterTrainingOutcome.SafeFallback"/>：取消 / 超时 / 未知异常 → 保留 last-good。</item>
    /// <item><see cref="FsrsParameterTrainingOutcome.Trained"/>：得到可发布候选。</item>
    /// </list>
    /// <para><b>线程纪律</b>：store 读取（历史快照 + canonical 快照签名 + 记账）全部发生在**调用线程**上；
    /// 纯 CPU 的构造与拟合整体交给 <c>Task.Run</c>/训练器自己，本方法不含 <c>ConfigureAwait(false)</c>，
    /// 因此 await 之后仍在调用方上下文（UI 线程）上续跑——那正是后面写库所要求的线程。</para>
    /// </summary>
    public async Task<FsrsParameterTrainingResult> TrainAsync(
        DateTime nowUtc, bool hasPendingTask, CancellationToken cancellationToken = default)
    {
        var now = NormalizeUtc(nowUtc);
        if (!_options.Enabled)
            return Deferred(FsrsParameterTrainingOutcome.Disabled, "个人参数训练已关闭。", now);
        if (!_optimizer.IsImplemented)
            return Deferred(FsrsParameterTrainingOutcome.NotImplemented,
                "个人 FSRS 参数训练器尚未实现；保持官方默认参数（这是契约允许的状态，不是错误）。", now);

        // —— 廉价前置闸：条数是"真实目标数"的**上界**，跨度也是目标跨度的上界 ——
        // 用它确定地否定"数据明显不够"的情形，避免每个轮末都整表物化历史。
        // 注意：这里既不推进水位也不动冷却——等待数据不是一次训练尝试。
        DateTime? lastAttempt0;
        DateTime? lastFailed0;
        int watermark;
        lock (_gate) { lastAttempt0 = _lastAttemptUtc; lastFailed0 = _lastFailedUtc; watermark = _reviewsAtLastAttempt; }
        // 瞬时故障（取消/超时/未产出裁决/读失败）不改动门槛状态：水位不动，**被拒快照令牌也不清**。
        // 清掉它等于顺手剥夺"快照已变即豁免增量门槛"的资格，用户会被迫再攒 N 条——与本次修复同一个症状。
        var preservedRejectionToken = _rejectedSnapshotSignature;
        if (hasPendingTask || (lastAttempt0 is { } a0 && now - a0 < _options.Cooldown)
            || (lastFailed0 is { } f0 && now - f0 < _options.Cooldown))
            return new FsrsParameterTrainingResult(FsrsParameterTrainingOutcome.Cooldown, null,
                CooldownDetail(now, hasPendingTask));

        try
        {
            if (_store.LoadCanonicalBounds(now) is { } bounds
                && (bounds.Count < _options.MinimumReviews
                    || SpanOf(bounds) < _options.MinimumSpanDays))
                return Deferred(FsrsParameterTrainingOutcome.WaitingForData,
                    $"有效 canonical {bounds.Count} 条 / 跨度 {SpanOf(bounds):F0} 天，明显低于门槛"
                    + $"（{_options.MinimumReviews} 条 / {_options.MinimumSpanDays} 天）；只收集，不消耗新增水位与冷却。",
                    now);
        }
        catch (Exception ex)
        {
            return new FsrsParameterTrainingResult(FsrsParameterTrainingOutcome.SafeFallback, null,
                $"训练输入范围查询失败（{ex.GetType().Name}：{ex.Message}）；本轮不做任何记账。");
        }

        IReadOnlyList<CanonicalHistoryRow> history;
        string signature;
        long startEpoch;
        Fsrs6Weights frozenBaseline;
        string activeVersion;
        try
        {
            // 快照（历史 + 签名）必须与冻结的 baseline/epoch **同一读取时段**：这之间不允许 await，
            // 否则训练用的标签与对照权重会来自两个不同的时刻。
            history = LoadTrainingHistory(now);
            signature = _store.ComputeCanonicalSignature();
            lock (_gate)
            {
                startEpoch = _holder.Epoch;
                frozenBaseline = _holder.Current;
                activeVersion = _activeParameterVersion;
            }
        }
        catch (Exception ex)
        {
            // 读取失败是**瞬时**故障：与取消/超时同口径，冷却照记、水位一动不动。
            // （原实现用缺省 advanceWatermark:true + targetCount 0，会把水位**清零**并落库，
            //  与 :1031 自述的"瞬时失败水位不动"直接矛盾，且会让增量门槛在下次启动后失效。）
            return Record(FsrsParameterTrainingOutcome.SafeFallback,
                $"训练输入读取失败（{ex.GetType().Name}：{ex.Message}）。", now, 0, 0,
                failed: true, null, preservedRejectionToken, advanceWatermark: false);
        }

        var targets = CountTrainingTargets(history);
        var basisChanged = BasisChanged(targets.Count, signature);
        var newTargets = targets.Count - watermark;
        if (!ShouldTrain(now, lastAttempt0, lastFailed0, false,
                targets.Count, targets.SpanDays, newTargets, basisChanged, _options))
        {
            // 门槛未满足 = **等待**，不是尝试：不推进水位、不推进冷却、不写任何记账行。
            return Deferred(FsrsParameterTrainingOutcome.WaitingForData,
                SkipReason(now, false, targets.Count, targets.SpanDays, newTargets)
                + (basisChanged ? "（数据基础已变化，增量门槛本轮豁免）" : ""), now);
        }

        // 对照基线用**冻结**的这一份（不是 await 之后重新读的当前值）：
        // 训练期间若发生换版，候选的对照与 epoch 都必须还是训练开始时的语义，
        // 否则"比现在更好"会被拿去和另一个版本比较。
        if (_optimizer is FsrsParameterOptimizer configurable)
        {
            configurable.Options.BaselineWeights = frozenBaseline;
            configurable.Options.CompletedAtCutoff = now;
        }

        var input = BuildOptimizerInput(history);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TrainingTimeout);

        Fsrs6Weights trained;
        try
        {
            // 只读快照已经取完；纯优化（含子进程）整体离开调用线程。
            trained = await Task.Run(() => _optimizer.OptimizeAsync(input, timeout.Token), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // 取消/超时是**瞬时**的：记冷却但**不推进水位**，让冷却过后同一批数据还能重试，
            // 而不是逼用户再攒 N 条新目标才允许再试一次。
            return Record(FsrsParameterTrainingOutcome.SafeFallback,
                "训练被取消或超过墙钟预算；保留 last-good。", now, targets.Count, targets.SpanDays,
                failed: true, null, preservedRejectionToken, advanceWatermark: false);
        }
        catch (FsrsOptimizerUnavailableException ex)
        {
            // helper **没有产出裁决**（进程没起来 / 退出码不是裁决码 / 响应损坏 / metadata 与固定版本不符 /
            // 执行期异常）：这批数据**从未被评估过**，因此只记冷却、**不推进**新增量水位——
            // 冷却过后同一批数据仍然可以重训，不会逼用户再攒 N 条新目标。
            return Record(FsrsParameterTrainingOutcome.SafeFallback,
                "训练器未产出裁决：" + ex.Message, now, targets.Count, targets.SpanDays,
                failed: true, null, preservedRejectionToken, advanceWatermark: false);
        }
        catch (FsrsOptimizationException ex)
        {
            // 训练器的**明确拒绝**（未获可靠提升 / 样本不足 / 输入形状与预算拒绝）：数据已被评估过或输入
            // 形状本身被确定性地拒绝，因此推进水位——否则同一批数据会被冷却反复唤醒、每小时重跑一次子进程。
            return Record(FsrsParameterTrainingOutcome.NoImprovement,
                "训练器拒绝本次候选：" + ex.Message, now, targets.Count, targets.SpanDays, failed: true, null, null);
        }
        catch (Exception ex)
        {
            // 未预期的异常通常是**瞬时**的（进程/IO/线程），不推进水位，让冷却过后能用同一批数据重试。
            return Record(FsrsParameterTrainingOutcome.SafeFallback,
                $"训练异常（{ex.GetType().Name}：{ex.Message}）；保留 last-good。",
                now, targets.Count, targets.SpanDays, failed: true, null, preservedRejectionToken, advanceWatermark: false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (trained is null)
            return Record(FsrsParameterTrainingOutcome.SafeFallback,
                "训练返回了 null 权重；保留 last-good。", now, targets.Count, targets.SpanDays, failed: true, null, null,
                advanceWatermark: false);
        if (!Fsrs6Weights.TryValidate(trained.ToArray(), out var validateError))
            return Record(FsrsParameterTrainingOutcome.SafeFallback,
                "训练返回的权重非法：" + validateError + "；保留 last-good。",
                now, targets.Count, targets.SpanDays, failed: true, null, null, advanceWatermark: false);

        var version = ParameterVersionOf(trained);

        // await 之后的 epoch 核对：训练期间若已发生另一次发布，本次候选的对照基线已经过时。
        lock (_gate)
        {
            if (_holder.Epoch != startEpoch)
                return Record(FsrsParameterTrainingOutcome.SafeFallback,
                    $"训练期间参数 epoch 由 {startEpoch} 变为 {_holder.Epoch}（另一次发布已发生）；"
                    + "本次候选作废，不据过时对照发布。",
                    now, targets.Count, targets.SpanDays, failed: true, null, null, advanceWatermark: false);
        }

        if (string.Equals(version, activeVersion, StringComparison.Ordinal))
        {
            return Record(FsrsParameterTrainingOutcome.NoImprovement,
                $"候选权重与当前生效参数版本 {activeVersion} 完全一致，无需发布。",
                now, targets.Count, targets.SpanDays, failed: false, null, null);
        }

        var candidate = new FsrsParameterCandidate(
            Weights: trained,
            ParameterVersion: version,
            StartEpoch: startEpoch,
            BaselineVersion: activeVersion,
            CanonicalSignature: signature,
            TrainedAtUtc: now,
            ReviewCount: targets.Count,
            SpanDays: targets.SpanDays,
            MetricsJson: BuildCandidateMetrics(trained, version, targets, history.Count, signature, frozenBaseline));

        // 产出候选**不**推进水位：此刻还没有把这份数据"用掉"——候选可能因 UI 忙被延后、
        // 因快照变化被拒、或因进程退出而丢失。只有当它真的发布成功（或训练器明确评估过这批数据）时，
        // 水位才前进。否则一次作废的候选会永久吃掉增量。
        return Record(FsrsParameterTrainingOutcome.Trained,
            $"候选参数版本 {version} 已通过训练器验证（真实正间隔目标 {targets.Count} 条 / 跨度 {targets.SpanDays:F0} 天），等待发布。",
            now, targets.Count, targets.SpanDays, failed: false, candidate, null, advanceWatermark: false);
    }

    private static double SpanOf(CanonicalHistoryBounds bounds) =>
        bounds.FirstReviewedAtUtc is { } from && bounds.LastReviewedAtUtc is { } to ? (to - from).TotalDays : 0.0;

    /// <summary>
    /// 把候选发布下去：epoch 检查 → **快照必须原样未变** → 单事务重放 → 内存换版 → Context 退资格。
    /// <para><b>为什么快照一变就拒绝（而不是用新历史重放）</b>：候选是训练器在**那一份**历史上验证过的
    /// ——它在留出未来段上"更好"这个结论，只对那批样本成立。训练期间发生的撤销/改判会让其中一部分
    /// 标签失效，此时用新历史重放等于把"未经本次验证的权重"发布出去。保守且最小的做法是拒绝，
    /// 由调用方记下拒绝令牌，允许之后针对**已经变化**的快照重训一次（冷却仍生效）。</para>
    /// </summary>
    public async Task<FsrsParameterPublishResult> PublishAsync(
        FsrsParameterCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        long epoch;
        string activeVersion;
        lock (_gate) { epoch = _holder.Epoch; activeVersion = _activeParameterVersion; }
        if (epoch != candidate.StartEpoch)
            return Rejected(FsrsParameterPublishRejection.EpochChanged,
                $"训练期间参数 epoch 已从 {candidate.StartEpoch} 变为 {epoch}（另一次发布已发生）；本次候选作废。");
        if (string.Equals(activeVersion, candidate.ParameterVersion, StringComparison.Ordinal))
            return Rejected(FsrsParameterPublishRejection.AlreadyActive,
                $"参数版本 {activeVersion} 已经是当前生效版本；无需重复发布。");

        IReadOnlyList<CanonicalHistoryRow> history;
        IReadOnlyList<FsrsManualDueOverride> overrides;
        string signature;
        try
        {
            // 历史、手动覆盖与签名必须在**同一读取时段**读出，中间不允许 await：
            // 三者共同定义"这次重放所依赖的输入"，读歪一个就会出现签名与实际输入不一致。
            history = LoadReplayHistory();
            overrides = _store.LoadManualDueOverrides();
            signature = _store.ComputeCanonicalSignature();
        }
        catch (Exception ex)
        {
            return Rejected(FsrsParameterPublishRejection.Failed,
                $"重放输入读取失败（{ex.GetType().Name}：{ex.Message}）；保留 last-good。");
        }

        if (!string.Equals(signature, candidate.CanonicalSignature, StringComparison.Ordinal))
            return Rejected(FsrsParameterPublishRejection.StaleSnapshot,
                "训练快照与当前有效 canonical 集合/手动 due 覆盖不一致（训练期间发生了定稿、撤销、改判或管理动作）；"
                + "本次候选作废，不做任何写入。");

        IReadOnlyList<FsrsWordReplay> plan;
        try
        {
            var weights = candidate.Weights;
            var frozenOverrides = overrides;
            plan = await Task.Run(() => BuildReplayPlan(history, weights, frozenOverrides, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return Rejected(FsrsParameterPublishRejection.Failed, "发布前的重放被取消；保留 last-good。");
        }
        catch (Exception ex)
        {
            return Rejected(FsrsParameterPublishRejection.Failed,
                $"重放计算失败（{ex.GetType().Name}：{ex.Message}）；保留 last-good。");
        }

        // 事务之前的最后一道 epoch 检查：内存换版绝不能发生在 store 提交之前。
        lock (_gate)
        {
            if (_holder.Epoch != candidate.StartEpoch)
                return Rejected(FsrsParameterPublishRejection.EpochChanged, "epoch 在重放期间发生变化；本次候选作废。");
        }

        var publication = new FsrsParameterPublication(
            ModelVersion: candidate.ParameterVersion,
            TrainedAtUtc: candidate.TrainedAtUtc,
            TrainCutoffUtc: candidate.TrainedAtUtc,
            CoefficientsJson: candidate.Weights.ToJson(),
            MetricsJson: candidate.MetricsJson,
            ParameterVersion: candidate.ParameterVersion,
            CanonicalSignature: signature,
            Words: plan,
            RecordKind: SchedulingConfig.FsrsParameterRecordKindParams);

        FsrsParameterPublishResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = _store.PublishFsrsParameters(publication);
        }
        catch (Exception ex)
        {
            // 事务已回滚（实现方契约）：内存 weights 一律不动。
            return Rejected(FsrsParameterPublishRejection.Failed,
                $"事务发布失败（{ex.GetType().Name}：{ex.Message}）；已回滚，保留 last-good。");
        }

        if (!result.Applied)
            return Rejected(FsrsParameterPublishRejection.StaleSnapshot,
                "canonical 快照在重放与提交之间发生变化；本次发布被拒绝，保留 last-good。");

        // —— store 已提交，现在才更新内存 ——
        // activeSince 用 store 在事务内取的**权威发布时刻**，不是训练完成时刻：
        // 候选可能被延后数小时/跨天发布，而 Context 正是按这个时刻过滤快照的。
        var publishedAt = result.PublishedAtUtc ?? DateTime.SpecifyKind(_clock(), DateTimeKind.Utc);
        _holder.Publish(candidate.Weights);
        lock (_gate)
        {
            _activeParameterVersion = candidate.ParameterVersion;
            _activeSinceUtc = publishedAt;
            _lastAttemptUtc = publishedAt;
            _lastFailedUtc = null;
            _reviewsAtLastAttempt = candidate.ReviewCount;
            _rejectedSnapshotSignature = null;
        }
        var contextDetail = DemoteContext(candidate.ParameterVersion, publishedAt);
        return new FsrsParameterPublishResult(true, false,
            $"已发布参数版本 {candidate.ParameterVersion}（发布时刻 {publishedAt:O}；重放 {result.WordsReplayed} 个词 / "
            + $"{result.CanonicalsRewritten} 条 canonical；Context：{contextDetail}）。",
            result.WordsReplayed, result.CanonicalsRewritten, publishedAt);
    }

    /// <summary>
    /// 记下一次**发布拒绝**：只推进冷却与"被拒快照"令牌，**不推进新增水位**。
    /// <para>不推进水位是关键：被拒的候选并没有把这份数据"用掉"，若推进水位就等于让一次作废的候选
    /// 永久吃掉增量（用户再攒够 100 条之前再也训不起来）。令牌则保证同一份失败快照不会每轮重跑，
    /// 而快照一旦变化，门槛里的 basisChanged 会豁免一次增量。</para>
    /// </summary>
    public void NotePublishRejected(FsrsParameterPublishResult result, DateTime nowUtc, string? rejectedSignature = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Rejected) return;
        if (result.Rejection is FsrsParameterPublishRejection.AlreadyActive) return;
        // 记冷却 + 被拒快照令牌，但**不推进水位**：被拒的候选没有把这份数据用掉。
        // 令牌让"同一份失败快照"不会每轮重跑（冷却之外的第二道闸），而快照一旦变化，
        // 门槛里的 basisChanged 会豁免一次增量，于是"允许一次针对变动快照的重训"成立。
        Record(FsrsParameterTrainingOutcome.SafeFallback, "发布被拒绝：" + result.Detail,
            NormalizeUtc(nowUtc), 0, 0, failed: true, null, rejectedSignature, advanceWatermark: false);
    }

    private static FsrsParameterPublishResult Rejected(FsrsParameterPublishRejection reason, string detail) =>
        new(false, true, detail, Rejection: reason);

    /// <summary>把 Context 校准模型降到重新资格：新 baseline 下旧 Active 一律不得沿用。返回可审计说明。</summary>
    private string DemoteContext(string parameterVersion, DateTime activeSinceUtc)
    {
        var calibrator = _contextCalibrator;
        if (calibrator is null) return "未挂载校准器，无需处理";
        try
        {
            calibrator.ApplyFsrsBaseline(parameterVersion, activeSinceUtc);
            return "已降回 ColdStart 并绑定新基线";
        }
        catch (Exception ex)
        {
            return $"降资格失败（{ex.GetType().Name}：{ex.Message}）";
        }
    }

    private IContextCalibrator? _contextCalibrator;

    /// <summary>挂上 Context 校准器：换版成功后由本类负责让它退到重新资格。
    /// 接口成员带默认空实现，因此"不参与 Context 的测试替身"不需要改动。</summary>
    public void AttachContextCalibrator(IContextCalibrator? calibrator) => _contextCalibrator = calibrator;

    /// <summary>当前挂载的 Context 校准器（可能为 null）。</summary>
    public IContextCalibrator? AttachedContext => _contextCalibrator;

    // ==================================================================================
    // 训练输入的读取与快照
    // ==================================================================================

    /// <summary>
    /// 训练历史快照：**未失效** canonical，且**双截止**（<c>ReviewedAtUtc &lt; now</c> 且
    /// <c>CompletedAtUtc &lt; now</c>）。
    /// 双截止不能只靠 store 的 <c>beforeUtc</c>——它只约束 <c>reviewed_at_utc</c>；
    /// 一次「复习发生在 now 之前、定稿发生在 now 之后」的会话如果进去，就是拿尚未确定的结果当训练目标。
    /// </summary>
    public IReadOnlyList<CanonicalHistoryRow> LoadTrainingHistory(DateTime nowUtc)
    {
        var rows = _store.LoadCanonicalHistory(null, nowUtc, null);
        var result = new List<CanonicalHistoryRow>(rows.Count);
        foreach (var row in rows)
        {
            if (row.CompletedAtUtc >= nowUtc) continue;
            result.Add(row);
        }
        return result;
    }

    /// <summary>重放历史：全部**未失效** canonical（不设 completed 截止——卡片必须反映已经落库的一切）。</summary>
    public IReadOnlyList<CanonicalHistoryRow> LoadReplayHistory() => _store.LoadCanonicalHistory(null, MaxUtc, null);

    /// <summary>
    /// **真实训练目标**的条数与跨度。规则与训练适配器构造 target 的规则逐字对齐：
    /// 每个词按 (ReviewedAtUtc, CompletedAtUtc, CanonicalId) 排序，跳过首条（首条只做初始化），
    /// 其后仅 <see cref="CanonicalOrigin.FirstRetrieval"/> 且与上一条 canonical 相隔**整天数 &gt; 0** 的
    /// 才是带正间隔的记忆留存预测目标。
    /// <para>为什么门槛不能用 <c>history.Count</c>：那会把首次学习聚合（acquisition）与同日重复
    /// 状态更新也算进去。一个"每天学 20 个新词、从不复习"的用户能轻易攒到 400 条 canonical，
    /// 但合格目标数是 0——按 history.Count 放行就等于让训练器每次都因样本不足而拒绝，
    /// 冷却被白白消耗，真正的门槛永远不生效。</para>
    /// </summary>
    private static int CompareCanonicalOrder(CanonicalHistoryRow a, CanonicalHistoryRow b)
    {
        var byTime = a.ReviewedAtUtc.CompareTo(b.ReviewedAtUtc);
        if (byTime != 0) return byTime;
        var byKnown = (a.CanonicalSequence > 0 ? 0 : 1).CompareTo(b.CanonicalSequence > 0 ? 0 : 1);
        if (byKnown != 0) return byKnown;
        var bySequence = Math.Max(0, a.CanonicalSequence).CompareTo(Math.Max(0, b.CanonicalSequence));
        return bySequence != 0 ? bySequence : string.CompareOrdinal(a.CanonicalId, b.CanonicalId);
    }

    public static FsrsTargetStats CountTrainingTargets(IReadOnlyList<CanonicalHistoryRow> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var ordered = new List<CanonicalHistoryRow>(history);
        ordered.Sort(static (a, b) =>
        {
            var byWord = string.CompareOrdinal(a.WordKey, b.WordKey);
            if (byWord != 0) return byWord;
            return CompareCanonicalOrder(a, b);
        });

        var count = 0;
        DateTime? first = null;
        DateTime? last = null;
        var index = 0;
        while (index < ordered.Count)
        {
            var word = ordered[index].WordKey;
            CanonicalHistoryRow? previous = null;
            while (index < ordered.Count && string.Equals(ordered[index].WordKey, word, StringComparison.Ordinal))
            {
                var row = ordered[index++];
                if (previous is { } prev
                    && row.Origin == CanonicalOrigin.FirstRetrieval
                    && Fsrs6Model.ElapsedWholeDays(prev.ReviewedAtUtc, row.ReviewedAtUtc) > 0)
                {
                    count++;
                    if (first is null || row.ReviewedAtUtc < first) first = row.ReviewedAtUtc;
                    if (last is null || row.ReviewedAtUtc > last) last = row.ReviewedAtUtc;
                }
                previous = row;
            }
        }
        var span = first is { } from && last is { } to ? (to - from).TotalDays : 0.0;
        return new FsrsTargetStats(count, span);
    }

    private static readonly DateTime MaxUtc = new(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    private static double SpanDaysOf(IReadOnlyList<CanonicalHistoryRow> history)
    {
        if (history.Count == 0) return 0;
        var first = history[0].ReviewedAtUtc;
        var last = history[0].ReviewedAtUtc;
        foreach (var row in history)
        {
            if (row.ReviewedAtUtc < first) first = row.ReviewedAtUtc;
            if (row.ReviewedAtUtc > last) last = row.ReviewedAtUtc;
        }
        return (last - first).TotalDays;
    }

    /// <summary>把 store 的行映射成训练边界的输入（<see cref="CanonicalReview"/>），保留 CanonicalId
    /// ——训练器用它在同刻 tie 上做稳定排序，缺了它同一份历史会产出不同的 item 序列。</summary>
    public static IReadOnlyList<CanonicalReview> BuildOptimizerInput(IReadOnlyList<CanonicalHistoryRow> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var list = new List<CanonicalReview>(history.Count);
        foreach (var row in history)
        {
            list.Add(new CanonicalReview
            {
                CanonicalId = row.CanonicalId,
                Revision = checked((int)row.Revision),
                CanonicalSequence = row.CanonicalSequence,
                WordKey = row.WordKey,
                SessionId = row.SessionId,
                Origin = row.Origin,
                Rating = row.Rating,
                ReviewedAtUtc = row.ReviewedAtUtc,
                CompletedAtUtc = row.CompletedAtUtc,
                Invalidated = false,
            });
        }
        return list;
    }

    // ==================================================================================
    // 重放（纯计算，无 IO）
    // ==================================================================================

    /// <summary>重放协议标识：写进 metrics_json，便于将来区分不同重放口径下的卡片。</summary>
    public const string ReplayProtocol = "fsrs6-replay-v1";

    /// <summary>
    /// 用 <paramref name="weights"/> 从头重放每个词的全部有效 canonical，产出：
    /// ① 每条 canonical 的**可撤销 pre-state**（撤销时要精确回放的那一对 D/S）；
    /// ② 该词重放后的最终卡片。纯函数：不读库、不读时钟、不改入参。
    /// </summary>
    public static IReadOnlyList<FsrsWordReplay> BuildReplayPlan(
        IReadOnlyList<CanonicalHistoryRow> history, Fsrs6Weights weights,
        IReadOnlyList<FsrsManualDueOverride>? manualDueOverrides = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(weights);
        cancellationToken.ThrowIfCancellationRequested();
        var overrides = new Dictionary<(string Word, string Canonical, long Revision), DateTime>();
        foreach (var item in manualDueOverrides ?? [])
            overrides[(item.WordKey, item.AnchorCanonicalId, item.AnchorRevision)] = item.DueAtUtc;

        var ordered = new List<CanonicalHistoryRow>(history);
        cancellationToken.ThrowIfCancellationRequested();
        ordered.Sort(static (a, b) =>
        {
            var byWord = string.CompareOrdinal(a.WordKey, b.WordKey);
            if (byWord != 0) return byWord;
            return CompareCanonicalOrder(a, b);
        });

        var scheduler = new Fsrs6Scheduler(weights);
        var plan = new List<FsrsWordReplay>();
        var index = 0;
        while (index < ordered.Count)
        {
            var wordKey = ordered[index].WordKey;
            FsrsCardState? card = null;
            var preStates = new List<FsrsPreStateRewrite>();
            while (index < ordered.Count && string.Equals(ordered[index].WordKey, wordKey, StringComparison.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = ordered[index++];
                preStates.Add(new FsrsPreStateRewrite(row.CanonicalId, FsrsPreState.From(card)));
                var outcome = scheduler.Review(card?.Clone(), row.Rating, row.ReviewedAtUtc)
                    ?? throw new InvalidOperationException("重放时排期器返回了 null。");
                card = outcome.Card.Clone();
                card.WordKey = wordKey;
                card.LastReviewAtUtc = row.ReviewedAtUtc;
                card.LastCanonicalRating = row.Rating;
                // 重放只重建 **FSRS 基线** 间隔：Context 校准量依赖当时的快照与当时冻结的模型，
                // 不可能事后重算；而换版本来就会让 Context 退到重新资格（见 ApplyFsrsBaseline），
                // 因此在 Context 重新通过资格之前，基线就是唯一正确的答案。
                var interval = MemoryScheduler.ApplyMinimumInterval(
                    outcome.BaselineIntervalDays, row.Rating, row.ReviewedAtUtc, null);
                card.NextReviewAtUtc = DateTime.SpecifyKind(row.ReviewedAtUtc.AddDays(interval), DateTimeKind.Utc);
                // 手动 due 覆盖按**锚点**（词 + canonical_id + revision）重新施加：
                // 该锚点是管理动作当时卡片的水位，因此覆盖只作用于"那一次学习事实之后"的排期，
                // 而撤销回放到该锚点时覆盖自然重新生效。revision 参与匹配，避免同 rowid 被
                // revive/改判后误用旧覆盖值。
                if (overrides.TryGetValue((wordKey, row.CanonicalId, row.Revision), out var manualDue))
                    card.NextReviewAtUtc = manualDue;
            }
            if (card is not null) plan.Add(new FsrsWordReplay(wordKey, card, preStates));
        }
        return plan;
    }

    // ==================================================================================
    // 记账（尝试时间必须落库：否则每次重启都会立刻重训一遍）
    // ==================================================================================

    /// <summary>
    /// **等待类**结果：开关关闭 / 训练器未实现 / 冷却 / 门槛未满足。
    /// <para>刻意不推进任何水位、不推进冷却、不写记账行：这些都不是"一次训练尝试"。
    /// 把它们当成尝试会制造两个真实故障——① 每次轮末推进水位，于是"新增 N 条才重训"永远凑不满
    /// （正常每天新增 20 条的用户再也训不起来）；② 每次轮末推进冷却，等于自己给自己上锁。
    /// 代价是跨重启后需要重新做一次廉价的范围查询（见 <see cref="ILearningMemoryStore.LoadCanonicalBounds"/>），
    /// 这是有界的、可接受的。</para>
    /// </summary>
    private static FsrsParameterTrainingResult Deferred(
        FsrsParameterTrainingOutcome outcome, string detail, DateTime nowUtc)
    {
        _ = nowUtc;
        return new FsrsParameterTrainingResult(outcome, null, detail);
    }

    /// <summary>
    /// 记一次**真实**训练尝试（确实调用过优化器，或确实因输入问题失败）。
    /// <paramref name="advanceWatermark"/> = false 用于瞬时失败：冷却照记，但水位不动，
    /// 这样冷却过后同一批数据还能重试，而不是被迫等到再攒够 N 条新目标。
    /// </summary>
    private FsrsParameterTrainingResult Record(
        FsrsParameterTrainingOutcome outcome, string detail, DateTime nowUtc,
        int targetCount, double spanDays, bool failed, FsrsParameterCandidate? candidate,
        string? rejectedSignature, bool advanceWatermark = true)
    {
        lock (_gate)
        {
            _lastAttemptUtc = nowUtc;
            if (failed) _lastFailedUtc = nowUtc; else _lastFailedUtc = null;
            if (advanceWatermark) _reviewsAtLastAttempt = targetCount;
            // **总是**赋值（而不是只在非 null 时）：令牌的语义是"最近一次尝试所针对的那份快照"。
            // 新一次训练产出候选就说明当前快照已经被处理过（传 null 清掉），
            // 否则一个陈旧的令牌会永久豁免增量门槛，等于把水位锁死。
            _rejectedSnapshotSignature = rejectedSignature;
            _lastAttemptDetail = detail;
        }
        PersistAttempt(outcome, detail, nowUtc, targetCount, spanDays, failed, advanceWatermark, rejectedSignature);
        return new FsrsParameterTrainingResult(outcome, candidate, detail);
    }

    private void PersistAttempt(
        FsrsParameterTrainingOutcome outcome, string detail, DateTime nowUtc,
        int targetCount, double spanDays, bool failed, bool advanceWatermark, string? rejectedSignature)
    {
        string version;
        lock (_gate) version = _activeParameterVersion;
        var metrics = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [SchedulingConfig.FsrsParameterRecordKindKey] = SchedulingConfig.FsrsParameterRecordKindAttempt,
            ["outcome"] = outcome.ToString(),
            ["counted"] = true,
            ["failed"] = failed,
            ["detail"] = detail,
            ["reviewCount"] = targetCount,
            ["spanDays"] = spanDays,
            ["advanceWatermark"] = advanceWatermark,
            ["reviewWatermark"] = _reviewsAtLastAttempt,
            ["rejectedSignature"] = rejectedSignature,
            ["attemptedAtUtc"] = nowUtc.ToString("O"),
            [SchedulingConfig.FsrsParameterVersionMetricKey] = version,
        });
        var state = new PersonalizationModelState
        {
            ModelVersion = SchedulingConfig.FsrsParameterAttemptVersionPrefix + ":" + nowUtc.Ticks.ToString("x", CultureInfo.InvariantCulture),
            TrainedAtUtc = nowUtc,
            TrainCutoffUtc = nowUtc,
            Status = failed ? ContextMode.Shadow : ContextMode.ColdStart,
            CoefficientsJson = "[]",
            ScalerJson = "{}",
            MetricsJson = metrics,
            LastGoodModelVersion = null,
        };
        try { _store.SavePersonalizationModel(state, SchedulingConfig.FsrsParameterModelKind); }
        catch (Exception ex)
        {
            lock (_gate) _lastAttemptDetail += $"（训练记账落库失败：{ex.Message}）";
        }
    }

    /// <summary>把候选的审计字段落成 metrics（store 会在事务内覆盖其中的发布时刻两个键）。</summary>
    private string BuildCandidateMetrics(
        Fsrs6Weights weights, string version, FsrsTargetStats targets,
        int canonicalCount, string signature, Fsrs6Weights baseline)
    {
        var payload = new Dictionary<string, object?>
        {
            [SchedulingConfig.FsrsParameterRecordKindKey] = SchedulingConfig.FsrsParameterRecordKindParams,
            [SchedulingConfig.FsrsParameterVersionMetricKey] = version,
            ["weightsHash"] = WeightsFingerprint(weights),
            ["weightsSource"] = weights.Source,
            ["baselineVersion"] = ParameterVersionOf(baseline),
            ["baselineWeightsHash"] = WeightsFingerprint(baseline),
            ["trainingTargetCount"] = targets.Count,
            ["trainingTargetSpanDays"] = targets.SpanDays,
            ["canonicalCount"] = canonicalCount,
            ["canonicalSignature"] = signature,
            ["replayProtocol"] = ReplayProtocol,
            ["trainedAtUtc"] = _clock().ToString("O"),
        };
        // 无法归因的 due 差异：只报数，不猜。迁移前的旧库没有来源标记，
        // 一旦发生整库重放，这些 due 会被模型日期取代——必须让它在发布前就被说出来。
        try
        {
            var unattributed = _store.CountCardsWithUnattributedDue();
            if (unattributed > 0) payload["cardsWithUnattributedDue"] = unattributed;
        }
        catch (Exception)
        {
            // 审计字段取不到不影响发布本身。
        }
        return JsonSerializer.Serialize(payload);
    }
    // ==================================================================================
    // metrics_json 读取（宽容：任何解析失败都退化成"缺失"，绝不抛）
    // ==================================================================================

    public static string ReadMetricString(string? json, string key)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            if (!doc.RootElement.TryGetProperty(key, out var value)) return "";
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };
        }
        catch (JsonException) { return ""; }
    }

    public static int ReadMetricInt(string? json, string key) =>
        int.TryParse(ReadMetricString(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static bool ReadMetricBool(string? json, string key) =>
        string.Equals(ReadMetricString(json, key), "true", StringComparison.OrdinalIgnoreCase);

    public static DateTime? ReadMetricUtc(string? json, string key) =>
        DateTime.TryParse(ReadMetricString(json, key), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>
/// 参数开关与门槛。默认值全部来自 <see cref="SchedulingConfig"/>——本类型不新增任何魔数，
/// 只提供"测试 / 诊断可以收紧"的注入位。
/// </summary>
public sealed record FsrsPersonalizationOptions
{
    /// <summary>总开关。关闭时一律不训练、不发布，权重停在恢复出来的那一份。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>有效 canonical 条数下限。</summary>
    public int MinimumReviews { get; init; } = SchedulingConfig.OptimizerMinimumReviews;

    /// <summary>历史跨度下限（天）。</summary>
    public int MinimumSpanDays { get; init; } = SchedulingConfig.OptimizerMinimumSpanDays;

    /// <summary>距上次尝试的新增条数下限。</summary>
    public int MinimumNewReviews { get; init; } = SchedulingConfig.OptimizerMinimumNewReviews;

    /// <summary>尝试冷却。</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(SchedulingConfig.OptimizerCooldownMinutes);

    /// <summary>单次训练墙钟上限。</summary>
    public TimeSpan TrainingTimeout { get; init; } = TimeSpan.FromSeconds(SchedulingConfig.OptimizerTrainingTimeoutSeconds);

    /// <summary>恢复时扫描的历史行数上限（参数行 + 记账行）。</summary>
    public int HistoryScanLimit { get; init; } = SchedulingConfig.FsrsParameterHistoryScanLimit;
}

/// <summary>
/// 当前生效权重的**单一可变持有者**：排期器、Context 特征提供者（算 R）与 Context 校准器
/// （解候选间隔）共享同一个实例，因此任何一次换版对三者同时可见——
/// 否则会出现"排期用新权重、快照 R 用旧权重"的静默不一致。
/// <para><see cref="Publish"/> 只允许在 store 事务**提交成功之后**调用；<see cref="Epoch"/> 只增不减。</para>
/// </summary>
public sealed class SchedulerWeights
{
    private readonly object _gate = new();
    private Fsrs6Weights _current;
    private long _epoch;

    public SchedulerWeights(Fsrs6Weights? initial = null) => _current = initial ?? Fsrs6Weights.Defaults;

    public long Epoch { get { lock (_gate) return _epoch; } }

    public Fsrs6Weights Current { get { lock (_gate) return _current; } }

    /// <summary>发布一组新权重，返回新的 epoch。调用方必须已经完成持久化。</summary>
    public long Publish(Fsrs6Weights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        lock (_gate)
        {
            _current = weights;
            return ++_epoch;
        }
    }

    /// <summary>
    /// 启动恢复专用：只有当持有者仍是**从未发布过**的初始状态时才写入（epoch 保持 0）。
    /// 用它而不是 <see cref="Publish"/>，是为了让"启动恢复"不被误记成一次换版
    /// ——否则任何在训练开始前拍下的 epoch 都会与恢复后的 epoch 不符，第一轮训练永远发布不出去。
    /// </summary>
    public bool PublishDefaultIfUnset(Fsrs6Weights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        lock (_gate)
        {
            if (_epoch != 0) return false;
            if (ReferenceEquals(_current, weights)) return false;
            if (!ReferenceEquals(_current, Fsrs6Weights.Defaults)
                && string.Equals(_current.Source, Fsrs6Weights.SourceDefaults, StringComparison.Ordinal)
                && _current.ValueEquals(Fsrs6Weights.Defaults)) return false;
            _current = weights;
            return true;
        }
    }
}

/// <summary>启动恢复的结果（可审计；不参与任何数值判断）。</summary>
public sealed record FsrsParameterRestoreResult(
    Fsrs6Weights Weights, string ParameterVersion, DateTime? ActiveSinceUtc,
    string? HelperVersion, string? HelperSha, string Detail,
    bool PublishedParametersFound = false, bool IsDefaultBaseline = false);

/// <summary>重启一致性核对的结果。</summary>
public sealed record FsrsParameterReconcileResult(bool Consistent, bool Replayed, string Detail);

/// <summary>一次训练/跳过/拒绝的结果。</summary>
public sealed record FsrsParameterTrainingResult(
    FsrsParameterTrainingOutcome Outcome, FsrsParameterCandidate? Candidate, string Detail);

/// <summary>训练结果的分类。全部都是**可审计的终态**，没有"悄悄什么都不做"的分支。</summary>
public enum FsrsParameterTrainingOutcome
{
    /// <summary>总开关关闭。</summary>
    Disabled,
    /// <summary>训练器未实现（<c>IsImplemented == false</c>）——契约允许的中间状态。</summary>
    NotImplemented,
    /// <summary>冷却 / 已有在途任务：本轮跳过，不算失败。</summary>
    Cooldown,
    /// <summary>数据量 / 跨度 / 新增量门槛未满足：只收集。</summary>
    WaitingForData,
    /// <summary>训练器明确拒绝（未获可靠提升、样本不足等）。</summary>
    NoImprovement,
    /// <summary>取消 / 超时 / 异常：保留 last-good。</summary>
    SafeFallback,
    /// <summary>得到可发布的候选。</summary>
    Trained,
}

/// <summary>通过训练器验证、等待发布（可能因 UI 忙 / 可撤销呈现而延后）的候选参数。</summary>
public sealed record FsrsParameterCandidate(
    Fsrs6Weights Weights,
    string ParameterVersion,
    long StartEpoch,
    string BaselineVersion,
    string CanonicalSignature,
    DateTime TrainedAtUtc,
    int ReviewCount,
    double SpanDays,
    string MetricsJson);

/// <summary>真实训练目标的条数与跨度（与训练适配器的 target 规则逐字对齐）。</summary>
public sealed record FsrsTargetStats(int Count, double SpanDays);

/// <summary>
/// 有效个人参数的**唯一**解析结果：Restore 与 Context 都从这里取，不允许各自另选基线。
/// <paramref name="Row"/> 为 null 表示库里没有可用的发布行（回退到官方默认参数）。
/// </summary>
public sealed record FsrsEffectiveParameters(
    Fsrs6Weights Weights, string ParameterVersion, DateTime? ActiveSinceUtc,
    PersonalizationModelState? Row, string? HelperVersion, string? HelperSha, string Detail);

/// <summary>
/// 启动装配的结果。只有 <see cref="Consistent"/> 为 true 才允许暴露可写的长期记忆协调器。
/// </summary>
/// <param name="Service">个人参数服务；null = 初始化失败（训练停用）。</param>
/// <param name="Consistent">当前 holder 的权重与库里现存的卡片/前置状态是否同源。</param>
/// <param name="Replayed">是否为了达成一致而做了一次整库重放。</param>
/// <param name="Error">初始化失败的原因（无失败时为 null）。</param>
public sealed record FsrsPersonalizationStartup(
    FsrsPersonalization? Service,
    bool Consistent,
    bool Replayed,
    FsrsParameterRestoreResult? Restore,
    FsrsParameterReconcileResult? Reconcile,
    string? Error)
{
    /// <summary>
    /// 建立服务 → 恢复有效参数 → 重启一致性核对，并把"能不能继续用这套权重写库"折算成一个布尔。
    /// <para>它刻意不依赖窗口：这是"恢复必须先证明一致，再暴露协调器"这条安全边界的**唯一**判定点，
    /// 必须能被直接测试，而不是只能靠读 MainWindow 的代码来相信。</para>
    /// </summary>
    public static FsrsPersonalizationStartup Create(
        ILearningMemoryStore store, SchedulerWeights holder,
        Func<Fsrs6Weights, IFSRSParameterOptimizer> optimizerFactory,
        FsrsPersonalizationOptions? options = null, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(optimizerFactory);
        void Report(string message)
        {
            try { diagnostic?.Invoke(message); }
            catch (Exception) { /* 诊断绝不能影响流程 */ }
        }

        try
        {
            var service = new FsrsPersonalization(store, holder, optimizerFactory(holder.Current), options);
            var restored = service.Restore();
            Report("启动恢复｜" + restored.Detail);
            var reconcile = service.Reconcile();
            Report("启动核对｜" + reconcile.Detail);
            return new FsrsPersonalizationStartup(service, reconcile.Consistent, reconcile.Replayed, restored, reconcile, null);
        }
        catch (Exception ex)
        {
            // 初始化异常本身不等于"不一致"：只有能证明"现有卡片恰好就是默认权重算出来的"时
            // 才允许用默认权重继续。否则拿 defaults 去推个人权重留下的 D/S，
            // 正是本轮要消灭的"新权重 + 旧 D/S"混合体。
            bool consistent;
            try { consistent = !store.HasCardsOutsideParameterVersion(SchedulingConfig.ParameterVersion); }
            catch (Exception) { consistent = false; }
            Report($"初始化失败（{ex.GetType().Name}：{ex.Message}）；"
                + (consistent ? "卡片与官方默认参数一致，继续以默认参数工作。" : "卡片与当前权重不同源，已暂停长期记忆写入。"));
            return new FsrsPersonalizationStartup(null, consistent, false, null, null, ex.Message);
        }
    }
}

/// <summary>
/// 个人参数的**运行期生命周期**（训练在途 / 发布在途 / 待发布候选 / 取消与 rebind 代际）。
///
/// <para>它存在的理由是"可测"：这段逻辑原本长在 <c>MainWindow</c> 里，而窗口需要 Avalonia
/// 才能构造，于是"发布是否会在 UI 上下文上死锁""旧续体是否会把候选串进新库"这类问题
/// 只能靠反射查方法名——那是空转。把它抽成一个不依赖窗口的类型之后，
/// 就能在真实的 <see cref="SynchronizationContext"/> 下驱动完整生命周期并断言。</para>
///
/// <para><b>为什么不能 <c>GetAwaiter().GetResult()</c></b>：<see cref="FsrsPersonalization.PublishAsync"/>
/// 内部 <c>await Task.Run(...)</c> 之后要回到调用上下文提交事务；在单线程 UI 上下文上同步等待，
/// 续体永远排不到队 → 真死锁。这里全程 <c>await</c>，所以续体照常回到 UI 线程，
/// store 的单连接仍然只在 UI 线程上用（也因此绝不能加 <c>ConfigureAwait(false)</c>）。</para>
///
/// <para><b>代际保护</b>：<see cref="Invalidate"/> 由 rebind / 真退出调用，它推进
/// <see cref="Generation"/>、取消两个 CTS、清掉待发布候选。之后任何旧任务的续体都必须先验证
/// "代际未变 且 service 仍是同一个实例"才允许改状态——否则一个训练完成后台拟合、UI 续体还没跑的
/// 旧任务会把候选交给刚替换过的服务。旧任务的收尾只允许清**自己**的 CTS。</para>
/// </summary>
public sealed class FsrsPersonalizationRuntime
{
    private readonly FsrsPersonalization? _service;
    private readonly Func<bool> _deferPublish;
    private readonly Func<DateTime> _clock;
    private readonly Action<string> _diagnostic;
    private readonly Action? _beforePublish;
    private readonly object _gate = new();

    private long _generation;
    private bool _training;
    private bool _publishing;
    private CancellationTokenSource? _trainingCts;
    private CancellationTokenSource? _publishCts;
    private FsrsParameterCandidate? _pending;
    private string _lastDetail = "";

    public FsrsPersonalizationRuntime(
        FsrsPersonalization? service,
        Func<bool> deferPublish,
        Action<string> diagnostic,
        Func<DateTime>? clock = null,
        Action? beforePublish = null)
    {
        _service = service;
        _deferPublish = deferPublish ?? throw new ArgumentNullException(nameof(deferPublish));
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _clock = clock ?? (() => DateTime.UtcNow);
        _beforePublish = beforePublish;
    }

    /// <summary>当前代际。rebind / 真退出后自增，旧任务的续体据此作废。</summary>
    public long Generation { get { lock (_gate) return _generation; } }

    public bool Training { get { lock (_gate) return _training; } }

    public bool Publishing { get { lock (_gate) return _publishing; } }

    public bool HasPendingCandidate { get { lock (_gate) return _pending is not null; } }

    public bool HasWork { get { lock (_gate) return _training || _publishing || _pending is not null; } }

    public string LastDetail { get { lock (_gate) return _lastDetail; } }

    public FsrsPersonalization? Service => _service;

    /// <summary>rebind / 真退出：推进代际、取消在途、丢掉已无意义的候选。旧任务续体会看到代际变化而自行放弃。</summary>
    public void Invalidate()
    {
        CancellationTokenSource? training;
        CancellationTokenSource? publishing;
        lock (_gate)
        {
            _generation++;
            _pending = null;
            training = _trainingCts;
            publishing = _publishCts;
            _trainingCts = null;
            _publishCts = null;
            _training = false;
            _publishing = false;
            _lastDetail = "已作废：词库重绑或退出。";
        }
        Cancel(training);
        Cancel(publishing);
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已收尾 */ }
    }

    /// <summary>
    /// 轮级触发的**唯一**入口（一轮开始 / 一轮结束 / 离开学习页）。
    /// 等待类结果不会推进任何水位或冷却，因此可以放心在每个轮末调用。
    /// </summary>
    public void NotifyRoundBoundary(string trigger)
    {
        var service = _service;
        if (service is null) return;

        long generation;
        bool busy;
        bool hasPending;
        lock (_gate)
        {
            generation = _generation;
            busy = _training || _publishing;
            hasPending = _pending is not null;
        }
        if (busy)
        {
            Report($"{trigger}｜跳过：已有训练或发布在途。");
            return;
        }
        if (hasPending)
        {
            // 待发布候选优先：它已经付过训练代价，且发布本身不需要新的数据门槛。
            StartPublish(trigger, service, generation);
            return;
        }
        if (!service.OptimizerImplemented)
        {
            Report($"{trigger}｜跳过：训练器未实现。");
            return;
        }

        var now = DateTime.SpecifyKind(_clock(), DateTimeKind.Utc);
        if (service.CooldownBlocks(now, hasPendingTask: false))
        {
            Report($"{trigger}｜跳过：{service.CooldownDetail(now, false)}。");
            return;
        }
        StartTraining(trigger, service, generation, now);
    }

    private void StartTraining(string trigger, FsrsPersonalization service, long generation, DateTime nowUtc)
    {
        var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(SchedulingConfig.OptimizerTrainingTimeoutSeconds));
        lock (_gate)
        {
            if (_generation != generation || _training || _publishing) { cts.Dispose(); return; }
            _training = true;
            _trainingCts = cts;
        }
        Report($"{trigger}｜开始训练。");
        _ = RunTrainingAsync(trigger, service, generation, nowUtc, cts);
    }

    private async Task RunTrainingAsync(
        string trigger, FsrsPersonalization service, long generation, DateTime nowUtc, CancellationTokenSource cts)
    {
        FsrsParameterTrainingResult? result = null;
        string? error = null;
        try { result = await service.TrainAsync(nowUtc, hasPendingTask: false, cts.Token); }
        catch (Exception ex) { error = ex.GetType().Name + "：" + ex.Message; }

        // —— 续体身份校验：rebind/退出之后一律不得改任何状态 ——
        if (!StillCurrent(service, generation))
        {
            cts.Dispose();
            return;
        }
        bool publishNow = false;
        lock (_gate)
        {
            // 先复位**自己**的标志再做别的事：一次异常不能让训练永久卡住。
            _training = false;
            if (ReferenceEquals(_trainingCts, cts)) _trainingCts = null;
            if (result is null)
                _lastDetail = $"{trigger}｜训练失败（{error}）。";
            else if (result.Outcome == FsrsParameterTrainingOutcome.Trained && result.Candidate is { } candidate)
            {
                _pending = candidate;
                _lastDetail = $"{trigger}｜{result.Detail}";
                publishNow = true;
            }
            else
                _lastDetail = $"{trigger}｜{result.Outcome}——{result.Detail}";
        }
        cts.Dispose();
        Report(LastDetail);
        if (publishNow) StartPublish(trigger + "｜训练结束", service, generation);
    }

    private void StartPublish(string trigger, FsrsPersonalization service, long generation)
    {
        if (_deferPublish())
        {
            Report($"{trigger}｜延后发布：UI 忙或有可撤销的呈现（候选保留，不丢）。");
            return;
        }
        var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(SchedulingConfig.OptimizerTrainingTimeoutSeconds));
        FsrsParameterCandidate? candidate;
        lock (_gate)
        {
            if (_generation != generation || _publishing || _training) { cts.Dispose(); return; }
            candidate = _pending;
            if (candidate is null) { cts.Dispose(); return; }
            _publishing = true;
            _publishCts = cts;
        }
        // 发布前取消在途的 Context 训练：它的基线马上要换，让它跑完只会产出一个会被世代闸丢弃的结果。
        try { _beforePublish?.Invoke(); }
        catch (Exception ex) { Report($"{trigger}｜发布前回调异常（{ex.GetType().Name}）：{ex.Message}"); }
        _ = RunPublishAsync(trigger, service, generation, candidate!, cts);
    }

    private async Task RunPublishAsync(
        string trigger, FsrsPersonalization service, long generation,
        FsrsParameterCandidate candidate, CancellationTokenSource cts)
    {
        FsrsParameterPublishResult? result = null;
        string? error = null;
        try
        {
            // 全程 await（绝不 GetResult）：续体回到调用上下文，事务仍在原线程上提交。
            result = await service.PublishAsync(candidate, cts.Token);
        }
        catch (Exception ex) { error = ex.GetType().Name + "：" + ex.Message; }

        if (!StillCurrent(service, generation))
        {
            cts.Dispose();
            return;
        }
        // 被拒的候选没有把这份数据"用掉"：只记冷却与被拒快照令牌，**不推进新增水位**，
        // 否则一次作废的候选会让用户再攒够 N 条之前都训不起来。
        // 顺序刻意放在清标志**之前**：这样任何观察到"发布已结束（无在途、无待发布）"的一方，
        // 也一定能看到这次拒绝留下的全部后果——否则收尾会被观察者切成两半。
        if (result is { Applied: false } rejected)
            service.NotePublishRejected(rejected, DateTime.SpecifyKind(_clock(), DateTimeKind.Utc),
                candidate.CanonicalSignature);

        lock (_gate)
        {
            _publishing = false;
            if (ReferenceEquals(_publishCts, cts)) _publishCts = null;
            _pending = null;
            _lastDetail = result is null
                ? $"{trigger}｜发布失败（{error}）；保留 last-good。"
                : $"{trigger}｜{result.Detail}";
        }
        cts.Dispose();
        Report(LastDetail);
    }

    private bool StillCurrent(FsrsPersonalization service, long generation)
    {
        lock (_gate) return _generation == generation && ReferenceEquals(_service, service);
    }

    private void Report(string message)
    {
        try { _diagnostic(message); }
        catch (Exception) { /* 诊断本身绝不能影响流程 */ }
    }

    /// <summary>测试/诊断：等到没有在途工作，返回是否在超时前完成。</summary>
    public bool WaitForIdle(TimeSpan timeout) => WaitForIdle(timeout, requireNoPending: false);

    /// <summary>
    /// <paramref name="requireNoPending"/> = true 时连"待发布候选"也要求清空。
    /// <para>为什么需要两种语义：训练收尾会先把候选放进 <c>_pending</c> 再启动发布，两步之间有一个
    /// 极短的窗口——只等 <c>!_training &amp;&amp; !_publishing</c> 的调用方可能正好落在窗口里，
    /// 于是"发布已完成"的断言会随机失败。期待发布跑完的用例用严格的那种；
    /// 断言"候选被保留"的用例用宽松的那种。</para>
    /// </summary>
    public bool WaitForIdle(TimeSpan timeout, bool requireNoPending)
    {
        var deadline = DateTime.UtcNow + timeout;
        bool Idle()
        {
            lock (_gate) return !_training && !_publishing && (!requireNoPending || _pending is null);
        }
        while (DateTime.UtcNow < deadline)
        {
            if (Idle()) return true;
            System.Threading.Thread.Sleep(5);
        }
        return Idle();
    }

    /// <summary>测试用：把候选直接放进来（跳过训练），用于只驱动发布路径。</summary>
    public void SeedPendingCandidate(FsrsParameterCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate) _pending = candidate;
    }
}

/// <summary>发布结果。<paramref name="PublishedAtUtc"/> 是 store 在事务内取的**权威发布时刻**（未发布时 null）。</summary>
public sealed record FsrsParameterPublishResult(
    bool Applied, bool Rejected, string Detail, int WordsReplayed = 0, int CanonicalsRewritten = 0,
    DateTime? PublishedAtUtc = null, FsrsParameterPublishRejection Rejection = FsrsParameterPublishRejection.None);

/// <summary>一次发布被拒绝的原因（决定调用方是"等新数据"还是"应当重训"）。</summary>
public enum FsrsParameterPublishRejection
{
    /// <summary>没有拒绝。</summary>
    None,
    /// <summary>训练快照与当前有效 canonical 集合/手动覆盖不一致（撤销、改判、新增、管理动作）。</summary>
    StaleSnapshot,
    /// <summary>训练期间已有另一次发布改变了参数 epoch。</summary>
    EpochChanged,
    /// <summary>该参数版本已经是当前生效版本。</summary>
    AlreadyActive,
    /// <summary>重放计算或事务失败（内部异常，已回滚）。</summary>
    Failed,
}

/// <summary>一条 canonical 在重放中的**前置状态**（要同事务写回 <c>canonical_reviews</c> 的 pre_state_* 列）。</summary>
public sealed record FsrsPreStateRewrite(string CanonicalId, FsrsPreState PreState);

/// <summary>一个词重放后的最终卡片 + 它每条 canonical 的 pre-state。</summary>
public sealed record FsrsWordReplay(
    string WordKey, FsrsCardState Card, IReadOnlyList<FsrsPreStateRewrite> PreStates);

/// <summary>
/// 一次参数发布要提交的全部内容。**同一个 SQLite 事务**里完成：
/// 参数模型状态行 + 每个词的卡片 + 每条 canonical 的 pre-state + 一致性签名核对。
/// </summary>
public sealed record FsrsParameterPublication(
    string ModelVersion,
    DateTime TrainedAtUtc,
    DateTime TrainCutoffUtc,
    string CoefficientsJson,
    string MetricsJson,
    string ParameterVersion,
    string CanonicalSignature,
    IReadOnlyList<FsrsWordReplay> Words,
    string RecordKind);
