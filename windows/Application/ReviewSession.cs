namespace Lexi;

/// <summary>Recall queue. Reveal is explicit; host persists a review before advancing.</summary>
public sealed class ReviewSession
{
    private Queue<WordItem> _remaining = new();
    public WordItem? Current => _remaining.TryPeek(out var word) ? word : null;
    public int Remaining => _remaining.Count;
    public bool IsRevealed { get; private set; }

    public void Reset(IEnumerable<WordItem> due)
    {
        ArgumentNullException.ThrowIfNull(due);
        _remaining = new Queue<WordItem>(due);
        IsRevealed = false;
    }

    public void Reveal()
    {
        if (Current is not null) IsRevealed = true;
    }

    public void CompleteCurrent()
    {
        if (!IsRevealed || Current is null)
            throw new InvalidOperationException("先揭示释义并保存复习结果，再进入下一张词卡。");
        _remaining.Dequeue();
        IsRevealed = false;
    }
}
