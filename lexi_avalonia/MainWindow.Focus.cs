using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lexi;

public partial class MainWindow
{
    private bool _wordFocusActive;
    private bool _focusAiVisible;
    private bool _openingArchivedFocus;
    private FocusSnapshot? _wordFocusSnapshot;
    private CancellationTokenSource? _focusMotionCts;
    private Border? _focusBar;
    private Border? _focusAiSection;
    private Button? _focusEntryButton;
    private Button? _focusBackButton;
    private Button? _focusAiToggleButton;
    private TextBlock? _focusHeading;
    private readonly Dictionary<Control, bool> _focusHiddenControls = new();
    private readonly Dictionary<Control, bool> _focusChromeVisibility = new();
    private RowDefinitions? _focusLayoutRows;
    private RowDefinitions? _focusLookupRows;
    private GridLength _focusSidebarWidth;
    private double _focusOriginalCardMaxWidth;
    private double _focusOriginalWordFontSize;
    private HorizontalAlignment _focusOriginalCardAlignment;
    private ScrollBarVisibility _focusOriginalVerticalScrollBarVisibility;
    private Control[] _focusDecorations = [];
    private Thickness _focusOriginalLookupMargin;

    // This is a view snapshot only: entering or leaving focus never writes word data.
    private sealed record FocusSnapshot(string Page, string Query, string Word, string Phonetic,
        string Translation, string Definition, string Pos, LlmResult? Expansion, bool ResultVisible,
        bool EmptyVisible, bool NotFoundVisible, bool DrawerOpen, Vector Offset, string Status);

    private bool FocusCanNavigate => _databaseAvailable && !_restoring && !_isForceClose
        && !DialogEditOverlay.IsVisible && !DialogStageOverlay.IsVisible
        && !DialogDeleteOverlay.IsVisible && !RestoreOverlay.IsVisible
        && !PlanDialogOverlay.IsVisible && _planSpellingOverlay?.IsVisible != true && _planActionOverlay?.IsVisible != true;

