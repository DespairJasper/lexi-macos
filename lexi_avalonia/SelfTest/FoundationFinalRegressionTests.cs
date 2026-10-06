using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Microsoft.Data.Sqlite;

namespace Lexi;

public static class FoundationFinalRegressionTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lexi_FinalRestore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<string>();
        try
        {
            var cases = new[] { "UPDATE word_archives SET encounter_count=2147483648", "UPDATE word_archives SET revision=2147483648",
                "UPDATE words SET stage=2147483648", "UPDATE words SET review_count=2147483648", "UPDATE words SET stage=5,status='learning'",
                "UPDATE words SET next_review_date='2026-02-30'", "UPDATE word_archives SET uuid='bad'",
                "UPDATE word_archives SET created_at_utc='2026-09-19T12:00:00+08:00'", "UPDATE word_archives SET revision=0" };
            for (var i = 0; i < cases.Length; i++)
            {
                var folder = Path.Combine(root, i.ToString()); Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, "target.sqlite3"); var candidate = Path.Combine(folder, "candidate.sqlite3");
                using (var v = new VocabularyService(target)) v.AddWord("original", "", "原库", "");
                using (var v = new VocabularyService(candidate)) v.AddWord("candidate", "", "候选", "");
                using (var c = Open(candidate)) { using var cmd = c.CreateCommand(); cmd.CommandText = cases[i]; cmd.ExecuteNonQuery(); }
                var before = Hash(target); var rejected = false;
                try { DatabaseSafety.RestoreBackup(candidate, target); }
                catch (Exception ex) when (ex is InvalidDataException or SqliteException or OverflowException or FormatException) { rejected = true; }
                if (!rejected || Hash(target) != before) failures.Add("candidate " + i + " was not rejected before destination replacement");
            }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
            Console.WriteLine("PASS: final restore validation (numeric bounds, schedule, UUID/UTC, destination hashes unchanged)");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    public static async Task RunAsync(MainWindow window, Action<bool, string> check)
    {
        var isolated = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(isolated)) throw new InvalidOperationException("Final UI tests require isolated LEXI_DATA_DIR.");
        T C<T>(string name) where T : Control => window.FindControl<T>(name)!;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        VocabularyService Store() => (VocabularyService)Field("_vocabService")!;
        async Task Lookup(string word) { C<TextBox>("LookupInput").Text = word; await (Task)Call("PerformLookupAsync")!; }
        async Task Restore(string path) { Set("_pendingRestore", path); await (Task)Call("RestoreChosenBackupAsync")!; }
        var failures = new List<string>();
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); check(true, name); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
        }
        using var server = new LocalAiServer();
        var settings = (AppSettings)Field("_settings")!;
        settings.ApiKey = "fixture-local-only"; settings.BaseUrl = server.Url; settings.Provider = "custom"; settings.Model = "fixture";
        await Test("AI merges latest saved modules across lookup and row", async () =>
        {
            Store().AddWord("final-ai", "", "测试", ""); Call("RefreshWords"); await Lookup("final-ai");
            var row = ((List<WordItem>)Field("_allWords")!).Single(w => w.Word == "final-ai");
            row.OptExamples = true; row.OptSynonyms = false;
            window.OnRowAiGenerateClick(new Button { DataContext = row }, new RoutedEventArgs());
            await server.ReplyAsync(new { examples = new[] { new { en = "latest example", zh = "最新例句" } } });
            await Until(() => !row.IsGeneratingAi);
            C<CheckBox>("AiOptExamples").IsChecked = false; C<CheckBox>("AiOptSynonyms").IsChecked = true;
            C<CheckBox>("AiOptAntonyms").IsChecked = C<CheckBox>("AiOptPhrases").IsChecked = false;
            var pending = (Task)Call("PerformAiGenerationAsync")!;
            await server.ReplyAsync(new { synonyms = new[] { "latest synonym", "second synonym", "third synonym" } }); await pending;
            var saved = Store().GetAllWords().Single(w => w.Id == row.Id).AiResult!;
            Require(saved.Examples.SingleOrDefault()?.English == "latest example" && saved.Synonyms.First() == "latest synonym", "unrequested saved example was overwritten");
            // Hold lookup request, then commit another module through the real service before completion.
            pending = (Task)Call("PerformAiGenerationAsync")!;
            Store().SaveExpansion(row.Id, new LlmResult { Examples = [new("concurrent example", "并发例句")], Phrases = [new("saved phrase", "词组")] });
            await server.ReplyAsync(new { synonyms = new[] { "concurrent synonym", "second synonym", "third synonym" } }); await pending;
            saved = Store().GetAllWords().Single(w => w.Id == row.Id).AiResult!;
            Require(saved.Examples.Single().English == "concurrent example" && saved.Phrases.Single().English == "saved phrase", "in-flight lookup merged stale state");
        });
        await Test("explicit edit cancels outstanding lookup and row AI", async () =>
        {
            Store().AddWord("final-edit", "", "编辑", ""); Call("RefreshWords"); await Lookup("final-edit");
            var row = ((List<WordItem>)Field("_allWords")!).Single(w => w.Word == "final-edit");
            var pending = (Task)Call("PerformAiGenerationAsync")!;
            var incoming = await server.ReceiveAsync();
            Call("OpenArchiveEditor", row);
            C<TextBox>("ArchiveSynonymsInput").Text = "explicit edit";
            Call("SaveArchiveEditor");
            await server.ReplyAsync(incoming, new { synonyms = new[] { "late AI", "second synonym", "third synonym" } }); await pending;
            Require(Store().GetAllWords().Single(w => w.Id == row.Id).AiResult!.Synonyms.Single() == "explicit edit", "late generation overwrote explicit edit");
            row.OptExamples = true; row.OptSynonyms = false;
            window.OnRowAiGenerateClick(new Button { DataContext = row }, new RoutedEventArgs()); incoming = await server.ReceiveAsync();
            Call("OpenArchiveEditor", row); C<TextBox>("ArchiveExamplesInput").Text = "explicit example | 编辑例句"; Call("SaveArchiveEditor");
            await server.ReplyAsync(incoming, new { examples = new[] { new { en = "late row", zh = "迟到" } } }); await Until(() => !row.IsGeneratingAi);
            Require(Store().GetAllWords().Single(w => w.Id == row.Id).AiResult!.Examples.Single().English == "explicit example", "late row generation overwrote explicit edit");
        });
        await Test("deleted row is never recreated by late AI", async () =>
        {
            Store().AddWord("final-delete", "", "删除", ""); Call("RefreshWords");
            var row = ((List<WordItem>)Field("_allWords")!).Single(w => w.Word == "final-delete"); row.OptExamples = true; row.OptSynonyms = false;
            window.OnRowAiGenerateClick(new Button { DataContext = row }, new RoutedEventArgs());
            var incoming = await server.ReceiveAsync(); Store().ExecuteBatch([row.Id], "delete"); Call("RefreshWords");
            await server.ReplyAsync(incoming, new { examples = new[] { new { en = "late", zh = "迟到" } } }); await Until(() => !row.IsGeneratingAi);
            Require(Store().GetAllWords().All(w => w.Id != row.Id), "deleted row was recreated");
        });
        await Test("restore oldest retained backup survives pre-restore rotation", async () =>
        {
            for (var i = 0; i < 20; i++) Store().CreateManualBackup();
            var oldest = Directory.GetFiles(Path.Combine(isolated, "backups"), "lexi-*.sqlite3").Order().First();
            using var backup = Open(oldest); using var cmd = backup.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM words"; var count = Convert.ToInt32(cmd.ExecuteScalar()); backup.Close();
            await Restore(oldest);
            Require(C<TextBlock>("GlobalStatusText").Text!.Contains("恢复完成") && Store().GetAllWords().Count == count, "selected oldest backup was removed before restore");
            Require(!Directory.GetFiles(isolated, ".restore-selection-*").Any(), "restore staging file leaked");
        });
        await Test("manual backup information survives settings refresh", async () =>
        {
            await (Task)Call("BackupNowAsync")!;
            var newest = Directory.GetFiles(Path.Combine(isolated, "backups"), "lexi-*.sqlite3").Order().Last();
            File.SetLastWriteTime(newest, new DateTime(2031, 3, 4, 5, 6, 7)); Call("UpdateDataInfo");
            Require(C<TextBlock>("LastBackupText").Text!.Contains("2031-03-04 05:06:07"), "settings reverted to stale backup path");
        });
        await Test("pre-disposal backup failure preserves current live connection", async () =>
        {
            var store = Store(); var backup = store.CreateManualBackup(); var selection = Path.Combine(isolated, "selected.sqlite3"); File.Copy(backup, selection, true);
            var folder = Path.Combine(isolated, "backups"); var moved = Path.Combine(isolated, "backups-test-held"); Directory.Move(folder, moved); File.WriteAllText(folder, "fixture directory obstruction");
            try { await Restore(selection); Require(ReferenceEquals(store, Store()) && store.GetAllWords().Count > 0, "backup failure replaced/leaked still-open connection"); }
            finally { File.Delete(folder); Directory.Move(moved, folder); File.Delete(selection); }
        });
        await Test("post-open refresh failure disposes the replaced service before retry", async () =>
        {
            var backup = Store().CreateManualBackup();
            Store().AddWord("refresh-failure-fixture", "", "刷新失败", ""); Call("RefreshWords");
            VocabularyService? firstOpened = null;
            void FailOnce(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property == TextBlock.TextProperty && firstOpened == null)
                {
                    firstOpened = Store();
                    throw new InvalidOperationException("injected UI refresh failure");
                }
            }
            C<TextBlock>("NavVocabBadge").PropertyChanged += FailOnce;
            try
            {
                await Restore(backup);
                Require(firstOpened != null && !ReferenceEquals(firstOpened, Store()), "refresh failure did not exercise reopened service retry");
                var closed = false;
                try { firstOpened!.GetAllWords(); } catch (InvalidOperationException) { closed = true; }
                Require(closed && Store().GetAllWords().All(w => w.Word != "refresh-failure-fixture"), "previous connection leaked or installed candidate was misreported");
                Require(C<TextBlock>("GlobalStatusText").Text!.Contains("备份已替换词库"), "status falsely claimed original database was still active");
            }
            finally { C<TextBlock>("NavVocabBadge").PropertyChanged -= FailOnce; }
        });
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI task did not finish."); await Task.Delay(20); }
    }

    /// <summary>Destructive only to an isolated fixture; leaves its window disabled for safe exit.</summary>
    public static async Task RunFatalRecoveryAsync(MainWindow window, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEXI_DATA_DIR"))) throw new InvalidOperationException("Requires isolated fixture.");
        var serviceField = typeof(MainWindow).GetField("_vocabService", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var service = (VocabularyService)serviceField.GetValue(window)!;
        var backup = service.CreateManualBackup();
        service.AddWord("fatal-refresh-fixture", "", "仅限测试", "");
        typeof(MainWindow).GetMethod("RefreshWords", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        var badge = window.FindControl<TextBlock>("NavVocabBadge")!;
        var injected = false;
        void CorruptAfterReopen(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property != TextBlock.TextProperty || injected) return;
            injected = true;
            using var db = Open(service.DatabasePath); using var command = db.CreateCommand();
            command.CommandText = "UPDATE word_archives SET encounter_count=2147483648"; command.ExecuteNonQuery();
            throw new InvalidOperationException("injected catastrophic refresh failure");
        }
        badge.PropertyChanged += CorruptAfterReopen;
        try
        {
            typeof(MainWindow).GetField("_pendingRestore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, backup);
            await (Task)typeof(MainWindow).GetMethod("RestoreChosenBackupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            Require(injected && !window.FindControl<Control>("PageLookup")!.IsEnabled && !window.FindControl<Control>("PageVocab")!.IsEnabled
                && !window.FindControl<Control>("PageSettings")!.IsEnabled && window.IsEnabled && window.FindControl<Button>("QuitAppBtn")!.IsEnabled,
                "fatal reopen did not disable data controls while allowing exit");
            var closed = false;
            try { ((VocabularyService)serviceField.GetValue(window)!).GetAllWords(); } catch (InvalidOperationException) { closed = true; }
            Require(closed && window.FindControl<TextBlock>("GlobalStatusText")!.Text!.Contains("备份已替换"), "fatal path leaked connection or claimed original database active");
            check(true, "fatal reopen disables data operations, releases connections, reports installed candidate, and allows exit");
        }
        finally { badge.PropertyChanged -= CorruptAfterReopen; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static SqliteConnection Open(string path) { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open(); return c; }

    private sealed class LocalAiServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public LocalAiServer()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Url = "http://127.0.0.1:" + port; _listener.Prefixes.Add(Url + "/"); _listener.Start();
        }
        public Task<HttpListenerContext> ReceiveAsync() => _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task ReplyAsync(object result) => await ReplyAsync(await ReceiveAsync(), result);
        public async Task ReplyAsync(HttpListenerContext context, object result)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(result) } } } }));
                context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }
        public void Dispose() => _listener.Close();
    }
}

