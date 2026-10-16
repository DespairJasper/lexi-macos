using System.Text.Json;
using Lexi.Social;

/// <summary>Contract tests for the fail-closed social payload decoder. No network, no clock, no files.</summary>
internal static class SocialDirectoryTests
{
    private const string ServerNow = "2026-10-10T12:00:00+00:00";
    private const string Today = "2026-10-10";
    private static readonly Guid Relation = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRelation = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid Friend = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Mine = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Invite = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid InviteIncoming = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    private static Dictionary<string, object?> PairStateObject() => new()
    {
        ["relationship_id"] = Relation, ["run_id"] = RunId, ["date"] = Today, ["server_now"] = ServerNow,
        ["lifecycle"] = "active", ["streak"] = 28, ["lifetime_days"] = 21,
        ["self_done"] = true, ["friend_done"] = true,
        ["self_completed_at"] = "2026-10-10T04:00:00+00:00", ["friend_completed_at"] = "2026-10-10T08:00:00+00:00",
        ["self_cause"] = "learning_complete", ["friend_cause"] = "learning_complete",
        ["success"] = true, ["extreme"] = false, ["pending_previous"] = false, ["broken_day"] = null
    };

    private static Dictionary<string, object?> FriendObject(Dictionary<string, object?>? pair = null) => new()
    {
        ["relationship_id"] = Relation, ["friend_id"] = Friend, ["nickname"] = "小明", ["avatar_path"] = null,
        ["share_counts"] = true, ["share_curve"] = true, ["share_time"] = true,
        ["friend_share_counts"] = true, ["friend_share_curve"] = true, ["friend_share_time"] = true,
        ["pair_state"] = pair ?? PairStateObject(),
        ["today"] = new Dictionary<string, object?>
        {
            ["date"] = Today, ["self_new_words"] = 20, ["self_review_words"] = 36,
            ["friend_new_words"] = 18, ["friend_review_words"] = 30
        }
    };

    private static Dictionary<string, object?> ProfileObject(string nickname = "我", object? avatar = null) => new()
    {
        ["user_id"] = Mine, ["nickname"] = nickname, ["avatar_path"] = avatar,
        ["updated_at"] = "2026-10-10T03:00:00+00:00"
    };

    private static Dictionary<string, object?> InviteObject(string status, object? nickname = null, object? relationship = null, Guid? id = null) => new()
    {
        ["id"] = id ?? Invite, ["nickname"] = nickname, ["status"] = status,
        ["created_at"] = "2026-10-09T02:00:00+00:00", ["expires_at"] = "2026-10-10T02:00:00+00:00",
        ["handled_at"] = null, ["relationship_id"] = relationship
    };

