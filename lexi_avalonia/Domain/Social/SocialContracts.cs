namespace Lexi.Social;

public enum QualificationCause { LearningComplete, PlanChange, Correction }

// Targets contain counts only. Word identities and the full plan never leave the device.
public sealed record SocialPlanTarget(string PlanId, bool Enabled, int Target,
    int Completed, DateTimeOffset? LastLearnedAt);

public sealed record PlanDayEligibility(bool Qualified, int Target, int Completed,
    DateTimeOffset? QualifiedAt, QualificationCause Cause);

public enum PairLifecycle { Active, Broken, Ended }
public enum SocialSyncStatus { Confirmed, Pending, Failed }
public sealed record SocialCompletion(DateOnly Date, DateTimeOffset EligibleAt, QualificationCause Cause);

// Cloud-confirmed presentation snapshot. It grants no client permission to write streaks.
public sealed record SocialPairView
{
    public Guid RelationshipId { get; init; }
    public Guid? RunId { get; init; }
    public string FriendName { get; init; } = "TA";
    public int Streak { get; init; }
    public DateOnly Today { get; init; }
    public PairLifecycle Lifecycle { get; init; }
    public SocialSyncStatus Sync { get; init; }
    public SocialCompletion? Self { get; init; }
    public SocialCompletion? Friend { get; init; }
    public bool CloudSuccess { get; init; }
    public DateOnly? SuccessDate { get; init; }
    public bool CloudExtremeConfirmed { get; init; }
    public bool BackfillConfirmed { get; init; }
    // The server decides "done" from target/completed alone. A legally stored summary can be
    // qualified without a qualified_at (first binding day, or a completion at or before BoundAt),
    // so the day's done-ness must be carried independently of any timestamp.
    public bool SelfDone { get; init; }
    public bool FriendDone { get; init; }
    public int LifetimeDays { get; init; }
    public bool PendingPrevious { get; init; }
    public DateOnly? BrokenDay { get; init; }
}

public sealed record SocialDailySummary(Guid BindingId, DateOnly Date, long Revision,
    int Target, int Completed, int NewWords, int ReviewWords,
    DateTimeOffset? QualifiedAt, QualificationCause Cause, bool TimeTrusted = false);
public sealed record SocialQueuedBatch(Guid BatchId, string Hash, SocialDailySummary Summary)
{
    // LoadPending always populates the exact hashed wire payload.
    public string RawPayload { get; init; } = "";
}

// ---- 3.3.1 friends / invitations / own sharing / profile / avatar ----

// Own sharing permissions toward one friend. Peer switches govern only what we may read back.
public sealed record SocialFriendView(
    Guid RelationshipId, Guid FriendId, string Nickname, string? AvatarPath,
    bool ShareCounts, bool ShareCurve, bool ShareTime,
    bool FriendShareCounts, bool FriendShareCurve, bool FriendShareTime,
    SocialPairView Pair, int? FriendNewWords, int? FriendReviewWords,
    int? SelfNewWords, int? SelfReviewWords);

public enum SocialInviteState { Pending, Accepted, Declined, Cancelled, Expired }

public sealed record SocialInviteView(Guid Id, string? Nickname, SocialInviteState State,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? HandledAt, Guid? RelationshipId);

public sealed record SocialInviteBook(IReadOnlyList<SocialInviteView> Incoming,
    IReadOnlyList<SocialInviteView> Outgoing, DateTimeOffset ServerNow);

// Created and claimed invitations are distinct: a claim only previews the sender's nickname.
public sealed record SocialInviteCreated(Guid Id, SocialInviteState State, DateTimeOffset ExpiresAt);
public sealed record SocialInviteClaim(Guid Id, string? Nickname, DateTimeOffset ExpiresAt);
public sealed record SocialInviteAnswer(SocialInviteState State, Guid? RelationshipId);

// Restarting a broken run needs the other side to agree; "waiting" means our request is now pending.
public enum SocialRestartState { Waiting, Declined, Started }
public sealed record SocialRestartAnswer(SocialRestartState State, Guid? RunId);

public sealed record SocialProfileView(Guid UserId, string Nickname, string? AvatarPath, DateTimeOffset UpdatedAt);

public sealed record SocialStatusView(Guid? BindingId, DateTimeOffset? BoundAt, DateOnly? FirstDay,
    SocialProfileView? Profile, DateTimeOffset ServerNow, DateOnly Date);

// One row per already-finished Beijing day. Absent peer fields mean "not authorized", never zero.
public sealed record SocialWeeklyDay(DateOnly Date, int? SelfNewWords, int? SelfReviewWords,
    int? FriendNewWords, int? FriendReviewWords, double? SelfSeconds, string? SelfTimeCoverage,
    string? SelfScope, double? FriendSeconds, string? FriendTimeCoverage, string? FriendScope);

public sealed record SocialRunView(Guid RunId, DateOnly StartDay, DateTimeOffset CreatedAt,
    PairLifecycle Lifecycle, int Streak, DateOnly? BrokenDay);

public sealed record SocialPairDetail(DateTimeOffset RelationshipCreatedAt, DateOnly CoverageFrom,
    IReadOnlyList<DateOnly> PendingDays, SocialPairView State, IReadOnlyList<SocialWeeklyDay> Weekly,
    bool FriendShareCounts, bool FriendShareTime, bool FriendShareCurve,
    IReadOnlyList<DateOnly> CommonDays, IReadOnlyList<SocialRunView> Runs);
