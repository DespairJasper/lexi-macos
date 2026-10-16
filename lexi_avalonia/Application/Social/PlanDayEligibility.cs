namespace Lexi.Social;

public static class PlanDayEligibilityRules
{
    public static PlanDayEligibility Evaluate(IReadOnlyList<SocialPlanTarget> targets, QualificationCause cause)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!Enum.IsDefined(cause)) throw new ArgumentOutOfRangeException(nameof(cause));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var target = 0; var completed = 0;
        DateTimeOffset? lastLearned = null;
        foreach (var plan in targets)
        {
            if (plan is null || string.IsNullOrWhiteSpace(plan.PlanId) || !ids.Add(plan.PlanId) ||
                plan.Target < 0 || plan.Completed < 0 || plan.Completed > plan.Target)
                throw new ArgumentException("Invalid or duplicate plan target.", nameof(targets));
            if (!plan.Enabled || plan.Target == 0) continue;
            target = checked(target + plan.Target);
            completed = checked(completed + plan.Completed);
            if (plan.LastLearnedAt is { } at && (lastLearned is null || at > lastLearned)) lastLearned = at;
        }
        var qualified = target > 0 && completed == target;
        return new(qualified, target, completed, qualified ? lastLearned : null, cause);
    }

    /// <summary>Read the original plan's actual today batch; do not change its selection or completion algorithm.</summary>
    /// <param name="excludedAtBinding">Already completed IDs on the binding day only. Caller persists these locally.</param>
    public static SocialPlanTarget FromPlan(DailyStudyPlan plan, DateOnly date,
        DateTimeOffset? lastLearnedAt, IReadOnlySet<string>? excludedAtBinding = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var enabled = plan.Status == DailyStudyPlanStatus.Active ||
            plan.Status == DailyStudyPlanStatus.Completed && plan.LastBatchCompletedDate == date;
        if (!enabled) return new(plan.Id, false, 0, 0, null);
        var batch = DailyStudyPlanRules.GetTodayBatch(plan, date);
        var target = 0; var done = 0;
        foreach (var word in batch)
        {
            if (excludedAtBinding?.Contains(word.Id) == true) continue;
            target++;
            if (plan.CompletedWordIds.Contains(word.Id)) done++;
        }
        return new(plan.Id, true, target, done, lastLearnedAt);
    }
}
