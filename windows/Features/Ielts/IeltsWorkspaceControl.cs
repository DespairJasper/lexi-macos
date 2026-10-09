using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Lexi.Controls;

namespace Lexi.Features.Ielts;

/// <summary>
/// IELTS 核心工作区：
/// - 有界Grid章节目录（克制选中状态，按类别/搜索筛选）
/// - 档案式词行（词头/音标/释义/状态/展开例句来源同义词/紧凑发音）
/// - 常用练习菜单与数量顺序设置面板
/// - 独立紧凑播放器含 seek
/// - 修复筛选与选择混用，切章保留选择，训练来源明确
/// </summary>
public sealed class IeltsWorkspaceControl : Grid, IDisposable
{
    private readonly IeltsCatalog _catalog;
    private readonly LearningProgress _progress;
    private readonly Func<IWordAudioPlayer> _playerProvider;
    private readonly Action<string> _setStatus;
    private readonly Action<List<LearningWord>, bool> _startTyping;
    private readonly Action<LearningSection> _createPlan;
    private readonly Action<List<LearningWord>> _startSynonyms;
    private readonly Action _openResources;
    private readonly Action _openWriting;
    private readonly Action<LearningSection?>? _openSynonyms;
    private readonly Action<List<LearningWord>>? _archiveBatch;
    private readonly Action<LearningWord, LearningSection?>? _archiveSingle;
    private readonly Action<string>? _viewArchive;
    private readonly Func<string, bool>? _isArchived;

    private readonly IeltsSelectionStore _selectionStore = new();
    private readonly IeltsPracticeSettings _practiceSettings = new();
    private readonly IeltsAudioBar _audioBar;

    // 章节目录
    private LearningSection? _selectedSection;
    private string _chapterKindFilter = "all";
    private string _chapterSearchKeyword = "";
    private readonly StackPanel _chapterListPanel = new() { Spacing = 2 };
    private readonly TextBox _chapterSearchBox;

    // 单词列表与筛选
    private string _wordSearchKeyword = "";
    private IeltsFilterType _wordFilterType = IeltsFilterType.All;
    private readonly StackPanel _wordListPanel = new() { Spacing = 2 };
    private readonly ScrollViewer _wordScrollViewer;
    private readonly TextBlock _sectionTitleBlock;
    private readonly TextBlock _sectionDescBlock;
    private readonly TextBox _wordSearchBox;

    // 批量栏与筛选操作
    private readonly Border _batchBar;
    private readonly TextBlock _batchSelectedCountBlock;
    private readonly Button _batchArchiveBtn;
    private readonly Button _batchClearBtn;
    private readonly Button _batchViewSelectedBtn;
    private readonly Button _batchPracticeBtn;
    private readonly Button _selectAllFilteredBtn;
    private readonly Button _invertVisibleBtn;

    // 练习设置与开始菜单
    private readonly Border _settingsDrawer;
    private readonly TextBox _practiceCountBox;
    private readonly CheckBox _practiceAllCheck;
    private readonly CheckBox _practiceRandomCheck;
    private readonly Button _startPracticeBtn;
    private readonly Button _toggleSettingsBtn;
    private readonly Button _createPlanBtn;
    private readonly MenuFlyout _practiceMenu;
    private readonly MenuItem _itemHinted;
    private readonly MenuItem _itemSynonyms;

    // 顶部题型切换标签
    private readonly StackPanel _topNavPanel;
    private readonly Button _tabVocab;
    private readonly Button _tabResources;
    private readonly Button _tabSynonyms;
    private readonly Button _tabWriting;

    private readonly HashSet<string> _sharedSelection;
    private readonly HashSet<string> _expandedWords = new(StringComparer.Ordinal);
    public event Action? ProgressChanged;
    public event Action<string>? SaveExampleRequested;

    // 筛选胶囊按钮
    private readonly Button _filterAllBtn;
    private readonly Button _filterTypedBtn;
    private readonly Button _filterErrorsBtn;
    private readonly Button _filterSelectedBtn;

