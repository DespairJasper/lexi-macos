namespace Lexi.Social;

public sealed record SocialWordCounts(int NewWords, int ReviewWords);

/// <summary>Read-only projection of durable canonical rows and foreground intervals.</summary>
public static class SocialFactProjection
{
    public static QualificationCause SavedCause(bool firstLearningCompleted, bool intentionalPlanChange) =>
        firstLearningCompleted ? QualificationCause.LearningComplete :
        intentionalPlanChange ? QualificationCause.PlanChange : QualificationCause.Correction;

    public static SocialWordCounts Counts(IReadOnlyList<CanonicalHistoryRow> rows, DateOnly date, DateTimeOffset boundAt)
    {
        var valid = rows.Where(r => !string.IsNullOrWhiteSpace(r.WordKey) && r.CompletedAtUtc != default).ToArray();
        var first = valid.GroupBy(r => r.WordKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(r => Utc(r.CompletedAtUtc)), StringComparer.Ordinal);
        var keys = valid.Where(r => Utc(r.CompletedAtUtc) >= boundAt && SocialDayRules.DateAt(Utc(r.CompletedAtUtc)) == date)
            .Select(r => r.WordKey).Distinct(StringComparer.Ordinal).ToArray();
        var newWords = keys.Count(k => SocialDayRules.DateAt(first[k]) == date);
        return new(newWords, keys.Length - newWords);
    }

    public static double Seconds(IReadOnlyList<StudyActivity> activities, DateOnly date, DateTimeOffset boundAt)
    {
        var start = SocialDayRules.DayStart(date); if (boundAt > start) start = boundAt;
        var end = SocialDayRules.DayStart(date.AddDays(1)); var total = 0d;
        foreach (var activity in activities.Where(StudyStatistics.IsValid).DistinctBy(a => a.ActivityId))
        {
            var actualStart = Utc(activity.StartUtc); var actualEnd = Utc(activity.EndUtc);
            var left = actualStart > start ? actualStart : start; var right = actualEnd < end ? actualEnd : end;
            if (right > left) total += activity.Seconds * (right - left).TotalSeconds / (actualEnd - actualStart).TotalSeconds;
        }
        return Math.Clamp(total, 0, 86400);
    }

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
