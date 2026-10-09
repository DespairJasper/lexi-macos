using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Controls;

/// <summary>
/// 词汇档案操作种类枚举。
/// </summary>
public enum ArchiveActionKind
{
    None = 0,
    StudySelected,
    FocusStudy,
    CreatePlan,
    TodayReview,
    MarkMastered,
    Relearn,
    Export,
    DeleteSelected
}

/// <summary>
/// 1.2.1 词汇档案中性操作栏与分组菜单：
/// - 替代旧 ComboBox 下拉
/// - 常驻/选词呈现：“已选 N 词 / 学习所选 / 更多 ▾”
/// - 更多菜单包含单色图标、标题+说明、分组间距，行高 32–36 DIP
/// - “今日复习”操作名，说明“将所选词安排到今天”
/// - 危险操作“删除所选”置于底部分隔组，仅文字/图标危险色
/// - 移除“完成本次复习 (推进阶段)”与“调整复习阶段”，避免破坏 FSRS 数据事实
/// </summary>
public class ArchiveActionsMenu : UserControl
{
    private static string T(string chinese) => UiText.Text(chinese);

    private readonly Border _containerBorder;
    private readonly TextBlock _selectedCountText;
    private readonly Button _studySelectedButton;
    private readonly Button _moreMenuButton;
    private readonly Flyout _menuFlyout;

    public int SelectedCount { get; private set; }
    public Flyout MoreFlyout => _menuFlyout;
    public bool AutoHideWhenNoSelection { get; set; } = false;

    public event Action<ArchiveActionKind>? ActionTriggered;
    public event Action? StudySelectedRequested;
    public event Action? FocusStudyRequested;
    public event Action? CreatePlanRequested;
    public event Action? TodayReviewRequested;
    public event Action? MarkMasteredRequested;
    public event Action? RelearnRequested;
    public event Action? ExportRequested;
    public event Action? DeleteSelectedRequested;

    public ArchiveActionsMenu()
    {
        _containerBorder = new Border();
        _containerBorder.Classes.Add("archive-action-bar");

        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };

        // 左侧：已选词数说明
        _selectedCountText = new TextBlock
        {
            Text = $"{T("已选")} 0 {T("词")}",
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        _selectedCountText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        mainGrid.Children.Add(_selectedCountText);

        // 右侧操作按钮组
        var actionsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        _studySelectedButton = new Button
        {
            Content = T("学习所选"),
            Classes = { "primary" },
            Padding = new Thickness(14, 6),
            IsEnabled = false
        };
        _studySelectedButton.Click += (_, _) =>
        {
            ActionTriggered?.Invoke(ArchiveActionKind.StudySelected);
            StudySelectedRequested?.Invoke();
        };
        actionsStack.Children.Add(_studySelectedButton);

        _moreMenuButton = new Button
        {
            Content = T("更多 ▾"),
            Classes = { "secondary" },
            Padding = new Thickness(12, 6),
            IsEnabled = false
        };

        // 构建更多分组浮窗菜单
        _menuFlyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ShowMode = FlyoutShowMode.Transient
        };
        _menuFlyout.Content = BuildMoreMenuContent();
        _moreMenuButton.Flyout = _menuFlyout;

        actionsStack.Children.Add(_moreMenuButton);

        Grid.SetColumn(actionsStack, 2);
        mainGrid.Children.Add(actionsStack);

        _containerBorder.Child = mainGrid;
        Content = _containerBorder;

