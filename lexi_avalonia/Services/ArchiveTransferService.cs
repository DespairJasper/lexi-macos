using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lexi.Core;

namespace Lexi;

/// <summary>Explicit portable archive schema: never serializes settings or the UI model directly.</summary>
public static partial class ArchiveTransferService
{
    public const int MaximumJsonBytes = 50 * 1024 * 1024;
    public const int MaximumEntries = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);
    private static string Quote(string? text)
    {
        text ??= "";
        var significant = text.AsSpan().TrimStart();
        if (text.StartsWith("'") || (!significant.IsEmpty && "=+-@".Contains(significant[0])) || (text.Length > 0 && text[0] is '\t' or '\r' or '\n')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public static string GenerateJson(IEnumerable<WordItem> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var entries = words.Take(MaximumEntries + 1).Select(ToRecord).ToList();
        var envelope = new Envelope { FormatVersion = 1, ExportedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), Entries = entries };
        Validate(envelope);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw Invalid("档案超过 50 MB 限制。");
        return json;
    }

    public static List<WordItem> ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumJsonBytes || Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw Invalid("档案超过 50 MB 限制。");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement);
            var envelope = document.RootElement.Deserialize<Envelope>(JsonOptions) ?? throw Invalid("档案为空。");
            Validate(envelope);
            return envelope.Entries.Select(FromRecord).ToList();
        }
        catch (JsonException ex) { throw new FormatException("档案 JSON 格式无效或缺少必填字段。", ex); }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("档案含重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static FormatException Invalid(string message) => new(message);
    private static void Text(string? value, int maximum, bool required = false)
    {
        if (value == null || value.Length > maximum || value.Contains('\0') || (required && string.IsNullOrWhiteSpace(value))) throw Invalid("档案文本字段无效或过长。");
    }

    private static void Utc(string? value)
    {
        Text(value, 40, true);
        if (!(value!.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal))
            || !DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero) throw Invalid("档案时间必须为 UTC。");
    }

    private static void Date(string? value, bool optional = false, bool timestamp = false)
    {
        if (value == null && optional) return;
        Text(value, 40, true);
        var formats = timestamp ? new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd" } : ["yyyy-MM-dd"];
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid("复习日期格式无效。");
    }

    private static void Validate(Envelope archive)
    {
        if (archive.FormatVersion != 1 || archive.Entries == null || archive.Entries.Count > MaximumEntries) throw Invalid("档案版本或词条数量不受支持。");
        Utc(archive.ExportedAtUtc);
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uuids = new HashSet<Guid>();
        foreach (var entry in archive.Entries)
        {
            if (entry == null || entry.Archive == null) throw Invalid("档案包含空词条。");
            Text(entry.Word, 512, true);
            if (!words.Add(entry.Word.Trim().Normalize(NormalizationForm.FormC))) throw Invalid("档案包含重复单词。");
            Text(entry.Phonetic, 4096); Text(entry.Translation, 100_000); Text(entry.Definition, 100_000); Text(entry.Notes, 100_000);
            if (entry.Id < 0 || entry.Stage is < 0 or > 5 || entry.Status is not ("learning" or "mastered")
                || entry.ReviewCount < 0 || (entry.Status == "mastered" && (entry.Stage != 5 || entry.NextReviewDate != null))
                || (entry.Status == "learning" && entry.Stage == 5)) throw Invalid("复习进度无效。");
            Date(entry.CreatedAt, timestamp: true); Date(entry.LearningStartDate); Date(entry.NextReviewDate, optional: true); Date(entry.LastReviewedAt, optional: true, timestamp: true);
            var a = entry.Archive;
            if (!Guid.TryParseExact(a.Uuid, "D", out var uuid) || uuid == Guid.Empty || !uuids.Add(uuid)) throw Invalid("档案标识无效或重复。");
            Text(a.SourceType, 256); Text(a.SourceTitle, 4096); Text(a.SourceExcerpt, 100_000);
            if (a.EncounterCount < 1 || a.Revision < 1 || a.Tags == null || a.Tags.Length > 100) throw Invalid("档案次数、版本或标签无效。");
            foreach (var tag in a.Tags) Text(tag, 256, true);
            Utc(a.CreatedAtUtc); Utc(a.UpdatedAtUtc); Utc(a.LastEncounteredAtUtc);
            if (entry.AiResult is { } ai)
            {
                if (ai.Examples == null || ai.Synonyms == null || ai.Antonyms == null || ai.Phrases == null
                    || ai.Examples.Count > 1000 || ai.Synonyms.Count > 1000 || ai.Antonyms.Count > 1000 || ai.Phrases.Count > 1000) throw Invalid("AI 档案列表无效。");
                foreach (var e in ai.Examples) { if (e == null) throw Invalid("AI 例句为空。"); Text(e.English, 100_000); Text(e.Chinese, 100_000); }
                foreach (var e in ai.Phrases) { if (e == null) throw Invalid("AI 词组为空。"); Text(e.English, 100_000); Text(e.Chinese, 100_000); }
                foreach (var e in ai.Synonyms.Concat(ai.Antonyms)) Text(e, 4096);
            }
        }
    }

    private static Entry ToRecord(WordItem word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var a = word.Archive ?? throw Invalid("缺少档案元数据。");
        return new Entry
        {
            Id = word.Id, Word = word.Word, Phonetic = word.Phonetic, Translation = word.Translation, Definition = word.Definition,
            Notes = word.Notes, Stage = word.Stage, Status = word.Status, CreatedAt = word.CreatedAt, LearningStartDate = word.LearningStartDate,
            NextReviewDate = word.NextReviewDate, LastReviewedAt = word.LastReviewedAt, ReviewCount = word.ReviewCount,
            Archive = new Metadata { Uuid = a.Uuid, SourceType = a.SourceType, SourceTitle = a.SourceTitle, SourceExcerpt = a.SourceExcerpt,
                Tags = a.Tags, EncounterCount = a.EncounterCount, Revision = a.Revision, CreatedAtUtc = a.CreatedAtUtc, UpdatedAtUtc = a.UpdatedAtUtc, LastEncounteredAtUtc = a.LastEncounteredAtUtc },
            AiResult = word.AiResult is not { } ai ? null : new AiRecord { Examples = ai.Examples, Synonyms = ai.Synonyms, Antonyms = ai.Antonyms, Phrases = ai.Phrases }
        };
    }

    private static WordItem FromRecord(Entry e) => new()
    {
        Id = e.Id, Word = e.Word, Phonetic = e.Phonetic, Translation = e.Translation, Definition = e.Definition, Notes = e.Notes,
        Stage = e.Stage, Status = e.Status, CreatedAt = e.CreatedAt, LearningStartDate = e.LearningStartDate, NextReviewDate = e.NextReviewDate,
        LastReviewedAt = e.LastReviewedAt, ReviewCount = e.ReviewCount,
        Archive = new ArchiveMetadata { Uuid = e.Archive.Uuid, SourceType = e.Archive.SourceType, SourceTitle = e.Archive.SourceTitle,
            SourceExcerpt = e.Archive.SourceExcerpt, Tags = e.Archive.Tags, EncounterCount = e.Archive.EncounterCount, Revision = e.Archive.Revision,
            CreatedAtUtc = e.Archive.CreatedAtUtc, UpdatedAtUtc = e.Archive.UpdatedAtUtc, LastEncounteredAtUtc = e.Archive.LastEncounteredAtUtc },
        AiResult = e.AiResult is not { } ai ? null : new LlmResult { Examples = ai.Examples, Synonyms = ai.Synonyms, Antonyms = ai.Antonyms, Phrases = ai.Phrases }
    };

    private sealed class Envelope
    {
        public required int FormatVersion { get; set; }
        public required string ExportedAtUtc { get; set; }
        public required List<Entry> Entries { get; set; }
    }
    private sealed class Entry
    {
        public required long Id { get; set; }
        public required string Word { get; set; }
        public required string Phonetic { get; set; }
        public required string Translation { get; set; }
        public required string Definition { get; set; }
        public required string Notes { get; set; }
        public required int Stage { get; set; }
        public required string Status { get; set; }
        public required string CreatedAt { get; set; }
        public required string LearningStartDate { get; set; }
        public required string? NextReviewDate { get; set; }
        public required string? LastReviewedAt { get; set; }
        public required int ReviewCount { get; set; }
        public required Metadata Archive { get; set; }
        public required AiRecord? AiResult { get; set; }
    }
    private sealed class Metadata
    {
        public required string Uuid { get; set; }
        public required string SourceType { get; set; }
        public required string SourceTitle { get; set; }
        public required string SourceExcerpt { get; set; }
        public required string[] Tags { get; set; }
        public required int EncounterCount { get; set; }
        public required int Revision { get; set; }
        public required string CreatedAtUtc { get; set; }
        public required string UpdatedAtUtc { get; set; }
        public required string LastEncounteredAtUtc { get; set; }
    }
    private sealed class AiRecord
    {
        public required List<ExampleItem> Examples { get; set; }
        public required List<string> Synonyms { get; set; }
        public required List<string> Antonyms { get; set; }
        public required List<PhraseItem> Phrases { get; set; }
    }
}
