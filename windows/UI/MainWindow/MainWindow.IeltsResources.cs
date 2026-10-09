using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lexi.Features.Ielts;

namespace Lexi;

public partial class MainWindow
{
    private List<LearningWord> _synonymQuestions = [];
    private int _synonymCursor;
    private bool _synonymCorrect;
    private TextBox? _synonymWordInput;
    private TextBox? _synonymAnswersInput;
    private TextBlock? _synonymFeedback;

    private void StartIeltsSynonyms(List<LearningWord> words)
    {
        _synonymQuestions = words.Where(w => w.Synonyms.Count > 0).ToList();
        _synonymCursor = 0;
        if (_synonymQuestions.Count == 0)
        {
            SetStatus(IeltsI18n.T("当前范围没有同义替换练习，已切换至全部同义替换浏览。"));
            ShowIeltsSynonymsBrowser();
            return;
        }

        // 切换到子页面展示
        if (!ShowIeltsSubPage(bounded: false)) return;
        RenderIeltsSynonym();
    }

    private bool ShowIeltsSubPage(bool bounded = false)
    {
        if (!TryFlushWritingDraft()) return false;
        ShowPage("ielts");
        if (_ieltsWorkspace != null) _ieltsWorkspace.IsVisible = false;
        if (_ieltsSubContentPage != null)
        {
            _ieltsSubContentPage.IsVisible = true;
            _ieltsSubContentPage.VerticalScrollBarVisibility = bounded
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        }
        _ieltsSubContent?.Children.Clear();
        return true;
    }

    private void ShowIeltsSynonymsBrowser(LearningSection? currentSection = null)
    {
        if (!ShowIeltsSubPage(bounded: true)) return;
        if (_ieltsSubContent == null || _ieltsCatalog == null) return;

        var browser = new IeltsSynonymBrowser(
            catalog: _ieltsCatalog,
            initialSection: currentSection,
            playerProvider: GetLearningAudio,
            startPractice: StartIeltsSynonyms,
            onBack: ShowIeltsCatalog
        );
        _ieltsSubContent.Children.Add(browser);
    }

    private void RenderIeltsSynonym()
    {
        if (_ieltsSubContent == null) return;
        _ieltsSubContent.Children.Clear();
        _synonymCorrect = false;

        var body = new StackPanel { Spacing = 14 };

        var topBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = LearningText(IeltsI18n.T("同义替换听写"), 22);
        Grid.SetColumn(title, 0);
        topBar.Children.Add(title);

        var backBtn = LearningButton(IeltsI18n.T("返回 IELTS 目录"), ShowIeltsCatalog);
        Grid.SetColumn(backBtn, 1);
        topBar.Children.Add(backBtn);
        body.Children.Add(topBar);

        if (_synonymCursor >= _synonymQuestions.Count)
        {
            body.Children.Add(LearningText(IeltsI18n.T("同义替换训练已完成。"), 18));
            _ieltsSubContent.Children.Add(LearningCard(body));
            return;
        }

        var word = _synonymQuestions[_synonymCursor];
        body.Children.Add(LearningText($"{IeltsI18n.T("同义替换听写")} {_synonymCursor + 1}/{_synonymQuestions.Count} · {word.Meaning}", 16));

        _synonymWordInput = new TextBox
        {
            Watermark = IeltsI18n.T("听音输入考点词"),
            Name = "SynonymWordInput",
            FontSize = 14
        };
        _synonymAnswersInput = new TextBox
        {
            Watermark = IeltsI18n.T("全部同义词，以逗号分隔"),
            Name = "SynonymAnswersInput",
            AcceptsReturn = true,
            FontSize = 14
        };
        _synonymFeedback = LearningText("");

        body.Children.Add(_synonymWordInput);
        body.Children.Add(_synonymAnswersInput);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(LearningButton(IeltsI18n.T("重播读音"), () => GetLearningAudio().Play(word.Word, IeltsCatalog.ResolveAsset(word.AudioPath))));
        actions.Children.Add(LearningButton(IeltsI18n.T("检查全部答案"), CheckIeltsSynonyms, true));
        actions.Children.Add(LearningButton(IeltsI18n.T("下一词"), () =>
        {
            if (!_synonymCorrect)
            {
                SetStatus(IeltsI18n.T("请正确输入考点词及全部同义替换后继续。"));
                return;
            }
            _synonymCursor++;
            RenderIeltsSynonym();
        }));
        body.Children.Add(actions);

        body.Children.Add(_synonymFeedback);
        _ieltsSubContent.Children.Add(LearningCard(body));

        GetLearningAudio().Play(word.Word, IeltsCatalog.ResolveAsset(word.AudioPath));
    }

