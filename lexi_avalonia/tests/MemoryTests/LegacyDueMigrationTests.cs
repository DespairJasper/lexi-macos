using System.Globalization;
using Lexi.Core;
using Microsoft.Data.Sqlite;

namespace Lexi.Tests;

internal static class LegacyDueMigrationTests
{
    private static readonly DateTime At = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    internal static void Run()
    {
        CheckMigration(publishedBeforeUpgrade: false);
        CheckMigration(publishedBeforeUpgrade: true);
    }

    private static void CheckMigration(bool publishedBeforeUpgrade)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lexi-legacy-due-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "fixture.sqlite3");
        var manual = At.AddDays(90);
        using (var store = new VocabularyService(db))
        {
            Commit(store, "archive:manual", "s0", At, Fsrs6Weights.Defaults);
            Commit(store, "archive:manual", "s1", At.AddDays(4), Fsrs6Weights.Defaults);
            Commit(store, "archive:normal", "s0", At, Fsrs6Weights.Defaults);
            Commit(store, "archive:normal", "s1", At.AddDays(4), Fsrs6Weights.Defaults);
            if (publishedBeforeUpgrade)
                store.SavePersonalizationModel(new PersonalizationModelState
                {
                    ModelVersion = "existing-personal", TrainedAtUtc = At, TrainCutoffUtc = At,
                    CoefficientsJson = Fsrs6Weights.Defaults.ToJson(), MetricsJson = "{\"recordKind\":\"params\"}"
                }, SchedulingConfig.FsrsParameterModelKind);
        }
        // Recreate the pre-upgrade condition: no migration marker and no explicit override table rows.
        using (var connection = new SqliteConnection("Data Source=" + db))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS fsrs_optimizer_migrations; UPDATE fsrs_cards SET next_review_at_utc=$due WHERE word_key='archive:manual';";
            cmd.Parameters.AddWithValue("$due", manual.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        using (var store = new VocabularyService(db))
        {
            var overrides = store.LoadManualDueOverrides();
            Program.Check(overrides.Count == (publishedBeforeUpgrade ? 0 : 1),
                publishedBeforeUpgrade ? "已有个人参数发布的库不推测旧due来源" : "升级从最新决策明确不同的旧due捕获一条管理覆盖");
            if (publishedBeforeUpgrade) return;
            Program.Check(overrides[0].WordKey == "archive:manual" && overrides[0].DueAtUtc == manual,
                "正常多决策历史不误捕获；旧管理due准确保留");
            Program.Check(store.CountCardsWithUnattributedDue() == 0, "只对最新对应决策比较，正常旧决策不误报来源缺失");
            var values = Fsrs6Weights.Defaults.ToArray(); values[2] = 3.4;
            var candidate = Fsrs6Weights.Create(values, Fsrs6Weights.SourceOptimized, At.AddDays(20));
            var history = store.LoadCanonicalHistory(null, DateTime.MaxValue, null);
            var publication = new FsrsParameterPublication("legacy-fixture", At, At, candidate.ToJson(),
                "{\"recordKind\":\"params\"}", FsrsPersonalization.ParameterVersionOf(candidate),
                store.ComputeCanonicalSignature(), FsrsPersonalization.BuildReplayPlan(history, candidate, overrides),
                SchedulingConfig.FsrsParameterRecordKindParams);
            Program.Check(store.PublishFsrsParameters(publication).Applied, "迁移后实际事务发布成功");
            Program.Check(store.GetCard("archive:manual")!.NextReviewAtUtc == manual, "换版重放保留旧管理due");
            var id = Commit(store, "archive:manual", "s2", At.AddDays(8), candidate);
            Program.Check(store.InvalidateCanonical(id, At.AddDays(9), "fixture-undo"), "真实撤销后续canonical成功");
            Program.Check(store.GetCard("archive:manual")!.NextReviewAtUtc == manual, "换版后撤销恢复迁移覆盖due");
        }
        using (var restarted = new VocabularyService(db))
            Program.Check(restarted.LoadManualDueOverrides().Count == 1, "重启迁移幂等，不重复写覆盖");
        // Fixture evidence is retained outside the product and official data directory.
    }

    private static string Commit(VocabularyService store, string word, string session, DateTime at, Fsrs6Weights weights)
    {
        var previous = store.GetCard(word);
        var outcome = new Fsrs6Scheduler(weights).Review(previous?.Clone(), StudyRating.Known, at);
        var card = outcome.Card.Clone(); card.WordKey = word;
        card.NextReviewAtUtc = at.AddDays(Math.Max(1, outcome.BaselineIntervalDays));
        var id = CanonicalReview.BuildId(word, session);
        store.CommitWordSession(new WordSessionCommit(null!, new WordSessionSummary
        {
            WordKey=word, SessionId=session, FirstPresentedAtUtc=at, CompletedAtUtc=at.AddMinutes(1),
            TotalPresentations=1, FinalKnownCount=1
        }, new CanonicalReview
        {
            CanonicalId=id, WordKey=word, SessionId=session,
            Origin=previous is null ? CanonicalOrigin.FirstLearnAggregate : CanonicalOrigin.FirstRetrieval,
            Rating=StudyRating.Known, ReviewedAtUtc=at, CompletedAtUtc=at.AddMinutes(1)
        }, FsrsPreState.From(previous), card, new SchedulerDecision
        {
            DecisionId="decision:"+id, WordKey=word, CanonicalId=id, TimestampUtc=at,
            BaselineIntervalDays=outcome.BaselineIntervalDays, FinalIntervalDays=outcome.BaselineIntervalDays,
            FinalDueAtUtc=card.NextReviewAtUtc!.Value,
            FsrsParameterVersion=FsrsPersonalization.ParameterVersionOf(weights)
        }, [], [], [], []));
        return id;
    }
}
