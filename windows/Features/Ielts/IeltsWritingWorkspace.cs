using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lexi.Controls;

using Path = Avalonia.Controls.Shapes.Path;

namespace Lexi.Features.Ielts;



/// <summary>
/// 雅思 100 句写作工作区：
/// - 一次一道题，左右布局（窄窗自适应上下），单层 ScrollViewer 杜绝嵌套滚动条。
/// - 中文题干英文输入，原题/参考答案/语法解析均采用 SelectableTextBlock，支持自由选择、一键复制与语音朗读。
/// - 参考答案默认隐藏，按需分段展示（书中答案、备用译文、语法解析）。
/// - 输入框动态命名为 WritingDraftN，支持短延迟防抖合并写入与 FlushPendingSave 强制刷盘。
/// - 异常隔离安全机制：保存失败保留输入并给予明确视觉反馈，导航前强制校验拦截，防止用户心血丢失。
/// - 全面支持 Lexi.UiText.Bilingual，RefreshLanguage 严格保留光标与当前 draft。
/// </summary>
public sealed class IeltsWritingWorkspace : Grid, IDisposable
{
    private enum SaveState
    {
        Idle,
        Pending,
        Saving,
        Saved,
        Failed
    }

    private readonly IeltsCatalog _catalog;
    private readonly Dictionary<int, string> _drafts;
    private readonly Action _save;
    private readonly Action<string> _speak;
    private readonly Action _back;

    private readonly List<WritingSentence> _sentences;
    private int _currentIndex = 0;
    private bool _showReference = false;
    private bool _isUpdatingUi = false;

    // 自动保存与防抖
    private readonly DispatcherTimer _debounceTimer;
    private bool _hasPendingSave = false;
    private bool _isSaving = false;
    private bool _saveFailed = false;
    private string _lastSaveError = "";

    // 顶部操作栏控件
    private readonly TextBlock _titleBlock;
    private readonly TextBlock _saveStatusBlock;
    private readonly TextBlock _feedbackBlock;
    private readonly Button _backBtn;

    // 导航栏控件
    private readonly Button _prevBtn;
    private readonly Button _nextBtn;
    private readonly ComboBox _sentenceSelector;
    private readonly TextBlock _counterBlock;
    private readonly Border _categoryChip;
    private readonly TextBlock _categoryText;

    // 核心内容区控件
    private readonly Grid _contentGrid;
    private readonly Grid _editorGrid;
    private readonly Border _leftPanel;
    private readonly Border _rightPanel;
    private readonly ScrollViewer _scrollViewer;

    // 左侧（中文题干与英文输入）
    private readonly TextBlock _promptHeader;
    private readonly SelectableTextBlock _promptBlock;
    private readonly Button _promptListenBtn;
    private readonly Button _promptCopyBtn;
    private readonly TextBlock _inputHeader;
    private readonly Button _draftListen;
    private readonly TextBox _draftInput;

    // 右侧（参考答案按需分段）
    private readonly Button _toggleRefBtn;
    private readonly StackPanel _referenceContainer;

    // 参考答案分段
    private readonly Border _bookAnswerCard;
    private readonly TextBlock _bookAnswerHeader;
    private readonly SelectableTextBlock _bookAnswerBlock;
    private readonly Button _bookListenBtn;
    private readonly Button _bookCopyBtn;

    private readonly Border _altAnswerCard;
    private readonly TextBlock _alternateAnswerHeader;
    private readonly SelectableTextBlock _altAnswerBlock;
    private readonly Button _altListenBtn;
    private readonly Button _altCopyBtn;

    private readonly Border _remarkCard;
    private readonly TextBlock _remarkHeader;
    private readonly SelectableTextBlock _remarkBlock;
    private readonly Button _remarkCopyBtn;

