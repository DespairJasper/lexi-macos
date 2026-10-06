using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Lexi;

public partial class MainWindow
{
    private Border? _planActionOverlay;
    private TextBlock _planActionTitle = null!, _planActionMessage = null!, _planActionError = null!;
    private Button _planActionConfirm = null!, _planActionCancel = null!;
    private Func<bool>? _pendingPlanAction;
    private string _planActionTitleKey = "", _planActionConfirmKey = "";

    private void ConfigurePlanActions()
    {
        var body = new StackPanel { Spacing = 16, MaxWidth = 460 };
        _planActionTitle = ContentText("", 20);
        _planActionMessage = ContentText("", 14); _planActionMessage.Name = "PlanActionMessage";
        _planActionError = ContentText("", 13); _planActionError.Name = "PlanActionError";
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        _planActionCancel = LearningButton("取消", "PlanActionCancelBtn");
        _planActionConfirm = LearningButton("确认", "PlanActionConfirmBtn", true);
        _planActionCancel.Click += (_, _) => ClosePlanAction();
        _planActionConfirm.Click += (_, _) =>
        {
            if (_pendingPlanAction?.Invoke() == true) ClosePlanAction();
            else _planActionError.Text = T("学习计划保存失败，请重试。");
        };
        actions.Children.Add(_planActionCancel); actions.Children.Add(_planActionConfirm);
        body.Children.Add(_planActionTitle); body.Children.Add(_planActionMessage); body.Children.Add(_planActionError); body.Children.Add(actions);
        _planActionOverlay = new Border { Name = "PlanActionOverlay", IsVisible = false,
            Child = new Border { Classes = { "card" }, Padding = new Thickness(24), Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = body } };
        KeyboardNavigation.SetTabNavigation((Border)_planActionOverlay.Child!, KeyboardNavigationMode.Cycle);
        ((Grid)RootWindowBorder.Child!).Children.Add(_planActionOverlay);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_planActionOverlay.IsVisible && e.Key == Key.Escape) { e.Handled = true; ClosePlanAction(); }
        }, RoutingStrategies.Tunnel);
    }

    private void OpenStudyPlanAdjustment(DailyStudyPlan plan)
    {
        if (!FocusCanNavigate || _planCardActive || _planReadFailed || plan.Status != DailyStudyPlanStatus.Active) return;
        _editingStudyPlan = plan; _planDraftSource = plan.Source;
        _planCandidates = plan.Words.ToList(); _planSourceLabel = plan.SourceLabel;
        _planSelectedIds.Clear(); foreach (var word in plan.Words) _planSelectedIds.Add(word.Id);
        PlanWordScope.IsVisible = _planChoicePaging.IsVisible = false;
        PlanNameInput.Text = plan.Name; PlanDailyCountInput.Value = plan.DailyWordCount; PlanRandomInput.IsChecked = plan.RandomOrder;
        ResetPlanOverlap(); UpdatePlanEstimate();
        PlanDialogTitle.Text = T("调整学习计划"); PlanCreateConfirmBtn.Content = T("保存调整");
        UpdatePlanDialogHeight(); PlanDialogCard.Background = PlanModalBrush(); PlanDialogOverlay.IsVisible = true; PlanNameInput.Focus();
    }

    private void UpdatePlanDialogHeight()
    {
        var available = Math.Max(180, (Bounds.Height > 0 ? Bounds.Height : Height) - 240);
        PlanDialogScroll.MaxHeight = _editingStudyPlan == null ? available : Math.Min(280, available);
        PlanDialogCard.MaxHeight = _editingStudyPlan == null ? double.PositiveInfinity : Math.Min(440, available + 160);
        PlanDialogCard.MinHeight = 0;
    }

    private void SaveStudyPlanAdjustment()
    {
        if (_editingStudyPlan == null || _planReadFailed) return;
        try
        {
            var editing = _editingStudyPlan;
            var updated = DailyStudyPlanRules.Adjust(editing, PlanNameInput.Text!,
                (int)(PlanDailyCountInput.Value ?? 1), PlanRandomInput.IsChecked == true,
                PlanRandomInput.IsChecked == editing.RandomOrder ? editing.ShuffleSeed : Random.Shared.Next());
            var pending = _studyPlans.Select(p => p.Id == editing.Id ? updated : p).ToList();
            if (!PersistStudyPlanList(pending)) { PlanDialogError.Text = T("学习计划保存失败，请重试。"); return; }
            CloseStudyPlanDialog(); _planFeedback.Text = T("计划已调整，学习进度已保留。"); RenderStudyPlanLists();
        }
        catch (ArgumentException ex) { PlanDialogError.Text = ex.Message; }
    }

    private void CloseStudyPlanDialog()
    {
        PlanDialogOverlay.IsVisible = false; _editingStudyPlan = null; _draftStudyPlan = null;
    }

    private bool PersistStudyPlanList(List<DailyStudyPlan> pending)
    {
        if (_planReadFailed) { SetStatus(T("学习计划读取失败，已暂停写入以保护原文件。")); return false; }
        try { _studyPlanStore.Save(pending); _studyPlans = pending; return true; }
        catch (Exception ex) { SetStatus(T("学习计划保存失败：") + ex.Message); return false; }
    }

    private void ResumeStudyPlan(DailyStudyPlan plan)
    {
        if (!FocusCanNavigate || _planReadFailed || plan.Status != DailyStudyPlanStatus.Stopped) return;
        var overlaps = DailyStudyPlanRules.FindOverlaps(_studyPlans, plan);
        if (overlaps.Count == 0) { CommitStudyPlanResume(plan); return; }
        var details = string.Join("；", overlaps.Take(8).Select(o => o.PlanName + " — " + o.Word))
            + (overlaps.Count > 8 ? TF($" 等 {overlaps.Count} 词") : "");
        var warning = TF($"与活动计划重叠：{details}");
        OpenPlanAction("恢复计划", warning, "仍然恢复", () => CommitStudyPlanResume(plan));
    }

    private bool CommitStudyPlanResume(DailyStudyPlan plan)
    {
        if (_planReadFailed || plan.Status != DailyStudyPlanStatus.Stopped || !_studyPlans.Any(p => ReferenceEquals(p, plan))) return false;
        if (!DailyStudyPlanRules.Resume(plan)) return false;
        if (!SaveStudyPlans()) { plan.Status = DailyStudyPlanStatus.Stopped; return false; }
        _planFeedback.Text = T("计划已恢复，将从原进度继续。"); RenderStudyPlanLists(); return true;
    }

    private void DeleteStudyPlan(DailyStudyPlan plan)
    {
        if (!FocusCanNavigate || _planReadFailed || plan.Status != DailyStudyPlanStatus.Stopped) return;
        OpenPlanAction("删除计划", TF($"确认删除计划“{plan.Name}”及其计划进度？"), "删除计划", () =>
        {
            if (plan.Status != DailyStudyPlanStatus.Stopped || !_studyPlans.Any(p => p.Id == plan.Id)) return false;
            if (!PersistStudyPlanList(_studyPlans.Where(p => p.Id != plan.Id).ToList())) return false;
            _planFeedback.Text = T("计划已删除。"); RenderStudyPlanLists(); return true;
        });
    }

    private void OpenPlanAction(string title, string message, string confirm, Func<bool> action)
    {
        _planActionTitleKey = title; _planActionConfirmKey = confirm;
        _planActionMessage.Text = message; _planActionError.Text = ""; _pendingPlanAction = action;
        _planActionOverlay!.Background = (Avalonia.Media.IBrush?)this.FindResource(ActualThemeVariant, "OverlayBrush");
        ((Border)_planActionOverlay.Child!).Background = PlanModalBrush();
        RefreshPlanActionsLanguage(); _planActionOverlay.IsVisible = true; _planActionCancel.Focus();
    }

    private void ClosePlanAction()
    {
        _planActionOverlay!.IsVisible = false; _pendingPlanAction = null;
    }

    private void RefreshPlanActionsLanguage()
    {
        if (_planActionOverlay == null) return;
        _planActionTitle.Text = T(_planActionTitleKey); _planActionConfirm.Content = T(_planActionConfirmKey);
        _planActionCancel.Content = T("取消");
        _planActionMessage.Text = UiText.Redisplay(_planActionMessage.Text); _planActionError.Text = UiText.Redisplay(_planActionError.Text);
    }
}
