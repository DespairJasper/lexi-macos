namespace Lexi;

public sealed record QuoteItem(long Id, string Original, string Translation, string Source, string Notes,
    string CreatedAtUtc, string UpdatedAtUtc)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AlreadyExists { get; init; }
}
