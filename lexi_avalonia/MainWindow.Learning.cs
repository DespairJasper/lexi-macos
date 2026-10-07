using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Lexi;

public partial class MainWindow
{
    private bool _reviewFullPage;
    private RowDefinitions? _reviewLayoutRows;
    private GridLength _reviewSidebarWidth;
    private readonly Dictionary<Control, bool> _reviewChromeVisibility = [];
    private Grid? _focusActions;
    private TextBlock? _focusFeedback;
    private Button? _focusKnownButton, _focusUnsureButton, _focusForgotButton, _focusUndoButton, _focusSaveButton, _focusMasterButton;
    private List<string> _focusDeck = [];
    private readonly StudyRound<string> _focusRound = new();
    private bool _focusRatingBusy;
    private bool _focusAnswerRevealed;
    private bool _focusRated;
    private StudyRating _focusLastRating = StudyRating.Known;
    private int _focusShownStreak;
    private int _focusShownTarget = StudyRound<string>.RequiredStreak;
    private bool _focusShownCompleted;
    private string? _focusRatedWord;
    private int _focusEpoch;
    private Button? _focusNextButton, _focusWrongButton, _focusStartRecallButton, _focusPronounceButton;
    private StackPanel? _focusDetails;
    private readonly Stack<(long Id, StudyRating Rating, StudyStep Step)> _focusUndo = new();

    private static readonly SolidColorBrush StreakEmptyBrush = new(Color.Parse("#BFC9CFD4"));
    private static readonly SolidColorBrush StreakKnownBrush = new(Color.Parse("#268D98"));

    /// <summary>
    /// 三条竖直连击指示（XAML 自上而下为 Bar3/Bar2/Bar1）：每攒到一次「认识」自下而上点亮一格。
    /// 模糊会熄灭最上面一格，忘记全部熄灭；新点亮的那格淡入（减少动态效果时即时）。
    /// </summary>
    private void PaintStreakBars(StackPanel host, int streak, int target, bool animateNewest)
    {
        var bars = host.Children.OfType<Border>().ToList();
        var reduce = ReduceMotionBox.IsChecked.GetValueOrDefault();
        var previous = host.Tag is int value ? value : -1;
        for (var i = 0; i < bars.Count; i++)
        {
            var bottomUp = bars.Count - 1 - i;
            var filled = bottomUp < streak;
            bars[i].Background = filled ? StreakKnownBrush : StreakEmptyBrush;
            bars[i].Opacity = 1;
            if (animateNewest && !reduce && filled && previous >= 0 && previous < streak && bottomUp == streak - 1)
            {
                bars[i].Opacity = 0;
                _ = Motion.ToPoseAsync(bars[i], Motion.Rest, 1, Motion.Standard, Motion.Enter);
            }
        }
        host.Tag = streak;
        ToolTip.SetTip(host, TF($"本轮认识 {streak}/{target}"));
    }

    private void PaintFocusStreak(int streak, int target, bool animateNewest)
    {
        if (this.FindControl<StackPanel>("FocusStreakBars") is not { } host) return;
        host.IsVisible = _wordFocusActive && _focusRound.Total > 0;
        PaintStreakBars(host, streak, target, animateNewest);
    }

    // Grid caches its row slots per RowDefinitions instance: assigning the very
    // same object back after collapsing the rows keeps the stale 0-height cache
    // and the title bar stays arranged at height 0. Always restore a fresh
    // instance built from the captured heights.
    private static RowDefinitions CloneRows(RowDefinitions source)
    {
        var clone = new RowDefinitions();
        foreach (var row in source) clone.Add(new RowDefinition(row.Height));
        return clone;
    }

    private void SetFullPageReview(bool active)
    {
        if (active == _reviewFullPage) return;
        StopLearningSpeech();
        var layout = this.FindControl<Grid>("WindowLayoutGrid")!;
        var body = this.FindControl<Grid>("WindowBodyGrid")!;
        if (active)
        {
            _reviewLayoutRows = CloneRows(layout.RowDefinitions);
            _reviewSidebarWidth = body.ColumnDefinitions[0].Width;
            layout.RowDefinitions = new RowDefinitions("0,*,0");
            body.ColumnDefinitions[0].Width = new GridLength(0);
            foreach (var chrome in new Control[] { DragBar, this.FindControl<Border>("SidebarShell")!, this.FindControl<Border>("WindowStatusBar")! })
            { _reviewChromeVisibility[chrome] = chrome.IsVisible; chrome.IsVisible = false; }
        }
        else
        {
            layout.RowDefinitions = CloneRows(_reviewLayoutRows!);
            body.ColumnDefinitions[0].Width = _reviewSidebarWidth;
            foreach (var (chrome, visible) in _reviewChromeVisibility) chrome.IsVisible = visible;
            _reviewChromeVisibility.Clear();
        }
        _reviewFullPage = active;
        ApplyLearningBackdrop();
        ApplyLayout();
    }

