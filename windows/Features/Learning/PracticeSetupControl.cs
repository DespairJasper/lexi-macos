using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Features.Learning;

/// <summary>
/// 1.2.3 拼写训练准备组件：
/// - 纯色卡片（最大宽 640，高约 320），删除多层卡片与冗长说明
/// - 仅保留两项练习范围（全部词条 / 仅薄弱项），空 weak 严格禁用薄弱项
/// - 删除可见模式选择，默认辅助拼写（Hints 恒为 true）
/// - 保留 MemoryModeRadio / AssistedModeRadio 作为不可见字段以兼容历史反射测试
/// - 唯一开始键与取消键，全部文本使用 UiText.Bilingual 双语国际化
/// </summary>
public class PracticeSetupControl : UserControl
{
    private readonly Action<bool, bool> _start;
    private readonly Action _cancel;

    private readonly TextBlock _titleBlock;
    private readonly TextBlock _subtitleBlock;

    private readonly TextBlock _scopeSectionHeader;
    private readonly RadioButton _allScopeRadio;
    private readonly TextBlock _allScopeDesc;
    private readonly RadioButton _weakScopeRadio;
    private readonly TextBlock _weakScopeDesc;
    private readonly Border _weakOptionBorder;

    // 历史兼容不可见字段：不暴露至视觉树
    private readonly RadioButton _memoryModeRadio = new() { IsVisible = false };
    private readonly RadioButton _assistedModeRadio = new() { IsVisible = false, IsChecked = true };

    private readonly Button _cancelButton;
    private readonly Button _startButton;

    public int TotalWords { get; }
    public int WeakWords { get; }

    public bool OnlyWeak => _weakScopeRadio.IsChecked == true && WeakWords > 0;
    public bool Hints => true; // 1.2.3 统一产品方案：Hints 恒为 true

    public Button StartButton => _startButton;
    public Button CancelButton => _cancelButton;
    public RadioButton AllScopeRadio => _allScopeRadio;
    public RadioButton WeakScopeRadio => _weakScopeRadio;
    public RadioButton MemoryModeRadio => _memoryModeRadio;
    public RadioButton AssistedModeRadio => _assistedModeRadio;

    public PracticeSetupControl(int total, int weak, Action<bool, bool> start, Action cancel)
    {
        TotalWords = Math.Max(0, total);
        WeakWords = Math.Max(0, weak);
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));

        var rootCard = new Border
        {
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(24, 20),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1)
        };
        rootCard.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBrush"));
        rootCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var mainLayout = new StackPanel { Spacing = 16 };

        // 1. 标题区
        var headerStack = new StackPanel { Spacing = 4 };
        _titleBlock = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        _titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        headerStack.Children.Add(_titleBlock);

        _subtitleBlock = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        _subtitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        headerStack.Children.Add(_subtitleBlock);
        mainLayout.Children.Add(headerStack);

        // 2. 范围区（仅两项范围，无模式选择）
        var scopeGroup = new StackPanel { Spacing = 8 };
        _scopeSectionHeader = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _scopeSectionHeader.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        scopeGroup.Children.Add(_scopeSectionHeader);

        // 全部词条选项
        var allOptionBorder = CreateOptionContainer();
        var allOptionStack = new StackPanel { Spacing = 2 };
        _allScopeRadio = new RadioButton
        {
            GroupName = "PracticeScope",
            FontSize = 14,
            FontWeight = FontWeight.Medium
        };
        allOptionStack.Children.Add(_allScopeRadio);
        _allScopeDesc = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(26, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        _allScopeDesc.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        allOptionStack.Children.Add(_allScopeDesc);
        allOptionBorder.Child = allOptionStack;
        scopeGroup.Children.Add(allOptionBorder);

        // 仅薄弱项选项（空 weak 严格禁用）
        _weakOptionBorder = CreateOptionContainer();
        var weakOptionStack = new StackPanel { Spacing = 2 };
        _weakScopeRadio = new RadioButton
        {
            GroupName = "PracticeScope",
            FontSize = 14,
            FontWeight = FontWeight.Medium
        };
        weakOptionStack.Children.Add(_weakScopeRadio);
        _weakScopeDesc = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(26, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        _weakScopeDesc.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        weakOptionStack.Children.Add(_weakScopeDesc);
        _weakOptionBorder.Child = weakOptionStack;

        if (WeakWords <= 0)
        {
            _weakScopeRadio.IsEnabled = false;
            _weakOptionBorder.Opacity = 0.55;
            _allScopeRadio.IsChecked = true;
        }
        else
        {
            _weakScopeRadio.IsEnabled = true;
            _weakScopeRadio.IsChecked = true;
        }

        scopeGroup.Children.Add(_weakOptionBorder);
        mainLayout.Children.Add(scopeGroup);

        // 3. 底部操作区（唯一开始键 + 取消键）
        var actionsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 4, 0, 0)
        };

        _cancelButton = new Button
        {
            Classes = { "ghost" },
            Padding = new Thickness(18, 8),
            FontSize = 14
        };
        _cancelButton.Click += (_, _) => _cancel();
        Grid.SetColumn(_cancelButton, 1);
        actionsGrid.Children.Add(_cancelButton);

        _startButton = new Button
        {
            Classes = { "primary" },
            Padding = new Thickness(24, 8),
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold
        };
        _startButton.Click += (_, _) => OnStartClicked();

        if (TotalWords <= 0)
        {
            _startButton.IsEnabled = false;
        }

        Grid.SetColumn(_startButton, 2);
        actionsGrid.Children.Add(_startButton);

        mainLayout.Children.Add(actionsGrid);

        rootCard.Child = mainLayout;
        Content = rootCard;

        RefreshLanguage();
    }

    private void OnStartClicked()
    {
        _start(OnlyWeak, Hints);
    }

    private Border CreateOptionContainer()
    {
        var border = new Border
        {
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        return border;
    }

    /// <summary>
    /// 双语刷新所有 UI 文本。
    /// </summary>
    public void RefreshLanguage()
    {
        _titleBlock.Text = UiText.Bilingual("拼写训练准备", "Practice Setup");
        _subtitleBlock.Text = UiText.Bilingual(
            "选择训练范围，针对性强化单词拼写记忆。",
            "Configure practice scope to target spelling retention.");

        _scopeSectionHeader.Text = UiText.Bilingual("练习范围", "Practice Scope");
        _allScopeRadio.Content = UiText.Bilingual($"全部词条（共 {TotalWords} 词）", $"All Words ({TotalWords} words)");
        _allScopeDesc.Text = UiText.Bilingual(
            "遍历当前计划或词库中的全部词汇，进行完整拼写检验。",
            "Iterate through all vocabulary items in the plan for a complete spelling check.");

        var weakText = WeakWords > 0
            ? UiText.Bilingual($"仅薄弱项（共 {WeakWords} 词）", $"Weak Words Only ({WeakWords} words)")
            : UiText.Bilingual("仅薄弱项（暂无薄弱词）", "Weak Words Only (No weak words)");
        _weakScopeRadio.Content = weakText;

        _weakScopeDesc.Text = WeakWords > 0
            ? UiText.Bilingual("聚焦近期评测中判定为「忘记」或拼写出错的词汇，专项攻克突破。", "Focus specifically on words recently rated 'Forgot' or misspelled.")
            : UiText.Bilingual("当前没有错误或薄弱词汇记录，可直接选择「全部词条」开启练习。", "No weak or mistaken words currently recorded. Select 'All Words' to begin.");

        _cancelButton.Content = UiText.Bilingual("取消", "Cancel");
        _startButton.Content = UiText.Bilingual("开始训练", "Start Practice");
    }
}
