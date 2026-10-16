using Avalonia.Controls;

namespace Lexi;

public partial class MainWindow
{
    private ExamCatalog _examCatalog = new();
    private string? _activeExamId;
    private readonly Dictionary<string, (int Kind, string? Section)> _examSelections = new(StringComparer.Ordinal);
    private TextBlock _examPageTitle = null!;
    private readonly Dictionary<string, Button> _examNavigationButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _examChineseUnitTitles = new(StringComparer.Ordinal);

    private string ExamTitle(ExamBook book) => UiText.Language == "en" ? book.EnglishTitle : book.Title;
    private List<ExamBook> CurrentExamBooks => _examCatalog.Books.Where(b => b.TopicId == _activeExamId).ToList();
    private ExamBook? ActiveExamBook => _activeExamId == null ? null : CurrentExamBooks.ElementAtOrDefault(Math.Max(0, _ieltsKind?.SelectedIndex ?? 0));
    private bool IsTopicVocabulary => _activeExamId != null || _ieltsKind.SelectedIndex < 4;
    private string ExamTopicTitle(string id) => id switch
    {
        "cet4" => UiText.Language == "en" ? "CET-4" : "四级",
        "cet6" => UiText.Language == "en" ? "CET-6" : "六级",
        "kaoyan" => UiText.Language == "en" ? "Postgraduate English" : "考研",
        "gmat" => "GMAT", "toefl" => "TOEFL", "gre" => "GRE", _ => id
    };
    private List<LearningWord> CurrentTopicWords() => (ActiveExamBook?.AllWords
        ?? _ieltsCatalog!.Sections.Where(s => !s.Kind.StartsWith("exam:", StringComparison.Ordinal)).SelectMany(s => s.Entries)).ToList();

    // Selected-topic lookup keeps overlapping spellings attached to their own stable source IDs.
    private LearningWord? FindLearningWord(string? word)
    {
        if (_currentPage == "ielts" || _wordFocusActive && _wordFocusSnapshot?.Page == "ielts")
        {
            var hit = ActiveExamBook?.AllWords.FirstOrDefault(w => w.Words.Any(form => string.Equals(form, word, StringComparison.OrdinalIgnoreCase)));
            if (hit != null) return hit;
        }
        return _ieltsCatalog?.Find(word);
    }

    private string[] TopicKinds() => _activeExamId != null ? CurrentExamBooks.Select(ExamTitle).ToArray() :
        [T("词汇真经"), T("听力 179"), T("阅读同义替换"), T("英美拼写规范"), T("写作 100 句"), T("语法与听力资料")];

    private void ConfigureExamTopics()
    {
        _examCatalog = ExamCatalog.Load();
        foreach (var book in _examCatalog.Books)
        {
            _ieltsCatalog!.Sections.AddRange(book.Sections);
            foreach (var section in book.Sections) _examChineseUnitTitles[section.Id] = section.Title;
        }
        foreach (var id in _examCatalog.Books.Select(b => b.TopicId).Distinct(StringComparer.Ordinal))
        {
            var button = new Button { Name = "NavExam_" + id, Classes = { "nav" }, FontSize = 13 };
            button.Click += (_, _) => OpenExamTopic(id);
            _examNavigationButtons.Add(id, button); ExamNavigationItems.Children.Add(button);
        }
        NavExams.Click += (_, _) =>
        {
            if (!CanChangeExamTopic()) return;
            OpenExamTopic(_activeExamId);
        };
        NavExamsBack.Click += (_, _) =>
        {
            if (!CanChangeExamTopic()) return;
            MainNavigation.IsVisible = true; ExamNavigation.IsVisible = false;
        };
        RefreshExamTopicLanguage();
    }

    private bool CanChangeExamTopic() => FocusCanNavigate && !PlanDialogOverlay.IsVisible && _planActionOverlay?.IsVisible != true;

