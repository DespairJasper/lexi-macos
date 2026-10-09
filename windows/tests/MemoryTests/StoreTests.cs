using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lexi.Tests;

/// <summary>
/// 持久层（<c>VocabularyService.Memory.cs</c>，schema v2 + <see cref="ILearningMemoryStore"/>）的回归测试。
///
/// 所有断言都在**自己的临时目录**里建库（<c>Path.GetTempPath()/lexi-store-test-&lt;guid&gt;</c>），
/// 结束时整目录删除；绝不读写真实用户数据目录，也不依赖 <c>LEXI_DATA_DIR</c>。
/// 只读源码（MemoryModels.cs / VocabularyContracts.cs / VocabularyService.Memory.cs）给出的真实列名与语义。
/// </summary>
public static class StoreTests
{
    /// <summary>固定的 UTC 锚点，避免依赖"当前时刻"造成不稳定。</summary>
    private static readonly DateTime Anchor = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>旧库快照用的列清单（顺序固定，便于逐行比对）。</summary>
    private const string WordsColumns =
        "id,word,phonetic,translation,definition,notes,stage,status,created_at,learning_start_date,next_review_date,last_reviewed_at,review_count";
    private const string ReviewLogsColumns =
        "id,word_id,action,old_stage,new_stage,old_status,new_status,old_next_review_date,new_next_review_date,"
        + "old_learning_start_date,new_learning_start_date,log_time,log_date";
    private const string WordArchivesColumns =
        "word_id,uuid,source_type,source_title,source_excerpt,tags_json,encounter_count,revision,"
        + "created_at_utc,updated_at_utc,last_encountered_at_utc,ai_json";

    /// <summary>MemorySchemaSql 建立的 9 张表。</summary>
    private static readonly string[] MemoryTables =
    [
        "learning_sessions",
        "learning_events",
        "word_session_summaries",
        "canonical_reviews",
        "fsrs_cards",
        "context_snapshots",
        "scheduler_decisions",
        "personalization_models",
        "mutation_outbox",
    ];

    public static void Run()
    {
        FreshDatabaseMigrates();
        LegacyV1UpgradesInPlace();
        AppendEventIsIdempotent();
        SessionRoundTripAndUpsert();
        CommitWordSessionIsIdempotent();
        PartialUniqueIndexRejectsSecondActiveCanonical();
        InvalidateReplaysPreStateWithoutDeletingEvents();
        DueQueueFiltersAndOrders();
        ContextSnapshotsAndLabels();
        MutationOutbox();
        SchedulerDecisionLog();
        MemoryLayerDoesNotTouchWords();
        UtcRoundTrip();
        PersonalizationModel();
        UndoRejectsNonLatestCanonical();
        HighFrequencyWritesDoNotBackupTheWholeDatabase();
        UnspecifiedTimestampIsInterpretedAsUtc();
        SameDaySecondSessionKeepsLapseAndUsesShortTermPath();
    }

    // =====================================================================
    // 1. 全新库的迁移
    // =====================================================================
    private static void FreshDatabaseMigrates()
    {
        using var box = new Sandbox();

        foreach (var table in MemoryTables)
            Program.Check(TableExists(box.DbPath, table), $"新库存在表 {table}");

        var versions = QueryInts(box.DbPath, "SELECT version FROM schema_migrations ORDER BY version");
        Program.Check(versions.Count == 2 && versions[0] == 1 && versions[1] == 2,
            $"schema_migrations 含 1 与 2（实际 {string.Join(",", versions)}）");

        Program.Check(IndexExists(box.DbPath, "ux_canonical_active"), "新库存在索引 ux_canonical_active");
        var indexSql = ScalarText(box.DbPath,
            "SELECT COALESCE(sql,'') FROM sqlite_master WHERE type='index' AND name='ux_canonical_active'");
        Program.Check(indexSql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                      && indexSql.Contains("word_key", StringComparison.OrdinalIgnoreCase)
                      && indexSql.Contains("session_id", StringComparison.OrdinalIgnoreCase)
                      && indexSql.Contains("invalidated", StringComparison.OrdinalIgnoreCase)
                      && indexSql.Contains("WHERE", StringComparison.OrdinalIgnoreCase),
            $"ux_canonical_active 是 (word_key, session_id) WHERE invalidated=0 的部分唯一索引（实际 DDL: {indexSql}）");

        Program.Check(ReferenceEquals(box.ArchivePort, box.MemoryPort),
            "ServiceFactory.OpenMemory(OpenArchive(path)) 返回同一个实例");

        Program.Check(string.Equals(Path.GetFullPath(box.DbPath), box.MemoryPort.DatabasePath, StringComparison.Ordinal),
            "ILearningMemoryStore.DatabasePath 指向显式传入的库文件");

        // 旧表仍然存在（迁移只新增，不改动）。
        foreach (var legacy in new[] { "words", "review_logs", "review_snapshots", "word_archives", "app_settings" })
            Program.Check(TableExists(box.DbPath, legacy), $"迁移未破坏旧表 {legacy}");
    }

    // =====================================================================
    // 2. 旧库就地升级（v1 → v2）
    // =====================================================================
    private static void LegacyV1UpgradesInPlace()
    {
        using var box = new Sandbox();

        box.ArchivePort.AddWord("legacy-alpha", "ˈælfə", "阿尔法", "first");
        box.ArchivePort.AddWord("legacy-beta", "ˈbiːtə", "贝塔", "second");
        Program.Check(box.Service.GetAllWords().Count == 2, "旧库预置 2 条 words 记录");

        box.Close();

        var wordsBefore = Dump(box.DbPath, "words", WordsColumns);
        var logsBefore = Dump(box.DbPath, "review_logs", ReviewLogsColumns);
        var archivesBefore = Dump(box.DbPath, "word_archives", WordArchivesColumns);
        Program.Check(wordsBefore.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2,
            "快照含 2 行 words");
        Program.Check(logsBefore.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2,
            "快照含 2 行 review_logs");
        Program.Check(archivesBefore.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2,
            "快照含 2 行 word_archives");

        // 手工把库退回 v1：删掉 version=2 的迁移记录，并 DROP 掉 9 张长期记忆表。
        Exec(box.DbPath, "DELETE FROM schema_migrations WHERE version = 2;");
        foreach (var table in MemoryTables) Exec(box.DbPath, $"DROP TABLE IF EXISTS {table};");

        Program.Check(QueryInts(box.DbPath, "SELECT version FROM schema_migrations ORDER BY version").SequenceEqual([1]),
            "退回后 schema_migrations 只剩 1");
        Program.Check(!TableExists(box.DbPath, "fsrs_cards"), "退回后 fsrs_cards 不存在");
        Program.Check(!IndexExists(box.DbPath, "ux_canonical_active"), "退回后 ux_canonical_active 不存在");

        // 用全新 VocabularyService 指向同一路径。
        box.Open();

        foreach (var table in MemoryTables)
            Program.Check(TableExists(box.DbPath, table), $"重新打开后迁移补齐表 {table}");
        Program.Check(QueryInts(box.DbPath, "SELECT version FROM schema_migrations ORDER BY version").SequenceEqual([1, 2]),
            "重新打开后 schema_migrations 补齐到 1 与 2");
        Program.Check(IndexExists(box.DbPath, "ux_canonical_active"), "重新打开后 ux_canonical_active 重建");

        Program.Check(Dump(box.DbPath, "words", WordsColumns) == wordsBefore, "升级后 words 逐行不变");
        Program.Check(Dump(box.DbPath, "review_logs", ReviewLogsColumns) == logsBefore, "升级后 review_logs 逐行不变");
        Program.Check(Dump(box.DbPath, "word_archives", WordArchivesColumns) == archivesBefore,
            "升级后 word_archives 逐行不变");
        Program.Check(box.Service.GetAllWords().Count == 2, "升级后仍能读出 2 条词");
    }

    // =====================================================================
    // 3. AppendEvent 幂等
    // =====================================================================
    private static void AppendEventIsIdempotent()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        var wordKey = WordKey.Archive("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa").Key;
        var first = NewEvent("evt-dup", "sess-evt", wordKey, "pres-evt", Anchor, StudyRating.Known);

        store.AppendEvent(first);
        store.AppendEvent(first);

