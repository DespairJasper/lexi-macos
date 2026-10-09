using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lexi;

public partial class MainWindow
{
    private readonly Dictionary<long, (WordKey Key, WordItem Word)> _sourceReviewWords = [];
    private WordKey ReviewKey(WordItem word) => word.Id < 0
        && _sourceReviewWords.TryGetValue(word.Id, out var entry)
        ? entry.Key : WordKeyResolver.FromArchive(word);
    private WordItem? ResolveReviewWord(long id) => id >= 0
        ? _allWords.FirstOrDefault(w => w.Id == id)
        : _sourceReviewWords.GetValueOrDefault(id).Word;

    private List<WordItem> SourceDueWords()
    {
        var pending = _activeLearningPlan is { } plan && _dailyLearningSession is { Round.IsFinished: false }
            ? plan.Words.Where(w => !plan.CompletedWordIds.Contains(w.Id))
                .Select(w => WordKeyResolver.ResolvePlanWord(plan, w,
                    id => _allWords.FirstOrDefault(a => a.Id.ToString() == id)).Key)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WordItem>();
        var due = Memory().QueryDue(DateTime.UtcNow, int.MaxValue).Select(c => c.WordKey).ToHashSet(StringComparer.Ordinal);
        var store = (VocabularyService)_learningMemoryStore!;
        foreach (var card in store.GetMemoryCards().Where(c => !c.WordKey.StartsWith(WordKey.ArchivePrefix, StringComparison.Ordinal)))
        {
            var key = WordKey.Parse(card.WordKey);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(card.WordKey));
            var id = -(BinaryPrimitives.ReadInt64BigEndian(hash) & long.MaxValue | 1L);
            if (_sourceReviewWords.TryGetValue(id, out var existing) && existing.Key != key)
                throw new InvalidDataException("教材复习身份冲突，已暂停装填。");
            _ieltsCatalog ??= IeltsCatalog.Load();
            var catalog = key.Source == WordSource.Ielts
                ? _ieltsCatalog.AllWords.FirstOrDefault(w => w.Id == key.SourceId) : null;
            var details = existing.Word == null ? store.LoadSourceWordDetails(key.Key) : null;
            var lookup = existing.Word == null && catalog == null && details == null && _dictService is DictionaryService dictionary
                ? dictionary.Lookup(key.SourceId) : null;
            var word = existing.Word ?? new WordItem
            {
                Id = id, Word = catalog?.Word ?? details?[0] ?? key.SourceId,
                Phonetic = catalog?.Phonetic ?? details?[1] ?? lookup?.Phonetic ?? "",
                Translation = catalog?.Meaning ?? details?[2] ?? lookup?.Translation ?? "",
                Definition = catalog?.Example ?? details?[3] ?? lookup?.Definition ?? "", Status = "learning"
            };
            word.NextReviewDate = card.NextReviewAtUtc?.ToLocalTime().ToString("yyyy-MM-dd");
            _sourceReviewWords[id] = (key, word);
            if (due.Contains(card.WordKey) && !pending.Contains(card.WordKey)) result.Add(word);
        }
        return result;
    }
}
