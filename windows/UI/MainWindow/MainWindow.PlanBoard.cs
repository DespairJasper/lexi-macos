using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Lexi.Features.Learning;
using Lexi.Features.Settings;

namespace Lexi;

public partial class MainWindow
{
    private Border? _planEditDrawer;

    private void RenderPlanBoardGroup(IEnumerable<DailyStudyPlan> plans)
    {
        var board=new PlanBoardControl();
        var grid=new ResponsivePlanGrid();
        var today=DateOnly.FromDateTime(DateTime.Now);
        foreach(var plan in plans)
        {
            var todayCompleted = plan.LastBatchCompletedDate is { } completed && completed < today
                ? 0 : plan.CurrentBatchWordIds.Count(id => plan.CompletedWordIds.Contains(id));
            var target=plan.LastBatchCompletedDate is { } previous && previous < today
                ? Math.Min(plan.DailyWordCount,plan.Words.Count-plan.CompletedWordIds.Count)
                : plan.CurrentBatchWordIds.Count>0?plan.CurrentBatchWordIds.Count:Math.Min(plan.DailyWordCount,plan.Words.Count-plan.CompletedWordIds.Count);
            var model=new PlanCardModel(plan.Id,plan.Name,plan.SourceLabel,
                UiText.Text(plan.Status==DailyStudyPlanStatus.Active?"进行中":plan.Status==DailyStudyPlanStatus.Stopped?"已停止":"已完成"),
                todayCompleted,target,
                plan.CompletedWordIds.Count,plan.Words.Count,DailyStudyPlanRules.EstimatedDaysRemaining(plan),plan.Status==DailyStudyPlanStatus.Active,plan);
            var card=board.CreateStandardCard(model,
                _=> { if(plan.Status==DailyStudyPlanStatus.Stopped) ResumeLearningPlan(plan); else if(plan.Status==DailyStudyPlanStatus.Completed) ShowLastPlanBatch(plan); else {ShowPage("learning");StartDailyLearning(plan);} },
                (_,anchor)=>ShowPlanManagementMenu(plan,anchor));
            if(plan.Status!=DailyStudyPlanStatus.Active && card is Border { Child: Grid layout } && layout.Children.LastOrDefault() is Grid actionBar && actionBar.Children[0] is Button start)
            { start.Content=UiText.Text(plan.Status==DailyStudyPlanStatus.Stopped?"恢复计划":"查看最近批次"); start.IsEnabled=plan.Status==DailyStudyPlanStatus.Stopped||plan.CurrentBatchWordIds.Count>0; }
            grid.Children.Add(card);
        }
        var cards=grid.Children.ToArray();
        grid.Children.Clear();
        board.SetCards(cards);
        _learningPlansPanel.Children.Add(board);
    }

    private void ShowPlanManagementMenu(DailyStudyPlan plan,Control anchor)
    {
        var menu=new MenuFlyout();
        TrackStudyFlyout(menu);
        void Item(string title,Action action){var item=new MenuItem { Header=UiText.Text(title) }; item.Click+=(_,_)=>action(); menu.Items.Add(item);}
        if(plan.Status==DailyStudyPlanStatus.Active)
        { Item("调整计划",()=>OpenPlanEditor(plan)); Item("停止计划",()=>SetLearningPlanStopped(plan)); }
        else if(plan.Status==DailyStudyPlanStatus.Stopped) Item("恢复计划",()=>ResumeLearningPlan(plan));
        if(plan.CurrentBatchWordIds.Count>0) Item("最近批次拼写练习",()=>ShowLastPlanBatch(plan));
        if(plan.Status!=DailyStudyPlanStatus.Active) Item("删除计划…",()=>ConfirmDeletePlan(plan,anchor));
        menu.ShowAt(anchor);
    }

    private void ClosePlanEditor()
    {
        if(_planEditDrawer?.Parent is Panel parent) parent.Children.Remove(_planEditDrawer);
        _planEditDrawer=null;
    }

    private void OpenPlanEditor(DailyStudyPlan plan)
    {
        ClosePlanEditor();
        var main=(Grid)((Grid)RootWindowBorder.Child!).Children[0];
        PlanEditorControl? editor=null;
        editor=new PlanEditorControl(new PlanEditorModel(plan.Name,plan.DailyWordCount,plan.RandomOrder,plan.Words.Count-plan.CompletedWordIds.Count),model=>
        {
            if(_restoring || !_databaseAvailable) {editor!.SetError(UiText.Bilingual("词库暂不可用。","Archive unavailable."));return;}
            var original=_learningPlans;
            AdjustLearningPlan(plan,model.Name,model.DailyCount,model.Shuffle);
            if(!ReferenceEquals(original,_learningPlans)) ClosePlanEditor();
            else editor!.SetError(GlobalStatusText.Text??UiText.Text("保存失败"));
        },ClosePlanEditor);
        editor.Name="PlanEditor";
        _planEditDrawer=new Border {Name="PlanEditorPage",Child=new ScrollViewer {Content=editor,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled},Padding=new Thickness(24,16),IsVisible=true};
        _planEditDrawer.Bind(Border.BackgroundProperty,this.GetResourceObservable("PaperBrush"));
        _planEditDrawer.SetValue(Panel.ZIndexProperty,210);Grid.SetRow(_planEditDrawer,1);main.Children.Add(_planEditDrawer);
    }
    private void ConfirmDeletePlan(DailyStudyPlan plan,Control anchor)
    {
        var body=new StackPanel {Spacing=12};
        body.Children.Add(LearningText(UiText.Text("删除只清除此计划记录，词汇档案不受影响。")));
        var confirm=new Flyout {Content=body};
        TrackStudyFlyout(confirm);
        body.Children.Add(LearningButton("确认删除此计划",()=>
        {
            var updated=_learningPlans.Where(p=>p.Id!=plan.Id).ToList();
            if(!SaveLearningPlans(updated))return;
            _learningPlans=updated;
            if(_activeLearningPlan?.Id==plan.Id) {_activeLearningPlan=null;_dailyLearningSession=null;_studyWorkspaceHost.Children.Clear();_studyWorkspaceHost.IsVisible=false;_managementHost.IsVisible=true;}
            confirm.Hide(); RenderLearningPlans();
        }));
        body.Children.Add(LearningButton("取消",()=>confirm.Hide()));
        confirm.ShowAt(anchor);
    }
}
