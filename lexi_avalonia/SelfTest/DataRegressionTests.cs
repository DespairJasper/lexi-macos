using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Lexi;

/// <summary>All fixtures live in a unique temp folder; no real user DB or API key is read.</summary>
public static class DataRegressionTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lexi_DataRegression_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            MigrationWithWal(root);
            MigrationFailures(root);
            UndoStateAndGuards(root);
            SchedulingAndSettings(root);
            Console.WriteLine("PASS: data regressions (WAL migration, failure atomicity, isolated paths, undo snapshots/guards, scheduling/settings)");
        }
        finally
        {
            if (OperatingSystem.IsMacOS()) new MacKeychainSecretStore(root).Delete();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Data regression failed: " + message);
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static void Sql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void MigrationWithWal(string root)
    {
        var source = Path.Combine(root, "legacy.sqlite3");
        var target = Path.Combine(root, "migrated.sqlite3");
        using (var seed = new VocabularyService(source)) { seed.AddWord("anchor", "", "保留", ""); }
        using var writer = Open(source);
        Sql(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        // Simulate the exact old schema, whose logs have no undo snapshot extension.
        Sql(writer, "DROP TABLE review_snapshots;");
        Sql(writer, "UPDATE words SET translation='committed only in WAL',review_count=3; INSERT INTO app_settings(id,settings_json) VALUES(1,'{\"provider\":\"custom\",\"rememberKey\":false}');");
        Check(File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length > 0, "fixture must contain committed WAL pages");
        var sourceHash = Hash(source);
        var walHash = Hash(source + "-wal");
        Check(VocabularyService.TryMigrateDatabase(source, target, out var message), message);
        Check(Hash(source) == sourceHash && Hash(source + "-wal") == walHash, "migration must not mutate source DB/WAL");
        using (var migrated = new VocabularyService(target))
        {
            var word = migrated.GetAllWords().Single();
            Check(word.Translation == "committed only in WAL" && word.ReviewCount == 3, "backup lost WAL content");
            Check(migrated.LoadSettings().Provider == "custom", "legacy camelCase settings lost");
            migrated.ExecuteBatch([word.Id], "review");
            Check(migrated.UndoLastReview(word.Id), "migrated schema did not support undo");
            Check(migrated.GetAllWords().Single().ReviewCount == 3, "migration changed review count");
        }
        Check(Hash(source) == sourceHash && Hash(source + "-wal") == walHash, "new version must never write legacy data");
        Check(!Directory.EnumerateFiles(root, ".lexi-migration-*").Any(), "temporary migration artifacts remain");
    }

    private static void MigrationFailures(string root)
    {
        var broken = Path.Combine(root, "broken.sqlite3");
        var target = Path.Combine(root, "failed-target.sqlite3");
        File.WriteAllText(broken, "not a sqlite database");
        Check(!VocabularyService.TryMigrateDatabase(broken, target, out var reason), "invalid DB accepted");
        Check(reason.StartsWith("迁移失败") && !File.Exists(target), "failed migration leaves target or loses error");
        var incomplete = Path.Combine(root, "incomplete.sqlite3");
        using (var db = Open(incomplete)) Sql(db, "CREATE TABLE words(id INTEGER PRIMARY KEY);");
        Check(!VocabularyService.TryMigrateDatabase(incomplete, target, out _), "incomplete schema accepted");
        Check(!File.Exists(target), "schema rejection left an empty target");
        File.WriteAllText(target, "preserve-existing");
        Check(!VocabularyService.TryMigrateDatabase(broken, target, out _), "existing target should not be overwritten");
        Check(File.ReadAllText(target) == "preserve-existing", "existing target was overwritten");
        Check(!Directory.EnumerateFiles(root, ".lexi-migration-*").Any(), "failed migration left temporary files");
        using var isolated = new VocabularyService(Path.Combine(root, "isolated.sqlite3"));
        Check(isolated.GetAllWords().Count == 0 && isolated.MigrationMessage == "", "custom database paths must not migrate user data");
    }

    private static void UndoStateAndGuards(string root)
    {
        var path = Path.Combine(root, "undo.sqlite3");
        using var store = new VocabularyService(path);
        store.AddWord("first", "", "一", "");
        var id = store.GetAllWords().Single().Id;
        using (var db = Open(path)) Sql(db, "UPDATE words SET last_reviewed_at='2020-01-02 03:04:05',review_count=7;");
        var before = store.GetAllWords().Single();
        store.ExecuteBatch([id, id], "review");
        store.ExecuteBatch([id], "review");
        Check(store.GetAllWords().Single().Stage == 1, "same-day or duplicate IDs advance twice");
        Check(store.UndoLastReview(id), "review was not undone");
        var restored = store.GetAllWords().Single();
        Check(restored.Stage == before.Stage && restored.Status == before.Status && restored.NextReviewDate == before.NextReviewDate
            && restored.LearningStartDate == before.LearningStartDate && restored.LastReviewedAt == before.LastReviewedAt
            && restored.ReviewCount == before.ReviewCount, "undo must restore the full previous state");
        Check(!store.UndoLastReview(id), "duplicate undo should do nothing");
        foreach (var action in new[] { "master", "restart", "stage", "today" })
        {
            store.AddWord(action, "", "", "");
            var actionId = store.GetAllWords().Single(w => w.Word == action).Id;
            store.ExecuteBatch([actionId], "review");
            store.ExecuteBatch([actionId], action, action == "stage" ? 3 : null);
            var state = store.GetAllWords().Single(w => w.Id == actionId);
            Check(!store.UndoLastReview(actionId), "undo crossed a later " + action + " action");
            var still = store.GetAllWords().Single(w => w.Id == actionId);
            Check(still.Stage == state.Stage && still.Status == state.Status && still.NextReviewDate == state.NextReviewDate, "blocked undo mutated state");
        }
        store.ExecuteBatch([id], "review");
        store.AddWord("newer-not-reviewed", "", "", "");
        Check(store.UndoMostRecentReview(), "global undo selected newest word rather than latest eligible review");
        Check(store.GetAllWords().Single(w => w.Id == id).Stage == 0, "global undo reverted the wrong word");
        Check(!store.UndoMostRecentReview(), "global undo crossed blocked history");
        // Imported legacy logs have no snapshot: recover prior timestamp from the log.
        store.ExecuteBatch([id], "review");
        using (var db = Open(path))
        {
            Sql(db, "DELETE FROM review_snapshots; UPDATE review_logs SET log_date='2000-01-01' WHERE action='batch_review';");
        }
        var prior = store.GetAllWords().Single(w => w.Id == id).LastReviewedAt;
        store.ExecuteBatch([id], "review");
        using (var db = Open(path)) Sql(db, "DELETE FROM review_snapshots;");
        Check(store.UndoLastReview(id), "legacy review undo failed");
        Check(store.GetAllWords().Single(w => w.Id == id).LastReviewedAt == prior, "legacy timestamp not reconstructed");
    }

    private static void SchedulingAndSettings(string root)
    {
        var path = Path.Combine(root, "schedule.sqlite3");
        using var store = new VocabularyService(path);
        store.AddWord("schedule", "", "original", "");
        store.AddWord("SCHEDULE", "sound", "overwrite", "definition");
        var item = store.GetAllWords().Single();
        Check(item.Translation == "original" && item.Phonetic == "sound", "duplicate add overwrote user text");
        for (var stage = 0; stage < 5; stage++)
        {
            store.ExecuteBatch([item.Id], "stage", stage);
            Check(store.GetAllWords().Single().NextReviewDate == DateTime.Today.AddDays(VocabularyService.StageOffsets[stage]).ToString("yyyy-MM-dd"), "incorrect stage date");
        }
        try { store.ExecuteBatch([item.Id, long.MaxValue], "delete"); throw new Exception("missing ID accepted"); }
        catch (InvalidOperationException) { }
        Check(store.GetAllWords().Count == 1, "atomic delete removed a valid word before rejecting missing ID");
        store.ExecuteBatch([item.Id], "stage", 2);
        using (var db = Open(path)) Sql(db, "UPDATE words SET next_review_date='2000-01-01',learning_start_date='1999-12-28';");
        store.ExecuteBatch([item.Id], "review");
        Check(store.GetAllWords().Single().NextReviewDate == DateTime.Today.AddDays(3).ToString("yyyy-MM-dd"), "late review did not preserve stage gap");
        store.SaveSettings(new AppSettings { ApiKey = "fake-test-secret", RememberKey = false });
        Check(store.LoadSettings().ApiKey == "", "API key persisted without opt-in");
        store.SaveSettings(new AppSettings { ApiKey = "fake-test-secret", RememberKey = true });
        Check(store.LoadSettings().ApiKey == "fake-test-secret", "opted-in API key not preserved");
    }
}