        UpdateSelection(0);
    }

    /// <summary>
    /// 更新当前选中的词数。
    /// </summary>
    public void UpdateSelection(int count)
    {
        SelectedCount = Math.Max(0, count);
        _selectedCountText.Text = $"{T("已选")} {SelectedCount} {T("词")}";

        var hasSelection = SelectedCount > 0;
        _studySelectedButton.IsEnabled = hasSelection;
        _moreMenuButton.IsEnabled = hasSelection;

        if (AutoHideWhenNoSelection)
        {
            IsVisible = hasSelection;
        }
    }

    public void RefreshLanguage()
    {
        _studySelectedButton.Content = T("学习所选");
        _moreMenuButton.Content = T("更多 ▾");
        _menuFlyout.Content = BuildMoreMenuContent();
        UpdateSelection(SelectedCount);
    }

    private Control BuildMoreMenuContent()
    {
        var menuRoot = new Border
        {
            Width = 260,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        };
        menuRoot.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBrush"));
        menuRoot.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var list = new StackPanel { Spacing = 2 };

        // 分组 1: 学习 (Study)
        list.Children.Add(CreateMenuItem(
            ArchiveActionKind.FocusStudy,
            "🎯",
            T("专注学习"),
            T("在沉浸式视图中学习所选单词"),
            () => { FocusStudyRequested?.Invoke(); }));

        list.Children.Add(CreateMenuItem(
            ArchiveActionKind.CreatePlan,
            "📋",
            T("创建计划"),
            T("基于所选单词创建新的学习计划"),
            () => { CreatePlanRequested?.Invoke(); }));

        list.Children.Add(CreateMenuItem(
            ArchiveActionKind.TodayReview,
            "📅",
            T("今日复习"),
            T("将所选词安排到今天"),
            () => { TodayReviewRequested?.Invoke(); }));

        // 分隔线
        list.Children.Add(CreateSeparator());

        // 分组 2: 整理 (Organize)
        list.Children.Add(CreateMenuItem(
            ArchiveActionKind.MarkMastered,
            "✓",
            T("标记已掌握"),
            T("将单词标记为已熟知，跳过日常复习"),
            () => { MarkMasteredRequested?.Invoke(); }));

        list.Children.Add(CreateMenuItem(
            ArchiveActionKind.Relearn,
            "↺",
            T("重新学习"),
            T("重置记忆进度并重新安排学习排期"),
            () => { RelearnRequested?.Invoke(); }));

        // 分隔线
        list.Children.Add(CreateSeparator());

        // 分组 3: 导出 (Export)
        list.Children.Add(CreateMenuItem(
            ArchiveActionKind.Export,
            "↗",
            T("导出所选"),
            T("导出为文本或表格文件"),
            () => { ExportRequested?.Invoke(); }));

        // 分隔线
        list.Children.Add(CreateSeparator());

        // 分组 4: 危险 (Danger, 底部)
        list.Children.Add(CreateMenuItem(
            ArchiveActionKind.DeleteSelected,
            "🗑",
            T("删除所选"),
            T("从词汇档案中永久移除所选单词"),
            () => { DeleteSelectedRequested?.Invoke(); },
            isDanger: true));

        menuRoot.Child = list;
        return menuRoot;
    }

    private Button CreateMenuItem(
        ArchiveActionKind kind,
        string icon,
        string title,
        string description,
        Action onTrigger,
        bool isDanger = false)
    {
        var itemBorder = new Button { HorizontalAlignment=HorizontalAlignment.Stretch, HorizontalContentAlignment=HorizontalAlignment.Stretch, Background=Brushes.Transparent };
        itemBorder.Classes.Add("ghost");
        itemBorder.Classes.Add("archive-menu-item");
        if (isDanger)
        {
            itemBorder.Classes.Add("danger");
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };

        // 单色图标
        var iconBlock = new TextBlock
        {
            Text = icon,
            FontSize = 13,
            Width = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (isDanger)
        {
            iconBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));
        }
        else
        {
            iconBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        }
        grid.Children.Add(iconBlock);

        // 标题 + 说明两行
        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.Medium
        };
        if (isDanger)
        {
            titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));
        }
        else
        {
            titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        }
        textStack.Children.Add(titleBlock);

        var descBlock = new TextBlock
        {
            Text = description,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        descBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        textStack.Children.Add(descBlock);

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        itemBorder.Content = grid;

        itemBorder.Click += (_, _) =>
        {
            _menuFlyout.Hide();
            ActionTriggered?.Invoke(kind);
            onTrigger();
        };

        return itemBorder;
    }

    private static Border CreateSeparator()
    {
        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(4, 3)
        };
        sep.Bind(Border.BackgroundProperty, sep.GetResourceObservable("LineBrush"));
        return sep;
    }
}