    internal static void Run(Action<bool, string> check)
    {
        void Reject(string message, Func<object?> action)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or JsonException or KeyNotFoundException or InvalidCastException) { check(true, message); return; }
            check(false, message);
        }

        // ---- friends: happy path ----
        var friends = SocialDirectory.Friends(Element(new List<object?> { FriendObject() }));
        check(friends.Count == 1, "a well-formed friend row decodes");
        var friend = friends[0];
        check(friend.FriendId == Friend && friend.Nickname == "小明" && friend.RelationshipId == Relation, "friend identity fields decode verbatim");
        check(friend.ShareCounts && friend.ShareCurve && friend.ShareTime, "own sharing switches decode");
        check(friend.FriendShareCounts && friend.FriendShareCurve && friend.FriendShareTime, "peer sharing switches decode");
        check(friend.Pair.Streak == 28 && friend.Pair.Today.ToString("yyyy-MM-dd") == Today && friend.Pair.Lifecycle == PairLifecycle.Active,
            "the embedded pair state decodes");
        check(friend.Pair.Self is { Cause: QualificationCause.LearningComplete } && friend.Pair.Friend is not null,
            "completions decode from the server's qualified time and cause");
        check(friend.Pair.CloudSuccess && friend.Pair.SuccessDate == friend.Pair.Today, "cloud success carries the server's own date");
        check(friend.SelfNewWords == 20 && friend.FriendNewWords == 18, "authorized word counts decode");
        check(friend.Pair.LifetimeDays == 21, "lifetime joint days decode");

        // ---- json_strip_nulls: an absent field is unknown, never zero ----
        var hiddenEntry = FriendObject();
        hiddenEntry["share_curve"] = false; hiddenEntry["share_time"] = false;
        hiddenEntry["friend_share_counts"] = false; hiddenEntry["friend_share_curve"] = false; hiddenEntry["friend_share_time"] = false;
        hiddenEntry["today"] = new Dictionary<string, object?> { ["date"] = Today };
        var hidden = SocialDirectory.Friends(Element(new List<object?> { hiddenEntry }));
        check(hidden[0].FriendNewWords is null && hidden[0].FriendReviewWords is null,
            "unauthorized peer word counts stay unknown rather than becoming zero");
        check(hidden[0].SelfNewWords is null && hidden[0].SelfReviewWords is null,
            "an absent self summary stays unknown rather than becoming zero");
        check(!hidden[0].FriendShareCounts && !hidden[0].FriendShareCurve && !hidden[0].FriendShareTime, "revoked peer switches decode as false");

        // ---- peer switches and required fields never default permissive ----
        foreach (var missing in new[] { "share_counts", "share_curve", "share_time", "friend_share_counts", "friend_share_curve", "friend_share_time", "nickname", "friend_id", "pair_state", "today" })
        {
            var name = missing;
            Reject($"a friend row missing {name} is rejected", () =>
            {
                var entry = FriendObject(); entry.Remove(name);
                return SocialDirectory.Friends(Element(new List<object?> { entry }));
            });
        }
        Reject("a non-boolean sharing switch is rejected", () =>
        {
            var entry = FriendObject(); entry["share_counts"] = "true";
            return SocialDirectory.Friends(Element(new List<object?> { entry }));
        });
        Reject("a friends payload that is not an array is rejected", () => SocialDirectory.Friends(Element(new Dictionary<string, object?>())));
        Reject("a friend row that is not an object is rejected", () => SocialDirectory.Friends(Element(new List<object?> { 1 })));

        // ---- mixed relations cannot be spliced together ----
        Reject("a friend row whose pair_state belongs to another relation is rejected", () =>
        {
            var entry = FriendObject(); entry["relationship_id"] = OtherRelation;
            return SocialDirectory.Friends(Element(new List<object?> { entry }));
        });

        // ---- every relation is decoded independently ----
        var secondPair = PairStateObjectWith(("streak", 12), ("success", false));
        secondPair["relationship_id"] = OtherRelation;
        var secondEntry = FriendObject(secondPair);
        secondEntry["relationship_id"] = OtherRelation; secondEntry["friend_id"] = Guid.NewGuid(); secondEntry["nickname"] = "小红";
        var two = SocialDirectory.Friends(Element(new List<object?> { FriendObject(), secondEntry }));
        check(two.Count == 2 && two[0].Pair.Streak == 28 && two[1].Pair.Streak == 12 && !two[1].Pair.CloudSuccess,
            "two relations decode independently without sharing state");

        // ---- pair state: the legal not-yet-started shape ----
        var fresh = SocialDirectory.PairState(Element(PairStateObjectWith(("run_id", null), ("lifecycle", null), ("streak", null),
            ("lifetime_days", 0), ("self_done", false), ("friend_done", false), ("self_completed_at", null),
            ("friend_completed_at", null), ("self_cause", null), ("friend_cause", null), ("success", false))));
        check(fresh.Lifecycle == PairLifecycle.Active && fresh.Streak == 0 && !fresh.CloudSuccess,
            "a run without a current run is an ordinary zero-day wait, never broken or successful");
        check(fresh.Self is null && fresh.Friend is null, "no qualification means no completion");

        // ---- a timestamp alone never fabricates a completion ----
        // daily_summaries.cause is NOT NULL, so a cause without a qualified time is the ordinary
        // "summary exists but is not qualified yet" shape; the reverse is impossible and rejected.
        var notQualified = SocialDirectory.PairState(Element(PairStateObjectWith(("self_completed_at", null),
            ("friend_completed_at", null), ("friend_done", false), ("success", false))));
        check(notQualified.Self is null && notQualified.Friend is null, "a cause without a qualified time carries no instant");
        // A legally stored summary can be complete with qualified_at NULL (first binding day, or a
        // completion at or before BoundAt). The server still reports the day as done, so the day
        // must survive as done even though it has no instant and therefore no extreme.
        check(notQualified.SelfDone && !notQualified.FriendDone,
            "a complete-but-untimed day keeps the server's own done flag instead of reading as unfinished");
        Reject("a qualified time without its server cause is rejected", () =>
            SocialDirectory.PairState(Element(PairStateObjectWith(("self_cause", null)))));
        Reject("an unreadable completion cause is rejected", () =>
            SocialDirectory.PairState(Element(PairStateObjectWith(("self_cause", "made_up")))));
        Reject("an unreadable lifecycle is rejected", () => SocialDirectory.PairState(Element(PairStateObjectWith(("lifecycle", "paused")))));
        Reject("a negative streak is rejected", () => SocialDirectory.PairState(Element(PairStateObjectWith(("streak", -1)))));

        // ---- server dates are self-consistent and never in the future ----
        Reject("a pair date that disagrees with server_now is rejected", () => SocialDirectory.PairState(Element(PairStateObjectWith(("date", "2026-10-11")))));
        Reject("a success claimed without server success is rejected", () =>
            SocialDirectory.PairState(Element(PairStateObjectWith(("success", false), ("extreme", true)))));

        // ---- pending_previous confirms only the older day ----
        var pendingPrevious = SocialDirectory.PairState(Element(PairStateObjectWith(("streak", 5), ("success", false), ("extreme", false),
            ("self_done", false), ("friend_done", false), ("self_completed_at", null), ("friend_completed_at", null),
            ("self_cause", null), ("friend_cause", null), ("pending_previous", true))));
        check(pendingPrevious.PendingPrevious && pendingPrevious.Lifecycle == PairLifecycle.Active && !pendingPrevious.CloudSuccess,
            "a pending older day never manufactures a broken or successful new day");
        check(pendingPrevious.Self is null && pendingPrevious.Friend is null, "an unfinished today stays unfinished while an older day is pending");

        // ---- invitations ----
        // An invitation is only ever in one direction, so the two lists never share an id.
        var incoming = InviteObject("pending", "小明", id: InviteIncoming); incoming["sender_id"] = Friend;
        var outgoing = InviteObject("expired");
        var book = SocialDirectory.Invites(Element(new Dictionary<string, object?>
        {
            ["incoming"] = new List<object?> { incoming }, ["outgoing"] = new List<object?> { outgoing }, ["server_now"] = ServerNow
        }));
        check(book.Incoming.Count == 1 && book.Outgoing.Count == 1, "incoming and outgoing invitations decode separately");
        check(book.Incoming[0].State == SocialInviteState.Pending && book.Incoming[0].Nickname == "小明", "a pending invitation keeps its sender nickname");
        check(book.Outgoing[0].State == SocialInviteState.Expired && book.Outgoing[0].Nickname is null, "an unredeemed invitation has no recipient nickname");
        check(book.ServerNow == DateTimeOffset.Parse(ServerNow, System.Globalization.CultureInfo.InvariantCulture), "the invitation book carries server time");
        foreach (var (wire, expected) in new[] { ("accepted", SocialInviteState.Accepted), ("declined", SocialInviteState.Declined), ("cancelled", SocialInviteState.Cancelled), ("expired", SocialInviteState.Expired) })
        {
            var status = wire;
            check(SocialDirectory.Invites(Element(new Dictionary<string, object?>
            {
                ["incoming"] = new List<object?>(), ["outgoing"] = new List<object?> { InviteObject(status) }, ["server_now"] = ServerNow
            })).Outgoing[0].State == expected, $"invitation status {status} maps to its own decoded state");
        }
        Reject("an unknown invitation status is rejected", () => SocialDirectory.Invites(Element(new Dictionary<string, object?>
        {
            ["incoming"] = new List<object?>(), ["outgoing"] = new List<object?> { InviteObject("revoked") }, ["server_now"] = ServerNow
        })));

        var created = SocialDirectory.InviteCreated(Element(new Dictionary<string, object?>
        {
            ["id"] = Invite, ["expires_at"] = "2026-10-11T02:00:00+00:00", ["status"] = "pending"
        }));
        check(created.Id == Invite && created.State == SocialInviteState.Pending, "a created invitation decodes its id and pending state");
        var claim = SocialDirectory.InviteClaim(Element(new Dictionary<string, object?>
        {
            ["id"] = Invite, ["nickname"] = "小明", ["expires_at"] = "2026-10-11T02:00:00+00:00"
        }));
        check(claim.Nickname == "小明", "claiming previews the inviting nickname before acceptance");
        var answer = SocialDirectory.InviteAnswer(Element(new Dictionary<string, object?> { ["status"] = "accepted", ["relationship_id"] = Relation }));
        check(answer.State == SocialInviteState.Accepted && answer.RelationshipId == Relation, "accepting returns the new relationship");
        var declined = SocialDirectory.InviteAnswer(Element(new Dictionary<string, object?> { ["status"] = "declined" }));
        check(declined.State == SocialInviteState.Declined && declined.RelationshipId is null, "declining returns no relationship");

        // ---- profile ----
        var avatar = Mine.ToString("D") + "/20261010-abcdef.webp";
        var profile = SocialDirectory.Profile(Element(ProfileObject(avatar: avatar)));
        check(profile.UserId == Mine && profile.Nickname == "我" && profile.AvatarPath == avatar, "profile decodes nickname and avatar path");
        check(SocialDirectory.Profile(Element(ProfileObject())).AvatarPath is null, "an absent avatar stays absent");
        Reject("an over-long nickname is rejected", () => SocialDirectory.Profile(Element(ProfileObject(new string('x', 33)))));
        Reject("an empty nickname is rejected", () => SocialDirectory.Profile(Element(ProfileObject(""))));
        Reject("a foreign avatar path is rejected", () => SocialDirectory.Profile(Element(ProfileObject(avatar: Friend.ToString("D") + "/other.webp"))));

        // ---- status ----
        var bound = SocialDirectory.Status(Element(new Dictionary<string, object?>
        {
            ["binding"] = new Dictionary<string, object?> { ["binding_id"] = Relation, ["bound_at"] = "2026-10-01T00:00:00+00:00", ["first_day"] = "2026-10-01" },
            ["profile"] = ProfileObject(), ["server_now"] = ServerNow, ["date"] = Today
        }));
        check(bound.BindingId == Relation && bound.FirstDay == new DateOnly(2026, 10, 1) && bound.Profile is not null,
            "a bound status decodes its binding and profile");
        var unbound = SocialDirectory.Status(Element(new Dictionary<string, object?>
        {
            ["binding"] = null, ["profile"] = null, ["server_now"] = ServerNow, ["date"] = Today
        }));
        check(unbound.BindingId is null && unbound.Profile is null, "an unbound status stays unbound");

        // ---- sharing round trip ----
        var sharing = SocialDirectory.Sharing(Element(new Dictionary<string, object?>
        {
            ["share_counts"] = true, ["share_curve"] = false, ["share_time"] = true
        }));
        check(sharing.Time && !sharing.Curve, "sharing reply decodes the time and curve directions");
        Reject("a sharing reply missing a switch is rejected", () => SocialDirectory.Sharing(Element(new Dictionary<string, object?> { ["share_counts"] = true, ["share_time"] = true })));

        // ---- pair detail ----
        var detail = SocialDirectory.PairDetail(Element(new Dictionary<string, object?>
        {
            ["relationship_created_at"] = "2026-10-01T00:00:00+00:00", ["coverage_from"] = "2025-10-12",
            ["pending_days"] = new List<object?> { "2026-10-08" }, ["state"] = PairStateObject(),
            ["weekly"] = new List<object?>
            {
                new Dictionary<string, object?> { ["date"] = "2026-10-09", ["self_new_words"] = 20, ["self_review_words"] = 36,
                    ["self_seconds"] = 1800.0, ["self_time_coverage"] = "complete", ["self_scope"] = "full" },
                new Dictionary<string, object?> { ["date"] = "2026-10-08", ["self_new_words"] = 10, ["self_review_words"] = 0 }
            },
            ["friend_share_counts"] = false, ["friend_share_time"] = false, ["friend_share_curve"] = true,
            ["common_days"] = new List<object?> { "2026-10-09", "2026-10-07" },
            ["runs"] = new List<object?>
            {
                new Dictionary<string, object?> { ["run_id"] = RunId, ["start_day"] = "2026-10-01",
                    ["created_at"] = "2026-10-01T00:00:00+00:00", ["lifecycle"] = "active", ["streak"] = 28, ["broken_day"] = null }
            }
        }));
        check(detail.Weekly.Count == 2 && detail.CommonDays.Count == 2 && detail.PendingDays.Count == 1,
            "pair detail decodes its weekly, common and pending days");
        check(detail.Weekly[0].SelfSeconds == 1800 && detail.Weekly[0].SelfScope == "full", "an authorized self duration decodes");
        check(detail.Weekly[1].FriendNewWords is null && detail.Weekly[1].FriendSeconds is null,
            "an unauthorized weekly peer field stays absent instead of becoming zero");
        check(!detail.FriendShareCounts && detail.FriendShareCurve, "pair detail keeps per-direction peer switches");
        check(detail.Runs.Count == 1 && detail.Runs[0].Streak == 28 && detail.Runs[0].Lifecycle == PairLifecycle.Active, "run history decodes");
        check(detail.State.CloudSuccess, "pair detail carries the same authoritative state");
        Reject("a weekly date in the future is rejected", () => SocialDirectory.PairDetail(Element(new Dictionary<string, object?>
        {
            ["relationship_created_at"] = "2026-10-01T00:00:00+00:00", ["coverage_from"] = "2025-10-12", ["pending_days"] = new List<object?>(),
            ["state"] = PairStateObject(), ["weekly"] = new List<object?> { new Dictionary<string, object?> { ["date"] = "2026-10-11", ["self_new_words"] = 1 } },
            ["friend_share_counts"] = true, ["friend_share_time"] = true, ["friend_share_curve"] = true,
            ["common_days"] = new List<object?>(), ["runs"] = new List<object?>()
        })));
        Reject("a common day in the future is rejected", () => SocialDirectory.PairDetail(Element(new Dictionary<string, object?>
        {
            ["relationship_created_at"] = "2026-10-01T00:00:00+00:00", ["coverage_from"] = "2025-10-12", ["pending_days"] = new List<object?>(),
            ["state"] = PairStateObject(), ["weekly"] = new List<object?>(), ["friend_share_counts"] = true, ["friend_share_time"] = true,
            ["friend_share_curve"] = true, ["common_days"] = new List<object?> { "2026-10-11" }, ["runs"] = new List<object?>()
        })));
        Reject("a duplicate common day is rejected", () => SocialDirectory.PairDetail(Element(new Dictionary<string, object?>
        {
            ["relationship_created_at"] = "2026-10-01T00:00:00+00:00", ["coverage_from"] = "2025-10-12", ["pending_days"] = new List<object?>(),
            ["state"] = PairStateObject(), ["weekly"] = new List<object?>(), ["friend_share_counts"] = true, ["friend_share_time"] = true,
            ["friend_share_curve"] = true, ["common_days"] = new List<object?> { "2026-10-09", "2026-10-09" }, ["runs"] = new List<object?>()
        })));

        // ---- own aggregate drives only our own secondary upload ----
        check(SocialDirectory.OwnAggregate(friends) is { Time: true, Curve: true }, "own aggregate follows own switches across accepted relations");
        var timeOff = FriendObject(); timeOff["share_time"] = false;
        check(SocialDirectory.OwnAggregate(SocialDirectory.Friends(Element(new List<object?> { timeOff }))) is { Time: false, Curve: true },
            "disabling one direction turns off that secondary upload");
        check(SocialDirectory.OwnAggregate([]) is { Time: false, Curve: false }, "no accepted relation means nothing is uploaded");
        // The server strips peer counts when the peer has not authorized them, so the fixture must too.
        var peerOnly = FriendObject();
        peerOnly["friend_share_counts"] = false; peerOnly["friend_share_curve"] = false; peerOnly["friend_share_time"] = false;
        peerOnly["today"] = new Dictionary<string, object?> { ["date"] = Today, ["self_new_words"] = 20, ["self_review_words"] = 36 };
        Reject("peer counts present while the peer switch is off are rejected", () =>
        {
            var leak = FriendObject(); leak["friend_share_counts"] = false;
            return SocialDirectory.Friends(Element(new List<object?> { leak }));
        });
        check(SocialDirectory.OwnAggregate(SocialDirectory.Friends(Element(new List<object?> { peerOnly }))) is { Time: true, Curve: true },
            "a peer's switches never change what we upload");
    }

    private static Dictionary<string, object?> PairStateObjectWith(params (string Key, object? Value)[] changes)
    {
        var state = PairStateObject();
        foreach (var (key, value) in changes) state[key] = value;
        return state;
    }
}
