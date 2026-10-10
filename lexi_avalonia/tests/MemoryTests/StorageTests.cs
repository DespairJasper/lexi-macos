using Microsoft.Data.Sqlite;

namespace Lexi.Tests;

/// <summary>Real SQLite/file tests: acknowledged snapshots, backup amplification and legacy bloat.</summary>
public static class StorageTests
{
    public static void Journal()
    {
        using var box = new Sandbox();
        using var service = new VocabularyService(box.Db);
        var journal = new CrossStoreJournal(service, box.Dir);
        for (var i = 0; i < 32; i++)
        {
            service.EnqueueMutations([CrossStoreJournal.CreateJsonSnapshot("daily-study-plans.json",
                "{\"progress\":" + i + ",\"words\":\"" + new string('x', 65536) + "\"}")]);
            Program.Check(journal.Replay(DateTime.UtcNow).Succeeded, "真实文件同步成功");
        }
        Program.Check(Scalar(box.Db, "SELECT count(*) FROM mutation_outbox") == 0,
            "成功同步的完整快照不累积在数据库中");
        Program.Check(File.ReadAllText(Path.Combine(box.Dir, "daily-study-plans.json")).Contains("\"progress\":31"),
            "清理后最终JSON进度完整保留");

        service.EnqueueMutations([CrossStoreJournal.CreateJsonSnapshot("ielts-learning.json", "{\"progress\":42}"),
            new PendingMutation("future.kind", "{\"value\":7}")]);
        var pending = service.LoadPendingMutations();
        service.RecordMutationFailure(pending[0].Id, "simulated interrupted confirmation");
        Program.Check(service.LoadPendingMutations()[0].Attempts == 1, "失败待办和重试计数保留");
        service.MarkMutationApplied(pending[1].Id, DateTime.UtcNow);
        Program.Check(Scalar(box.Db, "SELECT count(*) FROM mutation_outbox WHERE kind='future.kind'") == 1,
            "未知种类即使确认也不被快照清理删除");
        using (var db = Open(box.Db))
            Exec(db, "CREATE TRIGGER interrupt_ack BEFORE DELETE ON mutation_outbox WHEN OLD.kind='json.snapshot.v1' BEGIN SELECT RAISE(ABORT,'interrupted acknowledgement'); END;");
        var interrupted = journal.Replay(DateTime.UtcNow);
        Program.Check(!interrupted.Succeeded && interrupted.Pending == 1, "确认事务失败时JSON写入不冒充全部成功");
        Program.Check(Scalar(box.Db, "SELECT count(*) FROM mutation_outbox WHERE kind='json.snapshot.v1' AND applied_at_utc IS NULL") == 1,
            "确认与回收事务一起回滚，完整待办仍未确认");
        var target = Path.Combine(box.Dir, "ielts-learning.json");
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-1));
        var stamp = File.GetLastWriteTimeUtc(target);
        using (var db = Open(box.Db)) Exec(db, "DROP TRIGGER interrupt_ack");
        Program.Check(journal.Replay(DateTime.UtcNow).Succeeded, "故障待办可继续恢复");
        Program.Check(File.GetLastWriteTimeUtc(target) == stamp, "确认失败后的重放不会重复改写已成功文件");
        Program.Check(File.ReadAllText(Path.Combine(box.Dir, "ielts-learning.json")) == "{\"progress\":42}",
            "故障恢复不丢学习进度");
        Program.Check(Scalar(box.Db, "SELECT count(*) FROM mutation_outbox WHERE kind='json.snapshot.v1'") == 0,
            "重试成功后也释放快照");
    }

    public static void Backups()
    {
        using var box = new Sandbox();
        using var service = new VocabularyService(box.Db);
        for (var i = 0; i < 32; i++)
            service.EnqueueMutations([CrossStoreJournal.CreateJsonSnapshot("daily-study-plans.json", "{\"i\":" + i + "}")]);
        Program.Check(Directory.GetFiles(box.Backups, "lexi-*.sqlite3").Length == 1,
            "频繁保存进度不会每次复制整个数据库");
        var automatic = service.LastBackupPath;
        var manual = service.CreateManualBackup();
        Program.Check(manual != automatic && File.Exists(manual), "手动备份始终立即创建独立副本");
        using var connection = Open(box.Db);
        var future = DateTime.UtcNow.AddHours(1);
        var due = DatabaseSafety.CreateAutomaticBackup(connection, box.Db, future);
        Program.Check(due != manual, "超过间隔时创建新的自动备份");
        var reuse = DatabaseSafety.CreateAutomaticBackup(connection, box.Db, future.AddMinutes(1));
        Program.Check(reuse == due, "间隔内复用完整备份");
        File.WriteAllText(due, "damaged backup");
        var repaired = DatabaseSafety.CreateAutomaticBackup(connection, box.Db, future.AddMinutes(2));
        Program.Check(repaired != due, "最近备份损坏时立即补建，不能节流到损坏文件");
        using (var incomplete = Open(repaired)) Exec(incomplete, "DROP TABLE words");
        var complete = DatabaseSafety.CreateAutomaticBackup(connection, box.Db, future.AddMinutes(3));
        Program.Check(complete != repaired, "物理完整但缺少业务schema的备份不能被复用");
        for (var i = 0; i < 24; i++) service.CreateManualBackup();
        Program.Check(Directory.GetFiles(box.Backups, "lexi-*.sqlite3").Length == 20, "完整备份数量受上限约束");

        using var big = new Sandbox();
        using var bigService = new VocabularyService(big.Db);
        using var bigConnection = Open(big.Db);
        Exec(bigConnection, "CREATE TABLE preserved_blob(data BLOB); INSERT INTO preserved_blob VALUES(zeroblob(140*1024*1024));");
        var first = bigService.CreateManualBackup();
        var unrelated = Path.Combine(big.Backups, "lexi-my-personal.sqlite3");
        File.WriteAllText(unrelated, "unmanaged backup");
        var second = bigService.CreateManualBackup();
        Program.Check(!File.Exists(first) && File.Exists(second), "备份总量受预算约束，仍保留最新完整副本");
        Program.Check(File.ReadAllText(unrelated) == "unmanaged backup", "清理不匹配任意用户命名文件");
        Program.Check(Scalar(second, "SELECT length(data) FROM preserved_blob") == 146800640,
            "预算管理没有裁剪备份中的业务数据");
    }

    public static void Legacy()
    {
        using var box = new Sandbox();
        using (var service = new VocabularyService(box.Db)) service.AddWord("preserved", "", "原词义", "原注释");
        using (var connection = Open(box.Db))
        {
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO mutation_outbox(kind,payload_json,created_at_utc,applied_at_utc) VALUES('json.snapshot.v1',$payload,'2026-10-10T00:00:00Z','2026-10-10T00:00:01Z')";
            cmd.Parameters.AddWithValue("$payload", new string('x', 4*1024*1024));
            for (var i = 0; i < 10; i++) cmd.ExecuteNonQuery();
            tx.Commit();
            Exec(connection, "INSERT INTO mutation_outbox(kind,payload_json,created_at_utc,applied_at_utc) VALUES('future.kind','preserve','2026-10-10T00:00:00Z','2026-10-10T00:00:01Z');");
        }
        var jsonPath = Path.Combine(box.Dir, "daily-study-plans.json");
        File.WriteAllText(jsonPath, "{\"progress\":123}");
        using (var service = new VocabularyService(box.Db))
            service.EnqueueMutations([CrossStoreJournal.CreateJsonSnapshot("ielts-learning.json", "{\"progress\":99}")]);
        // Simulate old 3.2.1 accumulation after a prior maintenance pass.
        using (var connection = Open(box.Db))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO mutation_outbox(kind,payload_json,created_at_utc,applied_at_utc) VALUES('json.snapshot.v1',$payload,'2026-10-10T00:00:00Z','2026-10-10T00:00:01Z')";
            cmd.Parameters.AddWithValue("$payload", new string('x', 4*1024*1024));
            for (var i = 0; i < 10; i++) cmd.ExecuteNonQuery();
        }
        var before = new FileInfo(box.Db).Length;
        using (var reopened = new VocabularyService(box.Db))
        {
            Program.Check(Scalar(box.Db, "SELECT count(*) FROM mutation_outbox WHERE applied_at_utc IS NOT NULL AND kind='json.snapshot.v1'") == 0,
                "旧版已确认快照在启动时自动清理");
            Program.Check(reopened.LoadPendingMutations().Count == 1, "启动清理完整保留待同步快照");
            Program.Check(Scalar(box.Db, "SELECT count(*) FROM mutation_outbox WHERE kind='future.kind'") == 1,
                "启动清理完整保留未知种类");
            Program.Check(Scalar(box.Db, "SELECT count(*) FROM words WHERE word='preserved' AND translation='原词义' AND definition='原注释'") == 1,
                "启动缩容完整保留词汇和内容");
            Program.Check(File.ReadAllText(jsonPath) == "{\"progress\":123}", "启动维护不改JSON进度");
            Program.Check(new FileInfo(box.Db).Length < 2*1024*1024 && before > 40*1024*1024,
                "清理后SQLite真实回收空闲磁盘空间");
            Program.Check(Directory.GetFiles(box.Backups, "lexi-*.sqlite3").Sum(f => new FileInfo(f).Length) < 8*1024*1024,
                "历史快照维护后仅保留干净备份，不继续存放膨胀副本");
            Program.Check(new CrossStoreJournal(reopened, box.Dir).Replay(DateTime.UtcNow).Succeeded,
                "缩容后原待办仍能按原协议重放");
            Program.Check(File.ReadAllText(Path.Combine(box.Dir, "ielts-learning.json")) == "{\"progress\":99}",
                "维护后故障恢复得到精确原进度");
        }
        using var final = Open(box.Db); DatabaseSafety.Validate(final);
        Program.Check(Scalar(box.Db, "PRAGMA freelist_count") < 512, "回收后没有大量闲置页");
    }

    public static void ClockRollback()
    {
        using var box = new Sandbox(); using var service = new VocabularyService(box.Db);
        service.AddWord("clock", "", "", "");
        using var db = Open(box.Db);
        var rolledBack = DateTime.UtcNow.AddDays(-1);
        var first = DatabaseSafety.CreateAutomaticBackup(db, box.Db, rolledBack);
        var second = DatabaseSafety.CreateAutomaticBackup(db, box.Db, rolledBack.AddMinutes(1));
        Program.Check(first == second, "时钟回拨后只补建一次备份，再次保存仍正常节流");
    }

    public static void LinkedDirectory()
    {
        using var box = new Sandbox();
        var foreign = Path.Combine(box.Dir, "foreign"); Directory.CreateDirectory(foreign);
        Directory.CreateSymbolicLink(box.Backups, foreign);
        using var service = new VocabularyService(box.Db);
        service.AddWord("safe", "", "", "");
        Program.Check(Directory.GetFiles(foreign).Length == 0, "链接备份目录不能向外部目录写入或轮转");
        Program.Check(service.GetAllWords().Count == 1 && service.BackupWarning.Length > 0,
            "拒绝链接备份目录不影响已提交的学习数据，只报告备份警告");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, Pooling=false }.ToString());
        connection.Open(); return connection;
    }
    private static void Exec(SqliteConnection connection, string sql)
    { using var cmd=connection.CreateCommand(); cmd.CommandText=sql; cmd.ExecuteNonQuery(); }
    private static long Scalar(string path,string sql)
    { using var connection=Open(path); using var cmd=connection.CreateCommand(); cmd.CommandText=sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "lexi-storage-test-" + Guid.NewGuid().ToString("N"));
        public string Db => Path.Combine(Dir, "vocab.sqlite3");
        public string Backups => Path.Combine(Dir,"backups");
        public Sandbox() => Directory.CreateDirectory(Dir);
        public void Dispose() => Directory.Delete(Dir,true);
    }
}
