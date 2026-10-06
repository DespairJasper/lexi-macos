using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi;

public partial class MainWindow
{
    private Grid _typingSetupView = null!, _typingPlayArea = null!, _typingWordArea = null!;
    private StackPanel _typingWordBody = null!, _typingFinished = null!;
    private Grid _typingFooter = null!;
    private TextBlock _typingProgress = null!;
    private Button _typingReplay = null!, _typingConfirm = null!;

    private void ConfigureTypingPage()
    {
        _typingPage = new Grid { Name = "PageTyping", IsVisible = false };
        _typingSetupView = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 24, Margin = new Thickness(28, 24) };
        _typingSetupView.Children.Add(LearningLabel("打字练习", 26));
        var setup = new StackPanel { Spacing = 18, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        _typingSources.AddRange(new[] { ("archive", "我的生词本"), ("due", "今日重逢"), ("dictionary", "全部离线词典"), ("ielts", "全部 IELTS 词汇"), ("synonyms", "全部 IELTS 同义替换") });
        _typingSources.AddRange(_ieltsCatalog!.Sections.Select(s => (s.Id, s.Title)));
        _typingSource = new ComboBox { Name = "TypingSource", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = _typingSources.Select(s => T(s.Title)).ToList(), SelectedIndex = 0 };
        _typingMode = new ComboBox { Name = "TypingMode", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { T("淡写"), T("默写") }, SelectedIndex = _learningProgress.TypingHints ? 0 : 1 };
        setup.Children.Add(LearningLabel("选择词库", 12)); setup.Children.Add(_typingSource);
        setup.Children.Add(_typingMode);
        _typingRoundOptions = CreateRoundControls("Typing"); setup.Children.Add(_typingRoundOptions.View);
        _typingStart = LearningButton("开始练习", "TypingStartBtn", true);
        _typingStart.Click += async (_, _) => await StartTypingSourceAsync(); setup.Children.Add(_typingStart);
        var scroll = new ScrollViewer { Content = setup, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); _typingSetupView.Children.Add(scroll); _typingPage.Children.Add(_typingSetupView);

        _typingPlayArea = new Grid { Name = "TypingPlayArea", RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(36, OperatingSystem.IsMacOS() ? 52 : 24, 36, 24), IsVisible = false };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        _typingProgress = ContentText("", 14); _typingProgress.Name = "TypingProgress"; top.Children.Add(_typingProgress);
        var save = new Button { Name = "TypingSaveBtn", Content = "☆", Classes = { "ghost" }, FontSize = 22 };
        ToolTip.SetTip(save, T("收藏")); save.Click += (_, _) => { if (_typingSession.Current is { } word) SaveTypingWord(word); _typingInput.Focus(); };
        Grid.SetColumn(save, 2); top.Children.Add(save); _typingPlayArea.Children.Add(top);

        _typingWordBody = new StackPanel { Spacing = 22, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _typingWordArea = new Grid { Name = "TypingWordArea", RowDefinitions = new RowDefinitions("Auto,Auto"), MinHeight = 70 };
        _typingLetters = new TextBlock { Name = "TypingLetters", FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = 44, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _typingInput = new TextBox { Name = "TypingInput", Classes = { "learning-input" }, FontFamily = _typingLetters.FontFamily,
            FontSize = 44, MinWidth = 0, MinHeight = 0, Padding = default, BorderThickness = default, Background = Brushes.Transparent, Foreground = Brushes.Transparent,
            CaretBrush = new SolidColorBrush(Color.Parse("#D7773B")), SelectionBrush = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, IsUndoEnabled = false };
        _typingInput.TextChanged += (_, _) => HandleTypingInput();
        _typingInput.PastingFromClipboard += (_, e) => e.Handled = true;
        _typingWordArea.Children.Add(_typingLetters); _typingWordArea.Children.Add(_typingInput);
        _typingFeedback = ContentText("", 44); _typingFeedback.Name = "TypingFeedback"; _typingFeedback.FontFamily = _typingLetters.FontFamily;
        _typingFeedback.Foreground = new SolidColorBrush(Color.Parse("#169E85")); _typingFeedback.TextAlignment = TextAlignment.Center;
        Grid.SetRow(_typingFeedback, 1); _typingWordArea.Children.Add(_typingFeedback);
        _typingMeaning = ContentText("", 18); _typingMeaning.Name = "TypingMeaning"; _typingMeaning.TextAlignment = TextAlignment.Center;
        _typingMeaning.MaxWidth = 760;
        _typingWordBody.Children.Add(_typingWordArea); _typingWordBody.Children.Add(_typingMeaning);
        Grid.SetRow(_typingWordBody, 1); _typingPlayArea.Children.Add(_typingWordBody);

        _typingFooter = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 14 };
        var exit = LearningPill("✕  Esc", "TypingExitBtn"); exit.HorizontalAlignment = HorizontalAlignment.Left;
        exit.Click += (_, _) => StopTypingRound(); _typingFooter.Children.Add(exit);
        _typingReplay = LearningPill("▷  ⌥T", "TypingReplayBtn");
        _typingReplay.Click += (_, _) => { ReplayTypingWord(true); _typingInput.Focus(); };
        Grid.SetColumn(_typingReplay, 1); _typingFooter.Children.Add(_typingReplay);
        _typingConfirm = LearningPill("✓  ↵", "TypingConfirmBtn"); _typingConfirm.HorizontalAlignment = HorizontalAlignment.Right;
        _typingConfirm.Click += (_, _) => ConfirmTypingWord(); Grid.SetColumn(_typingConfirm, 2); _typingFooter.Children.Add(_typingConfirm);
        Grid.SetRow(_typingFooter, 2); _typingPlayArea.Children.Add(_typingFooter);

        _typingFinished = new StackPanel { Name = "TypingFinished", Spacing = 20, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
        _typingFinished.Children.Add(LearningLabel("本轮已完成", 28));
        _typingStats = ContentText("", 16); _typingStats.Name = "TypingStats"; _typingStats.TextAlignment = TextAlignment.Center;
        _typingFinished.Children.Add(_typingStats);
        var doneActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var restart = LearningButton("重新练习", "TypingRestartBtn"); restart.Click += (_, _) => BeginTypingRound(_typingDeck, _typingDeckTitle);
        var errors = LearningButton("错词重练", "TypingErrorsBtn"); errors.Click += (_, _) =>
        { var words = _typingDeck.Where(w => _typingSession.ErrorIds.Contains(w.Id)).ToList(); if (words.Count > 0) BeginTypingRound(words, _typingDeckTitle); };
        var back = LearningButton("返回", "TypingDoneBtn"); back.Click += (_, _) => StopTypingRound();
        doneActions.Children.Add(restart); doneActions.Children.Add(errors); doneActions.Children.Add(back);
        _typingFinished.Children.Add(doneActions); Grid.SetRow(_typingFinished, 1); _typingPlayArea.Children.Add(_typingFinished);
        _typingPage.Children.Add(_typingPlayArea);
        _typingWordBody.PointerPressed += (_, _) => _typingInput.Focus();
        _typingPage.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!_typingPlaying || !FocusCanNavigate) return;
            if (e.Key == Key.Escape) { e.Handled = true; StopTypingRound(); }
            else if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.T or Key.Space)
            { e.Handled = true; ReplayTypingWord(true); _typingInput.Focus(); }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; ConfirmTypingWord(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _typingPage.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (_typingPlaying && e.Key == Key.Enter) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    private Button LearningPill(string text, string name) => new()
    { Name = name, Content = text, Classes = { "ghost", "learning-action" }, CornerRadius = new CornerRadius(24), Padding = new Thickness(18, 8), FontSize = 15 };

    private void SaveTypingWord(LearningWord word)
    {
        try
        {
            _vocabService.AddWord(word.Word, word.Phonetic, word.Pos + " " + word.Meaning, word.Extra);
            RefreshWords();
            if (_ieltsCatalog?.Find(word.Word) is { } source)
            {
                var item = _allWords.First(w => w.Word.Equals(word.Word, StringComparison.OrdinalIgnoreCase));
                if (item.AiResult == null) _vocabService.SaveExpansion(item.Id, CreateIeltsExpansion(source));
            }
        }
        catch (Exception ex) { SetStatus(T("收藏失败：") + ex.Message); }
    }
}
