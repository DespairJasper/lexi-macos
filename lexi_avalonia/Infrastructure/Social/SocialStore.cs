using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Lexi.Social;

/// <summary>Small social-only sidecar. It never connects to or migrates the learning database.</summary>
public sealed partial class SocialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    private readonly string _connectionString;
    private readonly string _path;

    public SocialStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(Path.GetFullPath(dataDirectory), "social.sqlite3");
        _path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        using var version = Command(connection, null, "PRAGMA user_version");
        if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) > 3)
            throw new InvalidOperationException("Social database belongs to a newer version; preserved without migration.");
        using var transaction = connection.BeginTransaction();
        using var schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS social_owner (
              singleton INTEGER PRIMARY KEY CHECK(singleton=1), owner_id TEXT NOT NULL, binding_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS social_batches (
              batch_id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, study_date TEXT NOT NULL,
              revision INTEGER NOT NULL CHECK(revision>0), payload_hash TEXT NOT NULL,
              payload TEXT, acknowledged INTEGER NOT NULL DEFAULT 0 CHECK(acknowledged IN (0,1)));
            CREATE TABLE IF NOT EXISTS social_day_revisions (
              study_date TEXT PRIMARY KEY, revision INTEGER NOT NULL CHECK(revision>0));
            CREATE INDEX IF NOT EXISTS social_batches_pending ON social_batches(owner_id, acknowledged, study_date, revision);
            CREATE TABLE IF NOT EXISTS social_binding_intent (
              singleton INTEGER PRIMARY KEY CHECK(singleton=1), owner_id TEXT NOT NULL, binding_id TEXT NOT NULL,
              proposed_at TEXT NOT NULL, exclusion_date TEXT NOT NULL, exclusions TEXT NOT NULL,
              nickname TEXT NOT NULL, bound_at TEXT, first_day TEXT);
            CREATE TABLE IF NOT EXISTS social_projections (
              study_date TEXT PRIMARY KEY, facts TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS social_receipts (
              batch_id TEXT PRIMARY KEY, study_date TEXT NOT NULL, status TEXT NOT NULL, received_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS social_clock_diagnostic (
              singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS social_binding_baseline (
              singleton INTEGER PRIMARY KEY CHECK(singleton=1), binding_id TEXT NOT NULL,
              bound_at TEXT NOT NULL, exclusion_date TEXT NOT NULL, exclusions TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS social_secondary (
              kind TEXT NOT NULL CHECK(kind IN ('duration','curve')), study_date TEXT NOT NULL,
              revision INTEGER NOT NULL, facts TEXT NOT NULL, payload TEXT NOT NULL, acknowledged INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(kind,study_date));
            CREATE TABLE IF NOT EXISTS social_secondary_sharing (
              singleton INTEGER PRIMARY KEY CHECK(singleton=1), share_time INTEGER NOT NULL, share_curve INTEGER NOT NULL);
            -- Additive only: the confirmed joint days we have already observed, kept per relationship
            -- (never per run, so reopening a run does not lose them) together with the server window
            -- those observations came from. Existing tables and pending queues are untouched.
            CREATE TABLE IF NOT EXISTS social_ui_days (
              relationship_id TEXT NOT NULL, study_date TEXT NOT NULL,
              joint TEXT NOT NULL CHECK(joint IN ('success','non_joint')),
              PRIMARY KEY(relationship_id, study_date));
            CREATE TABLE IF NOT EXISTS social_ui_coverage (
              relationship_id TEXT PRIMARY KEY, coverage_from TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS social_ui_events (
              relationship_id TEXT NOT NULL, study_date TEXT NOT NULL, kind TEXT NOT NULL,
              PRIMARY KEY(relationship_id, study_date, kind));
            PRAGMA user_version=3;
            """);
        schema.ExecuteNonQuery(); transaction.Commit();
    }

    /// <summary>Called only after the user explicitly binds the existing dataset. Cannot reassign it to another account.</summary>
    public void BindOwner(Guid owner, Guid binding)
    {
        RequireId(owner); RequireId(binding);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var read = Command(connection, transaction, "SELECT owner_id,binding_id FROM social_owner WHERE singleton=1");
        using (var reader = read.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != Id(owner) || reader.GetString(1) != Id(binding))
                    throw new InvalidOperationException("This local dataset is already bound to another account or binding.");
                transaction.Commit(); return;
            }
        }
        using var insert = Command(connection, transaction,
            "INSERT INTO social_owner(singleton,owner_id,binding_id) VALUES(1,$owner,$binding)",
            ("$owner", Id(owner)), ("$binding", Id(binding)));
        insert.ExecuteNonQuery(); transaction.Commit();
    }

    public string Enqueue(Guid actor, Guid batchId, SocialDailySummary summary)
    {
        RequireId(actor); RequireId(batchId); Validate(summary);
        var payload = JsonSerializer.Serialize(summary, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var binding = AssertOwner(connection, transaction, actor);
        if (binding != Id(summary.BindingId)) throw new InvalidOperationException("Summary belongs to a different binding.");
        using var prior = Command(connection, transaction, "SELECT payload_hash FROM social_batches WHERE batch_id=$id", ("$id", Id(batchId)));
        if (prior.ExecuteScalar() is string priorHash)
        {
            if (priorHash != hash) throw new InvalidOperationException("Batch identifier was reused with a different payload.");
            transaction.Commit(); return hash;
        }
        using var latest = Command(connection, transaction, "SELECT revision FROM social_day_revisions WHERE study_date=$day", ("$day", Day(summary.Date)));
        var previous = latest.ExecuteScalar();
        if (previous is not null && summary.Revision <= Convert.ToInt64(previous, CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Daily revision must advance monotonically.");
        using var insert = Command(connection, transaction, """
            INSERT INTO social_batches(batch_id,owner_id,study_date,revision,payload_hash,payload)
            VALUES($id,$owner,$day,$revision,$hash,$payload);
            INSERT INTO social_day_revisions(study_date,revision) VALUES($day,$revision)
            ON CONFLICT(study_date) DO UPDATE SET revision=excluded.revision;
            """, ("$id", Id(batchId)), ("$owner", Id(actor)), ("$day", Day(summary.Date)),
            ("$revision", summary.Revision), ("$hash", hash), ("$payload", payload));
        insert.ExecuteNonQuery(); transaction.Commit(); return hash;
    }

    public int CountPending(Guid actor)
    {
        RequireId(actor);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var count = Command(connection, transaction,
            "SELECT COUNT(*) FROM social_batches WHERE owner_id=$owner AND acknowledged=0", ("$owner", Id(actor)));
        var result = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        transaction.Commit(); return result;
    }

    public IReadOnlyList<SocialQueuedBatch> LoadPending(Guid actor, int limit = 50)
    {
        RequireId(actor);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var read = Command(connection, transaction, """
            SELECT batch_id,payload_hash,payload FROM social_batches
            WHERE owner_id=$owner AND acknowledged=0 ORDER BY study_date,revision LIMIT $limit
            """, ("$owner", Id(actor)), ("$limit", limit));
        var pending = new List<SocialQueuedBatch>();
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                var payload = reader.GetString(2); var hash = reader.GetString(1);
                if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant() != hash)
                    throw new InvalidOperationException("Pending summary failed integrity validation; preserved for recovery.");
                var summary = JsonSerializer.Deserialize<SocialDailySummary>(payload, JsonOptions)
                    ?? throw new InvalidOperationException("Pending summary cannot be read; preserved for recovery.");
                Validate(summary);
                pending.Add(new(Guid.Parse(reader.GetString(0)), hash, summary) { RawPayload = payload });
            }
        }
        transaction.Commit(); return pending;
    }

    /// <summary>Call only with a receipt returned under the same authenticated account. Retry is harmless.</summary>
    public bool Acknowledge(Guid actor, Guid batchId, string expectedHash)
    {
        RequireId(actor); RequireId(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var update = Command(connection, transaction, """
            UPDATE social_batches SET acknowledged=1,payload=NULL
            WHERE batch_id=$id AND owner_id=$owner AND payload_hash=$hash
            """, ("$id", Id(batchId)), ("$owner", Id(actor)), ("$hash", expectedHash));
        var matched = update.ExecuteNonQuery() == 1;
        transaction.Commit(); return matched;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = Command(connection, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
            pragma.ExecuteNonQuery(); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static string AssertOwner(SqliteConnection connection, SqliteTransaction transaction, Guid actor)
    {
        using var read = Command(connection, transaction, "SELECT binding_id FROM social_owner WHERE singleton=1 AND owner_id=$owner", ("$owner", Id(actor)));
        return read.ExecuteScalar() as string ?? throw new InvalidOperationException("Account cannot access this local dataset.");
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
    private static void Validate(SocialDailySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary); RequireId(summary.BindingId);
        if (summary.Revision <= 0 || summary.Target < 0 || summary.Completed < 0 || summary.Completed > summary.Target ||
            summary.NewWords < 0 || summary.ReviewWords < 0 || !Enum.IsDefined(summary.Cause) ||
            summary.QualifiedAt is { } at && (SocialDayRules.DateAt(at) != summary.Date || summary.Target == 0 || summary.Completed != summary.Target))
            throw new ArgumentException("Invalid daily summary.", nameof(summary));
    }
    private static void RequireId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("Identifier cannot be empty."); }
    private static string Id(Guid value) => value.ToString("D");
    private static string Day(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
