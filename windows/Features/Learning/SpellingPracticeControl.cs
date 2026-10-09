using Avalonia.Controls.Primitives;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Lexi.Controls;

namespace Lexi.Features.Learning;

/// <summary>
/// 1.2.3 拼写练习展示数据模型，供主控注入。
/// </summary>
public sealed class SpellingPracticeModel
{
    public string Word { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string Hint { get; set; } = "";
    public string Feedback { get; set; } = "";
    public bool? IsCorrect { get; set; }
    public bool AnswerRevealed { get; set; }
    public string ProgressText { get; set; } = "";
    public Action? Speak { get; set; }
}

/// <summary>
/// 1.2.3 拼写练习独立展示控件：
/// - 纯色背景与共享主题，短窗 ScrollViewer 自适应纵向滚动
/// - 释义 20 字号居中呈现
/// - 音标旁紧凑放置 InlineAudioButton 小扬声器
/// - 去除巨大双重占位线，单一逐字轻量提示
/// - 输入框 26 字号（24-28 范围）轻底线，保留实际 TextBox 焦点与输入能力
/// - Enter 键单次防抖提交
/// - 提示按钮触发时不清除当前已输入文字
/// - 紧凑操作区与就近即时反馈
/// </summary>
public class SpellingPracticeControl : UserControl
{
    // 事件与回调
    public event Action<string>? Submitted;
    public event Action? HintRequested;
    public event Action? AnswerRequested;
    public event Action? SpeakRequested;
    public event Action? NextRequested;
    public event Action? CancelRequested;

    public Action<string>? OnSubmit { get; set; }
    public Action? OnHint { get; set; }
    public Action? OnRevealAnswer { get; set; }
    public Action? OnSpeak { get; set; }
    public Action? OnNext { get; set; }
    public Action? OnCancel { get; set; }

    // 视觉元素
    private readonly TextBlock _progressBlock;
    private readonly SelectableTextBlock _meaningBlock;
    private readonly SelectableTextBlock _phoneticBlock;
    private readonly StackPanel _phoneticRow;
    private readonly SelectableTextBlock _hintBlock;
    private readonly TextBox _inputBox;
    private readonly SelectableTextBlock _feedbackBlock;
    private readonly Button _hintButton;
    private readonly Button _answerButton;
    private readonly Button _submitButton;
    private readonly Button _nextButton;
    private readonly InlineAudioButton _speakerButton;

    private SpellingPracticeModel _currentModel = new();
    private bool _isSubmitting;

    public TextBox InputBox => _inputBox;
    public Button HintButton => _hintButton;
    public Button AnswerButton => _answerButton;
    public Button SubmitButton => _submitButton;
    public Button NextButton => _nextButton;
    public SelectableTextBlock MeaningBlock => _meaningBlock;
    public SelectableTextBlock HintBlock => _hintBlock;
    public SelectableTextBlock FeedbackBlock => _feedbackBlock;
    public SpellingPracticeModel CurrentModel => _currentModel;

    public SpellingPracticeControl()
    {
        var contentStack = new StackPanel
        {
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 14,
            Margin = new Thickness(24, 20)
        };

        // 1. 进度指示
        _progressBlock = new TextBlock
        {
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.75,
            IsVisible = false
        };
        _progressBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        contentStack.Children.Add(_progressBlock);

        // 2. 释义（20 字号）
        _meaningBlock = new SelectableTextBlock
        {
            FontSize = 20,
            FontWeight = FontWeight.Medium,
            LineHeight = 28,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _meaningBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        contentStack.Children.Add(_meaningBlock);

        // 3. 音标与小扬声器
        _phoneticRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            IsVisible = false
        };
        _phoneticBlock = new SelectableTextBlock
        {
            FontSize = 16,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center
        };
        _phoneticBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        _phoneticRow.Children.Add(_phoneticBlock);

        _speakerButton = new InlineAudioButton(UiText.Bilingual("朗读单词","Listen to word"), RequestSpeak);
        _phoneticRow.Children.Add(_speakerButton);
        contentStack.Children.Add(_phoneticRow);

        // 4. 单一逐字轻量提示（替代巨大双重占位线）
        _hintBlock = new SelectableTextBlock
        {
            FontSize = 18,
            FontWeight = FontWeight.Normal,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.85,
            IsVisible = false
        };
        _hintBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        contentStack.Children.Add(_hintBlock);

        // 5. 输入框（26 字号，轻底线，保留实际 TextBox 焦点与输入能力）
        _inputBox = new TextBox
        {
            FontSize = 26,
            FontWeight = FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Width = 360,
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(0)
        };
        _inputBox.Bind(TextBox.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        _inputBox.Bind(TextBox.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        _inputBox.Classes.Add("spelling-input");
        _inputBox.KeyDown += OnInputKeyDown;
        contentStack.Children.Add(_inputBox);

        // 6. 就近反馈
        _feedbackBlock = new SelectableTextBlock
        {
            FontSize = 14,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -4, 0, 0),
            IsVisible = false
        };
        contentStack.Children.Add(_feedbackBlock);

        // 7. 紧凑操作区
        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
            Margin = new Thickness(0, 4, 0, 0)
        };

        _hintButton = new Button
        {
            Classes = { "ghost" },
            Padding = new Thickness(16, 8),
            FontSize = 14
        };
        _hintButton.Click += (_, _) =>
        {
            // 提示不清输入，保留焦点
            HintRequested?.Invoke();
            OnHint?.Invoke();
            _inputBox.Focus();
        };
        actionsRow.Children.Add(_hintButton);

        _answerButton = new Button
        {
            Classes = { "ghost" },
            Padding = new Thickness(16, 8),
            FontSize = 14
        };
        _answerButton.Click += (_, _) =>
        {
            AnswerRequested?.Invoke();
            OnRevealAnswer?.Invoke();
            _inputBox.Focus();
        };
        actionsRow.Children.Add(_answerButton);

        _submitButton = new Button
        {
            Classes = { "primary" },
            Padding = new Thickness(22, 8),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold
        };
        _submitButton.Click += (_, _) => SubmitCurrentInput();
        actionsRow.Children.Add(_submitButton);

        _nextButton = new Button
        {
            Classes = { "primary" },
            Padding = new Thickness(22, 8),
            FontSize = 14,
            FontWeight = FontWeight.Normal,
            IsVisible = false
        };
        _nextButton.Click += (_, _) =>
        {
            NextRequested?.Invoke();
            OnNext?.Invoke();
        };
        actionsRow.Children.Add(_nextButton);

        contentStack.Children.Add(actionsRow);

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = contentStack
        };

