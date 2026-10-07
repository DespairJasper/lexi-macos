using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lexi.Core;

namespace Lexi;

public sealed partial class VocabularyService
{
    public (int Added, int Skipped) ImportArchive(IReadOnlyList<WordItem> entries)
    {
        ArchiveTransferService.GenerateJson(entries);
        foreach (var entry in entries) { ValidateArchiveTags(entry.Archive.Tags); ValidateArchiveAi(entry.AiResult); }
        CreateManualBackup();
        int added=0, skipped=0;
        using var tx=_connection.BeginTransaction();
        foreach(var w in entries) {
            using var exists=_connection.CreateCommand();exists.Transaction=tx;
            exists.CommandText="SELECT 1 FROM words w JOIN word_archives a ON a.word_id=w.id WHERE lower(w.word)=lower($word) OR a.uuid=$uuid LIMIT 1";
            exists.Parameters.AddWithValue("$word",w.Word);exists.Parameters.AddWithValue("$uuid",w.Archive.Uuid);
            if(exists.ExecuteScalar()!=null){skipped++;continue;}
            using var cmd=_connection.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO words (word,phonetic,translation,definition,notes,stage,status,created_at,learning_start_date,next_review_date,last_reviewed_at,review_count) VALUES ($word,$phon,$trans,$def,$notes,$stage,$status,$created,$start,$next,$last,$reviews); SELECT last_insert_rowid();";
            void P(string key,object? value)=>cmd.Parameters.AddWithValue(key,value??DBNull.Value);
            P("$word",w.Word);P("$phon",w.Phonetic);P("$trans",w.Translation);P("$def",w.Definition);P("$notes",w.Notes);P("$stage",w.Stage);P("$status",w.Status);P("$created",w.CreatedAt);P("$start",w.LearningStartDate);P("$next",w.NextReviewDate);P("$last",w.LastReviewedAt);P("$reviews",w.ReviewCount);
            var id=(long)cmd.ExecuteScalar()!;cmd.Parameters.Clear();
            cmd.CommandText="INSERT INTO word_archives (word_id,uuid,source_type,source_title,source_excerpt,tags_json,encounter_count,revision,created_at_utc,updated_at_utc,last_encountered_at_utc,ai_json) VALUES ($id,$uuid,$type,$title,$excerpt,$tags,$count,$revision,$created,$updated,$encounter,$ai)";
            var a=w.Archive;P("$id",id);P("$uuid",a.Uuid);P("$type",a.SourceType);P("$title",a.SourceTitle);P("$excerpt",a.SourceExcerpt);P("$tags",JsonSerializer.Serialize(a.Tags));P("$count",a.EncounterCount);P("$revision",a.Revision);P("$created",a.CreatedAtUtc);P("$updated",a.UpdatedAtUtc);P("$encounter",a.LastEncounteredAtUtc);P("$ai",w.AiResult==null?null:JsonSerializer.Serialize(w.AiResult));
            cmd.ExecuteNonQuery();added++;
        }
        tx.Commit();BackupCommittedState();return(added,skipped);
    }

