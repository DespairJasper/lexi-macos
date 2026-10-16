using System.Globalization;
using System.Text.Json;

namespace Lexi.Social;

/// <summary>Typed, fail-closed decoding of social RPC payloads.
/// Every value is validated before it reaches the UI: missing or malformed data is rejected
/// rather than defaulted, an absent field stays unknown instead of becoming zero, and a
/// peer-controlled switch never falls back to a permissive value. No response is ever logged raw.</summary>
public static class SocialDirectory
{
    // ---------- friends ----------

    public static IReadOnlyList<SocialFriendView> Friends(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array) throw Malformed();
        var result = new List<SocialFriendView>();
        var relations = new HashSet<Guid>();
        foreach (var entry in payload.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw Malformed();
            var relationship = RequiredGuid(entry, "relationship_id");
            if (!relations.Add(relationship)) throw Malformed();
            var friend = RequiredGuid(entry, "friend_id");
            var pair = PairState(Required(entry, "pair_state"));
            // A row may only ever carry its own relation's confirmed state.
            if (pair.RelationshipId != relationship) throw Malformed();
            var today = Required(entry, "today");
            if (today.ValueKind != JsonValueKind.Object || RequiredDate(today, "date") != pair.Today) throw Malformed();
            var peerCounts = RequiredBool(entry, "friend_share_counts");
            var friendNew = OptionalCount(today, "friend_new_words");
            var friendReview = OptionalCount(today, "friend_review_words");
            // Refusing to surface peer counts the peer has not authorized is the whole point of the switch.
            if (!peerCounts && (friendNew is not null || friendReview is not null)) throw Malformed();
            result.Add(new SocialFriendView(relationship, friend, Nickname(Required(entry, "nickname")), AvatarPath(entry, friend),
                RequiredBool(entry, "share_counts"), RequiredBool(entry, "share_curve"), RequiredBool(entry, "share_time"),
                peerCounts, RequiredBool(entry, "friend_share_curve"), RequiredBool(entry, "friend_share_time"),
                pair, friendNew, friendReview, OptionalCount(today, "self_new_words"), OptionalCount(today, "self_review_words")));
        }
        return result;
    }

    /// <summary>At least one accepted relation must permit a direction before we upload it.
    /// Only our own switches count; a peer's switches govern what we may read.</summary>
    public static SocialSecondarySharing OwnAggregate(IReadOnlyList<SocialFriendView> friends)
    {
        ArgumentNullException.ThrowIfNull(friends);
        var time = false; var curve = false;
        foreach (var friend in friends)
        {
            if (friend is null) throw new ArgumentException("Invalid friend list.", nameof(friends));
            time |= friend.ShareTime; curve |= friend.ShareCurve;
        }
        return new(time, curve);
    }

    // ---------- invitations ----------

    public static SocialInviteBook Invites(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        var serverNow = RequiredInstant(payload, "server_now");
        var incoming = InviteList(payload, "incoming");
        var outgoing = InviteList(payload, "outgoing");
        var ids = new HashSet<Guid>();
        foreach (var invite in incoming.Concat(outgoing)) if (!ids.Add(invite.Id)) throw Malformed();
        return new(incoming, outgoing, serverNow);
    }

    public static SocialInviteCreated InviteCreated(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        return new(RequiredGuid(payload, "id"), InviteState(RequiredString(payload, "status")), RequiredInstant(payload, "expires_at"));
    }

    /// <summary>Claiming only previews the inviting nickname; acceptance is a separate explicit action.</summary>
    public static SocialInviteClaim InviteClaim(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        return new(RequiredGuid(payload, "id"), OptionalNickname(payload), RequiredInstant(payload, "expires_at"));
    }

    public static SocialInviteAnswer InviteAnswer(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        var state = InviteState(RequiredString(payload, "status"));
        var relationship = OptionalGuid(payload, "relationship_id");
        if (state == SocialInviteState.Accepted && relationship is null) throw Malformed();
        return new(state, relationship);
    }

    /// <summary>Only a broken run can restart, and only the other member can start it.
    /// A "started" reply must name the new run; anything else would leave the UI guessing.</summary>
    public static SocialRestartAnswer Restart(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        var state = RequiredString(payload, "status") switch
        {
            "waiting" => SocialRestartState.Waiting,
            "declined" => SocialRestartState.Declined,
            "started" => SocialRestartState.Started,
            _ => throw Malformed()
        };
        var run = OptionalGuid(payload, "run_id");
        if (state == SocialRestartState.Started != (run is not null)) throw Malformed();
        return new(state, run);
    }

    /// <summary>The current user's curve plus every friend's that we are still authorized to read.
    /// Each curve is a self-contained 121-point sample; nothing here ranks or compares them.</summary>
    public static IReadOnlyList<SocialCurveView> CurveList(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array) throw Malformed();
        var result = new List<SocialCurveView>();
        var users = new HashSet<Guid>();
        foreach (var row in payload.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw Malformed();
            var user = RequiredGuid(row, "user_id");
            if (!users.Add(user)) throw Malformed();
            var takenAt = RequiredInstant(row, "taken_at");
            var samples = RequiredInt(row, "sample_count");
            if (samples is < 1 or > 1_000_000) throw Malformed();
            var protocol = RequiredString(row, "protocol");
            if (string.IsNullOrWhiteSpace(protocol)) throw Malformed();
            var points = Required(row, "points");
            if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() != SocialCurve.DefaultSampleCount)
                throw Malformed();
            var rates = new double[SocialCurve.DefaultSampleCount];
            var index = 0;
            foreach (var point in points.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Number || !point.TryGetDouble(out var rate) ||
                    !double.IsFinite(rate) || rate is < 0 or > 1) throw Malformed();
                rates[index++] = rate;
            }
            result.Add(new(user, Nickname(Required(row, "nickname")), takenAt, samples, rates, protocol));
        }
        return result;
    }

    // ---------- profile and status ----------

    public static SocialProfileView Profile(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        var user = RequiredGuid(payload, "user_id");
        return new(user, Nickname(Required(payload, "nickname")), AvatarPath(payload, user), RequiredInstant(payload, "updated_at"));
    }

    public static SocialStatusView Status(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        var serverNow = RequiredInstant(payload, "server_now");
        var date = RequiredDate(payload, "date");
        if (SocialDayRules.DateAt(serverNow) != date) throw Malformed();
        Guid? bindingId = null; DateTimeOffset? boundAt = null; DateOnly? firstDay = null;
        var binding = Required(payload, "binding");
        if (binding.ValueKind != JsonValueKind.Null)
        {
            if (binding.ValueKind != JsonValueKind.Object) throw Malformed();
            bindingId = RequiredGuid(binding, "binding_id");
            boundAt = RequiredInstant(binding, "bound_at");
            firstDay = RequiredDate(binding, "first_day");
            if (SocialDayRules.DateAt(boundAt.Value) > firstDay.Value) throw Malformed();
        }
        var profile = Required(payload, "profile");
        return new(bindingId, boundAt, firstDay, profile.ValueKind == JsonValueKind.Null ? null : Profile(profile), serverNow, date);
    }

    /// <summary>The three switches ride together; a partial reply cannot half-apply a permission change.</summary>
    public static SocialSecondarySharing Sharing(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        RequiredBool(payload, "share_counts");
        return new(RequiredBool(payload, "share_time"), RequiredBool(payload, "share_curve"));
    }

    // ---------- pair state ----------

    public static SocialPairView PairState(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        var relationship = RequiredGuid(payload, "relationship_id");
        var runId = OptionalGuid(payload, "run_id");
        var date = RequiredDate(payload, "date");
        var serverNow = RequiredInstant(payload, "server_now");
        // The server's own Beijing day must agree with the instant it was derived from.
        if (SocialDayRules.DateAt(serverNow) != date) throw Malformed();
        // A relation with no current run is a legal, ordinary zero-day wait; it is never broken.
        var lifecycle = OptionalString(payload, "lifecycle") is { } wire ? Lifecycle(wire) : PairLifecycle.Active;
        var streak = OptionalInt(payload, "streak") ?? 0;
        var lifetime = RequiredInt(payload, "lifetime_days");
        if (streak < 0 || lifetime < 0) throw Malformed();
        var selfDone = RequiredBool(payload, "self_done");
        var friendDone = RequiredBool(payload, "friend_done");
        var self = Completion(payload, "self", date);
        var friend = Completion(payload, "friend", date);
        var success = RequiredBool(payload, "success");
        var extreme = RequiredBool(payload, "extreme");
        if (extreme && !success) throw Malformed();
        return new SocialPairView
        {
            RelationshipId = relationship, RunId = runId, Today = date, Lifecycle = lifecycle, Streak = streak,
            LifetimeDays = lifetime, Self = self, Friend = friend, SelfDone = selfDone, FriendDone = friendDone,
            CloudSuccess = success,
            SuccessDate = success ? date : null, CloudExtremeConfirmed = extreme,
            PendingPrevious = RequiredBool(payload, "pending_previous"), BrokenDay = OptionalDate(payload, "broken_day"),
            Sync = SocialSyncStatus.Confirmed
        };
    }

    /// <summary>A qualified instant is only a completion together with the server's own cause.
    /// The database stores a cause for every summary, so a cause without a time is simply "not qualified yet".
    /// A day the server reports as done with no qualified instant is left without a time rather
    /// than given an invented one; the caller reads that day's done-ness from the done flag.</summary>
    private static SocialCompletion? Completion(JsonElement payload, string prefix, DateOnly date)
    {
        if (OptionalInstant(payload, prefix + "_completed_at") is not { } at) return null;
        if (OptionalString(payload, prefix + "_cause") is not { } cause) throw Malformed();
        if (SocialDayRules.DateAt(at) != date) throw Malformed();
        var mapped = cause switch
        {
            "learning_complete" => QualificationCause.LearningComplete,
            "plan_change" => QualificationCause.PlanChange,
            "correction" => QualificationCause.Correction,
            _ => throw Malformed()
        };
        return new SocialCompletion(date, at, mapped);
    }

    // ---------- pair detail ----------

    public static SocialPairDetail PairDetail(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Malformed();
        var created = RequiredInstant(payload, "relationship_created_at");
        var coverage = RequiredDate(payload, "coverage_from");
        var state = PairState(Required(payload, "state"));
        // The window is documented as the trailing year. An implausible one would make the UI do
        // work proportional to its length, so it is bounded rather than trusted.
        if (coverage > state.Today || coverage < state.Today.AddDays(-MaximumCoverageDays)) throw Malformed();
        var pending = DayList(payload, "pending_days", state.Today);
        var common = DayList(payload, "common_days", state.Today);
        var peerCounts = RequiredBool(payload, "friend_share_counts");
        var peerTime = RequiredBool(payload, "friend_share_time");
        var weekly = new List<SocialWeeklyDay>();
        var days = new HashSet<DateOnly>();
        var array = Required(payload, "weekly");
        if (array.ValueKind != JsonValueKind.Array) throw Malformed();
        foreach (var row in array.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw Malformed();
            // Only finished Beijing days belong in the weekly comparison.
            var day = RequiredDate(row, "date");
            if (day >= state.Today || !days.Add(day)) throw Malformed();
            var friendNew = OptionalCount(row, "friend_new_words");
            var friendReview = OptionalCount(row, "friend_review_words");
            var friendSeconds = OptionalSeconds(row, "friend_seconds");
            var friendCoverage = OptionalString(row, "friend_time_coverage");
            var friendScope = OptionalString(row, "friend_scope");
            // Every peer field is gated by the same direction the server used; a value arriving
            // under a direction the peer did not grant is refused rather than displayed.
            if (!peerCounts && (friendNew is not null || friendReview is not null) ||
                !peerTime && (friendSeconds is not null || friendCoverage is not null || friendScope is not null)) throw Malformed();
            weekly.Add(new(day, OptionalCount(row, "self_new_words"), OptionalCount(row, "self_review_words"),
                friendNew, friendReview, OptionalSeconds(row, "self_seconds"), OptionalString(row, "self_time_coverage"),
                OptionalString(row, "self_scope"), friendSeconds, friendCoverage, friendScope));
        }
        return new(created, coverage, pending, state, weekly, peerCounts, peerTime, RequiredBool(payload, "friend_share_curve"),
            common, RunList(payload));
    }

    private static IReadOnlyList<SocialRunView> RunList(JsonElement payload)
    {
        var array = Required(payload, "runs");
        if (array.ValueKind != JsonValueKind.Array) throw Malformed();
        var result = new List<SocialRunView>();
        var ids = new HashSet<Guid>();
        foreach (var row in array.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw Malformed();
            var id = RequiredGuid(row, "run_id");
            if (!ids.Add(id)) throw Malformed();
            var streak = RequiredInt(row, "streak");
            if (streak < 0) throw Malformed();
            result.Add(new(id, RequiredDate(row, "start_day"), RequiredInstant(row, "created_at"),
                Lifecycle(RequiredString(row, "lifecycle")), streak, OptionalDate(row, "broken_day")));
        }
        return result;
    }

    // ---------- shared readers ----------

    private const int MaximumCoverageDays = 370;

    private static InvalidOperationException Malformed() =>
        new("社交服务返回的数据无法识别；本机学习记录已保留，本次响应未被采用。");

    private static JsonElement Required(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) throw Malformed();
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (value.ValueKind != JsonValueKind.String) throw Malformed();
        return value.GetString()!;
    }

    private static bool RequiredBool(JsonElement parent, string name) => Required(parent, name).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Malformed()
    };

    private static int RequiredInt(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Malformed();
        return number;
    }

    private static Guid RequiredGuid(JsonElement parent, string name)
    {
        var text = RequiredString(parent, name);
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty) throw Malformed();
        return value;
    }

    private static Guid? OptionalGuid(JsonElement parent, string name)
    {
        if (OptionalString(parent, name) is not { } text) return null;
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty) throw Malformed();
        return value;
    }

    private static int? OptionalInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Malformed();
        return number;
    }

    private static int? OptionalCount(JsonElement parent, string name)
    {
        if (OptionalInt(parent, name) is not { } number) return null;
        if (number < 0) throw Malformed();
        return number;
    }

    private static double? OptionalSeconds(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds is < 0 or > 86400) throw Malformed();
        return seconds;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Malformed();
        return value.GetString();
    }

    private static DateTimeOffset RequiredInstant(JsonElement parent, string name) =>
        ParseInstant(RequiredString(parent, name));

    private static DateTimeOffset? OptionalInstant(JsonElement parent, string name) =>
        OptionalString(parent, name) is { } text ? ParseInstant(text) : null;

    private static DateTimeOffset ParseInstant(string text)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            throw Malformed();
        return value;
    }

    private static DateOnly RequiredDate(JsonElement parent, string name) => ParseDate(RequiredString(parent, name));

    private static DateOnly? OptionalDate(JsonElement parent, string name) =>
        OptionalString(parent, name) is { } text ? ParseDate(text) : null;

    private static DateOnly ParseDate(string text)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw Malformed();
        return value;
    }

    private static string Nickname(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Malformed();
        var text = value.GetString()!;
        // Postgres counts code points and trims only U+0020; counting UTF-16 units or trimming
        // the full Unicode whitespace set would reject a nickname the server considers valid.
        if (text.Trim(' ') != text || text.EnumerateRunes().Count() is < 1 or > 32) throw Malformed();
        return text;
    }

    private static string? OptionalNickname(JsonElement parent, string name = "nickname") =>
        OptionalString(parent, name) is { } text ? Nickname(JsonSerializer.SerializeToElement(text)) : null;

    /// <summary>An avatar may only ever point inside the owner's own folder with a safe file name.</summary>
    private static string? AvatarPath(JsonElement parent, Guid owner)
    {
        if (OptionalString(parent, "avatar_path") is not { } path) return null;
        var parts = path.Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out var holder) || holder != owner) throw Malformed();
        // The bucket policy bounds the stem only by "one or more of [A-Za-z0-9_-]"; a narrower
        // client rule would reject a path the server considers valid and drop the whole directory.
        if (parts[1].Length is < 5 or > 128 || !parts[1].EndsWith(".webp", StringComparison.Ordinal) ||
            parts[1][..^5].Length < 1 ||
            parts[1][..^5].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) throw Malformed();
        return path;
    }

    private static PairLifecycle Lifecycle(string wire) => wire switch
    {
        "active" => PairLifecycle.Active, "broken" => PairLifecycle.Broken, "ended" => PairLifecycle.Ended,
        _ => throw Malformed()
    };

    private static SocialInviteState InviteState(string wire) => wire switch
    {
        "pending" => SocialInviteState.Pending, "accepted" => SocialInviteState.Accepted,
        "declined" => SocialInviteState.Declined, "cancelled" => SocialInviteState.Cancelled,
        "expired" => SocialInviteState.Expired, _ => throw Malformed()
    };

    private static IReadOnlyList<SocialInviteView> InviteList(JsonElement payload, string name)
    {
        var array = Required(payload, name);
        if (array.ValueKind != JsonValueKind.Array) throw Malformed();
        var result = new List<SocialInviteView>();
        foreach (var row in array.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw Malformed();
            result.Add(new(RequiredGuid(row, "id"), OptionalNickname(row), InviteState(RequiredString(row, "status")),
                RequiredInstant(row, "created_at"), RequiredInstant(row, "expires_at"),
                OptionalInstant(row, "handled_at"), OptionalGuid(row, "relationship_id")));
        }
        return result;
    }

    /// <summary>Already-finished or already-confirmed Beijing days only; a future date is never trusted.</summary>
    private static IReadOnlyList<DateOnly> DayList(JsonElement payload, string name, DateOnly limit)
    {
        var array = Required(payload, name);
        if (array.ValueKind != JsonValueKind.Array) throw Malformed();
        var result = new List<DateOnly>();
        var days = new HashSet<DateOnly>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Malformed();
            var day = ParseDate(item.GetString()!);
            if (day > limit || !days.Add(day)) throw Malformed();
            result.Add(day);
        }
        return result;
    }
}
