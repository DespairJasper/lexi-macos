using Microsoft.Data.Sqlite;

namespace Lexi;

public sealed partial class VocabularyService
{
    private bool NeedsStorageMaintenanceBackup()
    {
        using var cmd = _connection.CreateCommand();
        long Pragma(string name)
        {
            cmd.CommandText = "PRAGMA " + name;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        var free = Pragma("freelist_count");
        var pages = Pragma("page_count");
        var pageSize = Pragma("page_size");
        // A previous launch may have deleted snapshots before VACUUM ran out
        // of space. Rotate a validated backup before retrying that maintenance.
        if (free * pageSize >= 32L * 1024 * 1024 && free >= pages / 4) return true;
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='mutation_outbox'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) return false;
        cmd.CommandText = "SELECT COALESCE(SUM(length(CAST(payload_json AS BLOB))),0) FROM mutation_outbox WHERE applied_at_utc IS NOT NULL AND kind=$kind";
        cmd.Parameters.AddWithValue("$kind", CrossStoreJournal.MutationKind);
        return Convert.ToInt64(cmd.ExecuteScalar()) >= 32L * 1024 * 1024;
    }
    /// <summary>3.2.1 compatibility maintenance; no business rows or schema changes.</summary>
    private void MaintainStorage()
    {
        try
        {
            int removed;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM mutation_outbox WHERE applied_at_utc IS NOT NULL AND kind = $kind";
                cmd.Parameters.AddWithValue("$kind", CrossStoreJournal.MutationKind);
                removed = cmd.ExecuteNonQuery();
            }
            using var maintenance = _connection.CreateCommand();
            long Pragma(string name)
            {
                maintenance.CommandText = "PRAGMA " + name;
                return Convert.ToInt64(maintenance.ExecuteScalar());
            }
            var free = Pragma("freelist_count");
            var pages = Pragma("page_count");
            var pageSize = Pragma("page_size");
            var compact = free * pageSize >= 32L * 1024 * 1024 && free >= pages / 4;
            if (compact)
            {
                // SQLite owns rollback and file replacement. Never delete a live
                // database/sidecar or rewrite memory state to reclaim space.
                maintenance.CommandText = "VACUUM";
                maintenance.ExecuteNonQuery();
                DatabaseSafety.Validate(_connection);
            }
            if (removed > 0 || compact) BackupCommittedState(force: true, keepOnlyLatest: true);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            // Freed pages remain reusable even if disk space blocks VACUUM.
            // Retry next launch; normal committed learning data is still usable.
            BackupWarning = "数据已保存，但存储整理未完成；下次启动将重试。请检查磁盘空间与权限。";
        }
    }
}
