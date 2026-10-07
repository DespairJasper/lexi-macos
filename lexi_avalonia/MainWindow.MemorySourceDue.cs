using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lexi;

public partial class MainWindow
{
    // Source cards remain in memory: entering review never adds a vocabulary/archive row.
    private readonly Dictionary<long, WordKey> _memorySourceReviewKeys = new();
    private readonly Dictionary<string, WordItem> _memorySourceReviewWords = new(StringComparer.Ordinal);
    internal Func<DateTime> MemorySourceDueClock { get; set; } = () => DateTime.UtcNow;

    private List<WordItem> MemorySourceDueWords()
    {
        var memory = Memory;
        if (memory is null || _memoryStore is null) return [];
        try
        {
            var pending = MemoryUnfinishedSourceKeys();
            return memory.QueryDue(MemorySourceDueClock(), int.MaxValue)
                .Where(card => !card.WordKey.StartsWith(WordKey.ArchivePrefix, StringComparison.Ordinal)
                    && !pending.Contains(card.WordKey))
                .Select(card => MemorySourceReviewWord(WordKey.Parse(card.WordKey), card.NextReviewAtUtc))
                .ToList();
        }
        catch (Exception ex)
        {
            MarkMemoryDisabled("教材/词形复习队列查询失败：" + ex.Message + "（已暂停装填）");
            return [];
        }
    }

    private bool MemoryTrySourceReviewKey(WordItem? word, out WordKey key)
    {
        key = default;
        return word is not null && word.Id < 0 && _memorySourceReviewKeys.TryGetValue(word.Id, out key);
    }

    private static long MemorySourceReviewId(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        // Stable across process/restart/platform; positive IDs remain reserved for the archive.
        return -(BinaryPrimitives.ReadInt64BigEndian(bytes) & long.MaxValue | 1L);
    }

    private WordItem MemorySourceReviewWord(WordKey key, DateTime dueAtUtc)
    {
        if (key.Source == WordSource.Archive) throw new InvalidDataException("档案词不能转换为教材临时卡。");
        var id = MemorySourceReviewId(key.Key);
        if (_memorySourceReviewKeys.TryGetValue(id, out var previous) && previous != key)
            throw new InvalidDataException("复习来源标识冲突；已停止装填，未合并不同来源。");
        _memorySourceReviewKeys[id] = key;

        // 复用同一实例：到期队列每次装填都会重建列表，反复 new 会让轮内/撤销持有不同的对象。
        if (_memorySourceReviewWords.TryGetValue(key.Key, out var cached))
        {
            cached.NextReviewDate = dueAtUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return cached;
        }

        var catalog = key.Source == WordSource.Ielts
            ? _ieltsCatalog?.AllWords.FirstOrDefault(w => string.Equals(w.Id, key.SourceId, StringComparison.Ordinal))
            : null;
        var form = catalog?.Word ?? key.SourceId;
        var archive = key.Source == WordSource.Form
            ? _allWords.FirstOrDefault(w => string.Equals(WordKeyResolver.FormC(w.Word), key.SourceId, StringComparison.Ordinal))
            : null;
        var item = new WordItem
        {
            Id = id,
            Word = form,
            Phonetic = catalog?.Phonetic ?? archive?.Phonetic ?? "",
            Translation = catalog?.Meaning ?? archive?.Translation ?? "",
            Definition = catalog is null ? archive?.Definition ?? "" : catalog.Example,
            Notes = catalog?.Extra ?? "",
            Status = "learning",
            NextReviewDate = dueAtUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // Keep Archive.Uuid empty: source identity must never be inferred as an archive key.
        };
        _memorySourceReviewWords[key.Key] = item;
        return item;
    }

