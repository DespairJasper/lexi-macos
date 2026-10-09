using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Features.Learning;

/// <summary>
/// 计划卡片数据模型，便于解耦主控与渲染。
/// </summary>
public sealed record PlanCardModel(
    string Id,
    string Title,
    string SourceName,
    string StatusText,
    int CompletedToday,
    int DailyTarget,
    int TotalCompleted,
    int TotalWords,
    int EstimatedDaysRemaining,
    bool IsActive = true,
    object? Tag = null
);

/// <summary>
/// 自适应便签矩形网格面板：
/// 满足最小宽 250 DIP、间距 16 DIP；
/// 根据可用宽度动态计算 1 列、2 列或 3 列，行内等高对齐。
/// </summary>
public class ResponsivePlanGrid : Panel
{
    public const double MinCardWidth = 250.0;
    public const double Gap = 16.0;

    public static int CalculateColumns(double availableWidth)
    {
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
            return 2;

        // 3 列: 3 * 250 + 2 * 16 = 782
        // 2 列: 2 * 250 + 1 * 16 = 516
        // 1 列: < 516
        if (availableWidth >= 782.0) return 3;
        if (availableWidth >= 516.0) return 2;
        return 1;
    }

    public int Columns { get; private set; } = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var visibleChildren = Children.Where(c => c.IsVisible).ToList();
        if (visibleChildren.Count == 0)
            return new Size(0, 0);

        var width = double.IsInfinity(availableSize.Width) ? 800.0 : availableSize.Width;
        Columns = CalculateColumns(width);

        var totalGapWidth = (Columns - 1) * Gap;
        var itemWidth = Math.Max(0, (width - totalGapWidth) / Columns);

        var totalRows = (int)Math.Ceiling((double)visibleChildren.Count / Columns);
        var totalHeight = 0.0;

        for (int r = 0; r < totalRows; r++)
        {
            var rowChildren = visibleChildren.Skip(r * Columns).Take(Columns).ToList();
            var maxRowHeight = 0.0;
            foreach (var child in rowChildren)
            {
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                if (child.DesiredSize.Height > maxRowHeight)
                    maxRowHeight = child.DesiredSize.Height;
            }
            totalHeight += maxRowHeight;
            if (r < totalRows - 1) totalHeight += Gap;
        }

        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visibleChildren = Children.Where(c => c.IsVisible).ToList();
        if (visibleChildren.Count == 0)
            return finalSize;

        Columns = CalculateColumns(finalSize.Width);
        var totalGapWidth = (Columns - 1) * Gap;
        var itemWidth = Math.Max(0, (finalSize.Width - totalGapWidth) / Columns);

        var totalRows = (int)Math.Ceiling((double)visibleChildren.Count / Columns);
        var currentY = 0.0;

        for (int r = 0; r < totalRows; r++)
        {
            var rowChildren = visibleChildren.Skip(r * Columns).Take(Columns).ToList();
            var maxRowHeight = 0.0;

            foreach (var child in rowChildren)
            {
                if (child.DesiredSize.Height > maxRowHeight)
                    maxRowHeight = child.DesiredSize.Height;
            }

            for (int c = 0; c < rowChildren.Count; c++)
            {
                var child = rowChildren[c];
                var x = c * (itemWidth + Gap);
                // 等高排布：在行内统一使用 maxRowHeight
                child.Arrange(new Rect(x, currentY, itemWidth, maxRowHeight));
            }

            currentY += maxRowHeight + Gap;
        }

        return finalSize;
    }
}

/// <summary>
/// 1.2.1 每日学习计划便签看板：
/// - 自适应 1 / 2 / 3 列矩形卡片网格
/// - 纯粹中性主题，无高饱和杂色或胶带装饰
/// - 每卡：标题（最多 2 行带省略）、来源与状态、今日完成/目标、细进度条、次要剩余说明、主动作 + “…” 菜单
/// - 停止/完成归档单独折叠分组
/// - 空状态提供清晰创建入口
/// - 支持 SetCards(IEnumerable&lt;Control&gt;) 或 SetPlans(IEnumerable&lt;PlanCardModel&gt;)
/// </summary>
public class PlanBoardControl : UserControl
{
    private static string T(string chinese) => UiText.Text(chinese);

