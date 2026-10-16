namespace Lexi.Social;

public sealed record SocialCurvePoint(double Days, double Rate);

/// <summary>One decoded curve: the current user's or an authorized friend's.</summary>
public sealed record SocialCurveView(Guid UserId, string Nickname, DateTimeOffset TakenAt, int SampleCount,
    IReadOnlyList<double> Points, string Protocol);

public sealed record SocialCurveLegendEntry(Guid UserId, string Label, string Color, bool IsSelf,
    int SampleCount, int CurvePoints);

/// <summary>The predicted-recall curve, built from word-card stability.
/// This is the same average-stability protocol the statistics page already drew; it is extracted
/// here so the friend overlay and the page can never drift apart. Nothing in this type decides
/// scheduling — it only renders what the existing model already predicts.</summary>
public static class SocialCurve
{
    public const string Protocol = "fsrs-6-average-stability-v1";
    public const int DefaultSampleCount = 121;
    public const double MaximumDays = 30;

    /// <summary>121 samples over 0..30 days at quarter-day steps, each the mean predicted recall
    /// over the usable cards. Cards that are not finite and strictly positive carry no prediction
    /// and are excluded, exactly as the statistics page already did.</summary>
    public static IReadOnlyList<SocialCurvePoint> Build(IEnumerable<double> stabilities,
        Func<double, double, double> retrievability, int sampleCount = DefaultSampleCount)
    {
        ArgumentNullException.ThrowIfNull(stabilities);
        ArgumentNullException.ThrowIfNull(retrievability);
        if (sampleCount < 1) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        var usable = stabilities.Where(s => double.IsFinite(s) && s > 0).ToArray();
        // No usable card is an absence of information, not a flat zero curve.
        if (usable.Length == 0) return [];
        var points = new List<SocialCurvePoint>(sampleCount);
        for (var index = 0; index < sampleCount; index++)
        {
            var days = index * MaximumDays / (sampleCount - 1);
            var total = 0d;
            foreach (var stability in usable) total += retrievability(stability, days);
            points.Add(new(days, total / usable.Length));
        }
        return points;
    }
}

/// <summary>Curve visibility. Every entry is independently shown or hidden; the order is the
/// server's, never a ranking, and each curve keeps its own colour and sample count.</summary>
public static class SocialCurveLegend
{
    private static readonly string[] Palette = ["#357B9B", "#79BDD4", "#7C9A6B", "#B08A5A", "#8C7BB0", "#B0747B"];

    public static IReadOnlyList<SocialCurveLegendEntry> Build(SocialCurveView self, IReadOnlyList<SocialCurveView> friends)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(friends);
        var entries = new List<SocialCurveLegendEntry>
        {
            new(self.UserId, "自己 · " + self.Nickname, Palette[0], true, self.SampleCount, self.Points.Count)
        };
        // Friends keep the order the server returned. Sorting by any score would be a ranking.
        var colour = 1;
        foreach (var friend in friends)
        {
            if (friend is null || friend.UserId == self.UserId) continue;
            entries.Add(new(friend.UserId, friend.Nickname, Palette[colour % Palette.Length], false,
                friend.SampleCount, friend.Points.Count));
            colour++;
        }
        return entries;
    }
}
