using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Features.Learning;

/// <summary>
/// 计划表单模型：统一适用于新建计划与调整现有计划。
/// </summary>
public sealed record PlanEditorModel(
    string Name,
    int DailyCount,
    bool Shuffle,
    int WordCount
);

/// <summary>
/// 1.2.3 统一计划创建与调整表单组件：
/// - 纯粹限宽表单，最大宽度 680 DIP，居中呈现
/// - 字段：计划名称、NumericUpDown 每日学习词数 (1-10000)、随机乱序选项
/// - 动态轻量排期摘要：随每日词数输入即时计算所需天数与尾日剩余
/// - 明确未来批次生效提示，杜绝技术底层文案
/// - 保存/取消按钮定宽，主次视觉明确
/// - 失败支持 SetError 并严格保留当前用户输入
/// </summary>
public class PlanEditorControl : UserControl
{
    private readonly Action<PlanEditorModel> _onSave;
    private readonly Action _onCancel;

    private int _wordCount;

    public TextBox NameInput { get; }
    public NumericUpDown DailyQuotaInput { get; }
    public CheckBox ShuffleCheckbox { get; }
    public TextBlock SummaryBlock { get; }
    public TextBlock FutureBatchNoticeBlock { get; }
    public TextBlock ErrorBlock { get; }
    public Button SaveButton { get; }
    public Button CancelButton { get; }

    private readonly TextBlock _titleBlock;
    private readonly TextBlock _nameLabel;
    private readonly TextBlock _quotaLabel;

    public PlanEditorControl(PlanEditorModel model, Action<PlanEditorModel> onSave, Action onCancel)
    {
        _onSave = onSave ?? throw new ArgumentNullException(nameof(onSave));
        _onCancel = onCancel ?? throw new ArgumentNullException(nameof(onCancel));
        _wordCount = Math.Max(0, model.WordCount);

        MaxWidth = 680;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = new Thickness(16, 8);

        var formStack = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // 1. 标题
        _titleBlock = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        formStack.Children.Add(_titleBlock);

        // 2. 计划名称字段
        var nameStack = new StackPanel { Spacing = 6 };
        _nameLabel = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.Medium
        };
        _nameLabel.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        nameStack.Children.Add(_nameLabel);

        NameInput = new TextBox
        {
            Name = "PlanEditorNameInput",
            Text = model.Name ?? "",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        nameStack.Children.Add(NameInput);
        formStack.Children.Add(nameStack);

        // 3. 每日词数字段 (NumericUpDown 1 - 10000)
        var quotaStack = new StackPanel { Spacing = 6 };
        _quotaLabel = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.Medium
        };
        _quotaLabel.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        quotaStack.Children.Add(_quotaLabel);

        DailyQuotaInput = new NumericUpDown
        {
            Name = "PlanEditorQuotaInput",
            Minimum = 1,
            Maximum = 10000,
            Increment = 1,
            FormatString = "0",
            Value = Math.Clamp(model.DailyCount <= 0 ? 20 : model.DailyCount, 1, 10000),
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        DailyQuotaInput.ValueChanged += (_, _) => UpdateEstimate();
        quotaStack.Children.Add(DailyQuotaInput);
        formStack.Children.Add(quotaStack);

        // 4. 随机乱序选项
        ShuffleCheckbox = new CheckBox
        {
            Name = "PlanEditorShuffleCheck",
            IsChecked = model.Shuffle,
            Margin = new Thickness(0, 4, 0, 4)
        };
        formStack.Children.Add(ShuffleCheckbox);

        // 5. 轻量排期摘要
        var summaryCard = new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        summaryCard.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBrush"));
        summaryCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        SummaryBlock = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        };
        SummaryBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        summaryCard.Child = SummaryBlock;
        formStack.Children.Add(summaryCard);

