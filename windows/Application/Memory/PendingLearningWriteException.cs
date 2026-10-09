namespace Lexi;

/// <summary>The SQLite fact is committed; JSON must be replayed before another rating.</summary>
public sealed class PendingLearningWriteException : IOException
{
    public PendingLearningWriteException(string wordId, StudyCommitResult result, Exception error)
        : base("Learning progress is committed and awaiting durable projection.", error)
    { WordId = wordId; Result = result; }
    public string WordId { get; }
    public StudyCommitResult Result { get; }
}
