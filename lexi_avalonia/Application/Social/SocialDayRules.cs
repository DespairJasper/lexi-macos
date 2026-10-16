namespace Lexi.Social;

public static class SocialDayRules
{
    // Social learning days use fixed UTC+8 on every platform, regardless of device timezone/DST.
    public static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    public static DateOnly DateAt(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(BeijingOffset).DateTime);
    public static DateOnly JustEndedDate(DateTimeOffset at) => DateAt(at).AddDays(-1);
    public static DateTimeOffset DayStart(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), BeijingOffset);
    public static DateTimeOffset Deadline(DateOnly date) => DayStart(date.AddDays(4)).ToUniversalTime();
    public static bool AcceptsReceipt(DateOnly date, DateTimeOffset receivedAt) =>
        receivedAt >= DayStart(date) && receivedAt < Deadline(date);
    // Only the cloud uses this to finalize; a device cannot infer failure from its clock or missing cache.
    public static bool CanFinalizeMissing(DateOnly date, DateTimeOffset serverNow) => serverNow >= Deadline(date);
}
