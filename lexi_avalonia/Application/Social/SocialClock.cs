namespace Lexi.Social;

/// <summary>Trust exists only within this process and follows a server anchor plus a monotonic clock.</summary>
public sealed class SocialClock
{
    private DateTimeOffset? _server;
    private TimeSpan _monotonic;
    public void Observe(DateTimeOffset serverNow, DateTimeOffset localNow, TimeSpan elapsed)
    {
        // A clock skew prevents an extreme animation; it never erases learning or prevents ordinary qualification.
        _server = Math.Abs((serverNow - localNow).TotalSeconds) <= 30 ? serverNow : null;
        _monotonic = elapsed;
    }
    public bool IsTrusted(DateTimeOffset localNow, TimeSpan elapsed) => _server is { } server &&
        elapsed >= _monotonic && elapsed - _monotonic <= TimeSpan.FromHours(72) &&
        Math.Abs((server + (elapsed - _monotonic) - localNow).TotalSeconds) <= 30;
}