    private void ConfigureWordFocus()
    {
        if (_focusBar != null) return;
        _focusAiSection = AiDrawerSlot.GetLogicalAncestors().OfType<Border>().First(x => x != LookupResultCard);
        _focusEntryButton = new Button
        {
            Name = "FocusEntryBtn", Classes = { "ghost" }, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 26, 0, 0)
        };
        _focusEntryButton.Click += (_, _) => EnterWordFocus();
        var wordHeader = ((StackPanel)LookupResultCard.Child!).Children.OfType<Grid>().First();
        Grid.SetColumn(_focusEntryButton, 1);
        wordHeader.Children.Add(_focusEntryButton);
        _focusDecorations =
        [
            wordHeader.Children.OfType<StackPanel>().First().Children.OfType<TextBlock>().First(),
            wordHeader.Children.OfType<Border>().First(x => Grid.GetColumn(x) == 1)
        ];
        ResultWordText.Cursor = new Cursor(StandardCursorType.Hand);
        ResultWordText.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(ResultWordText).Properties.IsLeftButtonPressed && FocusCanNavigate)
            { EnterWordFocus(); e.Handled = true; }
        };

        _focusBackButton = new Button { Name = "FocusBackBtn", Classes = { "ghost" }, FontSize = 13, Padding = new Thickness(12, 7) };
        _focusBackButton.Click += (_, _) => { if (FocusCanNavigate) ExitWordFocus(); };
        _focusHeading = new TextBlock { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var keyHint = new TextBlock { Text = "Esc", Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var focusHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        focusHeader.Children.Add(_focusBackButton);
        Grid.SetColumn(_focusHeading, 1); focusHeader.Children.Add(_focusHeading);
        Grid.SetColumn(keyHint, 2); focusHeader.Children.Add(keyHint);
        _focusBar = new Border
        {
            Name = "WordFocusBar", Child = focusHeader, IsVisible = false,
            Padding = OperatingSystem.IsMacOS() ? new Thickness(100, 12, 20, 12) : new Thickness(20, 12),
            MinHeight = OperatingSystem.IsMacOS() ? 60 : 0, Background = Brushes.Transparent
        };
        _focusBar.PointerPressed += OnTitleBarPressed;
        LookupPageHost.Children.Add(_focusBar);
        _focusAiToggleButton = new Button
        {
            Name = "FocusAiToggleBtn", Classes = { "ghost" }, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13, Padding = new Thickness(0, 8)
        };
        _focusAiToggleButton.Click += (_, _) =>
        {
            _focusAiVisible = !_focusAiVisible;
            _focusAiSection.IsVisible = _focusAiVisible;
            RefreshWordFocusLabels();
        };
        ((StackPanel)LookupResultCard.Child!).Children.Add(_focusAiToggleButton);
        ConfigureFocusLearningActions(focusHeader);

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!FocusCanNavigate) return;
            if (e.Key == Key.Escape && _wordFocusActive)
            { ExitWordFocus(); e.Handled = true; }
            else if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Shift)
                     && (_wordFocusActive || (_currentPage == "lookup" && LookupResultCard.IsVisible)))
            {
                if (_wordFocusActive) ExitWordFocus(); else EnterWordFocus();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        // Space can change focus while its asynchronous rating is still running.
        // Consume the matching release before the newly focused Button handles it.
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (_wordFocusActive && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Space or Key.Enter)
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Restrict double-click to passive word-row content; editing and selecting retain
        // their existing input behavior and never open a card accidentally.
        VocabListBox.AddHandler(PointerPressedEvent, async (_, e) =>
        {
            if (e.ClickCount != 2 || !e.GetCurrentPoint(VocabListBox).Properties.IsLeftButtonPressed
                || e.Source is not Control source || !FocusCanNavigate) return;
            var ancestors = source.GetVisualAncestors().OfType<Control>().Prepend(source).TakeWhile(x => x != VocabListBox).ToArray();
            if (ancestors.Any(x => x is Button or ToggleButton or CheckBox or TextBox or ComboBox)) return;
            var word = ancestors.Select(x => x.DataContext).OfType<WordItem>().FirstOrDefault();
            if (word == null) return;
            e.Handled = true;
            await OpenArchivedWordFocusAsync(word);
        }, RoutingStrategies.Bubble);
        RefreshWordFocusLabels();
    }

    private void RefreshWordFocusLabels()
    {
        if (_focusEntryButton == null) return;
        _focusEntryButton.Content = T("专注 ↗");
        ToolTip.SetTip(_focusEntryButton, T("打开沉浸式单词卡（⌘⇧F）"));
        _focusBackButton!.Content = T("返回");
        ToolTip.SetTip(_focusBackButton, T("退出专注 · Esc"));
        _focusHeading!.Text = T("单词卡");
        _focusAiToggleButton!.Content = "···";
        ToolTip.SetTip(_focusAiToggleButton, T(_focusAiVisible ? "收起 AI 扩展" : "展开 AI 扩展"));
        RefreshFocusLearningLabels();
        // 评价后的释义页保留被评价词的连击；其余时刻跟随当前卡。
        if (!_focusRated) PaintFocusStreak(_focusRound.CurrentStreak, _focusRound.CurrentTarget, animateNewest: false);
        else if (this.FindControl<StackPanel>("FocusStreakBars") is { } streakHost) streakHost.IsVisible = _wordFocusActive && _focusRound.Total > 0;
    }

    private FocusSnapshot CaptureFocusSnapshot() => new(_currentPage, LookupInput.Text ?? "",
        ResultWordText.Text ?? "", ResultPhoneticText.Text ?? "", ResultTranslationText.Text ?? "",
        ResultDefinitionText.Text ?? "", string.Join(" ", ResultPosPanel.Children.OfType<Border>()
            .Select(x => (x.Child as TextBlock)?.Text)), _currentExpansion, LookupResultCard.IsVisible,
        LookupEmptyCard.IsVisible, LookupNotFoundCard.IsVisible, _drawerOpen, PageLookup.Offset,
        GlobalStatusText.Text ?? "");

    private async Task OpenArchivedWordFocusAsync(WordItem word)
    {
        if (_openingArchivedFocus || _wordFocusActive || !FocusCanNavigate) return;
        _openingArchivedFocus = true;
        var snapshot = CaptureFocusSnapshot();
        try
        {
            LookupInput.Text = word.Word;
            await PerformLookupAsync();
            // A navigation or a newer query during the await takes precedence.
            if (!FocusCanNavigate || _currentPage != snapshot.Page || ResultWordText.Text != word.Word
                || !LookupResultCard.IsVisible) return;
            EnterWordFocus(snapshot);
        }
        finally { _openingArchivedFocus = false; }
    }

    private void EnterWordFocus(FocusSnapshot? snapshot = null)
    {
        if (_wordFocusActive || !FocusCanNavigate || !LookupResultCard.IsVisible || _focusBar == null) return;
        _wordFocusSnapshot = snapshot ?? CaptureFocusSnapshot();
        // Switch page before setting active, so ordinary navigation hooks cannot exit us.
        ShowPage("lookup");
        _focusOriginalVerticalScrollBarVisibility = PageLookup.VerticalScrollBarVisibility;
        PageLookup.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _wordFocusActive = true;
        Classes.Set("word-focus", true);
        var layout = this.FindControl<Grid>("WindowLayoutGrid")!;
        var body = this.FindControl<Grid>("WindowBodyGrid")!;
        _focusLayoutRows = CloneRows(layout.RowDefinitions);
        _focusLookupRows = CloneRows(LookupPageHost.RowDefinitions);
        _focusSidebarWidth = body.ColumnDefinitions[0].Width;
        layout.RowDefinitions = new RowDefinitions("0,*,0");
        body.ColumnDefinitions[0].Width = new GridLength(0);
        foreach (var chrome in new Control[] { DragBar, this.FindControl<Border>("SidebarShell")!, this.FindControl<Border>("WindowStatusBar")! })
        { _focusChromeVisibility[chrome] = chrome.IsVisible; chrome.IsVisible = false; }
        LookupPageHost.RowDefinitions = new RowDefinitions("Auto,*,0");
        Grid.SetRow(_focusBar, 0); Grid.SetRow(PageLookup, 1); Grid.SetRow(LookupActionBar, 2);
        _focusBar.IsVisible = true;
        foreach (var control in LookupPageContent.Children.Where(x => x != LookupResultCard)) HideForFocus(control);
        HideForFocus(LookupActionBar);
        HideForFocus(_focusEntryButton!);
        HideForFocus(_focusAiSection!);
        foreach (var decoration in _focusDecorations) HideForFocus(decoration);
        _focusOriginalCardMaxWidth = LookupResultCard.MaxWidth;
        _focusOriginalCardAlignment = LookupResultCard.HorizontalAlignment;
        _focusOriginalWordFontSize = ResultWordText.FontSize;
        _focusOriginalLookupMargin = PageLookup.Margin;
        LookupResultCard.MaxWidth = 780;
        LookupResultCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        ResultWordText.FontSize = 32;
        ResultPhoneticText.FontSize = 15;
        foreach (var answer in new Control[] { ResultTranslationText, ResultDefinitionText, ResultPosPanel }) HideForFocus(answer);
        _focusAiVisible = false;
        _focusAiToggleButton!.IsVisible = true;
        if (!_planCardActive) BeginFocusLearning();
        ApplyLearningBackdrop();
        PageLookup.Offset = default;
        RefreshWordFocusLabels();
        _focusBackButton!.Focus();

        // 进入沉浸：顶部条轻微下滑，卡片轻微上浮，避免整块界面硬切。
        _focusMotionCts?.Cancel();
        var focusCts = _focusMotionCts = new CancellationTokenSource();
        Motion.SetPose(_focusBar, Motion.Pose(0, -6, 1), 0);
        Motion.SetPose(LookupResultCard, Motion.Pose(0, 10, 0.985), 0);
        _ = Motion.ToPoseAsync(_focusBar, Motion.Rest, 1, Motion.Fast, Motion.Enter, focusCts.Token);
        _ = Motion.ToPoseAsync(LookupResultCard, Motion.Rest, 1, Motion.Standard, Motion.Enter, focusCts.Token);
    }

    private void HideForFocus(Control control)
    {
        _focusHiddenControls[control] = control.IsVisible;
        control.IsVisible = false;
    }

    private void ExitWordFocus()
    {
        if (!_wordFocusActive || _wordFocusSnapshot == null) return;
        // 让仍在飞行中的卡片动画失效，避免淡出/淡入写回已复原的布局。
        ++_focusEpoch;
        var snapshot = _wordFocusSnapshot;
        _wordFocusActive = false;
        PageLookup.VerticalScrollBarVisibility = _focusOriginalVerticalScrollBarVisibility;
        StopLearningSpeech();
        _focusDetails!.IsVisible = false;
        _focusPronounceButton!.IsVisible = false;
        Classes.Set("word-focus", false);
        this.FindControl<Grid>("WindowLayoutGrid")!.RowDefinitions = CloneRows(_focusLayoutRows!);
        this.FindControl<Grid>("WindowBodyGrid")!.ColumnDefinitions[0].Width = _focusSidebarWidth;
        LookupPageHost.RowDefinitions = CloneRows(_focusLookupRows!);
        Grid.SetRow(PageLookup, 0); Grid.SetRow(LookupActionBar, 1);
        _focusBar!.IsVisible = false;
        _focusAiToggleButton!.IsVisible = false;
        LookupResultCard.MaxWidth = _focusOriginalCardMaxWidth;
        LookupResultCard.HorizontalAlignment = _focusOriginalCardAlignment;
        ResultWordText.FontSize = _focusOriginalWordFontSize;
        ResultPhoneticText.FontSize = 17;
        PageLookup.Margin = _focusOriginalLookupMargin;
        _focusActions!.IsVisible = false;
        LookupResultCard.MinHeight = 0;
        foreach (var (control, visible) in _focusHiddenControls) control.IsVisible = visible;
        foreach (var (control, visible) in _focusChromeVisibility) control.IsVisible = visible;
        _focusHiddenControls.Clear(); _focusChromeVisibility.Clear();
        LookupInput.Text = snapshot.Query;
        if (!string.Equals(ResultWordText.Text, snapshot.Word, StringComparison.Ordinal) || snapshot.Page != "lookup")
        {
            ++_lookupVersion;
            _aiCts?.Cancel();
            ResultWordText.Text = snapshot.Word;
            ResultPhoneticText.Text = snapshot.Phonetic is "暂无音标" or "No pronunciation available"
                ? T("暂无音标") : snapshot.Phonetic;
            ResultTranslationText.Text = snapshot.Translation;
            ResultDefinitionText.Text = snapshot.Definition;
            RenderPartOfSpeech(snapshot.Pos, snapshot.Translation);
            _currentExpansion = snapshot.Expansion;
            if (snapshot.Expansion != null) RenderExpansion(snapshot.Expansion);
            else AiDrawerToggleBtn.IsVisible = false;
            LookupResultCard.IsVisible = snapshot.ResultVisible;
            LookupEmptyCard.IsVisible = snapshot.EmptyVisible;
            LookupNotFoundCard.IsVisible = snapshot.NotFoundVisible;
            GlobalStatusText.Text = UiText.Redisplay(snapshot.Status);
            if (snapshot.DrawerOpen && snapshot.Expansion != null) _ = OpenDrawerAsync();
            else _ = CloseDrawerAsync(immediate: true);
        }
        UpdateLookupArchiveState();
        if (_planCardActive) EndPlanCardFocus();
        _wordFocusSnapshot = null;
        ShowPage(snapshot.Page);
        ApplyLearningBackdrop();

        // 退出沉浸：标题栏/侧栏淡入，卡片回落，抵消布局瞬间复原的突兀感。
        _focusMotionCts?.Cancel();
        var exitCts = _focusMotionCts = new CancellationTokenSource();
        foreach (var chrome in new Control[] { DragBar, this.FindControl<Border>("SidebarShell")!, this.FindControl<Border>("WindowStatusBar")! })
        {
            Motion.SetPose(chrome, Motion.Rest, 0);
            _ = Motion.ToPoseAsync(chrome, Motion.Rest, 1, Motion.Fast, Motion.Enter, exitCts.Token);
        }
        if (LookupResultCard.IsVisible)
        {
            Motion.SetPose(LookupResultCard, Motion.Pose(0, -8, 0.99), 0);
            _ = Motion.ToPoseAsync(LookupResultCard, Motion.Rest, 1, Motion.Standard, Motion.Enter, exitCts.Token);
        }

        // Page visibility/layout changes can clamp Offset until the next arrange pass.
        var restoredVersion = _lookupVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_wordFocusActive && _lookupVersion == restoredVersion) PageLookup.Offset = snapshot.Offset;
        }, DispatcherPriority.Loaded);
    }
}
