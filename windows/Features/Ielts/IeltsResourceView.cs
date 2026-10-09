using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Features.Ielts;

/// <summary>
/// IELTS 听力与语法资料分类浏览视图：
/// - 分类资源卡片：“听力笔记与章节录音”、“语法讲义与思维导图”、“来源许可”；
/// - 移除失效视频入口，呈现本地 PDF、SVG、PNG 与章节录音状态；
/// - 长笔记采用单层滚动有界阅读器，消除双层正文滚动。
/// </summary>
public sealed class IeltsResourceView : Grid
{
    private readonly IeltsCatalog _catalog;
    private readonly Action<LearningSection> _onSelectSection;
    private readonly Action<string?> _onOpenResource;
    private readonly Action _onBack;

    private readonly Border _notesViewerCard;
    private readonly TextBlock _notesContentBlock;
    private bool _notesExpanded;
    private readonly ScrollViewer _mainScroll;

    public IeltsResourceView(
        IeltsCatalog catalog,
        Action<LearningSection> onSelectSection,
        Action<string?> onOpenResource,
        Action onBack)
    {
        _catalog = catalog;
        _onSelectSection = onSelectSection;
        _onOpenResource = onOpenResource;
        _onBack = onBack;

        RowDefinitions = new RowDefinitions("Auto,*");
        Margin = new Thickness(24, 16, 24, 20);

        // ---------------- ROW 0: 顶部标题与返回 ----------------
        var topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 16)
        };

        var title = new TextBlock
        {
            Text = UiText.T("听力与语法学习资料"),
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(title, 0);
        topBar.Children.Add(title);

        var backBtn = new Button
        {
            Content = UiText.T("返回 IELTS 目录"),
            Classes = { "secondary" },
            Padding = new Thickness(14, 7),
            FontSize = 13
        };
        backBtn.Click += (_, _) => _onBack();
        Grid.SetColumn(backBtn, 1);
        topBar.Children.Add(backBtn);

        Grid.SetRow(topBar, 0);
        Children.Add(topBar);

        // ---------------- ROW 1: 资源分类卡片 ----------------
        var contentStack = new StackPanel { Spacing = 14, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

        // 1. 听力笔记与章节录音卡片
        var listeningCard = CreateListeningCard();
        contentStack.Children.Add(listeningCard);

        // 听力笔记有界详情阅读器（默认折叠，单层滚动）
        var notesPath = IeltsCatalog.ResolveAsset("listening-notes.txt");
        _notesViewerCard = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(18, 14),
            Margin = new Thickness(0, -6, 0, 4),
            IsVisible = false
        };

        var notesStack = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var notesHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var notesTitle = new TextBlock
        {
            Text = UiText.T("完整听力笔记"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        notesTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(notesTitle, 0);
        notesHeader.Children.Add(notesTitle);

        if (notesPath != null)
        {
            var openExternalBtn = new Button
            {
                Content = UiText.T("在外部程序中打开"),
                Classes = { "secondary" },
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 12
            };
            openExternalBtn.Click += (_, _) => _onOpenResource(notesPath);
            Grid.SetColumn(openExternalBtn, 1);
            notesHeader.Children.Add(openExternalBtn);
        }

        var closeNotesBtn = new Button
        {
            Content = UiText.T("收起听力笔记"),
            Classes = { "secondary" },
            Padding = new Thickness(10, 4),
            FontSize = 12
        };
        closeNotesBtn.Click += (_, _) => ToggleNotes(false);
        Grid.SetColumn(closeNotesBtn, 2);
        notesHeader.Children.Add(closeNotesBtn);
        notesStack.Children.Add(notesHeader);

        _notesContentBlock = new TextBlock
        {
            FontSize = 13,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap
        };
        if (notesPath != null && File.Exists(notesPath))
        {
            _notesContentBlock.Text = File.ReadAllText(notesPath);
        }

        var notesScroll = new ScrollViewer
        {
            Content = _notesContentBlock,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(notesScroll,1); notesStack.Children.Add(notesScroll);
        _notesViewerCard.Child = notesStack;

        // 2. 语法讲义与思维导图卡片
        var grammarCard = CreateGrammarCard();
        contentStack.Children.Add(grammarCard);

        // 3. 来源与许可卡片
        var licenseCard = CreateLicenseCard();
        contentStack.Children.Add(licenseCard);

        _mainScroll = new ScrollViewer
        {
            Content = contentStack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_mainScroll, 1);
        Children.Add(_mainScroll);
        Grid.SetRow(_notesViewerCard,1); Children.Add(_notesViewerCard);
    }

    private Border CreateListeningCard()
    {
        var card = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(20, 16)
        };

        var stack = new StackPanel { Spacing = 10 };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var headerStack = new StackPanel { Spacing = 3 };

        var eyebrow = new TextBlock { Text = UiText.T("听力资料"), Classes = { "eyebrow" }, FontSize = 11 };
        var cardTitle = new TextBlock
        {
            Text = UiText.T("听力笔记与章节录音"),
            FontSize = 16,
            FontWeight = FontWeight.Bold
        };
        cardTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        headerStack.Children.Add(eyebrow);
        headerStack.Children.Add(cardTitle);
        Grid.SetColumn(headerStack, 0);
        top.Children.Add(headerStack);

        var notesExist = IeltsCatalog.ResolveAsset("listening-notes.txt") != null;
        var badge = new Border
        {
            Classes = { "badge" },
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = notesExist ? UiText.T("本地文件就绪") : "缺失",
                Classes = { "badge-text" }
            }
        };
        Grid.SetColumn(badge, 1);
        top.Children.Add(badge);
        stack.Children.Add(top);

        var desc = new TextBlock
        {
            Text = UiText.T("包含雅思听力高频考点词汇、听力笔记及章节录音音频。"),
            Classes = { "muted" },
            FontSize = 13
        };
        stack.Children.Add(desc);

        // 听力各章节快捷入口
        var listeningSections = _catalog.Sections.Where(s => s.Kind == "listening").ToList();
        if (listeningSections.Count > 0)
        {
            var secWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var sec in listeningSections)
            {
                var secBtn = new Button
                {
                    Content = $"{sec.Title} ({sec.Entries.Count})",
                    Classes = { "secondary" },
                    Padding = new Thickness(12, 6),
                    Margin = new Thickness(0, 0, 8, 6),
                    FontSize = 12
                };
                secBtn.Click += (_, _) => _onSelectSection(sec);
                secWrap.Children.Add(secBtn);
            }
            stack.Children.Add(secWrap);
        }

        // 完整笔记动作按钮
        if (notesExist)
        {
            var noteActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            var toggleBtn = new Button
            {
                Content = UiText.T("展开完整听力笔记"),
                Classes = { "primary" },
                Padding = new Thickness(14, 6),
                FontSize = 12
            };
            toggleBtn.Click += (_, _) => ToggleNotes(!_notesExpanded);
            noteActions.Children.Add(toggleBtn);
            stack.Children.Add(noteActions);
        }

        card.Child = stack;
        return card;
    }

    private Border CreateGrammarCard()
    {
        var card = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(20, 16)
        };

        var stack = new StackPanel { Spacing = 10 };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var headerStack = new StackPanel { Spacing = 3 };

        var eyebrow = new TextBlock { Text = UiText.T("语法资料"), Classes = { "eyebrow" }, FontSize = 11 };
        var cardTitle = new TextBlock
        {
            Text = UiText.T("语法讲义与思维导图"),
            FontSize = 16,
            FontWeight = FontWeight.Bold
        };
        cardTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        headerStack.Children.Add(eyebrow);
        headerStack.Children.Add(cardTitle);
        Grid.SetColumn(headerStack, 0);
        top.Children.Add(headerStack);

        var pdfPath = IeltsCatalog.ResolveAsset("grammar/雅思基础语法配套课程讲义.pdf");
        var svgPath = IeltsCatalog.ResolveAsset("grammar/雅思语法.svg");
        var pngPath = IeltsCatalog.ResolveAsset("grammar/mindmap.png");

        var badge = new Border
        {
            Classes = { "badge" },
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = pdfPath != null || svgPath != null ? UiText.T("本地资源完整") : "未安装",
                Classes = { "badge-text" }
            }
        };
        Grid.SetColumn(badge, 1);
        top.Children.Add(badge);
        stack.Children.Add(top);

        var desc = new TextBlock
        {
            Text = UiText.T("雅思基础语法核心思维导图与完整配套讲义。"),
            Classes = { "muted" },
            FontSize = 13
        };
        stack.Children.Add(desc);

        // 本地语法资料动作（去掉失效视频，仅提供经过验证的本地真实资料）
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

        if (pdfPath != null)
        {
            var pdfBtn = new Button
            {
                Content = "📄 " + UiText.T("打开语法讲义 PDF"),
                Classes = { "secondary" },
                Padding = new Thickness(14, 6),
                Margin = new Thickness(0, 0, 8, 6),
                FontSize = 12
            };
            pdfBtn.Click += (_, _) => _onOpenResource(pdfPath);
            actions.Children.Add(pdfBtn);
        }

        if (svgPath != null)
        {
            var svgBtn = new Button
            {
                Content = "🗺 " + UiText.T("打开语法思维导图 SVG"),
                Classes = { "secondary" },
                Padding = new Thickness(14, 6),
                Margin = new Thickness(0, 0, 8, 6),
                FontSize = 12
            };
            svgBtn.Click += (_, _) => _onOpenResource(svgPath);
            actions.Children.Add(svgBtn);
        }

        if (pngPath != null)
        {
            var pngBtn = new Button
            {
                Content = "🖼 " + UiText.T("打开思维导图图片 PNG"),
                Classes = { "secondary" },
                Padding = new Thickness(14, 6),
                Margin = new Thickness(0, 0, 8, 6),
                FontSize = 12
            };
            pngBtn.Click += (_, _) => _onOpenResource(pngPath);
            actions.Children.Add(pngBtn);
        }

        stack.Children.Add(actions);
        card.Child = stack;
        return card;
    }

    private Border CreateLicenseCard()
    {
        var card = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(20, 14)
        };

        var stack = new StackPanel { Spacing = 8 };

        var eyebrow = new TextBlock { Text = UiText.T("来源许可"), Classes = { "eyebrow" }, FontSize = 11 };
        stack.Children.Add(eyebrow);

        var licenseText = new TextBlock
        {
            Text = UiText.T("资料来自 my-ielts；原作者禁止商业用途。口语和大小作文尚无完整内容。"),
            Classes = { "muted" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(licenseText);

        if (!string.IsNullOrWhiteSpace(_catalog.Source))
        {
            var sourceBtn = new Button
            {
                Content = UiText.T("访问资料开源主页"),
                Classes = { "secondary" },
                Padding = new Thickness(12, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 12
            };
            sourceBtn.Click += (_, _) => _onOpenResource(_catalog.Source);
            stack.Children.Add(sourceBtn);
        }

        card.Child = stack;
        return card;
    }

    private void ToggleNotes(bool expand)
    {
        _notesExpanded = expand;
        _notesViewerCard.IsVisible = expand;
        _mainScroll.IsVisible = !expand;
    }
}
