namespace Lexi.Social;

// These are LOCAL facts. IDs, plans and durations are never part of summary RPC serialization.
public sealed record SocialBindingState(Guid OwnerId, Guid BindingId, DateTimeOffset ProposedAt,
    DateOnly ExclusionDate, DateTimeOffset? BoundAt, DateOnly? FirstDay, string Nickname)
{
    public bool FirstDayBaselineConfirmed { get; init; }
}
public sealed record SocialDayObservation(DateOnly Date, IReadOnlyList<SocialPlanTarget> Plans,
    int NewWords, int ReviewWords, double ActiveSeconds, DateTimeOffset ObservedAt,
    DateTimeOffset? LastActualCompletionAt, QualificationCause Cause);
public sealed record SocialDayProjection(SocialDayObservation Facts, SocialDailySummary Summary);
public sealed record SocialFlushResult(int Accepted, int Stale, int Late, int Pending,
    bool Deferred = false, string? Error = null);
public sealed record SocialStoredReceipt(Guid BatchId, DateOnly Date, string Status, DateTimeOffset ReceivedAt);

public sealed record SocialDurationChange(DateOnly Date, double? Seconds, string Coverage);
// Protocol grid is exactly 0..30 days at 0.25 day intervals, with local predictions only.
public sealed record SocialCurveChange(DateTimeOffset TakenAt, int SampleCount, IReadOnlyList<double> Points);
public sealed record SocialSecondaryBatch(string Kind, DateOnly Date, long Revision, string Payload);
public sealed record SocialSecondarySharing(bool Time, bool Curve);
public sealed record SocialSecondaryFlushResult(int Accepted, int Late, int NotShared, int Pending, bool Deferred = false, string? Error = null);

// Diagnostic only: this persisted elapsed value must never establish trust after a process restart.
public sealed record SocialClockDiagnostic(DateTimeOffset ServerNow, DateTimeOffset LocalObservedAt,
    TimeSpan ElapsedAtObservation, bool TrustedAtObservation);
