using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Shell;

/// <summary>
/// 1.2.1 全局专注模式宿主容器：
/// - 沉浸式学习视图容器，隐藏侧栏与管理 chrome
/// - 有界 Header、Body、Footer 结构
/// - 动态主题：浅色柔和纸白、深色低对比蓝灰分层，绝不使用突兀纯黑 (#000000)
/// - 保证 760×520 完整展示，正文长内容滚动而底部评分栏固定
/// - 直接挂载原学习会话 Control（AttachSessionContent），不重造学习算法
/// </summary>
public class FocusWorkspaceHost : UserControl
{
    private static string T(string chinese) => UiText.Text(chinese);

    private readonly Border _rootContainer;

    // Header 区域控件
    private readonly Border _headerBorder;
    private readonly Button _backButton;
    private readonly TextBlock _taskTitleBlock;
    private readonly TextBlock _taskSubtitleBlock;
    private readonly TextBlock _progressBlock;
    private readonly Border _streakBadge;
    private readonly TextBlock _streakBlock;
    private readonly Button _audioButton;
    private readonly Button _starButton;
    private readonly Button _moreButton;

    // Body 区域控件
    private readonly ScrollViewer _bodyScrollViewer;
    private readonly ContentControl _sessionContentHost;

    // Footer 区域控件
    private readonly Border _footerBorder;
    private readonly ContentControl _footerContentHost;

    // 内置标准评分栏元素
    private readonly StackPanel _standardFooterPanel;
    private readonly TextBlock _feedbackBlock;
    private readonly Button _primaryActionButton;
    private readonly StackPanel _ratingButtonsPanel;
    private readonly Button _forgotButton;
    private readonly Button _unsureButton;
    private readonly Button _knownButton;
    private Action? _onForgot, _onUnsure, _onKnown, _onPrimary;

    public bool IsInFocus => IsVisible;

    public event EventHandler? ExitRequested;
    public event Action<bool>? ChromeVisibilityChanged;
    public event Action? AudioRequested;
    public event Action? StarRequested;
    public event Action? MoreRequested;

    public FocusWorkspaceHost()
    {
        IsVisible = false;

        _rootContainer = new Border
        {
            Classes = { "focus-workspace-host" },
            MinWidth = 0,
            MinHeight = 0
        };

        var mainLayout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };

        // =======================
        // 1. Header (有界顶部栏)
        // =======================
        _headerBorder = new Border
        {
            Classes = { "focus-header" }
        };

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        // 返回按钮（不设默认焦点，防 Space 误触）
        _backButton = new Button
        {
            Content = $"← {T("退出专注")} (Esc)",
            Classes = { "ghost" },
            Padding = new Thickness(10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        _backButton.Click += (_, _) => ExitFocus();
        headerGrid.Children.Add(_backButton);

        // 任务标题与说明
        var titleStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0),
            Spacing = 2
        };

        _taskTitleBlock = new TextBlock
        {
            Text = T("今日复习"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold
        };
        _taskTitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        titleStack.Children.Add(_taskTitleBlock);

        _taskSubtitleBlock = new TextBlock
        {
            FontSize = 11,
            IsVisible = false
        };
        _taskSubtitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        titleStack.Children.Add(_taskSubtitleBlock);

        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(titleStack);

        // 进度与连击展示
        var statsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };

        _progressBlock = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        _progressBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        statsStack.Children.Add(_progressBlock);

        _streakBadge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        _streakBadge.Bind(Border.BackgroundProperty, this.GetResourceObservable("TintBrush"));

