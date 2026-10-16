namespace Lexi.Social;

/// <summary>One finished Beijing day in the weekly detail.</summary>
public sealed record SocialWeekDay(DateOnly Date, int SelfNewWords, int SelfReviewWords, double? SelfSeconds,
    string SelfScope, int? FriendNewWords, int? FriendReviewWords, double? FriendSeconds);

/// <summary>The seven finished Beijing days shown in the weekly detail.
/// The self series is the full local history, including days before the binding; the friend
/// series only exists where the server actually reported an authorized value, and a day the
/// server did not report stays missing so the chart can break the line instead of drawing zero.</summary>
public static class SocialWeek
{
    public const int Days = 7;
    public const string ScopeFull = "full";
    public const string ScopeBindingPartial = "binding_partial";

    public static IReadOnlyList<SocialWeekDay> Build(DateOnly today,
        IReadOnlyDictionary<DateOnly, SocialWordCounts> selfCounts,
        IReadOnlyDictionary<DateOnly, double?> selfSeconds,
        DateOnly? bindingFirstDay, IReadOnlyList<SocialWeeklyDay> server)
    {
        ArgumentNullException.ThrowIfNull(selfCounts);
        ArgumentNullException.ThrowIfNull(selfSeconds);
        ArgumentNullException.ThrowIfNull(server);
        var byDate = new Dictionary<DateOnly, SocialWeeklyDay>();
        foreach (var row in server)
        {
            if (row is null) throw new ArgumentException("Invalid weekly row.", nameof(server));
            if (row.Date >= today) throw new ArgumentException("The weekly detail only covers finished days.", nameof(server));
            if (!byDate.TryAdd(row.Date, row)) throw new ArgumentException("Duplicate weekly day.", nameof(server));
        }
        var week = new List<SocialWeekDay>(Days);
        for (var offset = Days; offset >= 1; offset--)
        {
            var date = today.AddDays(-offset);
            selfCounts.TryGetValue(date, out var counts);
            selfSeconds.TryGetValue(date, out var seconds);
            if (seconds is { } value && (!double.IsFinite(value) || value is < 0 or > 86400))
                throw new ArgumentException("Daily seconds must be finite and within one day.", nameof(selfSeconds));
            byDate.TryGetValue(date, out var row);
            // The binding day only holds the part of the day after binding, and the chart says so.
            var scope = bindingFirstDay is { } first && date <= first ? ScopeBindingPartial : ScopeFull;
            week.Add(new(date, counts?.NewWords ?? 0, counts?.ReviewWords ?? 0, seconds, scope,
                row?.FriendNewWords, row?.FriendReviewWords, row?.FriendSeconds));
        }
        return week;
    }
}
