using System;
using Microsoft.Data.Sqlite;

namespace Lexi;

public sealed partial class VocabularyService
{
    /// <summary>One-time, additive migration of proven pre-optimizer management due changes.
    /// Only the latest effective canonical's own decision is evidence; earlier decisions are irrelevant.
    /// No card or legacy projection is rewritten. Unknown sources are not inferred.</summary>
    private void CaptureLegacyManagedDue(SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS fsrs_optimizer_migrations (
                migration_id TEXT PRIMARY KEY, applied_at_utc TEXT NOT NULL);
            INSERT INTO fsrs_manual_due_overrides
                (word_key, anchor_canonical_id, anchor_revision, due_at_utc, reason, created_at_utc)
            SELECT c.word_key, r.canonical_id, r.revision, c.next_review_at_utc,
                   'legacy-card-due-v1', $now
            FROM fsrs_cards c JOIN canonical_reviews r
              ON r.rowid = c.last_applied_canonical_seq AND r.word_key = c.word_key
            WHERE r.invalidated = 0
              AND r.rowid = (SELECT MAX(r2.rowid) FROM canonical_reviews r2
                             WHERE r2.word_key=c.word_key AND r2.invalidated=0)
              AND c.last_review_at_utc = r.reviewed_at_utc
              AND c.next_review_at_utc IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM fsrs_optimizer_migrations WHERE migration_id='legacy-card-due-v1')
              AND NOT EXISTS (
                    SELECT 1 FROM personalization_models p WHERE p.model_kind='fsrs_params'
                    AND CASE WHEN json_valid(p.metrics_json)
                        THEN json_extract(p.metrics_json,'$.recordKind')='params'
                        ELSE 1 END)
              AND NOT EXISTS (SELECT 1 FROM fsrs_manual_due_overrides o
                              WHERE o.word_key=c.word_key AND o.anchor_canonical_id=r.canonical_id
                                AND o.anchor_revision=r.revision)
              AND c.next_review_at_utc <> (
                    SELECT d.final_due_at_utc FROM scheduler_decisions d
                    WHERE d.word_key=c.word_key AND d.canonical_id=r.canonical_id
                    ORDER BY d.rowid DESC LIMIT 1);
            INSERT INTO fsrs_optimizer_migrations (migration_id, applied_at_utc)
            VALUES ('legacy-card-due-v1', $now) ON CONFLICT(migration_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$now", UtcText(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }
}