    public static string GenerateStableUuid(string word)
    {
        var clean = (word ?? "").Trim().ToLowerInvariant();
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("lexi:word:" + clean));
        return new Guid(hash).ToString();
    }

    private void MigrateArchiveSchema()
    {
        using var tx = _connection.BeginTransaction();
        try
        {
            using var checkCmd = _connection.CreateCommand();
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL); SELECT COALESCE(MAX(version),0) FROM schema_migrations;";
            var version = Convert.ToInt32(checkCmd.ExecuteScalar());
            if (version > 2) throw new InvalidDataException("词库来自更新版本，当前版本无法写入。");
            if (version == 1)
            {
                InstallArchiveRevisionTrigger(tx);
                tx.Commit();
                return;
            }
            using (var schemaCmd = _connection.CreateCommand())
            {
                schemaCmd.Transaction = tx;
                schemaCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS word_archives (
                        word_id INTEGER PRIMARY KEY REFERENCES words(id) ON DELETE CASCADE,
                        uuid TEXT UNIQUE NOT NULL,
                        source_type TEXT NOT NULL DEFAULT '',
                        source_title TEXT NOT NULL DEFAULT '',
                        source_excerpt TEXT NOT NULL DEFAULT '',
                        tags_json TEXT NOT NULL DEFAULT '[]',
                        encounter_count INTEGER NOT NULL DEFAULT 1,
                        revision INTEGER NOT NULL DEFAULT 1,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        last_encountered_at_utc TEXT NOT NULL,
                        ai_json TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_word_archives_uuid ON word_archives(uuid);
                ";
                schemaCmd.ExecuteNonQuery();
            }

            var unmigrated = new List<(long Id, string Word, string CreatedAt)>();
            using (var queryCmd = _connection.CreateCommand())
            {
                queryCmd.Transaction = tx;
                queryCmd.CommandText = @"
                    SELECT id, word, created_at
                    FROM words
                    WHERE id NOT IN (SELECT word_id FROM word_archives)
                ";
                using var reader = queryCmd.ExecuteReader();
                while (reader.Read())
                {
                    unmigrated.Add((
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2)
                    ));
                }
            }

            var defaultUtc = DateTime.UtcNow.ToString("o");
            foreach (var (wordId, wordText, createdAt) in unmigrated)
            {
                var createdUtc = defaultUtc;
                if (DateTime.TryParse(createdAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
                {
                    createdUtc = parsed.ToUniversalTime().ToString("o");
                }

                var uuid = GenerateStableUuid(wordText);

                using var insertCmd = _connection.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
                    INSERT INTO word_archives (
                        word_id, uuid, source_type, source_title, source_excerpt,
                        tags_json, encounter_count, revision, created_at_utc,
                        updated_at_utc, last_encountered_at_utc, ai_json
                    ) VALUES (
                        $id, $uuid, '', '', '',
                        '[]', 1, 1, $createdUtc,
                        $createdUtc, $createdUtc, NULL
                    )
                    ON CONFLICT(word_id) DO NOTHING;
                ";
                insertCmd.Parameters.AddWithValue("$id", wordId);
                insertCmd.Parameters.AddWithValue("$uuid", uuid);
                insertCmd.Parameters.AddWithValue("$createdUtc", createdUtc);
                insertCmd.ExecuteNonQuery();
            }

            using (var migCmd = _connection.CreateCommand())
            {
                migCmd.Transaction = tx;
                migCmd.CommandText = @"
                    INSERT INTO schema_migrations (version, applied_at)
                    VALUES (1, $appliedAt)
                    ON CONFLICT(version) DO NOTHING;
                ";
                migCmd.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("o"));
                migCmd.ExecuteNonQuery();
            }

            InstallArchiveRevisionTrigger(tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private void InstallArchiveRevisionTrigger(SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"CREATE TRIGGER IF NOT EXISTS lexi_word_revision
            AFTER UPDATE OF word,phonetic,translation,definition,notes,stage,status,learning_start_date,next_review_date,last_reviewed_at,review_count ON words
            BEGIN UPDATE word_archives SET revision=revision+1, updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE word_id=NEW.id; END;";
        cmd.ExecuteNonQuery();
    }

    internal static void ValidateArchiveText(string? value, int maximum, string field, bool required = false)
    {
        if (value == null || value.Length > maximum || value.Contains('\0') || (required && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException(field + "为空、包含无效字符或超出长度限制。");
    }

    internal static void ValidateArchiveTags(string[]? tags)
    {
        if (tags == null || tags.Length > 50) throw new ArgumentException("标签最多 50 项，且不能为空列表。");
        foreach (var tag in tags) ValidateArchiveText(tag, 100, "标签", true);
    }

    internal static void ValidateArchiveAi(LlmResult? ai)
    {
        if (ai == null) return;
        if (ai.Examples == null || ai.Synonyms == null || ai.Antonyms == null || ai.Phrases == null
            || ai.Examples.Count > 20 || ai.Phrases.Count > 20 || ai.Synonyms.Count > 100 || ai.Antonyms.Count > 100)
            throw new ArgumentException("AI 内容列表为空或超出数量限制。");
        foreach (var example in ai.Examples) { if (example == null) throw new ArgumentException("AI 例句不能为空。"); ValidateArchiveText(example.English, 10000, "例句"); ValidateArchiveText(example.Chinese, 10000, "例句翻译"); }
        foreach (var phrase in ai.Phrases) { if (phrase == null) throw new ArgumentException("AI 词组不能为空。"); ValidateArchiveText(phrase.English, 10000, "词组"); ValidateArchiveText(phrase.Chinese, 10000, "词组翻译"); }
        foreach (var value in ai.Synonyms.Concat(ai.Antonyms)) ValidateArchiveText(value, 10000, "相关词");
    }

    public void SaveArchive(long id, string translation, string notes, ArchiveMetadata metadata, LlmResult? ai)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateArchiveText(translation, 100000, "释义", true); ValidateArchiveText(notes, 10000, "备注");
        ValidateArchiveText(metadata.SourceType, 256, "来源类型"); ValidateArchiveText(metadata.SourceTitle, 4096, "来源标题");
        ValidateArchiveText(metadata.SourceExcerpt, 100000, "来源原句"); ValidateArchiveTags(metadata.Tags); ValidateArchiveAi(ai);
        using var tx = _connection.BeginTransaction();
        using var archive = _connection.CreateCommand(); archive.Transaction = tx;
        archive.CommandText = @"UPDATE word_archives SET source_type=$type,source_title=$title,source_excerpt=$excerpt,tags_json=$tags,ai_json=$ai WHERE word_id=$id";
        archive.Parameters.AddWithValue("$id", id); archive.Parameters.AddWithValue("$type", metadata.SourceType.Trim());
        archive.Parameters.AddWithValue("$title", metadata.SourceTitle.Trim()); archive.Parameters.AddWithValue("$excerpt", metadata.SourceExcerpt.Trim());
        archive.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(metadata.Tags));
        archive.Parameters.AddWithValue("$ai", ai == null ? DBNull.Value : JsonSerializer.Serialize(ai));
        if (archive.ExecuteNonQuery() != 1) throw new InvalidOperationException("未找到完整词汇档案。");
        using var word = _connection.CreateCommand(); word.Transaction = tx;
        // The words trigger owns the single revision increment for this edit.
        word.CommandText = "UPDATE words SET translation=$translation,notes=$notes WHERE id=$id";
        word.Parameters.AddWithValue("$id", id); word.Parameters.AddWithValue("$translation", translation.Trim()); word.Parameters.AddWithValue("$notes", notes.Trim());
        if (word.ExecuteNonQuery() != 1) throw new InvalidOperationException("未找到单词。");
        tx.Commit();
        BackupCommittedState();
    }
    public void RecordEncounter(long id)
    {
        string wordText;
        using (var checkCmd = _connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT word FROM words WHERE id = $id";
            checkCmd.Parameters.AddWithValue("$id", id);
            var result = checkCmd.ExecuteScalar();
            if (result == null)
            {
                throw new InvalidOperationException($"未找到 ID 为 {id} 的单词。");
            }
            wordText = result.ToString()!;
        }

        var nowUtc = DateTime.UtcNow.ToString("o");
        var uuid = GenerateStableUuid(wordText);

        using var tx = _connection.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO word_archives (
                    word_id, uuid, source_type, source_title, source_excerpt,
                    tags_json, encounter_count, revision, created_at_utc,
                    updated_at_utc, last_encountered_at_utc, ai_json
                ) VALUES (
                    $id, $uuid, '', '', '',
                    '[]', 2, 2, $nowUtc,
                    $nowUtc, $nowUtc, NULL
                )
                ON CONFLICT(word_id) DO UPDATE SET
                    encounter_count = word_archives.encounter_count + 1,
                    revision = word_archives.revision + 1,
                    updated_at_utc = $nowUtc,
                    last_encountered_at_utc = $nowUtc;
            ";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$uuid", uuid);
            cmd.Parameters.AddWithValue("$nowUtc", nowUtc);
            cmd.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        BackupCommittedState();
    }

    public void SaveExpansion(long id, LlmResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateArchiveAi(result);
        string wordText;
        using (var checkCmd = _connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT word FROM words WHERE id = $id";
            checkCmd.Parameters.AddWithValue("$id", id);
            var res = checkCmd.ExecuteScalar();
            if (res == null)
            {
                throw new InvalidOperationException($"未找到 ID 为 {id} 的单词。");
            }
            wordText = res.ToString()!;
        }

        var nowUtc = DateTime.UtcNow.ToString("o");
        var aiJson = JsonSerializer.Serialize(result);
        var uuid = GenerateStableUuid(wordText);

        using var tx = _connection.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO word_archives (
                    word_id, uuid, source_type, source_title, source_excerpt,
                    tags_json, encounter_count, revision, created_at_utc,
                    updated_at_utc, last_encountered_at_utc, ai_json
                ) VALUES (
                    $id, $uuid, '', '', '',
                    '[]', 1, 1, $nowUtc,
                    $nowUtc, $nowUtc, $aiJson
                )
                ON CONFLICT(word_id) DO UPDATE SET
                    ai_json = excluded.ai_json,
                    revision = word_archives.revision + 1,
                    updated_at_utc = $nowUtc;
            ";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$uuid", uuid);
            cmd.Parameters.AddWithValue("$nowUtc", nowUtc);
            cmd.Parameters.AddWithValue("$aiJson", aiJson);
            cmd.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        BackupCommittedState();
    }

    public void MarkUnfamiliar(long id)
    {
        var tomorrowStr = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var nowUtc = DateTime.UtcNow.ToString("o");

        using var tx = _connection.BeginTransaction();
        try
        {
            int currentStage;
            string currentStatus;
            string? currentNextReview;
            string currentStart;
            string? currentLastReviewed;
            int currentReviewCount;
            string wordText;

            using (var queryCmd = _connection.CreateCommand())
            {
                queryCmd.Transaction = tx;
                queryCmd.CommandText = @"
                    SELECT word, stage, status, next_review_date, learning_start_date, last_reviewed_at, review_count
                    FROM words
                    WHERE id = $id
                ";
                queryCmd.Parameters.AddWithValue("$id", id);
                using var reader = queryCmd.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidOperationException($"未找到 ID 为 {id} 的单词。");
                }

                wordText = reader.GetString(0);
                currentStage = reader.GetInt32(1);
                currentStatus = reader.GetString(2);
                currentNextReview = reader.IsDBNull(3) ? null : reader.GetString(3);
                currentStart = reader.GetString(4);
                currentLastReviewed = reader.IsDBNull(5) ? null : reader.GetString(5);
                currentReviewCount = reader.GetInt32(6);
            }

            // Record action in review_logs so that UndoLastReview can reverse it
            using (var logCmd = _connection.CreateCommand())
            {
                logCmd.Transaction = tx;
                logCmd.CommandText = @"
                    INSERT INTO review_logs (
                        word_id, action, old_stage, new_stage, old_status, new_status,
                        old_next_review_date, new_next_review_date,
                        old_learning_start_date, new_learning_start_date,
                        log_time, log_date
                    ) VALUES (
                        $wordId, 'unfamiliar', $oldStage, $newStage, $oldStatus, 'learning',
                        $oldNext, $newNext, $oldStart, $newStart,
                        $now, $today
                    );
                ";
                logCmd.Parameters.AddWithValue("$wordId", id);
                logCmd.Parameters.AddWithValue("$oldStage", currentStage);
                logCmd.Parameters.AddWithValue("$newStage", Math.Min(currentStage, MaxStage - 1));
                logCmd.Parameters.AddWithValue("$oldStatus", currentStatus);
                logCmd.Parameters.AddWithValue("$oldNext", (object?)currentNextReview ?? DBNull.Value);
                logCmd.Parameters.AddWithValue("$newNext", tomorrowStr);
                logCmd.Parameters.AddWithValue("$oldStart", currentStart);
                logCmd.Parameters.AddWithValue("$newStart", currentStart);
                logCmd.Parameters.AddWithValue("$now", nowStr);
                logCmd.Parameters.AddWithValue("$today", todayStr);
                logCmd.ExecuteNonQuery();
            }

            // Snapshot for undo
            using (var snapshotCmd = _connection.CreateCommand())
            {
                snapshotCmd.Transaction = tx;
                snapshotCmd.CommandText = @"
                    INSERT INTO review_snapshots (log_id, last_reviewed_at, review_count)
                    VALUES (last_insert_rowid(), $last, $count);
                ";
                snapshotCmd.Parameters.AddWithValue("$last", (object?)currentLastReviewed ?? DBNull.Value);
                snapshotCmd.Parameters.AddWithValue("$count", currentReviewCount);
                snapshotCmd.ExecuteNonQuery();
            }

            // Update word: arrange review for tomorrow, ensure status is learning, do NOT reset stage or review_count
            using (var updateCmd = _connection.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"
                    UPDATE words
                    SET stage = MIN(stage, 4), status = 'learning', next_review_date = $tomorrow
                    WHERE id = $id;
                ";
                updateCmd.Parameters.AddWithValue("$tomorrow", tomorrowStr);
                updateCmd.Parameters.AddWithValue("$id", id);
                updateCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        BackupCommittedState();
    }

    public string CreateManualBackup()
    {
        var backupPath = DatabaseSafety.CreateBackup(_connection, _dbPath);
        LastBackupPath = backupPath;
        BackupWarning = "";
        return backupPath;
    }

    /// <summary>回忆评价「模糊」：等级不变，保持今日到期，本轮内会再次出现。</summary>
    public void MarkUnsure(long id) => MarkRecallOutcome(id, "unsure", resetToFirstStage: false);

    /// <summary>回忆评价「忘记」：等级归零重新学，保持今日到期，本轮重学后再测。</summary>
    public void MarkForgot(long id) => MarkRecallOutcome(id, "forgot", resetToFirstStage: true);

    private void MarkRecallOutcome(long id, string action, bool resetToFirstStage)
    {
        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        // 忘记后重新通过时，下一次复习落在明天：把学习起点回拨一个初始间隔。
        var restartStart = DateTime.Today.AddDays(-StageOffsets[0]).ToString("yyyy-MM-dd");
        var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using var tx = _connection.BeginTransaction();
        try
        {
            int currentStage;
            string currentStatus;
            string? currentNextReview;
            string currentStart;
            string? currentLastReviewed;
            int currentReviewCount;

            using (var queryCmd = _connection.CreateCommand())
            {
                queryCmd.Transaction = tx;
                queryCmd.CommandText = @"
                    SELECT stage, status, next_review_date, learning_start_date, last_reviewed_at, review_count
                    FROM words
                    WHERE id = $id
                ";
                queryCmd.Parameters.AddWithValue("$id", id);
                using var reader = queryCmd.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidOperationException($"未找到 ID 为 {id} 的单词。");
                }
                currentStage = reader.GetInt32(0);
                currentStatus = reader.GetString(1);
                currentNextReview = reader.IsDBNull(2) ? null : reader.GetString(2);
                currentStart = reader.GetString(3);
                currentLastReviewed = reader.IsDBNull(4) ? null : reader.GetString(4);
                currentReviewCount = reader.GetInt32(5);
            }

            var newStage = resetToFirstStage ? 0 : currentStage;
            var newStart = resetToFirstStage ? restartStart : currentStart;

            using (var logCmd = _connection.CreateCommand())
            {
                logCmd.Transaction = tx;
                logCmd.CommandText = @"
                    INSERT INTO review_logs (
                        word_id, action, old_stage, new_stage, old_status, new_status,
                        old_next_review_date, new_next_review_date,
                        old_learning_start_date, new_learning_start_date,
                        log_time, log_date
                    ) VALUES (
                        $wordId, $action, $oldStage, $newStage, $oldStatus, 'learning',
                        $oldNext, $newNext, $oldStart, $newStart,
                        $now, $today
                    );
                ";
                logCmd.Parameters.AddWithValue("$wordId", id);
                logCmd.Parameters.AddWithValue("$action", action);
                logCmd.Parameters.AddWithValue("$oldStage", currentStage);
                logCmd.Parameters.AddWithValue("$newStage", newStage);
                logCmd.Parameters.AddWithValue("$oldStatus", currentStatus);
                logCmd.Parameters.AddWithValue("$oldNext", (object?)currentNextReview ?? DBNull.Value);
                logCmd.Parameters.AddWithValue("$newNext", todayStr);
                logCmd.Parameters.AddWithValue("$oldStart", currentStart);
                logCmd.Parameters.AddWithValue("$newStart", newStart);
                logCmd.Parameters.AddWithValue("$now", nowStr);
                logCmd.Parameters.AddWithValue("$today", todayStr);
                logCmd.ExecuteNonQuery();
            }

            using (var snapshotCmd = _connection.CreateCommand())
            {
                snapshotCmd.Transaction = tx;
                snapshotCmd.CommandText = @"
                    INSERT INTO review_snapshots (log_id, last_reviewed_at, review_count)
                    VALUES (last_insert_rowid(), $last, $count);
                ";
                snapshotCmd.Parameters.AddWithValue("$last", (object?)currentLastReviewed ?? DBNull.Value);
                snapshotCmd.Parameters.AddWithValue("$count", currentReviewCount);
                snapshotCmd.ExecuteNonQuery();
            }

            using (var updateCmd = _connection.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"
                    UPDATE words
                    SET stage = $stage, status = 'learning',
                        next_review_date = $today, learning_start_date = $start
                    WHERE id = $id;
                ";
                updateCmd.Parameters.AddWithValue("$stage", newStage);
                updateCmd.Parameters.AddWithValue("$today", todayStr);
                updateCmd.Parameters.AddWithValue("$start", newStart);
                updateCmd.Parameters.AddWithValue("$id", id);
                updateCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        BackupCommittedState();
    }
}

