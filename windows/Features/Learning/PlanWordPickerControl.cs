using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Features.Learning;

/// <summary>
/// 计划选词条目展示模型。
/// </summary>
public sealed record PlanWordItemModel(
    string Id,
    string Word,
    string Meaning,
    string? Phonetic = null,
    string? Definition = null,
    string? Example = null
)
{
    public static PlanWordItemModel FromDailyStudyPlanWord(DailyStudyPlanWord w) =>
        new(w.Id, w.Word, w.Meaning, w.Phonetic, w.Definition, w.Example);
}

/// <summary>
/// 统一词行展示控件：
/// - 固定列关系：选择勾选框 (40 DIP) | 单词与次要音标 (180 DIP) | 释义与按需展开 (*)
/// - 释义使用直接可选正文 (SelectableTextBlock)，彻底杜绝 Expander 白色输入框样式
/// - 音标作为辅助信息次要展示 (MutedBrush, 12 DIP)
/// - 英文详解与例句支持按需轻量展开，不嵌套白卡
/// - 选中时使用 SelectionBrush 轻高亮整行
/// </summary>
public class PlanWordRowControl : UserControl
{
    private readonly Border _rowBorder;
    private readonly StackPanel _expandPanel;
    private readonly Button _expandToggleBtn;

    public PlanWordItemModel Item { get; }
    public CheckBox SelectCheckBox { get; }
    public SelectableTextBlock WordBlock { get; }
    public TextBlock PhoneticBlock { get; }
    public SelectableTextBlock MeaningBlock { get; }
    public SelectableTextBlock? DefinitionBlock { get; }
    public SelectableTextBlock? ExampleBlock { get; }

    public event Action<bool>? SelectionChanged;

    public bool IsSelected
    {
        get => SelectCheckBox.IsChecked == true;
        set => SelectCheckBox.IsChecked = value;
    }

    public PlanWordRowControl(PlanWordItemModel item, bool isSelected, Action<bool>? onSelectionChanged = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        if (onSelectionChanged != null) SelectionChanged += onSelectionChanged;

        _rowBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Background = Brushes.Transparent
        };

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("40,180,*")
        };

        // 列 0：选择框（定宽 40 DIP，居中）
        SelectCheckBox = new CheckBox
        {
            IsChecked = isSelected,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SelectCheckBox.IsCheckedChanged += (_, _) =>
        {
            var selected = SelectCheckBox.IsChecked == true;
            UpdateSelectionVisual(selected);
            SelectionChanged?.Invoke(selected);
        };
        Grid.SetColumn(SelectCheckBox, 0);
        rowGrid.Children.Add(SelectCheckBox);

        // 列 1：单词主标题 + 次要音标（定宽 180 DIP）
        var wordStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Margin = new Thickness(0, 0, 8, 0)
        };

        WordBlock = new SelectableTextBlock
        {
            Text = item.Word,
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        };
        WordBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        wordStack.Children.Add(WordBlock);

        PhoneticBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Phonetic) ? "" : $"/{item.Phonetic.Trim('/')}/",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap
        };
        PhoneticBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        if (!string.IsNullOrWhiteSpace(item.Phonetic))
        {
            wordStack.Children.Add(PhoneticBlock);
        }

        Grid.SetColumn(wordStack, 1);
        rowGrid.Children.Add(wordStack);

        // 列 2：直接可选正文释义 + 轻量按需展开
        var meaningStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        MeaningBlock = new SelectableTextBlock
        {
            Text = item.Meaning,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        MeaningBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(MeaningBlock, 0);
        headerRow.Children.Add(MeaningBlock);

        // 详细释义 / 例句展开面板
        _expandPanel = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false
        };

        bool hasDetails = false;

        if (!string.IsNullOrWhiteSpace(item.Definition))
        {
            hasDetails = true;
            DefinitionBlock = new SelectableTextBlock
            {
                Text = item.Definition,
                FontSize = 13,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            };
            DefinitionBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
            _expandPanel.Children.Add(DefinitionBlock);
        }

        if (!string.IsNullOrWhiteSpace(item.Example))
        {
            hasDetails = true;
            ExampleBlock = new SelectableTextBlock
            {
                Text = item.Example,
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            };
            ExampleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
            _expandPanel.Children.Add(ExampleBlock);
        }

        _expandToggleBtn = new Button
        {
            Classes = { "ghost" },
            Padding = new Thickness(6, 2),
            FontSize = 11,
            Content = "详细 ▾",
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = hasDetails
        };
        _expandToggleBtn.Click += (_, _) =>
        {
            _expandPanel.IsVisible = !_expandPanel.IsVisible;
            _expandToggleBtn.Content = _expandPanel.IsVisible ? "收起 ▴" : "详细 ▾";
        };
        Grid.SetColumn(_expandToggleBtn, 1);
        headerRow.Children.Add(_expandToggleBtn);

        meaningStack.Children.Add(headerRow);
        if (hasDetails)
        {
            meaningStack.Children.Add(_expandPanel);
        }

        Grid.SetColumn(meaningStack, 2);
        rowGrid.Children.Add(meaningStack);

        _rowBorder.Child = rowGrid;
        Content = _rowBorder;

        UpdateSelectionVisual(isSelected);
    }

    private void UpdateSelectionVisual(bool isSelected)
    {
        if (isSelected)
        {
            _rowBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("SelectionBrush"));
        }
        else
        {
            _rowBorder.Background = Brushes.Transparent;
        }
    }
}

