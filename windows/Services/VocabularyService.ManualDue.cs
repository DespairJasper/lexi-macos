using Microsoft.Data.Sqlite;

namespace Lexi;

public sealed partial class VocabularyService
{
    private void ApplyTodayMemoryOverride(SqliteTransaction tx, long wordId, long logId)
    {
        using var schema = _connection.CreateCommand(); schema.Transaction = tx;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS archive_memory_due_undo(
                log_id INTEGER PRIMARY KEY REFERENCES review_logs(id) ON DELETE CASCADE,
                word_key TEXT NOT NULL, old_due TEXT, canonical_seq INTEGER, override_id INTEGER);
            """;
        schema.ExecuteNonQuery();
        using var read = _connection.CreateCommand(); read.Transaction = tx;
        read.CommandText = """
            SELECT c.word_key,c.next_review_at_utc,c.last_applied_canonical_seq
            FROM fsrs_cards c JOIN word_archives a ON c.word_key='archive:' || a.uuid
            WHERE a.word_id=$wordId;
            """;
        read.Parameters.AddWithValue("$wordId", wordId);
        string key; string? due; long? seq;
        using (var r = read.ExecuteReader())
        {
            if (!r.Read()) return;
            key = r.GetString(0); due = r.IsDBNull(1) ? null : r.GetString(1);
            seq = r.IsDBNull(2) ? null : r.GetInt64(2);
        }
        using var before = _connection.CreateCommand(); before.Transaction = tx;
        before.CommandText = "SELECT COALESCE(MAX(override_id),0) FROM fsrs_manual_due_overrides";
        var priorOverride = Convert.ToInt64(before.ExecuteScalar());
        SyncCardDueToLocalDate(tx, wordId, DateTime.Today, "manual-today");
        var afterOverride = Convert.ToInt64(before.ExecuteScalar());
        using var snapshot = _connection.CreateCommand(); snapshot.Transaction = tx;
        snapshot.CommandText = "INSERT INTO archive_memory_due_undo VALUES($log,$key,$due,$seq,$override)";
        snapshot.Parameters.AddWithValue("$log", logId); snapshot.Parameters.AddWithValue("$key", key);
        snapshot.Parameters.AddWithValue("$due", (object?)due ?? DBNull.Value);
        snapshot.Parameters.AddWithValue("$seq", (object?)seq ?? DBNull.Value);
        snapshot.Parameters.AddWithValue("$override", afterOverride > priorOverride ? afterOverride : DBNull.Value);
        snapshot.ExecuteNonQuery();
    }

    private bool UndoTodayMemoryOverride(SqliteTransaction tx, long logId)
    {
        using var exists = _connection.CreateCommand(); exists.Transaction = tx;
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE name='archive_memory_due_undo'";
        if (exists.ExecuteScalar() == null) return true;
        using var read = _connection.CreateCommand(); read.Transaction = tx;
        read.CommandText = "SELECT word_key,old_due,canonical_seq,override_id FROM archive_memory_due_undo WHERE log_id=$log";
        read.Parameters.AddWithValue("$log", logId);
        string key; string? due; long? seq, overrideId;
        using (var r = read.ExecuteReader())
        {
            if (!r.Read()) return true;
            key = r.GetString(0); due = r.IsDBNull(1) ? null : r.GetString(1);
            seq = r.IsDBNull(2) ? null : r.GetInt64(2); overrideId = r.IsDBNull(3) ? null : r.GetInt64(3);
        }
        using var update = _connection.CreateCommand(); update.Transaction = tx;
        update.CommandText = "UPDATE fsrs_cards SET next_review_at_utc=$due WHERE word_key=$key AND last_applied_canonical_seq IS $seq";
        update.Parameters.AddWithValue("$due", (object?)due ?? DBNull.Value);
        update.Parameters.AddWithValue("$key", key); update.Parameters.AddWithValue("$seq", (object?)seq ?? DBNull.Value);
        if (update.ExecuteNonQuery() != 1) return false;
        if (overrideId is { } id)
        {
            using var delete = _connection.CreateCommand(); delete.Transaction = tx;
            delete.CommandText = "DELETE FROM fsrs_manual_due_overrides WHERE override_id=$id";
            delete.Parameters.AddWithValue("$id", id); delete.ExecuteNonQuery();
        }
        return true;
    }
}