        // 6. 明确未来批次生效提示（无技术底层文案）
        FutureBatchNoticeBlock = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        };
        FutureBatchNoticeBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        formStack.Children.Add(FutureBatchNoticeBlock);

        // 7. 错误提示区域（SetError 时可见，不清除用户输入）
        ErrorBlock = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        ErrorBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));
        formStack.Children.Add(ErrorBlock);

        // 8. 操作按钮（保存/取消按钮内容定宽 96 DIP）
        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Margin = new Thickness(0, 8, 0, 0)
        };

        CancelButton = new Button
        {
            Width = 96,
            Height = 34,
            Classes = { "ghost" },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        CancelButton.Click += (_, _) => _onCancel();
        actionsRow.Children.Add(CancelButton);

        SaveButton = new Button
        {
            Width = 96,
            Height = 34,
            Classes = { "primary" },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        SaveButton.Click += (_, _) => HandleSave();
        actionsRow.Children.Add(SaveButton);

        formStack.Children.Add(actionsRow);

        Content = formStack;

        RefreshLanguage();
        UpdateEstimate();
    }

    private void HandleSave()
    {
        var name = NameInput.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            SetError(UiText.Bilingual("计划名称不能为空，请输入计划名称", "Plan name cannot be empty. Please enter a name."));
            return;
        }

        var daily = (int)(DailyQuotaInput.Value ?? 20);
        daily = Math.Clamp(daily, 1, 10000);
        var shuffle = ShuffleCheckbox.IsChecked == true;

        SetError(null);
        _onSave(new PlanEditorModel(name, daily, shuffle, _wordCount));
    }

    /// <summary>
    /// 设置错误信息提示；错误发生时严格保留用户已输入的名称、数值与选项。
    /// </summary>
    public void SetError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            ErrorBlock.IsVisible = false;
            ErrorBlock.Text = "";
        }
        else
        {
            ErrorBlock.Text = errorMessage;
            ErrorBlock.IsVisible = true;
        }
    }

    /// <summary>
    /// 动态刷新排期预估摘要。
    /// </summary>
    private void UpdateEstimate()
    {
        var daily = (int)(DailyQuotaInput.Value ?? 20);
        daily = Math.Max(1, daily);

        if (_wordCount <= 0)
        {
            SummaryBlock.Text = UiText.Bilingual(
                "排期摘要：当前计划尚未选词或词数为 0，保存后可在词表添加词条。",
                "Schedule summary: No words selected yet. Words can be added after saving.");
            return;
        }

        var (days, desc) = PlanCreationState.CalculateEstimate(_wordCount, daily);
        SummaryBlock.Text = UiText.Bilingual(
            $"选词规模：共 {_wordCount} 词 · 每日学习 {daily} 词 · {desc}",
            $"Scope: {_wordCount} words · Daily target: {daily} · {desc}");
    }

    /// <summary>
    /// 获取当前表单输入对应的模型快照。
    /// </summary>
    public PlanEditorModel GetCurrentModel()
    {
        var name = NameInput.Text?.Trim() ?? "";
        var daily = Math.Clamp((int)(DailyQuotaInput.Value ?? 20), 1, 10000);
        var shuffle = ShuffleCheckbox.IsChecked == true;
        return new PlanEditorModel(name, daily, shuffle, _wordCount);
    }

    /// <summary>
    /// 重置或更新表单模型。
    /// </summary>
    public void SetModel(PlanEditorModel model)
    {
        _wordCount = Math.Max(0, model.WordCount);
        NameInput.Text = model.Name ?? "";
        DailyQuotaInput.Value = Math.Clamp(model.DailyCount <= 0 ? 20 : model.DailyCount, 1, 10000);
        ShuffleCheckbox.IsChecked = model.Shuffle;
        SetError(null);
        UpdateEstimate();
    }

    /// <summary>
    /// 刷新多语言文本，不重置任何输入数据。
    /// </summary>
    public void RefreshLanguage()
    {
        _titleBlock.Text = UiText.Bilingual("计划设置与排期", "Plan Settings & Schedule");
        _nameLabel.Text = UiText.Bilingual("计划名称", "Plan Name");
        NameInput.Watermark = UiText.Bilingual("请输入计划名称", "Enter plan name");
        _quotaLabel.Text = UiText.Bilingual("每日学习词数 (1 - 10000)", "Daily Target (1 - 10,000)");
        ShuffleCheckbox.Content = UiText.Bilingual("随机打乱顺序（在创建时生成固定乱序）", "Randomize word order (shuffled upon creation)");
        FutureBatchNoticeBlock.Text = UiText.Bilingual(
            "提示：每日学习词数与乱序设置将在后续生成的新批次生效，不影响今日已学完的记录与历史进度。",
            "Note: Daily target and order adjustments will take effect in subsequent batches without altering completed progress.");
        CancelButton.Content = UiText.Bilingual("取消", "Cancel");
        SaveButton.Content = UiText.Bilingual("保存计划", "Save Plan");

        UpdateEstimate();
    }
}
