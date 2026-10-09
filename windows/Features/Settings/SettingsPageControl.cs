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
/// 1.2.3 全页面横向分类设置组件：
/// - 完整页面呈现，最大宽度 960 DIP，单 ScrollViewer 纵向正文滚动
/// - 顶部简约 Header：设置标题 (24)、状态提示、关闭/返回按键；移除大宣传卡、离线标语和手风琴说明
/// - 横向可滚动分类栏：分类文字中性，当前类别底线或轻底指示
/// - 正文同 PaperBrush 统一底色，消除嵌套白卡
/// - 注册原控件迁入逻辑继续工作，语言切换不丢失表单输入
/// - ShowCategories 直接切换至 Appearance 分类
/// </summary>
public class SettingsPageControl : UserControl
{
    private sealed class CategoryTabSlot
    {
        public SettingsSection Section { get; init; }
        public Border IndicatorBorder { get; init; } = null!;
        public Button TabButton { get; init; } = null!;
        public TextBlock TitleBlock { get; init; } = null!;
    }

    private readonly TextBlock _headerTitleBlock;
    private readonly TextBlock _statusMessageBlock;
    private readonly Button _closeButton;

    private readonly StackPanel _categoryTabsStack;
    private readonly Dictionary<SettingsSection, CategoryTabSlot> _tabs = new();
    private readonly Dictionary<SettingsSection, Control> _sectionContents = new();
    private readonly Dictionary<SettingsSection, Control?> _sectionFooters = new();

    private readonly ContentControl _contentHost;
    private readonly Border _footerBar;
    private readonly ContentControl _footerHost;

    public ScrollViewer BodyScroller { get; }

    public SettingsSection CurrentSection { get; private set; } = SettingsSection.None;
    public bool IsOpen => IsVisible;

    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<SettingsSection>? SectionChanged;

