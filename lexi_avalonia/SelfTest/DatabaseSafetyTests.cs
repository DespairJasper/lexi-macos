using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace Lexi;

public static class DatabaseSafetyTests
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LexiSafety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "vocab.sqlite3");
            var orphan = Path.Combine(folder, "orphan.sqlite3");
            File.WriteAllText(orphan + "-wal", "preserve unmatched old WAL");
            var orphanRejected = false;
            try { using var bad = new VocabularyService(orphan); } catch (InvalidDataException) { orphanRejected = true; }
            Check(orphanRejected && !File.Exists(orphan) && File.ReadAllText(orphan + "-wal") == "preserve unmatched old WAL", "orphaned WAL blocks new database creation without deletion");
            using (var service = new VocabularyService(path))
            {
                service.AddWord("durable", "/ˈdjʊərəbl/", "耐久的", "lasting");
                Check(Directory.Exists(Path.Combine(folder, "backups")) && Directory.GetFiles(Path.Combine(folder, "backups"), "*.sqlite3").Length > 0, "committed word has a standalone automatic backup");
                var newest = Directory.GetFiles(Path.Combine(folder, "backups"), "*.sqlite3").OrderDescending().First();
                using var backup = Open(newest);
                DatabaseSafety.Validate(backup);
                Check(Scalar(backup, "SELECT count(*) FROM words WHERE word='durable'") == "1", "backup contains committed word");
                Check(Scalar(backup, "PRAGMA journal_mode") == "delete", "backup does not depend on WAL sidecars");
            }
            // Exercise repeated upgrade-like close/reopen cycles with settings + edits.
            for (var i = 0; i < 24; i++)
            {
                using var service = new VocabularyService(path);
                Check(service.GetAllWords().Count == 1, "reopen preserves words");
                service.EditWord(service.GetAllWords()[0].Id, "durable-" + i);
                service.SaveSettings(new AppSettings { Theme = i % 2 == 0 ? "Light" : "Dark", ApiKey = "not-persisted", RememberKey = false });
            }
            using (var check = Open(path))
            {
                DatabaseSafety.Validate(check);
                Check(Scalar(check, "SELECT translation FROM words") == "durable-23", "repeated reopen preserves latest edits");
                Check(Scalar(check, "SELECT settings_json FROM app_settings").Contains("not-persisted") == false, "backups do not enable key persistence");
            }
            // Explicitly capture the latest edit; routine backups now use a time interval.
            using (var current = new VocabularyService(path)) current.CreateManualBackup();
            Check(Directory.GetFiles(Path.Combine(folder, "backups"), "*.sqlite3").Length <= 20, "snapshot retention bounded");
            // A corrupt preexisting file must fail before schema/init or backup rotation.
            var corrupt = Path.Combine(folder, "corrupt.sqlite3");
            File.WriteAllText(corrupt, "not a database, preserve this evidence");
            var before = SHA256.HashData(File.ReadAllBytes(corrupt));
            var rejected = false;
            try { using var bad = new VocabularyService(corrupt); } catch { rejected = true; }
            Check(rejected && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(corrupt))), "corrupt startup fails without overwriting original");
            var restoreFrom = Directory.GetFiles(Path.Combine(folder, "backups"), "*.sqlite3").OrderDescending().First();
            var archive = DatabaseSafety.RestoreBackup(restoreFrom, corrupt);
            Check(File.ReadAllText(Path.Combine(archive, "corrupt.sqlite3")) == "not a database, preserve this evidence", "restore archives corrupt evidence");
            using (var restored = Open(corrupt))
            {
                DatabaseSafety.Validate(restored);
                Check(Scalar(restored, "SELECT translation FROM words") == "durable-23", "explicit restore preserves snapshot contents");
            }
            var badBackup = Path.Combine(folder, "bad-backup.sqlite3");
            File.WriteAllText(badBackup, "invalid backup");
            var oldHash = SHA256.HashData(File.ReadAllBytes(corrupt));
            rejected = false;
            try { DatabaseSafety.RestoreBackup(badBackup, corrupt); } catch { rejected = true; }
            Check(rejected && oldHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(corrupt))), "invalid recovery candidate cannot replace data");
            var incomplete = Path.Combine(folder, "incomplete.sqlite3");
            using (var missing = Open(incomplete))
            {
                using var cmd = missing.CreateCommand();
                cmd.CommandText = "CREATE TABLE words(id,word,translation,stage,status,next_review_date,review_count);CREATE TABLE app_settings(settings_json);CREATE TABLE review_logs(id,word_id);";
                cmd.ExecuteNonQuery();
            }
            rejected = false;
            try { DatabaseSafety.RestoreBackup(incomplete, corrupt); } catch { rejected = true; }
            Check(rejected && oldHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(corrupt))), "physically valid backup missing app columns is rejected");
            var interrupted = Path.Combine(folder, "interrupted.sqlite3");
            File.WriteAllText(interrupted + ".restore-pending.json", "{\"interrupted\":true}");
            rejected = false;
            try { using var bad = new VocabularyService(interrupted); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && !File.Exists(interrupted), "interrupted restore never silently creates empty vocabulary");
            using (var guard = new InstallationGuard(folder))
            {
                rejected = false;
                try { using var second = new InstallationGuard(folder); } catch (IOException) { rejected = true; }
                Check(rejected, "running/install guard excludes another holder");
            }
            using (var guard = new InstallationGuard(folder)) { }
            Console.WriteLine("PASS: automatic standalone backups, 24 reopen/write cycles, retention, fail-closed corruption guard");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open(); return c;
    }
    private static string Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar()?.ToString() ?? ""; }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("Safety regression: " + message); }
}
