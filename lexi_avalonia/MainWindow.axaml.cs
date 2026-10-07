using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lexi;

public partial class MainWindow : Window
{
    private readonly IDictionaryLookup _dictService;
    private IVocabularyArchive _vocabService;
    private readonly IAiExpansion _aiService;
    private readonly IArchiveBackups _backups = ServiceFactory.CreateBackups();
    private AppSettings _settings;

    private List<WordItem> _allWords = new();
    private List<WordItem> _displayedWords = new();
    private bool _batchSelecting;
    private const int VocabPageSize = 50;
    private int _vocabPage;
    private string _filterIdentity = "";
    private List<WordItem> _filteredWords = new();
    private int _lookupVersion;
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private CancellationTokenSource? _aiCts;
    private CancellationTokenSource? _lookupAiCts;
    private bool _isForceClose;
    private string _currentPage = "lookup"; // lookup, vocab, review, settings
    private bool _isReviewMode;
    private string? _generatingWord;

    // Temporary storage for active dialogs
    private long? _editingWordId;
    private List<long>? _pendingDeleteIds;
    private List<long>? _pendingStageIds;

    public MainWindow() : this(null) { }

    internal MainWindow(IWordAudioPlayer? audio)
    {
        _wordAudio = audio ?? new LocalWordAudioPlayer(message => Dispatcher.UIThread.Post(() => SetStatus(T(message))));
        InitializeComponent();

        _dictService = ServiceFactory.OpenDictionary();
        _vocabService = ServiceFactory.OpenArchive();
        _aiService = ServiceFactory.CreateAi();
        _settings = _vocabService.LoadSettings();

        VocabListBox.ItemsSource = _displayedWords;

        BindEvents();
        ConfigureWindowChrome();
        ConfigureResponsiveCards();
        ConfigureLookupShortcut();
        BindFoundationEvents();
        BindReviewEvents();
        BindAppearanceEvents();
        BindLanguageEvents();
        ConfigureMacShortcuts();
        ConfigureQuickActions();
        ConfigureGlassAppearance();
        LoadSettingsToUi();
        ConfigureWordFocus();
        ConfigureLearningMemory();
        RefreshWords();
        ConfigureLearningPages();

        ShowPage("lookup");
    }

