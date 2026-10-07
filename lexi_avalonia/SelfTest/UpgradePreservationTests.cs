using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lexi;

/// <summary>
/// 升级数据保留的回归：在隔离目录里造出"旧版本已有数据"的现场，用新版本代码重新打开，
/// 逐项核对记录数、关键字段、学习进度、设置与内部记忆都没有变化、没有被初始化成空。
/// 全程只在 temp 目录操作，绝不读写真实用户数据目录。
/// </summary>
public static class UpgradePreservationTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lexi_Upgrade_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PathIdentity(root);
            ContentPreservedAcrossUpgrade(root);
            JsonSidecarsPreserved(root);
            RepeatedUpgradeIsIdempotent(root);
            FutureSchemaIsRejectedWithoutTouchingData(root);
            Console.WriteLine("PASS: upgrade preservation (path identity, content fingerprint, json sidecars, repeated upgrade, schema gate)");
        }
        finally
        {
            if (OperatingSystem.IsMacOS()) new MacKeychainSecretStore(root).Delete();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Upgrade preservation failed: " + message);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Sql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 数据目录位置是"更新后数据还在不在"的唯一决定因素：它一旦漂移，启动会静默新建空库。
    /// 因此把路径公式本身钉成不变量。
    /// </summary>
    private static void PathIdentity(string root)
    {
        // 自检进程本身带着 LEXI_DATA_DIR；必须先摘掉它，才能核对真实的路径公式。
        var isolated = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        Environment.SetEnvironmentVariable("LEXI_DATA_DIR", null);
        try
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lexi", "vocab.sqlite3");
            Check(string.Equals(VocabularyService.GetDefaultDatabasePath(), expected, StringComparison.Ordinal),
                "default database path moved; an upgrade would silently start from an empty archive");
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cn.local.lexi", "vocab.sqlite3");
            Check(string.Equals(VocabularyService.GetLegacyDatabasePath(), legacy, StringComparison.Ordinal),
                "legacy database path moved; pre-3.0 archives would no longer be found");
            Check(expected.Replace('\\', '/').EndsWith("/Lexi/vocab.sqlite3", StringComparison.Ordinal),
                "data directory and file name must stay <data>/Lexi/vocab.sqlite3");
            Check(string.Equals(VocabularyService.GetDefaultDatabasePath(), expected, StringComparison.Ordinal), "path must be stable across calls");
        }
        finally { Environment.SetEnvironmentVariable("LEXI_DATA_DIR", isolated); }
        // 隔离目录只换目录、不换文件名。
        Check(!string.IsNullOrWhiteSpace(isolated), "self-test must run with LEXI_DATA_DIR set");
        Check(string.Equals(VocabularyService.GetDefaultDatabasePath(), Path.Combine(Path.GetFullPath(isolated!), "vocab.sqlite3"), StringComparison.Ordinal),
            "LEXI_DATA_DIR must only relocate the directory, never rename the database file");
    }

    /// <summary>旧版本留下的现场：词条、复习日志、档案、设置、以及全部内部记忆表都有内容。</summary>
    private static void ContentPreservedAcrossUpgrade(string root)
    {
        var path = Path.Combine(root, "vocab.sqlite3");
        string before;
        var settings = new AppSettings
        {
            Provider = "custom", BaseUrl = "https://example.invalid/v1", Model = "test-model",
            Theme = "Dark", UiLanguage = "en", Timeout = 42, GlassIntensity = 0.4,
            LookupShortcut = "K", TranslateShortcut = "T", QuoteShortcut = "Q"
        };
        using (var store = new VocabularyService(path))
        {
            foreach (var word in new[] { "anchor", "bearing", "cavity" }) store.AddWord(word, "/ˈæŋkə/", "释义 " + word, "definition " + word);
            var ids = store.GetAllWords().Select(w => w.Id).ToArray();
            store.ExecuteBatch(ids, "review");
            store.SaveSettings(settings);
        }
        using (var connection = Open(path))
        {
            // 记忆层：FSRS 卡、规范复习、个人参数模型、上下文快照、排期决策、轨迹与断点。
            // 记忆层的每一张表都要有代表性内容，否则"保留内部记忆"无从验证。
            Sql(connection, "INSERT INTO learning_sessions(session_id,started_at_utc,mode,primary_source,planned_word_count,plan_id) VALUES('s-1','2026-10-01T00:00:00Z','Review','Archive',3,'plan-1');");
            Sql(connection, "INSERT INTO fsrs_cards(word_key,difficulty,stability,reps,lapses,state,last_review_at_utc,next_review_at_utc,last_canonical_rating,fsrs_algorithm_version,fsrs_library_version,fsrs_parameter_version,last_applied_canonical_seq) VALUES('w:anchor',5.5,21.25,7,1,'Review','2026-10-01T00:00:00Z','2026-11-01T00:00:00Z','good','FSRS-6','0.6.0','fsrs6-p1',1);");
            Sql(connection, "INSERT INTO canonical_reviews(canonical_id,word_key,session_id,origin,rating,reviewed_at_utc,completed_at_utc,aggregation_policy_version,revision,invalidated,difficulty,stability,reps,lapses,state,last_canonical_rating) VALUES('c-1','w:anchor','s-1','round','good','2026-10-01T00:00:00Z','2026-10-01T00:00:01Z','agg-1',3,0,5.5,21.25,7,1,'Review','good');");
            Sql(connection, "INSERT INTO personalization_models(model_version,model_kind,trained_at_utc,train_cutoff_utc,status,coefficients_json,scaler_json,metrics_json,last_good_model_version) VALUES('fsrs6-p1-20261001','fsrs_params','2026-10-01T00:00:00Z','2026-09-30T00:00:00Z','Active','[0.1,0.2]','{}','{\"logLoss\":0.31}','fsrs6-p1-20260901');");
            Sql(connection, "INSERT INTO context_snapshots(snapshot_id,word_key,session_id,presentation_id,captured_at_utc,feature_schema_version,model_version,features_json,missing_flags_json,label,confident_recall) VALUES('snap-1','w:anchor','s-1','p-1','2026-10-01T00:00:00Z','ctx-feat-1','ctx-lr-1','{\"latencyMs\":1500}','[]','good',1);");
            Sql(connection, "INSERT INTO learning_events(event_id,session_id,word_key,presentation_id,occurred_at_utc,learning_mode,kind,response,is_recall) VALUES('e-1','s-1','w:anchor','p-1','2026-10-01T00:00:00Z','Review','Rated','good',1);");
            Sql(connection, "INSERT INTO word_session_summaries(word_key,session_id,first_presented_at_utc,completed_at_utc,total_presentations,final_known_count) VALUES('w:anchor','s-1','2026-10-01T00:00:00Z','2026-10-01T00:00:02Z',3,3);");
            Sql(connection, "INSERT INTO memory_session_checkpoints(surface,session_id,queue_json,updated_at_utc) VALUES('review','s-1','{\"index\":2}','2026-10-01T00:00:00Z');");
            Sql(connection, "INSERT INTO fsrs_manual_due_overrides(word_key,anchor_canonical_id,anchor_revision,due_at_utc,reason,created_at_utc) VALUES('w:anchor','c-1',3,'2026-10-05T00:00:00Z','manual','2026-10-01T00:00:00Z');");
            Sql(connection, "INSERT INTO scheduler_decisions(decision_id,word_key,canonical_id,timestamp_utc,final_interval_days,final_due_at_utc,scheduler_version) VALUES('d-1','w:anchor','c-1','2026-10-01T00:00:00Z',21,'2026-10-22T00:00:00Z','sched-1');");
            Sql(connection, "INSERT INTO mutation_outbox(kind,payload_json,created_at_utc,attempts) VALUES('json.snapshot.v1','{\"v\":1}','2026-10-01T00:00:00Z',0);");
        }
        before = Fingerprint(path);
        var beforeCounts = Counts(path);

        // 模拟"覆盖安装到 3.1.2 之后的全新一次启动"：同一条启动路径重新打开同一个库。
        using (var upgraded = new VocabularyService(path))
        {
            Check(upgraded.GetAllWords().Count == 3, "words were lost across the upgrade");
            var loaded = upgraded.LoadSettings();
            Check(loaded.Provider == settings.Provider && loaded.BaseUrl == settings.BaseUrl && loaded.Model == settings.Model,
                "AI settings were not preserved");
            Check(loaded.Theme == "Dark" && loaded.UiLanguage == "en" && loaded.Timeout == 42 && Math.Abs(loaded.GlassIntensity - 0.4) < 1e-9,
                "appearance or language settings were not preserved");
            Check(loaded.LookupShortcut == "K" && loaded.TranslateShortcut == "T" && loaded.QuoteShortcut == "Q",
                "shortcut settings were not preserved");
            Check(upgraded.DatabasePath == Path.GetFullPath(path), "upgrade opened a different database file");
        }
        Check(Fingerprint(path) == before, "user data or internal memory changed across the upgrade");
        Check(Counts(path) == beforeCounts, "a table gained or lost rows across the upgrade");
    }

    /// <summary>两个 JSON sidecar 是学习进度与计划的实际存放处，必须原样保留。</summary>
    private static void JsonSidecarsPreserved(string root)
    {
        var plansPath = Path.Combine(root, "daily-study-plans.json");
        var progressPath = Path.Combine(root, "ielts-learning.json");
        var store = new DailyStudyPlanStore(plansPath);
        var words = new List<DailyStudyPlanWord>
        {
            new() { Id = "w-1", Word = "alpha", Meaning = "首个" },
            new() { Id = "w-2", Word = "beta", Meaning = "次个" },
        };
        var plans = new List<DailyStudyPlan>
        {
            DailyStudyPlanRules.Create("每日复习", DailyStudyPlanSource.Archive, "词汇档案", words, 1, true, 4242)
        };
        store.Save(plans);
        var progress = new LearningProgress { SelectedSection = "02_人文", RoundSize = 33, TypingHints = false, LastShuffleSeed = 987654 };
        progress.Typed.Add("alpha");
        progress.Errors.Add("beta");
        progress.WritingDrafts[3] = "draft";
        progress.Save(progressPath);
        var plansHash = Hash(plansPath);
        var progressHash = Hash(progressPath);

        // 全新一次启动：重新读取，内容必须完全一致。
        var reloaded = new DailyStudyPlanStore(plansPath).Load();
        Check(reloaded.Count == 1, "daily study plans were lost");
        Check(reloaded[0].Name == "每日复习" && reloaded[0].Source == DailyStudyPlanSource.Archive
              && reloaded[0].DailyWordCount == 1 && reloaded[0].RandomOrder && reloaded[0].ShuffleSeed == 4242
              && reloaded[0].Status == DailyStudyPlanStatus.Active && reloaded[0].Words.Count == 2
              && reloaded[0].OriginalWordIds.Count == 2 && reloaded[0].Id == plans[0].Id,
            "daily study plan fields were not preserved");
        var reloadedProgress = LearningProgress.Load(progressPath);
        Check(reloadedProgress.SelectedSection == "02_人文" && reloadedProgress.RoundSize == 33 && !reloadedProgress.TypingHints
              && reloadedProgress.LastShuffleSeed == 987654 && reloadedProgress.Typed.Contains("alpha")
              && reloadedProgress.Errors.Contains("beta") && reloadedProgress.WritingDrafts[3] == "draft",
            "IELTS learning progress was not preserved");
        Check(Hash(plansPath) == plansHash && Hash(progressPath) == progressHash, "reading progress rewrote the sidecar files");
    }

    /// <summary>连续启动与重复迁移都不得重复导入、不得丢失。</summary>
    private static void RepeatedUpgradeIsIdempotent(string root)
    {
        var path = Path.Combine(root, "repeat.sqlite3");
        using (var store = new VocabularyService(path)) store.AddWord("once", "", "一次", "");
        var baseline = Counts(path);
        var fingerprint = Fingerprint(path);
        for (var launch = 0; launch < 3; launch++)
            using (var store = new VocabularyService(path))
                Check(store.GetAllWords().Count == 1, "re-launch duplicated or dropped words");
        Check(Counts(path) == baseline && Fingerprint(path) == fingerprint, "repeated launches changed stored data");

        // 旧路径上还有一份库时，不得再迁移一次，更不得覆盖现有库。
        var legacy = Path.Combine(root, "legacy.sqlite3");
        using (var old = new VocabularyService(legacy)) old.AddWord("legacy-only", "", "旧", "");
        var legacyHash = Hash(legacy);
        Check(!VocabularyService.TryMigrateDatabase(legacy, path, out var message), "migration must refuse an existing target");
        Check(message.Contains("已存在", StringComparison.Ordinal), "refusal must be explained, got: " + message);
        Check(Counts(path) == baseline, "refused migration still modified the current archive");
        Check(Hash(legacy) == legacyHash, "refused migration modified the legacy archive");

        // 中断残留（迁移临时文件）不得影响启动，也不得被当成数据。
        File.WriteAllText(Path.Combine(root, ".lexi-migration-interrupted.sqlite3"), "partial");
        using (var store = new VocabularyService(path)) Check(store.GetAllWords().Count == 1, "stale migration artifact broke startup");
        Check(Counts(path) == baseline && Fingerprint(path) == fingerprint, "stale migration artifact changed stored data");
    }

    /// <summary>
    /// 未来版本写过的库必须被明确拒绝，而不是被当成空库重建 —— 这也界定"回滚到旧版本"的限制。
    /// </summary>
    private static void FutureSchemaIsRejectedWithoutTouchingData(string root)
    {
        var path = Path.Combine(root, "future.sqlite3");
        using (var store = new VocabularyService(path)) store.AddWord("future", "", "未来", "");
        // schema_migrations.version 是主键，所以要整体替换成"未来版本"的单一记录。
        using (var connection = Open(path))
            Sql(connection, "DELETE FROM schema_migrations; INSERT INTO schema_migrations(version,applied_at) VALUES(99,'2026-10-07T00:00:00Z');");
        var before = Hash(path);
        SqliteConnection.ClearAllPools();
        var rejected = false;
        try { using var store = new VocabularyService(path); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "a database from a newer schema version must be refused, never silently recreated");
        SqliteConnection.ClearAllPools();
        Check(File.Exists(path), "rejected database was removed");
        Check(Hash(path) == before, "rejected database was modified");
        using (var verify = Open(path)) Check(Convert.ToInt32(Scalar(verify, "SELECT count(*) FROM words")) == 1, "rejected database lost its rows");
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>每个用户表的内容指纹：行数与逐行规范化文本，按表名与行内容排序 → 与物理顺序无关。</summary>
    private static string Fingerprint(string path)
    {
        using var connection = Open(path);
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var builder = new StringBuilder();
        foreach (var table in tables)
        {
            var rows = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM \"" + table + "\"";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var cells = new string[reader.FieldCount];
                    for (var index = 0; index < reader.FieldCount; index++)
                        cells[index] = reader.IsDBNull(index) ? "\u0000" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    rows.Add(string.Join('\u001f', cells));
                }
            }
            rows.Sort(StringComparer.Ordinal);
            builder.Append(table).Append(':').Append(rows.Count).Append('\n');
            foreach (var row in rows) builder.Append(row).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string Counts(string path)
    {
        using var connection = Open(path);
        var builder = new StringBuilder();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var table = reader.GetString(0);
                builder.Append(table).Append('=').Append(Scalar(connection, "SELECT count(*) FROM \"" + table + "\"")).Append(';');
            }
        }
        return builder.ToString();
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
