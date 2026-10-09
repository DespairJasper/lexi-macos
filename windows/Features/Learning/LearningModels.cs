using System;
using System.Collections.Generic;
using System.Linq;

namespace Lexi.Features.Learning;

/// <summary>
/// 计划选词的范围状态与辅助逻辑，保持与领域模型和存储契约严格解耦。
/// </summary>
public sealed class PlanCreationState
{
    public DailyStudyPlanSource Source { get; }
    public HashSet<string> SelectedIds { get; } = new(StringComparer.Ordinal);
    public string PlanName { get; set; } = "";
    public int DailyWordCount { get; set; } = 20;
    public bool RandomOrder { get; set; }
    public string SearchText { get; set; } = "";
    public bool OnlyShowSelected { get; set; }
    public int PageIndex { get; set; }
    public const int PageSize = 10;

    public PlanCreationState(DailyStudyPlanSource source, string defaultName)
    {
        Source = source;
        PlanName = defaultName;
    }

    /// <summary>
    /// 计算预计天数与尾日剩余词数说明。
    /// </summary>
    public static (int Days, string Description) CalculateEstimate(int totalWords, int dailyQuota)
    {
        if (totalWords <= 0) return (0, "请至少选择 1 个词条");
        if (dailyQuota <= 0) return (0, "每日词数需大于 0");

        var days = (totalWords + dailyQuota - 1) / dailyQuota;
        if (dailyQuota >= totalWords)
        {
            return (1, $"每日目标超过或等于选词总数，预计 1 天完成（首日学习全部 {totalWords} 词）。");
        }

        var remainder = totalWords % dailyQuota;
        if (remainder == 0)
        {
            return (days, $"预计 {days} 天完成（每天学习 {dailyQuota} 词）。");
        }

        return (days, $"预计 {days} 天完成（前 {days - 1} 天每天 {dailyQuota} 词，最后 1 天学习剩余 {remainder} 词）。");
    }
}

/// <summary>
/// 计划冲突明细展示模型。
/// </summary>
public sealed record PlanConflictInfo(string PlanName, IReadOnlyList<string> ConflictingWords);
