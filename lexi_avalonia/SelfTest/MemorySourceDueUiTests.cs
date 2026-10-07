using System.Reflection;
using Avalonia.Controls;
using Microsoft.Data.Sqlite;

namespace Lexi;

/// <summary>
/// 非档案到期卡（教材 / 词形）进入既有复习入口的端到端验证。
/// 长期记忆层的 FSRS 卡对 <c>form:</c>/<c>ielts:</c> 身份同样排期，到期后必须能被复习页装填、
/// 评分、撤销，而且**绝不**因此给用户自动建立档案行（规格书 §3 / §9.1）。
/// </summary>
/// <remarks>
/// 卡本身用真实 <see cref="LearningMemoryCoordinator"/> 建立、真实 canonical + FSRS 卡，
/// 只是不经过"查词页开一轮"那段 UI（那段由既有 focus/IELTS 测试覆盖）；这里要验证的是
/// 复习页这一侧的接线：装填、身份解析、评分、撤销与档案隔离。
/// </remarks>
public static class MemorySourceDueUiTests
{
    private const string SourceWord = "source-due-fixture";

    public static async Task RunAsync(MainWindow window, Action<bool, string> check)
    {
        var isolated = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(isolated)) throw new InvalidOperationException("Source-due UI tests require isolated data.");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        async Task Invoke(string name, params object[] args) => await (Task)Call(name, args)!;

        var originalArchive = Field<IVocabularyArchive>("_vocabService");
        var folder = Path.Combine(isolated, "memory-source-due-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var fixture = ServiceFactory.OpenArchive(Path.Combine(folder, "vocab.sqlite3"));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, Pooling = false }.ToString());
        db.Open();
        void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        long Count(string sql, string key)
        {
            using var command = db.CreateCommand(); command.CommandText = sql;
            command.Parameters.AddWithValue("$key", key);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        try
        {
            Set("_vocabService", fixture);
            Call("RebindMemory"); Call("RefreshWords");
            Field<Dictionary<long, int>>("_reviewHandled").Clear();
            var archiveCountBefore = fixture.GetAllWords().Count;

            // 真实协调器给一个「不在档案里」的词形建立 canonical 与 FSRS 卡。
            var memory = Field<LearningMemoryCoordinator>("_memory");
            var key = WordKeyResolver.FromForm(SourceWord);
            const string presentation = "source-due-presentation";
            memory.BeginSession(StudyMode.FirstLearn, WordSource.Archive, "", 1);
            memory.OnPresented(key, presentation, isRecall: true);
            memory.OnRated(key, presentation, StudyRating.Known, 0, 1, null);
            check(memory.CommitWord(key, StudyMode.FirstLearn) is not null,
                "source-due fixture really commits a canonical for a non-archive word form");
            check(Count("SELECT COUNT(*) FROM fsrs_cards WHERE word_key=$key", key.Key) == 1,
                "source-due fixture really created one FSRS card for the non-archive identity");
            Sql("UPDATE fsrs_cards SET next_review_at_utc='2000-01-01T00:00:00.0000000Z'");

            Call("ShowPage", "review");
            var round = Field<StudyRound<WordItem>>("_reviewRound");
            check(round.HasCurrent && round.Current.Id < 0 && round.Current.Word == SourceWord,
                "review queue reaches a non-archive due card through the real deck entry point");
            check(Field<List<WordItem>>("_allWords").All(w => w.Id > 0) && fixture.GetAllWords().Count == archiveCountBefore,
                "filling the review queue never auto-archives the non-archive word");
            check(round.Current.Archive.Uuid.Length == 0,
                "the temporary review card never borrows an archive identity");

            await Invoke("OnReviewRatingAsync", StudyRating.Known);
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0 AND origin='FirstRetrieval'", key.Key) == 1,
                "rating the source card commits a real retrieval canonical on the source identity");
            check(Count("SELECT reps FROM fsrs_cards WHERE word_key=$key", key.Key) == 2,
                "rating the source card updates its FSRS card exactly once more");
            check(fixture.GetAllWords().Count == archiveCountBefore,
                "rating the source card writes nothing into the archive");

            // 显式手动重排到今日（与 ExecuteBatch("today") 语义一致），再做第二次真实复习。
            Sql("UPDATE fsrs_cards SET next_review_at_utc='2000-01-01T00:00:00.0000000Z'");
            Call("ShowPage", "lookup"); Call("ShowPage", "review");
            var reopened = Field<StudyRound<WordItem>>("_reviewRound");
            check(reopened.HasCurrent && reopened.Current.Id < 0 && reopened.Current.Word == SourceWord,
                "an explicitly rescheduled source card is served again from the real deck entry point");
            await Invoke("OnReviewRatingAsync", StudyRating.Known);
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0 AND origin='FirstRetrieval'", key.Key) == 2,
                "a second real retrieval on the source card is a separate canonical (same-day cross-session rule)");
            Call("UndoReviewFromLearningPage");
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0 AND origin='FirstRetrieval'", key.Key) == 1,
                "undoing the source card invalidates its canonical through the long-term layer");
            check(fixture.GetAllWords().Count == archiveCountBefore,
                "undoing the source card still leaves the archive untouched");

            // 同一个词形在 ielts: 与 form: 是两条独立身份，绝不合并：真实目录条目 + 同一个词形。
            var catalogEntry = Field<IeltsCatalog?>("_ieltsCatalog")?.AllWords.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w.Id));
            if (catalogEntry is not null)
            {
                var resolved = WordKeyResolver.Resolve(catalogEntry.Word, WordKeyResolver.IeltsPageKind, _ => null, _ => catalogEntry);
                check(resolved.Source == WordSource.Ielts && resolved.Key != key.Key,
                    "the same word form resolves to a different identity when the IELTS catalogue owns it");
                check(Count("SELECT COUNT(*) FROM fsrs_cards WHERE word_key=$key", resolved.Key) == 0,
                    "the form-keyed FSRS card never leaks into the IELTS identity");
            }
        }
        finally
        {
            Set("_vocabService", originalArchive);
            Call("RebindMemory"); Call("RefreshWords");
            (fixture as IDisposable)?.Dispose();
        }
    }
}