    // 公开属性供自测与主控接入
    public TextBox DraftInput => _draftInput;
    public Button PrevButton => _prevBtn;
    public Button NextButton => _nextBtn;
    public Button BackButton => _backBtn;
    public Button ToggleReferenceButton => _toggleRefBtn;
    public ComboBox SentenceSelector => _sentenceSelector;
    public SelectableTextBlock PromptTextBlock => _promptBlock;
    public SelectableTextBlock BookAnswerTextBlock => _bookAnswerBlock;
    public SelectableTextBlock AlternateAnswerTextBlock => _altAnswerBlock;
    public SelectableTextBlock RemarkTextBlock => _remarkBlock;
    public StackPanel ReferenceContainer => _referenceContainer;
    public TextBlock SaveStatusBlock => _saveStatusBlock;
    public int CurrentIndex => _currentIndex;
    public WritingSentence? CurrentSentence => _sentences.Count > 0 && _currentIndex >= 0 && _currentIndex < _sentences.Count ? _sentences[_currentIndex] : null;

    public IeltsWritingWorkspace(
        IeltsCatalog catalog,
        Dictionary<int, string> drafts,
        Action save,
        Action<string> speak,
        Action back)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _speak = speak ?? throw new ArgumentNullException(nameof(speak));
        _back = back ?? throw new ArgumentNullException(nameof(back));

        _sentences = _catalog.Sentences?.ToList() ?? [];

