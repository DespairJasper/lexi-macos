using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Microsoft.Data.Sqlite;

namespace Lexi;

public partial class MainWindow
{
    private readonly TypingSession _typingSession = new();
    private ComboBox _typingSource = null!, _typingMode = null!;
    private TextBox _typingInput = null!;
    private TextBlock _typingLetters = null!, _typingMeaning = null!, _typingFeedback = null!, _typingStats = null!;
    private Button _typingStart = null!;
    private bool _typingPlaying, _typingUpdating;
    private CancellationTokenSource? _typingAdvanceCts;
    private List<LearningWord> _typingDeck = [];
    private List<LearningWord>? _dictionaryTypingWords;
    private readonly List<(string Id, string Title)> _typingSources = [];
    private int _typingLoadEpoch;
    private string _typingDeckTitle = "";

    private void RenderTypingSetup()
    {
        if (_typingSetupView == null) return;
        _typingAdvanceCts?.Cancel(); _typingPlaying = false; ++_typingLoadEpoch;
        Classes.Set("typing-focus", false); SetFullPageReview(false);
        _typingSetupView.IsVisible = true; _typingPlayArea.IsVisible = false; _typingStart.IsEnabled = true;
    }
    private void StopTypingRound()
    {
        StopLearningSpeech(); RenderTypingSetup();
    }
    private void OpenTypingWords(List<LearningWord> words, string title)
    {
        if (!FocusCanNavigate) return;
        ShowPage("typing"); StartTypingRound(words, title);
    }
    private static LearningWord ArchiveLearningWord(WordItem word) => new()
    { Id = "archive:" + word.Id, Words = [word.Word], Meaning = word.Translation, Phonetic = word.Phonetic, Extra = word.Definition, Example = word.AiResult?.Examples.FirstOrDefault()?.English ?? "" };
    private async Task StartTypingSourceAsync()
    {
        if (!FocusCanNavigate || !_typingStart.IsEnabled || _typingSource.SelectedIndex < 0) return;
        var source = _typingSources[_typingSource.SelectedIndex];
        var epoch = ++_typingLoadEpoch; _typingStart.IsEnabled = false;
        try
        {
            List<LearningWord> words;
            if (source.Id == "dictionary")
            {
                _dictionaryTypingWords ??= await Task.Run(ReadDictionaryTypingWords);
                words = [.. _dictionaryTypingWords];
            }
            else if (source.Id == "archive") words = _allWords.Select(ArchiveLearningWord).ToList();
            else if (source.Id == "due") words = GetPendingReviewWords().Select(ArchiveLearningWord).ToList();
            else if (source.Id == "ielts") words = _ieltsCatalog!.AllWords.ToList();
            else if (source.Id == "synonyms") words = _ieltsCatalog!.AllWords.SelectMany(w => w.Synonyms.Select((synonym, i) => new LearningWord
            { Id = w.Id + ":synonym:" + i, Words = [synonym], Meaning = w.Meaning, Extra = w.Word })).ToList();
            else words = _ieltsCatalog!.Sections.Single(s => s.Id == source.Id).Entries;
            if (epoch != _typingLoadEpoch || _currentPage != "typing" || _isForceClose) return;
            StartTypingRound(words, T(source.Title));
        }
        catch (Exception ex) { SetStatus(T("词库读取失败：") + ex.Message); }
        finally { if (epoch == _typingLoadEpoch) _typingStart.IsEnabled = true; }
    }
    private static List<LearningWord> ReadDictionaryTypingWords()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DictionaryService.ResolveDictionaryPath(), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT word,phonetic,translation FROM ecdict ORDER BY word";
        using var reader = command.ExecuteReader(); var words = new List<LearningWord>(60000);
        while (reader.Read())
        {
            var word = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(word)) continue;
            words.Add(new LearningWord { Id = "dictionary:" + word, Words = [word], Phonetic = reader.IsDBNull(1) ? "" : reader.GetString(1), Meaning = reader.IsDBNull(2) ? "" : reader.GetString(2) });
        }
        return words;
    }
    private void StartTypingRound(List<LearningWord> words, string title) => BeginTypingRound(SelectLearningRound(words, _typingRoundOptions), title);
    private void BeginTypingRound(List<LearningWord> selected, string title)
    {
        _typingAdvanceCts?.Cancel(); StopLearningSpeech();
        _typingDeck = selected.ToList(); _typingDeckTitle = title;
        _learningProgress.TypingHints = _typingMode.SelectedIndex == 0; SaveLearningProgress();
        _typingSession.Reset(_typingDeck, _learningProgress.TypingHints);
        _typingPlaying = true; _typingSetupView.IsVisible = false; _typingPlayArea.IsVisible = true;
        Classes.Set("typing-focus", true); SetFullPageReview(true);
        RenderTypingWord();
    }
    private void RenderTypingWord()
    {
        _typingUpdating = true; _typingInput.Text = ""; _typingUpdating = false;
        _typingFeedback.Text = ""; _typingFeedback.IsVisible = false;
        var playing = _typingSession.Current != null;
        _typingWordBody.IsVisible = _typingFooter.IsVisible = playing; _typingFinished.IsVisible = !playing;
        _typingInput.IsEnabled = playing;
        _typingProgress.Text = $"{_typingSession.Cursor}/{_typingSession.Count}";
        _typingReplay.Content = "▷  ⌥T";
        if (_typingSession.Current is { } word)
        {
            _typingMeaning.Text = (word.Pos + " " + word.Meaning).Trim();
            ReplayTypingWord(); RenderTypingLetters(); _typingInput.Focus();
        }
        else
        {
            StopLearningSpeech(); RenderTypingStats();
            ((Button)_typingFinished.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "TypingErrorsBtn")).IsEnabled = _typingSession.ErrorIds.Count > 0;
        }
    }
    private void ReplayTypingWord(bool showPhonetic = false)
    {
        if (_typingSession.Current is not { } word) return;
        var original = _ieltsCatalog?.Find(word.Word);
        var recording = IeltsCatalog.ResolveAsset(word.AudioPath) ??
            (string.Equals(original?.Word, word.Word, StringComparison.OrdinalIgnoreCase) ? IeltsCatalog.ResolveAsset(original?.AudioPath) : null);
        _wordAudio.Play(word.Word, recording);
        if (showPhonetic)
        {
            var phonetic = string.IsNullOrWhiteSpace(word.Phonetic) ? original?.Phonetic ?? "" : word.Phonetic;
            _typingReplay.Content = "▷  " + (phonetic.Length > 0 ? "/" + phonetic.Trim('/') + "/" : "⌥T");
        }
    }
    private void RenderTypingLetters()
    {
        _typingLetters.Text = null; _typingLetters.Inlines!.Clear();
        if (_typingSession.Current is not { } word) return;
        var target = TypingSession.Normalize(word.Word);
        var failed = _typingSession.Outcome == TypingOutcome.Retry;
        var input = failed ? _typingSession.FailedInput : _typingSession.Input;
        var length = failed ? input.Length : _typingSession.Hints ? target.Length : input.Length;
        for (var i = 0; i < length; i++)
        {
            var typed = i < input.Length;
            var character = typed ? input[i] : target[i];
            var run = new Avalonia.Controls.Documents.Run(failed && character == ' ' ? "·" : character.ToString());
            run.Foreground = failed ? new SolidColorBrush(Color.Parse(i < target.Length && character == target[i] ? "#169E85" : "#D84D43"))
                : typed && _typingSession.Hints ? new SolidColorBrush(Color.Parse("#169E85"))
                : typed ? (IBrush?)this.FindResource(ActualThemeVariant, "InkBrush")
                : new SolidColorBrush(Color.Parse(_settings.Theme == "Dark" ? "#75858E" : "#BAC8CC"));
            _typingLetters.Inlines.Add(run);
        }
        _typingFeedback.IsVisible = failed;
        _typingFeedback.Text = failed ? target : "";
        var available = Math.Max(240, (Bounds.Width > 0 ? Bounds.Width : Width) - 100);
        var fontSize = Math.Clamp(available / (Math.Max(target.Length, input.Length) * .65 + 1), 20, 44);
        _typingInput.FontSize = _typingLetters.FontSize = _typingFeedback.FontSize = fontSize;
        var visibleLength = _typingSession.Hints || failed ? Math.Max(target.Length, input.Length) : input.Length;
        _typingWordArea.Width = Math.Min(available, Math.Max(2, visibleLength * fontSize * .602 + (visibleLength > 0 ? 6 : 0)));
        _typingInput.CaretIndex = _typingInput.Text?.Length ?? 0;
    }
    private void ConfirmTypingWord()
    {
        if (!_typingPlaying || _typingSession.Current == null) return;
        if (_typingSession.Outcome == TypingOutcome.Correct)
        { _typingAdvanceCts?.Cancel(); if (_typingSession.Advance()) RenderTypingWord(); }
        else HandleTypingInput();
        _typingInput.Focus();
    }
    private void HandleTypingInput()
    {
        if (_typingUpdating || !_typingPlaying || _typingSession.Current == null || _currentPage != "typing") return;
        if (_typingSession.Outcome == TypingOutcome.Correct || (_typingSession.Outcome == TypingOutcome.Retry && string.IsNullOrEmpty(_typingInput.Text))) return;
        var outcome = _typingSession.Submit(_typingInput.Text ?? "");
        if (outcome == TypingOutcome.Retry)
        {
            _learningProgress.Errors.Add(_typingSession.Current.Id); SaveLearningProgress();
            _typingUpdating = true; _typingInput.Text = ""; _typingUpdating = false;
        }
        else if (outcome == TypingOutcome.Correct)
        {
            _learningProgress.Typed.Add(_typingSession.Current.Id); SaveLearningProgress();
            _typingInput.IsEnabled = false;
            _typingAdvanceCts?.Cancel(); var cts = _typingAdvanceCts = new CancellationTokenSource();
            _ = AdvanceTypingAsync(cts.Token);
        }
        RenderTypingLetters();
    }
    private async Task AdvanceTypingAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested && _typingPlaying && _currentPage == "typing" && _typingSession.Advance()) RenderTypingWord();
        }
        catch (OperationCanceledException) { }
    }
    private void RenderTypingStats() => _typingStats.Text = TF($"准确率 {_typingSession.Accuracy:F0}% · {_typingSession.Wpm} WPM · 重试 {_typingSession.Retries} 次");
    private void RefreshTypingLabels()
    {
        if (_typingSource == null) return;
        void Refresh(ComboBox combo, IEnumerable<string> items)
        { var index = combo.SelectedIndex; combo.ItemsSource = items.ToList(); combo.SelectedIndex = index; }
        Refresh(_typingSource, _typingSources.Select(s => T(s.Title)));
        Refresh(_typingMode, new[] { T("淡写"), T("默写") });
        RefreshRoundControls(_typingRoundOptions); RefreshRoundControls(_ieltsRoundOptions);
        if (_typingPlaying) { if (_typingSession.Current == null) RenderTypingStats(); else RenderTypingLetters(); }
    }
}
