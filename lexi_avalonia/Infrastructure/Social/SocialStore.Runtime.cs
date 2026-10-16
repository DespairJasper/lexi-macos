using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lexi.Social;

public sealed partial class SocialStore
{
    private readonly object _backupGate = new();
    public SocialBindingState PrepareBinding(Guid actor, Guid binding, string nickname,
        DateTimeOffset proposedAt, IReadOnlyDictionary<string, IReadOnlySet<string>> exclusions, bool explicitlyAuthorized)
    {
        if (!explicitlyAuthorized) throw new InvalidOperationException("Explicit dataset binding authorization is required.");
        RequireId(actor); RequireId(binding); ArgumentNullException.ThrowIfNull(exclusions);
        nickname = nickname.Trim();
        if (nickname.Length is < 1 or > 32 || exclusions.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("Invalid binding intent.");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertExistingOwner(connection, transaction, actor);
        var previous = ReadBinding(connection, transaction, actor);
        if (previous is not null) { transaction.Commit(); return previous; }
        using var owner = Command(connection, transaction, "SELECT binding_id FROM social_owner WHERE singleton=1");
        if (owner.ExecuteScalar() is string old && old != Id(binding)) throw new InvalidOperationException("Existing binding cannot be replaced.");
        var day = SocialDayRules.DateAt(proposedAt);
        // Stable IDs stay only in this local table. Capture once, never replace on retry.
        var json = JsonSerializer.Serialize(exclusions.ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray()));
        using var insert = Command(connection, transaction, """
            INSERT OR IGNORE INTO social_owner(singleton,owner_id,binding_id) VALUES(1,$owner,$binding);
            INSERT INTO social_binding_intent(singleton,owner_id,binding_id,proposed_at,exclusion_date,exclusions,nickname)
            VALUES(1,$owner,$binding,$at,$day,$exclusions,$name);
            """, ("$owner", Id(actor)), ("$binding", Id(binding)), ("$at", Instant(proposedAt)),
            ("$day", Day(day)), ("$exclusions", json), ("$name", nickname));
        insert.ExecuteNonQuery(); transaction.Commit();
        return new(actor, binding, proposedAt, day, null, null, nickname);
    }

    public SocialBindingState? GetBinding(Guid actor)
    {
        RequireId(actor); using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertExistingOwner(connection, transaction, actor);
        var state = ReadBinding(connection, transaction, actor); transaction.Commit(); return state;
    }

    public void ActivateBinding(Guid actor, Guid binding, DateTimeOffset boundAt, DateOnly firstDay)
    {
        if (SocialDayRules.DateAt(boundAt) != firstDay) throw new InvalidOperationException("Cloud binding date is invalid.");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        var prior = ReadBinding(connection, transaction, actor) ?? throw new InvalidOperationException("No durable binding intent.");
        if (prior.BindingId != binding || prior.BoundAt is { } existing && (existing != boundAt || prior.FirstDay != firstDay))
            throw new InvalidOperationException("Cloud binding does not match the authorized intent.");
        using var update = Command(connection, transaction,
            "UPDATE social_binding_intent SET bound_at=$at,first_day=$day WHERE singleton=1", ("$at", Instant(boundAt)), ("$day", Day(firstDay)));
        update.ExecuteNonQuery(); transaction.Commit();
    }

    public void ConfirmBindingBaseline(Guid actor, Guid bindingId, DateTimeOffset boundAt,
        IReadOnlyDictionary<string,IReadOnlySet<string>> completedIdsByPlan)
    {
        ArgumentNullException.ThrowIfNull(completedIdsByPlan);
        if (completedIdsByPlan.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("Invalid completed-ID baseline.");
        var json = JsonSerializer.Serialize(completedIdsByPlan.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray()));
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        var binding = ReadBinding(connection, transaction, actor);
        if (binding?.BoundAt != boundAt || binding.BindingId != bindingId) throw new InvalidOperationException("Baseline must use the actual authorized cloud BoundAt.");
        using var read = Command(connection, transaction, "SELECT exclusions FROM social_binding_baseline WHERE singleton=1");
        if (read.ExecuteScalar() is string previous)
        {
            if (previous != json) throw new InvalidOperationException("Confirmed first-day baseline is immutable.");
            transaction.Commit(); return;
        }
        using var insert = Command(connection, transaction, """
            INSERT INTO social_binding_baseline(singleton,binding_id,bound_at,exclusion_date,exclusions)
            VALUES(1,$binding,$at,$day,$exclusions);
            """, ("$binding", Id(bindingId)), ("$at", Instant(boundAt)), ("$day", Day(binding.FirstDay!.Value)), ("$exclusions", json));
        insert.ExecuteNonQuery(); transaction.Commit();
    }