    private void CheckIeltsSynonyms()
    {
        if (_synonymCursor >= _synonymQuestions.Count) return;
        var word = _synonymQuestions[_synonymCursor];

        static string Normalize(string value) =>
            string.Join(' ', TypingSession.Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var actual = (_synonymAnswersInput?.Text ?? "")
            .Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _synonymCorrect = word.Words.Any(w => Normalize(w) == Normalize(_synonymWordInput?.Text ?? "")) &&
                          actual.SetEquals(word.Synonyms.Select(Normalize));

        _synonymFeedback!.Text = _synonymCorrect
            ? IeltsI18n.T("全部正确，可以进入下一词。")
            : IeltsI18n.T("请重新填写：") + word.Word + " · " + string.Join("、", word.Synonyms);

        if (!_synonymCorrect)
            _ieltsProgress.Errors.Add(word.Id);
        else
            _ieltsProgress.Typed.Add(word.Id);

        SaveIeltsProgress();
    }

    private void ShowIeltsResources()
    {
        if (!ShowIeltsSubPage(bounded: true)) return;
        if (_ieltsSubContent == null || _ieltsCatalog == null) return;

        var view = new IeltsResourceView(
            catalog: _ieltsCatalog,
            onSelectSection: SelectIeltsSection,
            onOpenResource: OpenIeltsResource,
            onBack: ShowIeltsCatalog
        );
        EnhanceListeningResources(view);
        _ieltsSubContent.Children.Add(view);
    }

    private void OpenIeltsResource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { SetStatus("该资源暂不可用。"); return; }
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme is not ("http" or "https"))
        {
            SetStatus("资源地址格式无效。");
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(UiText.Bilingual("无法打开资源：", "Cannot open resource: ") + ex.Message); }
    }

    private void ShowIeltsWriting()
    {
        if (!ShowIeltsSubPage(bounded: true)) return;
        if (_ieltsSubContent == null || _ieltsCatalog == null) return;

        var workspace = new IeltsWritingWorkspace(
            catalog: _ieltsCatalog,
            drafts: _ieltsProgress.WritingDrafts,
            save: () =>
            {
                if (_restoring || !_databaseAvailable || _ieltsProgressBlocked)
                    throw new InvalidOperationException(UiText.Bilingual("数据库不可用或学习记录被锁定", "Database unavailable or progress locked"));
                var path = Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "learning-progress.json");
                _ieltsProgress.Save(path);
            },
            speak: text => GetLearningAudio().Play(text),
            back: ShowIeltsCatalog
        );
        _ieltsSubContent.Children.Add(workspace);
    }

    private void EnhanceListeningResources(IeltsResourceView view)
    {
        try
        {
            if (view.Children.Count > 2 && view.Children[2] is Border notesCard && notesCard.Child is Grid notesStack)
            {
                if (notesStack.Children.Count > 1 && notesStack.Children[1] is ScrollViewer notesScroll)
                {
                    var notesPath = IeltsCatalog.ResolveAsset("listening-notes.txt");
                    var notesText = (notesPath != null && File.Exists(notesPath)) ? File.ReadAllText(notesPath) : "";
                    var selectableNotes = new SelectableTextBlock
                    {
                        Text = notesText,
                        FontSize = 13,
                        LineHeight = 22,
                        TextWrapping = TextWrapping.Wrap
                    };
                    notesScroll.Content = selectableNotes;
                }
            }

            if (view.Children.Count > 1 && view.Children[1] is ScrollViewer mainScroll && mainScroll.Content is StackPanel contentStack)
            {
                var passagesCard = CreateListeningPassagesCard();
                if (contentStack.Children.Count > 0)
                    contentStack.Children.Insert(1, passagesCard);
                else
                    contentStack.Children.Add(passagesCard);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EnhanceListeningResources Error] {ex.Message}");
        }
    }

    private Border CreateListeningPassagesCard()
    {
        var card = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(20, 16)
        };
        var stack = new StackPanel { Spacing = 10 };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*") };
        var titleStack = new StackPanel { Spacing = 3 };
        titleStack.Children.Add(new TextBlock { Text = UiText.Bilingual("听力练习示例", "Listening practice examples"), Classes = { "eyebrow" }, FontSize = 12 });
        var title = new TextBlock
        {
            Text = UiText.Bilingual("情景句子与朗读", "Situational sentences & audio"),
            FontSize = 16,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap
        };
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        titleStack.Children.Add(title);
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        stack.Children.Add(header);

        var desc = new TextBlock
        {
            Text = UiText.Bilingual("以下为情景练习示例，非雅思真题原文。可选中文本复制，点击扬声器图标朗读，点击 ＋ 收藏到金句本。",
                                   "These are situational practice examples, not official IELTS passages. Select text to copy; use the speaker icon to listen and ＋ to save to quotes."),
            Classes = { "muted" },
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(desc);

        var passages = new (string Section, string English, string Chinese)[]
        {
            ("Section 1 · 租房日常咨询 (Dialogue: Accommodation Enquiry)",
             "Good morning, I'm calling about the apartment advertised on Bridge Street. Could you tell me if water and heating are included in the monthly rent?",
             "早上好，我打电话咨询布里奇街广告上的那套公寓。请问月租金里包含水费和暖气费吗？"),
            ("Section 2 · 景区导览介绍 (Monologue: Tourism & Heritage Park)",
             "Welcome to the heritage park. Before we begin the tour, please note that photography is strictly prohibited inside the historical exhibition hall.",
             "欢迎来到文化遗产公园。在开始游览之前，请注意历史展览馆内严禁拍照。"),
            ("Section 3 · 学术导师研讨 (Tutorial: Research Methodology)",
             "We need to analyze the survey responses thoroughly before submitting our research proposal to the supervisor next Monday.",
             "在下周一向导师提交研究方案之前，我们需要彻底分析问卷调查结果。"),
            ("Section 4 · 环境科学讲座 (Lecture: Environmental Adaptation)",
             "Recent archaeological findings suggest that early human settlements adapted remarkably well to severe climate fluctuations.",
             "最新的考古发现表明，早期人类定居点对剧烈的气候波动展现出了非凡的适应能力。")
        };

        var passageList = new StackPanel { Spacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (sec, en, zh) in passages)
        {
            var pBox = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#08808080")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10)
            };
            var pStack = new StackPanel { Spacing = 6 };
            pStack.Children.Add(new TextBlock { Text = sec, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.8 });

            var enBlock = new SelectableTextBlock
            {
                Text = en,
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22
            };
            pStack.Children.Add(enBlock);

            var zhBlock = new SelectableTextBlock
            {
                Text = zh,
                FontSize = 13,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            };
            pStack.Children.Add(zhBlock);

            var actions = CreateExampleActions(en, zh);
            pStack.Children.Add(actions);

            pBox.Child = pStack;
            passageList.Children.Add(pBox);
        }
        stack.Children.Add(passageList);

        card.Child = stack;
        return card;
    }
}

