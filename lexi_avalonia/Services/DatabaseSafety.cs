using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lexi;

/// <summary>Never copies a live .db file or guesses which WAL frames to discard.</summary>
public static class DatabaseSafety
{
    private static readonly Regex BackupName = new(
        @"^lexi-(\d{8}-\d{6}-\d{7})-[a-f0-9]{32}\.sqlite3$", RegexOptions.CultureInvariant);
    private static IEnumerable<FileInfo> ManagedBackups(string folder) => new DirectoryInfo(folder)
        .GetFiles("lexi-*.sqlite3")
        .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && BackupName.IsMatch(f.Name) && TryBackupTime(f, out _))
        .OrderByDescending(f => f.Name, StringComparer.Ordinal);

    private static void RejectLinkedBackupDirectory(string folder)
    {
        if (new DirectoryInfo(folder).LinkTarget != null)
            throw new InvalidDataException("备份目录不能是符号链接；已停止向外部目录写入或清理。");
    }

    private static bool TryBackupTime(FileInfo file, out DateTime created) => DateTime.TryParseExact(
        BackupName.Match(file.Name).Groups[1].Value, "yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out created);

    private static void ValidateCompleteBackup(string path)
    {
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        backup.Open(); Validate(backup); ValidateApplicationSchema(backup);
    }

    private static void PruneBackups(string folder, string keep, bool keepOnlyLatest)
    {
        long retainedBytes = new FileInfo(keep).Length;
        var retainedCount = 1;
        foreach (var old in ManagedBackups(folder).Where(f => f.FullName != keep))
        {
            if (!keepOnlyLatest && retainedCount < 20 && retainedBytes + old.Length <= 256L * 1024 * 1024)
            { retainedBytes += old.Length; retainedCount++; }
            else try { old.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string CreateStorageMaintenanceBackup(SqliteConnection source, string databasePath)
    {
        // The source has already passed startup integrity/schema validation.
        // Retain one independently validated old snapshot before freeing space;
        // otherwise a disk full of 3.2.1 backups can prevent maintenance starting.
        var folder = Path.Combine(Path.GetDirectoryName(databasePath)!, "backups");
        RejectLinkedBackupDirectory(folder);
        if (Directory.Exists(folder))
        {
            foreach (var candidate in ManagedBackups(folder))
            {
                try { ValidateCompleteBackup(candidate.FullName); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
                { continue; }
                PruneBackups(folder, candidate.FullName, keepOnlyLatest: true);
                break;
            }
        }
        return CreateBackup(source, databasePath, keepOnlyLatest: true);
    }

    public static string CreateAutomaticBackup(SqliteConnection source, string databasePath, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("备份时刻必须是 UTC。", nameof(nowUtc));
        var folder = Path.Combine(Path.GetDirectoryName(databasePath)!, "backups");
        RejectLinkedBackupDirectory(folder);
        var latest = Directory.Exists(folder)
            ? ManagedBackups(folder).FirstOrDefault(file => TryBackupTime(file, out var time) && time <= now) : null;
        if (latest != null && TryBackupTime(latest, out var created) && now - created < TimeSpan.FromMinutes(15))
        {
            try { ValidateCompleteBackup(latest.FullName); return latest.FullName; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
            { /* Never throttle to a damaged/unreadable backup. Create a valid one. */ }
        }
        return CreateBackup(source, databasePath, now);
    }

    public static void RejectOrphanedSidecars(string path)
    {
        if (File.Exists(path + ".restore-pending.json"))
            throw new InvalidDataException("检测到上次恢复被中断，已停止启动以防新建空库。原文件、恢复候选和操作记录均已保留，请先处理 .restore-pending.json 指向的恢复现场。");
        if (!File.Exists(path) && new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(path + suffix)))
            throw new InvalidDataException("主词库缺失，但检测到遗留写入日志。已停止新建/迁移，避免不同词库拼接；请保留这些文件并通过备份恢复。");
    }

    public static void ValidateApplicationSchema(SqliteConnection source)
    {
        using var schema = source.CreateCommand();
        schema.CommandText = "SELECT id,word,phonetic,translation,definition,notes,stage,status,created_at,learning_start_date,next_review_date,last_reviewed_at,review_count FROM words LIMIT 0; SELECT id,settings_json FROM app_settings LIMIT 0; SELECT id,word_id,action,old_stage,new_stage,old_status,new_status,old_next_review_date,new_next_review_date,old_learning_start_date,new_learning_start_date,log_time,log_date FROM review_logs LIMIT 0;";
        using (var reader = schema.ExecuteReader()) { do { while (reader.Read()) { } } while (reader.NextResult()); }
        ValidateWordRecords(source);
        VocabularyService.ValidateQuoteSchema(source);
        schema.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='review_snapshots' AND type='table'";
        if (Convert.ToInt32(schema.ExecuteScalar()) > 0)
        {
            schema.CommandText = "SELECT log_id,last_reviewed_at,review_count FROM review_snapshots LIMIT 0";
            using var reader = schema.ExecuteReader();
        }
        schema.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='schema_migrations' AND type='table'";
        var hasVersions = Convert.ToInt32(schema.ExecuteScalar()) > 0;
        schema.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='word_archives' AND type='table'";
        var hasArchives = Convert.ToInt32(schema.ExecuteScalar()) > 0;
        if (!hasVersions && !hasArchives) return; // Verified legacy schema may be upgraded.
        if (!hasVersions || !hasArchives) throw new InvalidDataException("词库档案结构不完整；已停止写入，请保留原库并恢复备份。");
        schema.CommandText = "SELECT version,applied_at FROM schema_migrations";
        var hasCurrent = false;
        using (var reader = schema.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.GetInt32(0) is not (1 or 2)) throw new InvalidDataException("词库版本不受支持，请使用匹配版本打开。");
                hasCurrent = true;
            }
        }
        if (!hasCurrent) throw new InvalidDataException("词库档案迁移记录缺失。");
        schema.CommandText = "SELECT word_id,uuid,source_type,source_title,source_excerpt,tags_json,encounter_count,revision,created_at_utc,updated_at_utc,last_encountered_at_utc,ai_json FROM word_archives LIMIT 0";
        using (var reader = schema.ExecuteReader()) { }
        schema.CommandText = "SELECT count(*) FROM words w LEFT JOIN word_archives a ON a.word_id=w.id WHERE a.word_id IS NULL";
        if (Convert.ToInt64(schema.ExecuteScalar()) != 0) throw new InvalidDataException("词汇档案记录缺失，已停止写入。");
        schema.CommandText = "SELECT tags_json,ai_json,uuid,encounter_count,revision,created_at_utc,updated_at_utc,last_encountered_at_utc FROM word_archives";
        using (var reader = schema.ExecuteReader())
        {
            while (reader.Read())
            {
                try
                {
                    if (!Guid.TryParseExact(reader.GetString(2), "D", out var uuid) || uuid == Guid.Empty) throw new InvalidDataException("档案标识无效。");
                    ReadBoundedInteger(reader, 3, 1, int.MaxValue);
                    ReadBoundedInteger(reader, 4, 1, int.MaxValue);
                    for (var i = 5; i <= 7; i++) ValidateDate(reader.GetString(i), utc: true);
                    var tags = System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(0));
                    VocabularyService.ValidateArchiveTags(tags);
                    if (!reader.IsDBNull(1))
                    {
                        var ai = System.Text.Json.JsonSerializer.Deserialize<LlmResult>(reader.GetString(1), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (ai == null) throw new InvalidDataException("AI 档案为空。");
                        VocabularyService.ValidateArchiveAi(ai);
                    }
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidCastException or OverflowException)
                { throw new InvalidDataException("词库标签或 AI 档案损坏，已停止写入；原数据未被清空，请恢复备份。", ex); }
            }
        }
    }

    private static long ReadBoundedInteger(SqliteDataReader reader, int ordinal, long minimum, long maximum)
    {
        if (reader.IsDBNull(ordinal) || reader.GetValue(ordinal) is not long value || value < minimum || value > maximum)
            throw new InvalidDataException("词库整数超出允许范围，已拒绝写入或恢复。");
        return value;
    }

    private static void ValidateDate(string? value, bool optional = false, bool utc = false, bool calendarOnly = false)
    {
        if (value == null && optional) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40) throw new InvalidDataException("词库日期为空或无效。");
        if (utc)
        {
            if (!(value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal))
                || !DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero)
                throw new InvalidDataException("档案时间必须使用有效 UTC 日期。");
            return;
        }
        string[] formats = calendarOnly ? ["yyyy-MM-dd"] : ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("词库日期不是有效日历日期。");
    }

    private static void ValidateWordRecords(SqliteConnection source)
    {
        using var command = source.CreateCommand();
        command.CommandText = "SELECT id,word,stage,status,review_count,created_at,learning_start_date,next_review_date,last_reviewed_at FROM words";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ReadBoundedInteger(reader, 0, 1, long.MaxValue);
                if (reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1))) throw new InvalidDataException("词条为空。");
                var stage = ReadBoundedInteger(reader, 2, 0, 5);
                var status = reader.IsDBNull(3) ? "" : reader.GetString(3);
                if (status is not ("learning" or "mastered") || (status == "mastered" && stage != 5) || (status == "learning" && stage == 5))
                    throw new InvalidDataException("复习阶段与状态不兼容。");
                ReadBoundedInteger(reader, 4, 0, int.MaxValue);
                ValidateDate(reader.IsDBNull(5) ? null : reader.GetString(5));
                ValidateDate(reader.IsDBNull(6) ? null : reader.GetString(6), calendarOnly: true);
                ValidateDate(reader.IsDBNull(7) ? null : reader.GetString(7), optional: true, calendarOnly: true);
                ValidateDate(reader.IsDBNull(8) ? null : reader.GetString(8), optional: true);
                if (status == "mastered" && !reader.IsDBNull(7)) throw new InvalidDataException("已掌握词条仍包含复习排期。");
            }
        }
    }

    /// <summary>Freeze a validated selection before backup rotation can remove its original filename.</summary>
    public static string StageRestoreSelection(string selectedBackup, string dataDirectory)
    {
        var staging = Path.Combine(Path.GetFullPath(dataDirectory), ".restore-selection-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(selectedBackup), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = staging, Pooling = false }.ToString()))
            {
                source.Open(); Validate(source); ValidateApplicationSchema(source);
                destination.Open(); source.BackupDatabase(destination); Validate(destination); ValidateApplicationSchema(destination);
                using var journal = destination.CreateCommand(); journal.CommandText = "PRAGMA journal_mode=DELETE"; journal.ExecuteScalar();
            }
            using (var file = new FileStream(staging, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
            return staging;
        }
        catch { DeleteRestoreSelection(staging); throw; }
    }

    public static void DeleteRestoreSelection(string stagedPath)
    {
        // Only paths returned by StageRestoreSelection belong to this cleanup operation.
        if (!Path.GetFileName(stagedPath).StartsWith(".restore-selection-", StringComparison.Ordinal)) throw new ArgumentException("无效的恢复暂存文件。");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            try { File.Delete(stagedPath + suffix); } catch (IOException) { }
    }
    public static void Validate(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        using (var rows = cmd.ExecuteReader())
        {
            if (!rows.Read() || rows.GetString(0) != "ok" || rows.Read())
                throw new InvalidDataException("词库完整性检查失败，已停止写入。原库和备份均未覆盖，请使用备份恢复。");
        }
        cmd.CommandText = "PRAGMA foreign_key_check";
        using var foreign = cmd.ExecuteReader();
        if (foreign.Read()) throw new InvalidDataException("词库关联记录检查失败，已停止写入。");
    }

    public static string CreateBackup(SqliteConnection source, string databasePath, DateTime? nowUtc = null, bool keepOnlyLatest = false)
    {
        var folder = Path.Combine(Path.GetDirectoryName(databasePath)!, "backups");
        RejectLinkedBackupDirectory(folder);
        Directory.CreateDirectory(folder);
        var now = nowUtc ?? DateTime.UtcNow;
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("备份时刻必须是 UTC。", nameof(nowUtc));
        var stamp = now.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"lexi-{stamp}-{Guid.NewGuid():N}.sqlite3");
        var temp = path + ".tmp";
        try
        {
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = temp, Pooling = false }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
                Validate(destination);
                ValidateApplicationSchema(destination);
                using var cmd = destination.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;";
                cmd.ExecuteNonQuery();
            }
            // Flush the complete standalone snapshot before publishing its name.
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            File.Move(temp, path);
            // Prune only our own completed snapshots, after a new valid one exists.
            // Always retain the just-validated snapshot, even after a clock change
            // or when the actual user's data itself is larger than the budget.
            PruneBackups(folder, path, keepOnlyLatest);
            return path;
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                try { File.Delete(temp + suffix); } catch (IOException) { }
        }
    }

    public static void ValidateExisting(string path)
    {
        RejectOrphanedSidecars(path);
        if (!File.Exists(path)) return;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            Validate(connection);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 776 && File.Exists(path + "-journal"))
        {
            // SQLITE_READONLY_ROLLBACK: SQLite needs to undo a hot rollback journal
            // before reads are possible. Preserve crash evidence, then allow ONLY
            // SQLite's own recovery (no schema/business writes before validation).
            var archive = Path.Combine(Path.GetDirectoryName(path)!, "crash-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(archive);
            var locks = new List<(string Path, FileStream Stream)>();
            try
            {
                foreach (var original in new[] { path, path + "-journal", path + "-wal", path + "-shm" }.Where(File.Exists))
                    locks.Add((original, new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read)));
                foreach (var item in locks)
                    using (var copy = new FileStream(Path.Combine(archive, Path.GetFileName(item.Path)), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { item.Stream.CopyTo(copy); copy.Flush(true); }
            }
            finally { foreach (var item in locks) item.Stream.Dispose(); }
            using var recovery = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            recovery.Open(); Validate(recovery);
        }
    }

    public static string RestoreBackup(string backup, string target)
    {
        backup = Path.GetFullPath(backup); target = Path.GetFullPath(target);
        if (string.Equals(backup, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能把当前词库当作恢复备份。");
        var marker = target + ".restore-pending.json";
        if (File.Exists(marker)) throw new IOException("存在未完成的恢复操作，已保留现场，请先处理恢复记录。");
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var candidate = Path.Combine(directory, ".restore-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        var archive = Path.Combine(directory, "before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        var moved = new List<(string Original, string Archived)>();
        var locks = new List<FileStream>();
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = candidate, Pooling = false }.ToString()))
            {
                source.Open(); Validate(source);
                ValidateApplicationSchema(source);
                destination.Open(); source.BackupDatabase(destination); Validate(destination); ValidateApplicationSchema(destination);
                using var journal = destination.CreateCommand(); journal.CommandText = "PRAGMA journal_mode=DELETE"; journal.ExecuteScalar();
            }
            using (var flush = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flush.Flush(true);
            // Take native deny-read/write handles before moving any member of the DB
            // family. This also refuses recovery while an older app has SQLite open.
            var originals = new[] { target, target + "-wal", target + "-shm", target + "-journal" }.Where(File.Exists).ToArray();
            foreach (var original in originals) locks.Add(new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete));
            Directory.CreateDirectory(archive);
            using (var record = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                System.Text.Json.JsonSerializer.Serialize(record, new { target, candidate, archive, originals });
                record.Flush(true);
            }
            foreach (var original in originals)
            {
                var archived = Path.Combine(archive, Path.GetFileName(original));
                File.Move(original, archived); moved.Add((original, archived));
            }
            File.Move(candidate, target);
            File.Delete(marker);
            return archive;
        }
        catch
        {
            foreach (var pair in moved.AsEnumerable().Reverse())
                if (!File.Exists(pair.Original) && File.Exists(pair.Archived)) File.Move(pair.Archived, pair.Original);
            // Keep the durable marker if rollback did not fully restore the original
            // family. A later startup must never interpret this as a brand-new user.
            if (moved.All(pair => File.Exists(pair.Original) && !File.Exists(pair.Archived))) File.Delete(marker);
            throw;
        }
        finally
        {
            foreach (var handle in locks) handle.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                try { File.Delete(candidate + suffix); } catch (IOException) { }
        }
    }
}
