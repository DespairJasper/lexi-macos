using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Data.Sqlite;

namespace Lexi;

/// <summary>真实MainWindow事件+隔离SQLite；重绑协调器模拟进程内重启，不冒充真实进程重启。</summary>
public static class MemoryRecoveryUiTests
{
    public static async Task RunAsync(MainWindow window, Action<bool, string> check)
    {
        var isolated = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(isolated)) throw new InvalidOperationException("Recovery UI tests require isolated data.");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        async Task Invoke(string name, params object[] args) => await (Task)Call(name, args)!;
        var originalArchive = Field<IVocabularyArchive>("_vocabService");
        var originalPlanStore = Field<DailyStudyPlanStore>("_studyPlanStore");
        var originalPlans = Field<List<DailyStudyPlan>>("_studyPlans");
        var folder = Path.Combine(isolated, "memory-recovery-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var fixture = ServiceFactory.OpenArchive(Path.Combine(folder, "vocab.sqlite3"));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, Pooling = false }.ToString());
        db.Open();
        void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        long Count(string sql, string? key = null)
        {
            using var command = db.CreateCommand(); command.CommandText = sql;
            if (key is not null) command.Parameters.AddWithValue("$key", key);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        try
        {
            Set("_vocabService", fixture);
            Call("RebindMemory"); Call("RefreshWords");
            Field<Dictionary<long, int>>("_reviewHandled").Clear();
            fixture.AddWord("recovery-round-one", "", "恢复甲", "");
            fixture.AddWord("recovery-round-two", "", "恢复乙", "");
            var words = fixture.GetAllWords();
            fixture.ExecuteBatch(words.Select(w => w.Id).ToArray(), "today");
            Call("RefreshWords"); Call("ShowPage", "review"); Call("OpenReviewDeck");
            var round = Field<StudyRound<WordItem>>("_reviewRound");
            check(round.Total == 2 && round.HasCurrent, "recovery probe starts two real review cards in isolated database");
            var memory = Field<LearningMemoryCoordinator>("_memory");
            var session = memory.CurrentSessionId;
            var key = WordKey.Archive(round.Current.Archive.Uuid).Key;
            await Invoke("OnReviewRatingAsync", StudyRating.Unsure);
            check(round.Completed == 0 && round.Unsure == 1, "first Unsure remains pending for reinforcement");
            await Invoke("AdvanceReviewAsync");
            var currentId = round.Current.Id;
            var store = Field<ILearningMemoryStore>("_memoryStore");
            var eventsBefore = store.LoadEvents(session);
            var rated = eventsBefore.Single(e => e.Kind == InteractionEventKind.Rated);
            var checkpoint = store.GetSessionCheckpoint("review");
            check(checkpoint?.SessionId == session && rated.Response == StudyRating.Unsure,
                "real UI saves unfinished session checkpoint and initial Unsure before restart");
            var streak = round.CurrentStreak;
            // 真正的新进程启动与「进程内重绑」唯一的语义差异就是 run 标识（见 MainWindow.MemoryProcessRunId）：
            // 恢复资格只认更早一次启动写下的 checkpoint。这里显式换标识来模拟新进程，
            // 不再依赖「重绑即可恢复」这个已经不成立的假设。跨真实进程边界见 MemoryRestartTests。
            Set("_memoryRunId", Guid.NewGuid().ToString("N"));
            // Replace coordinator and registrations, as startup does, and reconstruct solely from disk.
            Call("RebindMemory");
            Set("_reviewRevealed", false); Set("_reviewCompleted", false);
            Field<Dictionary<long, int>>("_reviewHandled").Clear();
            Call("OpenReviewDeck");
            var recovered = Field<LearningMemoryCoordinator>("_memory");
            check(recovered.CurrentSessionId == session && round.Current.Id == currentId && round.CurrentStreak == streak,
                "UI rebind restores same session, current word and reinforcement streak");
            check(Field<ILearningMemoryStore>("_memoryStore").LoadEvents(session).Count == eventsBefore.Count,
                "restoring the same answered card does not invent another presentation");
            check(Field<ILearningMemoryStore>("_memoryStore").LoadEvents(session).Single(e => e.EventId == rated.EventId).Response == StudyRating.Unsure,
                "restart preserves the original response event");
            // 恢复出来的轮里没有"上次评分的是哪个词"（它只在内存里）：此时撤销必须什么都不做，
            // 绝不把 Undone 事件打到"当前显示的那张卡"上。
            var eventsBeforeUnarmedUndo = Field<ILearningMemoryStore>("_memoryStore").LoadEvents(session).Count;
            Call("UndoReviewFromLearningPage");
            check(Field<ILearningMemoryStore>("_memoryStore").LoadEvents(session).Count == eventsBeforeUnarmedUndo
                && !Field<ILearningMemoryStore>("_memoryStore").LoadEvents(session).Any(e => e.Kind == InteractionEventKind.Undone),
                "an unarmed undo after a restart writes nothing instead of hitting the displayed card");
            // Complete the pending real round through its actual handlers.
            for (var guard = 0; guard < 25 && !round.IsFinished; guard++)
            {
                if (Field<bool>("_reviewRevealed")) await Invoke("AdvanceReviewAsync");
                else await Invoke("OnReviewRatingAsync", StudyRating.Known);
            }
            check(round.IsFinished, "restored reinforcement round really completes");
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0", key) == 1,
                "restored word receives one effective canonical");
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND origin='FirstRetrieval' AND rating='Unsure' AND invalidated=0", key) == 1,
                "restored canonical retains first Unsure despite later Known reinforcement");
            check(Count("SELECT reps FROM fsrs_cards WHERE word_key=$key", key) == 1,
                "restored canonical updates FSRS exactly once");

            // A separate real card exposes same-screen Known -> Forgot invalidation.
            fixture.AddWord("recovery-correction", "", "改判用例", "");
            var correction = fixture.GetAllWords().Single(w => w.Word == "recovery-correction");
            fixture.ExecuteBatch([correction.Id], "today");
            Call("RefreshWords"); Call("OpenReviewDeck");
            check(round.HasCurrent && round.Current.Id == correction.Id, "correction fixture is the actual visible review card");
            var correctionKey = WordKey.Archive(correction.Archive.Uuid).Key;
            await Invoke("OnReviewRatingAsync", StudyRating.Known);
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0", correctionKey) == 1,
                "real Known commits one Good before correction");
            await Invoke("ReclassifyReviewAsync");
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0", correctionKey) == 0,
                "same-screen correction invalidates former Good canonical");
            check(Count("SELECT COUNT(*) FROM fsrs_cards WHERE word_key=$key", correctionKey) == 0,
                "same-screen correction removes former Good FSRS side effect");
            check(Field<ILearningMemoryStore>("_memoryStore").LoadEvents(Field<LearningMemoryCoordinator>("_memory").CurrentSessionId)
                .Any(e => e.Kind == InteractionEventKind.Revised && e.Response == StudyRating.Forgot),
                "same-screen correction persists its real revision event");
            // Plan completion must include the final third rating before canonical reduction.
            async Task<DailyStudyPlan> StartPlan(string wordText)
            {
                fixture.AddWord(wordText, "", "计划测试", "");
                var item = fixture.GetAllWords().Single(w => w.Word == wordText);
                Call("RefreshWords");
                var plan = DailyStudyPlanRules.Create("Recovery plan " + wordText, DailyStudyPlanSource.Archive,
                    "test", [new DailyStudyPlanWord { Id = item.Id.ToString(), Word = item.Word, Meaning = item.Translation }],
                    1, false, 713);
                Set("_studyPlanStore", new DailyStudyPlanStore(Path.Combine(folder, "daily-study-plans.json")));
                Set("_studyPlans", new List<DailyStudyPlan> { plan });
                Call("StartPlanCardRound", plan, StudyMode.FirstLearn, null!);
                check(Field<bool>("_planCardActive") && Field<StudyRound<string>>("_focusRound").HasCurrent,
                    "plan fixture really opens shared card UI");
                await Invoke("CompleteFocusLearnAsync");
                return plan;
            }
            async Task KnownPlan(bool advance)
            {
                await Invoke("RateFocusedWordAsync", StudyRating.Known);
                if (advance) await Invoke("AdvanceFocusAsync");
            }
            var plan = await StartPlan("recovery-plan-complete");
            await KnownPlan(true); await KnownPlan(true); await KnownPlan(false);
            var planKey = WordKey.Archive(fixture.GetAllWords().Single(w => w.Word == "recovery-plan-complete").Archive.Uuid).Key;
            check(plan.CompletedWordIds.Count == 1 && Field<StudyRound<string>>("_focusRound").Completed == 1,
                "third real plan Known completes one plan word");
            check(Count("SELECT final_known_count FROM word_session_summaries WHERE word_key=$key", planKey) == 3,
                "plan canonical summary contains all three real Known responses");
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0", planKey) == 1,
                "plan final response produces one effective canonical");

            var failedPlan = await StartPlan("recovery-plan-json-failure");
            await KnownPlan(true); await KnownPlan(true);
            var jsonPath = Path.Combine(folder, "daily-study-plans.json");
            File.Move(jsonPath, jsonPath + ".saved", overwrite: true);
            Directory.CreateDirectory(jsonPath);
            try
            {
                await KnownPlan(false);
                check(!Field<bool>("_focusShownCompleted"), "JSON delivery failure does not show completed card");
                check(Count("SELECT COUNT(*) FROM mutation_outbox WHERE applied_at_utc IS NULL") > 0,
                    "failed JSON materialization retains replayable pending target");
                // canonical 已提交、JSON 未交付：此时再点一次评分不能重复计分（必须先排空在途写入）。
                var failedPlanKey = WordKey.Archive(fixture.GetAllWords()
                    .Single(w => w.Word == "recovery-plan-json-failure").Archive.Uuid).Key;
                check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0", failedPlanKey) == 1,
                    "the committed canonical really exists before the retry probe");
                // 这次重评会在入口被"有待交付写入"的守卫短路（RatePlanFocusedWordAsync 开头就返回），
                // 因此这条断言证的是**结果不变量**：在途交付未完成时重复作答不会产生第二条 canonical。
                await KnownPlan(false);
                check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0", failedPlanKey) == 1,
                    "a rating retried while a delivered write is pending does not create a second canonical");
                check(Count("SELECT COUNT(*) FROM mutation_outbox WHERE applied_at_utc IS NULL") > 0,
                    "the retry did not silently declare the undelivered plan write done");
                // 新的计划编辑也不能越过未交付的快照：必须先排空 outbox，失败就报失败。
                check(Call("MemorySavePlanProgress") is false,
                    "a new plan edit cannot bypass an undelivered pending snapshot");
            }
            finally { Directory.Move(jsonPath, jsonPath + ".blocking-directory"); }
            Call("MemoryReplayJournal");
            check(Count("SELECT COUNT(*) FROM mutation_outbox WHERE applied_at_utc IS NULL") == 0,
                "restored filesystem permits durable target replay");
            var delivered = JsonSerializer.Deserialize<List<DailyStudyPlan>>(File.ReadAllText(jsonPath))!;
            check(delivered.Single(p => p.Id == failedPlan.Id).CompletedWordIds.Contains(failedPlan.Words[0].Id),
                "replay materializes completed target state rather than stale partial plan");

            // —— pending finalize 窗口：轮内已完成、canonical 尚未提交（两次事务之间），
            //    checkpoint 会带着 Finished=true，重启时绝不能当"派生事实已提交"直接丢掉 ——
            // 上一段留下了在途的计划完成回调；RatePlanFocusedWordAsync 会先把它排空并跳过本次评分。
            Call("MemoryTryCompletePending");
            var crashPlan = await StartPlan("recovery-plan-pending-finalize");
            var crashKey = WordKey.Archive(fixture.GetAllWords().Single(w => w.Word == "recovery-plan-pending-finalize").Archive.Uuid).Key;
            // 首次学习要攒够三次「认识」才算完成本轮，前两次正常提交，只有第三次落在被阻断的定稿上。
            await KnownPlan(true); await KnownPlan(true);
            Sql("CREATE TRIGGER fail_pending_finalize BEFORE INSERT ON canonical_reviews BEGIN SELECT RAISE(ABORT,'injected finalize failure'); END");
            try { await KnownPlan(false); }
            finally { Sql("DROP TRIGGER IF EXISTS fail_pending_finalize"); }
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key", crashKey) == 0,
                "blocked canonical really left the completed round without its finalize");
            var crashCheckpoint = Field<ILearningMemoryStore>("_memoryStore").GetSessionCheckpoint("plan");
            check(crashCheckpoint is not null, "the interrupted round still has a durable checkpoint");
            using (var saved = JsonDocument.Parse(crashCheckpoint!.QueueJson))
            {
                check(saved.RootElement.GetProperty("Finished").GetBoolean()
                    && saved.RootElement.GetProperty("PendingFinalize").GetArrayLength() == 1,
                    "checkpoint reports the round finished while still carrying the pending finalize");
            }
            // 真正的新进程启动与进程内重绑唯一的语义差异就是 run 标识（见 MainWindow.MemoryProcessRunId）。
            Set("_memoryRunId", Guid.NewGuid().ToString("N"));
            Call("RebindMemory"); Call("RefreshWords");
            Call("StartPlanCardRound", crashPlan, StudyMode.FirstLearn, null!);
            check(Count("SELECT COUNT(*) FROM canonical_reviews WHERE word_key=$key AND invalidated=0", crashKey) == 1,
                "restart replays the pending finalize instead of dropping a finished-but-uncommitted round");
            check(Count("SELECT reps FROM fsrs_cards WHERE word_key=$key", crashKey) == 1,
                "the replayed finalize updates the FSRS card exactly once");
            check(Count("SELECT final_known_count FROM word_session_summaries WHERE word_key=$key", crashKey) == 3,
                "the replayed finalize writes the full word-session summary (all three Known responses)");

            // —— 完成 / 改判 / 撤销 / 掌握之后，持久 checkpoint 必须与轮内状态一致 ——
            // 用**两词一轮**：复习词第一次「认识」就定稿，但仍有一张待答卡，所以本轮不会立刻结束，
            // 改判/掌握都还能作用在刚定稿的那张卡上（单词一轮会直接结束，那些路径根本走不到）。
            fixture.AddWord("recovery-sync-one", "", "检查点甲", "");
            fixture.AddWord("recovery-sync-two", "", "检查点乙", "");
            var syncOne = fixture.GetAllWords().Single(w => w.Word == "recovery-sync-one");
            var syncTwo = fixture.GetAllWords().Single(w => w.Word == "recovery-sync-two");
            Sql("UPDATE words SET next_review_date='2999-01-01' WHERE id NOT IN (" + syncOne.Id + "," + syncTwo.Id + ")"
                + "; UPDATE fsrs_cards SET next_review_at_utc='2999-01-01T00:00:00.0000000Z'");
            fixture.ExecuteBatch([syncOne.Id, syncTwo.Id], "today");
            Call("RefreshWords"); Call("ShowPage", "review");
            var syncRound = Field<StudyRound<WordItem>>("_reviewRound");
            check(syncRound.Total == 2 && syncRound.HasCurrent,
                "checkpoint fixture really starts a two-card review round");
            var firstKey = WordKey.Archive(syncRound.Current.Archive.Uuid).Key;
            void CheckCheckpointMatchesRound(string stage)
            {
                var saved = Field<ILearningMemoryStore>("_memoryStore").GetSessionCheckpoint("review");
                check(saved is not null, "review checkpoint exists after " + stage);
                using var outer = JsonDocument.Parse(saved!.QueueJson);
                using var round = JsonDocument.Parse(outer.RootElement.GetProperty("RoundJson").GetString()!);
                var r = round.RootElement;
                var durableCurrent = r.GetProperty("Current").GetArrayLength() == 0
                    ? null : r.GetProperty("Current")[0].GetProperty("WordId").GetString();
                var liveCurrent = syncRound.HasCurrent
                    ? syncRound.Current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                check(r.GetProperty("Total").GetInt32() == syncRound.Total
                    && r.GetProperty("Completed").GetInt32() == syncRound.Completed
                    && r.GetProperty("Known").GetInt32() == syncRound.Known
                    && r.GetProperty("Unsure").GetInt32() == syncRound.Unsure
                    && r.GetProperty("Forgot").GetInt32() == syncRound.Forgot
                    && durableCurrent == liveCurrent,
                    "durable checkpoint matches the live round after " + stage);
            }
            List<string> DurableCommitted()
            {
                var saved = Field<ILearningMemoryStore>("_memoryStore").GetSessionCheckpoint("review")!;
                using var outer = JsonDocument.Parse(saved.QueueJson);
                return outer.RootElement.GetProperty("Committed").EnumerateArray()
                    .Select(e => e.GetString()!).ToList();
            }
            await Invoke("OnReviewRatingAsync", StudyRating.Known);
            check(Field<HashSet<string>>("_committedWordKeys").Contains(firstKey)
                && DurableCommitted().Contains(firstKey),
                "the finishing Known really committed the word in this session");
            CheckCheckpointMatchesRound("a completed review rating");
            await Invoke("ReclassifyReviewAsync");
            CheckCheckpointMatchesRound("a reclassification");
            check(!DurableCommitted().Contains(firstKey) && !Field<HashSet<string>>("_committedWordKeys").Contains(firstKey),
                "an invalidated word is dropped from the durable committed set after a reclassification");
            Call("UndoReviewFromLearningPage");
            CheckCheckpointMatchesRound("an undo");
            check(!DurableCommitted().Contains(firstKey),
                "an undone word stays out of the durable committed set");
            // 掌握出队同样要落盘：不落盘的话重启会把已经掌握出队的卡装回来。
            await Invoke("OnReviewRatingAsync", StudyRating.Known);
            await Invoke("MasterReviewFromLearningPageAsync");
            CheckCheckpointMatchesRound("mastering the current card");

            // 队列里的词被删掉后，检查点再也恢复不了：必须丢弃它并照常开轮，
            // 否则复习页会在同一处永久抛出（连点开都做不到）。
            fixture.AddWord("recovery-doomed", "", "失效检查点", "");
            var doomedWord = fixture.GetAllWords().Single(w => w.Word == "recovery-doomed");
            fixture.ExecuteBatch([doomedWord.Id], "today");
            // 只留这一张卡到期：否则本轮会带着别的待答词，"未完成检查点"的前提就不成立了。
            // 注意排除已掌握词：给 mastered 词写回复习日期会被 DatabaseSafety 判为非法数据。
            Sql("UPDATE words SET next_review_date='2999-01-01' WHERE id <> " + doomedWord.Id
                + " AND status <> 'mastered'"
                + "; UPDATE fsrs_cards SET next_review_at_utc='2999-01-01T00:00:00.0000000Z'");
            Call("RefreshWords"); Call("ShowPage", "review");
            check(Field<StudyRound<WordItem>>("_reviewRound").HasCurrent
                && Field<StudyRound<WordItem>>("_reviewRound").Total == 1,
                "the doomed fixture really owns a pending (not finished) checkpoint");
            var staleSession = Field<ILearningMemoryStore>("_memoryStore").GetSessionCheckpoint("review")!.SessionId;
            fixture.ExecuteBatch([doomedWord.Id], "delete");
            Set("_memoryRunId", Guid.NewGuid().ToString("N"));
            Call("RebindMemory"); Call("RefreshWords");
            var restoreThrew = false;
            try { Call("ShowPage", "review"); }
            catch (Exception ex) { restoreThrew = true; check(false, "unresolvable checkpoint must not throw: " + ex.Message); }
            check(!restoreThrew, "a checkpoint whose word no longer exists can no longer lock the review surface");
            var afterDrop = Field<ILearningMemoryStore>("_memoryStore").GetSessionCheckpoint("review");
            check(afterDrop is null || afterDrop.SessionId != staleSession,
                "the unresolvable checkpoint is discarded instead of being retried forever");
            Call("ShowPage", "lookup"); Call("ShowPage", "review");
            check(true, "the review surface still opens on every later visit");

            // —— 长期层被停用后，开轮必须照常（开轮不是真实 retrieval 的持久化承诺）——
            Call("MarkMemoryDisabled", "injected disabled layer");
            var openThrew = false;
            try { Call("ShowPage", "lookup"); Call("ShowPage", "review"); }
            catch (Exception ex) { openThrew = true; check(false, "disabled layer must not block opening a round: " + ex.Message); }
            check(!openThrew, "a disabled long-term layer never blocks opening the review deck");
            check(Field<bool>("_memoryDisabled"),
                "the disabled layer really stayed disabled for this probe");
            Call("RebindMemory");
        }
        finally
        {
            if (Field<bool>("_planCardActive")) Call("ExitWordFocus");
            Set("_studyPlanStore", originalPlanStore); Set("_studyPlans", originalPlans);
            Set("_vocabService", originalArchive);
            Call("RebindMemory"); Call("RefreshWords");
            (fixture as IDisposable)?.Dispose();
        }
    }
}
