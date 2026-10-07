using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Lexi;

public static class SelfTestRunner
{
    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("       Lexi Avalonia --self-test 自动化自检      ");
        Console.WriteLine("=================================================");

        int passed = 0;
        int total = 15;
        string tempDir = Path.Combine(Path.GetTempPath(), "Lexi_SelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Enforce test isolation: LEXI_DATA_DIR
        Environment.SetEnvironmentVariable("LEXI_DATA_DIR", tempDir);

        try
        {
            // Test 1: Offline Dictionary Lookup
            Console.Write("[1/7] 离线词库检索与边界测试... ");
            TestDictionary();
            Console.WriteLine("PASS");
            passed++;

            // Test 2: Scheduling & Idempotency
            Console.Write("[2/7] 艾宾浩斯排期、同日幂等与逾期推进测试... ");
            TestScheduling(tempDir);
            Console.WriteLine("PASS");
            passed++;

            // Test 3: Word Deduplication & Field Merging
            Console.Write("[3/7] 重复词录入补全与防破坏测试... ");
            TestWordDeduplication(tempDir);
            Console.WriteLine("PASS");
            passed++;

            // Test 4: Batch Atomicity & Rollback
            Console.Write("[4/7] 批量操作原子事务与回滚验证... ");
            TestBatchAtomicity(tempDir);
            Console.WriteLine("PASS");
            passed++;

            // Test 5: Legacy Migration & WAL Handling
            Console.Write("[5/7] SQLite Backup 与 WAL 数据迁移验证... ");
            TestMigrationAndWal(tempDir);
            Console.WriteLine("PASS");
            passed++;

            // Test 6: A4 Export HTML Escaping
            Console.Write("[6/7] A4 HTML 导出排版与安全转义验证... ");
            TestExportEscaping();
            Console.WriteLine("PASS");
            passed++;

            // Test 7: Mock HTTP AI Integration
            Console.Write("[7/7] 模拟 HTTP AI 请求、结构校验与超时取消测试... ");
            await TestMockAiServiceAsync();
            Console.WriteLine("PASS");
            passed++;

            DataRegressionTests.Run();
            passed++;
            DatabaseSafetyTests.Run();
            passed++;
            FoundationDataTests.Run();
            passed++;
            FoundationTransferTests.Run();
            passed++;
            FoundationCredentialTests.Run();
            passed++;
            FoundationFinalRegressionTests.Run();
            passed++;
            await UpdateCheckTests.RunAsync();
            passed++;
            UpgradePreservationTests.Run();
            passed++;
            Console.WriteLine("=================================================");
            Console.WriteLine($"所有自检项目执行完毕: {passed}/{total} 项通过！退出码: 0");
            Console.WriteLine("=================================================");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED!");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] 自检失败: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            Console.WriteLine("=================================================");
            Console.WriteLine($"自检未通过: {passed}/{total} 项通过。退出码: 1");
            Console.WriteLine("=================================================");
            return 1;
        }
        finally
        {
            try
            {
                Environment.SetEnvironmentVariable("LEXI_DATA_DIR", null);
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { }
        }
    }

    private static void TestDictionary()
    {
        using var dict = new DictionaryService();
        var r1 = dict.Lookup("hello");
        if (!r1.Found || string.IsNullOrEmpty(r1.Translation))
        {
            throw new Exception("检索单词 'hello' 失败或释义为空");
        }
        if (!r1.Phonetic.StartsWith('/') || !r1.Phonetic.EndsWith('/'))
        {
            throw new Exception($"音标格式不正确: {r1.Phonetic}");
        }

        var rCase = dict.Lookup("HELLO");
        if (!rCase.Found || rCase.Word.ToLowerInvariant() != "hello")
        {
            throw new Exception("大写检索 'HELLO' 失败");
        }

        var rNon = dict.Lookup("xyznonexistentword12345");
        if (rNon.Found)
        {
            throw new Exception("不存在的单词却返回 Found = true");
        }
    }

    private static void TestScheduling(string tempDir)
    {
        var dbPath = Path.Combine(tempDir, "test_sched.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        vocab.AddWord("resilient", "/rɪˈzɪliənt/", "有复原力的", "able to withstand or recover quickly");
        var list = vocab.GetAllWords();
        var word = list.FirstOrDefault(w => w.Word == "resilient");
        if (word == null || word.Stage != 0 || word.Status != "learning")
        {
            throw new Exception("初次录入单词状态或阶段不正确");
        }

        // Review stage 0 -> stage 1
        vocab.ExecuteBatch(new[] { word.Id }, "review");
        list = vocab.GetAllWords();
        word = list.First(w => w.Id == word.Id);
        if (word.Stage != 1)
        {
            throw new Exception($"推进复习失败，预期 stage=1，实际 stage={word.Stage}");
        }

        // Same day review idempotency
        vocab.ExecuteBatch(new[] { word.Id }, "review");
        list = vocab.GetAllWords();
        word = list.First(w => w.Id == word.Id);
        if (word.Stage != 1)
        {
            throw new Exception($"同日复习未保持幂等，stage 变为了 {word.Stage}");
        }

        // Overdue review test
        var pastDate = DateTime.Today.AddDays(-10).ToString("yyyy-MM-dd");
        using (var rawConn = new SqliteConnection($"Data Source={dbPath}"))
        {
            rawConn.Open();
            using var cmd = rawConn.CreateCommand();
            cmd.CommandText = "UPDATE words SET next_review_date = $past, last_reviewed_at = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$past", pastDate);
            cmd.Parameters.AddWithValue("$id", word.Id);
            cmd.ExecuteNonQuery();

            // Clear review_logs for today to test review again
            using var clearLog = rawConn.CreateCommand();
            clearLog.CommandText = "DELETE FROM review_logs WHERE word_id = $id AND log_date = $today";
            clearLog.Parameters.AddWithValue("$id", word.Id);
            clearLog.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
            clearLog.ExecuteNonQuery();
        }

        vocab.ExecuteBatch(new[] { word.Id }, "review");
        list = vocab.GetAllWords();
        word = list.First(w => w.Id == word.Id);
        if (word.Stage != 2)
        {
            throw new Exception($"逾期复习推进失败，预期 stage=2，实际={word.Stage}");
        }
        if (string.Compare(word.NextReviewDate, DateTime.Today.ToString("yyyy-MM-dd")) <= 0)
        {
            throw new Exception($"逾期复习推进后下一次复习时间必须严格晚于今天，实际为 {word.NextReviewDate}");
        }

        // Undo review test
        var undoOk = vocab.UndoLastReview(word.Id);
        if (!undoOk)
        {
            throw new Exception("撤销复习操作返回 false");
        }
        list = vocab.GetAllWords();
        word = list.First(w => w.Id == word.Id);
        if (word.Stage != 1)
        {
            throw new Exception($"撤销复习后阶段未恢复，预期 stage=1，实际={word.Stage}");
        }
    }

    private static void TestWordDeduplication(string tempDir)
    {
        var dbPath = Path.Combine(tempDir, "test_dedup.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        vocab.AddWord("apple", "", "苹果", "");
        var words = vocab.GetAllWords();
        if (words.Count != 1 || words[0].Word != "apple")
        {
            throw new Exception("第一次添加 apple 失败");
        }

        // Advance progress
        vocab.ExecuteBatch(new[] { words[0].Id }, "stage", 3);

        // Add again with missing fields filled
        vocab.AddWord("apple", "/ˈæpl/", "苹果", "a round fruit");
        words = vocab.GetAllWords();
        if (words.Count != 1)
        {
            throw new Exception("重复添加导致了多条记录产生");
        }
        var updated = words[0];
        if (updated.Phonetic != "/ˈæpl/" || updated.Definition != "a round fruit")
        {
            throw new Exception("未能正确补充空白字段");
        }
        if (updated.Stage != 3)
        {
            throw new Exception("补充字段破坏了原有学习进度 stage");
        }
    }

    private static void TestBatchAtomicity(string tempDir)
    {
        var dbPath = Path.Combine(tempDir, "test_atom.sqlite3");
        using var vocab = new VocabularyService(dbPath);

        vocab.AddWord("word1", "", "词1", "");
        vocab.AddWord("word2", "", "词2", "");

        var words = vocab.GetAllWords();
        var id1 = words[0].Id;
        var id2 = words[1].Id;
        var invalidId = 8888888L;

        bool threw = false;
        try
        {
            vocab.ExecuteBatch(new[] { id1, invalidId, id2 }, "master");
        }
        catch (InvalidOperationException ex)
        {
            threw = true;
            if (!ex.Message.Contains(invalidId.ToString()))
            {
                throw new Exception("未在异常中指明无效的 ID");
            }
        }

        if (!threw)
        {
            throw new Exception("包含不存在 ID 的批处理未抛出异常");
        }

        var checkWords = vocab.GetAllWords();
        foreach (var w in checkWords)
        {
            if (w.Status == "mastered" || w.Stage != 0)
            {
                throw new Exception($"事务未完整回滚，单词 {w.Word} 的状态被部分修改！");
            }
        }
    }

    private static void TestMigrationAndWal(string tempDir)
    {
        var legacyPath = Path.Combine(tempDir, "legacy_vocab.sqlite3");
        var targetPath = Path.Combine(tempDir, "new_vocab.sqlite3");

        // 1. Create legacy DB with WAL mode and insert test data
        using (var conn = new SqliteConnection($"Data Source={legacyPath};Mode=ReadWriteCreate"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE words (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    word TEXT UNIQUE NOT NULL,
                    phonetic TEXT DEFAULT '',
                    translation TEXT DEFAULT '',
                    definition TEXT DEFAULT '',
                    notes TEXT DEFAULT '',
                    stage INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'learning',
                    created_at TEXT NOT NULL,
                    learning_start_date TEXT NOT NULL,
                    next_review_date TEXT,
                    last_reviewed_at TEXT,
                    review_count INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO words (word, translation, created_at, learning_start_date)
                VALUES ('migrated_test', '迁移测试', '2026-09-18 12:00:00', '2026-09-18');
            ";
            cmd.ExecuteNonQuery();
        }

        // 2. Safely copy/export using VACUUM INTO to target without touching legacy DB
        using (var srcConn = new SqliteConnection($"Data Source={legacyPath};Mode=ReadOnly"))
        {
            srcConn.Open();
            using var vacCmd = srcConn.CreateCommand();
            vacCmd.CommandText = "VACUUM INTO $targetPath";
            vacCmd.Parameters.AddWithValue("$targetPath", targetPath);
            vacCmd.ExecuteNonQuery();
        }

        // 3. Verify target DB has the data
        using (var destConn = new SqliteConnection($"Data Source={targetPath};Mode=ReadOnly"))
        {
            destConn.Open();
            using var queryCmd = destConn.CreateCommand();
            queryCmd.CommandText = "SELECT COUNT(*) FROM words WHERE word = 'migrated_test'";
            var count = (long)queryCmd.ExecuteScalar()!;
            if (count != 1)
            {
                throw new Exception("迁移后的数据库未包含预期数据");
            }
        }
    }

    private static void TestExportEscaping()
    {
        var words = new[]
        {
            new WordItem
            {
                Word = "rock & roll <tag>",
                Phonetic = "/test \"quote\"/",
                Translation = "摇滚 & '单引号' <HTML>",
                Stage = 0
            }
        };

        var html = ExportService.GenerateHtml(words);

        if (html.Contains("<tag>") || html.Contains("<HTML>"))
        {
            throw new Exception("HTML 导出未正确转义尖括号");
        }
        if (!html.Contains("&amp;") || !html.Contains("&lt;tag&gt;"))
        {
            throw new Exception("HTML 导出缺少必要的实体转义");
        }
        if (!html.Contains("&#9633;"))
        {
            throw new Exception("HTML 导出未包含复习打卡方框");
        }
    }

    private static async Task TestMockAiServiceAsync()
    {
        // Start a mock HTTP server on an ephemeral loopback port
        var listener = new HttpListener();
        int port = 49152;
        while (port < 50000)
        {
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                break;
            }
            catch
            {
                port++;
            }
        }

        if (!listener.IsListening)
        {
            throw new Exception("未能启动本地 HTTP 测试服务器");
        }

        var ctsServer = new CancellationTokenSource();
        var baseUrl = $"http://127.0.0.1:{port}";
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();

        // Run mock server handler in background
        _ = Task.Run(async () =>
        {
            while (!ctsServer.IsCancellationRequested)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    var req = ctx.Request;

                    if (req.Url?.AbsolutePath.EndsWith("chat/completions") == true)
                    {
                        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                        var body = await reader.ReadToEndAsync();
                        requests.Enqueue(body);

                        if (body.Contains("timeout_trigger"))
                        {
                            // Simulate delay exceeding client timeout
                            await Task.Delay(2500, ctsServer.Token);
                        }

                        var mockResponseJson = @"
                        {
                            ""choices"": [
                                {
                                    ""message"": {
                                        ""content"": ""{\""examples\"":[{\""en\"":\""Practice makes perfect.\"",\""zh\"":\""熟能生巧。\""}],\""synonyms\"":[\""exercise\"",\""rehearse\"",\""train\""],\""antonyms\"":[\""neglect\"",\""forget\""]}""
                                    }
                                }
                            ]
                        }";

                        var respBytes = Encoding.UTF8.GetBytes(mockResponseJson);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = respBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(respBytes, 0, respBytes.Length);
                        ctx.Response.Close();
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        });

        try
        {
            var aiService = new AiService();
            var settings = new AppSettings
            {
                BaseUrl = baseUrl,
                Model = "mock-model",
                ApiKey = "mock-key",
                Timeout = 5,
                AiContext = "技术文档"
            };

            // 1. Normal successful parsing
            var result = await aiService.GenerateExpansionAsync(
                "practice",
                new[] { "examples", "synonyms", "antonyms" },
                settings, sourceExcerpt: "source-private-marker"
            );

            if (result.Examples.Count != 1 || result.Synonyms.Count != 3 || result.Antonyms.Count != 2)
            {
                throw new Exception("解析模拟 AI 输出结构失败或数量不匹配");
            }

            var firstRequest = requests.Last();
            using (var payload = JsonDocument.Parse(firstRequest))
            using (var user = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!))
            {
                if (user.RootElement.GetProperty("context").GetString() != "技术文档" || user.RootElement.GetProperty("source").ValueKind != JsonValueKind.Null)
                    throw new Exception("AI context or private source opt-in boundary failed");
            }
            settings.IncludeSourceInAi = true;
            await aiService.GenerateExpansionAsync("practice", new[] { "examples", "synonyms", "antonyms" }, settings, sourceExcerpt: "source-private-marker");
            if (!requests.Last().Contains("source-private-marker")) throw new Exception("AI source opt-in was ignored");

            // 2. Timeout testing
            var shortTimeoutSettings = new AppSettings
            {
                BaseUrl = baseUrl,
                Model = "mock-model",
                ApiKey = "mock-key",
                Timeout = 1 // 1 second timeout
            };

            bool timeoutCaught = false;
            try
            {
                await aiService.GenerateExpansionAsync(
                    "timeout_trigger",
                    new[] { "examples" },
                    shortTimeoutSettings
                );
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("超时"))
            {
                timeoutCaught = true;
            }

            if (!timeoutCaught)
            {
                throw new Exception("AI 超时机制未生效");
            }

            // 3. User cancel testing
            using var userCts = new CancellationTokenSource();
            userCts.Cancel();
            bool cancelCaught = false;
            try
            {
                await aiService.GenerateExpansionAsync(
                    "cancel_test",
                    new[] { "examples" },
                    settings,
                    userCts.Token
                );
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("已取消生成"))
            {
                cancelCaught = true;
            }

            if (!cancelCaught)
            {
                throw new Exception("AI 取消机制未生效");
            }
        }
        finally
        {
            ctsServer.Cancel();
            listener.Stop();
            listener.Close();
        }
    }
}
