using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Lexi.Controls;
using Lexi.Features.Settings;

namespace Lexi;

public partial class MainWindow
{
    private SettingsPageControl? _settingsDrawer;
    private string _settingsPreviousPage = "lookup";
    private readonly Dictionary<Control, bool> _settingsPageVisibility = [];
    private bool _settingsPreviousFocusChrome;
    private bool _settingsDrawerOpen;
    private bool _globalFocusActive;
    private ArchiveActionsMenu? _archiveActions;
    private Border? _workspaceChooser;
    private readonly List<Control> _settingsRoots = [];
    private readonly HashSet<Avalonia.Controls.Primitives.FlyoutBase> _openStudyFlyouts=[];
    private void TrackStudyFlyout(Avalonia.Controls.Primitives.FlyoutBase flyout)
    {
        flyout.Opened+=(_,_)=>_openStudyFlyouts.Add(flyout);
        flyout.Closed+=(_,_)=>_openStudyFlyouts.Remove(flyout);
        flyout.Opened+=(_,_)=>{ if(_glassHost!=null)_glassHost.TrackOnce(flyout); };
    }

    private void ConfigureInteractionWorkspace()
    {
        GlobalFocusButton.Click += (_, _) => ToggleGlobalFocus();
        _settingsDrawer = new SettingsPageControl { Name = "SettingsPage" };
        var main = (Grid)((Grid)RootWindowBorder.Child!).Children[0];
        _settingsDrawer.Closed += (_, _) => RestoreSettingsSource();

        Control Move(string name)
        {
            var card = this.FindControl<Control>(name)!;
            SettingsContent.Children.Remove(card);
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.MaxWidth = double.PositiveInfinity;
            _settingsRoots.Add(card);
            return card;
        }
        _settingsDrawer.RegisterSectionContent(SettingsSection.Appearance, Move("ThemeSettingsCard"));
        if (this.FindControl<Border>("ThemeSettingsCard")!.Child is StackPanel appearance)
        {
            appearance.Children.Add(new TextBlock { Text = UiText.Bilingual("专注背景：跟随应用主题", "Focus background: follows app theme"), Margin = new Thickness(0,12,0,0) });
        }
        _settingsDrawer.RegisterSectionContent(SettingsSection.AiService, Move("AiSettingsCard"));
        _settingsDrawer.RegisterSectionContent(SettingsSection.DataAndBackup, Move("DataSettingsCard"));
        _settingsDrawer.RegisterSectionContent(SettingsSection.About, Move("AboutSettingsCard"));
        var studySettings = new StackPanel { Spacing = 16 };
        studySettings.Children.Add(Move("LookupShortcutsCard"));
        // Move the existing global quick-card settings without replacing their editor state.
        foreach (var card in SettingsContent.Children.OfType<Border>().ToArray())
        { SettingsContent.Children.Remove(card); studySettings.Children.Add(card); _settingsRoots.Add(card); }
        AddStudyShortcutSettings(studySettings);
        _settingsDrawer.RegisterSectionContent(SettingsSection.StudyAndShortcuts, studySettings);
        PageSettings.Margin = new Thickness(0);
        PageSettings.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        PageSettings.Content = _settingsDrawer;
        PageSettings.IsVisible = false;

        _archiveActions = new ArchiveActionsMenu { Name = "ArchiveActionsMenu" };
        TrackStudyFlyout(_archiveActions.MoreFlyout);
        Grid.SetColumn(_archiveActions, 1);
        ArchiveActionsHost.Children.Add(_archiveActions);
        _archiveActions.ActionTriggered += ExecuteArchiveMenuAction;
        _archiveActions.UpdateSelection(_filteredWords.Count(w => w.Selected));

        // Keep caption controls available while a focused session or spelling task is shown.
        var outer = (Grid)RootWindowBorder.Child!;
        foreach (var host in new[] { _focusHost, _typingHost })
        {
            if (host == null) continue;
            outer.Children.Remove(host); main.Children.Add(host);
        }
        if (_focusHost != null)
            _focusHost.Bind(Border.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        ConfigureGlobalStudySurface();
    }

    private void OpenSettingsDrawer()
    {
        if(!TryLeaveQuoteEditor())return;
        if (_settingsDrawer == null || _settingsDrawerOpen) return;
        if (!TryFlushWritingDraft()) return;
        PersistPausedSurfaces();
        StopWindowsLearningAudio();
        ++_reviewEpoch;
        _settingsPreviousPage = _currentPage;
        _settingsPreviousFocusChrome = _globalFocusActive;
        _settingsPageVisibility.Clear();
        foreach (var control in new Control?[] { PageLookup, LookupPageHost, PageVocab, PageReview,
            _learningHubPage, _ieltsPage, _quotesPage, _focusHost, _typingHost })
            if (control != null) { _settingsPageVisibility[control] = control.IsVisible; control.IsVisible = false; }
        SetGlobalFocusChrome(false);
        _currentPage = "settings";
        NavLookup.Classes.Set("active", false); NavVocab.Classes.Set("active", false); NavReview.Classes.Set("active", false);
        _navLearningHub?.Classes.Set("active", false); _ieltsNav?.Classes.Set("active", false); _quotesNav?.Classes.Set("active", false);
        UpdateDataInfo();
        _settingsDrawerOpen = true;
        PageSettings.IsVisible = true;
        NavSettings.Classes.Set("active", true);
        _settingsDrawer.Open();
    }
    private void CloseSettingsDrawer() { _settingsDrawer?.Close(); }

    private void RestoreSettingsSource()
    {
        if (!_settingsDrawerOpen) return;
        _settingsDrawerOpen = false;
        PageSettings.IsVisible = false;
        _currentPage = _settingsPreviousPage;
        foreach (var (control, visible) in _settingsPageVisibility) control.IsVisible = visible;
        _settingsPageVisibility.Clear();
        NavSettings.Classes.Set("active", false);
        NavLookup.Classes.Set("active", _currentPage == "lookup"); NavVocab.Classes.Set("active", _currentPage == "vocab");
        NavReview.Classes.Set("active", _currentPage == "review"); _navLearningHub?.Classes.Set("active", _currentPage == "learning");
        _ieltsNav?.Classes.Set("active", _currentPage == "ielts"); _quotesNav?.Classes.Set("active", _currentPage == "quotes");
        SetGlobalFocusChrome(_settingsPreviousFocusChrome);
    }

    private void SetGlobalFocusChrome(bool active)
    {
        _globalFocusActive = active;
        SidebarBorder.IsVisible = !active;
        MainBodyGrid.ColumnDefinitions[0].Width = new GridLength(active ? 0 : 208);
        GlobalFocusButton.Content = UiText.Text(active ? "退出专注" : "专注");
        if (!active && _workspaceChooser != null) _workspaceChooser.IsVisible = false;
        RefreshUnifiedStudyCanvas();
        RefreshGlobalStudySurface();
    }

    private void ToggleGlobalFocus()
    {
        if (_settingsDrawerOpen) { CloseSettingsDrawer(); return; }
        if (_globalFocusActive || _focusActive)
        {
            if (_focusActive) ExitFocus();
            SetGlobalFocusChrome(false);
            return;
        }
        if (_currentPage == "lookup")
        {
            if (!LookupResultCard.IsVisible) { ShowFocusSourceChooser(); return; }
            EnterFocusFromLookup();
        }
        else if (_currentPage == "vocab")
        {
            if (!_filteredWords.Any(w => w.Selected)) { ShowFocusSourceChooser(); return; }
            EnterArchiveFocus();
        }
        else if (_currentPage == "learning" && !_studyWorkspaceHost.IsVisible && _typingHost?.IsVisible != true)
        { ShowFocusSourceChooser(); return; }
        else if (_currentPage=="ielts" && _ieltsWorkspace?.IsVisible==true)
        { ShowIeltsFocusChooser(); return; }
        else if (_currentPage is not "review" and not "learning")
        { ShowFocusSourceChooser(); return; }
        SetGlobalFocusChrome(true);
    }

    private void ShowFocusSourceChooser()
    {
        if (_workspaceChooser != null) { _workspaceChooser.IsVisible=false;_glassHost?.Unregister(_workspaceChooser);((Grid)RootWindowBorder.Child!).Children.Remove(_workspaceChooser); }
        var choices = new StackPanel { Spacing = 14, MaxWidth = 460 };
        choices.Children.Add(LearningText(UiText.Text("选择专注内容"), 24));
        choices.Children.Add(LearningText(UiText.Text("继续已有学习，或选择词汇来源。"), 13));
        choices.Children.Add(LearningButton("今日复习", () => { _workspaceChooser!.IsVisible=false; ShowPage("review"); SetGlobalFocusChrome(true); }, true));
        foreach (var plan in _learningPlans.Where(p => p.Status == DailyStudyPlanStatus.Active))
            choices.Children.Add(LearningButton(plan.Name, () => { _workspaceChooser!.IsVisible=false; ShowPage("learning"); StartDailyLearning(plan); if (_studyWorkspaceHost.IsVisible) SetGlobalFocusChrome(true); }));
        choices.Children.Add(LearningButton("选择词汇档案中的词", () => { _workspaceChooser!.IsVisible=false; ShowPage("vocab"); }));
        choices.Children.Add(LearningButton("浏览 IELTS 教材", () => { _workspaceChooser!.IsVisible=false; ShowIeltsCatalog(); }));
        choices.Children.Add(LearningButton("取消", () => _workspaceChooser!.IsVisible=false));
        var card = new Border { Padding=new Thickness(28), CornerRadius=new CornerRadius(16), Child=choices, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, MaxHeight=Math.Max(280,Bounds.Height-90) };
        Lexi.Shell.GlassOverlayHost.Decorate(card);
        card.Bind(Border.BackgroundProperty,this.GetResourceObservable("CardBrush"));
        _workspaceChooser = new Border { Background=new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#660F172A")), Child=new ScrollViewer { Content=card } };
        _workspaceChooser.SetValue(Panel.ZIndexProperty,220);
        ((Grid)RootWindowBorder.Child!).Children.Add(_workspaceChooser);
        _glassHost!.Register(_workspaceChooser);_glassHost.Refresh();
    }

    private void ExecuteArchiveMenuAction(ArchiveActionKind action)
    {
        var ids = _filteredWords.Where(w => w.Selected).Select(w => w.Id).ToList();
        if (ids.Count == 0) return;
        switch (action)
        {
            case ArchiveActionKind.StudySelected: case ArchiveActionKind.FocusStudy:
                EnterArchiveFocus(); SetGlobalFocusChrome(true); break;
            case ArchiveActionKind.CreatePlan: OpenPlanCreator(DailyStudyPlanSource.Archive); break;
            case ArchiveActionKind.TodayReview: ExecuteBatchAction(ids,"today"); break;
            case ArchiveActionKind.MarkMastered: ExecuteBatchAction(ids,"master"); break;
            case ArchiveActionKind.Relearn: ShowArchiveRelearnConfirmation(ids); break;
            case ArchiveActionKind.Export: OpenSettingsDrawer(); _settingsDrawer!.ShowSection(SettingsSection.DataAndBackup); break;
            case ArchiveActionKind.DeleteSelected:
                _pendingDeleteIds=ids;
                DialogDeleteTitle.Text=UiText.Bilingual($"确认删除选中的 {ids.Count} 个单词？",$"Delete {ids.Count} selected words?");
                DialogDeleteOverlay.IsVisible=true; break;
        }
    }

    private void ShowArchiveRelearnConfirmation(List<long> ids)
    {
        var actions=new StackPanel { Spacing=12 };
        actions.Children.Add(LearningText(UiText.Text("重新学习将重置所选词的记忆进度与排期。")));
        var flyout=new Flyout { Content=actions };
        TrackStudyFlyout(flyout);
        actions.Children.Add(LearningButton("确认重新学习",()=>{ flyout.Hide(); ExecuteBatchAction(ids,"restart"); }));
        actions.Children.Add(LearningButton("取消",()=>flyout.Hide()));
        flyout.ShowAt(_archiveActions!);
    }
}
