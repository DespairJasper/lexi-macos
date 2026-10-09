using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Features.Settings;

/// <summary>
/// 设置分类枚举。
/// </summary>
public enum SettingsSection
{
    None = 0,
    Appearance,
    StudyAndShortcuts,
    AiService,
    DataAndBackup,
    About
}

/// <summary>
/// 1.2.1 设置右侧抽屉组件：
/// - 右侧抽屉 440–480 DIP 有界，保留后方主页面
/// - 分类入口：外观、学习与快捷键、AI 服务、数据与备份、关于
/// - 固定 Header（标题、返回分类、关闭“✕”、保存状态提示）
/// - 正文独立滚动
/// - 支持现有设置控件分组迁入（RegisterSectionContent），不伪造假设置
/// - 提供 Open / Close / ShowSection / ShowCategories API 与 Esc 拦截
/// </summary>
public class SettingsDrawerControl : UserControl
{
    private static string T(string chinese) => UiText.Text(chinese);

    private readonly Grid _rootOverlayGrid;
    private readonly Border _scrimOverlay;
    private readonly Border _drawerPanel;

    // Header 元素
    private readonly Button _backButton;
    private readonly TextBlock _headerTitleBlock;
    private readonly TextBlock _statusMessageBlock;
    private readonly Button _closeButton;

    // Body 容器
    private readonly ScrollViewer _bodyScrollViewer;
    private readonly StackPanel _categoriesPanel;
    private readonly ContentControl _sectionContentHost;

    // Footer 容器（可选挂载节级动作，如“保存”）
    private readonly Border _footerBar;
    private readonly ContentControl _footerContentHost;

    // 注册的内容映射
    private readonly Dictionary<SettingsSection, Control> _sectionContents = new();
    private readonly Dictionary<SettingsSection, Control?> _sectionFooters = new();

    public SettingsSection CurrentSection { get; private set; } = SettingsSection.None;
    public bool IsOpen => IsVisible;

    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<SettingsSection>? SectionChanged;

    public SettingsDrawerControl()
    {
        IsVisible = false;

        _rootOverlayGrid = new Grid();

        // 1. 半透明背景遮罩（点击可收起抽屉）
        _scrimOverlay = new Border();
        _scrimOverlay.Bind(Border.BackgroundProperty, this.GetResourceObservable("DrawerOverlayBrush"));
        _scrimOverlay.PointerPressed += (_, _) => Close();
        _rootOverlayGrid.Children.Add(_scrimOverlay);

        // 2. 右侧抽屉主体（有界 440–480 DIP，默认 460）
        _drawerPanel = new Border
        {
            Width = 460,
            MinWidth = 440,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Right,
            Classes = { "settings-drawer" }
        };

        var drawerLayout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };

