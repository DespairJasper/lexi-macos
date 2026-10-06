namespace Lexi;

/// <summary>背词模式：复习（到期词）或首次学习（新词，先学后测）。</summary>
public enum StudyMode
{
    Review,
    FirstLearn,
}

/// <summary>卡片步骤：学习卡（答案可见）或回忆卡（答案隐藏，需要评价）。</summary>
public enum StudyStep
{
    Learn,
    Recall,
}

/// <summary>回忆评价，对应不背单词的「认识 / 模糊 / 忘记了」。</summary>
public enum StudyRating
{
    Known,
    Unsure,
    Forgot,
}

/// <summary>一次评价的结果：是否因此出队、该词当前连击、本次目标连击。</summary>
public readonly record struct StudyCommitResult(bool Completed, int Streak, int Target);

/// <summary>
/// 一轮背词队列（不背单词连击口径）：
/// 首次学习每个词要连续攒够 3 次「认识」才算出队；复习时本轮第一次评价就是「认识」则一次即过，
/// 否则同样按 3 次计。中途「模糊」把连击减一（不低于 0），「忘记」直接清零重算。
/// 轮内按「遍」推进：每一遍包含全部未完成词各一次并整体打乱，同一个词不会连着出现好几遍。
/// 纯逻辑，不依赖 UI 与数据库。
/// </summary>
public sealed class StudyRound<T>
{
    /// <summary>连击目标：首次学习固定 3 次；复习首次评价不是认识时改为 3 次。</summary>
    public const int RequiredStreak = 3;

    private sealed class WordState
    {
        public int Streak;
        public bool FirstRated;
    }

    private readonly record struct Card(T Word, StudyStep Step, bool IsNew);

    private readonly Dictionary<T, WordState> _states = [];
    private readonly List<Card> _current = [];
    private readonly List<Card> _next = [];
    private readonly Random _random;
    private (Card Card, StudyRating Rating, int PrevStreak, bool PrevFirstRated)? _last;
    private T? _lastShown;
    private bool _hasLastShown;

    public StudyRound() : this(new Random()) { }

    public StudyRound(Random random) => _random = random ?? throw new ArgumentNullException(nameof(random));

    public StudyMode Mode { get; private set; } = StudyMode.Review;
    public int Total { get; private set; }
    public int Completed { get; private set; }
    public int Known { get; private set; }
    public int Unsure { get; private set; }
    public int Forgot { get; private set; }

    /// <summary>本轮尚未完全记住的词数（分母口径固定，不因重新排队变化）。</summary>
    public int Remaining => Total - Completed;

    public bool HasCurrent => _current.Count > 0;
    public bool IsFinished => Completed >= Total;
    public bool CanUndo => _last != null;

    public T Current => _current.Count > 0 ? _current[0].Word : throw new InvalidOperationException("本轮已完成。");
    public StudyStep CurrentStep => _current.Count > 0 ? _current[0].Step : throw new InvalidOperationException("本轮已完成。");

    /// <summary>当前词本轮的连续认识次数（右侧竖条点亮数）。</summary>
    public int CurrentStreak => _current.Count > 0 ? State(_current[0].Word).Streak : 0;

    /// <summary>当前词本轮完成所需的认识次数：多为 3，复习首次作答前为 1。</summary>
    public int CurrentTarget => _current.Count > 0 ? TargetFor(_current[0]) : RequiredStreak;

    public void Reset(IEnumerable<T> words, StudyMode mode)
    {
        ArgumentNullException.ThrowIfNull(words);
        Reset(words, _ => mode == StudyMode.FirstLearn, mode);
    }

    /// <summary>按每个词的新旧程度决定起始步骤：新词先学后测，已有档案的词直接回忆。</summary>
    public void Reset(IEnumerable<T> words, Func<T, bool> isFirstLearn)
    {
        ArgumentNullException.ThrowIfNull(isFirstLearn);
        Reset(words, isFirstLearn, StudyMode.Review);
    }

    private void Reset(IEnumerable<T> words, Func<T, bool> isFirstLearn, StudyMode mode)
    {
        ArgumentNullException.ThrowIfNull(words);
        _states.Clear();
        _current.Clear();
        _next.Clear();
        _last = null;
        _hasLastShown = false;
        _lastShown = default;
        Total = Completed = Known = Unsure = Forgot = 0;
        var cards = new List<Card>();
        foreach (var word in words)
        {
            var isNew = isFirstLearn(word);
            cards.Add(new Card(word, isNew ? StudyStep.Learn : StudyStep.Recall, isNew));
            _states[word] = new WordState();
            Total++;
        }
        Mode = cards.Any(card => card.IsNew) ? StudyMode.FirstLearn : mode;
        if (cards.Count == 0) return;
        // 保留第一个词（界面上正在看的那张卡），首遍其余顺序打乱。
        var rest = cards.Skip(1).ToList();
        Shuffle(rest);
        _current.Add(cards[0]);
        _current.AddRange(rest);
    }

    /// <summary>学习卡看完：该词延到下一遍进入回忆步骤。</summary>
    public void CompleteLearn()
    {
        if (_current.Count == 0 || _current[0].Step != StudyStep.Learn)
            throw new InvalidOperationException("当前不是学习卡。");
        var card = _current[0];
        _current.RemoveAt(0);
        _next.Add(card with { Step = StudyStep.Recall });
        _last = null;
        Showed(card.Word);
        PromoteIfNeeded();
    }

