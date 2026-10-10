using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Lexi;

public partial class MainWindow
{
    private int _planFilter;
    private static string PlanText(string zh, string en) => UiText.Language == "en" ? en : zh;
    private static DateOnly PlanToday => DateOnly.FromDateTime(DateTime.Now);

    private TextBlock PlanLabel(string text, double size = 13, bool muted = false)
    {
        var label = ContentText(text, size);
        if (muted) label.Classes.Add("muted");
        return label;
    }

    private Button PlanButton(string text, string name, Action action, bool primary = false)
    {
        var button = new Button { Name = name, Classes = { primary ? "primary" : "secondary" }, Padding = new Thickness(12, 8), FontSize = 13 };
        button.Content = text; button.Click += (_, _) => action();
        return button;
    }

    private Border PlanPanel(Control child, bool tint = false, Thickness? padding = null)
    {
        var card = new Border { Child = child, CornerRadius = new CornerRadius(16),
            Padding = padding ?? new Thickness(26), BorderThickness = new Thickness(1) };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension(tint ? "TintBrush" : "CardBrush"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush"));
        return card;
    }

    private Control PlanMetric(string value, string label, double size = 34)
    {
        var box = new StackPanel { Spacing = 5 };
        var number = PlanLabel(value, size); number.FontWeight = FontWeight.Medium;
        number.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("PrimaryGreen"));
        box.Children.Add(number); box.Children.Add(PlanLabel(label, 12, true));
        return box;
    }

    private string PlanDays(DailyStudyPlan plan)
    {
        var recorded = plan.LearningDates.Count;
        var knownWords = plan.Activities.Where(a => a.Kind == "firstlearn").Sum(a => a.CompletedWordCount);
        if (plan.CompletedWordIds.Count > knownWords)
            return recorded == 0 ? PlanText("未知（旧记录缺日期）", "Unknown (legacy dates missing)")
                : PlanText($"已记录 {recorded} 天；旧日期未知", $"{recorded} recorded days; legacy dates unknown");
        return PlanText($"{recorded} 天", $"{recorded} days");
    }

    private string PlanWordIdentity(DailyStudyPlan plan, DailyStudyPlanWord word)
    {
        if (plan.Source == DailyStudyPlanSource.Ielts) return WordKey.Ielts(word.Id).Key;
        var archive = _allWords.FirstOrDefault(w => w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == word.Id);
        return archive != null && !string.IsNullOrWhiteSpace(archive.Archive.Uuid)
            ? WordKeyResolver.FromArchive(archive).Key : "archive-missing:" + word.Id;
    }

