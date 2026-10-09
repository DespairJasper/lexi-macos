using System.Diagnostics;
using System.Text;

namespace Lexi;

public enum TypingOutcome { Pending, Retry, Correct, Finished }

/// <summary>Both modes require a full correct spelling before advancing. UI never owns grading.</summary>
public sealed class TypingSession
{
    private List<LearningWord> _words = [];
    private readonly Stopwatch _clock = new();
    private int _submittedCharacters, _correctCharacters, _completedCharacters;
    public LearningWord? Current => Cursor < _words.Count ? _words[Cursor] : null;
    public int Cursor { get; private set; }
    public int Count => _words.Count;
    public bool Hints { get; private set; }
    public string Input { get; private set; } = "";
    public string FailedInput { get; private set; } = "";
    public IReadOnlyList<int> ErrorPositions { get; private set; } = [];
    public TypingOutcome Outcome { get; private set; } = TypingOutcome.Finished;
    public int Retries { get; private set; }
    private readonly HashSet<int> _revealed = [];
    private bool _answerViewed;
    public int AssistedCount { get; private set; }
    public int UnassistedCorrectCount { get; private set; }
    public string HintText => Current == null ? "" : string.Concat(Normalize(Current.Word).Select((ch,i)=>char.IsLetter(ch)&&!_revealed.Contains(i)?'_':ch));
    public bool RevealHint()
    {
        if (!Hints || Current == null || Outcome == TypingOutcome.Correct) return false;
        var target=Normalize(Current.Word);
        for(var i=0;i<target.Length;i++) if(char.IsLetter(target[i])&&!_revealed.Contains(i))
        { _revealed.Add(i); return true; }
        return false;
    }
    public void RevealAnswer()
    {
        if(Current==null||Outcome==TypingOutcome.Correct)return;
        for(var i=0;i<Normalize(Current.Word).Length;i++)_revealed.Add(i);
        _answerViewed=true;ErrorIds.Add(Current.Id);
    }
    public HashSet<string> ErrorIds { get; } = [];
    public double Accuracy => _submittedCharacters == 0 ? 100 : 100d * _correctCharacters / _submittedCharacters;
    public int Wpm => _clock.Elapsed.TotalMinutes > 0 ? (int)(_completedCharacters / 5d / _clock.Elapsed.TotalMinutes) : 0;
    public void Reset(IEnumerable<LearningWord> words, bool hints)
    {
        _words = words.Where(w => !string.IsNullOrWhiteSpace(w.Word)).ToList();
        Cursor = Retries = _submittedCharacters = _correctCharacters = _completedCharacters = 0;
        Hints = hints; AssistedCount=UnassistedCorrectCount=0; ErrorIds.Clear(); _clock.Reset(); ClearAttempt();
        Outcome = Current == null ? TypingOutcome.Finished : TypingOutcome.Pending;
    }
    public static string Normalize(string input) => string.Concat(input.Normalize(NormalizationForm.FormC)
        .Select(c => char.IsWhiteSpace(c) || c == '\u200b' ? ' ' : char.ToLowerInvariant(c)));
    public TypingOutcome Submit(string value)
    {
        if (Current == null) return Outcome = TypingOutcome.Finished;
        if (Outcome == TypingOutcome.Correct) return Outcome;
        var input = Normalize(value);
        var target = Normalize(Current.Word);
        if (input.Length > 0 && !_clock.IsRunning) _clock.Start();
        Input = input;
        // No-hint mode intentionally exposes no per-letter correctness before full length.
        if (!Hints && input.Length < target.Length) return Outcome = TypingOutcome.Pending;
        var errors = Enumerable.Range(0, Math.Max(input.Length, target.Length))
            .Where(i => i < input.Length && (i >= target.Length || input[i] != target[i])).ToArray();
        if (errors.Length > 0)
        {
            _submittedCharacters += input.Length;
            _correctCharacters += Enumerable.Range(0, Math.Min(input.Length, target.Length)).Count(i => input[i] == target[i]);
            FailedInput = input; ErrorPositions = errors; Input = "";
            Retries++; ErrorIds.Add(Current.Id);
            return Outcome = TypingOutcome.Retry;
        }
        if (input.Length == target.Length)
        {
            _submittedCharacters += input.Length; _correctCharacters += input.Length;
            _completedCharacters += input.Length;
            if(_revealed.Count>0 || _answerViewed) AssistedCount++; else UnassistedCorrectCount++;
            ErrorPositions = []; FailedInput = "";
            return Outcome = TypingOutcome.Correct;
        }
        return Outcome = TypingOutcome.Pending;
    }
    public bool Advance()
    {
        if (Outcome != TypingOutcome.Correct) return false;
        Cursor++; ClearAttempt();
        Outcome = Current == null ? TypingOutcome.Finished : TypingOutcome.Pending;
        if (Current == null) _clock.Stop();
        return true;
    }
    private void ClearAttempt() { Input = FailedInput = ""; ErrorPositions = []; _revealed.Clear(); _answerViewed=false; }
}
