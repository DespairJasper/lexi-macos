namespace Lexi;

public static class LearningRound
{
    public static List<LearningWord> Select(IEnumerable<LearningWord> words, int count, bool all, bool random, int seed)
    {
        var deck = words.SelectMany(w => w.Words.Where(f => !string.IsNullOrWhiteSpace(f)).Select(form => new LearningWord
        {
            Id = w.Id, Words = [form], Meaning = w.Meaning, Pos = w.Pos, Phonetic = w.Phonetic,
            Example = w.Example, Extra = w.Extra, Group = w.Group, Synonyms = [.. w.Synonyms],
            AudioPath = form == w.Word ? w.AudioPath : ""
        })).ToList();
        if (random) new Random(seed).Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(deck));
        return deck.Take(all ? deck.Count : Math.Clamp(count, 1, Math.Max(1, deck.Count))).ToList();
    }
}
