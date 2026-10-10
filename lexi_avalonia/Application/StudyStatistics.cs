namespace Lexi;

public enum StatisticsPeriod { Day, Week, Month, Cumulative }
public enum StatisticsActivityFilter { All, Cards, Spelling }
public enum StudyActivityKind { FirstLearn, Review, Spelling }
public sealed record StudyActivity(string SessionId, StudyActivityKind Kind, DateTime StartUtc, DateTime EndUtc, double Seconds)
{
    public string ActivityId { get; init; } = Guid.NewGuid().ToString("N");
}
public sealed class StudyStatisticsDay
{
    public double FirstLearnSeconds { get; set; }
    public double ReviewSeconds { get; set; }
    public double SpellingSeconds { get; set; }
    public int Words { get; set; }
    public int NewWords { get; set; }
    public double Seconds(StatisticsActivityFilter filter) => filter switch { StatisticsActivityFilter.Cards => FirstLearnSeconds + ReviewSeconds, StatisticsActivityFilter.Spelling => SpellingSeconds, _ => FirstLearnSeconds + ReviewSeconds + SpellingSeconds };
}
public sealed record ForgettableWord(string WordKey, int Forgotten, int Samples, int AllForgotten) { public double Rate => Samples == 0 ? 0 : (double)Forgotten / Samples; }
public sealed record StatisticsColumn(DateOnly Start, DateOnly End, double Seconds, double FirstLearnSeconds, double ReviewSeconds, double SpellingSeconds, int NewWords, bool Future);
public sealed record RecallObservation(double Days, double Rate, int Samples);
public sealed class StudyStatisticsSnapshot
{
    public Dictionary<DateOnly, StudyStatisticsDay> Days { get; init; } = [];
    public DateOnly Today { get; init; }
    public int LearnedWords { get; init; }
    public int LearningDays => Days.Count;
    public int Reviews { get; init; }
    public int LongestStreak { get; init; }
    public int CurrentStreak { get; init; }
    public int PeakWords => Days.Values.Select(d => d.Words).DefaultIfEmpty().Max();
    public double PeakSeconds => Days.Values.Select(d => d.Seconds(StatisticsActivityFilter.All)).DefaultIfEmpty().Max();
    public double LongestSessionSeconds { get; init; }
    public double FirstLearnSeconds => Days.Values.Sum(d => d.FirstLearnSeconds);
    public double ReviewSeconds => Days.Values.Sum(d => d.ReviewSeconds);
    public double CardSeconds => FirstLearnSeconds + ReviewSeconds;
    public double SpellingSeconds => Days.Values.Sum(d => d.SpellingSeconds);
    public IReadOnlyList<ForgettableWord> Forgettable { get; init; } = [];
    public IReadOnlyList<ForgettableWord> LifetimeForgettable { get; init; } = [];
    public IReadOnlyList<ForgettableWord> InsufficientWords { get; init; } = [];
    public IReadOnlyList<RecallObservation> Observations { get; init; } = [];
}
/// <summary>Pure projections of valid canonical facts and measured foreground activity. Never estimates missing historical time.</summary>
public static class StudyStatistics
{
    public static StudyStatisticsSnapshot Build(IEnumerable<CanonicalHistoryRow> history, IEnumerable<StudyActivity> activities, DateTime nowUtc, TimeZoneInfo zone)
    {
        var now = Utc(nowUtc);
        var today = LocalDate(now, zone);
        var rows = history.Where(r => r.ReviewedAtUtc <= now && r.CompletedAtUtc <= now && !string.IsNullOrWhiteSpace(r.WordKey))
            .GroupBy(r => (r.WordKey, r.SessionId)).Select(g => g.OrderByDescending(r => r.Revision).First())
            .OrderBy(r => r.ReviewedAtUtc).ThenBy(r => r.CanonicalSequence).ToArray();
        var days = new Dictionary<DateOnly, StudyStatisticsDay>();
        StudyStatisticsDay Day(DateOnly date) { if (!days.TryGetValue(date, out var d)) days[date] = d = new(); return d; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in rows.OrderBy(r=>r.CompletedAtUtc).GroupBy(r => LocalDate(r.CompletedAtUtc, zone)))
        {
            var day = Day(group.Key); day.Words = group.Select(r => r.WordKey).Distinct(StringComparer.Ordinal).Count();
            foreach (var row in group) if (seen.Add(row.WordKey)) day.NewWords++;
        }
        var validActivities = activities.Where(a => IsValid(a) && a.StartUtc <= now).GroupBy(a => a.ActivityId).Select(g => g.First()).ToArray();
        foreach (var activity in validActivities)
        {
            var start = Utc(activity.StartUtc); var end = Utc(activity.EndUtc); if (end > now) end = now;
            var fraction = activity.Seconds / (Utc(activity.EndUtc) - start).TotalSeconds;
            while (start < end)
            {
                var date = LocalDate(start, zone);
                var nextLocal = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
                // In zones with a skipped midnight, move to the first valid wall-clock time.
                while (zone.IsInvalidTime(nextLocal)) nextLocal = nextLocal.AddMinutes(1);
                var boundary = TimeZoneInfo.ConvertTimeToUtc(nextLocal, zone);
                var until = end < boundary ? end : boundary;
                if (until <= start) break;
                var seconds = (until - start).TotalSeconds * fraction; var day = Day(date);
                if (activity.Kind == StudyActivityKind.FirstLearn) day.FirstLearnSeconds += seconds;
                else if (activity.Kind == StudyActivityKind.Review) day.ReviewSeconds += seconds;
                else day.SpellingSeconds += seconds;
                start = until;
            }
        }
        var reviews = rows.Where(r => r.Origin == CanonicalOrigin.FirstRetrieval).ToArray();
        var forgetting = reviews.GroupBy(r => r.WordKey).Select(g => {
            var recent = g.Where(r => r.ReviewedAtUtc >= now.AddDays(-30)).ToArray();
            return new ForgettableWord(g.Key,recent.Count(r => r.Rating == StudyRating.Forgot),recent.Length,g.Count(r => r.Rating == StudyRating.Forgot));
        }).ToArray();
        var longest = 0; var streak = 0; DateOnly? previous = null;
        foreach (var date in days.Keys.Order()) { streak = previous?.AddDays(1) == date ? streak + 1 : 1; longest = Math.Max(longest, streak); previous = date; }
        var current = 0; var cursor = days.ContainsKey(today) ? today : today.AddDays(-1);
        while (days.ContainsKey(cursor)) { current++; cursor = cursor.AddDays(-1); }
        var bins = new double[] { 0,1,3,7,14,30,60,90,double.PositiveInfinity };
        var observations = new List<(double Days, bool Success)>();
        foreach (var wordRows in rows.GroupBy(r => r.WordKey))
        {
            CanonicalHistoryRow? prior = null;
            foreach (var row in wordRows) {
                if (prior != null && row.Origin == CanonicalOrigin.FirstRetrieval) {
                    var elapsed = (Utc(row.ReviewedAtUtc) - Utc(prior.CompletedAtUtc)).TotalDays;
                    if (elapsed >= 0) observations.Add((elapsed,row.Rating != StudyRating.Forgot));
                }
                prior = row;
            }
        }
        var observed = new List<RecallObservation>();
        for (var i=0;i<bins.Length-1;i++) { var items=observations.Where(o=>o.Days>=bins[i]&&o.Days<bins[i+1]).ToArray(); if (items.Length>0) observed.Add(new(items.Average(o=>o.Days),items.Count(o=>o.Success)/(double)items.Length,items.Length)); }
        return new() { Days=days, Today=today, LearnedWords=seen.Count, Reviews=reviews.Length, LongestStreak=longest, CurrentStreak=current,
            LongestSessionSeconds=validActivities.GroupBy(a=>a.SessionId).Select(g=>g.Sum(a=>a.Seconds)).DefaultIfEmpty().Max(),
            LifetimeForgettable=forgetting.OrderByDescending(w=>w.AllForgotten).ThenByDescending(w=>w.Samples).ThenBy(w=>w.WordKey,StringComparer.Ordinal).Take(50).ToArray(),
            Forgettable=forgetting.Where(w=>w.Samples>=3).OrderByDescending(w=>w.Rate).ThenByDescending(w=>w.Samples).ThenBy(w=>w.WordKey,StringComparer.Ordinal).Take(50).ToArray(),
            InsufficientWords=forgetting.Where(w=>w.Samples<3&&w.Samples>0).OrderByDescending(w=>w.Forgotten).ThenBy(w=>w.WordKey,StringComparer.Ordinal).ToArray(), Observations=observed };
    }
    public static IReadOnlyList<StatisticsColumn> BuildGrid(StudyStatisticsSnapshot snapshot, int year, StatisticsPeriod period, StatisticsActivityFilter filter)
    {
        var first = new DateOnly(year,1,1); var last = new DateOnly(year,12,31);
        var gridStart = first.AddDays(-(((int)first.DayOfWeek+6)%7));
        var columns = new List<StatisticsColumn>();
        var count=(last.DayNumber-gridStart.DayNumber+7)/7;
        for (var col=0;col<count;col++)
        {
            var start = gridStart.AddDays(col*7); var end = start.AddDays(6);
            if (start < first) start = first; if (end > last) end = last;
            if (period == StatisticsPeriod.Month) { var anchor = gridStart.AddDays(col*7+3); if(anchor<first)anchor=first;if(anchor>last)anchor=last; start=new(year,anchor.Month,1);end=start.AddMonths(1).AddDays(-1); }
            if(period==StatisticsPeriod.Cumulative) start=first;
            var subset=snapshot.Days.Where(p=>p.Key>=start&&p.Key<=end&&p.Key.Year==year).Select(p=>p.Value).ToArray();
            var future = gridStart.AddDays(col*7) > snapshot.Today;
            columns.Add(new(start,end,future?0:subset.Sum(d=>d.Seconds(filter)),subset.Sum(d=>d.FirstLearnSeconds),subset.Sum(d=>d.ReviewSeconds),subset.Sum(d=>d.SpellingSeconds),subset.Sum(d=>d.NewWords),future));
        }
        return columns;
    }
    public static bool IsValid(StudyActivity a) => !string.IsNullOrWhiteSpace(a.ActivityId) && !string.IsNullOrWhiteSpace(a.SessionId) && Enum.IsDefined(a.Kind) && double.IsFinite(a.Seconds) && a.Seconds>0 && a.EndUtc>a.StartUtc && a.Seconds <= (a.EndUtc-a.StartUtc).TotalSeconds+0.1;
    public static DateTime Utc(DateTime date) => date.Kind==DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date,DateTimeKind.Utc);
    public static DateOnly LocalDate(DateTime date,TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(date),zone));
}
