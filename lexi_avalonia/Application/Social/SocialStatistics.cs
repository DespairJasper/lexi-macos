namespace Lexi.Social;

public enum JointDayKnowledge { Unknown, Pending, NonJoint, Success }
public sealed record SocialLocalDay(DateOnly Date, JointDayKnowledge Joint, double? ActiveSeconds, int? Words);
public sealed record SocialMetricAggregate(double? Total, double? Mean, int Days);
public sealed record SocialMetricComparison(SocialMetricAggregate Joint, SocialMetricAggregate NonJoint,
    double? MeanDifference, double? PercentDifference);
public sealed record SocialStatisticsComparison(int JointDays, int NonJointDays,
    SocialMetricComparison Seconds, SocialMetricComparison Words);

/// <summary>Local descriptive comparison only. Cloud success dates are input, never inferred here.</summary>
public static class SocialStatistics
{
    public static SocialStatisticsComparison Build(IEnumerable<SocialLocalDay> days,
        DateOnly today, DateOnly? from = null, bool activeOnly = false)
    {
        ArgumentNullException.ThrowIfNull(days);
        var dates = new HashSet<DateOnly>();
        var joint = new Accumulator(); var nonJoint = new Accumulator();
        foreach (var day in days)
        {
            if (day is null || !dates.Add(day.Date) || !Enum.IsDefined(day.Joint) || day.Words is < 0 ||
                day.ActiveSeconds is { } time && (!double.IsFinite(time) || time < 0 || time > 86400))
                throw new ArgumentException("Invalid or duplicate daily metric.", nameof(days));
            if (day.Date >= today || from is { } start && day.Date < start ||
                day.Joint is JointDayKnowledge.Unknown or JointDayKnowledge.Pending) continue;
            if (activeOnly && !(day.ActiveSeconds > 0 || day.Words > 0)) continue;
            var group = day.Joint == JointDayKnowledge.Success ? joint : nonJoint;
            group.Days++;
            if (day.ActiveSeconds is { } seconds) { group.Seconds += seconds; group.SecondDays++; }
            if (day.Words is { } words) { group.Words += words; group.WordDays++; }
        }
        return new(joint.Days, nonJoint.Days,
            Compare(joint.Seconds, joint.SecondDays, nonJoint.Seconds, nonJoint.SecondDays),
            Compare(joint.Words, joint.WordDays, nonJoint.Words, nonJoint.WordDays));
    }

    private static SocialMetricComparison Compare(double jointTotal, int jointDays, double otherTotal, int otherDays)
    {
        var joint = Aggregate(jointTotal, jointDays); var other = Aggregate(otherTotal, otherDays);
        double? difference = joint.Mean is { } a && other.Mean is { } b ? a - b : null;
        double? percentage = other.Mean is > 0 && difference is { } d ? d / other.Mean.Value * 100 : null;
        return new(joint, other, difference, percentage);
    }
    private static SocialMetricAggregate Aggregate(double total, int days) =>
        days == 0 ? new(null, null, 0) : new(total, total / days, days);
    private sealed class Accumulator
    {
        public int Days, SecondDays, WordDays;
        public double Seconds, Words;
    }
}