    public SettingsPageControl()
    {
        IsVisible = false;

        // 顶层单滚动容器：禁用横向滚动，纵向正文自适应单滚动
        BodyScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        // 页面核心容器：居中、最大宽度 960 DIP、内边距 24 DIP
        var pageContainer = new StackPanel
        {
            MaxWidth = 960,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(24),
            Spacing = 16
        };

        // 1. 顶部 Header 区域（仅标题 24、就近状态提示、关闭/返回键，无大宣传卡）
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 4, 0, 8)
        };

        _headerTitleBlock = new TextBlock
        {
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        _headerTitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(_headerTitleBlock, 0);
        headerGrid.Children.Add(_headerTitleBlock);

        _statusMessageBlock = new TextBlock
        {
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        Grid.SetColumn(_statusMessageBlock, 1);
        headerGrid.Children.Add(_statusMessageBlock);

        _closeButton = new Button
        {
            Content = "✕",
            Classes = { "ghost" },
            Padding = new Thickness(12, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        _closeButton.Click += (_, _) => Close();
        Grid.SetColumn(_closeButton, 2);
        headerGrid.Children.Add(_closeButton);

        pageContainer.Children.Add(headerGrid);

        // 2. 横向分类栏（可滚动分类条，中性文字，当前类别底线或轻底指示）
        var categoryScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _categoryTabsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        CreateCategoryTab(SettingsSection.Appearance);
        CreateCategoryTab(SettingsSection.StudyAndShortcuts);
        CreateCategoryTab(SettingsSection.AiService);
        CreateCategoryTab(SettingsSection.DataAndBackup);
        CreateCategoryTab(SettingsSection.About);

        categoryScroller.Content = _categoryTabsStack;

        var categoryBarBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 4)
        };
        categoryBarBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        categoryBarBorder.Child = categoryScroller;

        pageContainer.Children.Add(categoryBarBorder);

        // 3. 正文内容承载区（与 PaperBrush 同底色，无嵌套白卡）
        _contentHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0)
        };
        pageContainer.Children.Add(_contentHost);

        // 4. 底部动作区
        _footerBar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 16, 0, 0),
            Margin = new Thickness(0, 16, 0, 0),
            IsVisible = false
        };
        _footerBar.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        _footerHost = new ContentControl();
        _footerBar.Child = _footerHost;
        pageContainer.Children.Add(_footerBar);

        BodyScroller.Content = pageContainer;
        Content = BodyScroller;

        RefreshLanguage();
    }

    private void CreateCategoryTab(SettingsSection section)
    {
        var indicatorBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(2, 0, 2, 4)
        };

        var titleBlock = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));

        var tabBtn = new Button
        {
            Classes = { "ghost" },
            Padding = new Thickness(14, 8),
            Content = titleBlock,
            CornerRadius = new CornerRadius(6)
        };
        tabBtn.Click += (_, _) => ShowSection(section);

        indicatorBorder.Child = tabBtn;
        _categoryTabsStack.Children.Add(indicatorBorder);

        _tabs[section] = new CategoryTabSlot
        {
            Section = section,
            IndicatorBorder = indicatorBorder,
            TabButton = tabBtn,
            TitleBlock = titleBlock
        };
    }

    /// <summary>
    /// 打开全页面设置。默认切换至外观分类。
    /// </summary>
    public void Open(SettingsSection section = SettingsSection.Appearance)
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
    /// 关闭设置页面。
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
            MountActiveContent(section);
        }
    }

    /// <summary>
    /// 切换横向分类，更新指示样式并挂载正文控件。
    /// </summary>
    public void ShowSection(SettingsSection section)
    {
        CurrentSection = section;

        // 更新横向分类选项卡的高亮与底线指示
        foreach (var (sec, tab) in _tabs)
        {
            if (sec == section && section != SettingsSection.None)
            {
                tab.IndicatorBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("PrimaryGreen"));
                tab.TabButton.Bind(Button.BackgroundProperty, this.GetResourceObservable("SelectionBrush"));
                tab.TitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
                tab.TitleBlock.FontWeight = FontWeight.SemiBold;
            }
            else
            {
                tab.IndicatorBorder.BorderBrush = Brushes.Transparent;
                tab.TabButton.Background = Brushes.Transparent;
                tab.TitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
                tab.TitleBlock.FontWeight = FontWeight.Normal;
            }
        }

        MountActiveContent(section);

        SectionChanged?.Invoke(this, section);
    }

    /// <summary>
    /// 1.2.3 替代旧手风琴收起：直接选择默认外观 (Appearance) 分类。
    /// </summary>
    public void ShowCategories()
    {
        ShowSection(SettingsSection.Appearance);
    }

    private void MountActiveContent(SettingsSection section)
    {
        if (section == SettingsSection.None)
        {
            _contentHost.Content = null;
            _footerHost.Content = null;
            _footerBar.IsVisible = false;
            return;
        }

        if (_sectionContents.TryGetValue(section, out var content))
        {
            _contentHost.Content = content;
        }
        else
        {
            _contentHost.Content = CreateEmptySectionPlaceholder(section);
        }

        if (_sectionFooters.TryGetValue(section, out var footer) && footer != null)
        {
            _footerHost.Content = footer;
            _footerBar.IsVisible = true;
        }
        else
        {
            _footerHost.Content = null;
            _footerBar.IsVisible = false;
        }
    }

    /// <summary>
    /// 设置右上角状态提示（如“已保存”、“保存失败”）。
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
    /// 全局按键处理：Esc 键退出设置页面。
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (!IsVisible) return false;

        if (e.Key == Key.Escape)
        {
            Close();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 刷新双语界面文本，确保中英切换即时生效且不丢弃已有表单输入。
    /// </summary>
    public void RefreshLanguage()
    {
        _headerTitleBlock.Text = UiText.Bilingual("设置", "Settings");
        _closeButton.Content = "✕";

        foreach (var (section, tab) in _tabs)
        {
            tab.TitleBlock.Text = GetSectionTitle(section);
        }

        // 若当前展示的是空占位符，刷新占位文本
        if (CurrentSection != SettingsSection.None && !_sectionContents.ContainsKey(CurrentSection))
        {
            _contentHost.Content = CreateEmptySectionPlaceholder(CurrentSection);
        }
    }

    private Control CreateEmptySectionPlaceholder(SettingsSection section)
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(0, 32, 0, 32),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var note = new TextBlock
        {
            Text = $"{GetSectionTitle(section)} - {UiText.Bilingual("等待主控挂载设置项", "Waiting for host controls to be mounted")}",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        note.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        panel.Children.Add(note);

        return panel;
    }

    public static string GetSectionTitle(SettingsSection section) => section switch
    {
        SettingsSection.Appearance => UiText.Bilingual("外观与界面", "Appearance & Interface"),
        SettingsSection.StudyAndShortcuts => UiText.Bilingual("学习与快捷键", "Study & Shortcuts"),
        SettingsSection.AiService => UiText.Bilingual("AI 服务配置", "AI Service Settings"),
        SettingsSection.DataAndBackup => UiText.Bilingual("数据与备份", "Data & Backup"),
        SettingsSection.About => UiText.Bilingual("关于 Lexi", "About Lexi"),
        _ => UiText.Bilingual("设置", "Settings")
    };

    public static string GetSectionSummary(SettingsSection section) => section switch
    {
        SettingsSection.Appearance => UiText.Bilingual("主题模式、字号大小与界面视觉偏好", "Theme mode, font sizing, and visual preferences"),
        SettingsSection.StudyAndShortcuts => UiText.Bilingual("学习交互按键绑定、复习队列与评测行为", "Study interaction keybindings, review queue, and rating behavior"),
        SettingsSection.AiService => UiText.Bilingual("API 密钥、大模型端点与提示词配置", "API keys, LLM endpoints, and prompt configuration"),
        SettingsSection.DataAndBackup => UiText.Bilingual("词库备份、导入导出与数据维护", "Vocabulary backup, import/export, and data maintenance"),
        SettingsSection.About => UiText.Bilingual("版本信息、开源许可与致谢", "Version information, open-source licenses, and credits"),
        _ => ""
    };
}