        var loaded = store.LoadEvents("sess-evt");
        Program.Check(loaded.Count == 1, $"同一 EventId 追加两次后只有 1 条（实际 {loaded.Count}）");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM learning_events WHERE event_id = 'evt-dup'") == 1,
            "learning_events 里 evt-dup 只有 1 行");

        var second = NewEvent("evt-other", "sess-evt", wordKey, "pres-evt-2", Anchor.AddMinutes(1), StudyRating.Forgot);
        store.AppendEvent(second);
        Program.Check(store.LoadEvents("sess-evt").Count == 2, "不同 EventId 正常追加");

        var round = store.LoadEvents("sess-evt");
        Program.Check(round[0].EventId == "evt-dup" && round[1].EventId == "evt-other", "LoadEvents 按 occurred_at_utc 升序");
        Program.Check(round[0].Response == StudyRating.Known && round[1].Response == StudyRating.Forgot,
            "事件的枚举判断往返一致");
        Program.Check(store.LoadEventsForWord(wordKey).Count == 2, "LoadEventsForWord 命中同一 word_key 的 2 条");
        Program.Check(store.LoadEvents("sess-none").Count == 0, "未知 session 返回空列表");
    }

    // =====================================================================
    // 4. 会话往返 + upsert
    // =====================================================================
    private static void SessionRoundTripAndUpsert()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        var session = new LearningSession
        {
            SessionId = "sess-round",
            StartedAtUtc = Anchor,
            EndedAtUtc = Anchor.AddMinutes(42),
            Mode = StudyMode.FirstLearn,
            PrimarySource = WordSource.Ielts,
            PlannedWordCount = 17,
            PlanId = "plan-9",
        };
        store.UpsertSession(session);

        var got = store.GetSession("sess-round");
        Program.Check(got is not null, "GetSession 能取回刚写入的会话");
        Program.Check(got!.SessionId == "sess-round", "SessionId 往返一致");
        Program.Check(got.StartedAtUtc == Anchor, "StartedAtUtc 往返一致");
        Program.Check(got.EndedAtUtc == Anchor.AddMinutes(42), "EndedAtUtc 往返一致");
        Program.Check(got.Mode == StudyMode.FirstLearn, "Mode 往返一致");
        Program.Check(got.PrimarySource == WordSource.Ielts, "PrimarySource 往返一致");
        Program.Check(got.PlannedWordCount == 17, "PlannedWordCount 往返一致");
        Program.Check(got.PlanId == "plan-9", "PlanId 往返一致");

        // EndedAtUtc 为空同样往返为 null。
        store.UpsertSession(new LearningSession
        {
            SessionId = "sess-open",
            StartedAtUtc = Anchor,
            EndedAtUtc = null,
            Mode = StudyMode.Review,
            PrimarySource = WordSource.Form,
            PlannedWordCount = 0,
            PlanId = "",
        });
        var open = store.GetSession("sess-open");
        Program.Check(open is not null && open.EndedAtUtc is null, "EndedAtUtc=null 往返仍是 null");
        Program.Check(open!.PlanId == "", "空 PlanId 往返为空串");

        // 同一 id 再 upsert：不产生第二行，且字段被更新。
        session.EndedAtUtc = Anchor.AddMinutes(90);
        session.PlannedWordCount = 20;
        session.PlanId = "";
        session.Mode = StudyMode.Review;
        store.UpsertSession(session);

        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM learning_sessions WHERE session_id = 'sess-round'") == 1,
            "重复 UpsertSession 同一 id 不产生第二行");
        var updated = store.GetSession("sess-round");
        Program.Check(updated!.EndedAtUtc == Anchor.AddMinutes(90), "重复 upsert 后 EndedAtUtc 已更新");
        Program.Check(updated.PlannedWordCount == 20, "重复 upsert 后 PlannedWordCount 已更新");
        Program.Check(updated.PlanId == "", "重复 upsert 后 PlanId 已更新");
        Program.Check(updated.Mode == StudyMode.Review, "重复 upsert 后 Mode 已更新");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM learning_sessions") == 2, "会话表共 2 行");

        Program.Check(store.GetSession("sess-missing") is null, "未知 session 返回 null");
    }

    // =====================================================================
    // 5. 定稿幂等
    // =====================================================================
    private static void CommitWordSessionIsIdempotent()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        var wordKey = WordKey.Archive("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb").Key;
        var cardAfter = NewCard(wordKey, 5.5, 12.25, 3, 1, FsrsState.Review,
            Anchor, Anchor.AddDays(4), StudyRating.Known);

        var first = store.CommitWordSession(BuildCommit(wordKey, "sess-commit", Anchor, cardAfter: cardAfter));
        Program.Check(first.Applied, "首次 CommitWordSession Applied=true");
        Program.Check(!first.AlreadyApplied, "首次 CommitWordSession AlreadyApplied=false");
        Program.Check(first.CanonicalId == CanonicalReview.BuildId(wordKey, "sess-commit"),
            "返回的 CanonicalId 是确定性幂等键");

        var cardAfterFirst = store.GetCard(wordKey);
        Program.Check(cardAfterFirst is not null, "首次定稿建出卡片");

        // 第二次用**不同的载荷**提交同一 canonical：必须完全不入库。
        var different = NewCard(wordKey, 9.0, 400.0, 99, 42, FsrsState.Relearning,
            Anchor.AddDays(1), Anchor.AddDays(60), StudyRating.Forgot);
        var second = store.CommitWordSession(BuildCommit(wordKey, "sess-commit", Anchor.AddHours(3), cardAfter: different));

        Program.Check(!second.Applied, "第二次 CommitWordSession Applied=false");
        Program.Check(second.AlreadyApplied, "第二次 CommitWordSession AlreadyApplied=true");
        Program.Check(second.CanonicalId == first.CanonicalId, "两次返回同一 CanonicalId");

        var cardAfterSecond = store.GetCard(wordKey);
        Program.Check(cardAfterSecond!.Reps == cardAfterFirst!.Reps,
            $"第二次调用未改变 Reps（{cardAfterFirst.Reps} → {cardAfterSecond.Reps}）");
        Program.Check(cardAfterSecond.Stability == cardAfterFirst.Stability,
            $"第二次调用未改变 Stability（{cardAfterFirst.Stability:R} → {cardAfterSecond.Stability:R}）");
        Program.Check(cardAfterSecond.Difficulty == cardAfterFirst.Difficulty, "第二次调用未改变 Difficulty");
        Program.Check(cardAfterSecond.State == cardAfterFirst.State, "第二次调用未改变 State");
        Program.Check(cardAfterSecond.LastAppliedCanonicalSeq == cardAfterFirst.LastAppliedCanonicalSeq,
            "第二次调用未推进 last_applied_canonical_seq");

        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM canonical_reviews") == 1, "canonical_reviews 只有 1 行");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM word_session_summaries") == 1,
            "word_session_summaries 只有 1 行");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM scheduler_decisions") == 1, "scheduler_decisions 只有 1 行");
    }

    // =====================================================================
    // 6. 部分唯一索引真的生效 + 失败整单回滚
    // =====================================================================
    private static void PartialUniqueIndexRejectsSecondActiveCanonical()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        var wordKey = WordKey.Archive("cccccccc-3333-4333-8333-cccccccccccc").Key;
        var cardA = NewCard(wordKey, 4.0, 10.0, 1, 0, FsrsState.Review, Anchor, Anchor.AddDays(2), StudyRating.Known);
        var first = store.CommitWordSession(BuildCommit(wordKey, "sess-unique", Anchor, cardAfter: cardA));
        Program.Check(first.Applied, "第一次定稿成功");
        var snapshot = store.GetCard(wordKey)!;

        // 第二次：canonical_id 不同，但 (word_key, session_id) 相同且 invalidated 仍是 0。
        var cardB = NewCard(wordKey, 9.0, 99.5, 7, 3, FsrsState.Relearning,
            Anchor.AddDays(1), Anchor.AddDays(30), StudyRating.Forgot);
        var rejected = BuildCommit(
            wordKey, "sess-unique", Anchor.AddMinutes(30),
            cardAfter: cardB,
            canonicalId: "cr:manual:second-active",
            events: [NewEvent("evt-rejected", "sess-unique", wordKey, "pres-rejected", Anchor.AddMinutes(30), StudyRating.Forgot)]);

        Exception? caught = null;
        try { store.CommitWordSession(rejected); }
        catch (Exception ex) { caught = ex; }

        Program.Check(caught is not null, "第二次提交同一 (word_key, session_id) 的活跃 canonical 抛异常");
        Program.Check(caught is SqliteException, $"异常是 SqliteException（实际 {caught?.GetType().Name}）");
        // 真实行为：SQLite 对"列上的"唯一约束报 `UNIQUE constraint failed: <表>.<列>, <表>.<列>`。
        // canonical_reviews 上只有 ux_canonical_active 约束 (word_key, session_id)，因此这两个列名
        // 足以证明命中的就是那个部分唯一索引（若 SQLite 改报索引名，这里也一并接受）。
        var sqlite = (SqliteException)caught!;
        Program.Check(sqlite.SqliteErrorCode == 19, $"SQLite 错误码为 19 (CONSTRAINT)，实际 {sqlite.SqliteErrorCode}");
        Program.Check(
            sqlite.Message.Contains("ux_canonical_active", StringComparison.OrdinalIgnoreCase)
            || (sqlite.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                && sqlite.Message.Contains("word_key", StringComparison.OrdinalIgnoreCase)
                && sqlite.Message.Contains("session_id", StringComparison.OrdinalIgnoreCase)),
            $"异常来自 (word_key, session_id) 的部分唯一索引（实际：{sqlite.Message}）");

        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM canonical_reviews") == 1,
            "失败提交没有留下第二条 canonical");
        Program.Check(store.LoadEvents("sess-unique").Count == 0,
            "失败提交连带写在同一事务里的事件也被回滚");

        var after = store.GetCard(wordKey)!;
        Program.Check(after.Difficulty == snapshot.Difficulty
                      && after.Stability == snapshot.Stability
                      && after.Reps == snapshot.Reps
                      && after.Lapses == snapshot.Lapses
                      && after.State == snapshot.State
                      && after.LastReviewAtUtc == snapshot.LastReviewAtUtc
                      && after.NextReviewAtUtc == snapshot.NextReviewAtUtc
                      && after.LastCanonicalRating == snapshot.LastCanonicalRating
                      && after.LastAppliedCanonicalSeq == snapshot.LastAppliedCanonicalSeq,
            "事务回滚：fsrs_cards 完全没有被第二次提交更新");
    }

    // =====================================================================
    // 7. 撤销的 pre-state 回放
    // =====================================================================
    private static void InvalidateReplaysPreStateWithoutDeletingEvents()
    {
        // (a) 原本没有卡 → 撤销后卡片被删除，事件不删。
        using (var box = new Sandbox())
        {
            var store = box.MemoryPort;
            var wordKey = WordKey.Archive("dddddddd-4444-4444-8444-dddddddddddd").Key;
            var canonicalId = CanonicalReview.BuildId(wordKey, "sess-inv-a");
            var card = NewCard(wordKey, 6.0, 15.0, 2, 0, FsrsState.Review,
                Anchor, Anchor.AddDays(5), StudyRating.Known);
            var evt = NewEvent("evt-inv-a", "sess-inv-a", wordKey, "pres-inv-a", Anchor, StudyRating.Known);

            store.CommitWordSession(BuildCommit(wordKey, "sess-inv-a", Anchor, cardAfter: card, events: [evt]));
            Program.Check(store.HasCard(wordKey), "(a) 定稿后存在卡片");
            var eventsBefore = store.LoadEvents("sess-inv-a").Count;
            Program.Check(eventsBefore == 1, "(a) 定稿写入 1 条事件");

            Program.Check(store.InvalidateCanonical(canonicalId, Anchor.AddHours(1), "用户撤销"), "(a) InvalidateCanonical 返回 true");
            Program.Check(store.GetCard(wordKey) is null, "(a) pre_state_existed=0 → 撤销后卡片被删除");
            Program.Check(!store.HasCard(wordKey), "(a) HasCard 同步为 false");
            Program.Check(store.LoadEvents("sess-inv-a").Count == eventsBefore, "(a) 撤销不删除任何 learning_events");
            Program.Check(ScalarInt(box.DbPath, $"SELECT invalidated FROM canonical_reviews WHERE canonical_id = '{canonicalId}'") == 1,
                "(a) canonical_reviews 该行 invalidated=1");
            Program.Check(ScalarText(box.DbPath, $"SELECT invalidation_reason FROM canonical_reviews WHERE canonical_id = '{canonicalId}'") == "用户撤销",
                "(a) 失效原因落库");
            Program.Check(ScalarInt(box.DbPath, $"SELECT COUNT(*) FROM canonical_reviews WHERE canonical_id = '{canonicalId}'") == 1,
                "(a) 撤销不删除 canonical 行");

            // 行为证据（不依赖直连 SQL）：失效后同一 canonical 重新定稿会走 redo 分支，返回 Applied 而非 AlreadyApplied。
            var redo = store.CommitWordSession(BuildCommit(wordKey, "sess-inv-a", Anchor.AddHours(2), cardAfter: card, events: [evt]));
            Program.Check(redo.Applied && !redo.AlreadyApplied,
                "(a) 失效后重新定稿走 redo（间接证明 invalidated 曾被置 1）");
            Program.Check(store.GetCard(wordKey) is not null, "(a) redo 后卡片重新出现");
        }

        // (b) 原本已有卡 → 撤销第二个 canonical 后精确回到第二个之前的卡状态。
        using (var box = new Sandbox())
        {
            var store = box.MemoryPort;
            var wordKey = WordKey.Archive("eeeeeeee-5555-4555-8555-eeeeeeeeeeee").Key;

            var card1 = NewCard(wordKey, 5.25, 30.5, 4, 1, FsrsState.Review,
                Anchor, Anchor.AddDays(6), StudyRating.Unsure);
            store.CommitWordSession(BuildCommit(wordKey, "sess-inv-b1", Anchor, cardAfter: card1));
            var before = store.GetCard(wordKey)!;
            Program.Check(before.Reps == 4 && before.Lapses == 1, "(b) 第一次定稿后的卡状态符合预期");

            var card2 = NewCard(wordKey, 9.75, 90.125, 9, 4, FsrsState.Relearning,
                Anchor.AddDays(1), Anchor.AddDays(20), StudyRating.Forgot);
            var canonical2 = CanonicalReview.BuildId(wordKey, "sess-inv-b2");
            store.CommitWordSession(BuildCommit(wordKey, "sess-inv-b2", Anchor.AddDays(1),
                cardAfter: card2, preState: FsrsPreState.From(before)));

            var mid = store.GetCard(wordKey)!;
            Program.Check(mid.Difficulty == card2.Difficulty && mid.Stability == card2.Stability
                          && mid.Reps == card2.Reps && mid.Lapses == card2.Lapses && mid.State == card2.State,
                "(b) 第二个 canonical 确实改动了卡片");

            var eventsBeforeB = store.LoadEvents("sess-inv-b2").Count;
            Program.Check(store.InvalidateCanonical(canonical2, Anchor.AddDays(1).AddHours(1), "撤销第二次"), "(b) 撤销返回 true");

            var replayed = store.GetCard(wordKey);
            Program.Check(replayed is not null, "(b) 撤销后卡片仍在（pre_state_existed=1）");
            Program.Check(replayed!.Difficulty == before.Difficulty, $"(b) Difficulty 精确回放（{before.Difficulty:R} → {replayed.Difficulty:R}）");
            Program.Check(replayed.Stability == before.Stability, $"(b) Stability 精确回放（{before.Stability:R} → {replayed.Stability:R}）");
            Program.Check(replayed.Reps == before.Reps, $"(b) Reps 精确回放（{before.Reps} → {replayed.Reps}）");
            Program.Check(replayed.Lapses == before.Lapses, $"(b) Lapses 精确回放（{before.Lapses} → {replayed.Lapses}）");
            Program.Check(replayed.State == before.State, $"(b) State 精确回放（{before.State} → {replayed.State}）");
            Program.Check(replayed.LastReviewAtUtc == before.LastReviewAtUtc, "(b) LastReviewAtUtc 精确回放");
            Program.Check(replayed.NextReviewAtUtc == before.NextReviewAtUtc, "(b) NextReviewAtUtc 精确回放");
            Program.Check(replayed.LastCanonicalRating == before.LastCanonicalRating, "(b) LastCanonicalRating 精确回放");
            Program.Check(store.LoadEvents("sess-inv-b2").Count == eventsBeforeB, "(b) 撤销不删除事件");
            Program.Check(ScalarInt(box.DbPath, $"SELECT invalidated FROM canonical_reviews WHERE canonical_id = '{canonical2}'") == 1,
                "(b) 第二个 canonical 标记为失效");
            Program.Check(ScalarInt(box.DbPath,
                    $"SELECT last_applied_canonical_seq FROM fsrs_cards WHERE word_key = '{wordKey}'")
                == ScalarInt(box.DbPath, "SELECT MAX(rowid) FROM canonical_reviews WHERE invalidated = 0"),
                "(b) 幂等水位回退到仍有效的前一条 canonical");

            // (c) 重复撤销返回 false。
            Program.Check(!store.InvalidateCanonical(canonical2, Anchor.AddDays(1).AddHours(2), "再来一次"),
                "(c) 对已失效的 canonical 重复 InvalidateCanonical 返回 false");
            Program.Check(!store.InvalidateCanonical(CanonicalReview.BuildId(wordKey, "sess-never"), Anchor, "不存在"),
                "(c) 对不存在的 canonical 返回 false");
            var afterRepeat = store.GetCard(wordKey)!;
            Program.Check(afterRepeat.Reps == before.Reps && afterRepeat.Stability == before.Stability,
                "(c) 重复撤销无副作用");
        }
    }

    // =====================================================================
    // 8. 到期队列
    // =====================================================================
    private static void DueQueueFiltersAndOrders()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        var now = DateTime.UtcNow;
        var yesterday = now.AddDays(-1).AddMinutes(-3);
        var tomorrow = now.AddDays(1).AddMinutes(7);

        var k1 = WordKey.Archive("10000000-0000-4000-8000-000000000001").Key;
        var k2 = WordKey.Archive("10000000-0000-4000-8000-000000000002").Key;
        var k3 = WordKey.Archive("10000000-0000-4000-8000-000000000003").Key;

        store.CommitWordSession(BuildCommit(k1, "sess-due-1", Anchor,
            cardAfter: NewCard(k1, 3.5, 8.0, 1, 0, FsrsState.Review, yesterday.AddDays(-1), yesterday, StudyRating.Known)));
        store.CommitWordSession(BuildCommit(k2, "sess-due-2", Anchor,
            cardAfter: NewCard(k2, 5.0, 20.5, 2, 0, FsrsState.Review, now.AddDays(-2), now, StudyRating.Unsure)));
        store.CommitWordSession(BuildCommit(k3, "sess-due-3", Anchor,
            cardAfter: NewCard(k3, 7.0, 55.0, 3, 1, FsrsState.Review, now, tomorrow, StudyRating.Forgot)));

        var due = store.QueryDue(now, 10);
        Program.Check(due.Count == 2, $"QueryDue 只返回昨天与现在两条（实际 {due.Count}）");
        Program.Check(due[0].WordKey == k1 && due[1].WordKey == k2, "到期队列按 next_review_at_utc 升序");
        Program.Check(due[0].NextReviewAtUtc == yesterday && due[1].NextReviewAtUtc == now,
            "到期项的 NextReviewAtUtc 精确往返");
        Program.Check(due[0].Difficulty == 3.5 && due[0].Stability == 8.0, "到期项携带 Difficulty/Stability");
        Program.Check(due.All(d => d.NextReviewAtUtc <= now), "到期项都不晚于 now");

        Program.Check(store.CountDue(now) == 2, "CountDue 与 QueryDue 同口径");

        var limited = store.QueryDue(now, 1);
        Program.Check(limited.Count == 1, $"QueryDue(now, 1) 只返回 1 条（实际 {limited.Count}）");
        Program.Check(limited[0].WordKey == k1, "limit 生效且取最早的一条");
        Program.Check(store.QueryDue(now, 0).Count == 0, "limit<=0 返回空列表");
        Program.Check(store.CountDue(yesterday.AddMinutes(-1)) == 0, "now 早于全部到期时刻时数量为 0");
        Program.Check(store.QueryDue(tomorrow.AddMinutes(1), 10).Count == 3, "now 晚于全部到期时刻时三条都到期");
    }

    // =====================================================================
    // 9. Context 快照与标签
    // =====================================================================
    private static void ContextSnapshotsAndLabels()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        var wordKey = WordKey.Archive("20000000-0000-4000-8000-000000000001").Key;
        var captured1 = new DateTime(Anchor.Ticks - TimeSpan.TicksPerDay + 1234567, DateTimeKind.Utc);
        var snap1 = new ContextFeatureSnapshot
        {
            SnapshotId = "snap-1",
            WordKey = wordKey,
            SessionId = "sess-ctx",
            PresentationId = "pres-ctx-1",
            CapturedAtUtc = captured1,
            FsrsDifficultyAtCapture = 5.125,
            FsrsStabilityAtCapture = 21.75,
            FsrsRetrievabilityAtCapture = 0.8125,
            FeatureSchemaVersion = "ctx-feat-1",
            ModelVersion = "ctx-model-0",
            FeaturesJson = "[1.5,2.25,-3]",
            MissingFlagsJson = "[\"fsrs_history\"]",
        };
        store.SaveContextSnapshot(snap1);

        var got = store.GetContextSnapshot("snap-1");
        Program.Check(got is not null, "SaveContextSnapshot 后能取回");
        Program.Check(got!.SnapshotId == "snap-1" && got.WordKey == wordKey && got.SessionId == "sess-ctx"
                      && got.PresentationId == "pres-ctx-1", "快照标识字段往返一致");
        Program.Check(got.CapturedAtUtc == captured1 && got.CapturedAtUtc.Kind == DateTimeKind.Utc,
            "CapturedAtUtc UTC 往返一致（含亚毫秒）");
        Program.Check(got.FeaturesJson == "[1.5,2.25,-3]", "FeaturesJson 往返一致");
        Program.Check(got.MissingFlagsJson == "[\"fsrs_history\"]", "MissingFlagsJson 往返一致");
        Program.Check(got.FsrsDifficultyAtCapture == 5.125 && got.FsrsStabilityAtCapture == 21.75
                      && got.FsrsRetrievabilityAtCapture == 0.8125, "FSRS 捕获值往返一致");
        Program.Check(got.FeatureSchemaVersion == "ctx-feat-1" && got.ModelVersion == "ctx-model-0", "版本戳往返一致");
        Program.Check(got.Label is null && got.ConfidentRecall is null && got.LabeledAtUtc is null, "未打标签时三项均为 null");

        Program.Check(store.GetSnapshotByPresentation("pres-ctx-1")?.SnapshotId == "snap-1", "GetSnapshotByPresentation 命中");
        Program.Check(store.GetSnapshotByPresentation("pres-none") is null, "未知 presentation 返回 null");
        Program.Check(store.GetContextSnapshot("snap-none") is null, "未知 snapshotId 返回 null");

        // 三个快照：过去 / 更过去 / 未来，用于标签与 cutoff 过滤。
        var capturedPast = new DateTime(Anchor.AddDays(-10).Ticks, DateTimeKind.Utc);
        var capturedFuture = new DateTime(Anchor.AddDays(10).Ticks, DateTimeKind.Utc);
        store.SaveContextSnapshot(NewSnapshot("snap-2", wordKey, "sess-ctx-2", "pres-ctx-2", capturedPast, "[0.5,1.5]", "[]"));
        store.SaveContextSnapshot(NewSnapshot("snap-3", wordKey, "sess-ctx-3", "pres-ctx-3", capturedFuture, "[9,9,9]", "[\"latency\"]"));

        store.LabelSnapshots(
        [
            new ContextLabel("snap-1", StudyRating.Known, true),
            new ContextLabel("snap-2", StudyRating.Forgot, false),
            new ContextLabel("snap-3", StudyRating.Unsure, true),
        ], Anchor);

        SeedTrainingAnchor(store, snap1, StudyRating.Known, Anchor);
        SeedTrainingAnchor(store, store.GetContextSnapshot("snap-2")!, StudyRating.Forgot, Anchor);
        SeedTrainingAnchor(store, store.GetContextSnapshot("snap-3")!, StudyRating.Unsure, capturedFuture.AddSeconds(1));
        store.LabelSnapshots([new ContextLabel("snap-3", StudyRating.Unsure, true)], capturedFuture.AddSeconds(1));
        var samples = store.LoadLabeledSamples();
        Program.Check(samples.Count == 3, $"LoadLabeledSamples 返回 3 个样本（实际 {samples.Count}）");
        Program.Check(samples[0].SnapshotId == "snap-2" && samples[1].SnapshotId == "snap-1" && samples[2].SnapshotId == "snap-3",
            "样本按 captured_at_utc 升序");

        var known = samples.Single(s => s.SnapshotId == "snap-1");
        Program.Check(known.Label == 1, "Known → 标签 1");
        Program.Check(known.ConfidentRecall, "Known 的 ConfidentRecall=true");
        Program.Check(known.WordKey == wordKey && known.CapturedAtUtc == captured1, "样本携带 word_key 与捕获时刻");
        Program.Check(known.Features.SequenceEqual([1.5, 2.25, -3]), "Features 反序列化正确");
        Program.Check(known.MissingFlagsJson == "[\"fsrs_history\"]", "样本携带 MissingFlagsJson");

        var forgot = samples.Single(s => s.SnapshotId == "snap-2");
        Program.Check(forgot.Label == 0, "Forgot → 标签 0");
        Program.Check(!forgot.ConfidentRecall, "Forgot 的 ConfidentRecall=false");

        var unsure = samples.Single(s => s.SnapshotId == "snap-3");
        Program.Check(unsure.Label == 1, "Unsure → 标签 1（二分类口径）");
        Program.Check(unsure.ConfidentRecall, "Unsure 的 ConfidentRecall 独立保留");

        Program.Check(store.LoadLabeledSamples(Anchor).Count == 0, "标签/定稿等于cutoff必须排除（严格时间上界）");
        var cutoff = store.LoadLabeledSamples(Anchor.AddTicks(1));
        Program.Check(cutoff.Count == 2, $"cutoff 过滤掉未来样本（实际 {cutoff.Count}）");
        Program.Check(cutoff.All(s => s.SnapshotId != "snap-3"), "captured_at_utc 晚于 cutoff 的样本被排除");
        Program.Check(cutoff[0].SnapshotId == "snap-2" && cutoff[1].SnapshotId == "snap-1", "cutoff 结果保持升序");
        Program.Check(store.LoadLabeledSamples(capturedPast).Count == 0, "cutoff 早于全部样本时返回 0 条");

        // 已落库快照再 upsert：特征被更新，但已回填的标签不被覆盖。
        var relabeled = NewSnapshot("snap-1", wordKey, "sess-ctx", "pres-ctx-1", captured1, "[7,7]", "[\"new\"]");
        store.SaveContextSnapshot(relabeled);
        var afterResave = store.GetContextSnapshot("snap-1")!;
        Program.Check(afterResave.FeaturesJson == "[7,7]", "重复 SaveContextSnapshot 会更新特征");
        Program.Check(afterResave.Label == StudyRating.Known && afterResave.ConfidentRecall == true
                      && afterResave.LabeledAtUtc is not null, "重复 SaveContextSnapshot 不覆盖已回填的标签");

        Program.Check(store.LoadLabeledSamples().Count == 3, "重存快照不产生第二个训练样本");
    }

    // =====================================================================
    // 10. outbox
    // =====================================================================
    private static void MutationOutbox()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        store.EnqueueMutations(
        [
            new PendingMutation("json.settings", "{\"a\":1}"),
            new PendingMutation("json.quotes", "{\"b\":2}"),
        ]);

        var pending = store.LoadPendingMutations();
        Program.Check(pending.Count == 2, $"EnqueueMutations 两条后待办 2 条（实际 {pending.Count}）");
        Program.Check(pending[0].Attempts == 0 && pending[1].Attempts == 0, "新条目 Attempts==0");
        Program.Check(pending[0].Kind == "json.settings" && pending[0].PayloadJson == "{\"a\":1}", "第 1 条载荷往返一致");
        Program.Check(pending[1].Kind == "json.quotes" && pending[1].PayloadJson == "{\"b\":2}", "第 2 条载荷往返一致");
        Program.Check(pending[0].Id < pending[1].Id, "待办按 id 升序");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM mutation_outbox") == 2, "mutation_outbox 共 2 行");

        store.MarkMutationApplied(pending[0].Id, Anchor);
        var left = store.LoadPendingMutations();
        Program.Check(left.Count == 1, $"MarkMutationApplied 后待办剩 1 条（实际 {left.Count}）");
        Program.Check(left[0].Id == pending[1].Id, "剩下的是未被标记的那条");
        Program.Check(ScalarText(box.DbPath, $"SELECT applied_at_utc FROM mutation_outbox WHERE id = {pending[0].Id}")
                      == Anchor.ToString("O", CultureInfo.InvariantCulture), "applied_at_utc 以 UTC ISO-8601 落库");

        store.RecordMutationFailure(left[0].Id, "boom");
        var afterFailure = store.LoadPendingMutations();
        Program.Check(afterFailure.Count == 1, "失败记录仍留在待办列表里");
        Program.Check(afterFailure[0].Attempts == 1, $"RecordMutationFailure 后 Attempts==1（实际 {afterFailure[0].Attempts}）");
        Program.Check(ScalarText(box.DbPath, $"SELECT last_error FROM mutation_outbox WHERE id = {left[0].Id}") == "boom",
            "last_error 落库");

        store.RecordMutationFailure(afterFailure[0].Id, "boom-again");
        Program.Check(store.LoadPendingMutations()[0].Attempts == 2, "再次失败 Attempts 累加");

        store.MarkMutationApplied(afterFailure[0].Id, Anchor.AddHours(1));
        Program.Check(store.LoadPendingMutations().Count == 0, "全部标记后待办为空");

        store.EnqueueMutations([new PendingMutation("json.word", "{}")]);
        Program.Check(store.LoadPendingMutations().Count == 1, "再次入队正常");
        store.EnqueueMutations([]);
        Program.Check(store.LoadPendingMutations().Count == 1, "空列表入队是 no-op");
    }

    // =====================================================================
    // 11. 调度决策日志
    // =====================================================================
    private static void SchedulerDecisionLog()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        var wordKey = WordKey.Archive("30000000-0000-4000-8000-000000000001").Key;
        var decision = new SchedulerDecision
        {
            DecisionId = "dec-1",
            WordKey = wordKey,
            CanonicalId = CanonicalReview.BuildId(wordKey, "sess-dec"),
            TimestampUtc = Anchor,
            BaselineIntervalDays = 3.5,
            BaselineRetrievability = 0.87,
            BaselineDifficulty = 5.125,
            BaselineStability = 21.75,
            ContextMode = ContextMode.Shadow,
            ContextDelta = -0.40625,
            ContextCandidateIntervalDays = 12.5,
            FinalIntervalDays = 9.75,
            FinalDueAtUtc = Anchor.AddDays(9).AddHours(18),
            DesiredRetention = 0.9,
            FsrsAlgorithmVersion = "fsrs-6",
            FsrsLibraryVersion = "lib-1.2.3",
            FsrsParameterVersion = "params-7",
            AggregationPolicyVersion = AggregationPolicy.Version,
            TrajectorySchemaVersion = "traj-1",
            ContextFeatureSchemaVersion = "ctx-feat-1",
            ContextModelVersion = "ctx-model-3",
            SchedulerVersion = "sched-1",
        };
        store.SaveSchedulerDecision(decision);

        var recent = store.LoadRecentDecisions(wordKey, 10);
        Program.Check(recent.Count == 1, $"SaveSchedulerDecision 后能取回 1 条（实际 {recent.Count}）");
        var got = recent[0];
        Program.Check(got.DecisionId == "dec-1" && got.WordKey == wordKey && got.CanonicalId == decision.CanonicalId,
            "决策标识字段往返一致");
        Program.Check(got.TimestampUtc == Anchor, "TimestampUtc 往返一致");
        Program.Check(got.FinalIntervalDays == 9.75, $"FinalIntervalDays 往返一致（{got.FinalIntervalDays:R}）");
        Program.Check(got.FinalDueAtUtc == Anchor.AddDays(9).AddHours(18), "FinalDueAtUtc 往返一致");
        Program.Check(got.BaselineIntervalDays == 3.5 && got.BaselineRetrievability == 0.87
                      && got.BaselineDifficulty == 5.125 && got.BaselineStability == 21.75, "baseline 四项往返一致");
        Program.Check(got.ContextMode == ContextMode.Shadow, "ContextMode 往返一致");
        Program.Check(got.ContextDelta == -0.40625 && got.ContextCandidateIntervalDays == 12.5, "ContextDelta 往返一致");
        Program.Check(got.DesiredRetention == 0.9, "DesiredRetention 往返一致");
        Program.Check(got.FsrsAlgorithmVersion == "fsrs-6" && got.FsrsLibraryVersion == "lib-1.2.3"
                      && got.FsrsParameterVersion == "params-7", "FSRS 三版本戳往返一致");
        Program.Check(got.AggregationPolicyVersion == AggregationPolicy.Version
                      && got.TrajectorySchemaVersion == "traj-1"
                      && got.ContextFeatureSchemaVersion == "ctx-feat-1"
                      && got.ContextModelVersion == "ctx-model-3"
                      && got.SchedulerVersion == "sched-1", "策略/轨迹/Context/调度版本戳往返一致");

        // 可空字段为 null 时同样往返。
        var nulls = new SchedulerDecision
        {
            DecisionId = "dec-nulls",
            WordKey = wordKey,
            CanonicalId = "cr:nulls",
            TimestampUtc = Anchor.AddMinutes(5),
            FinalDueAtUtc = Anchor.AddDays(1),
            ContextDelta = null,
            ContextCandidateIntervalDays = null,
        };
        store.SaveSchedulerDecision(nulls);
        var gotNulls = store.LoadRecentDecisions(wordKey, 10).Single(d => d.DecisionId == "dec-nulls");
        Program.Check(gotNulls.ContextDelta is null && gotNulls.ContextCandidateIntervalDays is null,
            "可空决策字段往返为 null");

        // 排序与 limit。
        var later = new SchedulerDecision
        {
            DecisionId = "dec-3",
            WordKey = wordKey,
            CanonicalId = "cr:later",
            TimestampUtc = Anchor.AddHours(1),
            FinalDueAtUtc = Anchor.AddDays(20),
        };
        store.SaveSchedulerDecision(later);
        Program.Check(store.LoadRecentDecisions(wordKey, 1).Single().DecisionId == "dec-3", "limit=1 取最新一条");
        Program.Check(store.LoadRecentDecisions(wordKey, 0).Count == 0, "limit<=0 返回空列表");
        Program.Check(store.LoadRecentDecisions("archive:unknown", 10).Count == 0, "未知 word_key 返回空列表");

        store.SaveSchedulerDecision(decision);
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM scheduler_decisions WHERE decision_id = 'dec-1'") == 1,
            "重复 DecisionId 被 ON CONFLICT 忽略");
        Program.Check(store.LoadRecentDecisions(wordKey, 10).Count == 3, "决策日志共 3 条");
    }

    // =====================================================================
    // 12. words 触发器不被内存层触发（关键回归）
    // =====================================================================
    private static void MemoryLayerDoesNotTouchWords()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        box.ArchivePort.AddWord("trigger-probe", "ˈtrɪɡə", "触发器探针", "regression");

        var item = box.Service.GetAllWords().Single(w => w.Word == "trigger-probe");
        var wordId = item.Id;
        var uuid = item.Archive.Uuid;
        var revisionBefore = item.Archive.Revision;
        var updatedAtBefore = item.Archive.UpdatedAtUtc;
        Program.Check(revisionBefore == 1, $"AddWord 后 word_archives.revision 为 1（实际 {revisionBefore}）");

        var wordsBefore = Dump(box.DbPath, "words", WordsColumns);
        var archivesBefore = Dump(box.DbPath, "word_archives", WordArchivesColumns);

        var wordKey = WordKey.Archive(uuid).Key;
        var card = NewCard(wordKey, 5.0, 18.0, 2, 0, FsrsState.Review, Anchor, Anchor.AddDays(4), StudyRating.Known);
        var canonicalId = CanonicalReview.BuildId(wordKey, "sess-trigger");
        var evt = NewEvent("evt-trigger", "sess-trigger", wordKey, "pres-trigger", Anchor, StudyRating.Known);
        var snapshot = NewSnapshot("snap-trigger", wordKey, "sess-trigger", "pres-trigger", Anchor, "[1,2,3]", "[]");

        store.AppendEvent(evt);
        store.CommitWordSession(BuildCommit(wordKey, "sess-trigger", Anchor, cardAfter: card,
            events: [], snapshot: snapshot, labels: [new ContextLabel("snap-trigger", StudyRating.Known, true)]));
        store.SaveContextSnapshot(NewSnapshot("snap-trigger-2", wordKey, "sess-trigger", "pres-trigger-2",
            Anchor.AddMinutes(1), "[4,5,6]", "[]"));
        store.SaveSchedulerDecision(new SchedulerDecision
        {
            DecisionId = "dec-trigger",
            WordKey = wordKey,
            CanonicalId = canonicalId,
            TimestampUtc = Anchor,
            FinalDueAtUtc = Anchor.AddDays(4),
        });
        store.InvalidateCanonical(canonicalId, Anchor.AddHours(1), "撤销以覆盖回放路径");

        var after = box.Service.GetAllWords().Single(w => w.Word == "trigger-probe");
        Program.Check(after.Archive.Revision == revisionBefore,
            $"完整内存层操作后 word_archives.revision 完全不变（{revisionBefore} → {after.Archive.Revision}）");
        Program.Check(after.Archive.UpdatedAtUtc == updatedAtBefore,
            "word_archives.updated_at_utc 未被 lexi_word_revision 触发器改写");
        Program.Check(Dump(box.DbPath, "words", WordsColumns) == wordsBefore, "words 表逐行完全不变");
        Program.Check(Dump(box.DbPath, "word_archives", WordArchivesColumns) == archivesBefore,
            "word_archives 表逐行完全不变");
        Program.Check(ScalarInt(box.DbPath, $"SELECT revision FROM word_archives WHERE word_id = {wordId}") == revisionBefore,
            "直连 SQL 复核 revision 不变");
        Program.Check(store.HasCard(wordKey) == false, "探针确实走完了撤销路径（卡片已被删除）");

        // 阳性对照：走一次旧写入路径（AddWord 命中已有词 → UPDATE words）必须让触发器自增 revision。
        // 这证明上面的"不变"不是因为触发器缺失或读错了列。
        box.ArchivePort.AddWord("trigger-probe", "", "", "");
        var afterLegacyWrite = box.Service.GetAllWords().Single(w => w.Word == "trigger-probe");
        Program.Check(afterLegacyWrite.Archive.Revision == revisionBefore + 1,
            $"阳性对照：旧写入路径确实会自增 revision（{revisionBefore} → {afterLegacyWrite.Archive.Revision}）");
        Program.Check(afterLegacyWrite.Archive.UpdatedAtUtc != updatedAtBefore,
            "阳性对照：旧写入路径确实会改写 updated_at_utc");
    }

    // =====================================================================
    // 13. UTC 时间往返
    // =====================================================================
    private static void UtcRoundTrip()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        var precise = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc).AddTicks(1234567);
        Program.Check(precise.Ticks % TimeSpan.TicksPerMillisecond != 0, "测试时刻确实含亚毫秒部分");

        store.UpsertSession(new LearningSession
        {
            SessionId = "sess-utc",
            StartedAtUtc = precise,
            EndedAtUtc = precise.AddTicks(7),
            Mode = StudyMode.Review,
            PrimarySource = WordSource.Archive,
            PlannedWordCount = 1,
            PlanId = "",
        });
        var session = store.GetSession("sess-utc")!;
        Program.Check(session.StartedAtUtc.Kind == DateTimeKind.Utc, "会话 StartedAtUtc 读回为 Utc");
        Program.Check(session.StartedAtUtc == precise, "会话 StartedAtUtc 精确到 tick");
        Program.Check(session.EndedAtUtc!.Value.Kind == DateTimeKind.Utc && session.EndedAtUtc.Value == precise.AddTicks(7),
            "会话 EndedAtUtc 精确到 tick");

        var wordKey = WordKey.Archive("40000000-0000-4000-8000-000000000001").Key;
        store.AppendEvent(NewEvent("evt-utc", "sess-utc", wordKey, "pres-utc", precise, StudyRating.Known));
        var evt = store.LoadEvents("sess-utc").Single();
        Program.Check(evt.OccurredAtUtc.Kind == DateTimeKind.Utc && evt.OccurredAtUtc == precise, "事件 OccurredAtUtc 精确往返");

        var snapshot = NewSnapshot("snap-utc", wordKey, "sess-utc", "pres-utc", precise, "[1]", "[]");
        store.SaveContextSnapshot(snapshot);
        store.LabelSnapshots([new ContextLabel("snap-utc", StudyRating.Known, true)], precise.AddTicks(11));

        var snap = store.GetContextSnapshot("snap-utc")!;
        Program.Check(snap.CapturedAtUtc.Kind == DateTimeKind.Utc && snap.CapturedAtUtc == precise, "快照 CapturedAtUtc 精确往返");
        Program.Check(snap.LabeledAtUtc!.Value.Kind == DateTimeKind.Utc && snap.LabeledAtUtc.Value == precise.AddTicks(11),
            "快照 LabeledAtUtc 精确往返");

        var card = NewCard(wordKey, 5.0, 20.0, 1, 0, FsrsState.Review, precise, precise.AddDays(3).AddTicks(13), StudyRating.Known);
        var utcCommit = BuildCommit(wordKey, "sess-utc", precise, cardAfter: card,
            snapshot: NewSnapshot("snap-utc-2", wordKey, "sess-utc", "pres-utc-2", precise, "[2]", "[]"));
        utcCommit.Canonical.SourcePresentationId = "pres-utc";
        store.CommitWordSession(utcCommit);
        var sample = store.LoadLabeledSamples().Single();
        Program.Check(sample.CapturedAtUtc.Kind == DateTimeKind.Utc && sample.CapturedAtUtc == precise,
            "有效canonical训练样本 CapturedAtUtc 精确往返");
        var stored = store.GetCard(wordKey)!;
        Program.Check(stored.LastReviewAtUtc!.Value.Kind == DateTimeKind.Utc && stored.LastReviewAtUtc.Value == precise,
            "卡片 LastReviewAtUtc 精确往返");
        Program.Check(stored.NextReviewAtUtc!.Value.Kind == DateTimeKind.Utc
                      && stored.NextReviewAtUtc.Value == precise.AddDays(3).AddTicks(13), "卡片 NextReviewAtUtc 精确往返");

        var due = store.QueryDue(precise.AddDays(4), 10).Single(d => d.WordKey == wordKey);
        Program.Check(due.NextReviewAtUtc.Kind == DateTimeKind.Utc
                      && due.NextReviewAtUtc == precise.AddDays(3).AddTicks(13), "到期项 NextReviewAtUtc 精确往返");

        var decision = new SchedulerDecision
        {
            DecisionId = "dec-utc",
            WordKey = wordKey,
            CanonicalId = "cr:utc",
            TimestampUtc = precise,
            FinalDueAtUtc = precise.AddTicks(17),
        };
        store.SaveSchedulerDecision(decision);
        var gotDecision = store.LoadRecentDecisions(wordKey, 10).Single(d => d.DecisionId == "dec-utc");
        Program.Check(gotDecision.TimestampUtc.Kind == DateTimeKind.Utc && gotDecision.TimestampUtc == precise,
            "决策 TimestampUtc 精确往返");
        Program.Check(gotDecision.FinalDueAtUtc.Kind == DateTimeKind.Utc && gotDecision.FinalDueAtUtc == precise.AddTicks(17),
            "决策 FinalDueAtUtc 精确往返");

        var model = new PersonalizationModelState
        {
            ModelVersion = "ctx-utc",
            TrainedAtUtc = precise,
            TrainCutoffUtc = precise.AddTicks(19),
            Status = ContextMode.ColdStart,
        };
        store.SavePersonalizationModel(model);
        var gotModel = store.GetPersonalizationModel()!;
        Program.Check(gotModel.TrainedAtUtc.Kind == DateTimeKind.Utc && gotModel.TrainedAtUtc == precise,
            "模型 TrainedAtUtc 精确往返");
        Program.Check(gotModel.TrainCutoffUtc.Kind == DateTimeKind.Utc && gotModel.TrainCutoffUtc == precise.AddTicks(19),
            "模型 TrainCutoffUtc 精确往返");

        // 传入 Local/Unspecified 的 DateTime：写入时归一化为 UTC，读回是同一时刻且 Kind=Utc。
        var local = precise.ToLocalTime();
        Program.Check(local.Kind == DateTimeKind.Local, "构造出的本地时间 Kind=Local");
        store.UpsertSession(new LearningSession
        {
            SessionId = "sess-local",
            StartedAtUtc = local,
            Mode = StudyMode.Review,
            PrimarySource = WordSource.Archive,
        });
        var localized = store.GetSession("sess-local")!;
        Program.Check(localized.StartedAtUtc.Kind == DateTimeKind.Utc, "Local 输入读出为 Utc");
        Program.Check(localized.StartedAtUtc == precise, "Local 输入读出为同一时刻");

        // 所有落库的时间串都是定长 28 字符的 UTC ISO-8601 "O"。
        var raw = ScalarText(box.DbPath, "SELECT started_at_utc FROM learning_sessions WHERE session_id = 'sess-utc'");
        Program.Check(raw.Length == 28 && raw.EndsWith('Z'), $"时间列是定长 28 字符的 UTC ISO-8601 串（实际 {raw}）");
    }

    // =====================================================================
    // 14. 个人化模型
    // =====================================================================
    private static void PersonalizationModel()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;

        Program.Check(store.GetPersonalizationModel() is null, "新库没有个人化模型");

        var v1 = new PersonalizationModelState
        {
            ModelVersion = "ctx-v1",
            TrainedAtUtc = Anchor,
            TrainCutoffUtc = Anchor.AddDays(-30),
            Status = ContextMode.Shadow,
            CoefficientsJson = "[0.125,0.25,-0.5]",
            ScalerJson = "{\"mean\":[1,2],\"std\":[3,4]}",
            MetricsJson = "{\"auc\":0.7125}",
            LastGoodModelVersion = null,
        };
        store.SavePersonalizationModel(v1);

        var got = store.GetPersonalizationModel();
        Program.Check(got is not null, "SavePersonalizationModel 后能取回");
        Program.Check(got!.ModelVersion == "ctx-v1", "ModelVersion 往返一致");
        Program.Check(got.TrainedAtUtc == Anchor, "TrainedAtUtc 往返一致");
        Program.Check(got.TrainCutoffUtc == Anchor.AddDays(-30), "TrainCutoffUtc 往返一致");
        Program.Check(got.Status == ContextMode.Shadow, "Status 往返一致");
        Program.Check(got.CoefficientsJson == "[0.125,0.25,-0.5]", "CoefficientsJson 往返一致");
        Program.Check(got.ScalerJson == "{\"mean\":[1,2],\"std\":[3,4]}", "ScalerJson 往返一致");
        Program.Check(got.MetricsJson == "{\"auc\":0.7125}", "MetricsJson 往返一致");
        Program.Check(got.LastGoodModelVersion is null, "LastGoodModelVersion 可为 null");

        var v2 = new PersonalizationModelState
        {
            ModelVersion = "ctx-v2",
            TrainedAtUtc = Anchor.AddDays(1),
            TrainCutoffUtc = Anchor,
            Status = ContextMode.Active,
            CoefficientsJson = "[1,2,3,4]",
            ScalerJson = "{}",
            MetricsJson = "{\"auc\":0.9}",
            LastGoodModelVersion = "ctx-v1",
        };
        store.SavePersonalizationModel(v2);

        var latest = store.GetPersonalizationModel()!;
        Program.Check(latest.ModelVersion == "ctx-v2", $"取回的是最新一版（实际 {latest.ModelVersion}）");
        Program.Check(latest.Status == ContextMode.Active, "最新版字段正确");
        Program.Check(latest.LastGoodModelVersion == "ctx-v1", "LastGoodModelVersion 保留");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models") == 2, "两版模型各自一行");

        v2.CoefficientsJson = "[9,9,9]";
        v2.MetricsJson = "{\"auc\":0.95}";
        store.SavePersonalizationModel(v2);
        var overwritten = store.GetPersonalizationModel()!;
        Program.Check(overwritten.CoefficientsJson == "[9,9,9]" && overwritten.MetricsJson == "{\"auc\":0.95}",
            "同版本号再存是更新而非新增");
        Program.Check(overwritten.LastGoodModelVersion == "ctx-v1", "更新后 LastGoodModelVersion 仍在");
        Program.Check(ScalarInt(box.DbPath, "SELECT COUNT(*) FROM personalization_models") == 2, "仍只有两行");
        Program.Check(ScalarText(box.DbPath, "SELECT model_kind FROM personalization_models WHERE model_version = 'ctx-v2'") == "context",
            "Context 模型以 model_kind='context' 落库");
    }

    // =====================================================================
    // 构造辅助
    // =====================================================================

    private static LearningInteractionEvent NewEvent(
        string eventId, string sessionId, string wordKey, string presentationId, DateTime atUtc, StudyRating rating) => new()
        {
            EventId = eventId,
            SessionId = sessionId,
            WordKey = wordKey,
            PresentationId = presentationId,
            OccurredAtUtc = atUtc,
            LearningMode = LearningMode.Review,
            Kind = InteractionEventKind.Rated,
            Response = rating,
            PreviousResponse = null,
            SessionAppearanceIndex = 1,
            WordAppearanceIndex = 1,
            RecognitionCountBefore = 0,
            RecognitionCountAfter = 1,
            IsFirstAppearanceForWord = true,
            ResponseLatencyMs = 812,
            SupersedesEventId = null,
            IsRecall = true,
        };

    private static FsrsCardState NewCard(
        string wordKey, double difficulty, double stability, long reps, long lapses,
        FsrsState state, DateTime? lastReviewAtUtc, DateTime? nextReviewAtUtc, StudyRating? rating) => new()
        {
            WordKey = wordKey,
            Difficulty = difficulty,
            Stability = stability,
            Reps = reps,
            Lapses = lapses,
            State = state,
            LastReviewAtUtc = lastReviewAtUtc,
            NextReviewAtUtc = nextReviewAtUtc,
            LastCanonicalRating = rating,
            FsrsAlgorithmVersion = "fsrs-6",
            FsrsLibraryVersion = "lib-1.0",
            FsrsParameterVersion = "params-1",
        };

    private static ContextFeatureSnapshot NewSnapshot(
        string snapshotId, string wordKey, string sessionId, string presentationId,
        DateTime capturedAtUtc, string featuresJson, string missingFlagsJson) => new()
        {
            SnapshotId = snapshotId,
            WordKey = wordKey,
            SessionId = sessionId,
            PresentationId = presentationId,
            CapturedAtUtc = capturedAtUtc,
            FsrsDifficultyAtCapture = 4.0,
            FsrsStabilityAtCapture = 9.0,
            FsrsRetrievabilityAtCapture = 0.5,
            FeatureSchemaVersion = "ctx-feat-1",
            ModelVersion = "ctx-model-0",
            FeaturesJson = featuresJson,
            MissingFlagsJson = missingFlagsJson,
        };

    /// <summary>组装一个合法的 <see cref="WordSessionCommit"/>（除 canonicalId 外全部字段都可由参数覆盖）。</summary>
    private static void SeedTrainingAnchor(ILearningMemoryStore store, ContextFeatureSnapshot snapshot,
        StudyRating rating, DateTime completedAtUtc)
    {
        var commit = BuildCommit(snapshot.WordKey, snapshot.SessionId, completedAtUtc);
        commit.Canonical.SourcePresentationId = snapshot.PresentationId;
        commit.Canonical.Rating = rating;
        commit.Canonical.ReviewedAtUtc = snapshot.CapturedAtUtc;
        commit.Canonical.CompletedAtUtc = completedAtUtc;
        store.CommitWordSession(commit);
    }

    private static WordSessionCommit BuildCommit(
        string wordKey,
        string sessionId,
        DateTime atUtc,
        FsrsCardState? cardAfter = null,
        FsrsPreState? preState = null,
        string? canonicalId = null,
        IReadOnlyList<LearningInteractionEvent>? events = null,
        ContextFeatureSnapshot? snapshot = null,
        IReadOnlyList<ContextLabel>? labels = null,
        IReadOnlyList<PendingMutation>? outbox = null)
    {
        var id = canonicalId ?? CanonicalReview.BuildId(wordKey, sessionId);
        var presentationId = "pres:" + sessionId;

        var session = new LearningSession
        {
            SessionId = sessionId,
            StartedAtUtc = atUtc,
            EndedAtUtc = atUtc.AddMinutes(10),
            Mode = StudyMode.Review,
            PrimarySource = WordSource.Archive,
            PlannedWordCount = 12,
            PlanId = "plan-store",
        };

        var summary = new WordSessionSummary
        {
            WordKey = wordKey,
            SessionId = sessionId,
            FirstPresentedAtUtc = atUtc,
            CompletedAtUtc = atUtc.AddMinutes(9),
            FirstInitialResponse = StudyRating.Unsure,
            FirstValidatedResponse = StudyRating.Known,
            TotalPresentations = 3,
            FinalKnownCount = 2,
            FinalFuzzyCount = 1,
            FinalForgottenCount = 0,
            ResetCount = 0,
            ResponseRevisionCount = 1,
            KnownToFuzzy = 1,
            KnownToForgotten = 0,
            FuzzyToForgotten = 0,
            HadFuzzy = true,
            HadForgotten = false,
            HadResponseRevision = true,
            MaxKnownStreak = 2,
            PresentationsToMastery = 3,
            TimeToMasteryMs = 54000,
        };

        var canonical = new CanonicalReview
        {
            CanonicalId = id,
            WordKey = wordKey,
            SessionId = sessionId,
            SourcePresentationId = presentationId,
            Origin = CanonicalOrigin.FirstRetrieval,
            Rating = StudyRating.Known,
            ReviewedAtUtc = atUtc,
            CompletedAtUtc = atUtc.AddMinutes(9),
            AggregationPolicyVersion = AggregationPolicy.Version,
            Revision = 0,
            Invalidated = false,
        };

        var card = cardAfter ?? NewCard(wordKey, 5.0, 15.0, 1, 0, FsrsState.Review,
            atUtc, atUtc.AddDays(4), StudyRating.Known);

        var decision = new SchedulerDecision
        {
            DecisionId = "dec:" + sessionId,
            WordKey = wordKey,
            CanonicalId = id,
            TimestampUtc = atUtc.AddMinutes(9),
            BaselineIntervalDays = 4.0,
            BaselineRetrievability = 0.9,
            BaselineDifficulty = card.Difficulty,
            BaselineStability = card.Stability,
            ContextMode = ContextMode.Disabled,
            ContextDelta = null,
            ContextCandidateIntervalDays = null,
            FinalIntervalDays = 4.0,
            FinalDueAtUtc = card.NextReviewAtUtc ?? atUtc.AddDays(4),
            DesiredRetention = 0.9,
            FsrsAlgorithmVersion = "fsrs-6",
            FsrsLibraryVersion = "lib-1.0",
            FsrsParameterVersion = "params-1",
            AggregationPolicyVersion = AggregationPolicy.Version,
            TrajectorySchemaVersion = "traj-1",
            ContextFeatureSchemaVersion = "ctx-feat-1",
            ContextModelVersion = "ctx-model-0",
            SchedulerVersion = "sched-1",
        };

        return new WordSessionCommit(
            session,
            summary,
            canonical,
            preState ?? new FsrsPreState { Existed = false },
            card,
            decision,
            events ?? [],
            snapshot is null ? [] : [snapshot],
            labels ?? [],
            outbox ?? []);
    }

    // =====================================================================
    // P1-2：只允许撤销该词**最新**的有效 canonical
    // =====================================================================

    private static void UndoRejectsNonLatestCanonical()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;
        var wordKey = WordKey.Archive("ffffffff-9999-4999-8999-ffffffffffff").Key;

        // canonical A：此前没有卡片
        var cardA = NewCard(wordKey, 4.0, 10.0, 1, 0, FsrsState.Review,
            Anchor, Anchor.AddDays(10), StudyRating.Known);
        var idA = CanonicalReview.BuildId(wordKey, "sess-ord-a");
        store.CommitWordSession(BuildCommit(wordKey, "sess-ord-a", Anchor, cardAfter: cardA));
        var afterA = store.GetCard(wordKey)!;
        Program.Check(afterA.Reps == 1, "A 定稿后卡片落地");

        // canonical B：pre-state = A 之后的卡片状态
        var cardB = NewCard(wordKey, 7.5, 44.0, 2, 0, FsrsState.Review,
            Anchor.AddDays(1), Anchor.AddDays(41), StudyRating.Unsure);
        var idB = CanonicalReview.BuildId(wordKey, "sess-ord-b");
        store.CommitWordSession(BuildCommit(wordKey, "sess-ord-b", Anchor.AddDays(1),
            cardAfter: cardB, preState: FsrsPreState.From(afterA)));
        var afterB = store.GetCard(wordKey)!;
        Program.Check(afterB.Reps == 2 && afterB.Stability == cardB.Stability, "B 施加后卡片反映 B");

        // 撤销「非最新」的 A → 必须被拒绝，且不得有任何副作用
        Program.Check(!store.InvalidateCanonical(idA, Anchor.AddDays(1).AddHours(1), "撤销非最新"),
            "撤销非最新的有效 canonical 返回 false");

        var cardNow = store.GetCard(wordKey)!;
        Program.Check(cardNow.Difficulty == afterB.Difficulty && cardNow.Stability == afterB.Stability
                      && cardNow.Reps == afterB.Reps && cardNow.Lapses == afterB.Lapses
                      && cardNow.State == afterB.State
                      && cardNow.LastReviewAtUtc == afterB.LastReviewAtUtc
                      && cardNow.NextReviewAtUtc == afterB.NextReviewAtUtc
                      && cardNow.LastCanonicalRating == afterB.LastCanonicalRating,
            "拒绝后卡片仍是 B 施加后的状态（不被 A 的 pre-state 覆盖）");
        Program.Check(store.HasCard(wordKey), "拒绝后卡片没有被删除");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT invalidated FROM canonical_reviews WHERE canonical_id = '{idA}'") == 0,
            "被拒绝的撤销没有把 A 标记为失效");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT last_applied_canonical_seq FROM fsrs_cards WHERE word_key = '{wordKey}'")
            == ScalarInt(box.DbPath,
                $"SELECT rowid FROM canonical_reviews WHERE canonical_id = '{idB}'"),
            "幂等水位仍然指向 B（拒绝不改动水位）");

        // 撤销**最新**的 B 仍然允许，并精确回放 B 的 pre-state
        Program.Check(store.InvalidateCanonical(idB, Anchor.AddDays(1).AddHours(2), "撤销最新"),
            "撤销最新的有效 canonical 仍然返回 true");
        var replayed = store.GetCard(wordKey)!;
        Program.Check(replayed.Reps == afterA.Reps && replayed.Stability == afterA.Stability,
            "撤销最新后精确回放到 B 之前的卡片状态（A 施加后的状态）");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT last_applied_canonical_seq FROM fsrs_cards WHERE word_key = '{wordKey}'")
            == ScalarInt(box.DbPath,
                $"SELECT MAX(rowid) FROM canonical_reviews WHERE word_key = '{wordKey}' AND invalidated = 0"),
            "水位回退到仍有效的最新 canonical（A）");
        Program.Check(store.InvalidateCanonical(idA, Anchor.AddDays(1).AddHours(3), "现在 A 已是最新"),
            "A 变为最新后可以撤销");
    }

    // =====================================================================
    // P1-7：高频追加型写不再每次触发全库备份；定稿仍触发一次
    // =====================================================================

    private static void HighFrequencyWritesDoNotBackupTheWholeDatabase()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;
        var now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var coordinator = new LearningMemoryCoordinator(store, new Fsrs6Scheduler(), clock: () => now);
        var word = WordKey.Archive("bbbbbbbb-aaaa-4aaa-8aaa-bbbbbbbbbbbb");

        coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-backup", 1);
        var afterSession = CountBackups(box.Dir);

        // 一次真实卡片的完整流程：呈现（AppendEvent + SaveContextSnapshot）→ 作答 → 改判
        coordinator.OnPresented(word, "bk-p1", true);
        now = now.AddSeconds(4);
        coordinator.OnRated(word, "bk-p1", StudyRating.Known, 0, 1, null);
        now = now.AddSeconds(2);
        coordinator.OnRevised(word, "bk-p1", StudyRating.Known, StudyRating.Unsure, null);

        Program.Check(CountBackups(box.Dir) == afterSession,
            $"呈现/作答/改判不再每次触发全库备份（新增 {CountBackups(box.Dir) - afterSession} 次）");

        now = now.AddSeconds(2);
        var commit = coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(commit is not null && commit.Applied, "定稿成功");
        Program.Check(CountBackups(box.Dir) == afterSession + 1,
            $"定稿仍然触发恰好一次全库备份（新增 {CountBackups(box.Dir) - afterSession} 次）");
    }

    private static int CountBackups(string dir)
    {
        var folder = Path.Combine(dir, "backups");
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "lexi-*.sqlite3").Length : 0;
    }

    // =====================================================================
    // P1-8：Unspecified 一律按 UTC 解释（与 Fsrs6Scheduler.NormalizeUtc 同一口径）
    // =====================================================================

    private static void UnspecifiedTimestampIsInterpretedAsUtc()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;
        var wordKey = WordKey.Archive("cccccccc-bbbb-4bbb-8bbb-cccccccccccc").Key;
        var unspecified = new DateTime(2026, 10, 6, 13, 45, 14, DateTimeKind.Unspecified);

        store.AppendEvent(NewEvent("evt-unspec", "sess-unspec", wordKey, "pres-unspec", unspecified, StudyRating.Known));
        var readBack = store.LoadEvents("sess-unspec")[0].OccurredAtUtc;
        Program.Check(readBack == DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
            $"持久层把 Unspecified 当 UTC（读到 {readBack:O}，期望 {DateTime.SpecifyKind(unspecified, DateTimeKind.Utc):O}）");

        var scheduler = new Fsrs6Scheduler();
        Program.Check(scheduler.Review(null, StudyRating.Known, unspecified).Card!.LastReviewAtUtc == readBack,
            "排期层（NormalizeUtc）与持久层对 Unspecified 的解释一致（同为 UTC）");

        var localValue = new DateTime(2026, 10, 6, 13, 45, 14, DateTimeKind.Local);
        store.AppendEvent(NewEvent("evt-local", "sess-local", wordKey, "pres-local", localValue, StudyRating.Known));
        Program.Check(store.LoadEvents("sess-local")[0].OccurredAtUtc == localValue.ToUniversalTime(),
            "Kind=Local 仍然按本机时区换算（不是「一律 UTC」）");
    }

    // =====================================================================
    // §Q / 裁定 O-2：真实库 + 真实 FSRS —— 同词同日跨 session 两条 canonical 并存，
    // 第二条走 elapsed_days == 0 的 short-term 路径，且当天较早那次 Again 的 lapse 不被抹掉。
    // =====================================================================

    private static void SameDaySecondSessionKeepsLapseAndUsesShortTermPath()
    {
        using var box = new Sandbox();
        var store = box.MemoryPort;
        var scheduler = new Fsrs6Scheduler();
        var now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var coordinator = new LearningMemoryCoordinator(store, scheduler, clock: () => now);
        var word = WordKey.Archive("dddddddd-cccc-4ccc-8ccc-dddddddddddd");
        var key = word.Key;

        // session 1：唯一一次作答 Again（首次复习 → 新建卡片，lapses = 1）
        coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-day", 1);
        coordinator.OnPresented(word, "s1p1", true);
        now = now.AddSeconds(4);
        coordinator.OnRated(word, "s1p1", StudyRating.Forgot, 0, 0, null);
        var first = coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(first is not null && first.Applied, "当天第一次定稿成功（Again）");

        var cardAfterFirst = store.GetCard(key)!;
        Program.Check(cardAfterFirst.Lapses == 1 && cardAfterFirst.Reps == 1,
            $"第一次 Again 建立卡片并记 1 次 lapse（Reps={cardAfterFirst.Reps} / Lapses={cardAfterFirst.Lapses}）");

        // session 2：同一个本地日历日（30 分钟后），作答 Good
        now = now.AddMinutes(30);
        Program.Check(now.ToLocalTime().Date == new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).ToLocalTime().Date,
            "测试前提：两次定稿落在同一个本地日历日");
        var session2 = coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "plan-day", 1);
        coordinator.OnPresented(word, "s2p1", true);
        now = now.AddSeconds(4);
        coordinator.OnRated(word, "s2p1", StudyRating.Known, 0, 1, null);
        var second = coordinator.CommitWord(word, StudyMode.Review);
        Program.Check(second is not null && second.Applied, "同一个本地日第二个 session 的定稿仍然成功");
        Program.Check(second!.CanonicalId != first!.CanonicalId && second.CanonicalId == CanonicalReview.BuildId(key, session2),
            "第二条 canonical 的身份按它自己的 session 生成");

        // —— 数据库层：两条 canonical 都 invalidated = 0 ——
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM canonical_reviews WHERE word_key = '{key}' AND invalidated = 0") == 2,
            "canonical_reviews 里该词有 2 条有效 canonical（跨 session 并存）");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM canonical_reviews WHERE word_key = '{key}'") == 2,
            "一共就 2 行（没有多余行、也没有被删除）");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT COUNT(*) FROM canonical_reviews WHERE word_key = '{key}' AND invalidated = 1") == 0,
            "没有任何一条被标记失效（同日第二条不再把第一条顶掉）");

        // —— 第二条 canonical 的 pre-state 就是第一条之后的状态（不是回退到当天开始之前）——
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT reps FROM canonical_reviews WHERE canonical_id = '{second.CanonicalId}'") == 1,
            "第二条 canonical 的 pre-state 的 reps = 1（pre-state 存在且是第一张卡的状态，不是「此前无卡」）");
        Program.Check(ScalarInt(box.DbPath,
                $"SELECT lapses FROM canonical_reviews WHERE canonical_id = '{second.CanonicalId}'") == 1,
            "第二条 canonical 的 pre-state 带着第一条的 lapse（卡片状态连续演进，没有被回放抹掉）");
        // —— 真实 FSRS：第二条走 short-term 公式，且 lapse 保留 ——
        var card = store.GetCard(key)!;
        Program.Check(card.Reps == 2, $"两次 canonical 各推进一次 Reps（实际 {card.Reps}）");
        Program.Check(card.Lapses == 1, $"首次 Again 的 lapse 没有被第二条 canonical 抹掉（实际 {card.Lapses}）");

        var weights = scheduler.Weights.Weights;
        var expectedStability = Fsrs6Model.ShortTermStability(weights, cardAfterFirst.Stability, 3);
        Program.Check(card.Stability == expectedStability,
            $"第二条走 elapsed_days == 0 的 short-term 公式：S = ShortTermStability(S1, Good)（实际 {card.Stability}，期望 {expectedStability}）");
        Program.Check(card.Stability != Fsrs6Model.ClampStability(weights[2]),
            "第二条不是「按新卡初始化」的结果（证明没有把卡片回放成不存在）");
    }

    // =====================================================================
    // 沙箱与 SQL 辅助
    // =====================================================================

    /// <summary>每个测试自己的临时目录：建库、关闭、重开、最后整目录删除。</summary>
    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; }
        public string DbPath { get; }

        private VocabularyService? _service;

        public Sandbox()
        {
            Dir = Path.Combine(Path.GetTempPath(), "lexi-store-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbPath = Path.Combine(Dir, "vocab.sqlite3");
            Open();
        }

        public VocabularyService Service => _service ?? throw new InvalidOperationException("沙箱服务未打开。");

        public IVocabularyArchive ArchivePort { get; private set; } = null!;

        public ILearningMemoryStore MemoryPort { get; private set; } = null!;

        public void Open()
        {
            if (_service != null) return;
            ArchivePort = ServiceFactory.OpenArchive(DbPath);
            MemoryPort = ServiceFactory.OpenMemory(ArchivePort);
            _service = (VocabularyService)ArchivePort;
        }

        public void Close()
        {
            _service?.Dispose();
            _service = null;
        }

        public void Dispose()
        {
            Close();
            try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static SqliteConnection Connect(string dbPath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(string dbPath, string table)
        => ScalarInt(dbPath, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'") == 1;

    private static bool IndexExists(string dbPath, string index)
        => ScalarInt(dbPath, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{index}'") == 1;

    private static long ScalarInt(string dbPath, string sql)
    {
        using var connection = Connect(dbPath, SqliteOpenMode.ReadOnly);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string ScalarText(string dbPath, string sql)
    {
        using var connection = Connect(dbPath, SqliteOpenMode.ReadOnly);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static List<long> QueryInts(string dbPath, string sql)
    {
        var list = new List<long>();
        using var connection = Connect(dbPath, SqliteOpenMode.ReadOnly);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetInt64(0));
        return list;
    }

    /// <summary>把整张表按固定列序 dump 成可逐字节比较的文本（NULL 记为 NULL，数值用不变文化）。</summary>
    private static string Dump(string dbPath, string table, string columns)
    {
        var builder = new StringBuilder();
        using var connection = Connect(dbPath, SqliteOpenMode.ReadOnly);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {columns} FROM {table} ORDER BY 1";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) builder.Append('|');
                builder.Append(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            }
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static void Exec(string dbPath, string sql)
    {
        using var connection = Connect(dbPath, SqliteOpenMode.ReadWrite);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