        var root=new Grid {RowDefinitions=new RowDefinitions("Auto,*")};
        var back=new Button {Content=UiText.Bilingual("← 返回","← Back"),Classes={"ghost"},Margin=new Thickness(24,12),HorizontalAlignment=HorizontalAlignment.Left};
        back.Click+=(_,_)=>{CancelRequested?.Invoke();OnCancel?.Invoke();};
        root.Children.Add(back);Grid.SetRow(scroller,1);root.Children.Add(scroller);Content=root;
        RefreshLanguage();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            e.Handled = true;
            SubmitCurrentInput();
        }
    }

    /// <summary>
    /// 触发单次输入提交
    /// </summary>
    public void SubmitCurrentInput()
    {
        if (_isSubmitting) return;
        _isSubmitting = true;
        try
        {
            var text = _inputBox.Text?.Trim() ?? "";
            Submitted?.Invoke(text);
            OnSubmit?.Invoke(text);
        }
        finally
        {
            _isSubmitting = false;
        }
    }

    private void RequestSpeak()
    {
        SpeakRequested?.Invoke();
        OnSpeak?.Invoke();
        _currentModel.Speak?.Invoke();
    }

    /// <summary>
    /// 更新展示模型状态
    /// </summary>
    public void Update(SpellingPracticeModel model)
    {
        _currentModel = model ?? new SpellingPracticeModel();

        _progressBlock.Text = _currentModel.ProgressText;
        _progressBlock.IsVisible = !string.IsNullOrWhiteSpace(_currentModel.ProgressText);

        _meaningBlock.Text = _currentModel.Meaning;

        _phoneticBlock.Text = _currentModel.Phonetic;
        _phoneticRow.IsVisible = !string.IsNullOrWhiteSpace(_currentModel.Phonetic)
            || _currentModel.Speak != null
            || SpeakRequested != null
            || OnSpeak != null;

        if (!string.IsNullOrWhiteSpace(_currentModel.Hint))
        {
            _hintBlock.Text = _currentModel.Hint;
            _hintBlock.IsVisible = true;
        }
        else
        {
            _hintBlock.Text = "";
            _hintBlock.IsVisible = false;
        }

        if (!string.IsNullOrWhiteSpace(_currentModel.Feedback))
        {
            _feedbackBlock.Text = _currentModel.Feedback;
            _feedbackBlock.IsVisible = true;
            if (_currentModel.IsCorrect == true)
            {
                _feedbackBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
            }
            else if (_currentModel.IsCorrect == false)
            {
                _feedbackBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
            }
            else
            {
                _feedbackBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
            }
        }
        else
        {
            _feedbackBlock.Text = "";
            _feedbackBlock.IsVisible = false;
        }

        if (_currentModel.AnswerRevealed)
        {
            _nextButton.IsVisible = true;
            _submitButton.IsVisible = false;
        }
        else
        {
            _nextButton.IsVisible = false;
            _submitButton.IsVisible = true;
        }
    }

    /// <summary>
    /// 保留实际焦点控制
    /// </summary>
    public void FocusInput()
    {
        _inputBox.Focus();
    }

    /// <summary>
    /// 双语刷新
    /// </summary>
    public void RefreshLanguage()
    {
        _inputBox.Watermark = "";
        _hintButton.Content = UiText.Bilingual("提示", "Hint");
        _answerButton.Content = UiText.Bilingual("看答案", "Reveal Answer");
        _submitButton.Content = UiText.Bilingual("提交", "Submit");
        _nextButton.Content = UiText.Bilingual("下一词", "Next Word");
    }
}