    private void ApplyLearningBackdrop()
    {
        if (!_wordFocusActive && !_reviewFullPage || _settings.HighContrast)
        {
            RootWindowBorder.Background = (IBrush?)this.FindResource(ActualThemeVariant, "PaperBrush");
            return;
        }
        var dark = _settings.Theme == "Dark";
        var alpha = _settings.OpaqueMaterial ? "FF" : MacGlassMaterial.WantsLiquidGlass(_settings)
            ? MacGlassMaterial.SurfaceColor(_settings).A.ToString("X2") : "CC";
        RootWindowBorder.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse("#" + alpha + (dark ? "193447" : "E5F5FB")), 0),
                new GradientStop(Color.Parse("#" + alpha + (dark ? "24445C" : "B5DDF2")), .52),
                new GradientStop(Color.Parse("#" + alpha + (dark ? "18364B" : "DAEFF8")), 1)
            ]
        };
    }

    private Button CreateRecallAction(string name, string label, string key, string color)
    {
        var content = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Text = T(label) + "  " + key, FontSize = 14, FontWeight = FontWeight.Medium });
        content.Children.Add(new Border { Width = 9, Height = 3, CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.Parse(color)), HorizontalAlignment = HorizontalAlignment.Center });
        return new Button { Name = name, Classes = { "ghost" }, Content = content,
            HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(20, 12) };
    }

    private void ConfigureFocusLearningActions(Grid header)
    {
        foreach (var oldHint in header.Children.OfType<TextBlock>().Where(x => x.Text == "Esc")) oldHint.IsVisible = false;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _focusUndoButton = new Button { Name = "FocusUndoBtn", Classes = { "ghost" }, Content = "↶  ⌥Space", IsEnabled = false };
        _focusUndoButton.Click += async (_, _) => await UndoFocusRatingAsync();
        _focusSaveButton = new Button { Name = "FocusSaveBtn", Classes = { "ghost" }, Content = "☆  C" };
        _focusSaveButton.Click += (_, _) => { if (!_planCardActive) AddCurrentWordToVocab(); RefreshFocusLearningLabels(); };
        _focusMasterButton = new Button { Name = "FocusMasterBtn", Classes = { "ghost" }, Content = T("熟") + "  Del" };
        _focusMasterButton.Click += async (_, _) => await MasterFocusAsync();
        tools.Children.Add(_focusUndoButton); tools.Children.Add(_focusSaveButton); tools.Children.Add(_focusMasterButton);
        ((StackPanel)LookupResultCard.Child!).Children.Remove(_focusAiToggleButton!);
        tools.Children.Add(_focusAiToggleButton!);
        tools.Children.Add(new TextBlock { Text = "Esc", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(tools, 2); header.Children.Add(tools);
        _focusBar!.Padding = new Thickness(24, OperatingSystem.IsMacOS() ? 52 : 12, 24, 8);
        _focusBar.MinHeight = OperatingSystem.IsMacOS() ? 94 : 50;

        _focusKnownButton = CreateRecallAction("FocusKnownBtn", "认识", "Q", "#268D98");
        _focusUnsureButton = CreateRecallAction("FocusUnsureBtn", "模糊", "W", "#D6AD4E");
        _focusForgotButton = CreateRecallAction("FocusForgotBtn", "忘记了", "E", "#CF7E7C");
        _focusKnownButton.Click += async (_, _) => await RateFocusedWordAsync(StudyRating.Known);
        _focusUnsureButton.Click += async (_, _) => await RateFocusedWordAsync(StudyRating.Unsure);
        _focusForgotButton.Click += async (_, _) => await RateFocusedWordAsync(StudyRating.Forgot);
        _focusStartRecallButton = CreateRecallAction("FocusStartRecallBtn", "开始回忆", "Space", "#268D98");
        _focusStartRecallButton.Click += async (_, _) => await CompleteFocusLearnAsync();
        _focusNextButton = CreateRecallAction("FocusNextBtn", "下一词", "Space", "#268D98");
        _focusWrongButton = CreateRecallAction("FocusWrongBtn", "记错了", "E", "#CF7E7C");
        _focusNextButton.Click += async (_, _) => await AdvanceFocusAsync();
        _focusWrongButton.Click += async (_, _) => await ReclassifyFocusAsync();
        _focusFeedback = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Center, Opacity = .65, TextWrapping = TextWrapping.Wrap };
        _focusActions = new Grid { Name = "WordFocusActions", RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("*,*,*"), MinHeight = 100, Margin = new Thickness(30, 0, 30, 20), IsVisible = false };
        Grid.SetColumnSpan(_focusFeedback, 3); _focusActions.Children.Add(_focusFeedback);
        foreach (var (button, column) in new[] { (_focusKnownButton, 0), (_focusUnsureButton, 1), (_focusForgotButton, 2) })
        { Grid.SetRow(button, 1); Grid.SetColumn(button, column); _focusActions.Children.Add(button); }
        Grid.SetRow(_focusStartRecallButton, 1); Grid.SetColumn(_focusStartRecallButton, 0); Grid.SetColumnSpan(_focusStartRecallButton, 3);
        Grid.SetRow(_focusNextButton, 1); Grid.SetRow(_focusWrongButton, 1);
        Grid.SetColumn(_focusNextButton, 0); Grid.SetColumnSpan(_focusNextButton, 2); Grid.SetColumn(_focusWrongButton, 2);
        _focusActions.Children.Add(_focusStartRecallButton);
        _focusActions.Children.Add(_focusNextButton); _focusActions.Children.Add(_focusWrongButton);
        Grid.SetRow(_focusActions, 2); LookupPageHost.Children.Add(_focusActions);
        _focusDetails = new StackPanel { Name = "FocusAnswerDetails", Spacing = 10, IsVisible = false };
        ((StackPanel)LookupResultCard.Child!).Children.Add(_focusDetails);
        _focusPronounceButton = new Button { Name = "FocusPronounceBtn", Classes = { "secondary" }, Content = "英  ▷", FontSize = 12,
            Padding = new Thickness(8, 2), IsVisible = false };
        _focusPronounceButton.Click += (_, _) => SpeakFocusedWord();
        var phoneticParent = (StackPanel)ResultPhoneticText.Parent!;
        phoneticParent.Children.Remove(ResultPhoneticText);
        var pronunciation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pronunciation.Children.Add(_focusPronounceButton); pronunciation.Children.Add(ResultPhoneticText);
        phoneticParent.Children.Add(pronunciation);

        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Handled || !_wordFocusActive || !FocusCanNavigate || _focusRatingBusy
                || e.Source is TextBox || e.Source is Control source && source.GetVisualAncestors().OfType<TextBox>().Any()) return;
            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Alt)
            { e.Handled = true; await UndoFocusRatingAsync(); }
            else if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Q or Key.W or Key.E or Key.Delete or Key.C or Key.Space or Key.A or Key.S)
            {
                e.Handled = true;
                if (e.Key == Key.C) { if (!_planCardActive) AddCurrentWordToVocab(); RefreshFocusLearningLabels(); }
                else if (e.Key == Key.A) SpeakFocusedWord();
                else if (e.Key == Key.S && _focusAnswerRevealed) SpeakLearningText((_planCardActive ? _currentExpansion : FindCurrentArchive()?.AiResult ?? _currentExpansion)?.Examples.FirstOrDefault()?.English);
                else if (e.Key == Key.Delete) await MasterFocusAsync();
                else if (e.Key == Key.Space && _focusRound.HasCurrent && _focusRound.CurrentStep == StudyStep.Learn
                    && (!_planCardActive || !_focusRated)) await CompleteFocusLearnAsync();
                else if (e.Key == Key.Space) await AdvanceFocusAsync();
                else if (e.Key == Key.Q) await RateFocusedWordAsync(StudyRating.Known);
                else if (e.Key == Key.W) await RateFocusedWordAsync(StudyRating.Unsure);
                else if (e.Key == Key.E) { if (_focusRated) await ReclassifyFocusAsync(); else await RateFocusedWordAsync(StudyRating.Forgot); }
            }
        }, RoutingStrategies.Tunnel);
    }

    private void BeginFocusLearning()
    {
        var deck = new List<string> { ResultWordText.Text! };
        deck.AddRange(_allWords.Where(x => x.Status == "learning"
            && !x.Word.Equals(ResultWordText.Text, StringComparison.OrdinalIgnoreCase)).Select(x => x.Word));
        StartFocusRound(deck);
    }

    /// <summary>开启一轮卡片：新词先学后测，已有档案的词直接回忆，两种词可在同一轮里并存。</summary>
    private void StartFocusRound(List<string> deck)
    {
        ++_focusEpoch;
        _focusDeck = deck;
        _focusRound.Reset(deck, word => FindArchive(word) == null);
        // 「本轮新词」必须在 Reset 的同一时刻捕获（规格书 §9.2）：轮中 AddCurrentWordToVocab 之后
        // FindArchive 会返回非 null，事后重算会把新词误判成复习词。
        _focusNewWords.Clear();
        foreach (var word in deck)
        {
            // 词形一律先归一化（与 WordKeyResolver 的查表口径一致，评审 P1-5）。
            var normalized = WordKeyResolver.FormC(word);
            if (FindArchive(normalized) == null) _focusNewWords.Add(normalized);
        }
        _focusUndo.Clear();
        ResetFocusAnswer();
        LookupPageHost.RowDefinitions = new RowDefinitions("Auto,*,Auto");
        _focusActions!.IsVisible = true;
        LookupResultCard.MinHeight = 0;
        ApplyLayout();
        PaintFocusStreak(_focusRound.CurrentStreak, _focusRound.CurrentTarget, animateNewest: false);
        if (_focusRound.HasCurrent && _focusRound.CurrentStep == StudyStep.Learn) ShowFocusLearnAnswer();
        else RefreshFocusLearningLabels();
        MemoryPresentFocusCard();
        SpeakFocusedWord();
    }

    private WordItem? FindArchive(string word) =>
        _allWords.FirstOrDefault(x => x.Word.Equals(word, StringComparison.OrdinalIgnoreCase));

    private void RefreshFocusLearningLabels()
    {
        if (_focusActions == null) return;
        var hasCard = _focusRound.HasCurrent;
        var learnCard = hasCard && !_focusRated && _focusRound.CurrentStep == StudyStep.Learn;
        var frontRecall = hasCard && !_focusAnswerRevealed;
        var finished = _focusRound.Total > 0 && _focusRound.IsFinished;
        var showNext = _focusRated;
        var showWrong = showNext && _focusLastRating != StudyRating.Forgot;
        var mode = T(_focusRound.Mode == StudyMode.FirstLearn ? "首次学习" : "复习");
        _focusHeading!.Text = !_wordFocusActive || _focusRound.Total == 0
            ? T("单词卡")
            : $"{mode} · {T("已完成")} {_focusRound.Completed}/{_focusRound.Total}";
        _focusFeedback!.Text = finished
            ? TF($"本轮 {_focusRound.Total} 个词全部通过「认识」 · 模糊 {_focusRound.Unsure} · 忘记 {_focusRound.Forgot}")
            : learnCard ? T("先看一遍释义，记住它，然后开始回忆。")
            : frontRecall ? T("瞬间想起词义，选「认识」 · 思考后想起词义，选「模糊」 · 想不起来选「忘记了」")
            : _focusRated ? _focusLastRating switch
            {
                StudyRating.Known => _focusShownCompleted
                    ? T("已记为认识，本轮完成。")
                    : TF($"已记为认识（{_focusShownStreak}/{_focusShownTarget}），本轮还会再出现。"),
                StudyRating.Unsure => T("已记为模糊，稍后本轮还会再出现一次。"),
                _ => T("已记为忘记，本轮会重新学习再测一次。")
            }
            : "";
        _focusSaveButton!.Content = FindCurrentArchive() == null ? "☆  C" : "★  C";
        _focusMasterButton!.Content = T("熟") + "  Del";
        _focusMasterButton.IsEnabled = !_focusRatingBusy && hasCard;
        _focusUndoButton!.IsEnabled = _focusUndo.Count > 0 && !_focusRatingBusy;
        _focusPronounceButton!.IsVisible = _wordFocusActive;
        ToolTip.SetTip(_focusPronounceButton, T("朗读单词") + "  A");
        _focusAiToggleButton!.IsEnabled = _focusAnswerRevealed;
        _focusStartRecallButton!.IsVisible = learnCard;
        _focusStartRecallButton.IsEnabled = learnCard && !_focusRatingBusy;
        _focusNextButton!.IsVisible = showNext;
        _focusWrongButton!.IsVisible = showWrong;
        foreach (var button in new[] { _focusKnownButton!, _focusUnsureButton!, _focusForgotButton! })
        { button.IsVisible = frontRecall; button.IsEnabled = frontRecall && !_focusRatingBusy; }
        _focusNextButton.IsEnabled = showNext && !_focusRatingBusy;
        _focusWrongButton.IsEnabled = showWrong && !_focusRatingBusy;
        _focusActions.ColumnDefinitions = new ColumnDefinitions("*,*,*");
        Grid.SetColumnSpan(_focusFeedback!, 3);
        ((TextBlock)((StackPanel)_focusStartRecallButton.Content!).Children[0]).Text = T("开始回忆") + "  Space";
        ((TextBlock)((StackPanel)_focusNextButton.Content!).Children[0]).Text = T(_focusRound.IsFinished ? "完成" : "下一词") + "  Space";
        ((TextBlock)((StackPanel)_focusWrongButton.Content!).Children[0]).Text = T("记错了") + "  E";
        foreach (var (button, label, key) in new[] { (_focusKnownButton!, "认识", "Q"), (_focusUnsureButton!, "模糊", "W"), (_focusForgotButton!, "忘记了", "E") })
        {
            ((TextBlock)((StackPanel)button.Content!).Children[0]).Text = T(label) + "  " + key;
        }
        RefreshPlanFocusLabels();
    }

    private void ResetFocusAnswer()
    {
        StopLearningSpeech();
        _focusRated = false; _focusAnswerRevealed = false;
        ResultTranslationText.IsVisible = ResultDefinitionText.IsVisible = ResultPosPanel.IsVisible = false;
        _focusDetails!.IsVisible = false; _focusDetails.Children.Clear();
        _focusAiVisible = false; _focusAiSection!.IsVisible = false;
    }

    // 首次学习的学习卡：释义直接可见，看过之后才开始回忆。
    private void ShowFocusLearnAnswer()
    {
        _focusRated = false;
        ShowFocusAnswer();
    }

    // 回忆卡的答案页：评价之后展示释义，可「记错了」改判。
    private void ShowFocusRecallAnswer()
    {
        ShowFocusAnswer();
    }

    private void ShowFocusAnswer()
    {
        _focusAnswerRevealed = true;
        ResultTranslationText.IsVisible = !string.IsNullOrWhiteSpace(ResultTranslationText.Text);
        ResultDefinitionText.IsVisible = string.IsNullOrWhiteSpace(ResultTranslationText.Text) && !string.IsNullOrWhiteSpace(ResultDefinitionText.Text);
        _focusDetails!.Children.Clear();
        if (_planCardActive && !string.IsNullOrWhiteSpace(ResultTranslationText.Text) && !string.IsNullOrWhiteSpace(ResultDefinitionText.Text))
            _focusDetails.Children.Add(new TextBlock { Text = ResultDefinitionText.Text, TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        AddLearningDetails(_focusDetails, _planCardActive ? _currentExpansion : FindCurrentArchive()?.AiResult ?? _currentExpansion);
        _focusDetails.IsVisible = _focusDetails.Children.Count > 0;
        RefreshFocusLearningLabels();
        if (_focusNextButton!.IsVisible) _focusNextButton.Focus();
        else _focusStartRecallButton?.Focus();
        RevealAnswerMotion();
    }

    // 兼容旧调用：把 Q/W/E 选择直接转成一次评分并展示答案。
    private Task ChooseFocusedWordAsync(int choice) => RateFocusedWordAsync(choice switch
    {
        0 => StudyRating.Known,
        1 => StudyRating.Unsure,
        _ => StudyRating.Forgot
    });

    /// <summary>学习卡看完：整个新词组先学完，再回到本轮队尾逐个回忆。</summary>
    private Task CompleteFocusLearnAsync()
    {
        if (!_wordFocusActive || _focusRatingBusy || !FocusCanNavigate || !_focusRound.HasCurrent
            || _focusRound.CurrentStep != StudyStep.Learn) return Task.CompletedTask;
        if (_planCardActive) _planLearningSession!.CompleteLearn();
        else _focusRound.CompleteLearn();
        MemorySaveRound();
        _focusRated = false;
        ResetFocusAnswer();
        return AdvanceFocusCardAsync();
    }

    private Task MoveFocusCardAsync(int epoch, double fromY, double toY, bool entering)
    {
        if (epoch != _focusEpoch || !_wordFocusActive) return Task.CompletedTask;
        Motion.SetPose(LookupResultCard, Motion.Pose(0, fromY, 1), entering ? 0 : 1);
        return Motion.ToPoseAsync(LookupResultCard, Motion.Pose(0, toY, 1), entering ? 1 : 0,
            entering ? Motion.Standard : Motion.Fast, entering ? Motion.Enter : Motion.Exit);
    }

    // 换词：旧卡上移淡出，新卡自下方上浮淡入；减少动态效果时即时切换。
    private async Task AdvanceFocusCardAsync()
    {
        if (_focusRatingBusy || !_wordFocusActive || !FocusCanNavigate) return;
        _focusRatingBusy = true;
        try
        {
            _focusRated = false;
            if (_focusRound.IsFinished)
            {
                if (_planCardActive) { FinishPlanCardRound(); MemoryTryTrainContext("计划轮结束"); return; }
                // 最后一个词评完仍保留释义与轮次统计，按「完成」才收起按钮。
                StopLearningSpeech(); RefreshFocusLearningLabels();
                // 一轮结束（T1 触发时机 ②）：只做一次资格检查，真正训练与否由冷却与样本门槛决定。
                MemoryTryTrainContext("单词卡轮结束");
                return;
            }
            var epoch = _focusEpoch;
            if (!ReduceMotionBox.IsChecked.GetValueOrDefault())
            {
                await MoveFocusCardAsync(epoch, 0, -14, entering: false);
                if (epoch != _focusEpoch || !_wordFocusActive) return;
                await LoadFocusWordAsync();
                if (epoch != _focusEpoch || !_wordFocusActive) return;
                await MoveFocusCardAsync(epoch, 14, 0, entering: true);
                Motion.SetPose(LookupResultCard, Motion.Rest, 1);
            }
            else
            {
                await LoadFocusWordAsync();
                Motion.SetPose(LookupResultCard, Motion.Rest, 1);
            }
        }
        finally { _focusRatingBusy = false; RefreshFocusLearningLabels(); }
    }

    private Task AdvanceFocusAsync()
    {
        if (!_focusRated || _focusRatingBusy || !_wordFocusActive || !FocusCanNavigate) return Task.CompletedTask;
        return AdvanceFocusCardAsync();
    }

    // 揭示释义：答案轻移淡入，避免整块内容瞬间出现。
    private void RevealAnswerMotion()
    {
        foreach (var control in new Control[] { ResultTranslationText, ResultDefinitionText, ResultPosPanel, _focusDetails! })
        {
            if (!control.IsVisible) { Motion.Reset(control); continue; }
            Motion.SetPose(control, Motion.Pose(0, 6, 1), 0);
            _ = Motion.ToPoseAsync(control, Motion.Rest, 1, Motion.Standard, Motion.Enter);
        }
    }

    /// <summary>一次回忆评价：认识出队，模糊/忘记回到本轮队尾，忘记在首次学习里回到学习卡。</summary>
    private async Task RateFocusedWordAsync(StudyRating rating)
    {
        if (MemoryTryCompletePending()) return;
        if (!_wordFocusActive || !FocusCanNavigate || _focusRatingBusy || _focusRated
            || !_focusRound.HasCurrent || _focusRound.CurrentStep != StudyStep.Recall) return;
        if (_planCardActive) { await RatePlanFocusedWordAsync(rating); return; }
        _focusRatingBusy = true;
        string? error = null;
        var word = _focusRound.Current;
        var step = _focusRound.CurrentStep;
        // StudyRound 要到 Commit() 之后才更新连击：作答前的连击必须在提交前取（规格书 §2）。
        var recognitionBefore = _focusRound.CurrentStreak;
        var checkpoint = _focusRound.CaptureCheckpoint();
        var result = _focusRound.Commit(rating);
        try
        {
            var focusIdentity = MemoryFocusIdentity(word);
            MemoryRated(MemorySurface.Focus, focusIdentity, rating, recognitionBefore, result.Streak,
                result.Completed ? MemoryFocusCommitMode(word) : null);
            var catalogWord = _wordFocusSnapshot?.Page == "ielts" ? _ieltsCatalog?.Find(word) : null;
            if (FindArchive(word) == null && catalogWord != null)
            {
                // 只在词表里认词时不自动建立个人档案；模糊/忘记记入错词本。
                if (rating != StudyRating.Known) _learningProgress.Errors.Add(catalogWord.Id);
                // JSON materializes only after the response / canonical journal is durable.
            }
            else
            {
                if (FindArchive(word) == null) AddCurrentWordToVocab();
                var item = FindArchive(word) ?? throw new InvalidOperationException(T("未能保存词条，请重试。"));
                // RefreshWords deliberately updates existing WordItem objects in
                // place; retain the scalar revision before that refresh.
                var previousRevision = item.Archive.Revision;
                ApplyFocusRating(item, rating, result.Completed);
                if (_allWords.Single(x => x.Id == item.Id).Archive.Revision != previousRevision)
                    _focusUndo.Push((item.Id, rating, step));
            }
            var jsonMutations = catalogWord is not null && FindArchive(word) is null
                ? new[] { MemoryIeltsSnapshot() } : Array.Empty<PendingMutation>();
            if (result.Completed) MemoryCommitCard(MemorySurface.Focus, focusIdentity, MemoryFocusCommitMode(word), jsonMutations);
            else if (jsonMutations.Length > 0) _memoryStore!.EnqueueMutations(jsonMutations);
            MemoryReplayJournal();
            MemorySaveRound();
            _focusLastRating = rating;
            _focusRatedWord = word;
            _focusRated = true;
            _focusShownStreak = result.Streak;
            _focusShownTarget = result.Target;
            _focusShownCompleted = result.Completed;
            PaintFocusStreak(result.Streak, result.Target, animateNewest: true);
            ShowFocusRecallAnswer();
            SetStatus(rating switch
            {
                StudyRating.Known => result.Completed
                    ? T("已记下这次重逢。")
                    : TF($"已记为认识（{result.Streak}/{result.Target}），本轮还会再出现。"),
                StudyRating.Unsure => T("已记为模糊，本轮稍后再见。"),
                _ => T("已记为忘记，稍后重新学习。")
            });
        }
        catch (Exception ex)
        {
            if (MemoryTryPresentedCard(MemorySurface.Focus, MemoryFocusIdentity(word), out var committedCard)
                && _committedWordKeys.Contains(committedCard.Key.Key)
                && _memoryStore!.LoadPendingMutations().Count > 0)
            {
                _memoryPendingCompletion = () =>
                {
                    _focusLastRating = rating; _focusRatedWord = word; _focusRated = true;
                    _focusShownStreak = result.Streak; _focusShownTarget = result.Target; _focusShownCompleted = result.Completed;
                    ShowFocusRecallAnswer(); MemorySaveRound(); SetStatus(T("已记下这次重逢。"));
                };
                error = T("本次复习未完成，请重试：") + ex.Message;
                return;
            }
            checkpoint.Restore();
            if (MemoryTryPresentedCard(MemorySurface.Focus, MemoryFocusIdentity(word), out var failedCard))
                _memory?.OnUndone(failedCard.Key, failedCard.PresentationId);
            if (_focusUndo.TryPeek(out var undo) && FindArchive(word)?.Id == undo.Id)
            {
                _vocabService.UndoLastLearningAction(undo.Id);
                _focusUndo.Pop(); RefreshWords();
            }
            error = T("本次复习未完成，请重试：") + ex.Message;
        }
        finally
        {
            if (error != null) ResetFocusAnswer();
            _focusRatingBusy = false; RefreshFocusLearningLabels();
            if (error != null) _focusFeedback!.Text = error;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 写旧列（兼容投影 + 既有界面显示，T4）：与 ApplyReviewRating 同一分工——
    /// 评分路径保留旧列写入，排期的唯一来源是 fsrs_cards。
    /// </summary>
    private void ApplyFocusRating(WordItem item, StudyRating rating, bool completed)
    {
        if (rating == StudyRating.Known)
        {
            // 中间的认识只累计连击，只有本轮真正完成时才推进一次调度。
            if (!completed) return;
            _vocabService.ExecuteBatch([item.Id], "review");
        }
        else if (rating == StudyRating.Unsure) _vocabService.MarkUnsure(item.Id);
        else _vocabService.MarkForgot(item.Id);
        RefreshWords();
        UpdateReviewBadge();
    }

    /// <summary>记错了：回滚上一次评价并改判为忘记，本轮重新学习后再测。</summary>
    private async Task ReclassifyFocusAsync()
    {
        if (!_wordFocusActive || _focusRatingBusy || _focusRated == false
            || _focusLastRating == StudyRating.Forgot || (!_planCardActive && !_focusRound.HasCurrent) || !FocusCanNavigate) return;
        if (_planCardActive) { await ReclassifyPlanFocusAsync(); return; }
        var word = _focusRatedWord ?? _focusRound.Current;
        _focusRatingBusy = true;
        string? error = null;
        try
        {
            var archive = FindArchive(word);
            (long Id, StudyRating Rating, StudyStep Step)? undone = null;
            if (archive != null && _focusUndo.Count > 0 && _focusUndo.Peek().Id == archive.Id)
            {
                undone = _focusUndo.Pop();
                if (!_vocabService.UndoLastLearningAction(archive.Id))
                { _focusUndo.Push(undone.Value); return; }
            }
            var previous = _focusRound.UndoLast();
            if (archive != null)
            {
                _vocabService.MarkForgot(archive.Id);
                _focusUndo.Push((archive.Id, StudyRating.Forgot, previous?.Step ?? StudyStep.Recall));
                RefreshWords(); UpdateReviewBadge();
            }
            if (_focusRound.HasCurrent)
            {
                var forgot = _focusRound.Commit(StudyRating.Forgot);
                _focusShownStreak = forgot.Streak;
                _focusShownTarget = forgot.Target;
                _focusShownCompleted = false;
                PaintFocusStreak(forgot.Streak, forgot.Target, animateNewest: true);
            }
            // 改判：同一 presentation 追加修正事件，presentationId 不变（规格书 §2）。
            MemoryRevised(MemorySurface.Focus, MemoryFocusIdentity(word), _focusLastRating, StudyRating.Forgot);
            _focusLastRating = StudyRating.Forgot;
            _focusRatedWord = word;
            ShowFocusRecallAnswer();
            SetStatus(T("已改判为忘记，稍后重新学习。"));
        }
        catch (Exception ex) { error = T("改判失败：") + ex.Message; }
        finally
        {
            _focusRatingBusy = false; RefreshFocusLearningLabels();
            if (error != null) _focusFeedback!.Text = error;
        }
        await Task.CompletedTask;
    }

    /// <summary>标记已掌握：直接出队完成本轮，可用 ⌥Space 撤销。</summary>
    private async Task MasterFocusAsync()
    {
        if (_planCardActive) return;
        if (!_wordFocusActive || !FocusCanNavigate || _focusRatingBusy || !_focusRound.HasCurrent) return;
        var word = _focusRound.Current;
        var step = _focusRound.CurrentStep;
        _focusRatingBusy = true;
        string? error = null;
        try
        {
            if (FindArchive(word) == null) AddCurrentWordToVocab();
            var item = FindArchive(word) ?? throw new InvalidOperationException(T("未能保存词条，请重试。"));
            _vocabService.ExecuteBatch([item.Id], "master");
            _focusUndo.Push((item.Id, StudyRating.Known, step));
            var mastered = _focusRound.CompleteCurrent();
            _focusShownStreak = mastered.Streak;
            _focusShownTarget = mastered.Target;
            _focusShownCompleted = mastered.Completed;
            PaintFocusStreak(mastered.Streak, mastered.Target, animateNewest: true);
            _focusLastRating = StudyRating.Known;
            RefreshWords(); UpdateReviewBadge();
        }
        catch (Exception ex) { error = T("本次复习未完成，请重试：") + ex.Message; }
        finally
        {
            _focusRatingBusy = false; RefreshFocusLearningLabels();
            if (error != null) _focusFeedback!.Text = error;
        }
        if (error == null) await AdvanceFocusCardAsync();
    }

    private async Task UndoFocusRatingAsync()
    {
        if (_planCardActive) { await UndoPlanFocusAsync(); return; }
        if (_focusRatingBusy || _focusUndo.Count == 0 || !FocusCanNavigate) return;
        _focusRatingBusy = true;
        string? error = null;
        try
        {
            var previous = _focusUndo.Peek();
            var undoneWord = _allWords.FirstOrDefault(w => w.Id == previous.Id);
            // 长期层是**提交点**（与 UndoReviewFromLearningPage 同一口径）：先撤销持久轨迹并失效
            // 已定稿的 canonical（规格书 §2 / §4），成功后才改旧列与轮内撤销栈；失败则轮内状态原样回滚。
            var checkpoint = _focusRound.CaptureCheckpoint();
            _focusRound.UndoLast();
            if (undoneWord is not null)
            {
                try { MemoryUndone(MemorySurface.Focus, MemoryFocusIdentity(undoneWord.Word)); }
                catch { checkpoint.Restore(); throw; }
            }
            _focusUndo.Pop();
            if (!_vocabService.UndoLastLearningAction(previous.Id))
                MemoryDiagnostic("撤销：旧列没有可回退的评分日志（id=" + previous.Id + "），长期层撤销已完成。");
            _focusRated = false;
            _focusRatedWord = null;
            RefreshWords(); UpdateReviewBadge();
            await LoadFocusWordAsync();
        }
        catch (Exception ex) { error = T("撤销失败：") + ex.Message; }
        finally
        {
            _focusRatingBusy = false; RefreshFocusLearningLabels();
            if (error != null) _focusFeedback!.Text = error;
        }
    }

    private async Task LoadFocusWordAsync()
    {
        if (_planCardActive) { await LoadPlanFocusWordAsync(); return; }
        LookupInput.Text = _focusRound.HasCurrent ? _focusRound.Current : ResultWordText.Text!;
        await PerformLookupAsync();
        if (!_wordFocusActive) return;
        ResetFocusAnswer();
        _focusRatedWord = null;
        _focusAiVisible = false; _focusAiSection!.IsVisible = false;
        _focusEntryButton!.IsVisible = false;
        foreach (var decoration in _focusDecorations) decoration.IsVisible = false;
        PaintFocusStreak(_focusRound.CurrentStreak, _focusRound.CurrentTarget, animateNewest: false);
        if (_focusRound.HasCurrent && _focusRound.CurrentStep == StudyStep.Learn) ShowFocusLearnAnswer();
        else RefreshFocusLearningLabels();
        MemoryPresentFocusCard();
        _focusBackButton!.Focus();
        SpeakFocusedWord();
    }
}