        // Header (Row 0, 固定)
        var headerBorder = new Border
        {
            Padding = new Thickness(20, 16),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        headerBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        _backButton = new Button
        {
            Content = T("← 返回分类"),
            Classes = { "ghost" },
            Padding = new Thickness(8, 6),
            IsVisible = false
        };
        _backButton.Click += (_, _) => ShowCategories();
        headerGrid.Children.Add(_backButton);

        _headerTitleBlock = new TextBlock
        {
            Text = T("设置"),
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        _headerTitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(_headerTitleBlock, 1);
        headerGrid.Children.Add(_headerTitleBlock);

        _statusMessageBlock = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsVisible = false
        };
        Grid.SetColumn(_statusMessageBlock, 2);
        headerGrid.Children.Add(_statusMessageBlock);

        _closeButton = new Button
        {
            Content = "✕",
            Classes = { "ghost" },
            Padding = new Thickness(10, 6)
        };
        _closeButton.Click += (_, _) => Close();
        Grid.SetColumn(_closeButton, 3);
        headerGrid.Children.Add(_closeButton);

        headerBorder.Child = headerGrid;
        drawerLayout.Children.Add(headerBorder);

        // Body (Row 1, 独立滚动)
        _bodyScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20, 16)
        };

        var bodyContainer = new StackPanel { Spacing = 16 };

        // 分类导航入口列表
        _categoriesPanel = new StackPanel { Spacing = 12 };
        BuildCategoryEntries();
        bodyContainer.Children.Add(_categoriesPanel);

        // 分类详情内容挂载宿主
        _sectionContentHost = new ContentControl { IsVisible = false };
        bodyContainer.Children.Add(_sectionContentHost);

        _bodyScrollViewer.Content = bodyContainer;
        Grid.SetRow(_bodyScrollViewer, 1);
        drawerLayout.Children.Add(_bodyScrollViewer);

        // Footer (Row 2, 可选操作栏)
        _footerBar = new Border
        {
            Padding = new Thickness(20, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsVisible = false
        };
        _footerBar.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        _footerContentHost = new ContentControl();
        _footerBar.Child = _footerContentHost;

        Grid.SetRow(_footerBar, 2);
        drawerLayout.Children.Add(_footerBar);

        _drawerPanel.Child = drawerLayout;
        _rootOverlayGrid.Children.Add(_drawerPanel);

        Content = _rootOverlayGrid;
    }

    /// <summary>
    /// 打开抽屉。可指定直接打开某一分类，或打开分类总览。
    /// </summary>
    public void Open(SettingsSection section = SettingsSection.None)
    {
        IsVisible = true;
        if (section != SettingsSection.None)
        {
            ShowSection(section);
        }
        else
        {
            ShowCategories();
        }
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 关闭抽屉。
    /// </summary>
    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        CurrentSection = SettingsSection.None;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 注册某一分类的具体内容控件（由主控将现有设置控件迁入）。
    /// </summary>
    public void RegisterSectionContent(SettingsSection section, Control content, Control? footerActions = null)
    {
        _sectionContents[section] = content;
        _sectionFooters[section] = footerActions;

        if (CurrentSection == section)
        {
            ShowSection(section);
        }
    }

    /// <summary>
    /// 切换并呈现指定分类详情。
    /// </summary>
    public void ShowSection(SettingsSection section)
    {
        CurrentSection = section;

        _categoriesPanel.IsVisible = false;
        _sectionContentHost.IsVisible = true;
        _backButton.IsVisible = true;

        _headerTitleBlock.Text = GetSectionTitle(section);

        if (_sectionContents.TryGetValue(section, out var content))
        {
            _sectionContentHost.Content = content;
        }
        else
        {
            _sectionContentHost.Content = CreateEmptySectionPlaceholder(section);
        }

        if (_sectionFooters.TryGetValue(section, out var footer) && footer != null)
        {
            _footerContentHost.Content = footer;
            _footerBar.IsVisible = true;
        }
        else
        {
            _footerContentHost.Content = null;
            _footerBar.IsVisible = false;
        }

        _bodyScrollViewer.ScrollToHome();
        SectionChanged?.Invoke(this, section);
    }

    /// <summary>
    /// 返回分类列表总览。
    /// </summary>
    public void ShowCategories()
    {
        CurrentSection = SettingsSection.None;
        _backButton.IsVisible = false;
        _headerTitleBlock.Text = T("设置");
        _categoriesPanel.IsVisible = true;
        _sectionContentHost.IsVisible = false;
        _footerBar.IsVisible = false;
        _footerContentHost.Content = null;
        _statusMessageBlock.IsVisible = false;

        _bodyScrollViewer.ScrollToHome();
        SectionChanged?.Invoke(this, SettingsSection.None);
    }

    /// <summary>
    /// 设置抽屉右上角状态消息提示（如“已保存”、“保存失败”）。
    /// </summary>
    public void SetStatusMessage(string message, bool isError = false)
    {
        if (string.IsNullOrEmpty(message))
        {
            _statusMessageBlock.IsVisible = false;
            return;
        }

        _statusMessageBlock.Text = message;
        _statusMessageBlock.IsVisible = true;
        if (isError)
        {
            _statusMessageBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));
        }
        else
        {
            _statusMessageBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        }
    }

    /// <summary>
    /// 处理全局或宿主传来的按键事件。按 Esc 优先返回分类或关闭抽屉。
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (!IsVisible) return false;

        if (e.Key == Key.Escape)
        {
            if (CurrentSection != SettingsSection.None)
            {
                ShowCategories();
            }
            else
            {
                Close();
            }
            return true;
        }

        return false;
    }

    private void BuildCategoryEntries()
    {
        _categoriesPanel.Children.Add(CreateCategoryCard(
            SettingsSection.Appearance,
            "🎨",
            T("外观与界面"),
            T("主题模式、字号大小与界面视觉偏好")));

        _categoriesPanel.Children.Add(CreateCategoryCard(
            SettingsSection.StudyAndShortcuts,
            "⌨️",
            T("学习与快捷键"),
            T("学习交互按键绑定、复习队列与评测行为")));

        _categoriesPanel.Children.Add(CreateCategoryCard(
            SettingsSection.AiService,
            "✨",
            T("AI 服务配置"),
            T("API 密钥、大模型端点与提示词配置")));

        _categoriesPanel.Children.Add(CreateCategoryCard(
            SettingsSection.DataAndBackup,
            "💾",
            T("数据与备份"),
            T("词库备份、导入导出与数据维护")));

        _categoriesPanel.Children.Add(CreateCategoryCard(
            SettingsSection.About,
            "ℹ️",
            T("关于 Lexi"),
            T("版本信息、开源许可与致谢")));
    }

    private Button CreateCategoryCard(SettingsSection section, string icon, string title, string summary)
    {
        var card = new Button { HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch };
        card.Classes.Add("ghost");
        card.Classes.Add("settings-section-card");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };

        var iconBlock = new TextBlock
        {
            Text = icon,
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        grid.Children.Add(iconBlock);

        var textStack = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold
        };
        titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        textStack.Children.Add(titleBlock);

        var summaryBlock = new TextBlock
        {
            Text = summary,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        summaryBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        textStack.Children.Add(summaryBlock);

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        var arrowBlock = new TextBlock
        {
            Text = "›",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        arrowBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        Grid.SetColumn(arrowBlock, 2);
        grid.Children.Add(arrowBlock);

        card.Content = grid;
        card.Click += (_, _) => ShowSection(section);
        return card;
    }

    private Control CreateEmptySectionPlaceholder(SettingsSection section)
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(0, 30, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var note = new TextBlock
        {
            Text = $"{GetSectionTitle(section)} - {T("等待主控挂载设置项")}",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        note.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        panel.Children.Add(note);

        return panel;
    }

    public static string GetSectionTitle(SettingsSection section) => section switch
    {
        SettingsSection.Appearance => T("外观与界面"),
        SettingsSection.StudyAndShortcuts => T("学习与快捷键"),
        SettingsSection.AiService => T("AI 服务配置"),
        SettingsSection.DataAndBackup => T("数据与备份"),
        SettingsSection.About => T("关于 Lexi"),
        _ => T("设置")
    };
}
