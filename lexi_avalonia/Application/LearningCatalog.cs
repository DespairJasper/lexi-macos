using System.Text.Json;

namespace Lexi;

public sealed class LearningWord
{
    public string Id { get; set; } = "";
    public List<string> Words { get; set; } = [];
    public string Word => Words.FirstOrDefault() ?? "";
    public string Pos { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public string Example { get; set; } = "";
    public string Extra { get; set; } = "";
    public int Group { get; set; }
    public string AudioPath { get; set; } = "";
    public List<string> Synonyms { get; set; } = [];
}

public sealed class LearningSection
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public List<LearningWord> Entries { get; set; } = [];
    public override string ToString() => Title;
}

public sealed class WritingSentence
{
    public int Number { get; set; }
    public string Category { get; set; } = "";
    public string Chinese { get; set; } = "";
    public string BookAnswer { get; set; } = "";
    public string AlternateAnswer { get; set; } = "";
    public string Remark { get; set; } = "";
}

public sealed class IeltsCatalog
{
    public string Source { get; set; } = "";
    public string Commit { get; set; } = "";
    public string GrammarVideo { get; set; } = "";
    public List<LearningSection> Sections { get; set; } = [];
    public List<WritingSentence> Sentences { get; set; } = [];
    private Dictionary<string, LearningWord>? _index;
    public IEnumerable<LearningWord> AllWords => Sections.SelectMany(s => s.Entries);
    public static string AssetDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "IELTS");
    public static IeltsCatalog Load(string? folder = null)
    {
        var path = Path.Combine(folder ?? AssetDirectory, "catalog.json");
        var catalog = JsonSerializer.Deserialize<IeltsCatalog>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("IELTS 目录为空。");
        if (catalog.Sections.Count == 0 || catalog.AllWords.Any(w => w.Words.Count == 0 || w.Words.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidDataException("IELTS 词条结构无效。");
        return catalog;
    }
    public LearningWord? Find(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        _index ??= AllWords.SelectMany(w => w.Words.Select(form => (form, w)))
            .GroupBy(pair => pair.form, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().w, StringComparer.OrdinalIgnoreCase);
        return _index.GetValueOrDefault(word.Trim());
    }
    public static string? ResolveAsset(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var root = Path.GetFullPath(AssetDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }
}