    public IReadOnlySet<string> GetBindingExclusions(Guid actor, string planId, DateOnly date)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        var binding = ReadBinding(connection, transaction, actor) ?? throw new InvalidOperationException("No binding intent.");
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (date == binding.FirstDay && binding.FirstDayBaselineConfirmed || date == binding.ExclusionDate)
        {
            using var read = Command(connection, transaction, date == binding.FirstDay && binding.FirstDayBaselineConfirmed
                ? "SELECT exclusions FROM social_binding_baseline WHERE singleton=1" : "SELECT exclusions FROM social_binding_intent WHERE singleton=1");
            var all = JsonSerializer.Deserialize<Dictionary<string,string[]>>((string)read.ExecuteScalar()!)!;
            if (all.TryGetValue(planId, out var ids)) values.UnionWith(ids);
        }
        transaction.Commit(); return values;
    }

    /// <summary>Caller reads already durable learning facts, applying GetBindingExclusions via FromPlan.
    /// New/review/seconds cover only events since BoundAt. No learning database is opened here.</summary>
    public SocialDayProjection ObserveDay(Guid actor, SocialDayObservation facts, bool timeTrusted, bool recovery = false)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var eligibility = PlanDayEligibilityRules.Evaluate(facts.Plans, facts.Cause);
        if (facts.NewWords is < 0 or > 1000000 || facts.ReviewWords is < 0 or > 1000000 || eligibility.Target > 1000000 ||
            !double.IsFinite(facts.ActiveSeconds) || facts.ActiveSeconds is < 0 or > 86400 ||
            facts.Date > SocialDayRules.DateAt(facts.ObservedAt) ||
            facts.LastActualCompletionAt is { } actual && (SocialDayRules.DateAt(actual) != facts.Date || actual > facts.ObservedAt))
            throw new ArgumentException("Invalid durable daily facts.");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        var binding = ReadBinding(connection, transaction, actor);
        if (binding?.BoundAt is null || facts.Date < binding.FirstDay) throw new InvalidOperationException("No active binding or date precedes binding.");
        var prior = ReadDay(connection, transaction, facts.Date);
        var cause = recovery ? QualificationCause.Correction : facts.Cause;
        DateTimeOffset? at = eligibility.Qualified
            ? (facts.Cause == QualificationCause.PlanChange ? facts.ObservedAt : facts.LastActualCompletionAt)
            : null;
        var trusted = at is not null && cause == QualificationCause.LearningComplete && timeTrusted && !recovery;
        if (at <= binding.BoundAt || at is { } chosen && SocialDayRules.DateAt(chosen) != facts.Date) at = null;
        // Polls and later corrections never replace the actual event that first qualified this day.
        if (eligibility.Qualified && prior?.Summary.QualifiedAt is { } original)
        {
            at = original;
            if (!recovery) { cause = prior.Summary.Cause; trusted = prior.Summary.TimeTrusted; }
        }
        if (facts.Date == binding.FirstDay && !binding.FirstDayBaselineConfirmed) at = null;
        if (at is null) trusted = false;
        using var revisions = Command(connection, transaction, "SELECT revision FROM social_day_revisions WHERE study_date=$day", ("$day", Day(facts.Date)));
        var revision = Convert.ToInt64(revisions.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        var summary = new SocialDailySummary(binding.BindingId, facts.Date, revision, eligibility.Target, eligibility.Completed,
            facts.NewWords, facts.ReviewWords, at, cause, trusted);
        var changed = prior is null || prior.Summary != summary;
        if (changed) summary = summary with { Revision = checked(revision + 1) };
        Validate(summary);
        var payload = JsonSerializer.Serialize(summary, JsonOptions);
        using var project = Command(connection, transaction, """
            INSERT INTO social_projections(study_date,facts,payload) VALUES($day,$facts,$payload)
            ON CONFLICT(study_date) DO UPDATE SET facts=excluded.facts,payload=excluded.payload;
            """, ("$day", Day(facts.Date)), ("$facts", JsonSerializer.Serialize(facts, JsonOptions)), ("$payload", payload));
        project.ExecuteNonQuery();
        if (changed)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            using var enqueue = Command(connection, transaction, """
                INSERT INTO social_batches(batch_id,owner_id,study_date,revision,payload_hash,payload)
                VALUES($id,$owner,$day,$revision,$hash,$payload);
                INSERT INTO social_day_revisions(study_date,revision) VALUES($day,$revision)
                ON CONFLICT(study_date) DO UPDATE SET revision=excluded.revision;
                """, ("$id", Id(Guid.NewGuid())), ("$owner", Id(actor)), ("$day", Day(facts.Date)),
                ("$revision", summary.Revision), ("$hash", hash), ("$payload", payload));
            enqueue.ExecuteNonQuery();
        }
        transaction.Commit(); return new(facts, summary);
    }

    public SocialDayProjection? LoadDay(Guid actor, DateOnly date)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor); var day = ReadDay(connection, transaction, date); transaction.Commit(); return day;
    }
    public SocialClockDiagnostic? LoadClockDiagnostic(Guid actor)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); AssertOwner(connection, transaction, actor);
        using var read = Command(connection, transaction, "SELECT payload FROM social_clock_diagnostic WHERE singleton=1");
        var result = read.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<SocialClockDiagnostic>(payload, JsonOptions) : null;
        transaction.Commit(); return result;
    }
    internal void RecordClockDiagnostic(Guid actor, SocialClockDiagnostic anchor)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); AssertOwner(connection, transaction, actor);
        using var update = Command(connection, transaction, """
            INSERT INTO social_clock_diagnostic(singleton,payload) VALUES(1,$payload)
            ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload;
            """, ("$payload", JsonSerializer.Serialize(anchor, JsonOptions)));
        update.ExecuteNonQuery(); transaction.Commit();
    }

    public void RecordReceipt(Guid actor, Guid batchId, string expectedHash, string status, DateTimeOffset receivedAt)
    {
        if (status is not ("accepted" or "stale" or "late")) throw new ArgumentException("Invalid receipt status.");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var read = Command(connection, transaction,
            "SELECT study_date FROM social_batches WHERE batch_id=$id AND owner_id=$owner AND payload_hash=$hash",
            ("$id", Id(batchId)), ("$owner", Id(actor)), ("$hash", expectedHash));
        var day = read.ExecuteScalar() as string ?? throw new InvalidOperationException("Receipt mismatch.");
        using var update = Command(connection, transaction, """
            INSERT INTO social_receipts(batch_id,study_date,status,received_at) VALUES($id,$day,$status,$at)
            ON CONFLICT(batch_id) DO NOTHING;
            UPDATE social_batches SET acknowledged=1,payload=NULL WHERE batch_id=$id AND owner_id=$owner AND payload_hash=$hash;
            """, ("$id", Id(batchId)), ("$owner", Id(actor)), ("$hash", expectedHash), ("$day", day),
            ("$status", status), ("$at", Instant(receivedAt)));
        update.ExecuteNonQuery(); transaction.Commit();
    }

    public IReadOnlyList<SocialStoredReceipt> LoadReceipts(Guid actor, int limit = 100)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var read = Command(connection, transaction,
            "SELECT batch_id,study_date,status,received_at FROM social_receipts ORDER BY received_at DESC LIMIT $limit", ("$limit", limit));
        var receipts = new List<SocialStoredReceipt>();
        using (var reader = read.ExecuteReader())
            while (reader.Read()) receipts.Add(new(Guid.Parse(reader.GetString(0)), DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture), reader.GetString(2), ParseInstant(reader.GetString(3))));
        transaction.Commit(); return receipts;
    }

    /// <summary>Keep all pending work, recent 8-day receipts and 30-day projections. Never expire pending work from a device clock.</summary>
    public void PruneConfirmed(Guid actor, DateOnly today)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var prune = Command(connection, transaction, """
            DELETE FROM social_receipts WHERE study_date<$receipts;
            DELETE FROM social_batches WHERE acknowledged=1 AND study_date<$receipts;
            DELETE FROM social_projections WHERE study_date<$facts AND NOT EXISTS
                (SELECT 1 FROM social_batches b WHERE b.study_date=social_projections.study_date AND b.acknowledged=0);
            DELETE FROM social_day_revisions WHERE study_date<$facts AND NOT EXISTS
                (SELECT 1 FROM social_batches b WHERE b.study_date=social_day_revisions.study_date AND b.acknowledged=0);
            """, ("$receipts", Day(today.AddDays(-7))), ("$facts", Day(today.AddDays(-29))));
        prune.ExecuteNonQuery(); transaction.Commit();
    }

    /// <summary>Independent social-only backups: SQLite Backup API, 15-minute throttle, latest 3 snapshots.
    /// Existing learning backup behavior is deliberately outside this store.</summary>
    public string? BackupIfChanged(string backupDirectory, DateTimeOffset now)
    {
        lock (_backupGate) return CreateSocialBackup(backupDirectory, now);
    }
    private string? CreateSocialBackup(string backupDirectory, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        Directory.CreateDirectory(backupDirectory);
        // Scope rotation/marker to this sidecar; never rotate another dataset's files.
        var prefix = "social-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path))).ToLowerInvariant()[..12] + "-";
        var markerPath = Path.Combine(backupDirectory, prefix + "backup-state.json");
        var source = new FileInfo(_path);
        var fingerprint = _path + "|" + source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + source.Length.ToString(CultureInfo.InvariantCulture);
        if (File.Exists(markerPath))
        {
            var prior = JsonSerializer.Deserialize<BackupMarker>(File.ReadAllText(markerPath));
            if (prior is not null && (prior.Fingerprint == fingerprint || now - prior.At < TimeSpan.FromMinutes(15))) return null;
        }
        var name = prefix + now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".sqlite3";
        var path = Path.Combine(Path.GetFullPath(backupDirectory), name);
        using (var connection = Open())
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            destination.Open(); connection.BackupDatabase(destination);
        }
        var temporary = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new BackupMarker(fingerprint, now)));
        File.Move(temporary, markerPath, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(backupDirectory, prefix + "*.sqlite3").OrderByDescending(File.GetLastWriteTimeUtc).Skip(3))
            File.Delete(old);
        return path;
    }
    private sealed record BackupMarker(string Fingerprint, DateTimeOffset At);

    private static SocialDayProjection? ReadDay(SqliteConnection connection, SqliteTransaction transaction, DateOnly date)
    {
        using var read = Command(connection, transaction, "SELECT facts,payload FROM social_projections WHERE study_date=$day", ("$day", Day(date)));
        using var reader = read.ExecuteReader();
        return reader.Read() ? new(JsonSerializer.Deserialize<SocialDayObservation>(reader.GetString(0), JsonOptions)!,
            JsonSerializer.Deserialize<SocialDailySummary>(reader.GetString(1), JsonOptions)!) : null;
    }
    private static SocialBindingState? ReadBinding(SqliteConnection connection, SqliteTransaction transaction, Guid actor)
    {
        using var read = Command(connection, transaction, """
            SELECT owner_id,binding_id,proposed_at,exclusion_date,bound_at,first_day,nickname,
            EXISTS(SELECT 1 FROM social_binding_baseline b WHERE b.binding_id=social_binding_intent.binding_id AND b.bound_at=social_binding_intent.bound_at)
            FROM social_binding_intent WHERE singleton=1 AND owner_id=$owner
            """, ("$owner", Id(actor)));
        using var reader = read.ExecuteReader();
        return reader.Read() ? new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), ParseInstant(reader.GetString(2)),
            DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture), reader.IsDBNull(4) ? null : ParseInstant(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture), reader.GetString(6)) { FirstDayBaselineConfirmed = reader.GetInt32(7) == 1 } : null;
    }
    private static void AssertExistingOwner(SqliteConnection connection, SqliteTransaction transaction, Guid actor)
    {
        using var read = Command(connection, transaction, "SELECT owner_id FROM social_owner WHERE singleton=1");
        if (read.ExecuteScalar() is string owner && owner != Id(actor)) throw new InvalidOperationException("Account cannot access this local dataset.");
    }
    private static string Instant(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseInstant(string at) => DateTimeOffset.Parse(at, CultureInfo.InvariantCulture);
}