    private void RenderPlanDashboard()
    {
        var feedback = _planFeedback.Text ?? "";
        _studyPlanPage!.Children.Clear();
        var page = new StackPanel { Spacing = 22 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
        var title = new StackPanel { Spacing = 5 };
        title.Children.Add(PlanLabel(PlanText("学习计划", "Study plans"), 28));
        _planOverview = PlanLabel(PlanText("每日有序推进，按自己的节奏复习与巩固。", "Build a daily rhythm, with review and practice at your own pace."), 13, true);
        title.Children.Add(_planOverview); heading.Children.Add(title);
        var create = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
        var archive = PlanButton(PlanText("创建词汇档案计划", "Create archive plan"), "ArchivePlanCreateBtn", () => OpenStudyPlanDraft(DailyStudyPlanSource.Archive), true);
        var ielts = PlanButton(PlanText("创建 IELTS 计划", "Create IELTS plan"), "IeltsPlanCreateBtn", () => OpenStudyPlanDraft(DailyStudyPlanSource.Ielts));
        archive.IsEnabled = ielts.IsEnabled = !_planReadFailed;
        archive.Margin = new Thickness(0, 0, 8, 6); create.Children.Add(archive); create.Children.Add(ielts);
        if (Bounds.Width < 1120) { heading.RowDefinitions = new RowDefinitions("Auto,Auto"); Grid.SetRow(create, 1); create.Margin = new Thickness(0, 14, 0, 0); }
        else Grid.SetColumn(create, 1);
        heading.Children.Add(create); page.Children.Add(heading);
        _planFeedback = PlanLabel(UiText.Redisplay(feedback), 12, true); _planFeedback.Name = "PlanFeedback";
        _planFeedback.IsVisible = !string.IsNullOrWhiteSpace(feedback); page.Children.Add(_planFeedback);
        var today = _studyPlans.Where(p => p.Status == DailyStudyPlanStatus.Active || p.LastBatchCompletedDate == PlanToday && p.Status == DailyStudyPlanStatus.Completed).ToList();
        var total = today.Sum(p => DailyStudyPlanRules.GetTodayBatch(p, PlanToday).Count);
        var completed = today.Sum(p => DailyStudyPlanRules.TodayCompleted(p, PlanToday));
        var top = new StackPanel { Spacing = 19 };
        var topTitle = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        topTitle.Children.Add(PlanLabel(PlanText("今日待学习", "Today’s learning"), 18));
        var date = PlanLabel(PlanToday.ToString(UiText.Language == "en" ? "ddd, dd MMM" : "M 月 d 日", UiText.Language == "en" ? System.Globalization.CultureInfo.InvariantCulture : System.Globalization.CultureInfo.GetCultureInfo("zh-CN")) + " · " + PlanText($"{today.Count} 个计划", $"{today.Count} plans"), 12, true);
        Grid.SetColumn(date, 1); topTitle.Children.Add(date); top.Children.Add(topTitle);
        var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 20 };
        var doneMetric = PlanMetric($"{completed} / {total}", PlanText("已完成 / 今日总词数", "Completed / scheduled today"), 42);
        var totalMetric = PlanMetric(total.ToString(), PlanText("今日任务总词数", "Words scheduled today"), 26);
        var leftMetric = PlanMetric(Math.Max(0, total - completed).ToString(), PlanText("剩余待学", "Words remaining"), 26);
        metrics.Children.Add(doneMetric); Grid.SetColumn(totalMetric, 1); metrics.Children.Add(totalMetric); Grid.SetColumn(leftMetric, 2); metrics.Children.Add(leftMetric); top.Children.Add(metrics);
        top.Children.Add(new ProgressBar { Name = "PlanTodayProgress", Minimum = 0, Maximum = Math.Max(1, total), Value = completed, Height = 7 });
        var pct = total == 0 ? 0 : 100 * completed / total;
        top.Children.Add(PlanLabel(PlanText($"已完成 {completed} 词 · {pct}% · 按今日首次学习任务计数", $"Completed {completed} words · {pct}% · Today’s first-learning tasks"), 12, true));
        var filter = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var item in new[] { (0, PlanText("全部", "All")), (1, "IELTS"), (2, PlanText("词汇档案", "Archive")) })
        {
            var index = item.Item1;
            var chip = PlanButton(item.Item2, "PlanFilter" + index, () => { _planFilter = index; RenderStudyPlanLists(); });
            chip.Margin = new Thickness(0, 0, 6, 0); chip.Padding = new Thickness(12, 5);
            if (_planFilter == index) chip.Bind(Button.BackgroundProperty, new DynamicResourceExtension("TintBrush"));
            filter.Children.Add(chip);
        }
        top.Children.Add(filter);
        _ieltsPlanRows = new StackPanel { Name = "IeltsPlanRows", Spacing = 0 };
        _archivePlanRows = new StackPanel { Name = "ArchivePlanRows", Spacing = 0 };
        var filtered = today.Where(p => _planFilter == 0 || (_planFilter == 1 ? p.Source == DailyStudyPlanSource.Ielts : p.Source == DailyStudyPlanSource.Archive)).OrderByDescending(p => p.CreatedAt).ToList();
        foreach (var plan in filtered) (plan.Source == DailyStudyPlanSource.Ielts ? _ieltsPlanRows : _archivePlanRows).Children.Add(BuildStudyPlanCard(plan));
        top.Children.Add(_ieltsPlanRows); top.Children.Add(_archivePlanRows);
        if (filtered.Count == 0) top.Children.Add(PlanLabel(PlanText("暂无活动计划。创建一个计划，开始每日单词卡学习。", "No active plans. Create a plan to start daily cards."), 13, true));
        var taskWords = today.SelectMany(p => DailyStudyPlanRules.GetTodayBatch(p, PlanToday).Select(w => w.Word.Trim().ToUpperInvariant())).ToList();
        var overlap = taskWords.Count - taskWords.Distinct(StringComparer.Ordinal).Count();
        if (overlap > 0) top.Children.Add(PlanLabel(PlanText($"今日包含 {overlap} 个跨计划重叠任务项；今日进度按任务项计数。", $"Today includes {overlap} overlapping task items; daily progress counts each plan’s tasks."), 11, true));
        page.Children.Add(PlanPanel(top));
        var past = _studyPlans.Where(p => p.Status == DailyStudyPlanStatus.Completed).OrderByDescending(p => p.CreatedAt).ToList();
        var history = new StackPanel { Spacing = 18 };
        history.Children.Add(PlanLabel(PlanText("已完成计划", "Completed plans") + $" · {past.Count}", 18));
        var pastMetrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 28 };
        var dates = past.SelectMany(p => p.LearningDates).Distinct().Count();
        var unknown = past.Any(p => p.CompletedWordIds.Count > p.Activities.Where(a => a.Kind == "firstlearn").Sum(a => a.CompletedWordCount));
        pastMetrics.Children.Add(PlanMetric(unknown && dates == 0 ? PlanText("未知", "Unknown") : dates.ToString(), PlanText("实际学习天数 · 去重日期", "Study days · unique dates")));
        var learned = past.SelectMany(p => p.Words.Where(w => p.CompletedWordIds.Contains(w.Id)).Select(w => PlanWordIdentity(p, w))).Distinct().Count();
        var learnedMetric = PlanMetric(learned.ToString(), PlanText("已学词汇 · 去重", "Unique words learned")); Grid.SetColumn(learnedMetric, 1); pastMetrics.Children.Add(learnedMetric); history.Children.Add(pastMetrics);
        if (unknown) history.Children.Add(PlanLabel(PlanText("旧记录缺少精确日期，学习天数仅含真实新增日期。", "Legacy dates are missing; recorded days include only actual new learning dates."), 11, true));
        foreach (var plan in past) history.Children.Add(BuildStudyPlanCard(plan));
        if (past.Count == 0) history.Children.Add(PlanLabel(PlanText("完成计划后，词表仍可用于自主复习与拼写。", "Completed word lists remain available for review and spelling."), 13, true));
        var stopped = _studyPlans.Where(p => p.Status == DailyStudyPlanStatus.Stopped).OrderByDescending(p => p.CreatedAt).ToList();
        var stoppedRows = new StackPanel { Spacing = 2 };
        foreach (var plan in stopped) stoppedRows.Children.Add(BuildStudyPlanCard(plan));
        history.Children.Add(new Expander { Name = "StoppedPlansExpander", Header = PlanText("已停止的计划", "Stopped plans") + $" · {stopped.Count}", Content = stoppedRows, HorizontalAlignment = HorizontalAlignment.Stretch });
        page.Children.Add(PlanPanel(history));
        _studyPlanPage.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }

    private Border BuildStudyPlanCard(DailyStudyPlan plan)
    {
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,140"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 20, RowSpacing = 12 };
        var info = new StackPanel { Spacing = 5 };
        var title = PlanButton(plan.Name, "PlanDetailBtn", () => OpenDailyPlanDetail(plan));
        title.Classes.Add("ghost"); title.Padding = new Thickness(0); title.HorizontalAlignment = HorizontalAlignment.Left; title.FontWeight = FontWeight.Medium; title.FontSize = 15;
        info.Children.Add(title);
        info.Children.Add(PlanLabel(UiText.Redisplay(plan.SourceLabel) + " · " + PlanText($"每日 {plan.DailyWordCount} 词", $"{plan.DailyWordCount} words/day") + " · " + (plan.RandomOrder ? PlanText("随机顺序", "Random order") : PlanText("顺序学习", "Sequential order")), 11, true));
        var batch = DailyStudyPlanRules.GetTodayBatch(plan, PlanToday); var done = DailyStudyPlanRules.TodayCompleted(plan, PlanToday);
        info.Children.Add(PlanLabel(plan.Status == DailyStudyPlanStatus.Completed ? PlanText("已完成", "Completed") + " · " + PlanDays(plan) : plan.Status == DailyStudyPlanStatus.Stopped ? PlanText("已停止", "Stopped") : PlanText($"今日 {done}/{batch.Count}", $"Today {done}/{batch.Count}"), 11, true));
        body.Children.Add(info);
        var progress = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        progress.Children.Add(PlanLabel($"{plan.CompletedWordIds.Count} / {plan.Words.Count}", 15));
        progress.Children.Add(new ProgressBar { Minimum = 0, Maximum = plan.Words.Count, Value = plan.CompletedWordIds.Count, Height = 4 });
        progress.Children.Add(PlanLabel(PlanText($"预计剩余 {DailyStudyPlanRules.EstimatedDaysRemaining(plan)} 天", $"{DailyStudyPlanRules.EstimatedDaysRemaining(plan)} days remaining"), 11, true));
        Grid.SetColumn(progress, 1); body.Children.Add(progress);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        void Add(Button button) { button.Padding = new Thickness(10, 6); button.FontSize = 11; button.Margin = new Thickness(0, 0, 7, 3); actions.Children.Add(button); }
        if (plan.Status == DailyStudyPlanStatus.Active)
        {
            var remaining = DailyStudyPlanRules.GetTodayWords(plan, PlanToday).Count;
            var start = PlanButton(remaining > 0 ? PlanText("今日单词卡", "Today’s cards") : PlanText("今日已完成", "Today complete"), "PlanStartBtn", () => StartStudyPlan(plan), true);
            start.IsEnabled = remaining > 0 && !_planReadFailed; Add(start);
            Add(PlanButton(PlanText("复习 / 拼写", "Review / spell"), "PlanPracticeBtn", () => OpenDailyPlanDetail(plan)));
            Add(PlanButton(PlanText("调整", "Adjust"), "PlanAdjustBtn", () => OpenStudyPlanAdjustment(plan)));
            Add(PlanButton(PlanText("停止", "Stop"), "PlanStopBtn", () => StopStudyPlan(plan)));
        }
        else if (plan.Status == DailyStudyPlanStatus.Stopped)
        {
            Add(PlanButton(PlanText("恢复计划", "Resume plan"), "PlanResumeBtn", () => ResumeStudyPlan(plan), true));
            Add(PlanButton(PlanText("删除计划", "Delete plan"), "PlanDeleteBtn", () => DeleteStudyPlan(plan)));
        }
        else Add(PlanButton(PlanText("复习 / 拼写", "Review / spell"), "CompletedPlanPracticeBtn", () => OpenDailyPlanDetail(plan)));
        Grid.SetRow(actions, 1); Grid.SetColumnSpan(actions, 2); body.Children.Add(actions);
        var row = new Border { Child = body, Padding = new Thickness(0, 18), BorderThickness = new Thickness(0, 1, 0, 0) };
        row.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush")); return row;
    }
}
