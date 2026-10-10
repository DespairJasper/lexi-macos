namespace Lexi;

public enum DailyStudyPlanSource { Archive, Ielts }
public enum DailyStudyPlanStatus { Active, Completed, Stopped }

public sealed class DailyStudyPlanActivity
{
    public string Id { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Kind { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public int WordCount { get; set; }
    public int CompletedWordCount { get; set; }
}

public sealed class DailyStudyPlanWord
{
    public string Id { get; set; } = "";
    public string Word { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string Definition { get; set; } = "";
    public string Example { get; set; } = "";
    public string AudioPath { get; set; } = "";
}

public sealed class DailyStudyPlan
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DailyStudyPlanSource Source { get; set; }
    public string SourceLabel { get; set; } = "";
    public int DailyWordCount { get; set; }
    public bool RandomOrder { get; set; }
    public int ShuffleSeed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateOnly? LastBatchCompletedDate { get; set; }
    public DailyStudyPlanStatus Status { get; set; }
    public List<DailyStudyPlanWord> Words { get; set; } = [];
    public HashSet<string> CompletedWordIds { get; set; } = [];
    public List<string> CurrentBatchWordIds { get; set; } = [];
    public bool? CurrentBatchRandomOrder { get; set; }
    public List<string> OriginalWordIds { get; set; } = [];
    public HashSet<string> ForgotWordIds { get; set; } = [];
    // Missing legacy dates remain missing. Only real new learning writes these fields.
    public DateOnly? CurrentBatchDate { get; set; }
    public HashSet<DateOnly> LearningDates { get; set; } = [];
    public List<DailyStudyPlanActivity> Activities { get; set; } = [];
}

public static class DailyStudyPlanRules
{
    public static DailyStudyPlan Create(string name, DailyStudyPlanSource source,
        string sourceLabel, IReadOnlyList<DailyStudyPlanWord> words,
        int dailyWordCount, bool randomOrder, int seed)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("计划名称不能为空。", nameof(name));
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if (words is null || words.Count == 0) throw new ArgumentException("计划词表不能为空。", nameof(words));
        if (dailyWordCount <= 0) throw new ArgumentOutOfRangeException(nameof(dailyWordCount));
        if (words.Any(w => w is null || string.IsNullOrWhiteSpace(w.Id) || string.IsNullOrWhiteSpace(w.Word)) ||
            words.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != words.Count)
            throw new ArgumentException("词条需要非空单词和唯一 ID。", nameof(words));

        var snapshot = words.Select(w => new DailyStudyPlanWord
        {
            Id = w.Id, Word = w.Word, Phonetic = w.Phonetic, Meaning = w.Meaning,
            Definition = w.Definition, Example = w.Example, AudioPath = w.AudioPath
        }).ToList();
        if (randomOrder)
        {
            var random = new Random(seed);
            for (var i = snapshot.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (snapshot[i], snapshot[j]) = (snapshot[j], snapshot[i]);
            }
        }
        return new DailyStudyPlan
        {
            Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), Source = source,
            SourceLabel = sourceLabel ?? "", DailyWordCount = dailyWordCount,
            RandomOrder = randomOrder, ShuffleSeed = seed, CreatedAt = DateTime.UtcNow,
            Status = DailyStudyPlanStatus.Active, Words = snapshot,
            OriginalWordIds = words.Select(w => w.Id).ToList()
        };
    }

