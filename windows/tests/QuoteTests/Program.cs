using Lexi;
using Microsoft.Data.Sqlite;
using System.Text.Json;

static void Check(bool value, string message)
{
    if (!value) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

var root = Path.Combine(Path.GetTempPath(), "LexiQuoteTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var db = Path.Combine(root, "quotes.sqlite3");
try
{
    using (var store = new VocabularyService(db))
    {
        Check(store.GetType().GetMethod("SaveQuote") != null, "quote saving API is available");
        dynamic quotes = store;
        dynamic saved = quotes.SaveQuote(null, " Evidence matters. ", "证据很重要。", "Paper", "note");
        Check(saved.Original == "Evidence matters." && saved.Translation == "证据很重要。", "quote fields are trimmed and saved");
        dynamic duplicate = quotes.SaveQuote(null, "Evidence matters.", "覆盖失败", "", "");
        Check(duplicate.AlreadyExists && duplicate.Translation == saved.Translation, "duplicate does not overwrite existing quote");
        var second = quotes.SaveQuote(null, "Independent sentence.", "第二句", "", "");
        var collided = false;
        try { quotes.SaveQuote(second.Id, saved.Original, "overwrite", "", ""); } catch (InvalidOperationException) { collided = true; }
        Check(collided && quotes.GetQuotes("Independent", 50, 0).Count == 1, "editing collision preserves both original quotes");
        quotes.DeleteQuote(second.Id);
        quotes.SaveQuote(saved.Id, saved.Original, "修改后的译文", "Paper", "new note");
        Check(quotes.GetQuotes("修改", 50, 0).Count == 1, "search includes edited translation");
        Check(quotes.GetQuotes("", 1, 0).Count == 1, "pagination limit is enforced");
        var rejected = false;
        try { quotes.SaveQuote(null, " ", "", "", ""); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "empty original is rejected");
        rejected = false;
        try { quotes.SaveQuote(null, "Bad\0quote", "", "", ""); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "embedded null characters are rejected before SQLite write");
        quotes.DeleteQuote(saved.Id);
        Check(quotes.GetQuotes().Count == 0, "delete removes quote");
    }
    using (var store = new VocabularyService(db))
    {
        dynamic quotes = store;
        quotes.SaveQuote(null, "export test", "before", "", "");
        var export = typeof(VocabularyService).Assembly.GetType("Lexi.QuoteExportService")?.GetMethod("ExportJsonAsync");
        Check(export != null, "consistent quote export API is available");
        using var output = new MemoryStream();
        await (Task)export!.Invoke(null, [output, store, CancellationToken.None])!;
        using var document = JsonDocument.Parse(output.ToArray());
        Check(document.RootElement.GetProperty("schema").GetString() == "lexi-quotes-1" &&
            document.RootElement.GetProperty("quotes").GetArrayLength() == 1, "quote export uses versioned JSON format");
    }

    using (var store = new VocabularyService(db))
    {
        var saved = store.GetQuotes().Single();
        Check(saved.Original == "export test" && saved.Translation == "before", "quote content survives restart");
        using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        raw.Open();
        using (var tx = raw.BeginTransaction())
        using (var command = raw.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "INSERT INTO quotes(original,translation,source,notes,created_at_utc,updated_at_utc) VALUES($original,'before','','',$time,$time)";
            command.Parameters.AddWithValue("$original", "");
            command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("o"));
            for (var i = 0; i < 500; i++) { command.Parameters["$original"].Value = "Snapshot fixture " + i; command.ExecuteNonQuery(); }
            tx.Commit();
        }
        Check(store.GetQuotes("Snapshot", 1, 499).Count == 1 && store.GetQuotes("Snapshot", 1, 500).Count == 0, "pagination offset handles final page");
        using var output = new MutatingStream(() => store.SaveQuote(saved.Id, saved.Original, "after", "", ""));
        var count = await QuoteExportService.ExportJsonAsync(output, store);
        using var document = JsonDocument.Parse(output.ToArray());
        var rows = document.RootElement.GetProperty("quotes").EnumerateArray().ToArray();
        Check(count == 501 && rows.Length == 501 && rows.Select(q => q.GetProperty("Id").GetInt64()).Distinct().Count() == 501,
            "export includes more than one page without duplicate rows");
        Check(rows.All(q => q.GetProperty("Translation").GetString() == "before") && store.GetQuotes("after").Count == 1,
            "export snapshot stays consistent while archive remains editable");
        var backup = store.CreateManualBackup();
        var staged = DatabaseSafety.StageRestoreSelection(backup, root);
        var restoredPath = Path.Combine(root, "restored.sqlite3");
        try { DatabaseSafety.RestoreBackup(staged, restoredPath); }
        finally { DatabaseSafety.DeleteRestoreSelection(staged); }
        using var restored = new VocabularyService(restoredPath);
        Check(restored.GetQuotes("", 500).Count == 500 && restored.GetQuotes("after").Single().Id == saved.Id,
            "backup restore preserves quotes and their edited content");
    }

    var tampered = Path.Combine(root, "tampered.sqlite3");
    using (var store = new VocabularyService(tampered)) { store.SaveQuote(null, "valid", "", "", ""); }
    using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tampered, Pooling = false }.ToString()))
    {
        raw.Open(); using var command = raw.CreateCommand();
        command.CommandText = "UPDATE quotes SET updated_at_utc='not-a-date'"; command.ExecuteNonQuery();
    }
    var invalidRejected = false;
    try { using var invalid = new VocabularyService(tampered); } catch (InvalidDataException) { invalidRejected = true; }
    Check(invalidRejected, "corrupt quote timestamps stop opening before schema changes");
}
finally { try { Directory.Delete(root, true); } catch { } }

sealed class MutatingStream(Action mutation) : MemoryStream
{
    private bool _mutated;
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_mutated) { _mutated = true; mutation(); }
        return base.WriteAsync(buffer, cancellationToken);
    }
}
