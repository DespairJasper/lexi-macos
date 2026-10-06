using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi;

public partial class MainWindow
{
    private DailyStudyPlanStore _studyPlanStore = null!;
    private List<DailyStudyPlan> _studyPlans = [];
    private Grid? _studyPlanPage;
    private StackPanel _archivePlanRows = null!, _ieltsPlanRows = null!;
    private TextBlock _planOverview = null!, _planFeedback = null!;
    private DailyStudyPlan? _activeStudyPlan, _draftStudyPlan, _editingStudyPlan;
    private DailyStudyPlanSource _planDraftSource;
    private List<DailyStudyPlanWord> _planCandidates = [];
    private readonly HashSet<string> _planSelectedIds = new(StringComparer.Ordinal);
    private string _planSourceLabel = "";
    private bool _planDialogConfiguring, _planReadFailed;
    private int _planChoicePage;
    private StackPanel _planChoicePaging = null!;
    private TextBlock _planChoicePageLabel = null!;
    private Button _planChoicePrevious = null!, _planChoiceNext = null!;

    private void ConfigureStudyPlans()
    {
        var path = Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "daily-study-plans.json");
        _studyPlanStore = new DailyStudyPlanStore(path);
        try { _studyPlans = _studyPlanStore.Load(); }
        catch (Exception ex) { _planReadFailed = true; SetStatus(T("学习计划读取失败：") + ex.Message); }
        _studyPlanPage = new Grid { Name = "PageStudyPlan", IsVisible = false,
            Margin = new Thickness(28, 24), RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 20 };
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(LearningLabel("学习计划", 26));
        _planOverview = ContentText("", 13); _planOverview.Classes.Add("muted"); header.Children.Add(_planOverview);
        var create = new WrapPanel();
        var archive = LearningButton("创建词汇档案计划", "ArchivePlanCreateBtn", true);
        var ielts = LearningButton("创建 IELTS 计划", "IeltsPlanCreateBtn");
        archive.IsEnabled = ielts.IsEnabled = !_planReadFailed;
        archive.Margin = new Thickness(0, 0, 10, 6); ielts.Margin = new Thickness(0, 0, 0, 6);
        archive.Click += (_, _) => OpenStudyPlanDraft(DailyStudyPlanSource.Archive);
        ielts.Click += (_, _) => OpenStudyPlanDraft(DailyStudyPlanSource.Ielts);
        create.Children.Add(archive); create.Children.Add(ielts); header.Children.Add(create);
        _planFeedback = ContentText("", 12); _planFeedback.Name = "PlanFeedback"; _planFeedback.Classes.Add("muted"); header.Children.Add(_planFeedback);
        _studyPlanPage.Children.Add(header);
        var groups = new StackPanel { Spacing = 24 };
        foreach (var source in new[] { DailyStudyPlanSource.Ielts, DailyStudyPlanSource.Archive })
        {
            var group = new StackPanel { Spacing = 12 };
            group.Children.Add(LearningLabel(source == DailyStudyPlanSource.Ielts ? "IELTS 专题" : "词汇档案", 18));
            var rows = new StackPanel { Name = source == DailyStudyPlanSource.Ielts ? "IeltsPlanRows" : "ArchivePlanRows", Spacing = 12 };
            if (source == DailyStudyPlanSource.Ielts) _ieltsPlanRows = rows; else _archivePlanRows = rows;
            group.Children.Add(rows); groups.Children.Add(group);
        }
        var scroll = new ScrollViewer { Content = groups, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); _studyPlanPage.Children.Add(scroll);
        PlanCancelBtn.Click += (_, _) => CloseStudyPlanDialog();
        PlanCancelOverlapBtn.Click += (_, _) => CloseStudyPlanDialog();
        PlanCreateConfirmBtn.Click += (_, _) => TryCreateStudyPlan();
        PlanContinueOverlapBtn.Click += (_, _) => CommitStudyPlan();
        PlanBookInput.SelectionChanged += (_, _) => { if (!_planDialogConfiguring) PopulatePlanUnits(); };
        PlanUnitInput.SelectionChanged += (_, _) => { if (!_planDialogConfiguring) SetPlanCandidates(); };
        PlanWordSearch.TextChanged += (_, _) => { _planChoicePage = 0; RenderPlanChoices(); };
        PlanSelectAllBtn.Click += (_, _) => { foreach (var word in FilterPlanChoices()) _planSelectedIds.Add(word.Id); ResetPlanOverlap(); RenderPlanChoices(); };
        PlanClearSelectionBtn.Click += (_, _) => { _planSelectedIds.Clear(); ResetPlanOverlap(); RenderPlanChoices(); };
        PlanDailyCountInput.ValueChanged += (_, _) => { ResetPlanOverlap(); UpdatePlanEstimate(); };
        PlanRandomInput.IsCheckedChanged += (_, _) => ResetPlanOverlap();
        PlanNameInput.TextChanged += (_, _) => ResetPlanOverlap();
        var paging = _planChoicePaging = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _planChoicePrevious = LearningButton("上一页", "PlanChoicePrevious");
        _planChoiceNext = LearningButton("下一页", "PlanChoiceNext");
        _planChoicePageLabel = ContentText("", 12); _planChoicePageLabel.VerticalAlignment = VerticalAlignment.Center;
        _planChoicePrevious.Click += (_, _) => { _planChoicePage--; RenderPlanChoices(); };
        _planChoiceNext.Click += (_, _) => { _planChoicePage++; RenderPlanChoices(); };
        paging.Children.Add(_planChoicePrevious); paging.Children.Add(_planChoicePageLabel); paging.Children.Add(_planChoiceNext);
        ((StackPanel)PlanSourceSummary.Parent!).Children.Insert(((StackPanel)PlanSourceSummary.Parent!).Children.IndexOf(PlanSourceSummary), paging);
        SizeChanged += (_, _) => UpdatePlanDialogHeight();
        UpdatePlanDialogHeight();
        ConfigurePlanActions(); ConfigurePlanLearning(); RenderStudyPlanLists();
    }

    private void OpenStudyPlanDraft(DailyStudyPlanSource source)
    {
        if (_planReadFailed) { SetStatus(T("学习计划读取失败，已暂停写入以保护原文件。")); return; }
        _planDialogConfiguring = true;
        _editingStudyPlan = null;
        PlanWordScope.IsVisible = _planChoicePaging.IsVisible = true;
        PlanDialogTitle.Text = T("创建每日学习计划"); PlanCreateConfirmBtn.Content = T("创建计划");
        _planDraftSource = source; _draftStudyPlan = null; _planSelectedIds.Clear();
        PlanWordSearch.Text = ""; PlanNameInput.Text = ""; PlanDailyCountInput.Value = 20; PlanRandomInput.IsChecked = false;
        PlanIeltsSelectors.IsVisible = source == DailyStudyPlanSource.Ielts;
        PlanBookInput.ItemsSource = new[] { T("词汇真经"), T("听力 179"), T("阅读同义替换"), T("英美拼写规范") };
        PlanBookInput.SelectedIndex = Math.Clamp(_ieltsKind.SelectedIndex, 0, 3);
        _planDialogConfiguring = false;
        if (source == DailyStudyPlanSource.Ielts) PopulatePlanUnits();
        else SetPlanCandidates();
        ResetPlanOverlap(); UpdatePlanDialogHeight(); PlanDialogCard.Background = PlanModalBrush(); PlanDialogOverlay.IsVisible = true; PlanNameInput.Focus();
    }

    private void PopulatePlanUnits()
    {
        var kind = PlanBookInput.SelectedIndex switch { 1 => "listening", 2 => "reading", 3 => "spelling", _ => "vocabulary" };
        var sections = _ieltsCatalog!.Sections.Where(s => s.Kind == kind).ToList();
        var whole = new LearningSection { Id = "plan-whole-book", Title = T("整本词书"),
            Entries = sections.SelectMany(s => s.Entries).DistinctBy(w => w.Id).ToList() };
        _planDialogConfiguring = true;
        PlanUnitInput.ItemsSource = new[] { whole }.Concat(sections).ToList();
        PlanUnitInput.SelectedItem = sections.FirstOrDefault(s => s.Id == (_ieltsSection.SelectedItem as LearningSection)?.Id) ?? whole;
        _planDialogConfiguring = false; SetPlanCandidates();
    }

    private void SetPlanCandidates()
    {
        _planSelectedIds.Clear(); _planChoicePage = 0;
        if (_planDraftSource == DailyStudyPlanSource.Archive)
        {
            _planSourceLabel = T("词汇档案");
            _planCandidates = _allWords.Select(w => new DailyStudyPlanWord { Id = w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Word = w.Word, Phonetic = w.Phonetic, Meaning = w.Translation, Definition = w.Definition, Example = w.Archive.SourceExcerpt ?? "" }).ToList();
            foreach (var word in _allWords.Where(w => w.Selected)) _planSelectedIds.Add(word.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            var section = PlanUnitInput.SelectedItem as LearningSection;
            _planSourceLabel = PlanBookInput.SelectedItem + " · " + section?.Title;
            _planCandidates = (section?.Entries ?? []).Select(w => new DailyStudyPlanWord { Id = w.Id, Word = w.Word,
                Phonetic = w.Phonetic, Meaning = w.Meaning, Definition = w.Extra, Example = w.Example, AudioPath = w.AudioPath }).ToList();
            foreach (var word in _planCandidates) _planSelectedIds.Add(word.Id);
        }
        ResetPlanOverlap(); RenderPlanChoices();
    }

    private List<DailyStudyPlanWord> FilterPlanChoices()
    {
        var query = PlanWordSearch.Text?.Trim() ?? "";
        return _planCandidates.Where(w => query.Length == 0 || (w.Word + " " + w.Meaning).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void RenderPlanChoices()
    {
        if (_planChoicePageLabel == null) return;
        var words = FilterPlanChoices(); var pages = Math.Max(1, (words.Count + 79) / 80);
        _planChoicePage = Math.Clamp(_planChoicePage, 0, pages - 1); PlanWordChoices.Children.Clear();
        foreach (var word in words.Skip(_planChoicePage * 80).Take(80))
        {
            var choice = new CheckBox { Name = "PlanWordChoice", IsChecked = _planSelectedIds.Contains(word.Id),
                Content = new TextBlock { Text = word.Word + "  ·  " + word.Meaning, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
            choice.IsCheckedChanged += (_, _) => { if (choice.IsChecked == true) _planSelectedIds.Add(word.Id); else _planSelectedIds.Remove(word.Id); ResetPlanOverlap(); UpdatePlanEstimate(); };
            PlanWordChoices.Children.Add(choice);
        }
        if (words.Count == 0) PlanWordChoices.Children.Add(LearningLabel("没有匹配词条。"));
        _planChoicePageLabel.Text = $"{_planChoicePage + 1} / {pages}";
        _planChoicePrevious.IsEnabled = _planChoicePage > 0; _planChoiceNext.IsEnabled = _planChoicePage + 1 < pages;
        UpdatePlanEstimate();
    }

    private void ResetPlanOverlap()
    {
        PlanDialogError.Text = ""; PlanOverlapActions.IsVisible = false; PlanCreateConfirmBtn.IsVisible = true; _draftStudyPlan = null;
    }

    private void UpdatePlanEstimate()
    {
        var count = _planSelectedIds.Count; var daily = Math.Max(1, (int)(PlanDailyCountInput.Value ?? 1));
        if (_editingStudyPlan is { } editing)
        {
            var preview = DailyStudyPlanRules.Adjust(editing, editing.Name, daily, editing.RandomOrder, editing.ShuffleSeed);
            PlanSourceSummary.Text = editing.SourceLabel + " · " + TF($"总进度 {editing.CompletedWordIds.Count}/{editing.Words.Count}")
                + "\n" + T("已学进度保留；词数和顺序调整从下一批生效。");
            PlanEstimate.Text = TF($"每日 {daily} 词 · 预计剩余 {DailyStudyPlanRules.EstimatedDaysRemaining(preview)} 天");
            return;
        }
        PlanSourceSummary.Text = TF($"{_planSourceLabel} · 已选 {count} 词");
        PlanEstimate.Text = TF($"每日 {daily} 词 · 预计 {((count + daily - 1) / daily)} 天完成");
    }

    private void TryCreateStudyPlan()
    {
        if (string.IsNullOrWhiteSpace(PlanNameInput.Text)) { PlanDialogError.Text = T("请输入计划名称。"); return; }
        if (_editingStudyPlan != null) { SaveStudyPlanAdjustment(); return; }
        var words = _planCandidates.Where(w => _planSelectedIds.Contains(w.Id)).ToList();
        if (words.Count == 0) { PlanDialogError.Text = T("请先选择计划单词。"); return; }
        var draft = DailyStudyPlanRules.Create(PlanNameInput.Text, _planDraftSource, _planSourceLabel, words,
            (int)(PlanDailyCountInput.Value ?? 1), PlanRandomInput.IsChecked == true, Random.Shared.Next());
        _draftStudyPlan = draft;
        var overlaps = DailyStudyPlanRules.FindOverlaps(_studyPlans, draft);
        if (overlaps.Count > 0)
        {
            PlanDialogError.Text = T("与活动计划重叠：") + " " + string.Join("；", overlaps.Take(8).Select(o => o.PlanName + " — " + o.Word))
                + (overlaps.Count > 8 ? TF($" 等 {overlaps.Count} 词") : "");
            PlanCreateConfirmBtn.IsVisible = false; PlanOverlapActions.IsVisible = true; return;
        }
        CommitStudyPlan();
    }

    private void CommitStudyPlan()
    {
        if (_draftStudyPlan == null || _planReadFailed) return;
        var pending = _studyPlans.Append(_draftStudyPlan).ToList();
        try { _studyPlanStore.Save(pending); _studyPlans = pending; }
        catch (Exception ex) { PlanDialogError.Text = T("学习计划保存失败：") + ex.Message; return; }
        _draftStudyPlan = null; PlanDialogOverlay.IsVisible = false;
        _planFeedback.Text = T("计划已创建。新的归档词不会加入既有计划。"); RenderStudyPlanLists();
    }

    private void RenderStudyPlanLists()
    {
        if (_archivePlanRows == null) return;
        _planOverview.Text = TF($"{_studyPlans.Count(p => p.Status == DailyStudyPlanStatus.Active)} 个活动计划 · 今日 { _studyPlans.Where(p => p.Status == DailyStudyPlanStatus.Active).Sum(p => DailyStudyPlanRules.GetTodayWords(p, DateOnly.FromDateTime(DateTime.Now)).Count)} 词待学");
        foreach (var source in new[] { DailyStudyPlanSource.Ielts, DailyStudyPlanSource.Archive })
        {
            var rows = source == DailyStudyPlanSource.Ielts ? _ieltsPlanRows : _archivePlanRows; rows.Children.Clear();
            var plans = _studyPlans.Where(p => p.Source == source).OrderByDescending(p => p.CreatedAt).ToList();
            foreach (var plan in plans.Where(p => p.Status == DailyStudyPlanStatus.Active)) rows.Children.Add(BuildStudyPlanCard(plan));
            if (plans.All(p => p.Status != DailyStudyPlanStatus.Active)) { var empty = LearningLabel("暂无活动计划。创建一个计划，开始每日单词卡学习。", 13); empty.Classes.Add("muted"); rows.Children.Add(empty); }
            var past = plans.Where(p => p.Status != DailyStudyPlanStatus.Active).ToList();
            if (past.Count > 0)
            {
                var history = new StackPanel { Spacing = 10 };
                foreach (var plan in past) history.Children.Add(BuildStudyPlanCard(plan));
                rows.Children.Add(new Expander { Header = T("已完成与已停止") + $" · {past.Count}", Content = history, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
        }
    }

    private Border BuildStudyPlanCard(DailyStudyPlan plan)
    {
        var body = new StackPanel { Spacing = 12 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        heading.Children.Add(new TextBlock { Text = plan.Name, FontSize = 18, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap });
        var status = ContentText(T(plan.Status == DailyStudyPlanStatus.Active ? "进行中" : plan.Status == DailyStudyPlanStatus.Completed ? "已完成" : "已停止"), 12); status.Classes.Add("muted");
        Grid.SetColumn(status, 1); heading.Children.Add(status); body.Children.Add(heading);
        var details = ContentText(plan.SourceLabel + " · " + TF($"每日 {plan.DailyWordCount} 词") + " · " + T(plan.RandomOrder ? "随机顺序" : "顺序学习"), 12); details.Classes.Add("muted"); body.Children.Add(details);
        body.Children.Add(new ProgressBar { Minimum = 0, Maximum = plan.Words.Count, Value = plan.CompletedWordIds.Count, Height = 5 });
        body.Children.Add(ContentText(TF($"总进度 {plan.CompletedWordIds.Count}/{plan.Words.Count} · 预计剩余 {DailyStudyPlanRules.EstimatedDaysRemaining(plan)} 天"), 13));
        if (plan.Status == DailyStudyPlanStatus.Active)
        {
            var today = DailyStudyPlanRules.GetTodayWords(plan, DateOnly.FromDateTime(DateTime.Now)).Count;
            var actions = new WrapPanel();
            var start = LearningButton(today > 0 ? "开始今日单词卡" : "今日已完成", "PlanStartBtn", true); start.IsEnabled = today > 0;
            start.Margin = new Thickness(0, 0, 10, 0); start.Click += (_, _) => StartStudyPlan(plan);
            var stop = LearningButton("停止计划", "PlanStopBtn"); stop.Click += (_, _) => StopStudyPlan(plan);
            var adjust = LearningButton("调整计划", "PlanAdjustBtn"); adjust.Margin = new Thickness(0, 0, 10, 0);
            adjust.Click += (_, _) => OpenStudyPlanAdjustment(plan);
            actions.Children.Add(start); actions.Children.Add(adjust); actions.Children.Add(stop); body.Children.Add(actions);
        }
        if (plan.Status == DailyStudyPlanStatus.Stopped)
        {
            var actions = new WrapPanel();
            var resume = LearningButton("恢复计划", "PlanResumeBtn", true); resume.Margin = new Thickness(0, 0, 10, 0);
            var delete = LearningButton("删除计划", "PlanDeleteBtn");
            resume.Click += (_, _) => ResumeStudyPlan(plan); delete.Click += (_, _) => DeleteStudyPlan(plan);
            actions.Children.Add(resume); actions.Children.Add(delete); body.Children.Add(actions);
        }
        return LearningCard(body);
    }

    private void StartStudyPlan(DailyStudyPlan plan) => StartPlanCardRound(plan);
    private void StopStudyPlan(DailyStudyPlan plan)
    {
        var priorStatus = plan.Status; DailyStudyPlanRules.Stop(plan);
        if (!SaveStudyPlans()) { plan.Status = priorStatus; return; } RenderStudyPlanLists();
    }
    private bool SaveStudyPlans()
    {
        if (_planReadFailed) { SetStatus(T("学习计划读取失败，已暂停写入以保护原文件。")); return false; }
        try { _studyPlanStore.Save(_studyPlans); return true; }
        catch (Exception ex) { SetStatus(T("学习计划保存失败：") + ex.Message); return false; }
    }
    private IBrush PlanModalBrush()
    {
        var color = ((ISolidColorBrush)this.FindResource(ActualThemeVariant, "PaperBrush")!).Color;
        return new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }
    private void RefreshStudyPlanLanguage()
    {
        NavPlans.Content = T("学习计划"); PlanDialogTitle.Text = T(_editingStudyPlan == null ? "创建每日学习计划" : "调整学习计划");
        PlanNameInput.Watermark = T("计划名称"); PlanWordSearch.Watermark = T("搜索计划词条");
        PlanSelectAllBtn.Content = T("全选"); PlanClearSelectionBtn.Content = T("清空选择");
        PlanDailyCountLabel.Text = T("每日学习词数"); PlanRandomInput.Content = T("随机顺序");
        PlanCancelBtn.Content = PlanCancelOverlapBtn.Content = T("取消"); PlanCreateConfirmBtn.Content = T(_editingStudyPlan == null ? "创建计划" : "保存调整");
        PlanContinueOverlapBtn.Content = T("继续创建");
        if (_studyPlanPage != null)
        {
            _planDialogConfiguring = true;
            var bookIndex = PlanBookInput.SelectedIndex;
            var unit = PlanUnitInput.SelectedItem as LearningSection;
            var units = (PlanUnitInput.ItemsSource as IEnumerable<LearningSection>)?.ToList();
            PlanBookInput.ItemsSource = new[] { T("词汇真经"), T("听力 179"), T("阅读同义替换"), T("英美拼写规范") };
            PlanBookInput.SelectedIndex = bookIndex;
            if (units != null)
            {
                foreach (var section in units.Where(s => s.Id == "plan-whole-book")) section.Title = T("整本词书");
                PlanUnitInput.ItemsSource = null; PlanUnitInput.ItemsSource = units; PlanUnitInput.SelectedItem = unit;
            }
            _planDialogConfiguring = false;
            _planSourceLabel = _editingStudyPlan?.SourceLabel ?? (_planDraftSource == DailyStudyPlanSource.Archive ? T("词汇档案") : PlanBookInput.SelectedItem + " · " + unit?.Title);
            RenderStudyPlanLists(); UpdatePlanEstimate(); RefreshPlanActionsLanguage(); RefreshPlanLearningLanguage();
        }
    }
}