    public IeltsWorkspaceControl(
        IeltsCatalog catalog,
        LearningProgress progress,
        HashSet<string> sharedSelection,
        Func<IWordAudioPlayer> playerProvider,
        Action<string> setStatus,
        Action<List<LearningWord>, bool> startTyping,
        Action<LearningSection> createPlan,
        Action<List<LearningWord>> startSynonyms,
        Action openResources,
        Action openWriting,
        Action<LearningSection?>? openSynonyms = null,
        Action<List<LearningWord>>? archiveBatch = null,
        Action<LearningWord, LearningSection?>? archiveSingle = null,
        Action<string>? viewArchive = null,
        Func<string, bool>? isArchived = null)
    {
        _catalog = catalog;
        _progress = progress;
        _sharedSelection = sharedSelection;
        _playerProvider = playerProvider;
        _setStatus = setStatus;
        _startTyping = startTyping;
        _createPlan = createPlan;
        _startSynonyms = startSynonyms;
        _openResources = openResources;
        _openWriting = openWriting;
        _openSynonyms = openSynonyms;
        _archiveBatch = archiveBatch;
        _archiveSingle = archiveSingle;
        _viewArchive = viewArchive;
        _isArchived = isArchived;

        // 恢复已有勾选词
        if (_sharedSelection.Count > 0)
        {
            foreach (var sec in _catalog.Sections)
            {
                foreach (var entry in sec.Entries)
                {
                    if (_sharedSelection.Contains(entry.Id))
                        _selectionStore.Toggle(entry, sec.Id, true);
                }
            }
        }

        RowDefinitions = new RowDefinitions("Auto,*,Auto");

        // ----------------- ROW 0: 顶部综合栏 -----------------
        var topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(24, 16, 24, 10),
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var mainTitle = new TextBlock
        {
            Text = IeltsI18n.T("IELTS 专题"),
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        mainTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        titleStack.Children.Add(mainTitle);
        Grid.SetColumn(titleStack, 0);
        topBar.Children.Add(titleStack);

        // 顶部分类导航
        _topNavPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        _tabVocab = CreateNavTab(IeltsI18n.T("词汇"), true, () => { });
        _tabResources = CreateNavTab(IeltsI18n.T("听力资料"), false, _openResources);
        _tabSynonyms = CreateNavTab(IeltsI18n.T("阅读同义替换"), false, () =>
        {
            if (_openSynonyms != null)
                _openSynonyms(_selectedSection);
            else
                _startSynonyms(GetActiveWordsForSynonyms());
        });
        _tabWriting = CreateNavTab(IeltsI18n.T("写作练习"), false, _openWriting);

        _topNavPanel.Children.Add(_tabVocab);
        _topNavPanel.Children.Add(_tabResources);
        _topNavPanel.Children.Add(_tabSynonyms);
        _topNavPanel.Children.Add(_tabWriting);

        Grid.SetColumn(_topNavPanel, 1);
        _topNavPanel.Margin = new Thickness(24, 0, 0, 0);
        topBar.Children.Add(_topNavPanel);

        Grid.SetRow(topBar, 0);
        Children.Add(topBar);

        // ----------------- ROW 1: 有界Grid主工作区 -----------------
        var workspaceGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("260,1,*"),
            Margin = new Thickness(24, 0, 24, 12)
        };

        // ====== 目录区（左列 260px，有界） ======
        var catalogPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Margin = new Thickness(0, 0, 14, 0)
        };

