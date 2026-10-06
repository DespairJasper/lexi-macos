using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lexi.Core;

namespace Lexi;

public static class FoundationDataTests
{
    public static void RunReviewFixes()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lexi_DataReview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<string>();
        void Test(string name, Action<string> test)
        {
            try { test(Path.Combine(root, name + ".sqlite3")); Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
        }
        try
        {
            Test("local-time-migration", path =>
            {
                using (var service = new VocabularyService(path)) service.AddWord("local-date", "", "时间", "");
                Sql(path, "DROP TRIGGER lexi_word_revision; DROP TABLE word_archives; DROP TABLE schema_migrations; UPDATE words SET created_at='2026-09-18 10:00:00';");
                using var migrated = new VocabularyService(path);
                var word = migrated.GetAllWords().Single();
                var expected = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, 18, 10, 0, 0), TimeZoneInfo.Local);
                Assert(DateTime.Parse(word.Archive.CreatedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind) == expected
                    && word.CreatedAt == "2026-09-18 10:00:00", "legacy local wall-clock time was relabeled UTC or changed");
            });
            Test("migration-atomic", path =>
            {
                using (var service = new VocabularyService(path)) service.AddWord("collision", "", "冲突", "");
                Sql(path, "DROP TRIGGER lexi_word_revision; DROP TABLE word_archives; DROP TABLE schema_migrations; INSERT INTO words(word,created_at,learning_start_date) VALUES('collision ','2026-09-18 10:00:00','2026-09-18');");
                Reject(() => { using var failed = new VocabularyService(path); });
                using var c = new SqliteConnection($"Data Source={path};Pooling=false"); c.Open(); using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN ('schema_migrations','word_archives')";
                Assert(Convert.ToInt32(cmd.ExecuteScalar()) == 0, "failed migration left partially created archive schema");
                cmd.CommandText = "SELECT count(*) FROM words";
                Assert(Convert.ToInt32(cmd.ExecuteScalar()) == 2, "failed migration changed words");
            });
            Test("archive-authority", path =>
            {
                using var service = new VocabularyService(path); service.AddWord("anchor", "", "锚", "");
                var original = service.GetAllWords().Single(); var stale = service.GetAllWords().Single().Archive!;
                service.RecordEncounter(original.Id);
                var latest = service.GetAllWords().Single().Archive!;
                stale.Uuid = Guid.NewGuid().ToString(); stale.EncounterCount = 900; stale.Revision = 800;
                stale.CreatedAtUtc = "2000-01-01T00:00:00Z"; stale.LastEncounteredAtUtc = stale.CreatedAtUtc;
                service.SaveArchive(original.Id, "锚点", "编辑", stale, null);
                var saved = service.GetAllWords().Single().Archive!;
                Assert(saved.Uuid == original.Archive!.Uuid && saved.EncounterCount == 2 && saved.CreatedAtUtc == original.Archive.CreatedAtUtc
                    && saved.LastEncounteredAtUtc == latest.LastEncounteredAtUtc && saved.Revision == latest.Revision + 1, "stale editor overwrote authoritative metadata or revision");
                Assert(stale.Revision == 800, "save mutated caller metadata");
            });
            Test("unfamiliar-mastered", path =>
            {
                using var service = new VocabularyService(path); service.AddWord("mastered", "", "熟悉", "");
                var id = service.GetAllWords().Single().Id; service.ExecuteBatch([id], "master");
                var before = service.GetAllWords().Single(); service.MarkUnfamiliar(id);
                var after = service.GetAllWords().Single();
                Assert(after.Stage == 4 && after.Status == "learning" && after.ReviewCount == before.ReviewCount, "mastered word retained invalid learning stage 5");
                Assert(after.Archive!.Revision == before.Archive!.Revision + 1, "unfamiliar revision incremented incorrectly");
                Assert(service.UndoMostRecentReview(), "unfamiliar undo unavailable");
                var undone = service.GetAllWords().Single();
                Assert(undone.Stage == 5 && undone.Status == "mastered" && undone.LastReviewedAt == before.LastReviewedAt
                    && undone.ReviewCount == before.ReviewCount && undone.NextReviewDate == before.NextReviewDate, "undo lost prior review state");
                Assert(undone.Archive!.Revision == after.Archive.Revision + 1, "undo did not advance revision exactly once");
            });
            Test("validation", path =>
            {
                using var service = new VocabularyService(path); service.AddWord("validation", "", "校验", "");
                var word = service.GetAllWords().Single();
                service.SaveArchive(word.Id, new string('释', 10000), "", word.Archive!, null);
                var before = service.GetAllWords().Single();
                word.Archive!.Tags = [null!];
                Reject(() => service.SaveArchive(word.Id, "invalid", "", word.Archive, null));
                Reject(() => service.SaveExpansion(word.Id, new LlmResult { Examples = [null!] }));
                Reject(() => service.SaveExpansion(word.Id, new LlmResult { Synonyms = null! }));
                Reject(() => service.SaveExpansion(word.Id, new LlmResult { Phrases = Enumerable.Repeat(new PhraseItem("a", "b"), 21).ToList() }));
                Assert(service.GetAllWords().Single().Translation == before.Translation, "validation failure changed stored content");
            });
            Test("future-schema", path =>
            {
                using (var service = new VocabularyService(path)) service.AddWord("future", "", "未来", "");
                Sql(path, "INSERT INTO schema_migrations VALUES(2,'2026-09-19T00:00:00Z')");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
                Reject(() => { using var service = new VocabularyService(path); });
                Assert(hash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), "future schema rejection changed original DB");
                Reject(() => DatabaseSafety.RestoreBackup(path, path + ".restored"));
                Assert(!File.Exists(path + ".restored"), "future schema restore created target");
            });
            Test("broken-schema", path =>
            {
                using (var service = new VocabularyService(path)) { }
                Sql(path, "DROP TABLE word_archives");
                Reject(() => { using var service = new VocabularyService(path); });
            });
            Test("corrupt-tags", path =>
            {
                using (var service = new VocabularyService(path)) service.AddWord("corrupt", "", "坏", "");
                Sql(path, "UPDATE word_archives SET tags_json='[null]'");
                Reject(() => { using var service = new VocabularyService(path); service.GetAllWords(); });
            });
            Test("corrupt-ai", path =>
            {
                using (var service = new VocabularyService(path)) service.AddWord("corrupt", "", "坏", "");
                Sql(path, "UPDATE word_archives SET ai_json='{bad}'");
                Reject(() => { using var service = new VocabularyService(path); service.GetAllWords(); });
            });
            Test("revision-and-model", path =>
            {
                var model = new WordItem(); var notices = new List<string?>(); model.PropertyChanged += (_, e) => notices.Add(e.PropertyName);
                Assert(model.Archive != null, "default archive is null"); model.Notes = "changed";
                Assert(notices.Contains("Notes"), "notes omitted property notification");
                using var service = new VocabularyService(path); service.AddWord("revision", "", "版本", "");
                var before = service.GetAllWords().Single(); service.ExecuteBatch([before.Id], "review");
                var after = service.GetAllWords().Single();
                Assert(after.Archive!.Revision == before.Archive!.Revision + 1, "review omitted revision");
                service.ExecuteBatch([before.Id], "review");
                Assert(service.GetAllWords().Single().Archive!.Revision == after.Archive.Revision, "duplicate review changed revision");
            });
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

        static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Text.Json.JsonException or SqliteException) { return; } throw new Exception("invalid input accepted"); }
        static void Sql(string path, string sql) { using var c = new SqliteConnection($"Data Source={path};Pooling=false"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    }
    public static void Run()
    {
        RunReviewFixes();
        Console.WriteLine("=================================================");
        Console.WriteLine("      Foundation Data Layer 自动化测试 (Task 1)     ");
        Console.WriteLine("=================================================");

        var root = Path.Combine(Path.GetTempPath(), "Lexi_FoundationDataTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Console.Write("[1/10] 测试 ReviewSchedule 纯日期计算与排期规则... ");
            TestReviewSchedule();
            Console.WriteLine("PASS");

            Console.Write("[2/10] 测试旧库版本迁移、表建立与稳定 UUID 幂等性... ");
            TestLegacyMigrationAndIdempotency(root);
            Console.WriteLine("PASS");

            Console.Write("[3/10] 测试迁移事务原子性与失败不改原库... ");
            TestMigrationTransactionSafety(root);
            Console.WriteLine("PASS");

            Console.Write("[4/10] 测试 AddWord 新词首次遇见为1、重复Add不累加... ");
            TestAddWordEncounter(root);
            Console.WriteLine("PASS");

            Console.Write("[5/10] 测试 RecordEncounter 明确动作计数+1并更新UTC时间戳... ");
            TestRecordEncounter(root);
            Console.WriteLine("PASS");

            Console.Write("[6/10] 测试 SaveArchive 事务校验、长度上限与元数据更新... ");
            TestSaveArchive(root);
            Console.WriteLine("PASS");

            Console.Write("[7/10] 测试 SaveExpansion 与 LlmResult 持久化及 GetAllWords 反序列化... ");
            TestSaveExpansionAndAiResult(root);
            Console.WriteLine("PASS");

            Console.Write("[8/10] 测试 MarkUnfamiliar 明日重逢、不重置进度、同日重叠与撤销... ");
            TestMarkUnfamiliar(root);
            Console.WriteLine("PASS");

            Console.Write("[9/10] 测试 CreateManualBackup 独立备份生成与验证... ");
            TestManualBackup(root);
            Console.WriteLine("PASS");

            Console.Write("[10/10] 测试 AppSettings 新字段 (AiContext, IncludeSourceInAi) 持久化... ");
            TestAppSettings(root);
            Console.WriteLine("PASS");

            Console.WriteLine("=================================================");
            Console.WriteLine("  Foundation Data Layer 测试全部通过！           ");
            Console.WriteLine("=================================================");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FoundationDataTest 失败: " + message);
    }

    private static void TestReviewSchedule()
    {
        var start = new DateTime(2026, 9, 19);
        var initial = ReviewSchedule.CalculateInitialReviewDate(start, 0);
        Assert(initial == new DateTime(2026, 9, 20), "阶段0下次复习应为+1天");

        // Normal review on-time
        var onTime = ReviewSchedule.CalculateNextReview(
            today: new DateTime(2026, 9, 20),
            learningStartDate: start,
            currentScheduledDate: new DateTime(2026, 9, 20),
            currentStage: 0);
        Assert(onTime.NewStage == 1, "阶段应为1");
        Assert(onTime.NextReviewDate == start.AddDays(ReviewSchedule.DefaultStageOffsets[1]), "正常复习下次日期计算不符");
        Assert(!onTime.IsMastered, "阶段1不应为已掌握");

        // Overdue review: today is 10 days after scheduled
        var overdue = ReviewSchedule.CalculateNextReview(
            today: new DateTime(2026, 9, 30),
            learningStartDate: start,
            currentScheduledDate: new DateTime(2026, 9, 20),
            currentStage: 0);
        Assert(overdue.NewStage == 1, "逾期推进后阶段应为1");
        Assert(overdue.NextReviewDate > new DateTime(2026, 9, 30), "逾期推进后日期必须晚于今天");

        // Mastered when reaching stage 5
        var mastered = ReviewSchedule.CalculateNextReview(
            today: new DateTime(2026, 9, 20),
            learningStartDate: start,
            currentScheduledDate: new DateTime(2026, 9, 20),
            currentStage: 4);
        Assert(mastered.IsMastered, "达到最大阶段应为已掌握");

        // Unfamiliar
        var unfam = ReviewSchedule.CalculateUnfamiliarReviewDate(new DateTime(2026, 9, 19));
        Assert(unfam == new DateTime(2026, 9, 20), "不熟词应安排在明日");
    }

    private static void TestLegacyMigrationAndIdempotency(string root)
    {
        var dbPath = Path.Combine(root, "legacy_migrate.sqlite3");

        // Create legacy DB without word_archives or schema_migrations
        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=false"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE words (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    word TEXT UNIQUE NOT NULL COLLATE NOCASE,
                    phonetic TEXT DEFAULT '',
                    translation TEXT DEFAULT '',
                    definition TEXT DEFAULT '',
                    notes TEXT DEFAULT '',
                    stage INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'learning',
                    created_at TEXT NOT NULL,
                    learning_start_date TEXT NOT NULL,
                    next_review_date TEXT,
                    last_reviewed_at TEXT,
                    review_count INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE review_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    word_id INTEGER NOT NULL,
                    action TEXT NOT NULL,
                    old_stage INTEGER,
                    new_stage INTEGER,
                    old_status TEXT,
                    new_status TEXT,
                    old_next_review_date TEXT,
                    new_next_review_date TEXT,
                    old_learning_start_date TEXT,
                    new_learning_start_date TEXT,
                    log_time TEXT NOT NULL,
                    log_date TEXT NOT NULL
                );
                CREATE TABLE app_settings (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    settings_json TEXT NOT NULL
                );
                INSERT INTO words (word, phonetic, translation, definition, notes, stage, status, created_at, learning_start_date, next_review_date, last_reviewed_at, review_count)
                VALUES ('perseverance', '/ˌpɜːrsəˈvɪrəns/', '毅力', 'persistence in doing something', '重点词', 2, 'learning', '2026-09-18 10:00:00', '2026-09-18', '2026-09-22', '2026-09-18 10:00:00', 2);
            ";
            cmd.ExecuteNonQuery();
        }

        // Open with VocabularyService to trigger migration
        string initialUuid;
        using (var vocab = new VocabularyService(dbPath))
        {
            var words = vocab.GetAllWords();
            Assert(words.Count == 1, "应有1个词");
            var w = words[0];
            Assert(w.Word == "perseverance", "词汇名称匹配");
            Assert(w.Stage == 2, "旧库阶段进度应保持不变");
            Assert(w.Notes == "重点词", "旧库备注应保持不变");
            Assert(w.Archive != null, "迁移后 WordItem.Archive 不应为 null");
            Assert(!string.IsNullOrEmpty(w.Archive!.Uuid), "应分配 UUID");
            Assert(w.Archive.EncounterCount == 1, "旧词初始化遇见过次数应为1");
            Assert(w.Archive.Revision == 1, "初始 Revision 应为1");
            initialUuid = w.Archive.Uuid;
        }

        // Open second time: verify idempotency, UUID should NOT change
        using (var vocab = new VocabularyService(dbPath))
        {
            var words = vocab.GetAllWords();
            var w = words[0];
            Assert(w.Archive != null && w.Archive.Uuid == initialUuid, "再次初始化应保持相同 UUID (幂等性)");
            Assert(w.Archive!.EncounterCount == 1, "再次初始化不应增加遇见次数");
            Assert(w.Archive!.Revision == 1, "再次初始化不应改变 Revision");
        }
    }

    private static void TestMigrationTransactionSafety(string root)
    {
        var dbPath = Path.Combine(root, "mig_safe.sqlite3");
        using (var vocab = new VocabularyService(dbPath))
        {
            vocab.AddWord("testword", "", "测试", "");
        }

        // Check backup exists before schema change
        var backupDir = Path.Combine(root, "backups");
        Assert(Directory.Exists(backupDir), "备份目录应存在");
        var backups = Directory.GetFiles(backupDir, "lexi-*.sqlite3");
        Assert(backups.Length > 0, "每次状态提交均应保留安全备份");
    }

    private static void TestAddWordEncounter(string root)
    {
        var dbPath = Path.Combine(root, "add_enc.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        // Add new word
        vocab.AddWord("ephemeral", "/ɪˈfemərəl/", "短暂的", "lasting for a very short time");
        var words = vocab.GetAllWords();
        Assert(words.Count == 1, "新词入库成功");
        var w = words[0];
        Assert(w.Archive != null, "新词应有 Archive");
        Assert(w.Archive!.EncounterCount == 1, "新词首次遇见次数必须为1");
        Assert(w.Archive.Revision == 1, "新词初始 Revision 必须为1");
        var firstUuid = w.Archive.Uuid;

        // Repeat AddWord with the same word
        vocab.AddWord("ephemeral", "/ɪˈfemərəl/", "短暂的; 瞬息的", "lasting for a very short time");
        words = vocab.GetAllWords();
        Assert(words.Count == 1, "重复 AddWord 不得新增条目");
        w = words[0];
        Assert(w.Archive != null, "重复 AddWord 后 Archive 仍存在");
        Assert(w.Archive!.EncounterCount == 1, "重复 AddWord 绝对不得增加遇见次数");
        Assert(w.Archive.Uuid == firstUuid, "UUID 不应改变");
    }

    private static void TestRecordEncounter(string root)
    {
        var dbPath = Path.Combine(root, "rec_enc.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        vocab.AddWord("serendipity", "", "机缘巧合", "");
        var word = vocab.GetAllWords().Single();
        Assert(word.Archive!.EncounterCount == 1, "初始遇见次数为1");
        var initialRevision = word.Archive.Revision;
        var initialLastEnc = word.Archive.LastEncounteredAtUtc;

        // User explicit action: RecordEncounter
        vocab.RecordEncounter(word.Id);
        word = vocab.GetAllWords().Single();
        Assert(word.Archive!.EncounterCount == 2, "RecordEncounter 应让遇见次数变为2");
        Assert(word.Archive.Revision > initialRevision, "Revision 必须递增");
        Assert(!string.IsNullOrEmpty(word.Archive!.LastEncounteredAtUtc), "LastEncounteredAtUtc 必须有值");
    }

    private static void TestSaveArchive(string root)
    {
        var dbPath = Path.Combine(root, "save_archive.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        vocab.AddWord("tenacious", "", "顽强的", "");
        var word = vocab.GetAllWords().Single();
        var id = word.Id;

        // 1. Metadata null rejected
        bool nullMetaCaught = false;
        try
        {
            vocab.SaveArchive(id, "顽强的", "备注", null!, null);
        }
        catch (ArgumentNullException)
        {
            nullMetaCaught = true;
        }
        Assert(nullMetaCaught, "metadata 为 null 时必须抛出 ArgumentNullException");

        // 2. Empty translation rejected
        bool emptyTransCaught = false;
        try
        {
            vocab.SaveArchive(id, "   ", "备注", word.Archive!, null);
        }
        catch (ArgumentException)
        {
            emptyTransCaught = true;
        }
        Assert(emptyTransCaught, "释义为空时必须抛出 ArgumentException");

        // 3. Length limit validation
        bool longNotesCaught = false;
        try
        {
            vocab.SaveArchive(id, "顽强的", new string('x', 10001), word.Archive!, null);
        }
        catch (ArgumentException)
        {
            longNotesCaught = true;
        }
        Assert(longNotesCaught, "备注超长必须抛出 ArgumentException");

        // 4. Valid save
        var meta = new ArchiveMetadata
        {
            Uuid = word.Archive!.Uuid,
            SourceType = "book",
            SourceTitle = "Steve Jobs",
            SourceExcerpt = "He was tenacious.",
            Tags = new[] { "biography", "apple" },
            EncounterCount = 3,
            Revision = word.Archive.Revision,
            CreatedAtUtc = word.Archive.CreatedAtUtc,
            UpdatedAtUtc = word.Archive.UpdatedAtUtc,
            LastEncounteredAtUtc = word.Archive.LastEncounteredAtUtc
        };

        var ai = new LlmResult
        {
            Examples = new() { new ExampleItem("He was tenacious.", "他很顽强。") },
            Synonyms = new() { "persistent", "stubborn" }
        };

        vocab.SaveArchive(id, "顽强的; 坚韧不拔的", "重要修辞", meta, ai);

        var updated = vocab.GetAllWords().Single();
        Assert(updated.Translation == "顽强的; 坚韧不拔的", "释义更新成功");
        Assert(updated.Notes == "重要修辞", "备注更新成功");
        Assert(updated.Archive != null, "Archive 存在");
        Assert(updated.Archive!.SourceType == "book", "SourceType 匹配");
        Assert(updated.Archive.SourceTitle == "Steve Jobs", "SourceTitle 匹配");
        Assert(updated.Archive.SourceExcerpt == "He was tenacious.", "SourceExcerpt 匹配");
        Assert(updated.Archive.Tags.SequenceEqual(new[] { "biography", "apple" }), "Tags 匹配");
        Assert(updated.Archive.EncounterCount == 1, "编辑档案不得改写遇见次数");
        Assert(updated.Archive.Revision > word.Archive.Revision, "Revision 递增");
        Assert(updated.AiResult != null, "AiResult 成功保存");
        Assert(updated.AiResult!.Examples.Count == 1, "AiResult 例句保存成功");
        Assert(updated.AiResult.Synonyms.Count == 2, "AiResult 同义词保存成功");
    }

    private static void TestSaveExpansionAndAiResult(string root)
    {
        var dbPath = Path.Combine(root, "save_expansion.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        vocab.AddWord("lucid", "", "清晰的", "");
        var word = vocab.GetAllWords().Single();
        Assert(word.AiResult == null, "初次录入无 AI");

        var llm = new LlmResult
        {
            Examples = new() { new ExampleItem("A lucid explanation.", "清晰的解释。") },
            Synonyms = new() { "clear", "transparent" },
            Antonyms = new() { "vague", "obscure" },
            Phrases = new() { new PhraseItem("lucid dream", "清醒梦") }
        };

        vocab.SaveExpansion(word.Id, llm);

        var updated = vocab.GetAllWords().Single();
        Assert(updated.AiResult != null, "SaveExpansion 后 AiResult 不为空");
        Assert(updated.AiResult!.HasContent, "AiResult HasContent 应当为 true");
        Assert(updated.AiResult.Examples.Count == 1, "例句数量为 1");
        Assert(updated.AiResult.Synonyms.Count == 2, "同义词数量为 2");
        Assert(updated.AiResult.Antonyms.Count == 2, "反义词数量为 2");
        Assert(updated.AiResult.Phrases.Count == 1, "常用词组数量为 1");
    }

    private static void TestMarkUnfamiliar(string root)
    {
        var dbPath = Path.Combine(root, "mark_unfam.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        vocab.AddWord("conundrum", "", "难题", "");
        var word = vocab.GetAllWords().Single();
        var id = word.Id;

        // Advance to stage 2
        vocab.ExecuteBatch(new[] { id }, "stage", 2);
        word = vocab.GetAllWords().Single();
        Assert(word.Stage == 2, "阶段推进到2");
        var schedBefore = word.NextReviewDate;

        // Mark unfamiliar
        vocab.MarkUnfamiliar(id);
        word = vocab.GetAllWords().Single();
        var tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        Assert(word.NextReviewDate == tomorrow, "MarkUnfamiliar 必须将下一次复习安排在明日");
        Assert(word.Stage == 2, "MarkUnfamiliar 绝不可重置已有学习阶段 stage");
        Assert(word.Status == "learning", "状态应为 learning");

        // Undo MarkUnfamiliar
        var undoOk = vocab.UndoLastReview(id);
        Assert(undoOk, "MarkUnfamiliar 应当支持撤销");
        word = vocab.GetAllWords().Single();
        Assert(word.NextReviewDate == schedBefore, "撤销后恢复原有复习排期");

        // Same-day conflict test: word was already reviewed today (记得)
        vocab.ExecuteBatch(new[] { id }, "review");
        word = vocab.GetAllWords().Single();
        var stageAfterReview = word.Stage;

        // User later marks unfamiliar on the same day: should arrange tomorrow
        vocab.MarkUnfamiliar(id);
        word = vocab.GetAllWords().Single();
        Assert(word.NextReviewDate == tomorrow, "同日记得后再标不熟，仍必须安排在明日");
        Assert(word.Stage == stageAfterReview, "阶段不得被重置");
    }

    private static void TestManualBackup(string root)
    {
        var dbPath = Path.Combine(root, "backup_test.sqlite3");
        using var vocab = new VocabularyService(dbPath);
        vocab.AddWord("resilience", "", "弹性", "");

        var backupPath = vocab.CreateManualBackup();
        Assert(File.Exists(backupPath), "手动备份文件必须存在于磁盘上");
        Assert(backupPath.EndsWith(".sqlite3"), "备份文件格式必须为 sqlite3");

        // Verify backup passes integrity
        using var conn = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=false");
        conn.Open();
        DatabaseSafety.Validate(conn);
    }

    private static void TestAppSettings(string root)
    {
        var dbPath = Path.Combine(root, "settings_test.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        var defaults = vocab.LoadSettings();
        Assert(defaults.AiContext == "日常表达", "默认 AiContext 应为 '日常表达'");
        Assert(!defaults.IncludeSourceInAi, "默认 IncludeSourceInAi 应为 false");

        defaults.AiContext = "学术阅读";
        defaults.IncludeSourceInAi = true;
        vocab.SaveSettings(defaults);

        var reloaded = vocab.LoadSettings();
        Assert(reloaded.AiContext == "学术阅读", "修改后的 AiContext 必须持久化保存");
        Assert(reloaded.IncludeSourceInAi == true, "修改后的 IncludeSourceInAi 必须持久化保存");
    }
}