    private readonly StackPanel _rootPanel;
    private readonly ResponsivePlanGrid _activePlanGrid;
    private readonly Border _emptyStateBorder;
    private readonly StackPanel _inactiveSection;
    private readonly ResponsivePlanGrid _inactivePlanGrid;
    private readonly Button _toggleInactiveBtn;
    private readonly TextBlock _inactiveTitleBlock;

    private bool _isInactiveExpanded;

    public event Action? CreatePlanRequested;
    public event Action<bool>? InactiveExpandedChanged;

    public PlanBoardControl()
    {
        _rootPanel = new StackPanel { Spacing = 24 };

        // 1. 空状态面板
        _emptyStateBorder = CreateEmptyStatePanel();
        _rootPanel.Children.Add(_emptyStateBorder);

        // 2. 活动计划网格
        _activePlanGrid = new ResponsivePlanGrid();
        _rootPanel.Children.Add(_activePlanGrid);

        // 3. 归档/停止计划折叠区
        _inactiveSection = new StackPanel { Spacing = 14, IsVisible = false };

        var toggleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 12, 0, 0)
        };

        _inactiveTitleBlock = new TextBlock
        {
            Text = T("已完成与已停止的计划"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _inactiveTitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        toggleRow.Children.Add(_inactiveTitleBlock);

        _toggleInactiveBtn = new Button
        {
            Content = T("展开 ▾"),
            Padding = new Thickness(12, 6)
        };
        _toggleInactiveBtn.Classes.Add("secondary");
        _toggleInactiveBtn.Click += (_, _) => ToggleInactiveSection();
        Grid.SetColumn(_toggleInactiveBtn, 1);
        toggleRow.Children.Add(_toggleInactiveBtn);

        _inactiveSection.Children.Add(toggleRow);

        _inactivePlanGrid = new ResponsivePlanGrid { IsVisible = false };
        _inactiveSection.Children.Add(_inactivePlanGrid);

        _rootPanel.Children.Add(_inactiveSection);

        Content = _rootPanel;
    }

    /// <summary>
    /// 直接设置活动卡片与归档卡片控件（提供给主控集成）。
    /// </summary>
    public void SetCards(IEnumerable<Control> activeCards, IEnumerable<Control>? inactiveCards = null)
    {
        var activeList = activeCards?.ToList() ?? new List<Control>();
        var inactiveList = inactiveCards?.ToList() ?? new List<Control>();

        _activePlanGrid.Children.Clear();
        foreach (var card in activeList)
        {
            _activePlanGrid.Children.Add(card);
        }

        var hasActive = activeList.Count > 0;
        _emptyStateBorder.IsVisible = !hasActive;
        _activePlanGrid.IsVisible = hasActive;

        _inactivePlanGrid.Children.Clear();
        foreach (var card in inactiveList)
        {
            _inactivePlanGrid.Children.Add(card);
        }

        var hasInactive = inactiveList.Count > 0;
        _inactiveSection.IsVisible = hasInactive;
        if (hasInactive)
        {
            _inactiveTitleBlock.Text = $"{T("已完成与已停止的计划")} ({inactiveList.Count})";
        }
    }

    /// <summary>
    /// 基于领域模型渲染标准卡片。
    /// </summary>
    public void SetPlans(
        IEnumerable<PlanCardModel> plans,
        Action<PlanCardModel>? onStart = null,
        Action<PlanCardModel, Control>? onMore = null)
    {
        var all = plans?.ToList() ?? new List<PlanCardModel>();
        var active = all.Where(p => p.IsActive).ToList();
        var inactive = all.Where(p => !p.IsActive).ToList();

        var activeControls = active.Select(p => CreateStandardCard(p, onStart, onMore)).ToList();
        var inactiveControls = inactive.Select(p => CreateStandardCard(p, onStart, onMore)).ToList();

        SetCards(activeControls, inactiveControls);
    }

    /// <summary>
    /// 构建单个标准便签卡片：
    /// - 标题（最多 2 行，文字溢出带 ToolTip）
    /// - 来源与状态徽章
    /// - 今日完成/目标进度条
    /// - 次要预计剩余说明
    /// - 主动作（“开始 / 继续学习”）+ “…” 管理操作
    /// </summary>
    public Control CreateStandardCard(
        PlanCardModel model,
        Action<PlanCardModel>? onStart = null,
        Action<PlanCardModel, Control>? onMore = null)
    {
        var card = new Border();
        card.Classes.Add("plan-card");

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto")
        };

        // Row 0: 标题两行 + 状态徽章
        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var titleBlock = new TextBlock
        {
            Text = model.Title,
            Classes = { "plan-card-title" },
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(titleBlock, model.Title);
        titleRow.Children.Add(titleBlock);

        var statusBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        statusBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("TintBrush"));

        var statusText = new TextBlock
        {
            Text = model.StatusText,
            FontSize = 11,
            FontWeight = FontWeight.Medium
        };
        statusText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        statusBorder.Child = statusText;
        Grid.SetColumn(statusBorder, 1);
        titleRow.Children.Add(statusBorder);

        layout.Children.Add(titleRow);

        // Row 1: 来源说明
        var sourceBlock = new TextBlock
        {
            Text = model.SourceName,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        sourceBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        Grid.SetRow(sourceBlock, 1);
        layout.Children.Add(sourceBlock);

        // Row 2: 今日完成进度
        var todayProgressText = new TextBlock
        {
            Text = UiText.Format($"今日完成 {model.CompletedToday}/{model.DailyTarget} 词"),
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Margin = new Thickness(0, 0, 0, 6)
        };
        todayProgressText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetRow(todayProgressText, 2);
        layout.Children.Add(todayProgressText);

        // Row 3: 细进度条
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = Math.Max(1, model.DailyTarget),
            Value = Math.Min(model.DailyTarget, model.CompletedToday),
            Classes = { "plan-progress" },
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(progressBar, 3);
        layout.Children.Add(progressBar);

        // Row 4: 次要预计剩余说明
        var estimateText = new TextBlock
        {
            Text = UiText.Format($"总进度 {model.TotalCompleted}/{model.TotalWords} · 预计剩余 {model.EstimatedDaysRemaining} 天"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 14)
        };
        estimateText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        Grid.SetRow(estimateText, 4);
        layout.Children.Add(estimateText);

        // Row 6: 底部操作栏（主动作 + “…” 更多菜单）
        var actionBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 4, 0, 0)
        };

        var startBtn = new Button
        {
            Content = model.CompletedToday > 0 ? T("继续学习") : T("开始学习"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        startBtn.Classes.Add("primary");
        startBtn.Click += (_, _) => onStart?.Invoke(model);
        actionBar.Children.Add(startBtn);

        var moreBtn = new Button
        {
            Content = "•••",
            Padding = new Thickness(10, 6),
            Margin = new Thickness(8, 0, 0, 0)
        };
        moreBtn.Classes.Add("secondary");
        ToolTip.SetTip(moreBtn, T("计划管理"));
        moreBtn.Click += (_, _) => onMore?.Invoke(model, moreBtn);
        Grid.SetColumn(moreBtn, 1);
        actionBar.Children.Add(moreBtn);

        Grid.SetRow(actionBar, 6);
        layout.Children.Add(actionBar);

        card.Child = layout;
        return card;
    }

    private Border CreateEmptyStatePanel()
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(32),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBrush"));
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var stack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 420
        };

        var iconText = new TextBlock
        {
            Text = "📋",
            FontSize = 36,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(iconText);

        var title = new TextBlock
        {
            Text = T("暂无学习计划"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        stack.Children.Add(title);

        var desc = new TextBlock
        {
            Text = T("创建学习计划，每天按时复习，稳步提升词汇量。"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
        desc.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        stack.Children.Add(desc);

        var createBtn = new Button
        {
            Content = T("创建学习计划"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(20, 8),
            Margin = new Thickness(0, 8, 0, 0)
        };
        createBtn.Classes.Add("primary");
        createBtn.Click += (_, _) => CreatePlanRequested?.Invoke();
        stack.Children.Add(createBtn);

        border.Child = stack;
        return border;
    }

    private void ToggleInactiveSection()
    {
        _isInactiveExpanded = !_isInactiveExpanded;
        _inactivePlanGrid.IsVisible = _isInactiveExpanded;
        _toggleInactiveBtn.Content = _isInactiveExpanded ? T("收起 ▴") : T("展开 ▾");
        InactiveExpandedChanged?.Invoke(_isInactiveExpanded);
    }
}
