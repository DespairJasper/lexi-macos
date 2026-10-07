using System.Reflection;
using Avalonia.Controls;
using Microsoft.Data.Sqlite;

namespace Lexi;

public static class MemoryFailureTests
{
    public static async Task RunAsync(MainWindow window, Action<bool, string> check)
    {
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var store = Field<IVocabularyArchive>("_vocabService");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
        db.Open();
        void Sql(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        Sql("UPDATE words SET next_review_date='2999-01-01'; UPDATE fsrs_cards SET next_review_at_utc='2999-01-01T00:00:00.0000000Z'");
        store.AddWord("memory-write-failure", "", "保存失败用例", "");
        var word = store.GetAllWords().Single(w => w.Word == "memory-write-failure");
        store.ExecuteBatch([word.Id], "today");
        Call("RefreshWords");
        Call("OpenReviewDeck");
        var before = store.GetAllWords().Single(w => w.Id == word.Id);
        Sql("CREATE TEMP TRIGGER fail_canonical BEFORE INSERT ON main.canonical_reviews BEGIN SELECT RAISE(ABORT,'injected canonical failure'); END");
        // TEMP triggers are connection-local: use a persistent trigger on this isolated fixture.
        Sql("DROP TRIGGER fail_canonical; CREATE TRIGGER fail_canonical BEFORE INSERT ON canonical_reviews BEGIN SELECT RAISE(ABORT,'injected canonical failure'); END");
        try
        {
            await (Task)Call("OnReviewRatingAsync", StudyRating.Known)!;
            var round = Field<StudyRound<WordItem>>("_reviewRound");
            check(round.Completed == 0 && round.HasCurrent && round.Current.Id == word.Id,
                "canonical write failure restores pending round instead of showing completion");
            check(window.FindControl<TextBlock>("ReviewHintText")!.Text!.Contains("未完成"),
                "canonical write failure is visible and offers retry");
            var after = store.GetAllWords().Single(w => w.Id == word.Id);
            check(after.Stage == before.Stage && after.NextReviewDate == before.NextReviewDate,
                "canonical write failure restores legacy progress projection");
        }
        finally
        {
            Sql("DROP TRIGGER IF EXISTS fail_canonical");
        }
        await (Task)Call("OnReviewRatingAsync", StudyRating.Known)!;
        check(Field<StudyRound<WordItem>>("_reviewRound").Completed == 1,
            "canonical save can retry successfully after storage recovers");
        int EffectiveCanonicals()
        {
            using var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0";
            count.Parameters.AddWithValue("$key", WordKey.Archive(word.Archive.Uuid).Key);
            return Convert.ToInt32(count.ExecuteScalar());
        }
        int RevisedEvents()
        {
            using var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM learning_events WHERE word_key=$key AND kind='Revised'";
            count.Parameters.AddWithValue("$key", WordKey.Archive(word.Archive.Uuid).Key);
            return Convert.ToInt32(count.ExecuteScalar());
        }
        check(EffectiveCanonicals() == 1, "retry applies exactly one effective canonical");

        // —— 撤销失败门（规格书 §5）：长期层写失败时不得显示"撤销成功"，轮内状态与旧列保持原样 ——
        var stageBeforeUndo = store.GetAllWords().Single(w => w.Id == word.Id).Stage;
        Sql("CREATE TRIGGER fail_invalidate BEFORE UPDATE ON canonical_reviews BEGIN SELECT RAISE(ABORT,'injected invalidation failure'); END");
        try
        {
            Call("UndoReviewFromLearningPage");
            var failedUndo = Field<StudyRound<WordItem>>("_reviewRound");
            check(failedUndo.Completed == 1 && failedUndo.IsFinished && failedUndo.Known == 1,
                "undo failure leaves the round exactly as it was instead of showing the word returned");
            check(window.FindControl<TextBlock>("ReviewHintText")!.Text!.Contains("撤销失败"),
                "undo failure is reported to the user rather than shown as a successful undo");
            check(store.GetAllWords().Single(w => w.Id == word.Id).Stage == stageBeforeUndo,
                "undo failure leaves the legacy progress projection untouched");
            check(EffectiveCanonicals() == 1, "undo failure leaves the committed canonical valid");
        }
        finally { Sql("DROP TRIGGER IF EXISTS fail_invalidate"); }
        Call("UndoReviewFromLearningPage");
        check(!Field<StudyRound<WordItem>>("_reviewRound").IsFinished && EffectiveCanonicals() == 0,
            "retry after storage recovers really invalidates the canonical (no consumed undo snapshot)");

        // —— 改判失败门：修正必须原子（事件 + canonical 失效同事务），失败不留半个改判 ——
        await (Task)Call("OnReviewRatingAsync", StudyRating.Known)!;
        check(EffectiveCanonicals() == 1, "re-rate before the revision probe commits one canonical again");
        Sql("CREATE TRIGGER fail_revise BEFORE UPDATE ON canonical_reviews BEGIN SELECT RAISE(ABORT,'injected revision failure'); END");
        try
        {
            await (Task)Call("ReclassifyReviewAsync")!;
            check(window.FindControl<TextBlock>("ReviewHintText")!.Text!.Contains("改判失败"),
                "revision failure is reported to the user rather than shown as a successful reclassification");
            check(EffectiveCanonicals() == 1, "revision failure leaves the former canonical valid");
            check(RevisedEvents() == 0, "revision failure persists no half-applied revision event");
        }
        finally { Sql("DROP TRIGGER IF EXISTS fail_revise"); }
        await (Task)Call("ReclassifyReviewAsync")!;
        check(EffectiveCanonicals() == 0 && RevisedEvents() == 1,
            "retry after storage recovers invalidates the canonical and persists exactly one revision event");

        // —— 手动管理动作对已有 FSRS 卡必须同样生效（规格书 §6：手动能力不能被旧排期静默吃掉）——
        store.AddWord("memory-manual-due", "", "手动排期用例", "");
        var manual = store.GetAllWords().Single(w => w.Word == "memory-manual-due");
        // 复习轮是顺序推进的（不评分就换不了卡），先把其它词全部推到远期，保证本轮只有这一个词。
        Sql("UPDATE words SET next_review_date='2999-01-01' WHERE id <> " + manual.Id
            + "; UPDATE fsrs_cards SET next_review_at_utc='2999-01-01T00:00:00.0000000Z'");
        store.ExecuteBatch([manual.Id], "today");
        Call("RefreshWords"); Call("OpenReviewDeck");
        var manualRound = Field<StudyRound<WordItem>>("_reviewRound");
        check(manualRound.HasCurrent && manualRound.Total == 1 && manualRound.Current.Id == manual.Id,
            "manual-override fixture really is the only front card before rating");
        await (Task)Call("OnReviewRatingAsync", StudyRating.Known)!;
        var manualKey = WordKey.Archive(manual.Archive.Uuid).Key;
        string? CardDue()
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT next_review_at_utc FROM fsrs_cards WHERE word_key=$key";
            c.Parameters.AddWithValue("$key", manualKey);
            return c.ExecuteScalar() as string;
        }
        var ratedDue = CardDue();
        check(ratedDue is { Length: > 0 } && string.CompareOrdinal(ratedDue, DateTime.UtcNow.ToString("O")) > 0,
            "the real rating scheduled the manual-override card into the future");
        foreach (var action in new[] { "stage", "restart" })
        {
            if (action == "stage") store.ExecuteBatch([manual.Id], "stage", 2);
            else store.ExecuteBatch([manual.Id], "restart");
            var legacyDate = store.GetAllWords().Single(w => w.Id == manual.Id).NextReviewDate;
            check(legacyDate is { Length: > 0 }, "manual " + action + " still writes the legacy projection date");
            var expected = DateTime.ParseExact(legacyDate!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                .ToUniversalTime().ToString("O");
            check(CardDue() == expected,
                "manual " + action + " moves the FSRS due to the same local day instead of silently doing nothing");
        }
        // 手动「完成本次复习」走的是界面入口（评分路径也写同一个旧列投影，但绝不允许碰 due）。
        // 该动作对"今天已经复习过"的词是幂等的；先清掉同日记录，保证这次真的改动了旧列投影
        // （否则前置条件不成立，断言会退化成"两个没变的日期相等"）。
        var legacyBeforeManualReview = store.GetAllWords().Single(w => w.Id == manual.Id).NextReviewDate;
        var dueBeforeManualReview = CardDue();
        Sql("DELETE FROM review_logs WHERE word_id = " + manual.Id
            + " AND log_date = '" + DateTime.Today.ToString("yyyy-MM-dd") + "'");
        Call("ExecuteBatchAction", new List<long> { manual.Id }, "review", null);
        check(store.GetAllWords().Single(w => w.Id == manual.Id).NextReviewDate != legacyBeforeManualReview,
            "the manual 'complete this review' probe really moved the legacy projection date");
        var reviewLegacy = store.GetAllWords().Single(w => w.Id == manual.Id).NextReviewDate;
        check(reviewLegacy is { Length: > 0 }, "manual 'complete this review' still writes the legacy projection date");
        check(CardDue() == DateTime.ParseExact(reviewLegacy!, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime().ToString("O"),
            "manual 'complete this review' moves the FSRS due too, instead of only the projection");
    }
}
