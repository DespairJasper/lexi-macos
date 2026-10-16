using System.Globalization;
using System.Text.Json;

namespace Lexi.Social;

public sealed partial class SocialStore
{
    public bool QueueDuration(Guid actor, SocialDurationChange change, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);
        var today = SocialDayRules.DateAt(now);
        if (change.Date < today.AddDays(-7) || change.Date > today || change.Coverage is not ("complete" or "partial" or "unavailable") ||
            (change.Coverage == "unavailable") != (change.Seconds is null) ||
            change.Seconds is { } seconds && (!double.IsFinite(seconds) || seconds is < 0 or > 86400))
            throw new ArgumentException("Invalid or out-of-window duration.");
        return QueueSecondary(actor, "duration", change.Date, JsonSerializer.Serialize(change, JsonOptions), (binding, revision) =>
            JsonSerializer.Serialize(new { p_binding_id = binding.BindingId, p_date = Day(change.Date), p_revision = revision,
                p_seconds = change.Seconds, p_coverage = change.Coverage }), now);
    }

    public bool QueueCurve(Guid actor, SocialCurveChange change, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.TakenAt > now || change.TakenAt < now.AddDays(-30) || change.SampleCount is < 1 or > 1000000 ||
            change.Points is null || change.Points.Count != 121 || change.Points.Any(p => !double.IsFinite(p) || p is < 0 or > 1))
            throw new ArgumentException("Expected the 121-point 0..30 day probability protocol.");
        return QueueSecondary(actor, "curve", SocialDayRules.DateAt(change.TakenAt), JsonSerializer.Serialize(change, JsonOptions), (binding, _) =>
        {
            if (change.TakenAt < binding.BoundAt) throw new InvalidOperationException("Curve predates dataset binding.");
            return JsonSerializer.Serialize(new { p_binding_id = binding.BindingId, p_taken_at = change.TakenAt,
                p_sample_count = change.SampleCount, p_points = change.Points });
        }, now, change.TakenAt);
    }

    private bool QueueSecondary(Guid actor, string kind, DateOnly date, string facts,
        Func<SocialBindingState,long,string> payloadFactory, DateTimeOffset now, DateTimeOffset? curveTakenAt = null)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        var binding = ReadBinding(connection, transaction, actor);
        if (binding?.BoundAt is null || date < binding.FirstDay) throw new InvalidOperationException("Secondary change requires active binding.");
        long revision = 1;
        using var read = Command(connection, transaction, "SELECT revision,facts FROM social_secondary WHERE kind=$kind AND study_date=$day", ("$kind", kind), ("$day", Day(date)));
        using (var reader = read.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(1) == facts) { transaction.Commit(); return false; }
                if (curveTakenAt is { } taken && taken <= JsonSerializer.Deserialize<SocialCurveChange>(reader.GetString(1), JsonOptions)!.TakenAt)
                    throw new InvalidOperationException("Changed curve must have a newer actual snapshot timestamp.");
                revision = checked(reader.GetInt64(0) + 1);
            }
        }
        var payload = payloadFactory(binding, revision);
        using var write = Command(connection, transaction, """
            INSERT INTO social_secondary(kind,study_date,revision,facts,payload,acknowledged) VALUES($kind,$day,$revision,$facts,$payload,0)
            ON CONFLICT(kind,study_date) DO UPDATE SET revision=excluded.revision,facts=excluded.facts,payload=excluded.payload,acknowledged=0;
            DELETE FROM social_secondary WHERE (kind='duration' AND study_date<$duration) OR (kind='curve' AND study_date<$curve);
            """, ("$kind", kind), ("$day", Day(date)), ("$revision", revision), ("$facts", facts), ("$payload", payload),
            ("$duration", Day(SocialDayRules.DateAt(now).AddDays(-7))), ("$curve", Day(SocialDayRules.DateAt(now.AddDays(-30)))));
        write.ExecuteNonQuery(); transaction.Commit(); return true;
    }

    public IReadOnlyList<SocialSecondaryBatch> LoadSecondaryPending(Guid actor, DateTimeOffset now)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var read = Command(connection, transaction, """
            SELECT kind,study_date,revision,payload,facts FROM social_secondary WHERE acknowledged=0 AND study_date<=$today
            AND ((kind='duration' AND study_date>=$duration) OR (kind='curve' AND study_date>=$curve)) ORDER BY study_date,kind
            """, ("$today", Day(SocialDayRules.DateAt(now))), ("$duration", Day(SocialDayRules.DateAt(now).AddDays(-7))),
            ("$curve", Day(SocialDayRules.DateAt(now.AddDays(-30)))));
        var result = new List<SocialSecondaryBatch>();
        using (var reader = read.ExecuteReader())
            while (reader.Read())
            {
                if (reader.GetString(0) == "curve" && JsonSerializer.Deserialize<SocialCurveChange>(reader.GetString(4), JsonOptions)!.TakenAt < now.AddDays(-30)) continue;
                result.Add(new(reader.GetString(0), DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture), reader.GetInt64(2), reader.GetString(3)));
            }
        transaction.Commit(); return result;
    }

    public void SetSecondarySharing(Guid actor, bool time, bool curve)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); AssertOwner(connection, transaction, actor);
        using var update = Command(connection, transaction, """
            INSERT INTO social_secondary_sharing(singleton,share_time,share_curve) VALUES(1,$time,$curve)
            ON CONFLICT(singleton) DO UPDATE SET share_time=excluded.share_time,share_curve=excluded.share_curve;
            UPDATE social_secondary SET acknowledged=0;
            """, ("$time", time ? 1 : 0), ("$curve", curve ? 1 : 0));
        update.ExecuteNonQuery(); transaction.Commit();
    }

    public SocialSecondarySharing GetSecondarySharing(Guid actor)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); AssertOwner(connection, transaction, actor);
        using var read = Command(connection, transaction, "SELECT share_time,share_curve FROM social_secondary_sharing WHERE singleton=1");
        SocialSecondarySharing result;
        using (var reader = read.ExecuteReader()) result = reader.Read() ? new(reader.GetInt32(0) == 1, reader.GetInt32(1) == 1) : new(false, false);
        transaction.Commit(); return result;
    }

    internal void AcknowledgeSecondary(Guid actor, SocialSecondaryBatch batch)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); AssertOwner(connection, transaction, actor);
        using var update = Command(connection, transaction, "UPDATE social_secondary SET acknowledged=1 WHERE kind=$kind AND study_date=$day AND revision=$revision AND payload=$payload",
            ("$kind", batch.Kind), ("$day", Day(batch.Date)), ("$revision", batch.Revision), ("$payload", batch.Payload));
        update.ExecuteNonQuery(); transaction.Commit();
    }
}
