using System.Text;
using System.Text.Json.Nodes;
using Lexi.Core;

namespace Lexi;

/// <summary>Fixtures contain only invented data and use unique temporary directories.</summary>
public static class FoundationTransferTests
{
    public static void Run()
    {
        var fixtureFolder = Environment.GetEnvironmentVariable("LEXI_CSV_FIXTURES");
        if (!string.IsNullOrWhiteSpace(fixtureFolder)) {
            Directory.CreateDirectory(fixtureFolder);
            File.WriteAllText(Path.Combine(fixtureFolder,"windows-legacy.csv"),GenerateLegacyCsv([Fixture()]),new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(fixtureFolder,"windows-current.csv"),ArchiveTransferService.GenerateCsv([Fixture()]),new UTF8Encoding(true));
        }
        CsvRoundTrip();
        ImportTransaction();
        RoundTrip();
        RejectInvalidArchives();
        CsvSafety();
        SecretStorage();
        Console.WriteLine("PASS: foundation transfer (portable JSON, validation, CSV quoting/formula safety, isolated DPAPI storage)");
    }

    private static WordItem Fixture() => new()
    {
        Id = 19, Word = "résumé", Phonetic = "ˈrezəmeɪ", Translation = "简历，履历", Definition = "a summary",
        Notes = "line one\n\"line two\"", Stage = 2, Status = "learning", CreatedAt = "2026-09-18 12:30:00",
        LearningStartDate = "2026-09-18", NextReviewDate = "2026-09-22", LastReviewedAt = "2026-09-19 08:30:00",
        ReviewCount = 7, Selected = true, IsExpanded = true, AiStatusText = "private-ui-marker", OptAntonyms = true,
        Archive = new ArchiveMetadata
        {
            Uuid = "7b0a67a6-577c-4913-bb8d-7e18288d24df", SourceType = "book", SourceTitle = "A \"book\"",
            SourceExcerpt = "The résumé arrived.\n第二行", Tags = ["工作", "writing"], EncounterCount = 3, Revision = 2,
            CreatedAtUtc = "2026-09-18T04:30:00.0000000Z", UpdatedAtUtc = "2026-09-19T00:30:00.0000000Z",
            LastEncounteredAtUtc = "2026-09-18T05:30:00.0000000Z"
        },
        AiResult = new LlmResult { Examples = [new("Send a résumé.", "发送简历。")], Synonyms = ["CV"],
            Antonyms = ["unrelated"], Phrases = [new("submit a résumé", "提交简历")] }
    };

