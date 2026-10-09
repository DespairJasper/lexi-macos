namespace Lexi;

public sealed class ReviewLog
{
    public long Id { get; set; }
    public long WordId { get; set; }
    public string Action { get; set; } = "";
    public int? OldStage { get; set; }
    public int? NewStage { get; set; }
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? OldNextReviewDate { get; set; }
    public string? NewNextReviewDate { get; set; }
    public string? OldLearningStartDate { get; set; }
    public string? NewLearningStartDate { get; set; }
    public string LogTime { get; set; } = "";
    public string LogDate { get; set; } = "";
}

public sealed class AppSettings
{
    public string AiProtocol { get; set; } = "chat";
    public string LookupShortcut { get; set; } = "D";
    public string TranslateShortcut { get; set; } = "A";
    public string QuoteShortcut { get; set; } = "S";
    public string Provider { get; set; } = "deepseek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-chat";
    public string ApiKey { get; set; } = "";
    public bool RememberKey { get; set; } = false;
    public bool Clipboard { get; set; } = false;
    public int Timeout { get; set; } = 15;
    public string Theme { get; set; } = "Light";
    public string UiLanguage { get; set; } = "zh-CN";
    public string AiContext { get; set; } = "日常表达";
    public bool IncludeSourceInAi { get; set; } = false;
    public bool HighContrast { get; set; }
    public bool OpaqueMaterial { get; set; }
    public bool ReduceMotion { get; set; }
}

public sealed class LookupResult
{
    public string Word { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Definition { get; set; } = "";
    public string Pos { get; set; } = "";
    public bool Found { get; set; }
}

public sealed record ExampleItem(string English, string Chinese);

public sealed record PhraseItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("en")] string English,
    [property: System.Text.Json.Serialization.JsonPropertyName("zh")] string Chinese
);

public sealed class LlmResult
{
    public List<ExampleItem> Examples { get; set; } = new();
    public List<string> Synonyms { get; set; } = new();
    public List<string> Antonyms { get; set; } = new();
    public List<PhraseItem> Phrases { get; set; } = new();

    public bool HasContent => Examples.Count > 0 || Synonyms.Count > 0 || Antonyms.Count > 0 || Phrases.Count > 0;
}