    private WordItem? MemoryResolveReviewIdentity(string identity)
    {
        if (!long.TryParse(identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        if (id >= 0) return _allWords.FirstOrDefault(w => w.Id == id);
        if (_memorySourceReviewKeys.TryGetValue(id, out var cached)
            && _memorySourceReviewWords.TryGetValue(cached.Key, out var existing)) return existing;

        // A resumed queue may contain a card which has since moved into the future. Rebuild
        // identity from all stored card keys, not only the currently due subset.
        if (Memory is null || _memoryStore is not VocabularyService store) return null;
        var matches = store.GetMemoryCardKeys()
            .Where(key => !key.StartsWith(WordKey.ArchivePrefix, StringComparison.Ordinal)
                && MemorySourceReviewId(key) == id).ToList();
        if (matches.Count > 1) throw new InvalidDataException("恢复复习队列时来源标识冲突。");
        if (matches.Count == 0) return null;
        var key = WordKey.Parse(matches[0]);
        var card = store.GetCard(key.Key);
        return MemorySourceReviewWord(key, card?.NextReviewAtUtc ?? MemorySourceDueClock());
    }

    private HashSet<string> MemoryUnfinishedSourceKeys()
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in new[] { "focus", "plan" })
        {
            // 这个方法现在在每次装填到期队列时都会跑。一个陈旧的 checkpoint（例如计划已被删除）
            // 只应该让"这个表面暂时算不出未完成词"，绝不能把整个长期记忆层降级掉。
            try { MemoryCollectUnfinishedSourceKeys(surface, pending); }
            catch (Exception ex) { MemoryDiagnostic("未完成学习队列解析失败（" + surface + "）：" + ex.Message + "；本次只跳过该表面。"); }
        }
        return pending;
    }

    private void MemoryCollectUnfinishedSourceKeys(string surface, HashSet<string> pending)
    {
        var checkpoint = _memoryStore!.GetSessionCheckpoint(surface);
            if (checkpoint is null) return;
            var session = _memoryStore.GetSession(checkpoint.SessionId);
            if (session is null || session.EndedAtUtc is not null) return;
            var saved = JsonSerializer.Deserialize<SavedMemoryRound>(checkpoint.QueueJson)
                ?? throw new InvalidDataException("未完成学习队列为空。");
            if (saved.Finished) return;
            foreach (var card in saved.Cards) pending.Add(card.WordKey);
            foreach (var e in _memoryStore.LoadEvents(checkpoint.SessionId)) pending.Add(e.WordKey);

            // Include cards not presented yet: their identities live in the durable queue.
            using var round = JsonDocument.Parse(saved.RoundJson);
            foreach (var queue in new[] { "Current", "Next" })
            {
                if (!round.RootElement.TryGetProperty(queue, out var cards))
                    throw new InvalidDataException("未完成学习队列缺少待学卡片。");
                foreach (var card in cards.EnumerateArray())
                {
                    var wordId = card.GetProperty("WordId").GetString()
                        ?? throw new InvalidDataException("未完成学习词标识为空。");
                    if (surface == "plan")
                    {
                        var plan = _studyPlans.FirstOrDefault(p => p.Id == session.PlanId)
                            ?? throw new InvalidDataException("未完成学习计划已不存在。");
                        var word = plan.Words.FirstOrDefault(w => w.Id == wordId)
                            ?? throw new InvalidDataException("未完成计划词已不存在。");
                        pending.Add(WordKeyResolver.ResolvePlanWord(plan, word, archiveId =>
                            _allWords.FirstOrDefault(w => w.Id.ToString(CultureInfo.InvariantCulture) == archiveId)).Key);
                    }
                    else if (session.PrimarySource == WordSource.Form)
                        pending.Add(WordKeyResolver.FromForm(wordId).Key);
                    else
                        pending.Add(WordKeyResolver.Resolve(WordKeyResolver.FormC(wordId),
                            session.PrimarySource == WordSource.Ielts ? WordKeyResolver.IeltsPageKind : null,
                            form => _allWords.FirstOrDefault(w => WordKeyResolver.FormC(w.Word) == form),
                            form => _ieltsCatalog?.Find(form)).Key);
                }
            }
    }
}