    // Removing any archived field or accidentally serializing the UI model breaks these assertions.
    private static void RoundTrip()
    {
        var json = ArchiveTransferService.GenerateJson([Fixture()]);
        Check(!json.Contains("private-ui-marker") && !json.Contains("selected", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("apiKey", StringComparison.OrdinalIgnoreCase) && !json.Contains("settings", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("optAntonyms", StringComparison.OrdinalIgnoreCase), "export exposed UI state or credential/settings fields");
        var word = ArchiveTransferService.ParseJson(json).Single();
        Check(word.Id == 19 && word.Word == "résumé" && word.Phonetic == "ˈrezəmeɪ" && word.Translation == "简历，履历"
            && word.Definition == "a summary" && word.Notes == "line one\n\"line two\"", "dictionary text roundtrip lost content");
        Check(word.Stage == 2 && word.Status == "learning" && word.CreatedAt == "2026-09-18 12:30:00"
            && word.LearningStartDate == "2026-09-18" && word.NextReviewDate == "2026-09-22"
            && word.LastReviewedAt == "2026-09-19 08:30:00" && word.ReviewCount == 7, "review progress roundtrip lost content");
        Check(word.Archive != null && word.Archive.Uuid == "7b0a67a6-577c-4913-bb8d-7e18288d24df" && word.Archive.SourceType == "book"
            && word.Archive.SourceTitle == "A \"book\"" && word.Archive.SourceExcerpt == "The résumé arrived.\n第二行"
            && word.Archive.Tags.SequenceEqual(new[] { "工作", "writing" }) && word.Archive.EncounterCount == 3
            && word.Archive.Revision == 2 && word.Archive.CreatedAtUtc == "2026-09-18T04:30:00.0000000Z"
            && word.Archive.UpdatedAtUtc == "2026-09-19T00:30:00.0000000Z"
            && word.Archive.LastEncounteredAtUtc == "2026-09-18T05:30:00.0000000Z", "personal metadata roundtrip lost content");
        Check(word.AiResult?.Examples.Single().Chinese == "发送简历。" && word.AiResult.Synonyms.Single() == "CV"
            && word.AiResult.Antonyms.Single() == "unrelated" && word.AiResult.Phrases.Single().English == "submit a résumé",
            "AI archive roundtrip lost content");
        Check(!word.Selected && !word.IsExpanded && word.AiStatusText == "", "import restored ephemeral UI state");
    }

    private static void RejectInvalidArchives()
    {
        void Invalid(Action<JsonObject> mutate)
        {
            var doc = JsonNode.Parse(ArchiveTransferService.GenerateJson([Fixture()]))!.AsObject();
            mutate(doc);
            Throws<FormatException>(() => ArchiveTransferService.ParseJson(doc.ToJsonString()), "invalid archive was accepted");
        }
        Invalid(d => d["formatVersion"] = 99);
        Invalid(d => d["entries"]![0]!["word"] = "  ");
        Invalid(d => d["entries"]![0]!["stage"] = 6);
        Invalid(d => d["entries"]![0]!["status"] = "other");
        Invalid(d => d["entries"]![0]!["reviewCount"] = -1);
        Invalid(d => d["entries"]![0]!["nextReviewDate"] = "2026-02-30");
        Invalid(d => d["entries"]![0]!["archive"]!["uuid"] = "invalid");
        Invalid(d => d["entries"]![0]!["archive"]!["createdAtUtc"] = "2026-09-18T04:30:00+08:00");
        Invalid(d => d["entries"]![0]!["archive"]!["tags"] = null);
        Invalid(d => d["entries"]![0]!["aiResult"]!["examples"] = null);
        Invalid(d => d["entries"]![0]!["aiResult"]!["synonyms"] = new JsonArray((JsonNode?)null));
        Invalid(d => d["entries"]![0] = null);
        Invalid(d => d["entries"]!.AsArray().Add(d["entries"]![0]!.DeepClone()));
        Invalid(d => d["apiKey"] = "must-not-be-imported");
        Invalid(d => d["entries"]![0]!.AsObject().Remove("notes"));
        Throws<FormatException>(() => ArchiveTransferService.ParseJson("{\"formatVersion\":1,\"formatVersion\":1}"), "duplicate JSON fields accepted");
        Throws<FormatException>(() => ArchiveTransferService.ParseJson(new string(' ', 50 * 1024 * 1024 + 1)), "oversized JSON accepted");
    }

    private static void CsvSafety()
    {
        var word = Fixture();
        word.Notes = " \t=HYPERLINK(\"bad\")\r\nnext,line";
        var csv = ArchiveTransferService.GenerateCsv([word]);
        Check(csv.StartsWith("\"单词\",", StringComparison.Ordinal), "CSV has no Chinese header");
        Check(csv.Contains("\"' \t=HYPERLINK(\"\"bad\"\")\r\nnext,line\"", StringComparison.Ordinal), "formula injection or multiline quoting is unsafe");
        foreach (var dangerous in new[] { "+cmd", "-cmd", "@SUM(1)", "\t=cmd", "\r=cmd", "\n=cmd", "\u2003=cmd" })
        {
            word.Notes = dangerous;
            Check(ArchiveTransferService.GenerateCsv([word]).Contains("\"'" + dangerous + "\"", StringComparison.Ordinal), "formula prefix was not neutralized");
        }
    }

    private static void CsvRoundTrip()
    {
        var fixture = Fixture();
        fixture.Id = 0;
        Check(!PrintDocumentFormatter.GenerateHtml([fixture]).Contains("a summary"), "print table contains English definition");
        foreach (var csv in new[]{ArchiveTransferService.GenerateCsv([fixture]),GenerateLegacyCsv([fixture])}) {
            var copy=ArchiveTransferService.ParseCsv("\uFEFF"+csv).Single();
            Check(copy.Archive.Uuid==fixture.Archive.Uuid && copy.Archive.Tags.SequenceEqual(fixture.Archive.Tags) && copy.AiResult!.Phrases[0].Chinese=="提交简历" && copy.ReviewCount==7 && copy.Notes==fixture.Notes,"cross-platform CSV content lost");
        }
        fixture.Notes="'=literal";
        fixture.Archive.Tags=["a\nb", "slash\\literal"];
        fixture.AiResult!.Examples=[new("", "中文\n行")];
        var round=ArchiveTransferService.ParseCsv(ArchiveTransferService.GenerateCsv([fixture])).Single();
        Check(round.Notes==fixture.Notes && round.Archive.Tags.SequenceEqual(fixture.Archive.Tags) && round.AiResult!.Examples[0].Chinese=="中文\n行", "CSV escape roundtrip");
        Throws<FormatException>(()=>ArchiveTransferService.ParseCsv("单词,单词\na,b"),"duplicate headers accepted");
        Throws<FormatException>(()=>ArchiveTransferService.ParseCsv("单词\n\"unfinished"),"broken CSV accepted");
        var android=Environment.GetEnvironmentVariable("LEXI_ANDROID_CSV");
        if (!string.IsNullOrEmpty(android)) {
            var imported=ArchiveTransferService.ParseCsv(File.ReadAllText(android)).Single();
            Check(imported.Word=="reason" && imported.Notes=="=HYPERLINK(\"https://example.com\")" && imported.Archive.Tags.SequenceEqual(new[]{"阅读","literal\\n","multi\nline"}) && imported.AiResult!.Synonyms.Single()=="'cause" && imported.AiResult.Examples[0].Chinese=="", "Android CSV field mismatch");
            var returned=Path.Combine(Path.GetDirectoryName(android)!,"windows-via-android.csv");
            if(File.Exists(returned)) {
                var returnedWord=ArchiveTransferService.ParseCsv(File.ReadAllText(returned)).Single();
                Check(returnedWord.Archive.Uuid==fixture.Archive.Uuid && returnedWord.ReviewCount==7 && returnedWord.AiResult!.Phrases[0].Chinese=="提交简历", "Windows Android Windows roundtrip mismatch");
            }
        }
    }

    private static void ImportTransaction()
    {
        var root=Path.Combine(Path.GetTempPath(),"Lexi_CsvImport_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            using var db=new VocabularyService(Path.Combine(root,"vocab.sqlite3"));
            var words=ArchiveTransferService.ParseCsv(ArchiveTransferService.GenerateCsv([Fixture()]));
            Check(db.ImportArchive(words)==(1,0),"CSV import failed");
            Check(db.ImportArchive(words)==(0,1),"duplicate CSV overwritten");
            var loaded=db.GetAllWords().Single();
            Check(loaded.Archive.Uuid==words[0].Archive.Uuid && loaded.Archive.Revision==2 && loaded.AiResult!.Examples[0].Chinese=="发送简历。" && loaded.ReviewCount==7,"database import lost metadata/AI/progress");
            words[0].Stage=8;
            Throws<FormatException>(()=>db.ImportArchive(words),"invalid import accepted");
            Check(db.GetAllWords().Count==1,"failed import changed data");
        } finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }

    /// <summary>Caller writes this text with new UTF8Encoding(true) for Excel's UTF-8 BOM detection.</summary>
    public static string GenerateLegacyCsv(IEnumerable<WordItem> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var output = new StringBuilder();
        AddRow(["单词", "音标", "中文释义", "英文释义", "备注", "学习阶段", "状态", "创建时间", "学习开始日期", "下次重逢日期",
            "上次复习时间", "复习次数", "档案标识", "来源类型", "来源标题", "来源原句", "标签", "遇见次数", "首次收藏UTC", "更新时间UTC", "最近遇见UTC", "修订版本", "AI例句", "AI同义词", "AI反义词", "AI词组"]);
        foreach (var word in words)
        {
            ArgumentNullException.ThrowIfNull(word);
            var a = word.Archive ?? throw new FormatException("缺少档案元数据。");
            AddRow([word.Word, word.Phonetic, word.Translation, word.Definition, word.Notes, Convert.ToString(word.Stage),
                word.Status == "mastered" ? "已掌握" : "学习中", word.CreatedAt, word.LearningStartDate, word.NextReviewDate,
                word.LastReviewedAt, Convert.ToString(word.ReviewCount), a.Uuid, a.SourceType, a.SourceTitle, a.SourceExcerpt,
                string.Join("; ", a.Tags), Convert.ToString(a.EncounterCount), a.CreatedAtUtc, a.UpdatedAtUtc, a.LastEncounteredAtUtc, Convert.ToString(a.Revision),
                string.Join("\n", word.AiResult?.Examples.Select(e => e.English + " / " + e.Chinese) ?? []),
                string.Join("; ", word.AiResult?.Synonyms ?? []), string.Join("; ", word.AiResult?.Antonyms ?? []),
                string.Join("\n", word.AiResult?.Phrases.Select(p => p.English + " / " + p.Chinese) ?? [])]);
        }
        return output.ToString();

        void AddRow(IEnumerable<string?> fields) => output.Append(string.Join(",", fields.Select(value => "\"" + (value ?? "").Replace("\"", "\"\"") + "\""))).Append("\r\n");
    }

    private static void SecretStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lexi_SecretTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Throws<PlatformNotSupportedException>(() => new WindowsSecretStore(root), "non-Windows store accepted");
                return;
            }
            var store = new WindowsSecretStore(root);
            Check(store.Get() == null, "missing credential is not empty");
            const string secret = "fixture-only-测试-secret-983725";
            store.Set(secret);
            Check(new WindowsSecretStore(root).Get() == secret, "DPAPI roundtrip failed");
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Check(files.Length == 1 && !Encoding.UTF8.GetString(File.ReadAllBytes(files[0])).Contains(secret), "secret was stored in plaintext or temporary file leaked");
            store.Set("replacement-fixture");
            Check(store.Get() == "replacement-fixture", "atomic replacement did not persist");
            File.WriteAllBytes(files[0], [1, 2, 3]);
            Throws<System.Security.Cryptography.CryptographicException>(() => store.Get(), "corrupt protected credential accepted");
            store.Delete();
            store.Delete();
            Check(store.Get() == null, "delete failed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Foundation transfer failed: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Foundation transfer failed: " + message);
    }
}