    private void BindEvents()
    {
        // Window events
        Closing += OnWindowClosing;

        // Navigation
        NavLookup.Click += (_, _) => ShowPage("lookup");
        NavVocab.Click += (_, _) => ShowPage("vocab");
        NavReview.Click += (_, _) => ShowPage("review");
        NavSettings.Click += (_, _) => ShowPage("settings");

        HideToTrayBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        QuitAppBtn.Click += (_, _) => { if (!_databaseAvailable) ForceClose(); else Close(); };
        DragBar.PointerPressed += OnTitleBarPressed;
        Opened += (_, _) =>
        {
            LookupInput.Focus();
            if (App.Hotkey?.IsRegistered == false) SetStatus(T("Option+D 被其他程序占用；可用托盘或桌面快捷方式呼出。请退出旧版后重开新版。"));
        };

        // Lookup events
        LookupResultCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) SyncLookupActionBarVisibility();
        };
        PageLookup.SizeChanged += (_, _) => LookupPageContent.Width = Math.Max(100, PageLookup.Bounds.Width - PageLookup.Padding.Left - PageLookup.Padding.Right - 14);
        LookupBtn.Click += async (_, _) => await PerformLookupAsync();
        LookupInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                await PerformLookupAsync();
            }
        };

        LookupClearBtn.Click += (_, _) =>
        {
            LookupInput.Text = "";
            LookupInput.Focus();
        };
        LookupInput.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                var hasText = !string.IsNullOrEmpty(LookupInput.Text);
                LookupClearBtn.Opacity = hasText ? 1.0 : 0.0;
                LookupClearBtn.IsHitTestVisible = hasText;
                LookupClearBtn.Focusable = hasText;
            }
        };

        QuickWord1.Click += async (_, _) => { LookupInput.Text = "serendipity"; await PerformLookupAsync(); };
        QuickWord2.Click += async (_, _) => { LookupInput.Text = "resilient"; await PerformLookupAsync(); };
        QuickWord3.Click += async (_, _) => { LookupInput.Text = "wander"; await PerformLookupAsync(); };

        AddWordBtn.Click += (_, _) => AddCurrentWordToVocab();
        ManualAddBtn.Click += (_, _) => AddManualWordToVocab();

        // AI Generation events
        AiDrawerToggleBtn.Click += async (_, _) =>
        {
            if (_drawerOpen) await CloseDrawerAsync();
            else await OpenDrawerAsync();
        };
        AiGenerateBtn.Click += async (_, _) => await PerformAiGenerationAsync();
        AiCancelBtn.Click += async (_, _) =>
        {
            _aiCts?.Cancel();
            await CloseDrawerAsync();
        };

        VocabPreviousBtn.Click += (_, _) => { if (_vocabPage > 0) { _vocabPage--; ApplyVocabFilters(); ResetVocabScroll(); } };
        VocabNextBtn.Click += (_, _) => { if ((_vocabPage + 1) * VocabPageSize < _filteredWords.Count) { _vocabPage++; ApplyVocabFilters(); ResetVocabScroll(); } };

        // Vocab list & filters
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyVocabFilters(); };
        VocabSearchInput.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        VocabStatusFilter.SelectionChanged += (_, _) => ApplyVocabFilters();
        SelectAllCheckBox.Click += (_, _) => OnSelectAllClicked();
        InvertSelectBtn.Click += (_, _) => OnInvertSelectClicked();
        UndoReviewBtn.Click += (_, _) => OnUndoReviewClicked();

        BatchActionCombo.SelectionChanged += (_, _) => OnBatchActionChanged();
        ExportPrintBtn.Click += (_, _) => OnExportPrintClicked();

        // Settings events
        SettingsProviderCombo.SelectionChanged += (_, _) => OnSettingsProviderChanged();
        SettingsSaveBtn.Click += (_, _) => OnSaveSettingsClicked();
        ThemeToggleBtn.Click += (_, _) => SettingsThemeCombo.SelectedIndex = SettingsThemeCombo.SelectedIndex == 1 ? 0 : 1;
        SettingsThemeCombo.SelectionChanged += (_, _) =>
        {
            var theme = SettingsThemeCombo.SelectedIndex == 1 ? "Dark" : "Light";
            RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            ThemeToggleBtn.Content = theme == "Dark" ? T("切换浅色") : T("切换深色");
            if (_settings.Theme == theme) return;
            var oldTheme = _settings.Theme;
            _settings.Theme = theme;
            ApplyAppearance();
            try { _vocabService.SaveSettings(_settings); }
            catch (Exception ex) { _settings.Theme = oldTheme; SetStatus(T("主题已预览，但未能保存：") + ex.Message); }
        };

        // Overlay Dialogs
        DialogEditCancelBtn.Click += (_, _) => HideOverlay(DialogEditOverlay);
        DialogEditConfirmBtn.Click += (_, _) => ConfirmEditWord();

        DialogStageCancelBtn.Click += (_, _) => HideOverlay(DialogStageOverlay);
        DialogStageConfirmBtn.Click += (_, _) => ConfirmStageChange();

        DialogDeleteCancelBtn.Click += (_, _) => HideOverlay(DialogDeleteOverlay);
        DialogDeleteConfirmBtn.Click += (_, _) => ConfirmDelete();
    }

    private void SetStatus(string message)
    {
        GlobalStatusText.Text = string.IsNullOrEmpty(_vocabService.BackupWarning) ? message : message + " " + T(_vocabService.BackupWarning);
    }

    #region Window Lifecycle & Tray

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_restoring) { e.Cancel = true; return; }
        // Keep the global selection cards available when the main window closes.
        // The application menu and Cmd+Q provide an explicit quit path.
        if (!_isForceClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _quickCard?.Close();
        SaveGlassSettings();
        _isForceClose = true;
        _dictService.Dispose();
        _vocabService.Dispose();
    }

    public void ForceClose()
    {
        if (_restoring) return;
        PrepareForApplicationShutdown();
        Close();
    }

    internal void PrepareForApplicationShutdown()
    {
        if (_isForceClose) return;
        MemoryCancelParameters();
        MemoryCancelContextTraining();
        StopLearningSpeech();
        _aiCts?.Cancel();
        _lookupAiCts?.Cancel();
        ++_lookupVersion;
        _filterTimer.Stop();
        foreach (var cts in _rowAiRequests.Values) cts.Cancel();
        ++_drawerAnimVersion;
        _isForceClose = true;
    }

    public void HideToTray()
    {
        StopLearningSpeech();
        // A decorated NSWindow can report Normal while ordering a zoomed window
        // out. Capture the visible state before native hide notifications arrive.
        if (IsVisible) _hiddenWindowState = WindowState == WindowState.Minimized ? _restoreWindowState : WindowState;
        Hide();
        SetStatus(QuickText("主窗口已隐藏，全局快捷卡片继续可用；从菜单栏或 Dock 打开主窗口。","Main window hidden. Quick cards remain available; open the main window from the menu or Dock."));
    }

    public void ShowAndActivate()
    {
        if (_isForceClose) return;
        var restoreState = _hiddenWindowState ?? (WindowState == WindowState.Minimized ? _restoreWindowState : WindowState);
        _hiddenWindowState = null;
        Show();
        WindowState = restoreState;
        Activate();
        if (_wordFocusActive || _typingPlaying)
        {
            if (_wordFocusActive) _focusBackButton?.Focus();
            else if (_currentPage == "typing" && _typingSession.Current != null && _typingSession.Outcome != TypingOutcome.Correct) _typingInput.Focus();
            return;
        }
        ShowPage("lookup");
        LookupInput.Focus();
    }

    public void ToggleVisibility()
    {
        if (_isForceClose) return;
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            HideToTray();
        }
        else
        {
            ShowAndActivate();
        }
    }

    #endregion

    #region Navigation

    private void ShowPage(string page)
    {
        if (!_databaseAvailable || PlanDialogOverlay.IsVisible || _planActionOverlay?.IsVisible == true) return;
        if (page == "lookup" && _wordFocusActive)
        {
            _focusBackButton?.Focus();
            return;
        }
        if (page == "lookup" && _typingPlaying && _currentPage == "typing")
        {
            _typingInput.Focus();
            return;
        }
        StopLearningSpeech();
        if (_wordFocusActive && page != "lookup") ExitWordFocus();
        if (_planTypingActive && page != "typing") RenderTypingSetup();
        SetFullPageReview(page == "review" || (page == "typing" && _typingPlaying));
        ++_reviewEpoch; // Invalidate any in-flight card transition before page navigation.
        var previousPage = _currentPage;
        _currentPage = page;
        _isReviewMode = false; // Review deck owns its queue; archive filters never change it.
        if (!_wordFocusActive) MemoryFocusSurfaceExited(); // 离开卡片表面：下一次进入重新开学习会话

        NavLookup.Classes.Set("active", page == "lookup");
        NavVocab.Classes.Set("active", page == "vocab");
        NavReview.Classes.Set("active", page == "review");
        NavSettings.Classes.Set("active", page == "settings");
        UpdateLearningNavigation(page);

        var incoming = PageControl(page);
        ApplyPageVisibility(page);
        if (previousPage != page) StartPageTransition(incoming);

        if (page == "lookup")
        {
            LookupInput.Focus();
        }
        else if (page == "review")
        {
            OpenReviewDeck();
        }
        else if (page == "vocab")
        {
            VocabPageTitle.Text = _isReviewMode ? T("今日重逢") : T("词汇档案");
            VocabPageSubtitle.Text = "";
            VocabStatusFilter.IsEnabled = !_isReviewMode;
            ApplyVocabFilters();
        }
        if (page == "settings") UpdateDataInfo();
        if (page == "quotes") RenderQuotes();
        if (page == "ielts") RenderIeltsPage();
        if (page == "plans") RenderStudyPlanLists();
        if (page == "typing") ShowTypingPractice();
    }

    private CancellationTokenSource? _pageTransitionCts;

    private Control? PageControl(string? page) => page switch
    {
        "lookup" => LookupPageHost,
        "vocab" => PageVocab,
        "review" => PageReview,
        "settings" => PageSettings,
        "ielts" => _ieltsPage,
        "plans" => _studyPlanPage,
        "typing" => _typingPage,
        "quotes" => _quotesPage,
        _ => null,
    };

    // 任一时刻只有一个页面可见（沿用原契约）；新页面随后淡入，避免两页半透明内容重影。
    private void ApplyPageVisibility(string page)
    {
        if (_quotesPage != null) _quotesPage.IsVisible = page == "quotes";
        _quotesNav?.Classes.Set("active",page == "quotes");
        if (_ieltsPage != null) _ieltsPage.IsVisible = page == "ielts";
        if (_studyPlanPage != null) _studyPlanPage.IsVisible = page == "plans";
        if (_typingPage != null) _typingPage.IsVisible = page == "typing";
        PageLookup.IsVisible = true; // 宿主的显隐已经足够，淡出过程中内容不应提前消失
        foreach (var (key, control) in new (string, Control)[]
                 { ("lookup", LookupPageHost), ("vocab", PageVocab), ("review", PageReview), ("settings", PageSettings) })
        {
            var visible = key == page;
            control.IsVisible = visible;
            if (!visible) Motion.Reset(control);
        }
    }

    // macOS 风格：旧页即时退场，新页淡入并轻微上浮（fade-through），不做硬切。
    private void StartPageTransition(Control? incoming)
    {
        _pageTransitionCts?.Cancel();
        if (incoming == null) return;
        var cts = new CancellationTokenSource();
        _pageTransitionCts = cts;

        Motion.SetPose(incoming, Motion.Pose(0, 12, 1), 0);
        _ = Motion.ToPoseAsync(incoming, Motion.Rest, 1, Motion.Standard, Motion.Enter, cts.Token);
    }

    #endregion

    #region Lookup & Offline Dictionary

    private async Task PerformLookupAsync()
    {
        if (_restoring || !_databaseAvailable) return;
        var requestVersion = ++_lookupVersion;
        _currentExpansion = null;
        _aiCts?.Cancel();
        _generatingWord = null;
        var query = LookupInput.Text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(query))
        {
            SetStatus(T("请输入英文单词进行查询。"));
            return;
        }

        LookupEmptyCard.IsVisible = false;
        LookupResultCard.IsVisible = false;
        LookupActionBar.IsVisible = false;
        LookupNotFoundCard.IsVisible = false;
        AiDrawerToggleBtn.IsVisible = false;
        AiGenerateBtn.IsEnabled = true;
        AiGenerateBtn.Content = T("✦ 生成");
        AiCancelBtn.IsVisible = false;
        _ = CloseDrawerAsync(immediate: true);

        LookupBtn.IsEnabled = false;
        LookupBtn.Content = T("查询中…");

        try
        {
            var personal = _allWords.FirstOrDefault(w => w.Word.Equals(query, StringComparison.OrdinalIgnoreCase));
            var res = personal == null ? await _dictService.LookupAsync(query) : new LookupResult
            {
                Word = personal.Word, Phonetic = personal.Phonetic, Translation = personal.Translation,
                Definition = personal.Definition, Found = true
            };
            var ielts = _ieltsCatalog?.Find(query);
            if (!res.Found && ielts != null) res = new LookupResult
            { Word = query, Translation = ielts.Pos + " " + ielts.Meaning, Phonetic = ielts.Phonetic, Definition = ielts.Extra, Found = true };
            if (requestVersion != _lookupVersion || _isForceClose) return;
            if (ielts != null) _currentExpansion = CreateIeltsExpansion(ielts);
            var aiCompleted = false;
            string? aiError = null;
            if (!res.Found && AiLookupConfigured)
            {
                _lookupAiCts?.Cancel();
                var aiCts = new CancellationTokenSource();
                _lookupAiCts = aiCts;
                SetStatus(TF($"离线词库未收录 '{query}'，正在请求 AI 补全…"));
                try { res = await new AiService().LookupWordAsync(query, _settings, aiCts.Token); aiCompleted = true; }
                catch (Exception ex) { aiError = ex.Message; }
                finally
                {
                    if (ReferenceEquals(_lookupAiCts, aiCts)) _lookupAiCts = null;
                    aiCts.Dispose();
                }
                if (requestVersion != _lookupVersion || _isForceClose) return;
            }
            if (res.Found)
            {
                ResultWordText.Text = res.Word;
                ResultPhoneticText.Text = string.IsNullOrEmpty(res.Phonetic) ? T("暂无音标") : res.Phonetic;
                ResultTranslationText.Text = res.Translation;
                ResultDefinitionText.Text = res.Definition;
                RenderPartOfSpeech(res.Pos, res.Translation);

                LookupResultCard.IsVisible = true;
                UpdateLookupArchiveState();
                var archived = FindCurrentArchive();
                if (archived?.AiResult is { HasContent: true } savedExpansion)
                {
                    _currentExpansion = savedExpansion;
                    RenderExpansion(savedExpansion);
                }
                var source = aiCompleted ? T("AI 补全") : personal == null ? T("离线词库") : T("私人词汇档案");
                SetStatus(TF($"查词成功: {res.Word} ({source})"));
            }
            else
            {
                ManualWordInput.Text = query;
                ManualPhoneticInput.Text = "";
                ManualTranslationInput.Text = "";
                ManualDefinitionInput.Text = "";
                LookupNotFoundCard.IsVisible = true;
                SetStatus(aiError == null ? TF($"离线词库未收录 '{query}'，可手动补充录入。") : TF($"离线词库未收录 '{query}'，AI 补全失败：{aiError}"));
            }
        }
        catch (Exception ex)
        {
            if (requestVersion == _lookupVersion) SetStatus(TF($"查词出错: {ex.Message}"));
        }
        finally
        {
            if (requestVersion == _lookupVersion) { LookupBtn.IsEnabled = true; LookupBtn.Content = T("查询 ↵"); }
        }

        await Task.CompletedTask;
    }

    private void RenderPartOfSpeech(string pos, string translation)
    {
        ResultPosPanel.Children.Clear();
        var labels = System.Text.RegularExpressions.Regex.Matches(pos + " " + translation, @"\b(n|v|vt|vi|adj|adv|prep|pron|conj|interj|num)\.")
            .Select(m => m.Value).Distinct().Take(6).ToList();
        foreach (var label in labels)
        {
            var chip = new Border { Classes = { "pos-chip" }, Child = new TextBlock { Text = label, FontSize = 12 } };
            if (label.StartsWith("v")) chip.Classes.Add("verb");
            if (label is "adj." or "adv.") chip.Classes.Add("modifier");
            ResultPosPanel.Children.Add(chip);
        }
        ResultPosPanel.IsVisible = labels.Count > 0;
    }

    private void AddCurrentWordToVocab()
    {
        if (_planCardActive) return;
        var word = ResultWordText.Text?.Trim();
        if (string.IsNullOrEmpty(word)) return;

        var phon = ResultPhoneticText.Text?.Trim() == T("暂无音标") ? "" : ResultPhoneticText.Text?.Trim() ?? "";
        var trans = ResultTranslationText.Text?.Trim() ?? "";
        var def = ResultDefinitionText.Text?.Trim() ?? "";

        try
        {
            _vocabService.AddWord(word, phon, trans, def);
            var added = _vocabService.GetAllWords().First(w => w.Word.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (_currentExpansion != null) _vocabService.SaveExpansion(added.Id, _currentExpansion);
            RefreshWords();
            UpdateLookupArchiveState();
            SetStatus(TF($"已将 '{word}' 加入生词本，首次复习安排在明天。"));
        }
        catch (Exception ex)
        {
            SetStatus(TF($"加入生词本失败: {ex.Message}"));
        }
    }

    private void AddManualWordToVocab()
    {
        var word = ManualWordInput.Text?.Trim();
        var trans = ManualTranslationInput.Text?.Trim();
        if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(trans))
        {
            SetStatus(T("单词和中文释义为必填项。"));
            return;
        }

        var phon = ManualPhoneticInput.Text?.Trim() ?? "";
        var def = ManualDefinitionInput.Text?.Trim() ?? "";

        try
        {
            _vocabService.AddWord(word, phon, trans, def);
            RefreshWords();
            LookupNotFoundCard.IsVisible = false;
            LookupInput.Text = word;
            SetStatus(TF($"已手动录入 '{word}' 至生词本。"));
        }
        catch (Exception ex)
        {
            SetStatus(TF($"录入失败: {ex.Message}"));
        }
    }

    #endregion

    #region Independent AI Generation & Collapsible Drawer

    private int _drawerAnimVersion = 0;
    private bool _drawerOpen;
    private CancellationTokenSource? _drawerCts;

    private async Task OpenDrawerAsync()
    {
        var version = ++_drawerAnimVersion;
        _drawerOpen = true;
        AiDrawerToggleBtn.Content = T("收起扩展 ↑");
        var startHeight = AiDrawerSlot.IsVisible ? (double.IsNaN(AiDrawerSlot.Height) ? AiDrawerSlot.Bounds.Height : AiDrawerSlot.Height) : 0;
        AiDrawerSlot.IsVisible = true;
        AiResultBox.IsVisible = true;

        // Measure natural height of the drawer content
        AiResultBox.InvalidateMeasure();
        var width = Math.Max(100, LookupResultCard.Bounds.Width - LookupResultCard.Padding.Left - LookupResultCard.Padding.Right - 4);
        AiResultBox.Measure(new Size(width, double.PositiveInfinity));
        var targetHeight = Math.Max(AiResultBox.DesiredSize.Height + 10, 48);

        _drawerCts?.Cancel();
        var cts = _drawerCts = new CancellationTokenSource();
        await Motion.HeightToAsync(AiDrawerSlot, startHeight, targetHeight, AiDrawerSlot.Opacity, 1.0,
            Motion.Slow, Motion.Enter, cts.Token);

        if (version == _drawerAnimVersion && !cts.IsCancellationRequested)
        {
            AiDrawerSlot.Height = double.NaN;
            AiDrawerSlot.Opacity = 1.0;
        }
    }

    private async Task CloseDrawerAsync(bool immediate = false)
    {
        _drawerOpen = false;
        AiDrawerToggleBtn.Content = T("展开扩展 ↓");
        var version = ++_drawerAnimVersion;
        _drawerCts?.Cancel();
        if (!AiDrawerSlot.IsVisible)
        {
            AiDrawerSlot.Transitions = null;
            AiDrawerSlot.Height = 0;
            AiDrawerSlot.Opacity = 0;
            AiResultBox.IsVisible = false;
            return;
        }

        if (immediate || _settings.ReduceMotion)
        {
            AiDrawerSlot.Transitions = null;
            AiDrawerSlot.Height = 0;
            AiDrawerSlot.Opacity = 0;
            AiDrawerSlot.IsVisible = false;
            AiResultBox.IsVisible = false;
            return;
        }

        var startHeight = double.IsNaN(AiDrawerSlot.Height) ? AiDrawerSlot.Bounds.Height : AiDrawerSlot.Height;
        var cts = _drawerCts = new CancellationTokenSource();
        await Motion.HeightToAsync(AiDrawerSlot, startHeight, 0, AiDrawerSlot.Opacity, 0,
            Motion.Standard, Motion.Exit, cts.Token);

        if (version == _drawerAnimVersion && !cts.IsCancellationRequested)
        {
            AiDrawerSlot.Height = 0;
            AiDrawerSlot.Opacity = 0;
            AiDrawerSlot.IsVisible = false;
            AiResultBox.IsVisible = false;
        }
    }


    private void RenderExpansion(LlmResult result)
    {
        AiResultPhrasesGroup.IsVisible = result.Phrases.Count > 0;
        AiResultPhrasesContainer.Children.Clear();
        foreach (var phrase in result.Phrases) AiResultPhrasesContainer.Children.Add(CreateBilingualCard(phrase.English, phrase.Chinese));
        AiDrawerToggleBtn.IsVisible = result.HasContent;
            // Render Examples
            AiResultExamplesGroup.IsVisible = result.Examples.Count > 0;
            AiResultExamplesTitle.IsVisible = result.Examples.Count > 0;
            AiResultExamplesContainer.Children.Clear();
            if (result.Examples.Count > 0)
            {
                AiResultExamplesBody.Text = string.Join("\n\n", result.Examples.Select((e, i) => $"{i + 1}. {e.English}\n   {e.Chinese}"));
                foreach (var eg in result.Examples)
                {
                    var card = new Border
                    {
                        Classes = { "example-card" },
                        Child = new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                new Grid
                                {
                                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                                    Children =
                                    {
                                        new TextBlock
                                        {
                                            Text = "“",
                                            FontFamily = (FontFamily)this.FindResource("DisplayFont")!,
                                            FontSize = 16,
                                            FontWeight = FontWeight.Normal,
                                            Classes = { "example-quote" },
                                            Margin = new Thickness(0, -2, 6, 0)
                                        },
                                        new TextBlock
                                        {
                                            [Grid.ColumnProperty] = 1,
                                            Text = eg.English,
                                            FontSize = 14,
                                            FontWeight = FontWeight.Normal,
                                            Classes = { "example-en" },
                                            TextWrapping = TextWrapping.Wrap,
                                            LineHeight = 24
                                        }
                                    }
                                },
                                new TextBlock
                                {
                                    Text = eg.Chinese,
                                    FontSize = 12,
                                    Classes = { "muted" },
                                    Margin = new Thickness(16, 0, 0, 0),
                                    TextWrapping = TextWrapping.Wrap,
                                    LineHeight = 21
                                }
                            }
                        }
                    };
                    AiResultExamplesContainer.Children.Add(card);
                }
            }

            // Synonyms (Micro-capsule Buttons)
            AiResultSynonymsGroup.IsVisible = result.Synonyms.Count > 0;
            AiResultSynonymsTitle.IsVisible = result.Synonyms.Count > 0;
            AiResultSynonymsPanel.Children.Clear();
            if (result.Synonyms.Count > 0)
            {
                AiResultSynonymsBody.Text = string.Join("  ·  ", result.Synonyms);
                foreach (var syn in result.Synonyms)
                {
                    var wordPill = syn.Trim();
                    var btn = new Button
                    {
                        Classes = { "micro-capsule" },
                        Content = wordPill
                    };
                    ToolTip.SetTip(btn, TF($"点击离线查询 '{wordPill}'"));
                    btn.Click += async (_, _) =>
                    {
                        LookupInput.Text = wordPill;
                        await PerformLookupAsync(); // Trigger basic query, never calls AI!
                    };
                    AiResultSynonymsPanel.Children.Add(btn);
                }
            }

            // Antonyms (Micro-capsule Buttons)
            AiResultAntonymsGroup.IsVisible = result.Antonyms.Count > 0;
            AiResultAntonymsTitle.IsVisible = result.Antonyms.Count > 0;
            AiResultAntonymsPanel.Children.Clear();
            if (result.Antonyms.Count > 0)
            {
                AiResultAntonymsBody.Text = string.Join("  ·  ", result.Antonyms);
                foreach (var ant in result.Antonyms)
                {
                    var wordPill = ant.Trim();
                    var btn = new Button
                    {
                        Classes = { "micro-capsule" },
                        Content = wordPill
                    };
                    ToolTip.SetTip(btn, TF($"点击离线查询 '{wordPill}'"));
                    btn.Click += async (_, _) =>
                    {
                        LookupInput.Text = wordPill;
                        await PerformLookupAsync(); // Trigger basic query, never calls AI!
                    };
                    AiResultAntonymsPanel.Children.Add(btn);
                }
            }

    }

    private async Task PerformAiGenerationAsync()
    {
        if (_restoring || !_databaseAvailable) return;
        var word = ResultWordText.Text?.Trim();
        if (string.IsNullOrEmpty(word)) return;

        var modules = new List<string>();
        if (AiOptExamples.IsChecked == true) modules.Add("examples");
        if (AiOptSynonyms.IsChecked == true) modules.Add("synonyms");
        if (AiOptAntonyms.IsChecked == true) modules.Add("antonyms");
        if (AiOptPhrases.IsChecked == true) modules.Add("phrases");

        if (modules.Count == 0)
        {
            SetStatus(T("请先勾选要生成的内容（例句 / 同义词 / 反义词 / 常用词组）。"));
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            SetStatus(T("请先在「模型与偏好」中配置 API Key。"));
            ShowPage("settings");
            return;
        }

        _aiCts?.Cancel();
        var request = new CancellationTokenSource();
        _aiCts = request;
        var lookupVersion = _lookupVersion;
        var originalArchiveId = FindCurrentArchive()?.Id;
        _generatingWord = word;

        AiGenerateBtn.IsEnabled = false;
        AiGenerateBtn.Content = T("生成中…");
        AiCancelBtn.IsVisible = true;
        AiDrawerToggleBtn.IsVisible = false;
        _ = CloseDrawerAsync(immediate: true);
        SetStatus(TF($"正在调用 AI 生成扩展内容: {word} ..."));

        try
        {
            var result = await _aiService.GenerateExpansionAsync(word, modules, _settings, request.Token, FindCurrentArchive()?.Archive.SourceExcerpt);
            if (request.IsCancellationRequested || lookupVersion != _lookupVersion || _generatingWord != word || ResultWordText.Text?.Trim() != word || _restoring) return;

            result = CommitExpansion(word, originalArchiveId, result, modules, request.Token);
            if (result == null) return;

            if (result.HasContent)
            {
                await OpenDrawerAsync();
            }
            else
            {
                await CloseDrawerAsync();
            }

            SetStatus(T("AI 扩展内容生成成功。"));
        }
        catch (OperationCanceledException)
        {
            if (lookupVersion != _lookupVersion) return;
            SetStatus(T("已取消 AI 生成。"));
            if (_currentExpansion != null) { RenderExpansion(_currentExpansion); await OpenDrawerAsync(); }
        }
        catch (Exception ex)
        {
            if (lookupVersion != _lookupVersion) return;
            SetStatus(TF($"AI 生成失败: {ex.Message}"));
            if (_currentExpansion != null) { RenderExpansion(_currentExpansion); await OpenDrawerAsync(); }
        }
        finally
        {
            if (ReferenceEquals(_aiCts, request))
            {
                AiGenerateBtn.IsEnabled = true;
                AiGenerateBtn.Content = T("✦ 生成");
                AiCancelBtn.IsVisible = false;
                _aiCts = null;
            }
            request.Dispose();
        }
    }

    #endregion

    #region Vocab Notebook & Review Management

    private void RefreshWords()
    {
        var existing = _allWords.ToDictionary(w => w.Id);
        _allWords = _vocabService.GetAllWords().Select(fresh =>
        {
            if (!existing.TryGetValue(fresh.Id, out var old)) return fresh;
            old.Translation = fresh.Translation; old.Phonetic = fresh.Phonetic; old.Definition = fresh.Definition;
            old.Notes = fresh.Notes; old.Stage = fresh.Stage; old.Status = fresh.Status;
            old.Archive = fresh.Archive;
            old.AiResult = fresh.AiResult;
            old.NextReviewDate = fresh.NextReviewDate; old.LastReviewedAt = fresh.LastReviewedAt;
            old.ReviewCount = fresh.ReviewCount; old.LearningStartDate = fresh.LearningStartDate;
            return old;
        }).ToList();
        ApplyVocabFilters();

        NavVocabBadge.Text = _allWords.Count.ToString();
        UpdateReviewBadge();
    }

    private void ResetVocabScroll()
    {
        var scroll = VocabListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll != null) scroll.Offset = default;
    }

    private void ApplyVocabFilters()
    {
        var q = VocabSearchInput.Text?.Trim().ToLowerInvariant() ?? "";
        var statusIndex = VocabStatusFilter.SelectedIndex; // 0: 全部, 1: 学习中, 2: 已掌握
        var identity = $"{_isReviewMode}|{statusIndex}|{q}";
        if (identity != _filterIdentity) { _vocabPage = 0; _filterIdentity = identity; ResetVocabScroll(); }
        // 复习模式与复习页共用同一个到期集合（规格书 §9.1：两处必须成对改成同一口径）。
        var reviewDueIds = _isReviewMode ? MemoryDueWordIds() : null;

        var filtered = _allWords.Where(w =>
        {
            if (_isReviewMode)
            {
                // Review mode: the same due set as the review deck (FSRS due ∪ legacy-due words without a card)
                if (reviewDueIds is null || !reviewDueIds.Contains(w.Id))
                {
                    return false;
                }
            }
            else
            {
                if (statusIndex == 1 && w.Status != "learning") return false;
                if (statusIndex == 2 && w.Status != "mastered") return false;
            }

            if (!string.IsNullOrEmpty(q))
            {
                var matchWord = w.Word.ToLowerInvariant().Contains(q);
                var matchTrans = w.Translation.ToLowerInvariant().Contains(q);
                var matchArchive = string.Join(" ", w.Notes, w.Archive.SourceTitle, w.Archive.SourceExcerpt, string.Join(" ", w.Archive.Tags)).Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!matchWord && !matchTrans && !matchArchive) return false;
            }

            return true;
        }).ToList();

        _filteredWords = filtered;
        var pageCount = Math.Max(1, (filtered.Count + VocabPageSize - 1) / VocabPageSize);
        _vocabPage = Math.Clamp(_vocabPage, 0, pageCount - 1);
        VocabPageText.Text = TF($"{_vocabPage + 1} / {pageCount} 页 · 共 {filtered.Count} 词");
        VocabPager.IsVisible = pageCount > 1;
        VocabPreviousBtn.IsEnabled = _vocabPage > 0;
        VocabNextBtn.IsEnabled = _vocabPage + 1 < pageCount;
        filtered = filtered.Skip(_vocabPage * VocabPageSize).Take(VocabPageSize).ToList();
        if (!_displayedWords.SequenceEqual(filtered))
        {
            foreach (var w in _displayedWords) w.PropertyChanged -= OnWordItemPropertyChanged;
            _displayedWords = filtered;
            foreach (var w in _displayedWords) w.PropertyChanged += OnWordItemPropertyChanged;
            VocabListBox.ItemsSource = _displayedWords;
        }

        UpdateSelectedCount();
    }

    private void OnWordItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_batchSelecting && e.PropertyName == nameof(WordItem.Selected))
        {
            UpdateSelectedCount();
        }
    }

    private void UpdateSelectedCount()
    {
        var selectedCount = _filteredWords.Count(w => w.Selected);
        SelectedCountText.Text = TF($"已选 {selectedCount} 项");
        SelectAllCheckBox.IsChecked = _displayedWords.Count > 0 && _displayedWords.All(w => w.Selected);
    }

    private void OnSelectAllClicked()
    {
        var check = SelectAllCheckBox.IsChecked == true;
        _batchSelecting = true;
        try { foreach (var w in _displayedWords) w.Selected = check; }
        finally { _batchSelecting = false; }
        UpdateSelectedCount();
    }

    private void OnInvertSelectClicked()
    {
        _batchSelecting = true;
        try { foreach (var w in _displayedWords) w.Selected = !w.Selected; }
        finally { _batchSelecting = false; }
        UpdateSelectedCount();
    }

    private void OnUndoReviewClicked()
    {
        // Try undoing selected word first, or the first reviewed item
        try
        {
            var targetWord = _filteredWords.FirstOrDefault(w => w.Selected);
            var ok = targetWord == null ? _vocabService.UndoMostRecentReview() : _vocabService.UndoLastReview(targetWord.Id);
            if (ok) RefreshWords();
            SetStatus(ok ? T("已撤销最近一次复习。") : T("没有可撤销的复习；后续修改过状态的记录不能回退。"));
        }
        catch (Exception ex) { SetStatus(TF($"撤销失败：{ex.Message}")); }
    }

    private void OnBatchActionChanged()
    {
        var idx = BatchActionCombo.SelectedIndex;
        if (idx <= 0) return;

        var selectedIds = _filteredWords.Where(w => w.Selected).Select(w => w.Id).ToList();
        if (selectedIds.Count == 0)
        {
            SetStatus(T("请先勾选列表中的单词。"));
            BatchActionCombo.SelectedIndex = 0;
            return;
        }

        // Action map:
        // 1: review, 2: master, 3: today, 4: stage, 5: restart, 6: delete
        switch (idx)
        {
            case 1:
                ExecuteBatchAction(selectedIds, "review");
                break;
            case 2:
                ExecuteBatchAction(selectedIds, "master");
                break;
            case 3:
                ExecuteBatchAction(selectedIds, "today");
                break;
            case 4:
                _pendingStageIds = selectedIds;
                ShowOverlay(DialogStageOverlay);
                break;
            case 5:
                ExecuteBatchAction(selectedIds, "restart");
                break;
            case 6:
                _pendingDeleteIds = selectedIds;
                DialogDeleteTitle.Text = TF($"确认删除选中的 {selectedIds.Count} 个单词？");
                ShowOverlay(DialogDeleteOverlay);
                break;
        }

        BatchActionCombo.SelectedIndex = 0;
    }

    private void ExecuteBatchAction(List<long> ids, string action, int? stage = null)
    {
        try
        {
            _vocabService.ExecuteBatch(ids, action, stage);
            // 手动「完成本次复习」是显式覆盖：让已有 FSRS 卡的词也真的动起来。
            // （§6 禁止的是**评分路径**覆盖 due，手动管理动作不受此限。）
            if (action == "review") (_vocabService as VocabularyService)?.SyncManualReviewDue(ids);
            RefreshWords();
            SetStatus(TF($"已批量操作 {ids.Count} 个单词。"));
        }
        catch (Exception ex)
        {
            SetStatus(TF($"批量操作失败: {ex.Message}"));
        }
    }

    private void OnExportPrintClicked()
    {
        var selectedWords = _filteredWords.Where(w => w.Selected).ToList();
        if (selectedWords.Count == 0)
        {
            selectedWords = _filteredWords.ToList();
        }

        if (selectedWords.Count == 0)
        {
            SetStatus(T("没有可导出的单词。"));
            return;
        }

        try
        {
            var html = ExportService.GenerateHtml(selectedWords);
            var path = ExportService.SaveAndOpen(html);
            SetStatus(TF($"清单已导出并在浏览器中打开: {Path.GetFileName(path)}"));
        }
        catch (Exception ex)
        {
            SetStatus(TF($"导出打印失败: {ex.Message}"));
        }
    }

    // Row Actions
    public void OnEditWordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long id)
        {
            var word = _allWords.FirstOrDefault(w => w.Id == id);
            if (word != null)
            {
                OpenArchiveEditor(word);
            }
        }
    }

    public void OnDeleteWordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long id)
        {
            var word = _allWords.FirstOrDefault(w => w.Id == id);
            if (word != null)
            {
                _pendingDeleteIds = new List<long> { id };
                DialogDeleteTitle.Text = TF($"确认删除 '{word.Word}'？");
                ShowOverlay(DialogDeleteOverlay);
            }
        }
    }

    private void ConfirmEditWord()
    {
        SaveArchiveEditor();
    }

    private void ConfirmStageChange()
    {
        if (_pendingStageIds != null && _pendingStageIds.Count > 0)
        {
            var stageIndex = DialogStageCombo.SelectedIndex; // 0..5
            ExecuteBatchAction(_pendingStageIds, "stage", stageIndex);
        }
        HideOverlay(DialogStageOverlay);
        _pendingStageIds = null;
    }

    private void ConfirmDelete()
    {
        if (_pendingDeleteIds != null && _pendingDeleteIds.Count > 0)
        {
            ExecuteBatchAction(_pendingDeleteIds, "delete");
        }
        HideOverlay(DialogDeleteOverlay);
        _pendingDeleteIds = null;
    }

    #endregion

    #region Settings Management

    private void LoadSettingsToUi()
    {
        LoadLanguage();
        LoadQuickActionSettings();
        var provider = _settings.Provider?.ToLowerInvariant() ?? "deepseek";
        SettingsProviderCombo.SelectedIndex = provider switch
        {
            "deepseek" => 0,
            "glm" => 1,
            "qwen" => 2,
            _ => 3
        };

        if (_protocolCombo != null) _protocolCombo.SelectedIndex = _settings.AiProtocol == "responses" ? 1 : 0;
        LoadGlassAppearance();
        SettingsBaseUrlInput.Text = _settings.BaseUrl;
        SettingsModelInput.Text = _settings.Model;
        SettingsApiKeyInput.Text = _settings.ApiKey;
        SettingsRememberKeyBox.IsChecked = _settings.RememberKey;
        LoadFoundationSettings();
        SettingsThemeCombo.SelectedIndex = _settings.Theme == "Dark" ? 1 : 0;
        RequestedThemeVariant = _settings.Theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        LoadAppearance();

        SettingsTimeoutCombo.SelectedIndex = _settings.Timeout switch
        {
            5 => 0,
            30 => 2,
            60 => 3,
            120 => 4,
            _ => 1
        };
    }

    private void OnSettingsProviderChanged()
    {
        var idx = SettingsProviderCombo.SelectedIndex;
        var key = idx switch
        {
            0 => "deepseek",
            1 => "glm",
            2 => "qwen",
            _ => "custom"
        };

        // Custom endpoints and protocols belong to the local user; preserve manual input.
        if (key == "custom") return;

        if (AiService.Presets.TryGetValue(key, out var preset))
        {
            if (_protocolCombo != null) _protocolCombo.SelectedIndex = 0;
            SettingsBaseUrlInput.Text = preset.BaseUrl;
            SettingsModelInput.Text = preset.Model;
            if (key != "custom")
            {
                SettingsApiKeyInput.Text = "";
            }
        }
    }

    private void OnSaveSettingsClicked()
    {
        var baseUrl = SettingsBaseUrlInput.Text?.Trim() ?? "";
        var model = SettingsModelInput.Text?.Trim() ?? "";
        var apiKey = SettingsApiKeyInput.Text?.Trim() ?? "";
        var rememberKey = SettingsRememberKeyBox.IsChecked == true;

        var timeout = SelectedTimeout();

        try
        {
            // Validate URL syntax
            AiService.NormalizeEndpoint(baseUrl, _protocolCombo?.SelectedIndex == 1 ? "responses" : "chat");
        }
        catch (Exception ex)
        {
            SetStatus(TF($"设置错误: {ex.Message}"));
            return;
        }

        if (string.IsNullOrEmpty(model))
        {
            SetStatus(T("请输入模型名称。"));
            return;
        }

        var providerKey = SettingsProviderCombo.SelectedIndex switch
        {
            0 => "deepseek",
            1 => "glm",
            2 => "qwen",
            _ => "custom"
        };

        _settings = new AppSettings
        {
            Material = _settings.Material, GlassIntensity = _settings.GlassIntensity,
            LookupShortcut = _settings.LookupShortcut, TranslateShortcut = _settings.TranslateShortcut, QuoteShortcut = _settings.QuoteShortcut,
            AiProtocol = _protocolCombo?.SelectedIndex == 1 ? "responses" : "chat",
            Provider = providerKey,
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = apiKey,
            RememberKey = rememberKey,
            Clipboard = false,
            Theme = SettingsThemeCombo.SelectedIndex == 1 ? "Dark" : "Light",
            UiLanguage = SettingsLanguageCombo.SelectedIndex == 1 ? "en" : "zh-CN",
            HighContrast = HighContrastBox.IsChecked == true,
            OpaqueMaterial = OpaqueMaterialBox.IsChecked == true,
            ReduceMotion = ReduceMotionBox.IsChecked == true,
            Timeout = timeout
        };
        _settings.AiContext = SelectedAiContext();
        _settings.IncludeSourceInAi = SettingsIncludeSourceBox.IsChecked == true;

        try
        {
            _vocabService.SaveSettings(_settings);
            SetStatus(T("设置已保存。基础查词不会调用模型。"));
        }
        catch (Exception ex) { SetStatus(TF($"设置未能保存：{ex.Message}")); }
    }

    #endregion
}
