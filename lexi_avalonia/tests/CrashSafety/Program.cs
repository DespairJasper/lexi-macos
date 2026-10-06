using System.Diagnostics;
using System.Reflection;
using Lexi;
using Microsoft.Data.Sqlite;

if (args.Length == 2 && args[0] == "--worker")
{
    var path = args[1];
    using (var service = new VocabularyService(path)) service.AddWord("committed", "", "saved before crash", "");
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    connection.Open();
    using var tx = connection.BeginTransaction();
    using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
    cmd.CommandText = "PRAGMA cache_size=1; UPDATE words SET translation='UNCOMMITTED' WHERE word='committed';";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "CREATE TABLE unfinished(payload TEXT);";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO unfinished(payload) VALUES ($payload)";
    cmd.Parameters.AddWithValue("$payload", new string('x', 32000));
    for (var i = 0; i < 40; i++) cmd.ExecuteNonQuery();
    if (!File.Exists(path + "-journal") || new FileInfo(path + "-journal").Length < 512)
        throw new Exception("Fixture did not produce a rollback journal");
    Console.WriteLine("READY"); Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
    return;
}

var folder = Path.Combine(Path.GetTempPath(), "LexiCrash-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    for (var i = 0; i < 5; i++)
    {
        var file = Path.Combine(folder, "iteration-" + i, "vocab.sqlite3");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--worker"); info.ArgumentList.Add(file);
        using var child = Process.Start(info)!;
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (ready != "READY") throw new Exception("Worker did not reach transaction: " + await child.StandardError.ReadToEndAsync());
            child.Kill(true); await child.WaitForExitAsync();
        }
        finally { if (!child.HasExited) child.Kill(true); }
        using var reopened = new VocabularyService(file);
        var words = reopened.GetAllWords();
        if (words.Count != 1 || words[0].Translation != "saved before crash") throw new Exception("Crash lost committed data or retained partial transaction");
        Console.WriteLine("PASS hard process kill during transaction, reopen retains committed data: " + (i + 1));
    }
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
