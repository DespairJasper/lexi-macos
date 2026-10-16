using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lexi.Social;

/// <summary>Relationship-scoped UI state: the confirmed joint days we have already seen, and the
/// one-shot animation events we have already played. Keyed by relationship rather than by run, so
/// reopening a run keeps the history, and a brand new relationship never inherits an old one's.</summary>
public sealed partial class SocialStore
{
    public sealed record SocialJointCache(DateOnly? CoverageFrom, IReadOnlyDictionary<DateOnly, JointDayKnowledge> Days);

    /// <summary>Store the latest authoritative window. Everything inside the new window is replaced
    /// outright, so a revocation or a recomputation actually removes days instead of leaving them
    /// behind; older observations are kept, but only as far back as the local history still reaches.</summary>
    public void RecordJointWindow(Guid actor, Guid relationship, DateOnly coverageFrom,
        IEnumerable<(DateOnly Date, JointDayKnowledge Joint)> confirmed, DateOnly earliestLocalDate, DateTimeOffset now)
    {
        RequireId(actor); RequireId(relationship);
        ArgumentNullException.ThrowIfNull(confirmed);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        // The reply is authoritative only for the span it actually names: from the window start
        // up to the newest confirmed day it reported. Clearing beyond that would delete days the
        // caller never spoke about, so the delete is bounded by that newest day instead of by
        // "everything from the window start onward".
        var materialised = confirmed.Where(c => c.Date >= coverageFrom && c.Joint is JointDayKnowledge.Success or JointDayKnowledge.NonJoint)
            .Select(c => c.Date).ToArray();
        if (materialised.Length > 0)
        {
            using var clear = Command(connection, transaction,
                "DELETE FROM social_ui_days WHERE relationship_id=$relation AND study_date>=$from AND study_date<=$to",
                ("$relation", Id(relationship)), ("$from", Day(coverageFrom)), ("$to", Day(materialised.Max())));
            clear.ExecuteNonQuery();
        }
        // Pruning is opt-in. A caller that could not read the local history passes
        // DateOnly.MinValue, which means "do not prune" rather than "delete everything".
        if (earliestLocalDate > DateOnly.MinValue)
            using (var prune = Command(connection, transaction,
                "DELETE FROM social_ui_days WHERE relationship_id=$relation AND study_date<$earliest",
                ("$relation", Id(relationship)), ("$earliest", Day(earliestLocalDate))))
                prune.ExecuteNonQuery();
        foreach (var (date, joint) in confirmed)
        {
            // Only days inside the window are this reply's to state; they are rewritten here after
            // the delete above. A day older than the window is not part of any authoritative reply,
            // so it is left exactly as it was. Pending and unknown days are never stored at all.
            if (date < coverageFrom) continue;
            if (joint is not (JointDayKnowledge.Success or JointDayKnowledge.NonJoint)) continue;
            using var insert = Command(connection, transaction, """
                INSERT INTO social_ui_days(relationship_id,study_date,joint) VALUES($relation,$day,$joint)
                ON CONFLICT(relationship_id,study_date) DO UPDATE SET joint=excluded.joint;
                """, ("$relation", Id(relationship)), ("$day", Day(date)),
                ("$joint", joint == JointDayKnowledge.Success ? "success" : "non_joint"));
            insert.ExecuteNonQuery();
        }
        using (var window = Command(connection, transaction, """
            INSERT INTO social_ui_coverage(relationship_id,coverage_from,updated_at) VALUES($relation,$from,$at)
            ON CONFLICT(relationship_id) DO UPDATE SET coverage_from=excluded.coverage_from,updated_at=excluded.updated_at;
            """, ("$relation", Id(relationship)), ("$from", Day(coverageFrom)), ("$at", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))))
            window.ExecuteNonQuery();
        transaction.Commit();
    }

    public SocialJointCache LoadJoint(Guid actor, Guid relationship)
    {
        RequireId(actor); RequireId(relationship);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        DateOnly? coverage = null;
        using (var read = Command(connection, transaction,
            "SELECT coverage_from FROM social_ui_coverage WHERE relationship_id=$relation", ("$relation", Id(relationship))))
            if (read.ExecuteScalar() is string text) coverage = DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = new Dictionary<DateOnly, JointDayKnowledge>();
        using (var read = Command(connection, transaction,
            "SELECT study_date,joint FROM social_ui_days WHERE relationship_id=$relation", ("$relation", Id(relationship))))
        using (var reader = read.ExecuteReader())
            while (reader.Read())
                days[DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture)] =
                    reader.GetString(1) == "success" ? JointDayKnowledge.Success : JointDayKnowledge.NonJoint;
        transaction.Commit();
        return new(coverage, days);
    }

    /// <summary>Forget everything cached for one relationship, used when it ends or is revoked.</summary>
    public void ForgetRelationship(Guid actor, Guid relationship)
    {
        RequireId(actor); RequireId(relationship);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var clear = Command(connection, transaction, """
            DELETE FROM social_ui_days WHERE relationship_id=$relation;
            DELETE FROM social_ui_coverage WHERE relationship_id=$relation;
            DELETE FROM social_ui_events WHERE relationship_id=$relation;
            """, ("$relation", Id(relationship)));
        clear.ExecuteNonQuery(); transaction.Commit();
    }

    /// <summary>True the first time an event is recorded. Animations are played once per
    /// relationship, day and kind, so reopening the app does not replay them.</summary>
    public bool RecordEvent(Guid actor, Guid relationship, DateOnly date, string kind)
    {
        RequireId(actor); RequireId(relationship); RequireId(date, kind);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AssertOwner(connection, transaction, actor);
        using var insert = Command(connection, transaction,
            "INSERT OR IGNORE INTO social_ui_events(relationship_id,study_date,kind) VALUES($relation,$day,$kind)",
            ("$relation", Id(relationship)), ("$day", Day(date)), ("$kind", kind));
        var fresh = insert.ExecuteNonQuery() == 1;
        transaction.Commit(); return fresh;
    }

    private static void RequireId(DateOnly date, string kind)
    {
        if (date == default) throw new ArgumentException("A real date is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
    }
}
