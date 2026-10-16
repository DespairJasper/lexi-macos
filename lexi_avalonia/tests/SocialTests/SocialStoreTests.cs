using Lexi.Social;

internal static class SocialStoreTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "lexi-social-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var owner = Guid.NewGuid(); var other = Guid.NewGuid(); var binding = Guid.NewGuid(); var batch = Guid.NewGuid();
            var summary = new SocialDailySummary(binding, new DateOnly(2026, 10, 10), 1, 20, 20,
                20, 0, new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), QualificationCause.LearningComplete);
            var original = Path.Combine(root, "lexi.sqlite3");
            File.WriteAllText(original, "untouched-learning-database-marker");
            var store = new SocialStore(root);
            store.BindOwner(owner, binding);
            store.BindOwner(owner, binding);
            check(File.ReadAllText(original) == "untouched-learning-database-marker", "sidecar creation never opens or overwrites the original learning database");
            var hash = store.Enqueue(owner, batch, summary);
            check(hash.Length == 64 && store.LoadPending(owner).Count == 1, "a small summary is durably queued");
            check(store.Enqueue(owner, batch, summary) == hash && store.LoadPending(owner).Count == 1,
                "same batch and payload is idempotent");
            var reopened = new SocialStore(root);
            check(reopened.LoadPending(owner).Single().Summary == summary, "pending work survives process/store reopening");
            void Deny(Action operation, string message)
            {
                try { operation(); } catch (InvalidOperationException) { check(true, message); return; }
                check(false, message);
            }
            Deny(() => reopened.BindOwner(other, Guid.NewGuid()), "another account cannot claim the existing local dataset");
            Deny(() => reopened.LoadPending(other), "another account cannot read a queue");
            Deny(() => reopened.Enqueue(other, Guid.NewGuid(), summary), "another account cannot append to an owner's queue");
            Deny(() => reopened.Enqueue(owner, batch, summary with { Completed = 19, QualifiedAt = null }), "same batch with a changed payload must be rejected");
            Deny(() => reopened.Enqueue(owner, Guid.NewGuid(), summary with { BindingId = Guid.NewGuid() }), "foreign binding cannot enter this queue");
            check(!reopened.Acknowledge(owner, batch, new string('0', 64)), "a mismatched receipt hash cannot erase pending study");
            check(reopened.LoadPending(owner).Count == 1, "failed receipt verification leaves the original pending summary");
            check(reopened.Acknowledge(owner, batch, hash), "matching receipt clears only its own batch payload");
            check(reopened.LoadPending(owner).Count == 0, "confirmed summaries no longer remain in the upload queue");
            check(reopened.Acknowledge(owner, batch, hash), "duplicate server receipt is harmless");
            check(reopened.Enqueue(owner, batch, summary) == hash && reopened.LoadPending(owner).Count == 0,
                "replayed confirmed batch cannot be uploaded again");
            var newer = summary with { Revision = 2, Target = 21, Completed = 20, QualifiedAt = null, Cause = QualificationCause.PlanChange };
            var secondBatch = Guid.NewGuid(); reopened.Enqueue(owner, secondBatch, newer);
            check(reopened.LoadPending(owner).Single().Summary.Revision == 2, "a plan change produces its own newer pending revision");
            Deny(() => reopened.Enqueue(owner, Guid.NewGuid(), summary), "old revision cannot overwrite a newer local day");
            Deny(() => reopened.Acknowledge(other, secondBatch, hash), "foreign account acknowledgement cannot discard another owner's queue");
            check(File.ReadAllText(original) == "untouched-learning-database-marker", "queue and account operations preserve the learning database");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true); // Only this test's unique temporary directory.
        }
    }
}