    private void OpenExamTopic(string? id)
    {
        if (!CanChangeExamTopic()) return;
        if (id != null && !_examNavigationButtons.ContainsKey(id)) return;
        StopLearningSpeech(); _ieltsFilterTimer.Stop();
        if (_writingDirty) SaveLearningProgress();
        if (_activeExamId != id)
        {
            _examSelections[_activeExamId ?? "ielts"] = (_ieltsKind.SelectedIndex, (_ieltsSection.SelectedItem as LearningSection)?.Id);
            _activeExamId = id;
            _examSelections.TryGetValue(id ?? "ielts", out var selection);
            _learningConfiguring = true;
            _ieltsKind.ItemsSource = TopicKinds(); _ieltsKind.SelectedIndex = Math.Clamp(selection.Kind, 0, _ieltsKind.ItemCount - 1);
            _ieltsSearch.Text = ""; _ieltsErrorsOnly.IsChecked = false; _ieltsListPage = 0;
            PopulateIeltsSections();
            if (selection.Section != null && _ieltsSection.ItemsSource is IEnumerable<LearningSection> sections)
                _ieltsSection.SelectedItem = sections.FirstOrDefault(s => s.Id == selection.Section) ?? _ieltsSection.SelectedItem;
            _learningConfiguring = false;
        }
        RefreshExamTopicLanguage();
        MainNavigation.IsVisible = false; ExamNavigation.IsVisible = true;
        ShowPage("ielts"); RenderIeltsPage();
    }

    private void RefreshExamNavigationActive(string page)
    {
        NavExams.Classes.Set("active", page == "ielts");
        NavIelts.Classes.Set("active", page == "ielts" && _activeExamId == null);
        foreach (var (id, button) in _examNavigationButtons) button.Classes.Set("active", page == "ielts" && id == _activeExamId);
    }

    private void RefreshExamTopicLanguage()
    {
        NavExams.Content = T("考试专题"); NavExamsBack.Content = "← " + T("主导航");
        foreach (var book in _examCatalog.Books)
        {
            foreach (var section in book.Sections)
                section.Title = UiText.Language == "en" ? section.EnglishTitle : _examChineseUnitTitles[section.Id];
        }
        foreach (var (id, button) in _examNavigationButtons) button.Content = ExamTopicTitle(id);
        // Rebind the unit selector after mutating its display labels, preserving selection and IDs.
        if (_ieltsSection != null)
        {
            var guard = _learningConfiguring; _learningConfiguring = true;
            var items = _ieltsSection.ItemsSource; var selected = _ieltsSection.SelectedItem;
            _ieltsSection.ItemsSource = null; _ieltsSection.ItemsSource = items; _ieltsSection.SelectedItem = selected;
            _learningConfiguring = guard;
        }
        if (_examPageTitle != null) _examPageTitle.Text = _activeExamId != null
            ? ExamTopicTitle(_activeExamId) + (UiText.Language == "en" ? " learning" : " 学习专题") : T("IELTS 学习专题");
        RefreshExamNavigationActive(_currentPage);
    }

    private string TypingSourceTitle((string Id, string Title) source)
    {
        var book = _examCatalog.Books.FirstOrDefault(b => b.Sections.Any(s => s.Id == source.Id));
        if (book == null) return T(source.Title);
        var section = book.Sections.Single(s => s.Id == source.Id);
        return ExamTitle(book) + " · " + section.Title;
    }

    private string PlanSourceDisplay(DailyStudyPlan plan)
    {
        var book = _examCatalog.Books.FirstOrDefault(b => plan.Words.FirstOrDefault()?.Id.StartsWith("exam:" + b.BookId + ":", StringComparison.Ordinal) == true);
        if (book == null) return UiText.Redisplay(plan.SourceLabel);
        var ids = plan.Words.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var units = book.Sections.Where(s => s.Entries.Any(e => ids.Contains(e.Id))).Take(2).ToList();
        return ExamTitle(book) + " · " + (units.Count == 1 ? units[0].Title : T("整本词书"));
    }

    private static readonly string[] PlanExamTopicIds = ["cet4", "cet6", "kaoyan", "gmat", "toefl", "gre"];
    private string? SelectedPlanExamTopic => PlanExamInput.SelectedIndex > 0
        ? PlanExamTopicIds[PlanExamInput.SelectedIndex - 1] : null;
    private List<ExamBook> SelectedPlanExamBooks => _examCatalog.Books.Where(b => b.TopicId == SelectedPlanExamTopic).ToList();
    private string[] PlanExamTitles() => new[] { "IELTS" }.Concat(PlanExamTopicIds.Select(ExamTopicTitle)).ToArray();
    private string[] PlanBookTitles() => SelectedPlanExamTopic == null
        ? [T("词汇真经"), T("听力 179"), T("阅读同义替换"), T("英美拼写规范")]
        : SelectedPlanExamBooks.Select(ExamTitle).ToArray();

    private void PopulatePlanBooks()
    {
        _planDialogConfiguring = true;
        PlanBookInput.ItemsSource = PlanBookTitles(); PlanBookInput.SelectedIndex = 0;
        _planDialogConfiguring = false; PopulatePlanUnits();
    }
}
