using Lexi.Social;

/// <summary>Joint-day classification and the seven-day weekly model. Pure; no clock, no I/O.</summary>
internal static class SocialWeekTests
{
    private static SocialLocalDay Local(DateOnly day, double? seconds = 10, int? words = 3) =>
        new(day, JointDayKnowledge.Unknown, seconds, words);

    internal static void Run(Action<bool, string> check)
    {
        var today = new DateOnly(2026, 10, 10);
        // The friendship must be older than the server's 365-day coverage, otherwise the
        // "inside the friendship but beyond server coverage" branch cannot exist at all.
        var friendship = today.AddDays(-400);
        var coverageFrom = today.AddDays(-364);
        var common = new HashSet<DateOnly> { today.AddDays(-1), today.AddDays(-3) };
        var pending = new HashSet<DateOnly> { today.AddDays(-2) };
        var cached = new Dictionary<DateOnly, JointDayKnowledge> { [today.AddDays(-380)] = JointDayKnowledge.NonJoint };
        var local = new[]
        {
            Local(today.AddDays(-500)),  // before the friendship
            Local(today.AddDays(-1)),    // common
            Local(today.AddDays(-2)),    // pending
            Local(today.AddDays(-3)),    // common
            Local(today.AddDays(-100)),  // inside friendship, inside server coverage, not common
            Local(today.AddDays(-380)),  // inside friendship, beyond server coverage, cached
            Local(today.AddDays(-390)),  // inside friendship, beyond server coverage, no cache
            Local(today)                 // today is never a finished day
        };
        var projected = SocialJointDays.Project(today, friendship, coverageFrom, common, pending, cached, local);
        JointDayKnowledge Of(DateOnly day) => projected.Single(d => d.Date == day).Joint;

        check(projected.All(d => d.Date < today), "today is never part of the finished-day comparison");
        check(Of(today.AddDays(-500)) == JointDayKnowledge.NonJoint,
            "a date before the friendship is a definite non-joint baseline day");
        check(Of(today.AddDays(-1)) == JointDayKnowledge.Success && Of(today.AddDays(-3)) == JointDayKnowledge.Success,
            "the server's common days are joint successes");
        check(Of(today.AddDays(-2)) == JointDayKnowledge.Pending, "a pending day stays pending so neither group counts it");
        check(Of(today.AddDays(-100)) == JointDayKnowledge.NonJoint,
            "inside the friendship and inside server coverage, a non-common day is definitely non-joint");
        check(Of(today.AddDays(-380)) == JointDayKnowledge.NonJoint,
            "a cached older observation keeps its known result");
        check(Of(today.AddDays(-390)) == JointDayKnowledge.Unknown,
            "beyond server coverage with no local cache the day is unknown, not a non-joint day");
        check(projected.Single(d => d.Date == today.AddDays(-1)).Words == 3,
            "the projection keeps the local observed counts for the comparison");

        // Duplicate local days cannot silently double count.
        try { SocialJointDays.Project(today, friendship, coverageFrom, common, pending, cached, [Local(today.AddDays(-1)), Local(today.AddDays(-1))]); check(false, "duplicate local days must be rejected"); }
        catch (ArgumentException) { check(true, "duplicate local days are rejected"); }
        // The server reports its own Beijing today in both sets, because reconcile writes a row for
        // the current day. That is an ordinary reply and must never fail the whole comparison.
        var withServerToday = SocialJointDays.Project(today, friendship, coverageFrom,
            new HashSet<DateOnly>(common) { today }, new HashSet<DateOnly>(pending) { today }, cached, local);
        check(withServerToday.All(d => d.Date < today),
            "the server's own today is ignored rather than turning the comparison into an error");
        check(withServerToday.Single(d => d.Date == today.AddDays(-1)).Joint == JointDayKnowledge.Success,
            "a reply that includes the server's today still classifies the finished days correctly");
        // A day beyond the server's today is equally unusable, and equally not an error.
        check(SocialJointDays.Project(today, friendship, coverageFrom, new HashSet<DateOnly> { today.AddDays(5) }, pending, cached, local)
            .All(d => d.Date < today), "a day the server has not reached yet is ignored too");
        // A pending day is excluded from both groups through the existing statistics path.
        var pendingExcluded = SocialStatistics.Build(withServerToday, today);
        check(pendingExcluded.JointDays + pendingExcluded.NonJointDays ==
            withServerToday.Count(d => d.Joint is JointDayKnowledge.Success or JointDayKnowledge.NonJoint),
            "pending days never enter either comparison group");

        // The classification feeds straight into the existing comparison: pending and unknown days
        // stay out of both groups, while a known inactive day is a real zero in the baseline.
        var stats = SocialStatistics.Build(SocialJointDays.Project(today, friendship, coverageFrom, common, pending, cached,
            [Local(today.AddDays(-1), 60, 10), Local(today.AddDays(-2), 999, 999),
             Local(today.AddDays(-100), 0, 0), Local(today.AddDays(-390), 999, 999)]), today);
        check(stats.JointDays == 1 && stats.NonJointDays == 1,
            "pending and unknown days stay out of both groups while a known inactive day is a baseline");
        check(stats.Seconds.NonJoint.Total == 0, "a known inactive baseline day is a real zero, not a missing value");
        check(stats.Seconds.Joint.Total == 60 && stats.Words.Joint.Total == 10, "the joint group only counts the confirmed day");

        // ---- weekly detail ----
        var firstDay = today.AddDays(-5);   // binding day, inside the window
        var server = new List<SocialWeeklyDay>
        {
            new(today.AddDays(-1), 20, 36, 18, 30, 1800, "complete", "full", 900, "complete", "full"),
            new(today.AddDays(-3), 10, 0, null, null, 600, "partial", "full", null, null, null)
        };
        var selfCounts = new Dictionary<DateOnly, SocialWordCounts>
        {
            [today.AddDays(-1)] = new(20, 36),
            [today.AddDays(-2)] = new(5, 1),
            [today.AddDays(-7)] = new(99, 99)   // the local day before the window
        };
        var selfSeconds = new Dictionary<DateOnly, double?>
        {
            [today.AddDays(-1)] = 1800,
            [today.AddDays(-2)] = null,
            [today.AddDays(-3)] = 600
        };
        var week = SocialWeek.Build(today, selfCounts, selfSeconds, firstDay, server);
        check(week.Count == 7, "the weekly detail always covers seven finished Beijing days");
        check(week[0].Date == today.AddDays(-7) && week[^1].Date == today.AddDays(-1),
            "the window runs from today-7 to today-1 and never includes today");
        check(week.All(d => d.Date < today), "no future or current day appears in the weekly detail");
        var yesterday = week.Single(d => d.Date == today.AddDays(-1));
        check(yesterday.SelfNewWords == 20 && yesterday.SelfReviewWords == 36 && yesterday.SelfSeconds == 1800,
            "the self series comes from the full local history");
        check(yesterday.FriendNewWords == 18 && yesterday.FriendReviewWords == 30 && yesterday.FriendSeconds == 900,
            "an authorized friend series is shown alongside");
        var gap = week.Single(d => d.Date == today.AddDays(-2));
        check(gap.SelfNewWords == 5 && gap.SelfReviewWords == 1, "a day with only local data still reports the self series");
        check(gap.FriendNewWords is null && gap.FriendSeconds is null,
            "a day the server did not report leaves the friend series missing, never zero");
        var unauthorized = week.Single(d => d.Date == today.AddDays(-3));
        check(unauthorized.FriendNewWords is null && unauthorized.FriendSeconds is null,
            "an unauthorized friend series stays missing rather than becoming zero");
        check(unauthorized.SelfSeconds == 600, "the self series is independent of the friend authorization");
        check(week.Single(d => d.Date == today.AddDays(-7)).SelfNewWords == 99,
            "a local day before the binding is still counted for the self series");
        check(week.Single(d => d.Date == firstDay).SelfScope == SocialWeek.ScopeBindingPartial &&
            yesterday.SelfScope == SocialWeek.ScopeFull,
            "the binding day is marked as partially covered and later days as full");
        check(unauthorized.SelfSeconds is not null && gap.SelfSeconds is null,
            "a missing self duration stays missing rather than becoming zero seconds");

        // ---- relationship-scoped cache: authoritative window replacement and bounded retention ----
        var root = Path.Combine(Path.GetTempPath(), "lexi-social-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var owner = Guid.NewGuid(); var binding = Guid.NewGuid();
            var store = new SocialStore(root); store.BindOwner(owner, binding);
            var relation = Guid.NewGuid(); var otherRelation = Guid.NewGuid();
            var now = DateTimeOffset.Parse("2026-10-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            var windowFrom = today.AddDays(-364);
            var earliest = today.AddDays(-500);
            var oldestRetained = today.AddDays(-500);

            // First authoritative window: an older, wider one.
            var olderWindow = today.AddDays(-400);
            store.RecordJointWindow(owner, relation, olderWindow,
                [(today.AddDays(-390), JointDayKnowledge.Success), (today.AddDays(-350), JointDayKnowledge.Success)], earliest, now);
            var cache = store.LoadJoint(owner, relation);
            check(cache.CoverageFrom == olderWindow, "the authoritative coverage window is stored with the observations");
            check(cache.Days.Count == 2, "the confirmed days from the reply are cached");

            // A newer window replaces everything it covers, so a day the server no longer reports
            // is actually removed instead of lingering as a stale success.
            store.RecordJointWindow(owner, relation, windowFrom,
                [(today.AddDays(-1), JointDayKnowledge.NonJoint), (today.AddDays(-100), JointDayKnowledge.NonJoint)], earliest, now.AddMinutes(1));
            cache = store.LoadJoint(owner, relation);
            check(cache.CoverageFrom == windowFrom, "the stored window follows the newest authoritative reply");
            check(!cache.Days.ContainsKey(today.AddDays(-350)),
                "a day the latest window no longer reports is removed rather than kept as a stale success");
            check(cache.Days[today.AddDays(-390)] == JointDayKnowledge.Success,
                "an observation older than the newest window is preserved");
            check(cache.Days[today.AddDays(-1)] == JointDayKnowledge.NonJoint, "the newest window's days are stored");

            // Retention is bounded by the local history: days we can never compare again are dropped.
            // The window is restated in full, which is what an authoritative reply always does.
            store.RecordJointWindow(owner, relation, windowFrom,
                [(today.AddDays(-1), JointDayKnowledge.NonJoint)], today.AddDays(-380), now.AddMinutes(2));
            cache = store.LoadJoint(owner, relation);
            check(!cache.Days.ContainsKey(today.AddDays(-390)), "an observation older than the local history is not retained");
            check(cache.Days[today.AddDays(-1)] == JointDayKnowledge.NonJoint, "restating the window keeps the day it still reports");
            check(!cache.Days.ContainsKey(today.AddDays(-100)),
                "a day the restated window no longer reports is removed, because the reply is authoritative");

            // A window that names no day says nothing about any day, so nothing is destroyed: the
            // delete is bounded by the newest day the reply actually reported.
            store.RecordJointWindow(owner, relation, windowFrom,
                [(today.AddDays(-1), JointDayKnowledge.NonJoint)], oldestRetained, now.AddMinutes(3));
            store.RecordJointWindow(owner, relation, windowFrom, [], oldestRetained, now.AddMinutes(4));
            check(store.LoadJoint(owner, relation).Days[today.AddDays(-1)] == JointDayKnowledge.NonJoint,
                "a reply that names no days leaves the cached days alone");

            // Pruning is opt-in: an unreadable local history must not wipe the out-of-window cache.
            store.RecordJointWindow(owner, relation, windowFrom,
                [(today.AddDays(-1), JointDayKnowledge.Success)], oldestRetained, now.AddMinutes(5));
            store.RecordJointWindow(owner, relation, today.AddDays(-300),
                [(today.AddDays(-1), JointDayKnowledge.Success)], DateOnly.MinValue, now.AddMinutes(6));
            check(store.LoadJoint(owner, relation).Days.ContainsKey(today.AddDays(-1)),
                "a caller that could not read the local history never prunes older observations");

            // A separate relationship keeps its own history and never merges with another.
            store.RecordJointWindow(owner, otherRelation, windowFrom,
                [(today.AddDays(-1), JointDayKnowledge.Success)], earliest, now);
            check(store.LoadJoint(owner, otherRelation).Days[today.AddDays(-1)] == JointDayKnowledge.Success,
                "a different relationship keeps its own independent history");

            // Pending days are never persisted as a known result.
            store.RecordJointWindow(owner, relation, windowFrom,
                [(today.AddDays(-2), JointDayKnowledge.Pending), (today.AddDays(-3), JointDayKnowledge.Unknown)], earliest, now.AddMinutes(3));
            cache = store.LoadJoint(owner, relation);
            check(!cache.Days.ContainsKey(today.AddDays(-2)) && !cache.Days.ContainsKey(today.AddDays(-3)),
                "pending and unknown days are never cached as known results");

            // Ending a relationship drops its cache entirely.
            store.ForgetRelationship(owner, relation);
            var forgotten = store.LoadJoint(owner, relation);
            check(forgotten.Days.Count == 0 && forgotten.CoverageFrom is null, "ending a relationship clears its cached days");
            check(store.LoadJoint(owner, otherRelation).Days.Count == 1, "clearing one relationship leaves the others alone");

            // Animation events fire once per relationship, day and kind.
            var day = today.AddDays(-1);
            check(store.RecordEvent(owner, relation, day, "milestone"), "the first animation event is fresh");
            check(!store.RecordEvent(owner, relation, day, "milestone"), "reopening the app does not replay the same animation");
            check(store.RecordEvent(owner, relation, day, "extreme"), "a different kind on the same day is its own event");
            check(store.RecordEvent(owner, otherRelation, day, "milestone"), "the same event on another relationship is independent");

            // Another account can neither read nor write this cache.
            var stranger = Guid.NewGuid();
            try { store.LoadJoint(stranger, relation); check(false, "another account must not read the cached days"); }
            catch (InvalidOperationException) { check(true, "another account cannot read the cached days"); }
            try { store.RecordJointWindow(stranger, relation, windowFrom, [], earliest, now); check(false, "another account must not write the cache"); }
            catch (InvalidOperationException) { check(true, "another account cannot write the cache"); }

            // The sidecar itself survives reopening, and the learning database is never touched.
            var reopened = new SocialStore(root);
            check(reopened.LoadJoint(owner, otherRelation).Days.Count == 1, "the cache survives reopening the sidecar");

            // The other tables are still intact, and the pending queue is untouched by schema growth.
            var summary = new SocialDailySummary(binding, today, 1, 20, 20, 20, 0, now, QualificationCause.LearningComplete);
            var batch = Guid.NewGuid();
            reopened.Enqueue(owner, batch, summary);
            check(reopened.LoadPending(owner).Count == 1, "the summary queue still works after the schema grew");
            reopened.RecordJointWindow(owner, relation, windowFrom, [], earliest, now);
            check(reopened.LoadPending(owner).Count == 1, "writing the UI cache never disturbs the pending queue");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
