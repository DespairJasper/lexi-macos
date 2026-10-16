using System.Text.Json;

namespace Lexi;

public sealed class ExamBook
{
    public string TopicId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public string BookId { get; set; } = "";
    public List<LearningSection> Sections { get; set; } = [];
    public IEnumerable<LearningWord> AllWords => Sections.SelectMany(s => s.Entries);
}

public sealed class ExamCatalog
{
    public string Source { get; set; } = "";
    public string Commit { get; set; } = "";
    public List<ExamBook> Books { get; set; } = [];

    public static ExamCatalog Load(string? folder = null)
    {
        var path = Path.Combine(folder ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Exams"), "catalog.json");
        var catalog = JsonSerializer.Deserialize<ExamCatalog>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty exam vocabulary catalog.");
        var words = catalog.Books.SelectMany(b => b.AllWords).ToList();
        var topics = new[] { "cet4", "cet6", "kaoyan", "gmat", "toefl", "gre" };
        if (catalog.Books.Count != 20 || !catalog.Books.Take(6).Select(b => b.Id).SequenceEqual(topics)
            || catalog.Books.Any(b => !topics.Contains(b.TopicId))
            || catalog.Books.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Books.Count
            || catalog.Books.Select(b => b.BookId).Distinct(StringComparer.Ordinal).Count() != catalog.Books.Count
            || catalog.Books.Any(b => b.Sections.Count == 0 || b.AllWords.Count() == 0 || string.IsNullOrWhiteSpace(b.EnglishTitle))
            || words.Any(w => !w.Id.StartsWith("exam:", StringComparison.Ordinal) || w.Words.Count != 1
                || string.IsNullOrWhiteSpace(w.Word) || string.IsNullOrWhiteSpace(w.Meaning))
            || words.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != words.Count)
            throw new InvalidDataException("Invalid exam vocabulary catalog.");
        return catalog;
    }
}