    /// <summary>提交一次回忆评价，累计连击、重新排入下一遍或直接出队。</summary>
    public StudyCommitResult Commit(StudyRating rating)
    {
        if (_current.Count == 0 || _current[0].Step != StudyStep.Recall)
            throw new InvalidOperationException("当前不是回忆卡。");
        var card = _current[0];
        var state = State(card.Word);
        var prevStreak = state.Streak;
        var prevFirstRated = state.FirstRated;
        var target = TargetFor(card);
        _current.RemoveAt(0);
        var completed = false;
        switch (rating)
        {
            case StudyRating.Known:
                Known++;
                state.Streak++;
                state.FirstRated = true;
                if (state.Streak >= target) { completed = true; Completed++; }
                else _next.Add(card);
                break;
            case StudyRating.Unsure:
                Unsure++;
                state.Streak = Math.Max(0, state.Streak - 1);
                state.FirstRated = true;
                _next.Add(card);
                break;
            default:
                Forgot++;
                state.Streak = 0;
                state.FirstRated = true;
                _next.Add(card with { Step = card.IsNew ? StudyStep.Learn : StudyStep.Recall });
                break;
        }
        _last = (card, rating, prevStreak, prevFirstRated);
        Showed(card.Word);
        PromoteIfNeeded();
        return new StudyCommitResult(completed, state.Streak, target);
    }

    /// <summary>跳过当前卡（例如直接标记为已掌握）：出队并计入本轮完成，仍可撤销。</summary>
    public StudyCommitResult CompleteCurrent()
    {
        if (_current.Count == 0) throw new InvalidOperationException("本轮已完成。");
        var card = _current[0];
        var state = State(card.Word);
        var prevStreak = state.Streak;
        var prevFirstRated = state.FirstRated;
        _current.RemoveAt(0);
        state.Streak = Math.Max(state.Streak, RequiredStreak);
        state.FirstRated = true;
        Completed++;
        Known++;
        _last = (card, StudyRating.Known, prevStreak, prevFirstRated);
        Showed(card.Word);
        PromoteIfNeeded();
        return new StudyCommitResult(true, state.Streak, RequiredStreak);
    }

    /// <summary>撤销上一次评价：恢复连击与统计，并把该词放回队首原步骤。</summary>
    public (T Word, StudyRating Rating, StudyStep Step)? UndoLast()
    {
        if (_last is not { } last) return null;
        // 评价后词被延到下一遍（或已出队）：撤销时先摘掉那个副本。
        RemoveDeferred(last.Card.Word, DeferredStep(last));
        var state = State(last.Card.Word);
        state.Streak = last.PrevStreak;
        state.FirstRated = last.PrevFirstRated;
        switch (last.Rating)
        {
            case StudyRating.Known: Completed--; Known--; break;
            case StudyRating.Unsure: Unsure--; break;
            default: Forgot--; break;
        }
        _current.Insert(0, last.Card);
        _last = null;
        return (last.Card.Word, last.Rating, last.Card.Step);
    }

    private static StudyStep DeferredStep((Card Card, StudyRating Rating, int PrevStreak, bool PrevFirstRated) last) =>
        last.Rating == StudyRating.Forgot && last.Card.IsNew ? StudyStep.Learn : last.Card.Step;

    private int TargetFor(Card card)
    {
        if (card.IsNew) return RequiredStreak;
        return State(card.Word).FirstRated ? RequiredStreak : 1;
    }

    private WordState State(T word)
    {
        if (!_states.TryGetValue(word, out var state))
        {
            state = new WordState();
            _states[word] = state;
        }
        return state;
    }

    private void Showed(T word)
    {
        _lastShown = word;
        _hasLastShown = true;
    }

    /// <summary>本遍走完后把下一遍的卡整体重新洗牌（衔接处避开同一个词）。</summary>
    private void PromoteIfNeeded()
    {
        if (_current.Count > 0 || _next.Count == 0) return;
        var pass = new List<Card>(_next);
        _next.Clear();
        Shuffle(pass);
        if (pass.Count > 1 && _hasLastShown)
        {
            var comparer = EqualityComparer<T>.Default;
            if (comparer.Equals(pass[0].Word, _lastShown))
            {
                for (var i = 1; i < pass.Count; i++)
                {
                    if (comparer.Equals(pass[i].Word, _lastShown)) continue;
                    (pass[0], pass[i]) = (pass[i], pass[0]);
                    break;
                }
            }
        }
        _current.AddRange(pass);
    }

    private void RemoveDeferred(T word, StudyStep step)
    {
        var comparer = EqualityComparer<T>.Default;
        var index = _next.FindIndex(card => card.Step == step && comparer.Equals(card.Word, word));
        if (index >= 0) { _next.RemoveAt(index); return; }
        index = _current.FindIndex(card => card.Step == step && comparer.Equals(card.Word, word));
        if (index >= 0) _current.RemoveAt(index);
    }

    private void Shuffle(List<Card> cards)
    {
        for (var i = cards.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
    }
}