        // 防抖计时器：350ms
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ExecuteSave();
        };

        // 根布局：Row 0 顶部栏, Row 1 导航栏, Row 2 单层滚动内容区
        RowDefinitions = new RowDefinitions("Auto,Auto,*");
        Background = Brushes.Transparent;

        // ------------------ Row 0: 顶部操作栏 ------------------
        var topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(16, 12, 16, 8),
            VerticalAlignment = VerticalAlignment.Center
        };

        _titleBlock = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(_titleBlock, 0);
        topBar.Children.Add(_titleBlock);

        _feedbackBlock = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _feedbackBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
        Grid.SetColumn(_feedbackBlock, 1);
        topBar.Children.Add(_feedbackBlock);

        _saveStatusBlock = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_saveStatusBlock, 2);
        topBar.Children.Add(_saveStatusBlock);

        _backBtn = new Button
        {
            Classes = { "secondary" },
            Padding = new Thickness(12, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        _backBtn.Click += (_, _) =>
        {
            if (!FlushPendingSave()) return;
            _back();
        };
        Grid.SetColumn(_backBtn, 3);
        topBar.Children.Add(_backBtn);

        Grid.SetRow(topBar, 0);
        Children.Add(topBar);

        // ------------------ Row 1: 题号导航与选择 ------------------
        var navBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(16, 4, 16, 12),
            VerticalAlignment = VerticalAlignment.Center
        };

        _prevBtn = new Button
        {
            Classes = { "secondary" },
            Padding = new Thickness(10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _prevBtn.Click += (_, _) =>
        {
            if (_currentIndex > 0) NavigateToIndex(_currentIndex - 1);
        };
        Grid.SetColumn(_prevBtn, 0);
        navBar.Children.Add(_prevBtn);

        _nextBtn = new Button
        {
            Classes = { "secondary" },
            Padding = new Thickness(10, 5),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _nextBtn.Click += (_, _) =>
        {
            if (_currentIndex < _sentences.Count - 1) NavigateToIndex(_currentIndex + 1);
        };
        Grid.SetColumn(_nextBtn, 1);
        navBar.Children.Add(_nextBtn);

        _sentenceSelector = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            MaxDropDownHeight = 350
        };
        _sentenceSelector.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingUi) return;
            var idx = _sentenceSelector.SelectedIndex;
            if (idx >= 0 && idx != _currentIndex)
            {
                NavigateToIndex(idx);
            }
        };
        Grid.SetColumn(_sentenceSelector, 2);
        navBar.Children.Add(_sentenceSelector);

        _categoryText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold
        };
        _categoryChip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _categoryText
        };
        _categoryChip.Bind(BackgroundProperty, this.GetResourceObservable("TintBrush"));
        Grid.SetColumn(_categoryChip, 3);
        navBar.Children.Add(_categoryChip);

        _counterBlock = new TextBlock
        {
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_counterBlock, 4);
        navBar.Children.Add(_counterBlock);

        Grid.SetRow(navBar, 1);
        Children.Add(navBar);

        // ------------------ Row 2: 单层滚动内容区 ------------------
        _contentGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto"),
            Margin = new Thickness(16, 0, 16, 16)
        };

        // --- 左侧面板：中文题干与英文输入 ---
        var leftStack = new StackPanel { Spacing = 12 };

        // 题干卡片
        var promptCard = new Border
        {
            Classes = { "panel-card" },
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12)
        };
        promptCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        promptCard.Bind(BackgroundProperty, this.GetResourceObservable("CardBrush"));

        var promptStack = new StackPanel { Spacing = 8 };
        var promptTopRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        _promptHeader = new TextBlock
        {
            Classes = { "eyebrow" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_promptHeader, 0);
        promptTopRow.Children.Add(_promptHeader);

        _promptListenBtn = CreateActionButton("M 3,6 L 6,6 L 10,2 L 10,14 L 6,10 L 3,10 Z M 12,5.5 C 13.5,7 13.5,9 12,10.5");
        _promptListenBtn.Click += (_, _) =>
        {
            if (CurrentSentence != null && !string.IsNullOrWhiteSpace(CurrentSentence.Chinese))
                _speak(CurrentSentence.Chinese);
        };
        Grid.SetColumn(_promptListenBtn, 1);
        promptTopRow.Children.Add(_promptListenBtn);

        _promptCopyBtn = CreateActionButton("M 4,4 L 10,4 L 10,12 L 4,12 Z M 6,2 L 12,2 L 12,10");
        _promptCopyBtn.Click += (_, _) =>
        {
            if (CurrentSentence != null) CopyToClipboard(CurrentSentence.Chinese);
        };
        Grid.SetColumn(_promptCopyBtn, 2);
        promptTopRow.Children.Add(_promptCopyBtn);

        promptStack.Children.Add(promptTopRow);

        _promptBlock = new SelectableTextBlock
        {
            FontSize = 18,
            LineHeight = 29,
            TextWrapping = TextWrapping.Wrap
        };
        promptStack.Children.Add(_promptBlock);
        promptCard.Child = promptStack;
        leftStack.Children.Add(promptCard);

        // 输入卡片
        var inputCard = new Border
        {
            Classes = { "panel-card" },
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12)
        };
        inputCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        inputCard.Bind(BackgroundProperty, this.GetResourceObservable("CardBrush"));

        var inputStack = new StackPanel { Spacing = 8 };
        _inputHeader = new TextBlock
        {
            Classes = { "eyebrow" },
            FontSize = 12
        };
        var inputHeaderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        inputHeaderRow.Children.Add(_inputHeader);
        _draftListen = new Lexi.Controls.InlineAudioButton() { Name = "WritingDraftListen" };
        _draftListen.Click += (_,_) => { if(!string.IsNullOrWhiteSpace(DraftInput.Text))_speak(DraftInput.Text); };
        Grid.SetColumn(_draftListen,1); inputHeaderRow.Children.Add(_draftListen);
        inputStack.Children.Add(inputHeaderRow);

        _draftInput = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 210,
            FontSize = 16,
            LineHeight = 26
        };
        _draftInput.PropertyChanged += (_, e) => { if(e.Property==TextBox.TextProperty)OnDraftTextChanged(); };
        inputStack.Children.Add(_draftInput);

        inputCard.Child = inputStack;
        leftStack.Children.Add(inputCard);

        _editorGrid = new Grid { ColumnSpacing = 20, RowSpacing = 16 };
        leftStack.Children.Remove(promptCard); leftStack.Children.Remove(inputCard);
        _editorGrid.Children.Add(promptCard); _editorGrid.Children.Add(inputCard);
        leftStack.Children.Add(_editorGrid);

        _leftPanel = new Border { Child = leftStack };
        Grid.SetColumn(_leftPanel, 0);
        Grid.SetRow(_leftPanel, 0);
        _contentGrid.Children.Add(_leftPanel);

        // --- 右侧面板：参考答案按需分段 ---
        var rightStack = new StackPanel { Spacing = 12 };

        _toggleRefBtn = new Button
        {
            Classes = { "secondary" },
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _toggleRefBtn.Click += (_, _) =>
        {
            _showReference = !_showReference;
            UpdateReferenceVisibility();
        };
        rightStack.Children.Add(_toggleRefBtn);

        _referenceContainer = new StackPanel { Spacing = 10 };

        // 1. 书中答案分段
        _bookAnswerCard = CreateSegmentCard();
        var bookStack = new StackPanel { Spacing = 6 };
        var bookTop = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _bookAnswerHeader = new TextBlock { Classes = { "eyebrow" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_bookAnswerHeader, 0);
        bookTop.Children.Add(_bookAnswerHeader);

        _bookListenBtn = CreateActionButton("M 3,6 L 6,6 L 10,2 L 10,14 L 6,10 L 3,10 Z M 12,5.5 C 13.5,7 13.5,9 12,10.5");
        _bookListenBtn.Click += (_, _) =>
        {
            if (CurrentSentence != null && !string.IsNullOrWhiteSpace(CurrentSentence.BookAnswer))
                _speak(CurrentSentence.BookAnswer);
        };
        Grid.SetColumn(_bookListenBtn, 1);
        bookTop.Children.Add(_bookListenBtn);

        _bookCopyBtn = CreateActionButton("M 4,4 L 10,4 L 10,12 L 4,12 Z M 6,2 L 12,2 L 12,10");
        _bookCopyBtn.Click += (_, _) =>
        {
            if (CurrentSentence != null) CopyToClipboard(CurrentSentence.BookAnswer);
        };
        Grid.SetColumn(_bookCopyBtn, 2);
        bookTop.Children.Add(_bookCopyBtn);

        bookStack.Children.Add(bookTop);
        _bookAnswerBlock = new SelectableTextBlock
        {
            Classes = { "example-en" },
            FontSize = 16,
            LineHeight = 26,
            TextWrapping = TextWrapping.Wrap
        };
        bookStack.Children.Add(_bookAnswerBlock);
        _bookAnswerCard.Child = bookStack;
        _referenceContainer.Children.Add(_bookAnswerCard);

        // 2. 备用译文分段
        _altAnswerCard = CreateSegmentCard();
        var altStack = new StackPanel { Spacing = 6 };
        var altTop = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _alternateAnswerHeader = new TextBlock { Classes = { "eyebrow" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_alternateAnswerHeader, 0);
        altTop.Children.Add(_alternateAnswerHeader);

        _altListenBtn = CreateActionButton("M 3,6 L 6,6 L 10,2 L 10,14 L 6,10 L 3,10 Z M 12,5.5 C 13.5,7 13.5,9 12,10.5");
        _altListenBtn.Click += (_, _) =>
        {
            if (CurrentSentence != null && !string.IsNullOrWhiteSpace(CurrentSentence.AlternateAnswer))
                _speak(CurrentSentence.AlternateAnswer);
        };
        Grid.SetColumn(_altListenBtn, 1);
        altTop.Children.Add(_altListenBtn);

        _altCopyBtn = CreateActionButton("M 4,4 L 10,4 L 10,12 L 4,12 Z M 6,2 L 12,2 L 12,10");
        _altCopyBtn.Click += (_, _) =>
        {
            if (CurrentSentence != null) CopyToClipboard(CurrentSentence.AlternateAnswer);
        };
        Grid.SetColumn(_altCopyBtn, 2);
        altTop.Children.Add(_altCopyBtn);

        altStack.Children.Add(altTop);
        _altAnswerBlock = new SelectableTextBlock
        {
            Classes = { "example-en" },
            FontSize = 16,
            LineHeight = 26,
            TextWrapping = TextWrapping.Wrap
        };
        altStack.Children.Add(_altAnswerBlock);
        _altAnswerCard.Child = altStack;
        _referenceContainer.Children.Add(_altAnswerCard);

        // 3. 语法解析与备注分段
        _remarkCard = CreateSegmentCard();
        var remarkStack = new StackPanel { Spacing = 6 };
        var remarkTop = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _remarkHeader = new TextBlock { Classes = { "eyebrow" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_remarkHeader, 0);
        remarkTop.Children.Add(_remarkHeader);

        _remarkCopyBtn = CreateActionButton("M 4,4 L 10,4 L 10,12 L 4,12 Z M 6,2 L 12,2 L 12,10");
        _remarkCopyBtn.Click += (_, _) =>
        {
            if (CurrentSentence != null) CopyToClipboard(CurrentSentence.Remark);
        };
        Grid.SetColumn(_remarkCopyBtn, 1);
        remarkTop.Children.Add(_remarkCopyBtn);

        remarkStack.Children.Add(remarkTop);
        _remarkBlock = new SelectableTextBlock
        {
            Classes = { "muted" },
            FontSize = 14,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap
        };
        remarkStack.Children.Add(_remarkBlock);
        _remarkCard.Child = remarkStack;
        _referenceContainer.Children.Add(_remarkCard);

        rightStack.Children.Add(_referenceContainer);

        _rightPanel = new Border
        {
            Margin = new Thickness(14, 0, 0, 0),
            Child = rightStack
        };
        Grid.SetColumn(_rightPanel, 1);
        Grid.SetRow(_rightPanel, 0);
        _contentGrid.Children.Add(_rightPanel);

        // 单层 ScrollViewer 包裹核心区域
        _scrollViewer = new ScrollViewer
        {
            Content = _contentGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_scrollViewer, 2);
        Children.Add(_scrollViewer);

        // 响应式窄窗自适应（小于 720DIP 切换上下排列）
        SizeChanged += (_, e) => UpdateResponsiveLayout(e.NewSize.Width);

        // 初始化
        UpdateSelectorItems();
        BindCurrentSentence();
        UpdateReferenceVisibility();
        RefreshLanguage();
    }

    private Border CreateSegmentCard()
    {
        var card = new Border
        {
            Classes = { "panel-card" },
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10)
        };
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        card.Bind(BackgroundProperty, this.GetResourceObservable("CardBrush"));
        return card;
    }

    private Button CreateActionButton(string pathData)
    {
        var icon = new Path
        {
            Data = SafeParseGeometry(pathData),
            StrokeThickness = 1.2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 14,
            Height = 14
        };
        icon.Bind(Shape.StrokeProperty, this.GetResourceObservable("MutedBrush"));

        return new Button
        {
            Classes = { "row-btn" },
            Padding = new Thickness(4, 2),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = icon
        };
    }

    private void UpdateResponsiveLayout(double width)
    {
        var narrow=width<720;
        _contentGrid.ColumnDefinitions=new ColumnDefinitions("*");
        _contentGrid.RowDefinitions=new RowDefinitions("Auto,Auto");
        Grid.SetColumn(_leftPanel,0);Grid.SetRow(_leftPanel,0);
        Grid.SetColumn(_rightPanel,0);Grid.SetRow(_rightPanel,1);
        _rightPanel.Margin=new Thickness(0,20,0,0);
        _editorGrid.ColumnDefinitions=new ColumnDefinitions(narrow?"*":"*,*");
        _editorGrid.RowDefinitions=new RowDefinitions(narrow?"Auto,Auto":"Auto");
        Grid.SetColumn(_editorGrid.Children[1],narrow?0:1);
        Grid.SetRow(_editorGrid.Children[1],narrow?1:0);
    }

    private void UpdateSelectorItems()
    {
        _isUpdatingUi = true;
        try
        {
            var items = new List<string>();
            foreach (var s in _sentences)
            {
                var summary = s.Chinese.Length > 24 ? s.Chinese[..24] + "..." : s.Chinese;
                items.Add($"{s.Number:00} · {s.Category} · {summary}");
            }
            _sentenceSelector.ItemsSource = items;
            if (_currentIndex >= 0 && _currentIndex < items.Count)
            {
                _sentenceSelector.SelectedIndex = _currentIndex;
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void BindCurrentSentence()
    {
        if (_sentences.Count == 0)
        {
            _promptBlock.Text = Lexi.UiText.Bilingual("暂无翻译练习题目。", "No writing exercises available.");
            _draftInput.Text = "";
            _draftInput.IsEnabled = false;
            _prevBtn.IsEnabled = false;
            _nextBtn.IsEnabled = false;
            _counterBlock.Text = "0 / 0";
            _categoryText.Text = "";
            return;
        }

        var sentence = CurrentSentence!;

        _isUpdatingUi = true;
        try
        {
            _promptBlock.Text = sentence.Chinese;
            _draftInput.Name = $"WritingDraft{sentence.Number}";
            _draftInput.Text = _drafts.GetValueOrDefault(sentence.Number, "");
            _draftInput.IsEnabled = true;

            _bookAnswerBlock.Text = sentence.BookAnswer;
            _altAnswerBlock.Text = sentence.AlternateAnswer;
            _remarkBlock.Text = sentence.Remark;

            // 按需分段：无内容的分段不强行占位展示
            _altAnswerCard.IsVisible = !string.IsNullOrWhiteSpace(sentence.AlternateAnswer);
            _remarkCard.IsVisible = !string.IsNullOrWhiteSpace(sentence.Remark);

            _prevBtn.IsEnabled = _currentIndex > 0;
            _nextBtn.IsEnabled = _currentIndex < _sentences.Count - 1;
            _counterBlock.Text = $"{sentence.Number:00} / {_sentences.Count:00}";
            _categoryText.Text = sentence.Category;

            _sentenceSelector.SelectedIndex = _currentIndex;

            _hasPendingSave = false;
            _saveFailed = false;
            UpdateSaveStatusVisual(SaveState.Idle);
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void UpdateReferenceVisibility()
    {
        _referenceContainer.IsVisible = _showReference;
        _toggleRefBtn.Content = _showReference
            ? Lexi.UiText.Bilingual("隐藏参考答案", "Hide Reference")
            : Lexi.UiText.Bilingual("显示参考答案", "Show Reference");
    }

    private void OnDraftTextChanged()
    {
        if (_isUpdatingUi) return;
        if (CurrentSentence == null) return;

        var text = _draftInput.Text ?? "";
        _drafts[CurrentSentence.Number] = text;
        _hasPendingSave = true;
        _saveFailed = false;

        UpdateSaveStatusVisual(SaveState.Pending);

        // 重启防抖计时器
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>
    /// 强制提交待保存的 draft。保存成功返回 true；保存抛出异常保留输入并反馈未保存，返回 false。
    /// </summary>
    public bool FlushPendingSave()
    {
        _debounceTimer.Stop();
        if (CurrentSentence == null) return true;

        // 总是合并最新文本到草稿字典
        var text = _draftInput.Text ?? "";
        _drafts[CurrentSentence.Number] = text;

        if (!_hasPendingSave)
        {
            return !_saveFailed;
        }

        return ExecuteSave();
    }

    private bool ExecuteSave()
    {
        if (_isSaving) return false;
        if (CurrentSentence != null)
        {
            _drafts[CurrentSentence.Number] = _draftInput.Text ?? "";
        }

        try
        {
            _isSaving = true;
            UpdateSaveStatusVisual(SaveState.Saving);
            _save();
            _hasPendingSave = false;
            _saveFailed = false;
            _lastSaveError = "";
            UpdateSaveStatusVisual(SaveState.Saved);
            return true;
        }
        catch (Exception ex)
        {
            _saveFailed = true;
            _lastSaveError = ex.Message;
            UpdateSaveStatusVisual(SaveState.Failed);
            SetFeedback(Lexi.UiText.Bilingual("保存失败，输入已保留，请重试。", "Save failed. Your text is preserved; please retry."), isError: true);
            return false;
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// 导航至指定题目。若当前草稿保存失败，则拦截导航以防丢失输入。
    /// </summary>
    private bool NavigateToIndex(int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= _sentences.Count) return false;
        if (targetIndex == _currentIndex) return true;

        // 导航前强制 Flush
        if (!FlushPendingSave())
        {
            SetFeedback(Lexi.UiText.Bilingual("当前草稿未保存成功，请检查后重试！", "Draft not saved; please retry before navigating!"), isError: true);
            // 还原 ComboBox 选中项以保持界面一致
            _isUpdatingUi = true;
            try { _sentenceSelector.SelectedIndex = _currentIndex; }
            finally { _isUpdatingUi = false; }
            return false;
        }

        _currentIndex = targetIndex;
        BindCurrentSentence();
        return true;
    }

    private void UpdateSaveStatusVisual(SaveState state)
    {
        switch (state)
        {
            case SaveState.Pending:
                _saveStatusBlock.Text = Lexi.UiText.Bilingual("有待保存修改...", "Unsaved changes...");
                _saveStatusBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
                break;
            case SaveState.Saving:
                _saveStatusBlock.Text = Lexi.UiText.Bilingual("正在保存...", "Saving...");
                _saveStatusBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
                break;
            case SaveState.Saved:
                _saveStatusBlock.Text = Lexi.UiText.Bilingual("已保存", "Saved");
                _saveStatusBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
                break;
            case SaveState.Failed:
                _saveStatusBlock.Text = Lexi.UiText.Bilingual("未保存 (保存失败)", "Unsaved (Save Failed)");
                _saveStatusBlock.Foreground = Brushes.IndianRed;
                break;
            default:
                _saveStatusBlock.Text = "";
                break;
        }
    }

    private void SetFeedback(string text, bool isError = false)
    {
        _feedbackBlock.Text = text;
        if (isError)
            _feedbackBlock.Foreground = Brushes.IndianRed;
        else
            _feedbackBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
    }

    private async void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
                SetFeedback(Lexi.UiText.Bilingual("已复制到剪贴板", "Copied to clipboard"));
            }
        }
        catch
        {
            // 无头或离线环境不抛出剪贴板异常
        }
    }

    /// <summary>
    /// 双语切换并保留当前光标和 draft 输入
    /// </summary>
    public void RefreshLanguage()
    {
        var caret = _draftInput.CaretIndex;
        var selStart = _draftInput.SelectionStart;
        var selEnd = _draftInput.SelectionEnd;
        var currentText = _draftInput.Text;

        _titleBlock.Text = Lexi.UiText.Bilingual("100 句翻译练习", "100 Sentences Writing Practice");
        _backBtn.Content = Lexi.UiText.Bilingual("返回 IELTS 目录", "Back to IELTS Catalog");
        _prevBtn.Content = Lexi.UiText.Bilingual("上一题", "Previous");
        _nextBtn.Content = Lexi.UiText.Bilingual("下一题", "Next");
        _promptHeader.Text = Lexi.UiText.Bilingual("中文题干", "Chinese Prompt");
        _inputHeader.Text = Lexi.UiText.Bilingual("英文输入", "English Translation");
        ToolTip.SetTip(_draftListen, Lexi.UiText.Bilingual("朗读我的译文", "Read my translation"));
        _draftInput.Watermark = Lexi.UiText.Bilingual("输入你的英文翻译，自动保存", "Type your English translation; auto-saved");

        _toggleRefBtn.Content = _showReference
            ? Lexi.UiText.Bilingual("隐藏参考答案", "Hide Reference")
            : Lexi.UiText.Bilingual("显示参考答案", "Show Reference");

        _bookAnswerHeader.Text = Lexi.UiText.Bilingual("书中答案", "Book Answer");
        _alternateAnswerHeader.Text = Lexi.UiText.Bilingual("备用译文", "Alternate Answer");
        _remarkHeader.Text = Lexi.UiText.Bilingual("语法与解析", "Grammar & Remarks");

        _promptListenBtn.SetValue(ToolTip.TipProperty, Lexi.UiText.Bilingual("朗读原题", "Listen to Prompt"));
        _promptCopyBtn.SetValue(ToolTip.TipProperty, Lexi.UiText.Bilingual("复制原题", "Copy Prompt"));
        _bookListenBtn.SetValue(ToolTip.TipProperty, Lexi.UiText.Bilingual("朗读参考译文", "Read Reference Answer"));
        _bookCopyBtn.SetValue(ToolTip.TipProperty, Lexi.UiText.Bilingual("复制参考译文", "Copy Reference Answer"));
        _altListenBtn.SetValue(ToolTip.TipProperty, Lexi.UiText.Bilingual("朗读备用译文", "Read Alternate Answer"));
        _altCopyBtn.SetValue(ToolTip.TipProperty, Lexi.UiText.Bilingual("复制备用译文", "Copy Alternate Answer"));
        _remarkCopyBtn.SetValue(ToolTip.TipProperty, Lexi.UiText.Bilingual("复制解析", "Copy Remarks"));

        UpdateSaveStatusVisual(_saveFailed ? SaveState.Failed : (_hasPendingSave ? SaveState.Pending : SaveState.Saved));
        UpdateSelectorItems();

        // 严格保留光标与 draft 文本
        _isUpdatingUi = true;
        try
        {
            _draftInput.Text = currentText;
            _draftInput.CaretIndex = caret;
            _draftInput.SelectionStart = selStart;
            _draftInput.SelectionEnd = selEnd;
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        FlushPendingSave();
    }

    private static Geometry? SafeParseGeometry(string data)
    {
        try { return Geometry.Parse(data); }
        catch { return null; }
    }
}


