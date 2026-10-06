using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Lexi;

public partial class MainWindow
{
    private bool _writingDirty;
    private void RenderWritingPage()
    {
        _ieltsRows.Children.Clear();
        var query = _ieltsSearch.Text?.Trim() ?? "";
        var sentences = _ieltsCatalog!.Sentences.Where(s => query.Length == 0 || (s.Chinese + s.Category + s.BookAnswer).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _ieltsListPage = Math.Clamp(_ieltsListPage, 0, Math.Max(0, (sentences.Count - 1) / 10));
        _ieltsSummary.Text = T("100 句翻译 · 书中答案与原仓库备用译文");
        foreach (var sentence in sentences.Skip(_ieltsListPage * 10).Take(10))
        {
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(ContentText($"{sentence.Number:00}  ·  {sentence.Category}", 12));
            body.Children.Add(ContentText(sentence.Chinese, 18));
            var draft = new TextBox { Name = "WritingDraft" + sentence.Number, Text = _learningProgress.WritingDrafts.GetValueOrDefault(sentence.Number, ""), Watermark = T("输入你的英文翻译，自动保存"), AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 86 };
            draft.TextChanged += (_, _) =>
            {
                _learningProgress.WritingDrafts[sentence.Number] = draft.Text ?? ""; _writingDirty = true;
                _writingSaveTimer.Stop(); _writingSaveTimer.Start();
            };
            body.Children.Add(draft);
            var answer = new StackPanel { Spacing = 8, IsVisible = false };
            answer.Children.Add(ContentText(T("书中答案") + "\n" + sentence.BookAnswer));
            answer.Children.Add(ContentText(T("原仓库 ChatGPT 备用译文") + "\n" + sentence.AlternateAnswer));
            if (sentence.Remark.Length > 0) answer.Children.Add(ContentText(sentence.Remark, 12));
            var tools = new WrapPanel();
            var reveal = LearningButton("显示 / 隐藏答案"); reveal.Click += (_, _) => answer.IsVisible = !answer.IsVisible;
            var play = LearningButton("朗读参考译文"); play.Margin = new Thickness(8, 0, 0, 0); play.Click += (_, _) => SpeakLearningText(sentence.BookAnswer);
            tools.Children.Add(reveal); tools.Children.Add(play); body.Children.Add(tools); body.Children.Add(answer);
            _ieltsRows.Children.Add(LearningCard(body));
        }
        _ieltsPageNumber.Text = $"{_ieltsListPage + 1} / {Math.Max(1, (sentences.Count + 9) / 10)}";
        _ieltsPrevious.IsEnabled = _ieltsListPage > 0; _ieltsNext.IsEnabled = (_ieltsListPage + 1) * 10 < sentences.Count;
    }
    private void RenderIeltsResources()
    {
        _ieltsRows.Children.Clear(); _ieltsPrevious.IsEnabled = _ieltsNext.IsEnabled = false; _ieltsPageNumber.Text = "";
        _ieltsSummary.Text = T("语法课程、讲义、导图与完整听力学习笔记");
        var grammar = new StackPanel { Spacing = 12 };
        grammar.Children.Add(LearningLabel("雅思语法", 20));
        var links = new WrapPanel();
        foreach (var (label, path) in new[] { ("课程视频", _ieltsCatalog!.GrammarVideo), ("语法讲义 PDF", IeltsCatalog.ResolveAsset("grammar/雅思基础语法配套课程讲义.pdf")), ("语法思维导图", IeltsCatalog.ResolveAsset("grammar/雅思语法.svg")) })
        {
            var button = LearningButton(label); button.Margin = new Thickness(0, 0, 8, 8); button.IsEnabled = path != null;
            button.Click += (_, _) => { if (path != null) OpenLearningResource(path); }; links.Children.Add(button);
        }
        grammar.Children.Add(links);
        if (IeltsCatalog.ResolveAsset("grammar/mindmap.png") is { } image)
        {
            grammar.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(image), Stretch = Avalonia.Media.Stretch.Uniform, MaxWidth = 900 });
        }
        _ieltsRows.Children.Add(LearningCard(grammar));
        var notes = new StackPanel { Spacing = 12 };
        notes.Children.Add(LearningLabel("听力学习笔记", 20));
        var pathNotes = IeltsCatalog.ResolveAsset("listening-notes.txt");
        if (pathNotes != null) notes.Children.Add(ContentText(File.ReadAllText(pathNotes)));
        _ieltsRows.Children.Add(LearningCard(notes));
        var source = new StackPanel { Spacing = 8 };
        source.Children.Add(ContentText(T("资料来自 my-ielts；原作者禁止商业用途。阅读文件实际 376 条。口语和大小作文尚无完整内容。"), 12));
        var upstream = LearningButton("查看资料来源"); upstream.Click += (_, _) => OpenLearningResource(_ieltsCatalog.Source); source.Children.Add(upstream);
        _ieltsRows.Children.Add(LearningCard(source));
    }
    private void ConfigureSynonymPractice()
    {
        _synonymPanel = new StackPanel { Name = "SynonymPractice", Spacing = 14, IsVisible = false };
        _synonymPrompt = ContentText("", 24); _synonymFeedback = ContentText("", 14);
        _synonymWord = new TextBox { Name = "SynonymWordInput", Watermark = T("听音输入考点词") };
        _synonymInput = new TextBox { Name = "SynonymInput", Watermark = T("输入同义替换，用逗号分隔"), AcceptsReturn = true, MinHeight = 90, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var actions = new WrapPanel();
        var play = LearningButton("重播读音"); play.Click += (_, _) =>
        { if (_synonymCursor < _synonymQuestions.Count) { var word = _synonymQuestions[_synonymCursor]; _wordAudio.Play(word.Word, IeltsCatalog.ResolveAsset(word.AudioPath)); } };
        var check = LearningButton("检查答案", "SynonymCheckBtn", true); check.Click += (_, _) => CheckSynonymAnswer();
        var next = LearningButton("下一词", "SynonymNextBtn"); next.Click += (_, _) =>
        {
            if (!_synonymCorrect) { _synonymFeedback.Text = T("请先正确填写当前词及全部同义替换。"); return; }
            _synonymCursor++; RenderSynonymQuestion();
        };
        foreach (var button in new[] { play, check, next }) { button.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(button); }
        _synonymCard = LearningCard(_synonymPanel);
        _synonymPanel.Children.Add(_synonymPrompt); _synonymPanel.Children.Add(_synonymWord); _synonymPanel.Children.Add(_synonymInput); _synonymPanel.Children.Add(actions); _synonymPanel.Children.Add(_synonymFeedback);
    }
    private void BeginSynonymPractice()
    {
        _synonymQuestions = IeltsFilteredWords().Where(w => w.Synonyms.Count > 0).ToList();
        if (_synonymQuestions.Count == 0) { SetStatus(T("当前词库没有同义替换练习。")); return; }
        _synonymCursor = 0; _ieltsRows.Children.Clear(); _ieltsRows.Children.Add(_synonymCard); _synonymPanel.IsVisible = true;
        _ieltsPrevious.IsEnabled = _ieltsNext.IsEnabled = false; _ieltsPageNumber.Text = ""; RenderSynonymQuestion();
    }
    private void RenderSynonymQuestion()
    {
        _synonymCorrect = false; _synonymWord.Text = _synonymInput.Text = ""; _synonymFeedback.Text = "";
        if (_synonymCursor >= _synonymQuestions.Count) { _synonymPrompt.Text = T("本轮已完成"); StopLearningSpeech(); return; }
        var word = _synonymQuestions[_synonymCursor];
        _synonymPrompt.Text = $"{_synonymCursor + 1} / {_synonymQuestions.Count}  ·  {word.Meaning}";
        _wordAudio.Play(word.Word, IeltsCatalog.ResolveAsset(word.AudioPath)); _synonymWord.Focus();
    }
    private void CheckSynonymAnswer()
    {
        if (_synonymCursor >= _synonymQuestions.Count) return;
        var word = _synonymQuestions[_synonymCursor];
        static string Normalize(string s) => string.Join(' ', TypingSession.Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var actual = (_synonymInput.Text ?? "").Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToHashSet();
        var expected = word.Synonyms.Select(Normalize).ToHashSet();
        _synonymCorrect = word.Words.Any(w => Normalize(w) == Normalize(_synonymWord.Text ?? "")) && actual.SetEquals(expected);
        _synonymFeedback.Text = _synonymCorrect ? T("正确，可以进入下一词。") : T("请重新填写：") + word.Word + "\n" + string.Join(" · ", word.Synonyms);
        if (!_synonymCorrect) { _learningProgress.Errors.Add(word.Id); SaveLearningProgress(); }
    }
}