        _streakBlock = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.Medium
        };
        _streakBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
        _streakBadge.Child = _streakBlock;
        statsStack.Children.Add(_streakBadge);

        Grid.SetColumn(statsStack, 2);
        headerGrid.Children.Add(statsStack);

        // 轻量化辅助操作
        var toolStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        _audioButton = new Button
        {
            Content = "🔊",
            Classes = { "ghost" },
            Padding = new Thickness(8, 6),
            Focusable = false
        };
        ToolTip.SetTip(_audioButton, $"{T("朗读当前词")} (↑)");
        _audioButton.Click += (_, _) => AudioRequested?.Invoke();
        toolStack.Children.Add(_audioButton);

        _starButton = new Button
        {
            Content = "⭐",
            Classes = { "ghost" },
            Padding = new Thickness(8, 6),
            Focusable = false
        };
        ToolTip.SetTip(_starButton, $"{T("收藏到词汇档案")} (Ctrl+D)");
        _starButton.Click += (_, _) => StarRequested?.Invoke();
        toolStack.Children.Add(_starButton);

        _moreButton = new Button
        {
            Content = "•••",
            Classes = { "ghost" },
            Padding = new Thickness(8, 6),
            Focusable = false
        };
        ToolTip.SetTip(_moreButton, T("更多操作"));
        _moreButton.Click += (_, _) => MoreRequested?.Invoke();
        toolStack.Children.Add(_moreButton);

        Grid.SetColumn(toolStack, 3);
        headerGrid.Children.Add(toolStack);

        _headerBorder.Child = headerGrid;
        mainLayout.Children.Add(_headerBorder);

        // =======================
        // 2. Body (独立滚动正文)
        // =======================
        _bodyScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24, 20)
        };

        _sessionContentHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 720
        };
        _sessionContentHost.Content = CreatePlaceholderContent();

        _bodyScrollViewer.Content = _sessionContentHost;
        Grid.SetRow(_bodyScrollViewer, 1);
        mainLayout.Children.Add(_bodyScrollViewer);

        // =======================
        // 3. Footer (固定操作栏)
        // =======================
        _footerBorder = new Border
        {
            Classes = { "focus-footer" }
        };

        _footerContentHost = new ContentControl();

        // 默认组装标准评分栏
        _standardFooterPanel = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _feedbackBlock = new TextBlock
        {
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            IsVisible = false
        };
        _feedbackBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        _standardFooterPanel.Children.Add(_feedbackBlock);

        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _primaryActionButton = new Button
        {
            Content = T("查看释义 ↵"),
            Classes = { "primary" },
            MinWidth = 160,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false
        };
        actionsRow.Children.Add(_primaryActionButton);

        _ratingButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            IsVisible = false
        };

        _forgotButton = new Button
        {
            Content = $"{T("忘记")} (←)",
            Classes = { "secondary" },
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false
        };
        _ratingButtonsPanel.Children.Add(_forgotButton);

        _unsureButton = new Button
        {
            Content = $"{T("模糊")} (↓)",
            Classes = { "secondary" },
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false
        };
        _ratingButtonsPanel.Children.Add(_unsureButton);

        _knownButton = new Button
        {
            Content = $"{T("认识")} (→)",
            Classes = { "secondary" },
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false
        };
        _ratingButtonsPanel.Children.Add(_knownButton);
        _forgotButton.Click += (_, _) => _onForgot?.Invoke();
        _unsureButton.Click += (_, _) => _onUnsure?.Invoke();
        _knownButton.Click += (_, _) => _onKnown?.Invoke();
        _primaryActionButton.Click += (_, _) => _onPrimary?.Invoke();

        actionsRow.Children.Add(_ratingButtonsPanel);
        _standardFooterPanel.Children.Add(actionsRow);

        _footerContentHost.Content = _standardFooterPanel;
        _footerBorder.Child = _footerContentHost;

        Grid.SetRow(_footerBorder, 2);
        mainLayout.Children.Add(_footerBorder);

        _rootContainer.Child = mainLayout;
        Content = _rootContainer;
    }

    /// <summary>
    /// 进入专注模式。通知主控隐藏侧边栏与管理 chrome。
    /// </summary>
    public void EnterFocus()
    {
        IsVisible = true;
        ChromeVisibilityChanged?.Invoke(false);
    }

    /// <summary>
    /// 退出专注模式。通知主控恢复原页面与管理 chrome。
    /// </summary>
    public void ExitFocus()
    {
        if (!IsVisible) return;
        IsVisible = false;
        ChromeVisibilityChanged?.Invoke(true);
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 挂载原会话学习视图控件。
    /// </summary>
    public void AttachSessionContent(Control content)
    {
        _sessionContentHost.Content = content;
        _bodyScrollViewer.ScrollToHome();
    }

    /// <summary>
    /// 解挂会话内容，返还给原主界面。
    /// </summary>
    public Control? DetachSessionContent()
    {
        var existing = _sessionContentHost.Content as Control;
        _sessionContentHost.Content = CreatePlaceholderContent();
        return existing;
    }

    /// <summary>
    /// 设置顶部任务信息与进度指示。
    /// </summary>
    public void SetTaskInfo(string title, string progressText, string? subtitle = null, int streakCount = 0)
    {
        _taskTitleBlock.Text = title;
        _progressBlock.Text = progressText;

        if (!string.IsNullOrEmpty(subtitle))
        {
            _taskSubtitleBlock.Text = subtitle;
            _taskSubtitleBlock.IsVisible = true;
        }
        else
        {
            _taskSubtitleBlock.IsVisible = false;
        }

        if (streakCount > 0)
        {
            _streakBlock.Text = $"🔥 {T("连击")} {streakCount}";
            _streakBadge.IsVisible = true;
        }
        else
        {
            _streakBadge.IsVisible = false;
        }
    }

    /// <summary>
    /// 挂载自定义 Footer 操作栏。
    /// </summary>
    public void SetFooterContent(Control? customFooter)
    {
        if (customFooter != null)
        {
            _footerContentHost.Content = customFooter;
        }
        else
        {
            _footerContentHost.Content = _standardFooterPanel;
        }
    }

    /// <summary>
    /// 配置标准评分栏状态与回调。
    /// </summary>
    public void SetupStandardRatingBar(
        Action? onForgot,
        Action? onUnsure,
        Action? onKnown,
        Action? onRevealOrNext = null,
        bool isRevealed = false,
        string? feedback = null)
    {
        _onForgot = onForgot; _onUnsure = onUnsure; _onKnown = onKnown; _onPrimary = onRevealOrNext;
        _footerContentHost.Content = _standardFooterPanel;

        if (!string.IsNullOrEmpty(feedback))
        {
            _feedbackBlock.Text = feedback;
            _feedbackBlock.IsVisible = true;
        }
        else
        {
            _feedbackBlock.IsVisible = false;
        }

        if (isRevealed)
        {
            _primaryActionButton.IsVisible = false;
            _ratingButtonsPanel.IsVisible = true;

        }
        else
        {
            _primaryActionButton.IsVisible = true;
            _ratingButtonsPanel.IsVisible = false;
            _primaryActionButton.Content = T("查看释义 ↵");
        }
    }

    private Control CreatePlaceholderContent()
    {
        var panel = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(0, 60, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var text = new TextBlock
        {
            Text = T("等待学习内容挂载..."),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        panel.Children.Add(text);

        return panel;
    }
}
