using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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
        long SourceReviewId(string wordKey) => (long)typeof(MainWindow)
            .GetMethod("MemorySourceReviewId", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { wordKey })!;
        async Task Invoke(string name, params object[] args) => await (Task)Call(name, args)!;
        void StartReviewBatch() => window.FindControl<Button>("ReviewStartAllBtn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
            StartReviewBatch();
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
            Call("ShowPage", "lookup"); Call("ShowPage", "review"); StartReviewBatch();
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

            // 3.2.3 回归：真实启动顺序下先装填一次到期队列（RefreshWords → UpdateReviewBadge），
            // 此时 IELTS 目录还没加载，来源卡只能按原始标识回退；目录就绪后缓存必须补全为目录词条，
            // 否则复习页会一直显示 "vocab:…:378" 这类内部 ID（音标与释义也为空）。
            var catalog = Field<IeltsCatalog?>("_ieltsCatalog")
                ?? throw new InvalidOperationException("catalogue is required for the review-identity regression.");
            var entry = catalog.Find("atmosphere")
                ?? throw new InvalidOperationException("catalogue fixture word is missing.");
            var ieltsKey = WordKeyResolver.FromIelts(entry);
            const string ieltsPresentation = "source-due-ielts-presentation";
            memory.BeginSession(StudyMode.FirstLearn, WordSource.Ielts, "", 1);
            memory.OnPresented(ieltsKey, ieltsPresentation, isRecall: true);
            memory.OnRated(ieltsKey, ieltsPresentation, StudyRating.Known, 0, 1, null);
            check(memory.CommitWord(ieltsKey, StudyMode.FirstLearn) is not null,
                "ielts fixture commits a canonical for a real catalogue word");
            check(Count("SELECT COUNT(*) FROM fsrs_cards WHERE word_key=$key", ieltsKey.Key) == 1,
                "ielts fixture created one FSRS card for the catalogue identity");
            Sql("UPDATE fsrs_cards SET next_review_at_utc='2000-01-01T00:00:00.0000000Z'");
            Field<Dictionary<long, WordKey>>("_memorySourceReviewKeys").Clear();
            Field<Dictionary<string, WordItem>>("_memorySourceReviewWords").Clear();
            Set("_ieltsCatalog", null);
            Call("UpdateReviewBadge");
            var cache = Field<Dictionary<string, WordItem>>("_memorySourceReviewWords");
            var stale = cache[ieltsKey.Key];
            check(stale.Id == SourceReviewId(ieltsKey.Key) && stale.Word == entry.Id,
                "regression precondition: before the catalogue exists the review cache falls back to the raw key");
            Set("_ieltsCatalog", catalog);
            var deckWords = Call("GetPendingReviewWords") as List<WordItem> ?? [];
            var filled = deckWords.FirstOrDefault(w => w.Id == stale.Id);
            check(filled is not null && filled.Word == entry.Word && filled.Translation == entry.Meaning
                && filled.Phonetic == entry.Phonetic,
                "3.2.3: filling the review deck completes the cached catalogue card instead of showing its raw key");
            check(!string.Equals(filled?.Word, entry.Id, StringComparison.Ordinal),
                "3.2.3: the review card never renders the internal catalogue id");
            var rebuilt = Call("MemoryResolveReviewIdentity", stale.Id.ToString(CultureInfo.InvariantCulture)) as WordItem;
            check(rebuilt is not null && rebuilt.Word == entry.Word,
                "3.2.3: the resume/undo identity path also returns the completed catalogue card");
            Call("ShowPage", "review");
            StartReviewBatch();
            var finalRound = Field<StudyRound<WordItem>>("_reviewRound");
            check(finalRound.HasCurrent && window.FindControl<TextBlock>("ReviewWordText")?.Text == finalRound.Current.Word,
                "the review page renders the resolved word text from the deck");

            // Six new source books must survive the same pre-catalog cache path fixed in 3.2.3.
            // Each is forced due only inside this disposable fixture database.
            foreach (var book in Field<ExamCatalog>("_examCatalog").Books)
            {
                Call("ShowPage", "lookup");
                Sql("UPDATE fsrs_cards SET next_review_at_utc='2999-01-01T00:00:00.0000000Z'");
                var examEntry = book.AllWords.First(w => w.Example.Length > 0 && w.Extra.Length > 0 && w.Phonetic.Length > 0);
                var examKey = WordKeyResolver.FromIelts(examEntry);
                var shown = "exam-due-" + book.Id;
                memory.BeginSession(StudyMode.FirstLearn, WordSource.Ielts, "", 1);
                memory.OnPresented(examKey, shown, isRecall: true);
                memory.OnRated(examKey, shown, StudyRating.Known, 0, 1, null);
                check(memory.CommitWord(examKey, StudyMode.FirstLearn) is not null, book.EnglishTitle + " due fixture commits the exact book identity");
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "UPDATE fsrs_cards SET next_review_at_utc='2000-01-01T00:00:00.0000000Z' WHERE word_key=$key";
                    command.Parameters.AddWithValue("$key", examKey.Key); command.ExecuteNonQuery();
                }
                Field<Dictionary<long, WordKey>>("_memorySourceReviewKeys").Clear();
                Field<Dictionary<string, WordItem>>("_memorySourceReviewWords").Clear();
                Set("_ieltsCatalog", null); Call("UpdateReviewBadge");
                var pending = Field<Dictionary<string, WordItem>>("_memorySourceReviewWords")[examKey.Key];
                check(pending.Word == examEntry.Id, book.EnglishTitle + " reproduces early-startup missing-catalog cache");
                Set("_ieltsCatalog", catalog);
                Call("ShowPage", "review");
                check(window.FindControl<TextBlock>("ReviewWordPreview")!.Text == examEntry.Word,
                    book.EnglishTitle + " review preview never exposes internal word IDs");
                StartReviewBatch();
                var examRound = Field<StudyRound<WordItem>>("_reviewRound");
                check(examRound.HasCurrent && examRound.Current.Word == examEntry.Word
                    && examRound.Current.Phonetic == examEntry.Phonetic && examRound.Current.Translation == examEntry.Meaning,
                    book.EnglishTitle + " due review repairs cached word, phonetics and meaning");
                check(window.FindControl<TextBlock>("ReviewWordText")?.Text == examEntry.Word,
                    book.EnglishTitle + " actual review surface displays the word rather than its ID");
                await Invoke("OnReviewRatingAsync", StudyRating.Known);
                check(window.FindControl<StackPanel>("ReviewDetails")!.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == examEntry.Example),
                    book.EnglishTitle + " review answer renders its own examples");
                check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0 AND origin='FirstRetrieval'", examKey.Key) == 1,
                    book.EnglishTitle + " due review records exactly one retrieval on the original identity");
                check(fixture.GetAllWords().Count == archiveCountBefore, book.EnglishTitle + " due review leaves the personal archive unchanged");
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
