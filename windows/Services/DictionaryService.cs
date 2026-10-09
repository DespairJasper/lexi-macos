using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Lexi;

public sealed class DictionaryService : IDictionaryLookup, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public Task<LookupResult> LookupAsync(string word) => Task.Run(() => Lookup(word));

    public static string ResolveDictionaryPath()
    {
        var exeDir = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(exeDir, "Assets", "dictionary.sqlite3"),
            Path.Combine(exeDir, "resources", "dictionary.sqlite3"),
            Path.Combine(exeDir, "dictionary.sqlite3"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "dictionary.sqlite3"),
            Path.Combine(Directory.GetCurrentDirectory(), "outputs", "lexi_avalonia", "Assets", "dictionary.sqlite3"),
            Path.Combine(Directory.GetCurrentDirectory(), "outputs", "lexi_tauri", "src-tauri", "resources", "dictionary.sqlite3")
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return Path.Combine(exeDir, "Assets", "dictionary.sqlite3");
    }

    public DictionaryService(string? path = null)
    {
        var dictPath = path ?? ResolveDictionaryPath();
        if (!File.Exists(dictPath))
        {
            throw new FileNotFoundException($"离线词库缺失，未找到词库文件: {dictPath}", dictPath);
        }

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dictPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        _connection = new SqliteConnection(connStr);
        _connection.Open();
    }

    public LookupResult Lookup(string word)
    {
        lock (_gate) return LookupCore(word);
    }

    private LookupResult LookupCore(string word)
    {
        var clean = word.Trim();
        if (string.IsNullOrEmpty(clean))
        {
            throw new ArgumentException("查询单词不能为空。", nameof(word));
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT word, phonetic, translation, definition, pos FROM ecdict WHERE word = $word COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$word", clean);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new LookupResult
            {
                Word = clean,
                Found = false
            };
        }

        string ReadStr(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);

        var phonetic = ReadStr(1).Trim();
        if (!string.IsNullOrEmpty(phonetic) && !phonetic.StartsWith('/'))
        {
            phonetic = $"/{phonetic}/";
        }

        return new LookupResult
        {
            Word = ReadStr(0),
            Phonetic = phonetic,
            Translation = ReadStr(2).Trim(),
            Definition = ReadStr(3).Trim(),
            Pos = ReadStr(4).Trim(),
            Found = true
        };
    }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}
