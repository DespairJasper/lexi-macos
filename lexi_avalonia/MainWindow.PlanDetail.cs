using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi;

public partial class MainWindow
{
    private Grid? _dailyPlanPage;
    private string? _dailyPlanId;

    private void OpenDailyPlanDetail(DailyStudyPlan plan)
    {
        if (!FocusCanNavigate) return;
        _dailyPlanId = plan.Id; RenderDailyPlanDetail(); ShowPage("dailyplan");
    }

    private IReadOnlyList<DailyStudyPlanWord> DailyPlanPracticeWords(DailyStudyPlan plan)
    {
        var batch = DailyStudyPlanRules.GetTodayBatch(plan, PlanToday);
        return batch.Count > 0 ? batch : plan.Status == DailyStudyPlanStatus.Completed ? plan.Words : [];
    }

    private void RenderDailyPlanDetail()
    {
        if (_dailyPlanPage == null) return;
        _dailyPlanPage.Children.Clear();
        var plan = _studyPlans.FirstOrDefault(p => p.Id == _dailyPlanId);
        if (plan == null)
        {
            var empty = new StackPanel { Spacing = 18 };
            empty.Children.Add(PlanLabel(PlanText("请选择一个学习计划", "Choose a study plan"), 26));
            empty.Children.Add(PlanButton(PlanText("返回学习计划", "Back to plans"), "DailyPlanBackBtn", () => ShowPage("plans")));
            _dailyPlanPage.Children.Add(empty); return;
        }
        var page = new StackPanel { Spacing = 20 };
        page.Children.Add(PlanButton(PlanText("← 学习计划", "← Study plans"), "DailyPlanBackBtn", () => ShowPage("plans")));
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(PlanLabel(plan.Name, 28));
        heading.Children.Add(PlanLabel(UiText.Redisplay(plan.SourceLabel) + " · " + PlanText($"每日 {plan.DailyWordCount} 词", $"{plan.DailyWordCount} words/day") + " · " + PlanText("按自己的节奏，多次复习与拼写。", "Review and spell as often as you need."), 13, true));
        page.Children.Add(heading);
        var batch = DailyStudyPlanRules.GetTodayBatch(plan, PlanToday);
        var words = DailyPlanPracticeWords(plan);
        var done = DailyStudyPlanRules.TodayCompleted(plan, PlanToday);
        var remaining = DailyStudyPlanRules.GetTodayWords(plan, PlanToday).Count;
        var summary = new StackPanel { Spacing = 16 };
        var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 22 };
        metrics.Children.Add(PlanMetric($"{done} / {batch.Count}", PlanText("今日首次学习", "Today’s first pass")));
        var overall = PlanMetric($"{plan.CompletedWordIds.Count} / {plan.Words.Count}", PlanText("计划总进度", "Overall plan progress"), 28);
        var left = PlanMetric(DailyStudyPlanRules.EstimatedDaysRemaining(plan).ToString(), PlanText("预计剩余天数", "Estimated days left"), 28);
        Grid.SetColumn(overall, 1); metrics.Children.Add(overall); Grid.SetColumn(left, 2); metrics.Children.Add(left); summary.Children.Add(metrics);
        summary.Children.Add(new ProgressBar { Name = "DailyPlanFirstProgress", Minimum = 0, Maximum = Math.Max(1, batch.Count), Value = done, Height = 7 });
        summary.Children.Add(PlanLabel(PlanText("复习与拼写保留独立轮次，不重置今日首次学习任务。", "Review and spelling have separate rounds; today’s first pass stays intact."), 12, true));
        page.Children.Add(PlanPanel(summary));
        var entries = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        Control Entry(string symbol, string title, string description, Button button)
        {
            var body = new StackPanel { Spacing = 10 };
            var icon = PlanLabel(symbol, 26); icon.FontWeight = FontWeight.Medium; body.Children.Add(icon);
            body.Children.Add(PlanLabel(title, 17)); body.Children.Add(PlanLabel(description, 12, true));
            button.HorizontalAlignment = HorizontalAlignment.Stretch; body.Children.Add(button);
            return PlanPanel(body, true, new Thickness(18));
        }
        var initial = PlanButton(remaining > 0 ? PlanText("开始今日单词卡", "Start today’s cards") : PlanText("今日已完成", "Today complete"), "DailyPlanFirstBtn", () => StartPlanCardRound(plan), true);
        initial.IsEnabled = remaining > 0 && !_planReadFailed && plan.Status == DailyStudyPlanStatus.Active;
        entries.Children.Add(Entry("01", PlanText("今日单词卡", "Today’s cards"), PlanText("先看释义，再回忆。完成首次任务后暂停。", "Read, then recall. First learning stops when today’s tasks are complete."), initial));
        var review = Entry("02", PlanText("今日复习", "Review today"), PlanText("直接回忆今日已学词，选择范围与批量，可重复多轮。", "Recall learned words. Choose a scope and batch, and repeat anytime."), PlanButton(PlanText("进入复习", "Review words"), "DailyPlanReviewBtn", () => ShowPlanReview(plan, words)));
        Grid.SetColumn(review, 1); entries.Children.Add(review);
        var spelling = Entry("03", PlanText("拼写练习", "Spelling practice"), PlanText("选择淡写或默写，不限练习次数。", "Choose hinted spelling or dictation, as often as you need."), PlanButton(PlanText("练习拼写", "Practice spelling"), "DailyPlanSpellBtn", () => ShowPlanSpelling(plan, words)));
        Grid.SetColumn(spelling, 2); entries.Children.Add(spelling); page.Children.Add(entries);
        var list = new StackPanel { Spacing = 10 };
        list.Children.Add(PlanLabel(plan.Status == DailyStudyPlanStatus.Completed && batch.Count == 0 ? PlanText("已完成词表", "Completed word list") : PlanText("今日词表", "Today’s words"), 18));
        if (words.Count == 0) list.Children.Add(PlanLabel(PlanText("今日暂无词表。", "No words scheduled today."), 13, true));
        foreach (var word in words)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 5) };
            row.Children.Add(PlanLabel(word.Word, 14));
            var meaning = PlanLabel(word.Meaning, 12, true); Grid.SetColumn(meaning, 1); row.Children.Add(meaning);
            var state = PlanLabel(plan.CompletedWordIds.Contains(word.Id) ? PlanText("已学", "Learned") : PlanText("待学", "To learn"), 11, true); Grid.SetColumn(state, 2); row.Children.Add(state); list.Children.Add(row);
        }
        var wordList = new Expander { Header = PlanText("查看词表", "View word list") + $" · {words.Count}", Content = list, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(PlanPanel(wordList));
        var activity = new StackPanel { Spacing = 10 };
        activity.Children.Add(PlanLabel(PlanText("当日活动记录", "Today’s activity"), 18));
        var records = plan.Activities.Where(a => a.Date == PlanToday).OrderByDescending(a => a.StartedAtUtc).Take(30).ToList();
        foreach (var record in records)
        {
            var kind = record.Kind switch { "review" => PlanText("复习", "Review"), "spelling" => PlanText("拼写", "Spelling"), _ => PlanText("首次学习", "First learning") };
            var progress = $"{record.CompletedWordCount} / {record.WordCount}";
            activity.Children.Add(PlanLabel($"{record.StartedAtUtc.ToLocalTime():HH:mm}   {kind}   {progress}", 13, true));
        }
        if (records.Count == 0) activity.Children.Add(PlanLabel(PlanText("暂无当日活动记录。旧记录缺日期时显示未知。", "No activity recorded today. Missing legacy dates stay unknown."), 13, true));
        page.Children.Add(PlanPanel(activity));
        _dailyPlanPage.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }
}