    public static DailyStudyPlan Adjust(DailyStudyPlan plan, string name,
        int dailyWordCount, bool randomOrder, int seed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Status != DailyStudyPlanStatus.Active)
            throw new ArgumentException("仅进行中的计划可以调整。", nameof(plan));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("计划名称不能为空。", nameof(name));
        if (dailyWordCount <= 0) throw new ArgumentOutOfRangeException(nameof(dailyWordCount));
        var adjusted = new DailyStudyPlan
        {
            Id = plan.Id, Name = name.Trim(), Source = plan.Source, SourceLabel = plan.SourceLabel,
            DailyWordCount = plan.DailyWordCount, RandomOrder = plan.RandomOrder, ShuffleSeed = plan.ShuffleSeed,
            CreatedAt = plan.CreatedAt, LastBatchCompletedDate = plan.LastBatchCompletedDate, Status = plan.Status,
            Words = plan.Words.Select(w => new DailyStudyPlanWord
            {
                Id = w.Id, Word = w.Word, Phonetic = w.Phonetic, Meaning = w.Meaning,
                Definition = w.Definition, Example = w.Example, AudioPath = w.AudioPath
            }).ToList(),
            CompletedWordIds = new(plan.CompletedWordIds, StringComparer.Ordinal),
            ForgotWordIds = new(plan.ForgotWordIds, StringComparer.Ordinal),
            CurrentBatchWordIds = new(plan.CurrentBatchWordIds),
            CurrentBatchRandomOrder = plan.CurrentBatchRandomOrder,
            CurrentBatchDate = plan.CurrentBatchDate,
            LearningDates = new(plan.LearningDates),
            Activities = plan.Activities.Select(a => new DailyStudyPlanActivity { Id = a.Id, Date = a.Date,
                Kind = a.Kind, StartedAtUtc = a.StartedAtUtc, WordCount = a.WordCount,
                CompletedWordCount = a.CompletedWordCount }).ToList(),
            OriginalWordIds = plan.OriginalWordIds.Count > 0 ? new(plan.OriginalWordIds) : plan.Words.Select(w => w.Id).ToList()
        };
        // Recover legacy progress using the old quota, before changing it.
        RestoreLegacyBatch(adjusted);
        if (adjusted.CurrentBatchWordIds.Count > 0)
            adjusted.CurrentBatchRandomOrder ??= plan.RandomOrder;
        if (randomOrder != plan.RandomOrder)
        {
            var frozenIds = new HashSet<string>(adjusted.CompletedWordIds, StringComparer.Ordinal);
            frozenIds.UnionWith(adjusted.CurrentBatchWordIds);
            var wordsById = adjusted.Words.ToDictionary(w => w.Id, StringComparer.Ordinal);
            var future = adjusted.OriginalWordIds.Where(id => !frozenIds.Contains(id)).Select(id => wordsById[id]).ToList();
            if (randomOrder)
            {
                var random = new Random(seed);
                for (var i = future.Count - 1; i > 0; i--)
                {
                    var j = random.Next(i + 1);
                    (future[i], future[j]) = (future[j], future[i]);
                }
            }
            adjusted.Words = adjusted.Words.Where(w => frozenIds.Contains(w.Id)).Concat(future).ToList();
            adjusted.ShuffleSeed = seed;
        }
        adjusted.DailyWordCount = dailyWordCount;
        adjusted.RandomOrder = randomOrder;
        return adjusted;
    }

    public static bool Resume(DailyStudyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Status != DailyStudyPlanStatus.Stopped) return false;
        plan.Status = DailyStudyPlanStatus.Active;
        return true;
    }

    public static IReadOnlyList<DailyStudyPlanWord> GetTodayWords(DailyStudyPlan plan, DateOnly date)
    {
        if (plan.Status != DailyStudyPlanStatus.Active ||
            plan.CompletedWordIds.Count >= plan.Words.Count ||
            plan.LastBatchCompletedDate is { } completedDate && date <= completedDate)
            return [];
        var unfinishedBatch = UnfinishedBatch(plan);
        return unfinishedBatch.Count > 0 ? unfinishedBatch : plan.Words
            .Where(w => !plan.CompletedWordIds.Contains(w.Id)).Take(plan.DailyWordCount).ToList();
    }

    /// <summary>Today's stable first-learning denominator, including already completed task items.</summary>
    public static IReadOnlyList<DailyStudyPlanWord> GetTodayBatch(DailyStudyPlan plan, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CurrentBatchWordIds.Count > 0 &&
            (plan.CurrentBatchDate == date || plan.LastBatchCompletedDate == date ||
             plan.CurrentBatchWordIds.Any(id => !plan.CompletedWordIds.Contains(id))))
        {
            var ids = plan.CurrentBatchWordIds.ToHashSet(StringComparer.Ordinal);
            return plan.Words.Where(w => ids.Contains(w.Id)).ToList();
        }
        if (plan.Status != DailyStudyPlanStatus.Active ||
            plan.LastBatchCompletedDate is { } completed && date <= completed) return [];
        // Preserve a partly learned legacy batch, without assigning it a fictitious start date.
        if (plan.CompletedWordIds.Count % plan.DailyWordCount != 0)
            return plan.Words.Skip(plan.CompletedWordIds.Count / plan.DailyWordCount * plan.DailyWordCount)
                .Take(plan.DailyWordCount).ToList();
        return GetTodayWords(plan, date);
    }

    public static int TodayCompleted(DailyStudyPlan plan, DateOnly date) =>
        GetTodayBatch(plan, date).Count(w => plan.CompletedWordIds.Contains(w.Id));

    private static IReadOnlyList<DailyStudyPlanWord> UnfinishedBatch(DailyStudyPlan plan)
    {
        if (plan.CurrentBatchWordIds.Count > 0)
        {
            var words = plan.Words.ToDictionary(w => w.Id, StringComparer.Ordinal);
            return plan.CurrentBatchWordIds.Where(id => !plan.CompletedWordIds.Contains(id)).Select(id => words[id]).ToList();
        }
        // Older files identify a partly completed batch through the old quota.
        if (plan.CompletedWordIds.Count % plan.DailyWordCount == 0) return [];
        var start = plan.CompletedWordIds.Count / plan.DailyWordCount * plan.DailyWordCount;
        return plan.Words.Skip(start).Take(plan.DailyWordCount).Where(w => !plan.CompletedWordIds.Contains(w.Id)).ToList();
    }

    internal static void RestoreLegacyBatch(DailyStudyPlan plan)
    {
        if (plan.CurrentBatchWordIds.Count > 0 || plan.CompletedWordIds.Count == 0) return;
        var partial = plan.CompletedWordIds.Count % plan.DailyWordCount != 0 && plan.Status != DailyStudyPlanStatus.Completed;
        var start = (partial ? plan.CompletedWordIds.Count : plan.CompletedWordIds.Count - 1) / plan.DailyWordCount * plan.DailyWordCount;
        plan.CurrentBatchWordIds = plan.Words.Skip(start).Take(plan.DailyWordCount).Select(w => w.Id).ToList();
        plan.CurrentBatchRandomOrder = plan.RandomOrder;
    }

    public static IReadOnlyList<(string PlanName, string Word)> FindOverlaps(
        IEnumerable<DailyStudyPlan> plans, DailyStudyPlan draft)
    {
        var draftWordsById = draft.Words.ToDictionary(w => w.Id, StringComparer.Ordinal);
        var draftWordsByText = draft.Words.GroupBy(w => w.Word.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return plans.Where(p => p.Status == DailyStudyPlanStatus.Active &&
                p.Source == draft.Source && p.Id != draft.Id)
            .SelectMany(p => p.Words.Select(w =>
                draftWordsById.GetValueOrDefault(w.Id) ?? draftWordsByText.GetValueOrDefault(w.Word.Trim()))
                .Where(w => w is not null)
                .Select(w => (PlanName: p.Name, Word: w!.Word)))
            .ToList();
    }

    public static bool CompleteWord(DailyStudyPlan plan, string wordId, DateOnly date)
    {
        if (plan.Status != DailyStudyPlanStatus.Active || string.IsNullOrWhiteSpace(wordId) ||
            !GetTodayWords(plan, date).Any(w => w.Id == wordId)) return false;
        RestoreLegacyBatch(plan);
        if (UnfinishedBatch(plan).Count == 0)
        {
            plan.CurrentBatchWordIds = GetTodayWords(plan, date).Select(w => w.Id).ToList();
            plan.CurrentBatchRandomOrder = plan.RandomOrder;
        }
        else plan.CurrentBatchRandomOrder ??= plan.RandomOrder;
        plan.CompletedWordIds.Add(wordId);
        plan.CurrentBatchDate = date;
        plan.LearningDates.Add(date);
        if (plan.CompletedWordIds.Count == plan.Words.Count)
            plan.Status = DailyStudyPlanStatus.Completed;
        if (plan.Status == DailyStudyPlanStatus.Completed ||
            plan.CurrentBatchWordIds.All(plan.CompletedWordIds.Contains))
            plan.LastBatchCompletedDate = date;
        return true;
    }

    public static void Stop(DailyStudyPlan plan)
    {
        if (plan.Status == DailyStudyPlanStatus.Active) plan.Status = DailyStudyPlanStatus.Stopped;
    }

    public static int EstimatedDaysRemaining(DailyStudyPlan plan)
    {
        if (plan.Status != DailyStudyPlanStatus.Active) return 0;
        var remaining = plan.Words.Count - plan.CompletedWordIds.Count;
        var batchRemaining = UnfinishedBatch(plan).Count;
        return (batchRemaining > 0 ? 1 : 0) +
            (remaining - batchRemaining + plan.DailyWordCount - 1) / plan.DailyWordCount;
    }
}
