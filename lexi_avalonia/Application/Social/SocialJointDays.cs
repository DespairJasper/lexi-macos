namespace Lexi.Social;

/// <summary>Turns local observed days into the joint/non-joint groups the comparison uses.
/// The point of this step is to never let an unknown day masquerade as a known non-joint one:
/// a date the server cannot vouch for and we have no cache for is excluded from both groups
/// rather than counted as evidence.</summary>
public static class SocialJointDays
{
    public static IReadOnlyList<SocialLocalDay> Project(DateOnly today, DateOnly friendshipStart,
        DateOnly coverageFrom, IReadOnlySet<DateOnly> commonDays, IReadOnlySet<DateOnly> pendingDays,
        IReadOnlyDictionary<DateOnly, JointDayKnowledge> cached, IEnumerable<SocialLocalDay> local)
    {
        ArgumentNullException.ThrowIfNull(commonDays);
        ArgumentNullException.ThrowIfNull(pendingDays);
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(local);
        // The server legitimately reports its own today in both sets (reconcile writes a row for
        // the current Beijing day, pending unless both sides have finished). Those days are not
        // finished yet, so they are simply never classified — never an error, and never a reason
        // to abandon the whole comparison.
        var finished = new HashSet<DateOnly>();
        foreach (var day in commonDays) if (day < today) finished.Add(day);
        var pending = new HashSet<DateOnly>();
        foreach (var day in pendingDays) if (day < today) pending.Add(day);

        var seen = new HashSet<DateOnly>();
        var result = new List<SocialLocalDay>();
        foreach (var observed in local)
        {
            if (observed is null) throw new ArgumentException("Invalid local day.", nameof(local));
            if (!seen.Add(observed.Date)) throw new ArgumentException("Duplicate local day.", nameof(local));
            if (observed.Date >= today) continue;   // today is never part of a finished-day comparison
            JointDayKnowledge joint;
            if (observed.Date < friendshipStart) joint = JointDayKnowledge.NonJoint;      // before we were friends: a valid baseline
            else if (pending.Contains(observed.Date)) joint = JointDayKnowledge.Pending;
            else if (finished.Contains(observed.Date)) joint = JointDayKnowledge.Success;
            else if (observed.Date >= coverageFrom) joint = JointDayKnowledge.NonJoint;   // the server can vouch for this day
            else if (cached.TryGetValue(observed.Date, out var known)) joint = known;     // an older observation we already confirmed
            else joint = JointDayKnowledge.Unknown;                                        // beyond server coverage and uncached
            result.Add(observed with { Joint = joint });
        }
        return result;
    }
}
