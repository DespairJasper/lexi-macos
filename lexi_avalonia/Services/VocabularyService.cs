using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lexi.Core;

namespace Lexi;

public sealed partial class VocabularyService : IVocabularyArchive, ILearningMemoryStore, IDisposable
{
    public static readonly int[] StageOffsets = ReviewSchedule.DefaultStageOffsets;
    public const int MaxStage = ReviewSchedule.DefaultMaxStage;

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly ISecureSecretStore _secretStore;
    public string CredentialWarning { get; private set; } = "";

    public string DatabasePath => _dbPath;
    public string BackupWarning { get; private set; } = "";
    public string? LastBackupPath { get; private set; }

    public static string GetDefaultDatabasePath()
    {
        var dataDirEnv = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDirEnv))
        {
            return Path.Combine(Path.GetFullPath(dataDirEnv.Trim()), "vocab.sqlite3");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Lexi", "vocab.sqlite3");
    }

    public static string GetLegacyDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "cn.local.lexi", "vocab.sqlite3");
    }

    public string MigrationMessage { get; private set; } = "";

    public static bool TryMigrateLegacyDatabase(string targetPath, out string message)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")))
        {
            message = "测试环境隔离中，跳过自动迁移。";
            return false;
        }
        return TryMigrateDatabase(GetLegacyDatabasePath(), targetPath, out message);
    }

    // Explicit paths make migration testable without ever consulting the real profile.
    public static bool TryMigrateDatabase(string sourcePath, string targetPath, out string message)
    {
        message = "";
        string? temporary = null;
        try
        {
            sourcePath = Path.GetFullPath(sourcePath);
            targetPath = Path.GetFullPath(targetPath);
            if (File.Exists(targetPath)) { message = "新词库已存在，无需迁移。"; return false; }
            DatabaseSafety.RejectOrphanedSidecars(targetPath);
            if (!File.Exists(sourcePath)) { message = "未检测到旧版词库文件。"; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            temporary = Path.Combine(Path.GetDirectoryName(targetPath)!, $".lexi-migration-{Guid.NewGuid():N}.sqlite3");
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString()))
            {
                source.Open();
                destination.Open();
                // The online backup API includes committed WAL pages as one consistent snapshot.
                source.BackupDatabase(destination);
                using var check = destination.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("旧词库完整性检查未通过。");
                check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('words','review_logs','app_settings')";
                if (Convert.ToInt32(check.ExecuteScalar()) != 3)
                    throw new InvalidDataException("旧词库缺少必需的数据表。");
                check.CommandText = "SELECT id,word,phonetic,translation,definition,notes,stage,status,created_at,learning_start_date,next_review_date,last_reviewed_at,review_count FROM words LIMIT 0";
                using (var reader = check.ExecuteReader()) { }
                check.CommandText = "SELECT id,word_id,action,old_stage,new_stage,old_status,new_status,old_next_review_date,new_next_review_date,old_learning_start_date,new_learning_start_date,log_time,log_date FROM review_logs LIMIT 0";
                using (var reader = check.ExecuteReader()) { }
                check.CommandText = "SELECT id,settings_json FROM app_settings LIMIT 0";
                using (var reader = check.ExecuteReader()) { }
                DatabaseSafety.ValidateApplicationSchema(destination);
                // Consolidate only the destination. Never checkpoint or modify the source.
                check.CommandText = "PRAGMA journal_mode=DELETE";
                check.ExecuteScalar();
            }
            File.Move(temporary, targetPath, false);
            message = "已安全复制旧版生词本、复习记录和配置；旧版数据保持不变。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"迁移失败，未创建新词库，请保留旧版数据后重试：{ex.Message}";
            return false;
        }
        finally
        {
            if (temporary != null)
                foreach (var path in new[] { temporary, temporary + "-wal", temporary + "-shm", temporary + "-journal" })
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    public VocabularyService(string? customDbPath = null, ISecureSecretStore? secretStore = null)
    {
        _dbPath = Path.GetFullPath(customDbPath ?? GetDefaultDatabasePath());
        _secretStore = secretStore ?? SecretStoreFactory.Create(Path.GetDirectoryName(_dbPath)!);
        DatabaseSafety.RejectOrphanedSidecars(_dbPath);
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Explicit/custom paths are isolated: never import the real user's database.
        if (customDbPath == null && !File.Exists(_dbPath)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")))
        {
            var legacyExists = File.Exists(GetLegacyDatabasePath());
            var migrated = TryMigrateLegacyDatabase(_dbPath, out var message);
            MigrationMessage = message;
            if (legacyExists && !migrated && !File.Exists(_dbPath))
                throw new InvalidOperationException(message);
        }

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        // Verify the complete database (including a legitimate WAL) before any schema
        // change. Never auto-delete sidecars or replace a broken DB with an empty one.
        DatabaseSafety.ValidateExisting(_dbPath);
        _connection = new SqliteConnection(connStr);
        try
        {
            _connection.Open();
            using var count = _connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='words'";
            if (Convert.ToInt32(count.ExecuteScalar()) > 0)
            {
                DatabaseSafety.ValidateApplicationSchema(_connection);
                LastBackupPath = DatabaseSafety.CreateBackup(_connection, _dbPath);
            }
            InitializeDatabase();
            DatabaseSafety.Validate(_connection);
        }
        catch { _connection.Dispose(); throw; }
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = DELETE;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS words (
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

            CREATE TABLE IF NOT EXISTS review_logs (
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
                log_date TEXT NOT NULL,
                FOREIGN KEY (word_id) REFERENCES words(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                settings_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS review_snapshots (
                log_id INTEGER PRIMARY KEY REFERENCES review_logs(id) ON DELETE CASCADE,
                last_reviewed_at TEXT,
                review_count INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_words_word ON words(word COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_words_status_next ON words(status, next_review_date);
            CREATE INDEX IF NOT EXISTS idx_review_logs_word_id ON review_logs(word_id);
            CREATE INDEX IF NOT EXISTS idx_review_logs_date_action ON review_logs(word_id, log_date, action);
        ";
        cmd.ExecuteNonQuery();

        MigrateArchiveSchema();
        MigrateQuoteSchema();
        MigrateMemorySchema();
    }

    public List<WordItem> GetAllWords()
    {
        var list = new List<WordItem>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT w.id, w.word, w.phonetic, w.translation, w.definition, w.stage, w.status,
                   w.created_at, w.learning_start_date, w.next_review_date, w.last_reviewed_at, w.review_count, w.notes,
                   a.uuid, a.source_type, a.source_title, a.source_excerpt, a.tags_json, a.encounter_count, a.revision,
                   a.created_at_utc, a.updated_at_utc, a.last_encountered_at_utc, a.ai_json
            FROM words w
            LEFT JOIN word_archives a ON w.id = a.word_id
            ORDER BY w.id DESC
        ";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string S(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
            int I(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
            long L(int i) => reader.GetInt64(i);

            var item = new WordItem
            {
                Id = L(0),
                Word = S(1),
                Phonetic = S(2),
                Translation = S(3),
                Definition = S(4),
                Stage = I(5),
                Status = S(6),
                CreatedAt = S(7),
                LearningStartDate = S(8),
                NextReviewDate = reader.IsDBNull(9) ? null : reader.GetString(9),
                LastReviewedAt = reader.IsDBNull(10) ? null : reader.GetString(10),
                ReviewCount = I(11),
                Notes = S(12)
            };

            if (!reader.IsDBNull(13))
            {
                string[] tags;
                try
                {
                    tags = JsonSerializer.Deserialize<string[]>(reader.GetString(17)) ?? throw new InvalidDataException("标签数据为空。");
                    ValidateArchiveTags(tags);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                { throw new InvalidDataException("标签档案损坏，已停止读取以避免覆盖原数据。", ex); }
                item.Archive = new ArchiveMetadata
                {
                    Uuid = S(13),
                    SourceType = S(14),
                    SourceTitle = S(15),
                    SourceExcerpt = S(16),
                    Tags = tags,
                    EncounterCount = reader.IsDBNull(18) ? 1 : reader.GetInt32(18),
                    Revision = reader.IsDBNull(19) ? 1 : reader.GetInt32(19),
                    CreatedAtUtc = S(20),
                    UpdatedAtUtc = S(21),
                    LastEncounteredAtUtc = S(22)
                };
            }
            else throw new InvalidDataException("词条缺少档案元数据，已停止读取。");

            if (!reader.IsDBNull(23))
            {
                try
                {
                    item.AiResult = JsonSerializer.Deserialize<LlmResult>(reader.GetString(23), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidDataException("AI 档案为空。");
                    ValidateArchiveAi(item.AiResult);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                { throw new InvalidDataException("AI 档案损坏，已停止读取以避免覆盖原数据。", ex); }
            }
            list.Add(item);
        }

        return list;
    }

    public void AddWord(string word, string phonetic, string translation, string definition)
    {
        var cleanWord = word.Trim();
        if (string.IsNullOrEmpty(cleanWord))
        {
            throw new ArgumentException("单词不能为空。", nameof(word));
        }

        phonetic = phonetic.Trim();
        translation = translation.Trim();
        definition = definition.Trim();

        using var checkCmd = _connection.CreateCommand();
        checkCmd.CommandText = "SELECT id, phonetic, translation, definition FROM words WHERE word = $word COLLATE NOCASE";
        checkCmd.Parameters.AddWithValue("$word", cleanWord);

        using var reader = checkCmd.ExecuteReader();
        if (reader.Read())
        {
            var id = reader.GetInt64(0);
            var existingPhon = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var existingTrans = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var existingDef = reader.IsDBNull(3) ? "" : reader.GetString(3);

            reader.Close();

            var newPhon = string.IsNullOrWhiteSpace(existingPhon) ? phonetic : existingPhon;
            var newTrans = string.IsNullOrWhiteSpace(existingTrans) ? translation : existingTrans;
            var newDef = string.IsNullOrWhiteSpace(existingDef) ? definition : existingDef;

            using var updateCmd = _connection.CreateCommand();
            updateCmd.CommandText = "UPDATE words SET phonetic = $phon, translation = $trans, definition = $def WHERE id = $id";
            updateCmd.Parameters.AddWithValue("$phon", newPhon);
            updateCmd.Parameters.AddWithValue("$trans", newTrans);
            updateCmd.Parameters.AddWithValue("$def", newDef);
            updateCmd.Parameters.AddWithValue("$id", id);
            updateCmd.ExecuteNonQuery();
            BackupCommittedState();
            return;
        }

        reader.Close();

        var today = DateTime.Today;
        var todayStr = today.ToString("yyyy-MM-dd");
        var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var nextReviewStr = today.AddDays(StageOffsets[0]).ToString("yyyy-MM-dd");

        using var tx = _connection.BeginTransaction();

        using var insertCmd = _connection.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = @"
            INSERT INTO words (
                word, phonetic, translation, definition, notes,
                stage, status, created_at, learning_start_date,
                next_review_date, last_reviewed_at, review_count
            ) VALUES ($word, $phon, $trans, $def, '', 0, 'learning', $createdAt, $start, $nextReview, NULL, 0)
        ";
        insertCmd.Parameters.AddWithValue("$word", cleanWord);
        insertCmd.Parameters.AddWithValue("$phon", phonetic);
        insertCmd.Parameters.AddWithValue("$trans", translation);
        insertCmd.Parameters.AddWithValue("$def", definition);
        insertCmd.Parameters.AddWithValue("$createdAt", nowStr);
        insertCmd.Parameters.AddWithValue("$start", todayStr);
        insertCmd.Parameters.AddWithValue("$nextReview", nextReviewStr);
        insertCmd.ExecuteNonQuery();

        using var idCmd = _connection.CreateCommand();
        idCmd.Transaction = tx;
        idCmd.CommandText = "SELECT last_insert_rowid()";
        var wordId = (long)idCmd.ExecuteScalar()!;

        var nowUtc = DateTime.UtcNow.ToString("o");
        var uuid = GenerateStableUuid(cleanWord);
        using var archiveCmd = _connection.CreateCommand();
        archiveCmd.Transaction = tx;
        archiveCmd.CommandText = @"
            INSERT INTO word_archives (
                word_id, uuid, source_type, source_title, source_excerpt,
                tags_json, encounter_count, revision, created_at_utc,
                updated_at_utc, last_encountered_at_utc, ai_json
            ) VALUES (
                $wordId, $uuid, '', '', '',
                '[]', 1, 1, $nowUtc,
                $nowUtc, $nowUtc, NULL
            )
            ON CONFLICT(word_id) DO NOTHING;
        ";
        archiveCmd.Parameters.AddWithValue("$wordId", wordId);
        archiveCmd.Parameters.AddWithValue("$uuid", uuid);
        archiveCmd.Parameters.AddWithValue("$nowUtc", nowUtc);
        archiveCmd.ExecuteNonQuery();

        using var logCmd = _connection.CreateCommand();
        logCmd.Transaction = tx;
        logCmd.CommandText = @"
            INSERT INTO review_logs (
                word_id, action, old_stage, new_stage, old_status, new_status,
                old_next_review_date, new_next_review_date,
                old_learning_start_date, new_learning_start_date,
                log_time, log_date
            ) VALUES ($wordId, 'add', NULL, 0, NULL, 'learning', NULL, $nextReview, NULL, $start, $now, $today)
        ";
        logCmd.Parameters.AddWithValue("$wordId", wordId);
        logCmd.Parameters.AddWithValue("$nextReview", nextReviewStr);
        logCmd.Parameters.AddWithValue("$start", todayStr);
        logCmd.Parameters.AddWithValue("$now", nowStr);
        logCmd.Parameters.AddWithValue("$today", todayStr);
        logCmd.ExecuteNonQuery();

        tx.Commit();
        BackupCommittedState();
    }

    public void EditWord(long id, string translation)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE words SET translation = $trans WHERE id = $id";
        cmd.Parameters.AddWithValue("$trans", translation.Trim());
        cmd.Parameters.AddWithValue("$id", id);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
        {
            throw new InvalidOperationException($"未找到 ID 为 {id} 的单词。");
        }
        BackupCommittedState();
    }

    public void ExecuteBatch(IEnumerable<long> ids, string action, int? targetStage = null)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0)
        {
            throw new ArgumentException("未提供任何单词 ID。", nameof(ids));
        }

        string[] validActions = ["master", "delete", "today", "stage", "review", "restart"];
        if (!validActions.Contains(action))
        {
            throw new ArgumentException($"不支持的批量操作 '{action}'。有效操作: {string.Join(", ", validActions)}");
        }

        if (action == "stage")
        {
            if (!targetStage.HasValue || targetStage.Value < 0 || targetStage.Value > MaxStage)
            {
                throw new ArgumentException($"调整阶段必须是 0 到 {MaxStage} 之间的整数。");
            }
        }

        var today = DateTime.Today;
        var todayStr = today.ToString("yyyy-MM-dd");
        var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using var tx = _connection.BeginTransaction();

        // 1. Validate that ALL IDs exist in database
        var wordRows = new List<WordItem>();
        foreach (var id in idList)
        {
            using var queryCmd = _connection.CreateCommand();
            queryCmd.Transaction = tx;
            queryCmd.CommandText = @"
                SELECT id, word, phonetic, translation, definition, stage, status,
                       learning_start_date, next_review_date, last_reviewed_at, review_count
                FROM words WHERE id = $id
            ";
            queryCmd.Parameters.AddWithValue("$id", id);

            using var reader = queryCmd.ExecuteReader();
            if (!reader.Read())
            {
                tx.Rollback();
                throw new InvalidOperationException($"单词 ID {id} 不存在。批量操作已中止，未修改任何数据。");
            }

            string S(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
            int I(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);

            wordRows.Add(new WordItem
            {
                Id = reader.GetInt64(0),
                Word = S(1),
                Phonetic = S(2),
                Translation = S(3),
                Definition = S(4),
                Stage = I(5),
                Status = S(6),
                LearningStartDate = S(7),
                NextReviewDate = reader.IsDBNull(8) ? null : reader.GetString(8),
                LastReviewedAt = reader.IsDBNull(9) ? null : reader.GetString(9),
                ReviewCount = I(10)
            });
        }

        // 2. Perform action
        switch (action)
        {
            case "delete":
                foreach (var item in wordRows)
                {
                    using var delCmd = _connection.CreateCommand();
                    delCmd.Transaction = tx;
                    delCmd.CommandText = "DELETE FROM words WHERE id = $id";
                    delCmd.Parameters.AddWithValue("$id", item.Id);
                    delCmd.ExecuteNonQuery();
                }
                break;

            case "master":
                foreach (var item in wordRows)
                {
                    using var updateCmd = _connection.CreateCommand();
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = @"
                        UPDATE words SET stage = $maxStage, status = 'mastered', next_review_date = NULL, last_reviewed_at = $now WHERE id = $id
                    ";
                    updateCmd.Parameters.AddWithValue("$maxStage", MaxStage);
                    updateCmd.Parameters.AddWithValue("$now", nowStr);
                    updateCmd.Parameters.AddWithValue("$id", item.Id);
                    updateCmd.ExecuteNonQuery();

                    using var logCmd = _connection.CreateCommand();
                    logCmd.Transaction = tx;
                    logCmd.CommandText = @"
                        INSERT INTO review_logs (
                            word_id, action, old_stage, new_stage, old_status, new_status,
                            old_next_review_date, new_next_review_date,
                            old_learning_start_date, new_learning_start_date,
                            log_time, log_date
                        ) VALUES ($wordId, 'batch_master', $oldStage, $newStage, $oldStatus, 'mastered', $oldNext, NULL, $oldStart, $newStart, $now, $today)
                    ";
                    logCmd.Parameters.AddWithValue("$wordId", item.Id);
                    logCmd.Parameters.AddWithValue("$oldStage", item.Stage);
                    logCmd.Parameters.AddWithValue("$newStage", MaxStage);
                    logCmd.Parameters.AddWithValue("$oldStatus", item.Status);
                    logCmd.Parameters.AddWithValue("$oldNext", (object?)item.NextReviewDate ?? DBNull.Value);
                    logCmd.Parameters.AddWithValue("$oldStart", item.LearningStartDate);
                    logCmd.Parameters.AddWithValue("$newStart", item.LearningStartDate);
                    logCmd.Parameters.AddWithValue("$now", nowStr);
                    logCmd.Parameters.AddWithValue("$today", todayStr);
                    logCmd.ExecuteNonQuery();
                    using var masterSnapshot = _connection.CreateCommand();
                    masterSnapshot.Transaction = tx;
                    masterSnapshot.CommandText = "INSERT INTO review_snapshots(log_id,last_reviewed_at,review_count) VALUES(last_insert_rowid(),$last,$count)";
                    masterSnapshot.Parameters.AddWithValue("$last", (object?)item.LastReviewedAt ?? DBNull.Value);
                    masterSnapshot.Parameters.AddWithValue("$count", item.ReviewCount);
                    masterSnapshot.ExecuteNonQuery();
                }
                break;

            case "restart":
                var restartDue = today.AddDays(StageOffsets[0]);
                var restartNextReview = restartDue.ToString("yyyy-MM-dd");
                foreach (var item in wordRows)
                {
                    using var updateCmd = _connection.CreateCommand();
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = @"
                        UPDATE words SET stage = 0, status = 'learning', learning_start_date = $today, next_review_date = $nextReview WHERE id = $id
                    ";
                    updateCmd.Parameters.AddWithValue("$today", todayStr);
                    updateCmd.Parameters.AddWithValue("$nextReview", restartNextReview);
                    updateCmd.Parameters.AddWithValue("$id", item.Id);
                    updateCmd.ExecuteNonQuery();

                    using var logCmd = _connection.CreateCommand();
                    logCmd.Transaction = tx;
                    logCmd.CommandText = @"
                        INSERT INTO review_logs (
                            word_id, action, old_stage, new_stage, old_status, new_status,
                            old_next_review_date, new_next_review_date,
                            old_learning_start_date, new_learning_start_date,
                            log_time, log_date
                        ) VALUES ($wordId, 'batch_restart', $oldStage, 0, $oldStatus, 'learning', $oldNext, $newNext, $oldStart, $newStart, $now, $today)
                    ";
                    logCmd.Parameters.AddWithValue("$wordId", item.Id);
                    logCmd.Parameters.AddWithValue("$oldStage", item.Stage);
                    logCmd.Parameters.AddWithValue("$oldStatus", item.Status);
                    logCmd.Parameters.AddWithValue("$oldNext", (object?)item.NextReviewDate ?? DBNull.Value);
                    logCmd.Parameters.AddWithValue("$newNext", restartNextReview);
                    logCmd.Parameters.AddWithValue("$oldStart", item.LearningStartDate);
                    logCmd.Parameters.AddWithValue("$newStart", todayStr);
                    logCmd.Parameters.AddWithValue("$now", nowStr);
                    logCmd.Parameters.AddWithValue("$today", todayStr);
                    logCmd.ExecuteNonQuery();
                    // 显式手动重排同样作用于已有 FSRS 卡（见 SyncCardDueToLocalDate）。
                    SyncCardDueToLocalDate(tx, item.Id, restartDue);
                }
                break;

            case "today":
                // 裁定 O-1（findings.md §P）的**显式手动覆盖**时间戳。
                // 格式必须与 VocabularyService.Memory.cs 的 UtcText 逐字节一致（UTC ISO-8601 "O"，
                // 定长 28 字符 + 尾缀 Z）：QueryDue 依赖定长字符串序比较，
                // 格式不一致会静默查不到到期。DateTime.UtcNow 的 Kind 已是 Utc，
                // UtcText 里的 ToUniversalTime() 对它是恒等变换，故二者输出相同。
                var todayOverrideUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                foreach (var item in wordRows)
                {
                    var phase = item.Stage >= MaxStage ? 0 : item.Stage;
                    var startDate = today.AddDays(-StageOffsets[phase]);
                    var startDateStr = startDate.ToString("yyyy-MM-dd");

                    using var updateCmd = _connection.CreateCommand();
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = @"
                        UPDATE words SET stage = $stage, status = 'learning', learning_start_date = $startDate, next_review_date = $today WHERE id = $id
                    ";
                    updateCmd.Parameters.AddWithValue("$stage", phase);
                    updateCmd.Parameters.AddWithValue("$startDate", startDateStr);
                    updateCmd.Parameters.AddWithValue("$today", todayStr);
                    updateCmd.Parameters.AddWithValue("$id", item.Id);
                    updateCmd.ExecuteNonQuery();

                    using var logCmd = _connection.CreateCommand();
                    logCmd.Transaction = tx;
                    logCmd.CommandText = @"
                        INSERT INTO review_logs (
                            word_id, action, old_stage, new_stage, old_status, new_status,
                            old_next_review_date, new_next_review_date,
                            old_learning_start_date, new_learning_start_date,
                            log_time, log_date
                        ) VALUES ($wordId, 'batch_today', $oldStage, $newStage, $oldStatus, 'learning', $oldNext, $today, $oldStart, $newStart, $now, $today)
                    ";
                    logCmd.Parameters.AddWithValue("$wordId", item.Id);
                    logCmd.Parameters.AddWithValue("$oldStage", item.Stage);
                    logCmd.Parameters.AddWithValue("$newStage", phase);
                    logCmd.Parameters.AddWithValue("$oldStatus", item.Status);
                    logCmd.Parameters.AddWithValue("$oldNext", (object?)item.NextReviewDate ?? DBNull.Value);
                    logCmd.Parameters.AddWithValue("$today", todayStr);
                    logCmd.Parameters.AddWithValue("$oldStart", item.LearningStartDate);
                    logCmd.Parameters.AddWithValue("$newStart", startDateStr);
                    logCmd.Parameters.AddWithValue("$now", nowStr);
                    logCmd.ExecuteNonQuery();

                    // ---------------------------------------------------------------------------
                    // 裁定 O-1（findings.md §P）：`today`（置入今日复习）是**用户主动发出的手动
                    // 排期命令**，属于用户明文要求保留的「手动管理能力」，因此必须继续生效。
                    //
                    // 这是**显式手动覆盖（administrative override）**，与评分路径的自动写截然不同：
                    // 评分路径（MarkUnsure / MarkForgot / ExecuteBatch("review")）**仍然不得**覆盖
                    // next_review_at_utc —— 用户禁止的是**自动**评分路径静默改写 due，本条不受影响。
                    //
                    // 冻结口径：
                    //  · 词身份 = "archive:" + word_archives.uuid，**不是** words.id
                    //    （rowid 在恢复备份后会重排，uuid 才是稳定身份；见 WordKeyResolver.FromArchiveUuid
                    //    与 MemoryModels.cs 的 WordKey.Archive(uuid)）。此处从 word_archives 反查 uuid。
                    //  · next_review_at_utc 用 todayOverrideUtc（与 VocabularyService.Memory.cs 的
                    //    UtcText 逐字节同一格式）。
                    //  · 必须复用本 action 已开启的事务 tx：Microsoft.Data.Sqlite 对带事务的连接
                    //    执行命令要求显式设置 cmd.Transaction，否则抛「命令需要事务」。
                    //  · 该词没有档案行、或没有 fsrs_cards 行时，下面这条 UPDATE 匹配 0 行 ——
                    //    不做任何额外操作，保持原行为（不建卡、不动卡）。
                    // ---------------------------------------------------------------------------
                    using var archiveKeyCmd = _connection.CreateCommand();
                    archiveKeyCmd.Transaction = tx;
                    archiveKeyCmd.CommandText = "SELECT uuid FROM word_archives WHERE word_id = $id";
                    archiveKeyCmd.Parameters.AddWithValue("$id", item.Id);
                    if (archiveKeyCmd.ExecuteScalar() is string archiveUuid && !string.IsNullOrWhiteSpace(archiveUuid))
                    {
                        using var cardOverrideCmd = _connection.CreateCommand();
                        cardOverrideCmd.Transaction = tx;
                        cardOverrideCmd.CommandText = @"
                            UPDATE fsrs_cards SET next_review_at_utc = $nowUtc WHERE word_key = $wordKey
                        ";
                        cardOverrideCmd.Parameters.AddWithValue("$nowUtc", todayOverrideUtc);
                        cardOverrideCmd.Parameters.AddWithValue("$wordKey", WordKey.Archive(archiveUuid).Key);
                        cardOverrideCmd.ExecuteNonQuery();
                    }
                }
                break;

            case "stage":
                var stageVal = targetStage ?? 0;
                foreach (var item in wordRows)
                {
                    if (stageVal >= MaxStage)
                    {
                        using var updateCmd = _connection.CreateCommand();
                        updateCmd.Transaction = tx;
                        updateCmd.CommandText = "UPDATE words SET stage = $maxStage, status = 'mastered', next_review_date = NULL WHERE id = $id";
                        updateCmd.Parameters.AddWithValue("$maxStage", MaxStage);
                        updateCmd.Parameters.AddWithValue("$id", item.Id);
                        updateCmd.ExecuteNonQuery();

                        using var logCmd = _connection.CreateCommand();
                        logCmd.Transaction = tx;
                        logCmd.CommandText = @"
                            INSERT INTO review_logs (
                                word_id, action, old_stage, new_stage, old_status, new_status,
                                old_next_review_date, new_next_review_date,
                                old_learning_start_date, new_learning_start_date,
                                log_time, log_date
                            ) VALUES ($wordId, 'batch_stage', $oldStage, $newStage, $oldStatus, 'mastered', $oldNext, NULL, $oldStart, $newStart, $now, $today)
                        ";
                        logCmd.Parameters.AddWithValue("$wordId", item.Id);
                        logCmd.Parameters.AddWithValue("$oldStage", item.Stage);
                        logCmd.Parameters.AddWithValue("$newStage", MaxStage);
                        logCmd.Parameters.AddWithValue("$oldStatus", item.Status);
                        logCmd.Parameters.AddWithValue("$oldNext", (object?)item.NextReviewDate ?? DBNull.Value);
                        logCmd.Parameters.AddWithValue("$oldStart", item.LearningStartDate);
                        logCmd.Parameters.AddWithValue("$newStart", item.LearningStartDate);
                        logCmd.Parameters.AddWithValue("$now", nowStr);
                        logCmd.Parameters.AddWithValue("$today", todayStr);
                        logCmd.ExecuteNonQuery();
                    }
                    else
                    {
                        var nextReviewDate = today.AddDays(StageOffsets[stageVal]);
                        var nextReview = nextReviewDate.ToString("yyyy-MM-dd");

                        using var updateCmd = _connection.CreateCommand();
                        updateCmd.Transaction = tx;
                        updateCmd.CommandText = @"
                            UPDATE words SET stage = $stage, status = 'learning', learning_start_date = $today, next_review_date = $nextReview WHERE id = $id
                        ";
                        updateCmd.Parameters.AddWithValue("$stage", stageVal);
                        updateCmd.Parameters.AddWithValue("$today", todayStr);
                        updateCmd.Parameters.AddWithValue("$nextReview", nextReview);
                        updateCmd.Parameters.AddWithValue("$id", item.Id);
                        updateCmd.ExecuteNonQuery();

                        using var logCmd = _connection.CreateCommand();
                        logCmd.Transaction = tx;
                        logCmd.CommandText = @"
                            INSERT INTO review_logs (
                                word_id, action, old_stage, new_stage, old_status, new_status,
                                old_next_review_date, new_next_review_date,
                                old_learning_start_date, new_learning_start_date,
                                log_time, log_date
                            ) VALUES ($wordId, 'batch_stage', $oldStage, $newStage, $oldStatus, 'learning', $oldNext, $nextReview, $oldStart, $newStart, $now, $today)
                        ";
                        logCmd.Parameters.AddWithValue("$wordId", item.Id);
                        logCmd.Parameters.AddWithValue("$oldStage", item.Stage);
                        logCmd.Parameters.AddWithValue("$newStage", stageVal);
                        logCmd.Parameters.AddWithValue("$oldStatus", item.Status);
                        logCmd.Parameters.AddWithValue("$oldNext", (object?)item.NextReviewDate ?? DBNull.Value);
                        logCmd.Parameters.AddWithValue("$nextReview", nextReview);
                        logCmd.Parameters.AddWithValue("$oldStart", item.LearningStartDate);
                        logCmd.Parameters.AddWithValue("$newStart", todayStr);
                        logCmd.Parameters.AddWithValue("$now", nowStr);
                        logCmd.Parameters.AddWithValue("$today", todayStr);
                        logCmd.ExecuteNonQuery();
                        // 显式手动调整阶段同样作用于已有 FSRS 卡（见 SyncCardDueToLocalDate）。
                        SyncCardDueToLocalDate(tx, item.Id, nextReviewDate);
                    }
                }
                break;

            case "review":
                foreach (var item in wordRows)
                {
                    // Check same-day review idempotency
                    using var checkCmd = _connection.CreateCommand();
                    checkCmd.Transaction = tx;
                    checkCmd.CommandText = @"
                        SELECT EXISTS(SELECT 1 FROM review_logs WHERE word_id = $wordId AND action = 'batch_review' AND log_date = $today)
                    ";
                    checkCmd.Parameters.AddWithValue("$wordId", item.Id);
                    checkCmd.Parameters.AddWithValue("$today", todayStr);
                    var alreadyReviewedToday = (long)checkCmd.ExecuteScalar()! != 0;

                    if (alreadyReviewedToday || item.Status == "mastered")
                    {
                        continue;
                    }

                    var newStage = Math.Min(item.Stage + 1, MaxStage - 1);
                    var newReviewCount = item.ReviewCount + 1;

                    if (newStage >= MaxStage)
                    {
                        using var updateCmd = _connection.CreateCommand();
                        updateCmd.Transaction = tx;
                        updateCmd.CommandText = @"
                            UPDATE words SET stage = $maxStage, status = 'mastered', next_review_date = NULL,
                                             last_reviewed_at = $now, review_count = $revCount WHERE id = $id
                        ";
                        updateCmd.Parameters.AddWithValue("$maxStage", MaxStage);
                        updateCmd.Parameters.AddWithValue("$now", nowStr);
                        updateCmd.Parameters.AddWithValue("$revCount", newReviewCount);
                        updateCmd.Parameters.AddWithValue("$id", item.Id);
                        updateCmd.ExecuteNonQuery();

                        using var logCmd = _connection.CreateCommand();
                        logCmd.Transaction = tx;
                        logCmd.CommandText = @"
                            INSERT INTO review_logs (
                                word_id, action, old_stage, new_stage, old_status, new_status,
                                old_next_review_date, new_next_review_date,
                                old_learning_start_date, new_learning_start_date,
                                log_time, log_date
                            ) VALUES ($wordId, 'batch_review', $oldStage, $newStage, $oldStatus, 'mastered', $oldNext, NULL, $oldStart, $newStart, $now, $today)
                        ";
                        logCmd.Parameters.AddWithValue("$wordId", item.Id);
                        logCmd.Parameters.AddWithValue("$oldStage", item.Stage);
                        logCmd.Parameters.AddWithValue("$newStage", MaxStage);
                        logCmd.Parameters.AddWithValue("$oldStatus", item.Status);
                        logCmd.Parameters.AddWithValue("$oldNext", (object?)item.NextReviewDate ?? DBNull.Value);
                        logCmd.Parameters.AddWithValue("$oldStart", item.LearningStartDate);
                        logCmd.Parameters.AddWithValue("$newStart", item.LearningStartDate);
                        logCmd.Parameters.AddWithValue("$now", nowStr);
                        logCmd.Parameters.AddWithValue("$today", todayStr);
                        logCmd.ExecuteNonQuery();
                    }
                    else
                    {
                        DateTime? scheduledDate = null;
                        if (!string.IsNullOrEmpty(item.NextReviewDate) && DateTime.TryParse(item.NextReviewDate, out var parsedNext))
                        {
                            scheduledDate = parsedNext;
                        }

                        var isLate = scheduledDate.HasValue && today > scheduledDate.Value;

                        string newStartStr;
                        string newNextReviewStr;

                        if (isLate)
                        {
                            var deltaDays = StageOffsets[newStage] - StageOffsets[item.Stage];
                            var nextDate = today.AddDays(deltaDays);
                            if (nextDate <= today)
                            {
                                nextDate = today.AddDays(1);
                            }
                            var newStart = nextDate.AddDays(-StageOffsets[newStage]);
                            newStartStr = newStart.ToString("yyyy-MM-dd");
                            newNextReviewStr = nextDate.ToString("yyyy-MM-dd");
                        }
                        else
                        {
                            var startDate = DateTime.TryParse(item.LearningStartDate, out var s) ? s : today;
                            var nextDate = startDate.AddDays(StageOffsets[newStage]);
                            if (nextDate <= today)
                            {
                                nextDate = today.AddDays(1);
                            }
                            newStartStr = item.LearningStartDate;
                            newNextReviewStr = nextDate.ToString("yyyy-MM-dd");
                        }

                        using var updateCmd = _connection.CreateCommand();
                        updateCmd.Transaction = tx;
                        updateCmd.CommandText = @"
                            UPDATE words SET stage = $stage, status = 'learning', learning_start_date = $start,
                                             next_review_date = $nextReview, last_reviewed_at = $now,
                                             review_count = $revCount WHERE id = $id
                        ";
                        updateCmd.Parameters.AddWithValue("$stage", newStage);
                        updateCmd.Parameters.AddWithValue("$start", newStartStr);
                        updateCmd.Parameters.AddWithValue("$nextReview", newNextReviewStr);
                        updateCmd.Parameters.AddWithValue("$now", nowStr);
                        updateCmd.Parameters.AddWithValue("$revCount", newReviewCount);
                        updateCmd.Parameters.AddWithValue("$id", item.Id);
                        updateCmd.ExecuteNonQuery();

                        using var logCmd = _connection.CreateCommand();
                        logCmd.Transaction = tx;
                        logCmd.CommandText = @"
                            INSERT INTO review_logs (
                                word_id, action, old_stage, new_stage, old_status, new_status,
                                old_next_review_date, new_next_review_date,
                                old_learning_start_date, new_learning_start_date,
                                log_time, log_date
                            ) VALUES ($wordId, 'batch_review', $oldStage, $newStage, $oldStatus, 'learning', $oldNext, $newNext, $oldStart, $newStart, $now, $today)
                        ";
                        logCmd.Parameters.AddWithValue("$wordId", item.Id);
                        logCmd.Parameters.AddWithValue("$oldStage", item.Stage);
                        logCmd.Parameters.AddWithValue("$newStage", newStage);
                        logCmd.Parameters.AddWithValue("$oldStatus", item.Status);
                        logCmd.Parameters.AddWithValue("$oldNext", (object?)item.NextReviewDate ?? DBNull.Value);
                        logCmd.Parameters.AddWithValue("$newNext", newNextReviewStr);
                        logCmd.Parameters.AddWithValue("$oldStart", item.LearningStartDate);
                        logCmd.Parameters.AddWithValue("$newStart", newStartStr);
                        logCmd.Parameters.AddWithValue("$now", nowStr);
                        logCmd.Parameters.AddWithValue("$today", todayStr);
                        logCmd.ExecuteNonQuery();
                    }
                    using var snapshot = _connection.CreateCommand();
                    snapshot.Transaction = tx;
                    snapshot.CommandText = "INSERT INTO review_snapshots(log_id,last_reviewed_at,review_count) VALUES(last_insert_rowid(),$last,$count)";
                    snapshot.Parameters.AddWithValue("$last", (object?)item.LastReviewedAt ?? DBNull.Value);
                    snapshot.Parameters.AddWithValue("$count", item.ReviewCount);
                    snapshot.ExecuteNonQuery();
                }
                break;
        }

        tx.Commit();
        BackupCommittedState();
    }

    public bool UndoLastReview(long wordId) => UndoLastAction(wordId, includeMaster: false);

    // Only the learning-page undo explicitly includes its "mastered" action;
    // ordinary archive undo retains the existing history boundary.
    public bool UndoLastLearningAction(long wordId) => UndoLastAction(wordId, includeMaster: true);

    private bool UndoLastAction(long wordId, bool includeMaster)
    {
        using var tx = _connection.BeginTransaction();

        using var queryCmd = _connection.CreateCommand();
        queryCmd.Transaction = tx;
        queryCmd.CommandText = @"
            SELECT l.id, l.old_stage, l.old_status, l.old_next_review_date, l.old_learning_start_date,
                   s.last_reviewed_at, s.review_count, s.log_id
            FROM review_logs l LEFT JOIN review_snapshots s ON s.log_id=l.id
            WHERE l.word_id = $wordId AND (l.action IN ('batch_review', 'unfamiliar', 'unsure', 'forgot')
                  OR ($includeMaster = 1 AND l.action = 'batch_master'))
              AND l.id = (SELECT MAX(id) FROM review_logs WHERE word_id=$wordId)
            LIMIT 1
        ";
        queryCmd.Parameters.AddWithValue("$wordId", wordId);
        queryCmd.Parameters.AddWithValue("$includeMaster", includeMaster ? 1 : 0);

        using var reader = queryCmd.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var logId = reader.GetInt64(0);
        int? oldStage = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        string? oldStatus = reader.IsDBNull(2) ? null : reader.GetString(2);
        string? oldNext = reader.IsDBNull(3) ? null : reader.GetString(3);
        string? oldStart = reader.IsDBNull(4) ? null : reader.GetString(4);
        string? oldLastReviewed = reader.IsDBNull(5) ? null : reader.GetString(5);
        int? oldReviewCount = reader.IsDBNull(6) ? null : reader.GetInt32(6);
        var hasSnapshot = !reader.IsDBNull(7);
        reader.Close();

        // Legacy logs do not have snapshots. Reconstruct the last timestamp from the
        // preceding actions that changed it (both review and mark-mastered do so).
        if (!hasSnapshot)
        {
            using var previous = _connection.CreateCommand();
            previous.Transaction = tx;
            previous.CommandText = "SELECT log_time FROM review_logs WHERE word_id=$wordId AND id<$id AND action IN ('batch_review','batch_master') ORDER BY id DESC LIMIT 1";
            previous.Parameters.AddWithValue("$wordId", wordId);
            previous.Parameters.AddWithValue("$id", logId);
            oldLastReviewed = previous.ExecuteScalar() as string;
        }

        if (oldStage.HasValue && !string.IsNullOrEmpty(oldStatus))
        {
            using var updateCmd = _connection.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = @"
                UPDATE words SET stage = $stage, status = $status,
                                 next_review_date = $nextReview,
                                 learning_start_date = COALESCE($start, learning_start_date),
                                 last_reviewed_at = $last,
                                 review_count = COALESCE($count, MAX(0, review_count - 1))
                WHERE id = $wordId
            ";
            updateCmd.Parameters.AddWithValue("$stage", oldStage.Value);
            updateCmd.Parameters.AddWithValue("$status", oldStatus);
            updateCmd.Parameters.AddWithValue("$nextReview", (object?)oldNext ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("$start", (object?)oldStart ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("$wordId", wordId);
            updateCmd.Parameters.AddWithValue("$last", (object?)oldLastReviewed ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("$count", (object?)oldReviewCount ?? DBNull.Value);
            updateCmd.ExecuteNonQuery();

            using var delCmd = _connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM review_logs WHERE id = $id";
            delCmd.Parameters.AddWithValue("$id", logId);
            delCmd.ExecuteNonQuery();

            tx.Commit();
            BackupCommittedState();
            return true;
        }

        return false;
    }

    /// <summary>Undo the newest review still eligible for undo, regardless of list selection.</summary>
    public bool UndoMostRecentReview()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"SELECT l.word_id FROM review_logs l
            WHERE l.action IN ('batch_review', 'unfamiliar', 'unsure', 'forgot')
              AND l.id=(SELECT MAX(id) FROM review_logs WHERE word_id=l.word_id)
            ORDER BY l.id DESC LIMIT 1";
        var id = cmd.ExecuteScalar();
        return id != null && UndoLastReview(Convert.ToInt64(id));
    }

    public AppSettings LoadSettings()
    {
        string? json;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT settings_json FROM app_settings WHERE id = 1";
            json = cmd.ExecuteScalar() as string;
        }
        if (json == null) return new AppSettings();
        AppSettings settings;
        try { settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings(); }
        catch (JsonException) { CredentialWarning = "设置格式损坏，已使用默认设置。原设置尚未覆盖，请先备份。"; return new AppSettings(); }
        // Migrate the removed relay preset without changing the user's private configuration.
        if (string.Equals(settings.Provider, "responses", StringComparison.OrdinalIgnoreCase))
            settings.Provider = "custom";
        CredentialWarning = "";
        try
        {
            if (settings.RememberKey)
            {
                if (!string.IsNullOrEmpty(settings.ApiKey))
                {
                    // Never discard the only recoverable legacy copy until DPAPI is verified.
                    var legacy = settings.ApiKey;
                    _secretStore.Set(legacy);
                    if (_secretStore.Get() != legacy) throw new System.Security.Cryptography.CryptographicException();
                    PersistSettings(settings);
                    BackupCommittedState();
                }
                settings.ApiKey = _secretStore.Get() ?? "";
            }
            else
            {
                if (!string.IsNullOrEmpty(settings.ApiKey)) { PersistSettings(settings); BackupCommittedState(); }
                settings.ApiKey = "";
                _secretStore.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or SqliteException or ArgumentException or PlatformNotSupportedException)
        {
            settings.ApiKey = "";
            CredentialWarning = "安全凭据读取或迁移失败；词库仍可使用。请重新填写 API Key 并保存。旧版未迁移凭据已保留，请勿删除原库。";
        }
        return settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var remember = settings.RememberKey && !string.IsNullOrEmpty(settings.ApiKey);
        if (remember)
        {
            _secretStore.Set(settings.ApiKey);
            if (_secretStore.Get() != settings.ApiKey) throw new System.Security.Cryptography.CryptographicException("安全凭据保存校验失败，原设置未更改。");
        }
        // Persist first: a failed settings write must not delete the previous credential.
        PersistSettings(settings);
        if (!remember) _secretStore.Delete();
        CredentialWarning = "";
        BackupCommittedState();
    }

    private static double MacSafeIntensity(double value) => double.IsFinite(value) ? Math.Clamp(value,0,1) : .65;

    private void PersistSettings(AppSettings settings)
    {
        var persisted = new AppSettings
        {
            AiProtocol = settings.AiProtocol, Material = settings.Material, GlassIntensity = MacSafeIntensity(settings.GlassIntensity),
            LookupShortcut = settings.LookupShortcut, TranslateShortcut = settings.TranslateShortcut, QuoteShortcut = settings.QuoteShortcut,
            Provider = settings.Provider, BaseUrl = settings.BaseUrl, Model = settings.Model,
            RememberKey = settings.RememberKey, Clipboard = settings.Clipboard, Theme = settings.Theme,
            UiLanguage = settings.UiLanguage == "en" ? "en" : "zh-CN",
            Timeout = settings.Timeout, ApiKey = "",
            AiContext = string.IsNullOrWhiteSpace(settings.AiContext) ? "日常表达" : settings.AiContext,
            IncludeSourceInAi = settings.IncludeSourceInAi,
            HighContrast = settings.HighContrast, OpaqueMaterial = settings.OpaqueMaterial, ReduceMotion = settings.ReduceMotion
        };
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO app_settings (id, settings_json) VALUES (1, $json)
            ON CONFLICT(id) DO UPDATE SET settings_json = excluded.settings_json";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(persisted));
        cmd.ExecuteNonQuery();
    }
    private void BackupCommittedState()
    {
        try
        {
            LastBackupPath = DatabaseSafety.CreateBackup(_connection, _dbPath);
            BackupWarning = "";
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            // The primary transaction already committed; never tell users to retry a
            // successful destructive action just because a secondary backup failed.
            BackupWarning = "数据已保存，但自动备份未完成。请检查磁盘空间与权限。";
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