        var catalogHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(4, 0, 4, 8)
        };
        var catalogTitle = new TextBlock
        {
            Text = IeltsI18n.T("章节目录"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        catalogTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(catalogTitle, 0);
        catalogHeader.Children.Add(catalogTitle);

        var totalSectionsBadge = new Border
        {
            Classes = { "badge" },
            Child = new TextBlock { Text = _catalog.Sections.Count.ToString(), Classes = { "badge-text" } }
        };
        Grid.SetColumn(totalSectionsBadge, 1);
        catalogHeader.Children.Add(totalSectionsBadge);
        Grid.SetRow(catalogHeader, 0);
        catalogPanel.Children.Add(catalogHeader);

        // 章节类别筛选（全部 / 词汇 / 听力）
        var kindTabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        kindTabs.Children.Add(CreateSmallFilterBtn("全部", "all", () => { _chapterKindFilter = "all"; RenderChapterList(); }));
        kindTabs.Children.Add(CreateSmallFilterBtn("词汇", "vocabulary", () => { _chapterKindFilter = "vocabulary"; RenderChapterList(); }));
        kindTabs.Children.Add(CreateSmallFilterBtn("听力", "listening", () => { _chapterKindFilter = "listening"; RenderChapterList(); }));
        Grid.SetRow(kindTabs, 1);
        catalogPanel.Children.Add(kindTabs);

        // 章节搜索框
        _chapterSearchBox = new TextBox
        {
            Watermark = IeltsI18n.T("搜索章节..."),
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 12,
            Padding = new Thickness(10, 6)
        };
        _chapterSearchBox.TextChanged += (_, _) =>
        {
            _chapterSearchKeyword = _chapterSearchBox.Text ?? "";
            RenderChapterList();
        };
        Grid.SetRow(_chapterSearchBox, 2);
        catalogPanel.Children.Add(_chapterSearchBox);

        // 章节有界滚动列表
        var chapterScroll = new ScrollViewer
        {
            Content = _chapterListPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(chapterScroll, 3);
        catalogPanel.Children.Add(chapterScroll);

        Grid.SetColumn(catalogPanel, 0);
        workspaceGrid.Children.Add(catalogPanel);

        // 分割线
        var divider = new Border();
        divider.Bind(BackgroundProperty, this.GetResourceObservable("LineBrush"));
        Grid.SetColumn(divider, 1);
        workspaceGrid.Children.Add(divider);

        // ====== 词表工作区（右列） ======
        var rightPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Margin = new Thickness(18, 0, 0, 0)
        };

        // 1. 章节头部信息与操作工具栏
        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var titlePanel = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        _sectionTitleBlock = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _sectionTitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));

        _sectionDescBlock = new TextBlock
        {
            Classes = { "muted" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        titlePanel.Children.Add(_sectionTitleBlock);
        titlePanel.Children.Add(_sectionDescBlock);
        Grid.SetColumn(titlePanel, 0);
        toolbarGrid.Children.Add(titlePanel);

        // 右上角动作区域：计划（低强调独立入口）、练习设置、开始练习菜单
        var actionGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 创建每日计划：从练习菜单拆出，作为独立低强调入口
        _createPlanBtn = new Button
        {
            Content = IeltsI18n.T("创建每日计划"),
            Classes = { "ghost" },
            Padding = new Thickness(12, 7),
            FontSize = 12
        };
        _createPlanBtn.Click += (_, _) =>
        {
            if (_selectedSection != null) _createPlan(_selectedSection);
        };
        actionGroup.Children.Add(_createPlanBtn);

        _toggleSettingsBtn = new Button
        {
            Content = "⚙ " + IeltsI18n.T("练习设置"),
            Classes = { "secondary" },
            Padding = new Thickness(12, 7),
            FontSize = 12
        };
        _toggleSettingsBtn.Click += (_, _) =>
        {
            if (_settingsDrawer != null)
                _settingsDrawer.IsVisible = !_settingsDrawer.IsVisible;
        };
        actionGroup.Children.Add(_toggleSettingsBtn);

        // 开始练习菜单：仅提示拼写（hints=true）；同义替换听写仅在对应专题可用。
        // 不再提供无提示默写。菜单项前景显式绑定 InkBrush，避免继承 primary 按钮的 OnPrimary 白字。
        _practiceMenu = new MenuFlyout();

        _itemHinted = CreatePracticeMenuItem(IeltsI18n.T("拼写练习"), () => ExecuteTypingPractice(true));
        _practiceMenu.Items.Add(_itemHinted);

        _itemSynonyms = CreatePracticeMenuItem(IeltsI18n.T("同义替换听写"), () =>
        {
            var targetWords = GetTargetPracticeWords().Where(w => w.Synonyms.Count > 0).ToList();
            if (targetWords.Count > 0)
            {
                _startSynonyms(targetWords);
            }
            else if (_openSynonyms != null)
            {
                _openSynonyms(_selectedSection);
            }
            else
            {
                _startSynonyms(GetTargetPracticeWords());
            }
        });
        _practiceMenu.Items.Add(_itemSynonyms);

        _startPracticeBtn = new Button
        {
            Content = IeltsI18n.T("开始练习") + " ▾",
            Classes = { "secondary" },
            Padding = new Thickness(16, 7),
            FontSize = 13,
            Flyout = _practiceMenu
        };
        actionGroup.Children.Add(_startPracticeBtn);

        Grid.SetColumn(actionGroup, 1);
        toolbarGrid.Children.Add(actionGroup);

        Grid.SetRow(toolbarGrid, 0);
        rightPanel.Children.Add(toolbarGrid);

        // 2. 紧凑练习设置面板（按需展开/收起，取代9大按钮操作墙）
        _settingsDrawer = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false
        };
        var settingsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        var countLabel = new TextBlock
        {
            Text = IeltsI18n.T("练习数量") + "：",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(0, 0, 4, 0)
        };
        _practiceCountBox = new TextBox
        {
            Text = _practiceSettings.Count.ToString(),
            Width = 65,
            Padding = new Thickness(8, 4),
            FontSize = 12
        };
        _practiceCountBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(_practiceCountBox.Text, out var val) && val > 0)
                _practiceSettings.Count = val;
        };

        _practiceAllCheck = new CheckBox
        {
            Content = IeltsI18n.T("全部词"),
            IsChecked = _practiceSettings.AllWords,
            Margin = new Thickness(16, 0, 0, 0),
            FontSize = 12
        };
        _practiceAllCheck.IsCheckedChanged += (_, _) => _practiceSettings.AllWords = _practiceAllCheck.IsChecked == true;

        _practiceRandomCheck = new CheckBox
        {
            Content = IeltsI18n.T("随机练习"),
            IsChecked = _practiceSettings.RandomOrder,
            Margin = new Thickness(16, 0, 0, 0),
            FontSize = 12
        };
        _practiceRandomCheck.IsCheckedChanged += (_, _) => _practiceSettings.RandomOrder = _practiceRandomCheck.IsChecked == true;

        settingsPanel.Children.Add(countLabel);
        settingsPanel.Children.Add(_practiceCountBox);
        settingsPanel.Children.Add(_practiceAllCheck);
        settingsPanel.Children.Add(_practiceRandomCheck);
        _settingsDrawer.Child = settingsPanel;

        Grid.SetRow(_settingsDrawer, 1);
        rightPanel.Children.Add(_settingsDrawer);

        // 3. 搜索条与状态筛选栏（解耦筛选与选择）
        var filterBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        _wordSearchBox = new TextBox
        {
            Watermark = IeltsI18n.T("搜索当前章节词汇或释义..."),
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 12,
            Padding = new Thickness(10, 6)
        };
        _wordSearchBox.TextChanged += (_, _) =>
        {
            _wordSearchKeyword = _wordSearchBox.Text ?? "";
            RenderWordList();
        };
        Grid.SetColumn(_wordSearchBox, 0);
        filterBar.Children.Add(_wordSearchBox);

        var filterPills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _filterAllBtn = CreateCapsuleBtn("全部", true, () => SetWordFilter(IeltsFilterType.All));
        _filterTypedBtn = CreateCapsuleBtn("已练习", false, () => SetWordFilter(IeltsFilterType.Practiced));
        _filterErrorsBtn = CreateCapsuleBtn("错误词", false, () => SetWordFilter(IeltsFilterType.Errors));
        _filterSelectedBtn = CreateCapsuleBtn("仅看已选", false, () => SetWordFilter(IeltsFilterType.SelectedOnly));

        filterPills.Children.Add(_filterAllBtn);
        filterPills.Children.Add(_filterTypedBtn);
        filterPills.Children.Add(_filterErrorsBtn);
        filterPills.Children.Add(_filterSelectedBtn);
        Grid.SetColumn(filterPills, 1);
        filterBar.Children.Add(filterPills);

        // 批量选择栏（只有存在选择时才显示）
        _batchBar = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14, 8),
            Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false
        };
        var batchGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto")
        };
        _batchSelectedCountBlock = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        _batchSelectedCountBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(_batchSelectedCountBlock, 0);
        batchGrid.Children.Add(_batchSelectedCountBlock);

        _batchArchiveBtn = new Button
        {
            Content = IeltsI18n.T("收藏到档案"),
            Classes = { "secondary" },
            Padding = new Thickness(10, 5),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12
        };
        _batchArchiveBtn.Click += (_, _) =>
        {
            var words = _selectionStore.GetSelectedWords();
            if (words.Count > 0)
                _archiveBatch?.Invoke(words);
        };
        Grid.SetColumn(_batchArchiveBtn, 1);
        batchGrid.Children.Add(_batchArchiveBtn);

        _batchViewSelectedBtn = new Button
        {
            Content = IeltsI18n.T("仅看已选"),
            Classes = { "secondary" },
            Padding = new Thickness(10, 5),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12
        };
        _batchViewSelectedBtn.Click += (_, _) =>
        {
            SetWordFilter(_wordFilterType == IeltsFilterType.SelectedOnly ? IeltsFilterType.All : IeltsFilterType.SelectedOnly);
        };
        Grid.SetColumn(_batchViewSelectedBtn, 2);
        batchGrid.Children.Add(_batchViewSelectedBtn);

        _batchClearBtn = new Button
        {
            Content = IeltsI18n.T("清空"),
            Classes = { "secondary" },
            Padding = new Thickness(10, 5),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12
        };
        _batchClearBtn.Click += (_, _) =>
        {
            _selectionStore.Clear();
            _sharedSelection.Clear();
            UpdateBatchBar();
            RenderWordList();
        };
        Grid.SetColumn(_batchClearBtn, 3);
        batchGrid.Children.Add(_batchClearBtn);

        _batchPracticeBtn = new Button
        {
            Content = IeltsI18n.T("学习已选词") + " ▾",
            Classes = { "primary" },
            Padding = new Thickness(12, 5),
            FontSize = 12,
            Flyout = _practiceMenu
        };
        Grid.SetColumn(_batchPracticeBtn, 4);
        batchGrid.Children.Add(_batchPracticeBtn);

        _batchBar.Child = batchGrid;

        // 筛选工具行（支持全选当前筛选 / 反选可见）
        var selectionTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 4)
        };
        _selectAllFilteredBtn = new Button
        {
            Content = IeltsI18n.T("全选当前筛选"),
            Classes = { "ghost" },
            Padding = new Thickness(8, 4),
            FontSize = 11
        };
        _selectAllFilteredBtn.Click += (_, _) => SelectAllCurrentFilter();
        selectionTools.Children.Add(_selectAllFilteredBtn);

        _invertVisibleBtn = new Button
        {
            Content = IeltsI18n.T("反选可见"),
            Classes = { "ghost" },
            Padding = new Thickness(8, 4),
            FontSize = 11
        };
        _invertVisibleBtn.Click += (_, _) => InvertVisibleSelection();
        selectionTools.Children.Add(_invertVisibleBtn);

        var filterAndBatchStack = new StackPanel { Spacing = 6 };
        filterAndBatchStack.Children.Add(filterBar);
        filterAndBatchStack.Children.Add(selectionTools);
        filterAndBatchStack.Children.Add(_batchBar);

        Grid.SetRow(filterAndBatchStack, 2);
        rightPanel.Children.Add(filterAndBatchStack);

        // 4. 可滚动词表
        _wordScrollViewer = new ScrollViewer
        {
            Content = _wordListPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_wordScrollViewer, 3);
        rightPanel.Children.Add(_wordScrollViewer);

        Grid.SetColumn(rightPanel, 2);
        workspaceGrid.Children.Add(rightPanel);

        Grid.SetRow(workspaceGrid, 1);
        Children.Add(workspaceGrid);

        // ----------------- ROW 2: 独立紧凑播放器含 seek -----------------
        _audioBar = new IeltsAudioBar(_playerProvider, _setStatus);
        Grid.SetRow(_audioBar, 2);
        Children.Add(_audioBar);

        var chapterPicker = new ComboBox
        {
            ItemsSource = _catalog.Sections.Select(s => s.Title).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false,
            Margin = new Thickness(0, 0, 0, 8)
        };
        titlePanel.Children.Insert(0, chapterPicker);
        chapterPicker.SelectionChanged += (_, _) =>
        {
            if (chapterPicker.SelectedIndex >= 0) SelectSection(_catalog.Sections[chapterPicker.SelectedIndex]);
        };
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 700;
            workspaceGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 220);
            workspaceGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 1);
            catalogPanel.IsVisible = !compact; divider.IsVisible = !compact;
            rightPanel.Margin = new Thickness(compact ? 0 : 18, 0, 0, 0);
            chapterPicker.IsVisible = compact;
            _sectionTitleBlock.IsVisible = !compact; _sectionDescBlock.IsVisible = !compact;
            if (compact) chapterPicker.SelectedIndex = _catalog.Sections.IndexOf(_selectedSection!);
            topBar.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto" : "Auto");
            Grid.SetRow(_topNavPanel, compact ? 1 : 0); Grid.SetColumn(_topNavPanel, compact ? 0 : 1);
            Grid.SetColumnSpan(_topNavPanel, compact ? 3 : 1);
            _topNavPanel.Margin = new Thickness(compact ? 0 : 24, compact ? 6 : 0, 0, 0);
        };

        // 初始化加载默认章节
        SelectInitialSection();
    }

    public void SelectSection(LearningSection section)
    {
        if (!_catalog.Sections.Contains(section) || _selectedSection?.Id == section.Id) return;
        _selectedSection = section; _progress.SelectedSection = section.Id;
        _audioBar.LoadSection(section); RenderChapterList(); RenderWordList(); ProgressChanged?.Invoke();
    }

    private void SelectInitialSection()
    {
        if (_catalog.Sections.Count == 0) return;
        var targetId = _progress.SelectedSection;
        _selectedSection = _catalog.Sections.FirstOrDefault(s => s.Id == targetId) ?? _catalog.Sections[0];
        _audioBar.LoadSection(_selectedSection);
        RenderChapterList();
        RenderWordList();
    }

    private void RenderChapterList()
    {
        _chapterListPanel.Children.Clear();

        var query = _catalog.Sections.AsEnumerable();
        if (_chapterKindFilter != "all")
            query = query.Where(s => s.Kind == _chapterKindFilter);

        if (!string.IsNullOrWhiteSpace(_chapterSearchKeyword))
            query = query.Where(s => s.Title.Contains(_chapterSearchKeyword, StringComparison.OrdinalIgnoreCase));

        var filtered = query.ToList();

        foreach (var section in filtered)
        {
            var isCurrent = _selectedSection?.Id == section.Id;

            var row = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(2, 1)
            };
            try { row.Cursor = new Cursor(StandardCursorType.Hand); } catch { }

            // 克制选中状态（不整块高饱和长蓝条，采用 SelectionBrush 及细辅色指示）
            if (isCurrent)
            {
                row.Bind(BackgroundProperty, this.GetResourceObservable("SelectionBrush"));
                row.BorderThickness = new Thickness(3, 0, 0, 0);
                row.Bind(Border.BorderBrushProperty, this.GetResourceObservable("PrimaryGreen"));
            }
            else
            {
                row.Background = Brushes.Transparent;
                row.BorderThickness = new Thickness(0);
            }

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };

            var title = new TextBlock
            {
                Text = section.Title,
                FontSize = 13,
                FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (isCurrent)
                title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
            else
                title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));

            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            var countPill = new Border
            {
                Classes = { "badge" },
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = section.Entries.Count.ToString(),
                    Classes = { "badge-text" }
                }
            };
            Grid.SetColumn(countPill, 1);
            grid.Children.Add(countPill);

            row.Child = grid;

            // 点击切换章节（保留跨章节勾选，不被清空）
            row.PointerPressed += (_, _) =>
            {
                if (_selectedSection?.Id != section.Id)
                {
                    SelectSection(section);
                }
            };

            _chapterListPanel.Children.Add(row);
        }
    }

    private void RenderWordList()
    {
        _wordListPanel.Children.Clear();

        if (_selectedSection == null)
        {
            _sectionTitleBlock.Text = "";
            _sectionDescBlock.Text = "";
            return;
        }

        _sectionTitleBlock.Text = _selectedSection.Title + " · " + IeltsI18n.WordCountFormat(_selectedSection.Entries.Count);
        _sectionDescBlock.Text = _selectedSection.Description;

        // 根据筛选条件过滤单词（筛选与选择解耦）
        var query = _selectedSection.Entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(_wordSearchKeyword))
        {
            query = query.Where(w =>
                w.Word.Contains(_wordSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                w.Meaning.Contains(_wordSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                w.Phonetic.Contains(_wordSearchKeyword, StringComparison.OrdinalIgnoreCase));
        }

        switch (_wordFilterType)
        {
            case IeltsFilterType.Practiced:
                query = query.Where(w => _progress.Typed.Contains(w.Id));
                break;
            case IeltsFilterType.Errors:
                query = query.Where(w => _progress.Errors.Contains(w.Id));
                break;
            case IeltsFilterType.SelectedOnly:
                query = query.Where(w => _selectionStore.Contains(w.Id));
                break;
        }

        var visibleWords = query.ToList();

        foreach (var word in visibleWords)
        {
            var row = new VocabularyRow();
            var statusKind = _progress.Errors.Contains(word.Id) ? "error" : (_progress.Typed.Contains(word.Id) ? "typed" : "new");
            var isSelected = _selectionStore.Contains(word.Id);

            var isArchived = _isArchived?.Invoke(word.Word) ?? false;

            row.BindWord(
                id: word.Id,
                word: word.Word,
                phonetic: word.Phonetic,
                pos: word.Pos,
                meaning: word.Meaning,
                example: word.Example,
                extra: word.Extra,
                sourceTitle: $"{_selectedSection.Title} · Group {word.Group}",
                synonyms: word.Synonyms,
                audioPath: word.AudioPath,
                statusKind: statusKind,
                isSelected: isSelected,
                isExpanded: _expandedWords.Contains(word.Id),
                isArchived: isArchived
            );
            row.SetExpanded(_expandedWords.Contains(word.Id));
            row.ExpansionToggled += (_, expanded) =>
            {
                if (expanded) _expandedWords.Add(word.Id); else _expandedWords.Remove(word.Id);
            };

            // 档案单词收藏与查看
            row.AddToArchiveRequested += (r) =>
            {
                _archiveSingle?.Invoke(word, _selectedSection);
                r.SetArchived(_isArchived?.Invoke(word.Word) ?? true);
            };
            row.ViewArchiveRequested += (_, text) =>
            {
                _viewArchive?.Invoke(text);
            };

            // 选择操作（跨章保存）
            row.SelectionToggled += (r, selected) =>
            {
                _selectionStore.Toggle(word, _selectedSection.Id, selected);
                if (selected)
                    _sharedSelection.Add(word.Id);
                else
                    _sharedSelection.Remove(word.Id);
                UpdateBatchBar();
            };

            // 发音操作（只播放，不改选择与展开）
            row.PlayAudioRequested += (_, audioPathOrWord) =>
            {
                var asset = IeltsCatalog.ResolveAsset(audioPathOrWord);
                _playerProvider().Play(audioPathOrWord, asset);
            };

            row.PlayExampleRequested += (_, example) =>
            {
                _playerProvider().Play(example);
            };

            _wordListPanel.Children.Add(row);
            row.SaveExampleRequested += (_, english) => SaveExampleRequested?.Invoke(english);
        }

        UpdateBatchBar();
    }

    public void RefreshArchivedStatus()
    {
        foreach (var row in _wordListPanel.Children.OfType<VocabularyRow>())
        {
            var archived = _isArchived?.Invoke(row.WordText) ?? false;
            row.SetArchived(archived);
        }
    }

    private void SelectAllCurrentFilter()
    {
        var visibleWords = GetCurrentlyVisibleWords();
        foreach (var w in visibleWords)
        {
            _selectionStore.Toggle(w, _selectedSection?.Id ?? "", true);
            _sharedSelection.Add(w.Id);
        }
        UpdateBatchBar();
        RenderWordList();
    }

    private void InvertVisibleSelection()
    {
        var visibleWords = GetCurrentlyVisibleWords();
        foreach (var w in visibleWords)
        {
            var nowSelected = !_selectionStore.Contains(w.Id);
            _selectionStore.Toggle(w, _selectedSection?.Id ?? "", nowSelected);
            if (nowSelected)
                _sharedSelection.Add(w.Id);
            else
                _sharedSelection.Remove(w.Id);
        }
        UpdateBatchBar();
        RenderWordList();
    }

    private List<LearningWord> GetCurrentlyVisibleWords()
    {
        if (_selectedSection == null) return [];
        var query = _selectedSection.Entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(_wordSearchKeyword))
        {
            query = query.Where(w =>
                w.Word.Contains(_wordSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                w.Meaning.Contains(_wordSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                w.Phonetic.Contains(_wordSearchKeyword, StringComparison.OrdinalIgnoreCase));
        }

        switch (_wordFilterType)
        {
            case IeltsFilterType.Practiced:
                query = query.Where(w => _progress.Typed.Contains(w.Id));
                break;
            case IeltsFilterType.Errors:
                query = query.Where(w => _progress.Errors.Contains(w.Id));
                break;
            case IeltsFilterType.SelectedOnly:
                query = query.Where(w => _selectionStore.Contains(w.Id));
                break;
        }

        return query.ToList();
    }

    private void UpdateBatchBar()
    {
        var count = _selectionStore.Count;
        _startPracticeBtn.Classes.Set("primary", count == 0);
        _startPracticeBtn.Classes.Set("secondary", count > 0);
        _batchBar.IsVisible = count > 0;
        if (count > 0)
        {
            _batchSelectedCountBlock.Text = IeltsI18n.SelectedCountFormat(count, _selectionStore.DistinctChapterCount);
            _batchViewSelectedBtn.Content = _wordFilterType == IeltsFilterType.SelectedOnly ? IeltsI18n.T("查看全部") : IeltsI18n.T("仅看已选");
        }
        UpdatePracticeMenuAvailability();
    }

    private void SetWordFilter(IeltsFilterType filterType)
    {
        _wordFilterType = filterType;
        _filterAllBtn.Classes.Set("active", filterType == IeltsFilterType.All);
        _filterTypedBtn.Classes.Set("active", filterType == IeltsFilterType.Practiced);
        _filterErrorsBtn.Classes.Set("active", filterType == IeltsFilterType.Errors);
        _filterSelectedBtn.Classes.Set("active", filterType == IeltsFilterType.SelectedOnly);
        RenderWordList();
    }

    /// <summary>
    /// 获取参与练习的目标词库：优先使用跨章已选词；未选时使用当前章节过滤后的全部词。
    /// </summary>
    private List<LearningWord> GetTargetPracticeWords()
    {
        if (_selectionStore.Count > 0)
            return _selectionStore.GetSelectedWords();

        if (_selectedSection == null) return [];
        return _selectedSection.Entries;
    }

    private List<LearningWord> GetActiveWordsForSynonyms()
    {
        var words = GetTargetPracticeWords();
        return words.Where(w => w.Synonyms.Count > 0).ToList();
    }

    private void ExecuteTypingPractice(bool hints)
    {
        var sourceWords = GetTargetPracticeWords();
        if (sourceWords.Count == 0)
        {
            _setStatus(IeltsI18n.T("没有可训练的词汇，请先收藏或勾选词汇。"));
            return;
        }

        var roundWords = LearningRound.Select(
            sourceWords,
            _practiceSettings.Count,
            _practiceSettings.AllWords,
            _practiceSettings.RandomOrder,
            Random.Shared.Next());

        _startTyping(roundWords, hints);
    }

    /// <summary>
    /// 创建练习菜单项：前景显式绑定 InkBrush，不继承 primary 按钮的 OnPrimary 白色，
    /// 避免浅色主题下菜单白底白字。
    /// </summary>
    private MenuItem CreatePracticeMenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Bind(MenuItem.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 同义替换听写仅在对应专题（当前训练范围存在同义替换考点）可用。
    /// </summary>
    private void UpdatePracticeMenuAvailability()
    {
        if (_itemSynonyms != null)
            _itemSynonyms.IsVisible = GetTargetPracticeWords().Any(w => w.Synonyms.Count > 0);
    }

    private Button CreateNavTab(string label, bool active, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            Classes = { "ghost" },
            Padding = new Thickness(14, 6),
            FontSize = 13
        };
        try { btn.Cursor = new Cursor(StandardCursorType.Hand); } catch { }
        if (active)
        {
            btn.Classes.Add("active");
            btn.FontWeight = FontWeight.SemiBold;
        }
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button CreateCapsuleBtn(string label, bool active, Action onClick)
    {
        var btn = new Button
        {
            Content = IeltsI18n.T(label),
            Classes = { "secondary" },
            Padding = new Thickness(12, 5),
            FontSize = 12
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button CreateSmallFilterBtn(string label, string kind, Action onClick)
    {
        var btn = new Button
        {
            Content = IeltsI18n.T(label),
            Classes = { "ghost" },
            Padding = new Thickness(8, 3),
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 11
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public void RefreshLanguage()
    {
        _tabVocab.Content = IeltsI18n.T("词汇");
        _tabResources.Content = IeltsI18n.T("听力资料");
        _tabSynonyms.Content = IeltsI18n.T("阅读同义替换");
        _tabWriting.Content = IeltsI18n.T("写作练习");

        _chapterSearchBox.Watermark = IeltsI18n.T("搜索章节...");
        _wordSearchBox.Watermark = IeltsI18n.T("搜索当前章节词汇或释义...");

        _filterAllBtn.Content = IeltsI18n.T("全部");
        _filterTypedBtn.Content = IeltsI18n.T("已练习");
        _filterErrorsBtn.Content = IeltsI18n.T("错误词");
        _filterSelectedBtn.Content = IeltsI18n.T("仅看已选");

        _practiceAllCheck.Content = IeltsI18n.T("全部词");
        _practiceRandomCheck.Content = IeltsI18n.T("随机练习");
        _startPracticeBtn.Content = IeltsI18n.T("开始练习") + " ▾";
        _toggleSettingsBtn.Content = "⚙ " + IeltsI18n.T("练习设置");
        _createPlanBtn.Content = IeltsI18n.T("创建每日计划");
        _itemHinted.Header = IeltsI18n.T("拼写练习");
        _itemSynonyms.Header = IeltsI18n.T("同义替换听写");

        _batchArchiveBtn.Content = IeltsI18n.T("收藏到档案");
        _batchClearBtn.Content = IeltsI18n.T("清空");
        _batchPracticeBtn.Content = IeltsI18n.T("学习已选词") + " ▾";
        _selectAllFilteredBtn.Content = IeltsI18n.T("全选当前筛选");
        _invertVisibleBtn.Content = IeltsI18n.T("反选可见");

        _audioBar.RefreshLanguage();
        UpdateBatchBar();

        foreach (var child in _wordListPanel.Children.OfType<VocabularyRow>())
            child.RefreshLanguage();
    }

    public void Dispose()
    {
        _audioBar.Dispose();
    }
}
