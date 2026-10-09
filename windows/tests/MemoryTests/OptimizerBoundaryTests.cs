using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lexi.Tests;

internal static class OptimizerBoundaryTests
{
    internal static void Run()
    {
        ContextUsesSameRecoveryWindow();
        PublishedParametersSurviveAttemptRowFlood();
    }

    private static void ContextUsesSameRecoveryWindow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lexi-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new VocabularyService(Path.Combine(dir, "fixture.sqlite3"));
        var at = new DateTime(2026,1,1,12,0,0,DateTimeKind.Utc);
        var values = Fsrs6Weights.Defaults.ToArray(); values[2]=3.4;
        var weights = Fsrs6Weights.Create(values, Fsrs6Weights.SourceOptimized, at);
        var version = FsrsPersonalization.ParameterVersionOf(weights);
        store.SavePersonalizationModel(new PersonalizationModelState
        {
            ModelVersion=version, TrainedAtUtc=at, TrainCutoffUtc=at, CoefficientsJson=weights.ToJson(),
            MetricsJson=JsonSerializer.Serialize(new Dictionary<string,object>{[SchedulingConfig.FsrsParameterRecordKindKey]="params",
                [SchedulingConfig.FsrsParameterVersionMetricKey]=version,[SchedulingConfig.FsrsParameterActiveSinceMetricKey]=at.ToString("O")})
        },SchedulingConfig.FsrsParameterModelKind);
        for(var i=1;i<=9;i++)
            store.SavePersonalizationModel(new PersonalizationModelState
            {
                ModelVersion="attempt:"+i, TrainedAtUtc=at.AddHours(i), TrainCutoffUtc=at,
                CoefficientsJson=Fsrs6Weights.Defaults.ToJson(), MetricsJson="{\"recordKind\":\"attempt\",\"counted\":true}"
            },SchedulingConfig.FsrsParameterModelKind);
        var effective = FsrsPersonalization.ResolveEffectiveWeights(store.LoadPersonalizationModels(
            SchedulingConfig.FsrsParameterModelKind,SchedulingConfig.FsrsParameterHistoryScanLimit));
        Program.Check(effective.ParameterVersion==version,"personal restore finds valid published parameters behind 9 attempts");
        var context = new ContextCalibrator(store,new Fsrs6Scheduler(weights));
        Program.Check(context.FsrsBaselineVersionProbe==version && context.FsrsBaselineSinceProbe==at,
            "Context and personal restore share version/since despite >8 recent failed attempts");
    }

    /// <summary>
    /// 记账行洪泛不得挤掉唯一的"已发布参数"行。
    /// 发布行与训练记账行共用同一个 <c>model_kind</c>；不推进水位的瞬时失败路径每小时写一行记账，
    /// 一旦攒够扫描窗口的条数，最新那行发布参数就会落在窗口之外——holder、Context 与撤销回放的版本戳
    /// 会**一起**静默回退 defaults，随后 Reconcile 会按 defaults 整库重放。
    /// 反例：旧读取实现只取"最新 limit 行"，本用例在旧源码上必红。
    /// </summary>
    private static void PublishedParametersSurviveAttemptRowFlood()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lexi-flood-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new VocabularyService(Path.Combine(dir, "fixture.sqlite3"));
        var at = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var values = Fsrs6Weights.Defaults.ToArray(); values[2] = 3.4;
        var weights = Fsrs6Weights.Create(values, Fsrs6Weights.SourceOptimized, at);
        var version = FsrsPersonalization.ParameterVersionOf(weights);

        // ① 一行**已发布**个人参数（最旧的一行）
        store.SavePersonalizationModel(new PersonalizationModelState
        {
            ModelVersion = version, TrainedAtUtc = at, TrainCutoffUtc = at,
            CoefficientsJson = weights.ToJson(),
            MetricsJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [SchedulingConfig.FsrsParameterRecordKindKey] = SchedulingConfig.FsrsParameterRecordKindParams,
                [SchedulingConfig.FsrsParameterVersionMetricKey] = version,
                [SchedulingConfig.FsrsParameterActiveSinceMetricKey] = at.ToString("O"),
                ["trainingTargetCount"] = 11,
            })
        }, SchedulingConfig.FsrsParameterModelKind);

        // ② 记账行洪泛：远超扫描窗口（每个真实尝试一行，崩溃/取消路径每小时一行且不推进水位）
        const int flood = 250;
        for (var i = 1; i <= flood; i++)
            store.SavePersonalizationModel(new PersonalizationModelState
            {
                ModelVersion = "attempt:" + i, TrainedAtUtc = at.AddHours(i), TrainCutoffUtc = at,
                CoefficientsJson = Fsrs6Weights.Defaults.ToJson(),
                MetricsJson = "{\"recordKind\":\"attempt\",\"counted\":true,\"advanceWatermark\":false,\"reviewWatermark\":0}"
            }, SchedulingConfig.FsrsParameterModelKind);

        // 前提：洪泛确实超过了窗口，否则下面的断言会空转
        Program.Check(flood > SchedulingConfig.FsrsParameterHistoryScanLimit,
            $"前提：记账行 {flood} 行确实超过扫描窗口 {SchedulingConfig.FsrsParameterHistoryScanLimit} 行");

        var rows = store.LoadPersonalizationModels(
            SchedulingConfig.FsrsParameterModelKind, SchedulingConfig.FsrsParameterHistoryScanLimit);
        // 锁定"窗口 + 恰好一行"这一边界：若子查询里的 LIMIT 被误删/被忽略，窗口会膨胀到全表，
        // 这条断言会立刻变红（否则该变异只有靠人工读 SQL 才能发现）。
        Program.Check(rows.Count == SchedulingConfig.FsrsParameterHistoryScanLimit + 1,
            $"只比窗口多**一行**（实际 {rows.Count}，期望 {SchedulingConfig.FsrsParameterHistoryScanLimit + 1}）");
        var effective = FsrsPersonalization.ResolveEffectiveWeights(rows);
        Program.Check(effective.ParameterVersion == version,
            $"记账行洪泛后仍解析出**个人**参数版本（实际 {effective.ParameterVersion}，默认 {SchedulingConfig.FsrsParameterVersionPrefix}）");
        Program.Check(!ReferenceEquals(effective.Row, null) && effective.Row!.ModelVersion == version,
            "解析结果绑定到真正那行已发布参数，而不是回退 defaults");
        Program.Check(effective.ActiveSinceUtc == at,
            $"生效时刻仍来自已发布行（实际 {effective.ActiveSinceUtc:O}）");

        // Context 与 holder 必须同源（同一个窗口、同一个解析器）
        var context = new ContextCalibrator(store, new Fsrs6Scheduler(weights));
        Program.Check(context.FsrsBaselineVersionProbe == version && context.FsrsBaselineSinceProbe == at,
            "Context 基线不被记账行洪泛挤掉，与 holder 保持同源");
    }

    internal static void SnapshotSignatureDoesNotCollide()
    {
        var dir=Path.Combine(Path.GetTempPath(),"lexi-signature-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var db=Path.Combine(dir,"fixture.sqlite3");
        using var store=new VocabularyService(db);
        using(var connection=new SqliteConnection("Data Source="+db))
        {
            connection.Open();using var cmd=connection.CreateCommand();
            cmd.CommandText="""
                INSERT INTO fsrs_manual_due_overrides(override_id,word_key,anchor_canonical_id,anchor_revision,due_at_utc,reason,created_at_utc)
                VALUES (1,'archive:a','cr:a:1',1,'2026-01-01T00:00:00.0000000Z','fixture','2026-01-01T00:00:00.0000000Z'),
                       (4,'archive:b','cr:b:1',1,'2026-01-01T00:00:00.0000000Z','fixture','2026-01-01T00:00:00.0000000Z');
                """;cmd.ExecuteNonQuery();
        }
        var before=store.ComputeCanonicalSignature();
        using(var connection=new SqliteConnection("Data Source="+db))
        {
            connection.Open();using var cmd=connection.CreateCommand();
            // Synthetic damaged/replaced snapshot, same count and id sum. The gate must hash actual input.
            cmd.CommandText="UPDATE fsrs_manual_due_overrides SET override_id=2 WHERE override_id=1; UPDATE fsrs_manual_due_overrides SET override_id=3 WHERE override_id=4;";cmd.ExecuteNonQuery();
        }
        Program.Check(store.ComputeCanonicalSignature()!=before,"same COUNT/SUM with different snapshot rows must be rejected");
    }
}