/// <summary>
/// 1.2.3 计划选词行展示组件与集成工具：
/// - 仅提供词行展示与选择组件，不重复主控现有的分页、搜索与跨章节数据逻辑
/// - 提供完整容器 PlanWordPickerControl 与即插即用的静态创建方法 CreateWordRow
/// </summary>
public class PlanWordPickerControl : UserControl
{
    private readonly StackPanel _rowsPanel;
    private readonly Border _headerBorder;

    public event Action<string, bool>? WordSelectionChanged;

    public PlanWordPickerControl()
    {
        var rootLayout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        // 统一表头
        _headerBorder = new Border
        {
            Padding = new Thickness(10, 6),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        _headerBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var headerCols = CreateHeaderGrid();
        _headerBorder.Child = headerCols;
        Grid.SetRow(_headerBorder, 0);
        rootLayout.Children.Add(_headerBorder);

        // 词行容器
        _rowsPanel = new StackPanel { Spacing = 2 };
        Grid.SetRow(_rowsPanel, 1);
        rootLayout.Children.Add(_rowsPanel);

        Content = rootLayout;
    }

    public static Grid CreateHeaderGrid()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("40,180,*")
        };

        var colSelect = new TextBlock
        {
            Text = "选择",
            FontSize = 12,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(colSelect, 0);
        grid.Children.Add(colSelect);

        var colWord = new TextBlock
        {
            Text = "单词 · 音标",
            FontSize = 12,
            Opacity = 0.7
        };
        Grid.SetColumn(colWord, 1);
        grid.Children.Add(colWord);

        var colMeaning = new TextBlock
        {
            Text = "中文释义与详解",
            FontSize = 12,
            Opacity = 0.7
        };
        Grid.SetColumn(colMeaning, 2);
        grid.Children.Add(colMeaning);

        return grid;
    }

    /// <summary>
    /// 设置当前页词条列表。
    /// </summary>
    public void SetWords(IEnumerable<PlanWordItemModel> words, Func<string, bool> isSelectedPredicate)
    {
        _rowsPanel.Children.Clear();
        foreach (var word in words)
        {
            var isSelected = isSelectedPredicate(word.Id);
            var row = new PlanWordRowControl(word, isSelected, selected =>
            {
                WordSelectionChanged?.Invoke(word.Id, selected);
            });
            _rowsPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// 支持直接从现有领域模型 DailyStudyPlanWord 挂载当前页词条。
    /// </summary>
    public void SetWords(IEnumerable<DailyStudyPlanWord> words, Func<string, bool> isSelectedPredicate)
    {
        SetWords(System.Linq.Enumerable.Select(words, PlanWordItemModel.FromDailyStudyPlanWord), isSelectedPredicate);
    }

    /// <summary>
    /// 静态集成方法：主控无需替换已有分页/筛选容器，直接调用此方法生成单一规范词行。
    /// </summary>
    public static Control CreateWordRow(PlanWordItemModel item, bool isSelected, Action<bool> onSelectionChanged)
    {
        return new PlanWordRowControl(item, isSelected, onSelectionChanged);
    }

    /// <summary>
    /// 静态集成方法：支持直接从 DailyStudyPlanWord 生成单一规范词行。
    /// </summary>
    public static Control CreateWordRow(DailyStudyPlanWord word, bool isSelected, Action<bool> onSelectionChanged)
    {
        return new PlanWordRowControl(PlanWordItemModel.FromDailyStudyPlanWord(word), isSelected, onSelectionChanged);
    }
}
