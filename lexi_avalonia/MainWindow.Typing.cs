using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace Lexi;

public partial class MainWindow
{
    private readonly TypingSession _typingSession = new();
    private readonly TypingFeedbackAudio _typingFeedbackAudio = new();
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
    private DispatcherTimer? _typingShakeTimer;
    private TranslateTransform? _typingShakeTransform;
    private readonly Stopwatch _typingShakeClock = new();
    private bool _typingShakeTimerHooked;
    private bool _typingAttemptWrongFeedbackPlayed;
    private const double TypingShakeDurationMs = 820;
    private static readonly double[] TypingShakeOffsets = [0, -1, 2, -4, 4, -4, 4, -4, 2, -1, 0];

    private void RenderTypingSetup()
    {
        if (_typingSetupView == null) return;
        _typingAdvanceCts?.Cancel(); _typingPlaying = false; ++_typingLoadEpoch;
        _planTypingActive = false;
        Classes.Set("typing-focus", false); SetFullPageReview(false);
        _typingSetupView.IsVisible = true; _typingPlayArea.IsVisible = false; _typingStart.IsEnabled = true;
    }
    private void StopTypingRound()
    {
        var returnToPlans = _planTypingActive;
        StopLearningSpeech(); RenderTypingSetup();
        if (returnToPlans) ShowPage("plans");
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
        var hints = _typingMode.SelectedIndex == 0;
        if (!_planTypingActive) { _learningProgress.TypingHints = hints; SaveLearningProgress(); }
        _typingSession.Reset(_typingDeck, hints);
        _typingPlaying = true; _typingSetupView.IsVisible = false; _typingPlayArea.IsVisible = true;
        Classes.Set("typing-focus", true); SetFullPageReview(true);
        RenderTypingWord();
    }
    private void RenderTypingWord()
    {
        UpdatePlanTypingActivity();
        StopTypingShake();
        _typingPlayArea.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "TypingSaveBtn").IsVisible = !_planTypingActive;
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
        var original = _planTypingActive ? null : _ieltsCatalog?.Find(word.Word);
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
        var letters = TypingFeedbackModel.Build(target, input, _typingSession.Hints, _typingSession.Outcome);
        foreach (var letter in letters)
        {
            var character = letter.Character;
            var run = new Avalonia.Controls.Documents.Run(failed && character == ' ' ? "·" : character.ToString());
            var dark = _settings.Theme == "Dark";
            run.Foreground = letter.Tone switch
            {
                TypingLetterTone.Correct => (IBrush?)this.FindResource(ActualThemeVariant, "SuccessBrush"),
                TypingLetterTone.Wrong => new SolidColorBrush(Color.Parse(_typingSession.Hints
                    ? (dark ? "#DC2626" : "#F87171") : (dark ? "#F87171" : "#DC2626"))),
                TypingLetterTone.Neutral => new SolidColorBrush(Color.Parse(dark ? "#F9FAFB" : "#4B5563")),
                _ => new SolidColorBrush(Color.Parse("#9CA3AF"))
            };
            _typingLetters.Inlines.Add(run);
        }
        var showAnswerLine = failed && !_typingSession.Hints;
        _typingFeedback.IsVisible = showAnswerLine;
        _typingFeedback.Text = showAnswerLine ? target : "";
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
        var submitted = _typingInput.Text ?? "";
        var previousInput = _typingSession.Input;
        var normalizedSubmission = TypingSession.Normalize(submitted);
        if (_typingSession.Outcome == TypingOutcome.Retry && previousInput.Length == 0 && normalizedSubmission.Length > 0)
            _typingAttemptWrongFeedbackPlayed = false;
        var addedCharacters = normalizedSubmission.Length > previousInput.Length && normalizedSubmission.StartsWith(previousInput, StringComparison.Ordinal);
        var target = TypingSession.Normalize(_typingSession.Current.Word);
        var addedWrongCharacter = addedCharacters && Enumerable.Range(previousInput.Length, normalizedSubmission.Length - previousInput.Length)
            .Any(index => index >= target.Length || normalizedSubmission[index] != target[index]);
        var outcome = _typingSession.Submit(submitted);
        UpdatePlanTypingActivity();
        var wrongFeedbackPlayed = false;
        if (addedWrongCharacter)
        {
            _typingFeedbackAudio.PlayWrong(); ShakeTypingError(); wrongFeedbackPlayed = true; _typingAttemptWrongFeedbackPlayed = true;
        }
        if (outcome == TypingOutcome.Retry)
        {
            if (!wrongFeedbackPlayed && !_typingAttemptWrongFeedbackPlayed) { _typingFeedbackAudio.PlayWrong(); ShakeTypingError(); _typingAttemptWrongFeedbackPlayed = true; }
            if (!_planTypingActive) { _learningProgress.Errors.Add(_typingSession.Current.Id); SaveLearningProgress(); }
            _typingUpdating = true; _typingInput.Text = ""; _typingUpdating = false;
        }
        else if (outcome == TypingOutcome.Correct)
        {
            _typingAttemptWrongFeedbackPlayed = false;
            _typingFeedbackAudio.PlayCorrect();
            if (!_planTypingActive) { _learningProgress.Typed.Add(_typingSession.Current.Id); SaveLearningProgress(); }
            _typingInput.IsEnabled = false;
            _typingAdvanceCts?.Cancel(); var cts = _typingAdvanceCts = new CancellationTokenSource();
            _ = AdvanceTypingAsync(cts.Token);
        }
        else if (addedCharacters && !wrongFeedbackPlayed) _typingFeedbackAudio.PlayKey();
        RenderTypingLetters();
    }
    private void ShakeTypingError()
    {
        _typingShakeTransform ??= new TranslateTransform();
        _typingWordArea.RenderTransform = _typingShakeTransform;
        var timer = _typingShakeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Stop(); _typingShakeClock.Restart(); _typingShakeTransform.X = 0;
        if (!_typingShakeTimerHooked)
        {
            _typingShakeTimerHooked = true;
            timer.Tick += (_, _) =>
            {
                var progress = _typingShakeClock.Elapsed.TotalMilliseconds / TypingShakeDurationMs;
                if (progress >= 1)
                {
                    timer.Stop(); _typingShakeClock.Stop(); _typingShakeTransform!.X = 0; return;
                }

                var segment = Math.Min((int)(progress * (TypingShakeOffsets.Length - 1)), TypingShakeOffsets.Length - 2);
                var segmentProgress = progress * (TypingShakeOffsets.Length - 1) - segment;
                var eased = CubicBezierProgress(segmentProgress, .36, .07, .19, .97);
                _typingShakeTransform!.X = TypingShakeOffsets[segment] + (TypingShakeOffsets[segment + 1] - TypingShakeOffsets[segment]) * eased;
            };
        }
        timer.Start();
    }
    private static double CubicBezierProgress(double x, double x1, double y1, double x2, double y2)
    {
        static double Curve(double t, double p1, double p2)
        {
            var inverse = 1 - t;
            return 3 * inverse * inverse * t * p1 + 3 * inverse * t * t * p2 + t * t * t;
        }

        var low = 0d; var high = 1d;
        for (var i = 0; i < 12; i++)
        {
            var t = (low + high) / 2;
            if (Curve(t, x1, x2) < x) low = t; else high = t;
        }
        return Curve((low + high) / 2, y1, y2);
    }
    private void StopTypingShake()
    {
        _typingShakeTimer?.Stop(); _typingShakeClock.Stop();
        if (_typingShakeTransform == null) return;
        _typingShakeTransform.X = 0; _typingWordArea.RenderTransform = null;
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
