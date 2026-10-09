using System;

namespace Lexi.Core;

public static class ReviewSchedule
{
    public static readonly int[] DefaultStageOffsets = [1, 2, 4, 7, 15];
    public const int DefaultMaxStage = 5;

    public static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd");

    public static DateTime CalculateInitialReviewDate(DateTime startDate, int stage = 0, int[]? stageOffsets = null)
    {
        var offsets = stageOffsets ?? DefaultStageOffsets;
        if (stage < 0 || stage >= offsets.Length) stage = 0;
        return startDate.Date.AddDays(offsets[stage]);
    }

    public static (DateTime NextReviewDate, DateTime LearningStartDate, int NewStage, bool IsMastered) CalculateNextReview(
        DateTime today,
        DateTime learningStartDate,
        DateTime? currentScheduledDate,
        int currentStage,
        int maxStage = DefaultMaxStage,
        int[]? stageOffsets = null)
    {
        var offsets = stageOffsets ?? DefaultStageOffsets;
        var newStage = currentStage + 1;
        var todayDate = today.Date;
        var startDate = learningStartDate.Date;

        if (newStage >= maxStage)
        {
            return (todayDate, startDate, maxStage, true);
        }

        var isLate = currentScheduledDate.HasValue && todayDate > currentScheduledDate.Value.Date;

        if (isLate)
        {
            var deltaDays = offsets[newStage] - offsets[currentStage];
            var nextDate = todayDate.AddDays(deltaDays);
            if (nextDate <= todayDate)
            {
                nextDate = todayDate.AddDays(1);
            }
            var newStart = nextDate.AddDays(-offsets[newStage]);
            return (nextDate, newStart, newStage, false);
        }
        else
        {
            var nextDate = startDate.AddDays(offsets[newStage]);
            if (nextDate <= todayDate)
            {
                nextDate = todayDate.AddDays(1);
            }
            return (nextDate, startDate, newStage, false);
        }
    }

    public static DateTime CalculateUnfamiliarReviewDate(DateTime today)
    {
        return today.Date.AddDays(1);
    }
}
