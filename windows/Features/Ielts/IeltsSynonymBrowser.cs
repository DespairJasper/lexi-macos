using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Lexi.Features.Ielts;

/// <summary>
/// 独立阅读同义替换浏览页：
/// - 展示全部同义组，支持章节筛选与实时搜索；
/// - 呈现 原词 ➔ 替换表达、词性、释义、例句与真实教材来源；
/// - 当前章无内容时呈现清晰空状态与“查看全部同义替换”入口；
/// - 从浏览页可一键进入同义替换听写练习。
/// </summary>
public sealed class IeltsSynonymBrowser : Grid
{
    private readonly IeltsCatalog _catalog;
    private readonly Func<IWordAudioPlayer> _playerProvider;
    private readonly Action<List<LearningWord>> _startPractice;
    private readonly Action _onBack;

    private readonly ComboBox _chapterFilterCombo;
    private readonly TextBox _searchBox;
    private readonly TextBlock _countBlock;
    private readonly StackPanel _listPanel = new() { Spacing = 10 };
    private readonly ScrollViewer _scrollViewer;
    private readonly Button _startPracticeBtn;
    private readonly Button _backBtn;
    private readonly TextBlock _titleBlock;

    private string _selectedSectionId = "all";
    private string _searchKeyword = "";

    public IeltsSynonymBrowser(
        IeltsCatalog catalog,
        LearningSection? initialSection,
        Func<IWordAudioPlayer> playerProvider,
        Action<List<LearningWord>> startPractice,
        Action onBack)
    {
        _catalog = catalog;
        _playerProvider = playerProvider;
        _startPractice = startPractice;
        _onBack = onBack;

        RowDefinitions = new RowDefinitions("Auto,Auto,*");
        Margin = new Thickness(24, 16, 24, 16);

        // ---------------- ROW 0: 顶部标题与动作栏 ----------------
        var topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 14)
        };

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _titleBlock = new TextBlock
        {
            Text = UiText.T("阅读同义替换"),
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        titleStack.Children.Add(_titleBlock);

        var totalSynonyms = _catalog.AllWords.Count(w => w.Synonyms.Count > 0);
        var totalBadge = new Border
        {
            Classes = { "badge" },
            Child = new TextBlock
            {
                Text = UiText.Format($"共 {totalSynonyms} 组同义替换"),
                Classes = { "badge-text" }
            }
        };
        titleStack.Children.Add(totalBadge);
        Grid.SetColumn(titleStack, 0);
        topBar.Children.Add(titleStack);

        var actionStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        _startPracticeBtn = new Button
        {
            Content = UiText.T("开始练习"),
            Classes = { "primary" },
            Padding = new Thickness(16, 7),
            FontSize = 13
        };
        _startPracticeBtn.Click += (_, _) =>
        {
            var targetWords = GetMatchingWords();
            if (targetWords.Count == 0)
                targetWords = _catalog.AllWords.Where(w => w.Synonyms.Count > 0).ToList();
            _startPractice(targetWords);
        };
        actionStack.Children.Add(_startPracticeBtn);

        _backBtn = new Button
        {
            Content = UiText.T("返回 IELTS 目录"),
            Classes = { "secondary" },
            Padding = new Thickness(14, 7),
            FontSize = 13
        };
        _backBtn.Click += (_, _) => _onBack();
        actionStack.Children.Add(_backBtn);

        Grid.SetColumn(actionStack, 1);
        topBar.Children.Add(actionStack);

        Grid.SetRow(topBar, 0);
        Children.Add(topBar);

        // ---------------- ROW 1: 筛选与搜索工具条 ----------------
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,*,Auto"),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var chapterOptions = new List<ChapterOption>
        {
            new("all", UiText.T("全部章节"))
        };

        foreach (var sec in _catalog.Sections)
        {
            var synCount = sec.Entries.Count(e => e.Synonyms.Count > 0);
            var title = synCount > 0 ? $"{sec.Title} ({synCount})" : sec.Title;
            chapterOptions.Add(new ChapterOption(sec.Id, title));
        }

        _chapterFilterCombo = new ComboBox
        {
            ItemsSource = chapterOptions,
            DisplayMemberBinding = new Avalonia.Data.Binding("Display"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 10, 0)
        };

        if (initialSection != null && chapterOptions.Any(o => o.Id == initialSection.Id))
        {
            _selectedSectionId = initialSection.Id;
            _chapterFilterCombo.SelectedItem = chapterOptions.First(o => o.Id == initialSection.Id);
        }
        else
        {
            _chapterFilterCombo.SelectedIndex = 0;
        }

        _chapterFilterCombo.SelectionChanged += (_, _) =>
        {
            if (_chapterFilterCombo.SelectedItem is ChapterOption opt)
            {
                _selectedSectionId = opt.Id;
                RenderList();
            }
        };
        Grid.SetColumn(_chapterFilterCombo, 0);
        toolbar.Children.Add(_chapterFilterCombo);

        _searchBox = new TextBox
        {
            Watermark = UiText.T("搜索考点词、同义表达或释义..."),
            Margin = new Thickness(0, 0, 12, 0),
            Padding = new Thickness(10, 6),
            FontSize = 12
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _searchKeyword = _searchBox.Text ?? "";
            RenderList();
        };
        Grid.SetColumn(_searchBox, 1);
        toolbar.Children.Add(_searchBox);

        _countBlock = new TextBlock
        {
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_countBlock, 2);
        toolbar.Children.Add(_countBlock);

        Grid.SetRow(toolbar, 1);
        Children.Add(toolbar);

        // ---------------- ROW 2: 滚动内容列表 ----------------
        _scrollViewer = new ScrollViewer
        {
            Content = _listPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_scrollViewer, 2);
        Children.Add(_scrollViewer);

        RenderList();
    }

    public void SetSectionFilter(string sectionId)
    {
        if (_chapterFilterCombo.ItemsSource is IEnumerable<ChapterOption> options)
        {
            var match = options.FirstOrDefault(o => o.Id == sectionId);
            if (match != null)
            {
                _chapterFilterCombo.SelectedItem = match;
                return;
            }
        }
        _selectedSectionId = sectionId;
        RenderList();
    }

    private List<LearningWord> GetMatchingWords()
    {
        IEnumerable<(LearningSection Section, LearningWord Word)> query = _catalog.Sections
            .SelectMany(s => s.Entries.Select(w => (Section: s, Word: w)))
            .Where(p => p.Word.Synonyms.Count > 0);

        if (_selectedSectionId != "all")
            query = query.Where(p => p.Section.Id == _selectedSectionId);

        if (!string.IsNullOrWhiteSpace(_searchKeyword))
        {
            var kw = _searchKeyword.Trim();
            query = query.Where(p =>
                p.Word.Word.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.Word.Meaning.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.Word.Synonyms.Any(s => s.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        }

        return query.Select(p => p.Word).ToList();
    }

    private void RenderList()
    {
        _listPanel.Children.Clear();

        IEnumerable<(LearningSection Section, LearningWord Word)> query = _catalog.Sections
            .SelectMany(s => s.Entries.Select(w => (Section: s, Word: w)))
            .Where(p => p.Word.Synonyms.Count > 0);

        var isFilteredByChapter = _selectedSectionId != "all";
        if (isFilteredByChapter)
            query = query.Where(p => p.Section.Id == _selectedSectionId);

        var sectionWords = query.ToList();

        if (!string.IsNullOrWhiteSpace(_searchKeyword))
        {
            var kw = _searchKeyword.Trim();
            sectionWords = sectionWords.Where(p =>
                p.Word.Word.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.Word.Meaning.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.Word.Synonyms.Any(s => s.Contains(kw, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        _countBlock.Text = UiText.Format($"当前显示 {sectionWords.Count} 组");
        _startPracticeBtn.IsEnabled = sectionWords.Count > 0;

        // 空状态处理
        if (sectionWords.Count == 0)
        {
            if (isFilteredByChapter && string.IsNullOrWhiteSpace(_searchKeyword))
            {
                // 当前章没有同义词，呈现具有引导性的空状态
                var emptyCard = new Border
                {
                    Classes = { "card" },
                    Padding = new Thickness(24, 28),
                    Margin = new Thickness(0, 20, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                var emptyStack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };

                var title = new TextBlock
                {
                    Text = UiText.T("当前章节暂无同义替换考点"),
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
                emptyStack.Children.Add(title);

                var subtitle = new TextBlock
                {
                    Text = UiText.T("雅思同义替换主要收录于高频考点词章节（如听力 179 考点词等）。"),
                    Classes = { "muted" },
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                emptyStack.Children.Add(subtitle);

                var viewAllBtn = new Button
                {
                    Content = UiText.T("查看全部同义替换"),
                    Classes = { "primary" },
                    Padding = new Thickness(18, 8),
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                viewAllBtn.Click += (_, _) =>
                {
                    _chapterFilterCombo.SelectedIndex = 0;
                };
                emptyStack.Children.Add(viewAllBtn);

                emptyCard.Child = emptyStack;
                _listPanel.Children.Add(emptyCard);
                return;
            }

            // 搜索无结果空状态
            var noMatchCard = new Border
            {
                Classes = { "card" },
                Padding = new Thickness(24, 24),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var noMatchStack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            var noMatchText = new TextBlock
            {
                Text = UiText.T("未找到匹配的同义替换考点"),
                FontSize = 15,
                FontWeight = FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            noMatchText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
            noMatchStack.Children.Add(noMatchText);

            var clearBtn = new Button
            {
                Content = UiText.T("清除搜索"),
                Classes = { "secondary" },
                Padding = new Thickness(14, 6),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            clearBtn.Click += (_, _) => _searchBox.Text = "";
            noMatchStack.Children.Add(clearBtn);

            noMatchCard.Child = noMatchStack;
            _listPanel.Children.Add(noMatchCard);
            return;
        }

        // 渲染同义组卡片
        foreach (var (section, word) in sectionWords)
        {
            var card = new Border
            {
                Classes = { "card" },
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 2)
            };

            var stack = new StackPanel { Spacing = 6 };

            // 词头行
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto")
            };

            var wordBlock = new TextBlock
            {
                Text = word.Word,
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            wordBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
            Grid.SetColumn(wordBlock, 0);
            headerGrid.Children.Add(wordBlock);

            if (!string.IsNullOrWhiteSpace(word.Phonetic))
            {
                var phonBlock = new TextBlock
                {
                    Text = word.Phonetic,
                    Classes = { "muted" },
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(phonBlock, 1);
                headerGrid.Children.Add(phonBlock);
            }

            if (!string.IsNullOrWhiteSpace(word.Pos))
            {
                var posChip = new Border
                {
                    Classes = { "pos-chip" },
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Child = new TextBlock { Text = word.Pos, FontSize = 11 }
                };
                Grid.SetColumn(posChip, 2);
                headerGrid.Children.Add(posChip);
            }

            // 来源标签
            var sourceText = new TextBlock
            {
                Text = $"{section.Title} · Group {word.Group}",
                Classes = { "muted" },
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(sourceText, 3);
            headerGrid.Children.Add(sourceText);

            // 发音按钮
            var speakerPath = new Path
            {
                Data = Geometry.Parse("M 3,6 L 6,6 L 10,2 L 10,14 L 6,10 L 3,10 Z M 12,5.5 C 13.5,7 13.5,9 12,10.5"),
                StrokeThickness = 1.2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 14,
                Height = 14
            };
            speakerPath.Bind(Shape.StrokeProperty, this.GetResourceObservable("MutedBrush"));

            var audioBtn = new Button
            {
                Classes = { "row-btn" },
                Padding = new Thickness(6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Content = speakerPath
            };
            audioBtn.Click += (_, _) =>
            {
                var asset = IeltsCatalog.ResolveAsset(word.AudioPath);
                _playerProvider().Play(word.Word, asset);
            };
            Grid.SetColumn(audioBtn, 4);
            headerGrid.Children.Add(audioBtn);

            stack.Children.Add(headerGrid);

            // 同义替换对应行
            var synWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var arrowLabel = new TextBlock
            {
                Text = "➔  " + UiText.T("同义替换") + "：",
                FontWeight = FontWeight.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            arrowLabel.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
            synWrap.Children.Add(arrowLabel);

            foreach (var syn in word.Synonyms)
            {
                var chip = new Border
                {
                    Classes = { "badge" },
                    Margin = new Thickness(0, 0, 6, 4),
                    Padding = new Thickness(8, 3),
                    Child = new TextBlock
                    {
                        Text = syn,
                        FontSize = 12,
                        FontWeight = FontWeight.Medium
                    }
                };
                synWrap.Children.Add(chip);
            }
            stack.Children.Add(synWrap);

            // 释义与例句
            var meaningBlock = new TextBlock
            {
                Text = word.Meaning,
                FontSize = 13,
                Margin = new Thickness(0, 2, 0, 0)
            };
            meaningBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
            stack.Children.Add(meaningBlock);

            if (!string.IsNullOrWhiteSpace(word.Example))
            {
                var exampleBlock = new TextBlock
                {
                    Text = "例句：" + word.Example,
                    Classes = { "example-en" },
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(exampleBlock);
            }

            card.Child = stack;
            _listPanel.Children.Add(card);
        }
    }

    public void RefreshLanguage()
    {
        _titleBlock.Text = UiText.T("阅读同义替换");
        _startPracticeBtn.Content = UiText.T("开始练习");
        _backBtn.Content = UiText.T("返回 IELTS 目录");
        _searchBox.Watermark = UiText.T("搜索考点词、同义表达或释义...");
        RenderList();
    }

    private sealed record ChapterOption(string Id, string Display);
}
