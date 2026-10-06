using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Lexi;

public partial class MainWindow
{
    private IeltsCatalog? _ieltsCatalog;
    private LearningProgress _learningProgress = new();
    private string LearningProgressPath => Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "ielts-learning.json");
    private Grid? _ieltsPage, _typingPage;
    private ComboBox _ieltsKind = null!, _ieltsSection = null!, _ieltsPracticeMode = null!;
    private TextBox _ieltsSearch = null!;
    private CheckBox _ieltsShowMeaning = null!, _ieltsErrorsOnly = null!;
    private StackPanel _ieltsRows = null!;
    private WrapPanel _ieltsWordActions = null!;
    private TextBlock _ieltsSummary = null!, _ieltsPageNumber = null!;
    private Button _ieltsPrevious = null!, _ieltsNext = null!;
    private int _ieltsListPage;
    private bool _learningConfiguring;
    private readonly List<(Control Control, string Text)> _learningLabels = [];
    private readonly DispatcherTimer _ieltsFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _writingSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private List<LearningWord> _synonymQuestions = [];
    private int _synonymCursor;
    private int _learningNavigationVersion;
    private StackPanel _synonymPanel = null!;
    private Border _synonymCard = null!;
    private TextBlock _synonymPrompt = null!, _synonymFeedback = null!;
    private TextBox _synonymWord = null!, _synonymInput = null!;
    private bool _synonymCorrect;

    private Button LearningButton(string text, string? name = null, bool primary = false)
    {
        var button = new Button { Content = T(text), Name = name, Classes = { primary ? "primary" : "secondary" }, Padding = new Thickness(12, 8), FontSize = 13 };
        if (_learningConfiguring) _learningLabels.Add((button, text));
        return button;
    }
    private TextBlock LearningLabel(string text, double size = 14)
    {
        var label = new TextBlock { Text = T(text), FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (_learningConfiguring) _learningLabels.Add((label, text)); return label;
    }
    private static TextBlock ContentText(string text, double size = 14) => new()
    { Text = text, FontSize = size, LineHeight = size * 1.65, TextWrapping = TextWrapping.Wrap };
    private static Border LearningCard(Control body) => new()
    { Classes = { "card" }, Padding = new Thickness(20), Child = body, CornerRadius = new CornerRadius(16) };

    private void ConfigureLearningPages()
    {
        _learningConfiguring = true;
        try
        {
            _ieltsCatalog = IeltsCatalog.Load();
            try { _learningProgress = LearningProgress.Load(LearningProgressPath); }
            catch (Exception ex)
            {
                var preserved = LearningProgressPath + ".unreadable-" + Guid.NewGuid().ToString("N");
                File.Copy(LearningProgressPath, preserved);
                SetStatus(T("学习记录读取失败，原文件已保留：") + preserved + " · " + ex.Message);
            }
            ConfigureIeltsPage(); ConfigureTypingPage();
            var host = (Panel)LookupPageHost.Parent!;
            host.Children.Add(_ieltsPage!); host.Children.Add(_typingPage!);
            NavIelts.Click += (_, _) => { if (FocusCanNavigate) ShowPage("ielts"); };
            NavTyping.Click += (_, _) => { if (FocusCanNavigate) ShowPage("typing"); };
            var vocabTyping = LearningButton("打字练习", "VocabTypingBtn");
            ((StackPanel)VocabPageTitle.Parent!).Children.Add(vocabTyping);
            vocabTyping.Click += (_, _) => OpenTypingWords(_filteredWords.Select(ArchiveLearningWord).ToList(), T("当前筛选词汇"));
            _ieltsFilterTimer.Tick += (_, _) => { _ieltsFilterTimer.Stop(); _ieltsListPage = 0; RenderIeltsPage(); };
            _writingSaveTimer.Tick += (_, _) => { _writingSaveTimer.Stop(); SaveLearningProgress(); };
            Closed += (_, _) => { _ieltsFilterTimer.Stop(); _writingSaveTimer.Stop(); if (_writingDirty) SaveLearningProgress(); _wordAudio.Dispose(); };
        }
        catch (Exception ex) { SetStatus(T("IELTS 内容加载失败：") + ex.Message); NavIelts.IsEnabled = NavTyping.IsEnabled = false; }
        finally { _learningConfiguring = false; }
        RefreshLearningLabels();
    }

    private void ConfigureIeltsPage()
    {
        _ieltsPage = new Grid { Name = "PageIelts", RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(28, 24, 28, 16), IsVisible = false, RowSpacing = 14 };
        var hero = new StackPanel { Spacing = 6 };
        hero.Children.Add(LearningLabel("IELTS 学习专题", 26));
        _ieltsSummary = ContentText("", 12); _ieltsSummary.Classes.Add("muted"); hero.Children.Add(_ieltsSummary);
        _ieltsPage.Children.Add(hero);
        var tools = new StackPanel { Spacing = 10 };
        var selectors = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        _ieltsKind = new ComboBox { Name = "IeltsKind", HorizontalAlignment = HorizontalAlignment.Stretch };
        _ieltsKind.ItemsSource = new[] { T("词汇真经"), T("听力 179"), T("阅读同义替换"), T("英美拼写规范"), T("写作 100 句"), T("语法与听力资料") };
        _ieltsKind.SelectedIndex = 0;
        _ieltsSection = new ComboBox { Name = "IeltsSection", HorizontalAlignment = HorizontalAlignment.Stretch };
        selectors.Children.Add(_ieltsKind); Grid.SetColumn(_ieltsSection, 1); selectors.Children.Add(_ieltsSection); tools.Children.Add(selectors);
        _ieltsSearch = new TextBox { Name = "IeltsSearch", Watermark = T("搜索单词、释义或同义词"), Padding = new Thickness(10, 7) };
        tools.Children.Add(_ieltsSearch);
        var actions = _ieltsWordActions = new WrapPanel { Orientation = Orientation.Horizontal };
        var practiceOptions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,2*"), ColumnSpacing = 12 };
        _ieltsPracticeMode = new ComboBox { Name = "IeltsPracticeMode", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { T("单词卡认词"), T("淡写"), T("默写") }, SelectedIndex = 0, VerticalAlignment = VerticalAlignment.Bottom };
        _ieltsRoundOptions = CreateRoundControls("Ielts");
        practiceOptions.Children.Add(_ieltsPracticeMode); Grid.SetColumn(_ieltsRoundOptions.View, 1); practiceOptions.Children.Add(_ieltsRoundOptions.View);
        tools.Children.Add(practiceOptions);
        var start = LearningButton("开始学习", "IeltsPracticeStartBtn", true);
        var chapterAudio = LearningButton("章节音频", "IeltsChapterAudioBtn");
        var stop = LearningButton("停止播放"); stop.Click += (_, _) => StopLearningSpeech();
        var synonyms = LearningButton("同义替换练习", "IeltsSynonymsBtn");
        foreach (var button in new[] { start, chapterAudio, stop, synonyms }) { button.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(button); }
        _ieltsShowMeaning = new CheckBox { Name = "IeltsShowMeaning", Content = T("显示释义"), IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
        _ieltsErrorsOnly = new CheckBox { Name = "IeltsErrorsOnly", Content = T("仅看错词"), Margin = new Thickness(0, 0, 14, 0) };
        var copyErrors = LearningButton("复制错词", "IeltsCopyErrorsBtn");
        actions.Children.Add(_ieltsShowMeaning); actions.Children.Add(_ieltsErrorsOnly); actions.Children.Add(copyErrors);
        tools.Children.Add(actions); ConfigureChapterAudio(tools); Grid.SetRow(tools, 1); _ieltsPage.Children.Add(tools);
        _ieltsRows = new StackPanel { Name = "IeltsRows", Spacing = 12 };
        var scroll = new ScrollViewer { Name = "IeltsScroll", Content = _ieltsRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); _ieltsPage.Children.Add(scroll);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        _ieltsPrevious = LearningButton("上一页"); _ieltsNext = LearningButton("下一页"); _ieltsPageNumber = ContentText("", 12);
        _ieltsPageNumber.HorizontalAlignment = HorizontalAlignment.Center; _ieltsPageNumber.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_ieltsPrevious); Grid.SetColumn(_ieltsPageNumber, 1); footer.Children.Add(_ieltsPageNumber); Grid.SetColumn(_ieltsNext, 2); footer.Children.Add(_ieltsNext);
        Grid.SetRow(footer, 3); _ieltsPage.Children.Add(footer);
        _ieltsPrevious.Click += (_, _) => { _ieltsListPage--; RenderIeltsPage(); scroll.Offset = default; };
        _ieltsNext.Click += (_, _) => { _ieltsListPage++; RenderIeltsPage(); scroll.Offset = default; };
        _ieltsKind.SelectionChanged += (_, _) => { if (_learningConfiguring) return; PopulateIeltsSections(); _ieltsListPage = 0; RenderIeltsPage(); };
        _ieltsSection.SelectionChanged += (_, _) =>
        {
            if (_learningConfiguring) return;
            if (_ieltsSection.SelectedItem is LearningSection section) { _learningProgress.SelectedSection = section.Id; SaveLearningProgress(); }
            _ieltsListPage = 0; RenderIeltsPage();
        };
        _ieltsSearch.TextChanged += (_, _) => { _ieltsFilterTimer.Stop(); _ieltsFilterTimer.Start(); };
        _ieltsShowMeaning.IsCheckedChanged += (_, _) => RenderIeltsPage();
        _ieltsErrorsOnly.IsCheckedChanged += (_, _) => { _ieltsListPage = 0; RenderIeltsPage(); };
        start.Click += async (_, _) =>
        {
            var words = IeltsFilteredWords();
            if (_ieltsPracticeMode.SelectedIndex == 0) await StartIeltsCardsAsync(words);
            else
            {
                var selected = SelectLearningRound(words, _ieltsRoundOptions);
                _typingMode.SelectedIndex = _ieltsPracticeMode.SelectedIndex - 1;
                ShowPage("typing"); BeginTypingRound(selected, (_ieltsSection.SelectedItem as LearningSection)?.Title ?? "IELTS");
            }
        };
        chapterAudio.Click += (_, _) =>
        {
            var section = _ieltsSection.SelectedItem as LearningSection;
            if (IeltsCatalog.ResolveAsset(section?.AudioPath) is { } path) { _wordAudio.Play(section!.Title, path); ShowChapterTimeline(); }
            else SetStatus(T("本组没有整章音频，可播放单词读音。"));
        };
        synonyms.Click += (_, _) => BeginSynonymPractice();
        copyErrors.Click += async (_, _) =>
        {
            if (Clipboard != null) await Clipboard.SetTextAsync(string.Join("\n", _ieltsCatalog!.AllWords.Where(w => _learningProgress.Errors.Contains(w.Id)).Select(w => w.Word + " " + w.Meaning)));
            SetStatus(T("错词已复制。"));
        };
        ConfigureSynonymPractice(); PopulateIeltsSections();
    }

    private void PopulateIeltsSections()
    {
        var kind = _ieltsKind.SelectedIndex switch { 1 => "listening", 2 => "reading", 3 => "spelling", _ => "vocabulary" };
        var sections = _ieltsCatalog!.Sections.Where(s => s.Kind == kind).ToList();
        var guard = _learningConfiguring; _learningConfiguring = true;
        _ieltsSection.ItemsSource = sections;
        _ieltsSection.SelectedItem = sections.FirstOrDefault(s => s.Id == _learningProgress.SelectedSection) ?? sections.First();
        _ieltsSection.IsVisible = _ieltsKind.SelectedIndex < 4;
        _learningConfiguring = guard;
    }
    private List<LearningWord> IeltsFilteredWords()
    {
        if (_ieltsKind.SelectedIndex >= 4) return [];
        var words = (_ieltsSection.SelectedItem as LearningSection)?.Entries ?? [];
        var query = _ieltsSearch.Text?.Trim() ?? "";
        return words.Where(w => (!_ieltsErrorsOnly.IsChecked.GetValueOrDefault() || _learningProgress.Errors.Contains(w.Id))
            && (query.Length == 0 || (string.Join(" ", w.Words) + " " + w.Meaning + " " + string.Join(" ", w.Synonyms)).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
    }
    private void RenderIeltsPage()
    {
        if (_ieltsRows == null || _ieltsCatalog == null || _learningConfiguring) return;
        _ieltsWordActions.IsVisible = _ieltsKind.SelectedIndex < 4;
        _ieltsRoundOptions.View.IsVisible = _ieltsPracticeMode.IsVisible = _ieltsKind.SelectedIndex < 4;
        _ieltsSearch.IsVisible = _ieltsKind.SelectedIndex != 5;
        _synonymPanel.IsVisible = false;
        if (_ieltsKind.SelectedIndex == 4) { RenderWritingPage(); return; }
        if (_ieltsKind.SelectedIndex == 5) { RenderIeltsResources(); return; }
        var words = IeltsFilteredWords();
        _ieltsListPage = Math.Clamp(_ieltsListPage, 0, Math.Max(0, (words.Count - 1) / 30));
        _ieltsRows.Children.Clear();
        var section = _ieltsSection.SelectedItem as LearningSection;
        _ieltsSummary.Text = section?.Description + "  ·  " + TF($"{words.Count} 个词 · 已正确拼写 {words.Count(w => _learningProgress.Typed.Contains(w.Id))} 个");
        foreach (var word in words.Skip(_ieltsListPage * 30).Take(30)) _ieltsRows.Children.Add(BuildIeltsRow(word));
        if (words.Count == 0) _ieltsRows.Children.Add(LearningLabel("没有匹配词条。"));
        _ieltsPageNumber.Text = $"{_ieltsListPage + 1} / {Math.Max(1, (words.Count + 29) / 30)}";
        _ieltsPrevious.IsEnabled = _ieltsListPage > 0; _ieltsNext.IsEnabled = (_ieltsListPage + 1) * 30 < words.Count;
    }
    private Border BuildIeltsRow(LearningWord word)
    {
        var body = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        var title = ContentText(string.Join(" / ", word.Words), 20); title.FontWeight = FontWeight.Medium;
        header.Children.Add(title);
        var play = new Button { Content = "▷", Classes = { "ghost" }, Padding = new Thickness(8) };
        ToolTip.SetTip(play, T("朗读单词"));
        play.Click += (_, _) => _wordAudio.Play(word.Word, IeltsCatalog.ResolveAsset(word.AudioPath)); Grid.SetColumn(play, 1); header.Children.Add(play);
        body.Children.Add(header);
        body.Children.Add(ContentText(word.Phonetic + (word.Group > 0 ? "  ·  " + TF($"词群 {word.Group}") : ""), 12));
        if (_ieltsShowMeaning.IsChecked.GetValueOrDefault()) body.Children.Add(ContentText(word.Pos + "  " + word.Meaning));
        if (word.Example.Length > 0) body.Children.Add(ContentText(word.Example));
        if (word.Synonyms.Count > 0) body.Children.Add(ContentText(T("同义替换") + "  " + string.Join(" · ", word.Synonyms)));
        if (word.Extra.Length > 0) body.Children.Add(ContentText(word.Extra, 12));
        var actions = new WrapPanel();
        Button Action(string text, Action action)
        {
            var b = new Button { Content = T(text), Classes = { "ghost" }, FontSize = 12, Padding = new Thickness(6, 5), Margin = new Thickness(0, 0, 10, 0) };
            b.Click += (_, _) => action(); actions.Children.Add(b); return b;
        }
        var saved = _allWords.Any(w => w.Word.Equals(word.Word, StringComparison.OrdinalIgnoreCase));
        var save = Action(saved ? "已收藏" : "收藏", () => SaveIeltsWord(word)); save.IsEnabled = !saved;
        Action("单词卡", async () => await StartIeltsCardsAsync([word]));
        Action("查词", async () => { ShowPage("lookup"); LookupInput.Text = word.Word; await PerformLookupAsync(); });
        Action("剑桥词典", () => OpenLearningResource("https://dictionary.cambridge.org/dictionary/english-chinese-simplified/" + Uri.EscapeDataString(word.Word)));
        Action("复制", async () => { if (Clipboard != null) await Clipboard.SetTextAsync(word.Word + " " + word.Pos + " " + word.Meaning); });
        body.Children.Add(actions); return LearningCard(body);
    }
    private static LlmResult CreateIeltsExpansion(LearningWord word) => new()
    {
        Examples = word.Example.Length > 0 ? [new ExampleItem(word.Example, "")] : [],
        Synonyms = [.. word.Synonyms],
        Phrases = word.Extra.Length > 0 ? [new PhraseItem(word.Extra, "")] : []
    };
    private void SaveIeltsWord(LearningWord word)
    {
        try
        {
            _vocabService.AddWord(word.Word, word.Phonetic, word.Pos + " " + word.Meaning, word.Extra);
            var archived = _vocabService.GetAllWords().First(w => w.Word.Equals(word.Word, StringComparison.OrdinalIgnoreCase));
            if (archived.AiResult == null) _vocabService.SaveExpansion(archived.Id, CreateIeltsExpansion(word));
            RefreshWords(); RenderIeltsPage(); SetStatus(T("已加入词汇档案。"));
        }
        catch (Exception ex) { SetStatus(T("收藏失败：") + ex.Message); }
    }
    private async Task StartIeltsCardsAsync(List<LearningWord> words)
    {
        words = SelectLearningRound(words, _ieltsRoundOptions);
        if (words.Count == 0) { SetStatus(T("没有可练习词条。")); return; }
        var snapshot = CaptureFocusSnapshot();
        ShowPage("lookup"); LookupInput.Text = words[0].Word;
        var navigationVersion = _learningNavigationVersion;
        var lookup = PerformLookupAsync();
        var lookupVersion = _lookupVersion;
        await lookup;
        // A newer query or navigation owns the UI, even if the user returns here.
        if (!FocusCanNavigate || _currentPage != "lookup" || navigationVersion != _learningNavigationVersion
            || lookupVersion != _lookupVersion || !LookupResultCard.IsVisible
            || !string.Equals(ResultWordText.Text, words[0].Word, StringComparison.OrdinalIgnoreCase)) return;
        EnterWordFocus(snapshot);
        StartFocusRound(words.SelectMany(w => w.Words).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
    private void SaveLearningProgress()
    {
        try { _learningProgress.Save(LearningProgressPath); _writingDirty = false; }
        catch (Exception ex) { SetStatus(T("学习记录保存失败：") + ex.Message); }
    }
    private void UpdateLearningNavigation(string page)
    {
        ++_learningNavigationVersion;
        NavIelts.Classes.Set("active", page == "ielts"); NavTyping.Classes.Set("active", page == "typing");
        if (page != "typing") { _typingAdvanceCts?.Cancel(); _typingPlaying = false; Classes.Set("typing-focus", false); }
    }
    private void RefreshLearningLabels()
    {
        foreach (var (control, text) in _learningLabels)
        {
            if (control is Button button) button.Content = T(text);
            else if (control is TextBlock label) label.Text = T(text);
        }
        NavIelts.Content = T("IELTS 专题"); NavTyping.Content = T("打字练习");
        if (_ieltsKind != null)
        {
            var index = _ieltsKind.SelectedIndex;
            _learningConfiguring = true;
            _ieltsKind.ItemsSource = new[] { T("词汇真经"), T("听力 179"), T("阅读同义替换"), T("英美拼写规范"), T("写作 100 句"), T("语法与听力资料") };
            _ieltsKind.SelectedIndex = index; _learningConfiguring = false;
            var practiceMode = _ieltsPracticeMode.SelectedIndex;
            _ieltsPracticeMode.ItemsSource = new[] { T("单词卡认词"), T("淡写"), T("默写") }; _ieltsPracticeMode.SelectedIndex = practiceMode;
            _ieltsRoundOptions.View.IsVisible = _ieltsPracticeMode.IsVisible = _ieltsKind.SelectedIndex < 4;
            _ieltsSearch.Watermark = T("搜索单词、释义或同义词");
            _ieltsShowMeaning.Content = T("显示释义"); _ieltsErrorsOnly.Content = T("仅看错词");
            if (_currentPage == "ielts") RenderIeltsPage();
        }
        RefreshTypingLabels();
    }
    private void OpenLearningResource(string target)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) { var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add(target); Process.Start(start)?.Dispose(); }
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { SetStatus(T("无法打开资料：") + ex.Message); }
    }
}
