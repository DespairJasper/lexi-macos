using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lexi;

/// <summary>
/// 长期记忆层（schema v2）：9 张表的幂等迁移 + <see cref="ILearningMemoryStore"/> 实现。
///
/// 三条硬约定：
/// 1. 新增时间列一律 **UTC ISO-8601（"O" 格式，7 位小数 + 尾缀 Z）**，定长 28 字符。
///    因此到期队列可以直接用字符串比较代替时间解析（见 <see cref="QueryDue"/>）。
/// 2. 所有写操作复用既有的单一 <c>_connection</c>；多表写入包在**一个** SQLite 事务里，
///    失败时 ROLLBACK 并**原样抛出**，调用方据此不显示"完成"。
/// 3. 本文件**绝不**读写 <c>words</c> 表的任何列——<c>lexi_word_revision</c> 触发器会因此
///    自增 <c>word_archives.revision</c>，破坏既有复习队列判据。
/// </summary>
public sealed partial class VocabularyService
{
    /// <summary>长期记忆 schema 版本（写入 schema_migrations.version）。</summary>
    private const int MemorySchemaVersion = 2;

    // =====================================================================
    // 1. schema v2 迁移
    // =====================================================================

    /// <summary>
    /// 建立长期记忆的 9 张表与索引（全部 CREATE ... IF NOT EXISTS，幂等）。
    /// 由 <c>InitializeDatabase()</c> 在现行 MigrateArchiveSchema()/MigrateQuoteSchema() 之后调用。
    /// 整体包在一个事务里：任一步失败即回滚，不留半成品。
    /// 只新增表，不改动 words / review_logs / review_snapshots / word_archives / quotes / app_settings 的任何列。
    /// </summary>
    private void MigrateMemorySchema()
    {
        using var tx = _connection.BeginTransaction();
        try
        {
            using (var schema = _connection.CreateCommand())
            {
                schema.Transaction = tx;
                schema.CommandText = MemorySchemaSql;
                schema.ExecuteNonQuery();
            }

            using (var stamp = _connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText =
                    "INSERT INTO schema_migrations (version, applied_at) VALUES ($version, $appliedAt) ON CONFLICT(version) DO NOTHING;";
                stamp.Parameters.AddWithValue("$version", MemorySchemaVersion);
                stamp.Parameters.AddWithValue("$appliedAt", UtcText(DateTime.UtcNow));
                stamp.ExecuteNonQuery();
            }

            CaptureLegacyManagedDue(tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 9 张表的 DDL。列名与 MemoryModels 的 C# 属性名一一对应（snake_case）。
    /// 枚举一律以字符串落库（可读、可审计、加成员不会静默错位）。
    /// </summary>
    private const string MemorySchemaSql = """
        -- Additive durable round checkpoints (one unfinished session per UI surface).
        CREATE TABLE IF NOT EXISTS memory_session_checkpoints (
            surface TEXT PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES learning_sessions(session_id),
            queue_json TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        -- 1. 学习会话
        CREATE TABLE IF NOT EXISTS learning_sessions (
            session_id TEXT PRIMARY KEY,
            started_at_utc TEXT NOT NULL,
            ended_at_utc TEXT,
            mode TEXT NOT NULL DEFAULT 'Review',
            primary_source TEXT NOT NULL DEFAULT 'Archive',
            planned_word_count INTEGER NOT NULL DEFAULT 0,
            plan_id TEXT NOT NULL DEFAULT ''
        );

        -- 2. 原始交互事件：追加写入，永不删除（修正/撤销都以新事件表达）
        CREATE TABLE IF NOT EXISTS learning_events (
            event_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            word_key TEXT NOT NULL,
            presentation_id TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            learning_mode TEXT NOT NULL,
            kind TEXT NOT NULL,
            response TEXT,
            previous_response TEXT,
            session_appearance_index INTEGER NOT NULL DEFAULT 0,
            word_appearance_index INTEGER NOT NULL DEFAULT 0,
            recognition_count_before INTEGER NOT NULL DEFAULT 0,
            recognition_count_after INTEGER NOT NULL DEFAULT 0,
            is_first_appearance_for_word INTEGER NOT NULL DEFAULT 0,
            response_latency_ms INTEGER,
            supersedes_event_id TEXT,
            is_recall INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS idx_learning_events_session ON learning_events(session_id);
        CREATE INDEX IF NOT EXISTS idx_learning_events_word ON learning_events(word_key);
        CREATE INDEX IF NOT EXISTS idx_learning_events_presentation ON learning_events(presentation_id);

        -- 3. 一个 word × session 的轨迹摘要
        CREATE TABLE IF NOT EXISTS word_session_summaries (
            word_key TEXT NOT NULL,
            session_id TEXT NOT NULL,
            first_presented_at_utc TEXT NOT NULL,
            completed_at_utc TEXT,
            first_initial_response TEXT,
            first_validated_response TEXT,
            total_presentations INTEGER NOT NULL DEFAULT 0,
            final_known_count INTEGER NOT NULL DEFAULT 0,
            final_fuzzy_count INTEGER NOT NULL DEFAULT 0,
            final_forgotten_count INTEGER NOT NULL DEFAULT 0,
            reset_count INTEGER NOT NULL DEFAULT 0,
            response_revision_count INTEGER NOT NULL DEFAULT 0,
            known_to_fuzzy INTEGER NOT NULL DEFAULT 0,
            known_to_forgotten INTEGER NOT NULL DEFAULT 0,
            fuzzy_to_forgotten INTEGER NOT NULL DEFAULT 0,
            had_fuzzy INTEGER NOT NULL DEFAULT 0,
            had_forgotten INTEGER NOT NULL DEFAULT 0,
            had_response_revision INTEGER NOT NULL DEFAULT 0,
            max_known_streak INTEGER NOT NULL DEFAULT 0,
            presentations_to_mastery INTEGER NOT NULL DEFAULT 0,
            time_to_mastery_ms INTEGER,
            PRIMARY KEY (word_key, session_id)
        );

        -- 4. 一个 word × session 的唯一长期信号 + FSRS 前置状态（撤销时精确回放，不重算历史）
        --
        --    canonical 序号用 rowid：本表从不 DELETE（撤销只把 invalidated 置 1），
        --    故 rowid 严格单调递增，可作为 fsrs_cards.last_applied_canonical_seq 的水位。
        --    difficulty/... 这 8 列即 FsrsPreState 的 Difficulty/Stability/Reps/Lapses/
        --    State/LastReviewAtUtc/NextReviewAtUtc/LastCanonicalRating，语义上是"前置值"，
        --    只在本表内出现，不与 fsrs_cards 的同名列冲突。
        CREATE TABLE IF NOT EXISTS canonical_reviews (
            canonical_id TEXT PRIMARY KEY,
            word_key TEXT NOT NULL,
            session_id TEXT NOT NULL,
            source_presentation_id TEXT,
            origin TEXT NOT NULL,
            rating TEXT NOT NULL,
            reviewed_at_utc TEXT NOT NULL,
            completed_at_utc TEXT NOT NULL,
            aggregation_policy_version TEXT NOT NULL DEFAULT '',
            revision INTEGER NOT NULL DEFAULT 0,
            invalidated INTEGER NOT NULL DEFAULT 0,
            invalidated_at_utc TEXT,
            invalidation_reason TEXT NOT NULL DEFAULT '',
            supersedes_canonical_id TEXT,
            pre_state_existed INTEGER NOT NULL DEFAULT 0,
            difficulty REAL NOT NULL DEFAULT 0,
            stability REAL NOT NULL DEFAULT 0,
            reps INTEGER NOT NULL DEFAULT 0,
            lapses INTEGER NOT NULL DEFAULT 0,
            state TEXT NOT NULL DEFAULT 'New',
            last_review_at_utc TEXT,
            next_review_at_utc TEXT,
            last_canonical_rating TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_canonical_reviews_word_session ON canonical_reviews(word_key, session_id);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_canonical_active ON canonical_reviews(word_key, session_id) WHERE invalidated = 0;

        -- 5. 每个词持久化的 FSRS 卡片状态
        CREATE TABLE IF NOT EXISTS fsrs_cards (
            word_key TEXT PRIMARY KEY,
            difficulty REAL,
            stability REAL,
            reps INTEGER,
            lapses INTEGER,
            state TEXT,
            last_review_at_utc TEXT,
            next_review_at_utc TEXT,
            last_canonical_rating TEXT,
            fsrs_algorithm_version TEXT,
            fsrs_library_version TEXT,
            fsrs_parameter_version TEXT,
            last_applied_canonical_seq INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_fsrs_cards_due ON fsrs_cards(next_review_at_utc);

        -- 6. 作答前的不可变特征快照（捕获后不修改；label 在定稿时才回填）
        CREATE TABLE IF NOT EXISTS context_snapshots (
            snapshot_id TEXT PRIMARY KEY,
            word_key TEXT NOT NULL,
            session_id TEXT NOT NULL,
            presentation_id TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            fsrs_difficulty_at_capture REAL NOT NULL DEFAULT 0,
            fsrs_stability_at_capture REAL NOT NULL DEFAULT 0,
            fsrs_retrievability_at_capture REAL NOT NULL DEFAULT 0,
            feature_schema_version TEXT NOT NULL DEFAULT '',
            model_version TEXT NOT NULL DEFAULT '',
            features_json TEXT NOT NULL DEFAULT '{}',
            missing_flags_json TEXT NOT NULL DEFAULT '[]',
            label TEXT,
            confident_recall INTEGER,
            labeled_at_utc TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_context_snapshots_presentation ON context_snapshots(presentation_id);
        CREATE INDEX IF NOT EXISTS idx_context_snapshots_word ON context_snapshots(word_key);
        CREATE INDEX IF NOT EXISTS idx_context_snapshots_captured ON context_snapshots(captured_at_utc);

        -- 7. 每次长期排期决策的完整审计记录
        CREATE TABLE IF NOT EXISTS scheduler_decisions (
            decision_id TEXT PRIMARY KEY,
            word_key TEXT NOT NULL,
            canonical_id TEXT NOT NULL,
            timestamp_utc TEXT NOT NULL,
            baseline_interval_days REAL NOT NULL DEFAULT 0,
            baseline_retrievability REAL NOT NULL DEFAULT 0,
            baseline_difficulty REAL NOT NULL DEFAULT 0,
            baseline_stability REAL NOT NULL DEFAULT 0,
            context_mode TEXT NOT NULL DEFAULT 'Disabled',
            context_delta REAL,
            context_candidate_interval_days REAL,
            final_interval_days REAL NOT NULL DEFAULT 0,
            final_due_at_utc TEXT NOT NULL,
            desired_retention REAL NOT NULL DEFAULT 0,
            fsrs_algorithm_version TEXT NOT NULL DEFAULT '',
            fsrs_library_version TEXT NOT NULL DEFAULT '',
            fsrs_parameter_version TEXT NOT NULL DEFAULT '',
            aggregation_policy_version TEXT NOT NULL DEFAULT '',
            trajectory_schema_version TEXT NOT NULL DEFAULT '',
            context_feature_schema_version TEXT NOT NULL DEFAULT '',
            context_model_version TEXT NOT NULL DEFAULT '',
            scheduler_version TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_scheduler_decisions_word_time ON scheduler_decisions(word_key, timestamp_utc DESC);

        -- 8. 个人化模型状态：Context 与 FSRS 个人参数共用一张表，用 model_kind 区分
        --    （'context' = Context 校准模型；'fsrs_params' = FSRS 个人参数）。
        CREATE TABLE IF NOT EXISTS personalization_models (
            model_version TEXT PRIMARY KEY,
            model_kind TEXT NOT NULL,
            trained_at_utc TEXT NOT NULL,
            train_cutoff_utc TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'ColdStart',
            coefficients_json TEXT NOT NULL DEFAULT '[]',
            scaler_json TEXT NOT NULL DEFAULT '{}',
            metrics_json TEXT NOT NULL DEFAULT '{}',
            last_good_model_version TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_personalization_models_kind_time ON personalization_models(model_kind, trained_at_utc DESC);

        -- 8b. 手动 due 覆盖（管理动作对 fsrs_cards.next_review_at_utc 的显式改写留痕）
        --
        --    为什么必须有它：管理动作（stage / restart / 批量"完成复习"）会把卡片 due 改成用户指定的
        --    本地日；而参数换版要求整库按新权重重放 due。没有留痕的话，重放会把用户显式设置的日期
        --    悄悄改回模型日期——"手动调整无效"。旧 words 列证明不了这件事：真正决定到期资格的是
        --    fsrs_cards.next_review_at_utc（QueryDue 只读它）。
        --
        --    为什么 append-only 且带 revision 锚点：覆盖必须绑定到**当时那条 canonical**
        --    （canonical_id + revision）。撤销回放到那个锚点时覆盖自然重新生效，而
        --    "同 rowid 被 revive/改判" 会因为 revision 不同而不会误用旧覆盖。
        --    追加写而不是原地更新，是为了让"最新一条覆盖"永远可判定（按 override_id 取 MAX）。
        --    注意：一致性签名**不是**靠 COUNT/SUM 聚合量察觉变化（那套口径已被逐行定界 SHA256 取代，
        --    见 ComputeCanonicalSignature），聚合量无法表达集合身份。
        --
        --    刻意不升 MemorySchemaVersion：本文件的 DDL 每次启动都整段幂等执行
        --    （CREATE ... IF NOT EXISTS），新表在老库上会被自动补建；而升版本号会撞上
        --    DatabaseSafety 与 Archive 的 `version > 2 → throw` 门禁（非本工作流拥有的文件）。
        CREATE TABLE IF NOT EXISTS fsrs_manual_due_overrides (
            override_id INTEGER PRIMARY KEY AUTOINCREMENT,
            word_key TEXT NOT NULL,
            anchor_canonical_id TEXT NOT NULL,
            anchor_revision INTEGER NOT NULL,
            due_at_utc TEXT NOT NULL,
            reason TEXT NOT NULL DEFAULT '',
            created_at_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_fsrs_manual_due_anchor
            ON fsrs_manual_due_overrides(word_key, anchor_canonical_id, anchor_revision, override_id DESC);

        -- 9. 跨存储变更的待应用条目（不声称跨存储原子，只保证可重放 + 最终一致）
        CREATE TABLE IF NOT EXISTS mutation_outbox (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            applied_at_utc TEXT,
            attempts INTEGER NOT NULL DEFAULT 0,
            last_error TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_mutation_outbox_pending ON mutation_outbox(applied_at_utc, id);
        """;

    // =====================================================================
    // 2. 时间与枚举的读写口径（UTC ISO-8601 "O"）
    // =====================================================================

    /// <summary>
    /// 写入：统一 UTC ISO-8601 "O"（定长 28 字符，尾缀 Z）。
    /// Kind 口径与 <c>Fsrs6Scheduler.NormalizeUtc</c> **完全一致**（评审 P1-8）：
    /// Utc 原样、Local 按本机时区换算、<b>Unspecified 一律按 UTC 解释</b>（不猜测本机时区）。
    /// 本层所有时间戳的生产者都写 UTC，所以把 Unspecified 当本地会让同一个 DateTime
    /// 在两个入口得到相差一个时区偏移的不同时刻，且不报错。
    /// </summary>
    /// <summary>
    /// 手动管理动作（stage / restart）把一个档案词显式排到本地某一天时，同步该词 FSRS 卡的 due。
    /// </summary>
    /// <remarks>
    /// 有卡片的词，排期的唯一来源是 <c>fsrs_cards.next_review_at_utc</c>（规格书 §6），
    /// 旧列只是兼容投影。如果手动动作只写旧列，用户看到的就是"手动调整无效"——这正是必须闭环的地方。
    /// 这是**显式手动覆盖**，与评分路径无关：评分路径仍然不得覆盖 due。
    /// 没有档案行 / 没有卡时 UPDATE 匹配 0 行，不建卡、不动卡，行为与改动前逐字节一致。
    /// </remarks>
    internal void SyncCardDueToLocalDate(SqliteTransaction tx, long wordId, DateTime localDate) =>
        SyncCardDueToLocalDate(tx, wordId, localDate, "manual-stage");

    /// <inheritdoc cref="SyncCardDueToLocalDate(SqliteTransaction, long, DateTime)"/>
    /// <param name="reason">管理动作来源（只进审计，不参与任何数值判断）。</param>
    internal void SyncCardDueToLocalDate(SqliteTransaction tx, long wordId, DateTime localDate, string reason)
    {
        using var keyCmd = _connection.CreateCommand();
        keyCmd.Transaction = tx;
        keyCmd.CommandText = "SELECT uuid FROM word_archives WHERE word_id = $id";
        keyCmd.Parameters.AddWithValue("$id", wordId);
        if (keyCmd.ExecuteScalar() is not string uuid || string.IsNullOrWhiteSpace(uuid)) return;
        var wordKey = WordKey.Archive(uuid).Key;
        var dueText = UtcText(localDate);
        int affected;
        using (var cardCmd = _connection.CreateCommand())
        {
            cardCmd.Transaction = tx;
            cardCmd.CommandText = "UPDATE fsrs_cards SET next_review_at_utc = $utc WHERE word_key = $wordKey";
            cardCmd.Parameters.AddWithValue("$utc", dueText);
            cardCmd.Parameters.AddWithValue("$wordKey", wordKey);
            affected = cardCmd.ExecuteNonQuery();
        }
        // 没有卡（affected == 0）→ 保持既有行为：只写旧列，不建卡、不留痕。
        if (affected == 0) return;
        // 锚点 = 卡片当前的幂等水位指向的那条 canonical（没有水位则回落该词最新有效 canonical）。
        // 用 (canonical_id, revision) 而不是 rowid：行可以被 revive/改判，revision 会变而 rowid 不变，
        // 只按 rowid 匹配会让新的一次改判误用旧的覆盖值。
        using var anchorCmd = _connection.CreateCommand();
        anchorCmd.Transaction = tx;
        anchorCmd.CommandText = """
            SELECT canonical_id, revision FROM canonical_reviews
            WHERE word_key = $wordKey AND invalidated = 0
            ORDER BY CASE WHEN rowid = (
                       SELECT last_applied_canonical_seq FROM fsrs_cards WHERE word_key = $wordKey)
                     THEN 0 ELSE 1 END, rowid DESC
            LIMIT 1;
            """;
        anchorCmd.Parameters.AddWithValue("$wordKey", wordKey);
        string? anchorId = null;
        long anchorRevision = 0;
        using (var reader = anchorCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                anchorId = reader.GetString(0);
                anchorRevision = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            }
        }
        // 没有 canonical 就没有"学习事实"可锚定：该词的 due 完全由管理动作决定，
        // 而重放只处理有有效 canonical 的词，因此它不会被重放覆盖，无需留痕。
        if (anchorId is null) return;
        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO fsrs_manual_due_overrides
                (word_key, anchor_canonical_id, anchor_revision, due_at_utc, reason, created_at_utc)
            VALUES ($wordKey, $anchorId, $anchorRevision, $due, $reason, $createdAt);
            """;
        insert.Parameters.AddWithValue("$wordKey", wordKey);
        insert.Parameters.AddWithValue("$anchorId", anchorId);
        insert.Parameters.AddWithValue("$anchorRevision", anchorRevision);
        insert.Parameters.AddWithValue("$due", dueText);
        insert.Parameters.AddWithValue("$reason", reason ?? "");
        insert.Parameters.AddWithValue("$createdAt", UtcText(DateTime.UtcNow));
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// 读出全部手动 due 覆盖（每 (word, anchor, revision) 取最新一条）。
    /// 表很小（只有用户显式管理动作才写入），因此整表读出即可，无需按词轮询。
    /// </summary>
    public IReadOnlyList<FsrsManualDueOverride> LoadManualDueOverrides()
    {
        var list = new List<FsrsManualDueOverride>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT o.word_key, o.anchor_canonical_id, o.anchor_revision, o.due_at_utc
            FROM fsrs_manual_due_overrides o
            WHERE o.override_id = (
                SELECT MAX(i.override_id) FROM fsrs_manual_due_overrides i
                WHERE i.word_key = o.word_key AND i.anchor_canonical_id = o.anchor_canonical_id
                  AND i.anchor_revision = o.anchor_revision)
            ORDER BY o.word_key, o.anchor_canonical_id;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FsrsManualDueOverride(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0 : r.GetInt64(2), UtcParse(r.GetString(3))));
        }
        return list;
    }

    /// <summary>
    /// 审计用：有多少张**有有效 canonical** 的卡片，其当前 due 与「它最后一条 canonical 的决策所隐含的
    /// due」不一致，且没有对应的手动覆盖留痕。
    /// <para>这是"无法归因的 due 差异"的显式计数——只用于在发布前**把风险说出来**，绝不据此猜测
    /// 哪些是手动设置（迁移前的旧库没有来源标记，猜错会静默改写用户的排期）。</para>
    /// </summary>
    public int CountCardsWithUnattributedDue()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM fsrs_cards c
            JOIN canonical_reviews r ON r.rowid=c.last_applied_canonical_seq AND r.word_key=c.word_key
            WHERE r.invalidated=0 AND c.next_review_at_utc IS NOT NULL
              AND r.rowid=(SELECT MAX(r2.rowid) FROM canonical_reviews r2
                           WHERE r2.word_key=c.word_key AND r2.invalidated=0)
              AND NOT EXISTS (SELECT 1 FROM fsrs_manual_due_overrides o
                              WHERE o.word_key=c.word_key AND o.anchor_canonical_id=r.canonical_id
                                AND o.anchor_revision=r.revision)
              AND c.next_review_at_utc <> (
                    SELECT d.final_due_at_utc FROM scheduler_decisions d
                    WHERE d.word_key=c.word_key AND d.canonical_id=r.canonical_id
                    ORDER BY d.rowid DESC LIMIT 1);
            """;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>该库是否出现过任何一次个人参数发布（recordKind=params 的 fsrs_params 行）。</summary>
    public bool HasPublishedPersonalParameters()
    {
        // 用参数拼 LIKE 模式而不是把引号写进内插字符串：后者在 C# 里会被当成字符串结束符。
        var pattern = "%\"" + SchedulingConfig.FsrsParameterRecordKindKey + "\":\""
            + SchedulingConfig.FsrsParameterRecordKindParams + "\"%";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM personalization_models WHERE model_kind = $kind AND metrics_json LIKE $pattern LIMIT 1";
        cmd.Parameters.AddWithValue("$kind", SchedulingConfig.FsrsParameterModelKind);
        cmd.Parameters.AddWithValue("$pattern", pattern);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// 手动「完成本次复习」的**显式覆盖**：把每个词的 FSRS 卡 due 同步到刚刚写好的兼容投影日期。
    /// </summary>
    /// <remarks>
    /// 只有界面上的手动批量动作调用它。评分路径写同一个旧列投影（<c>ApplyReviewRating</c> →
    /// <c>ExecuteBatch("review")</c>）时**绝不**调用——那里排期已经由 canonical 决定，
    /// 再按旧阶段覆盖就是把 FSRS 顶掉（规格书 §6 明确禁止评分路径覆盖 due）。
    /// </remarks>
    internal void SyncManualReviewDue(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        using var tx = _connection.BeginTransaction();
        foreach (var id in ids.Distinct())
        {
            using var read = _connection.CreateCommand();
            read.Transaction = tx;
            read.CommandText = "SELECT next_review_date FROM words WHERE id = $id";
            read.Parameters.AddWithValue("$id", id);
            if (read.ExecuteScalar() is not string next || string.IsNullOrWhiteSpace(next)) continue;
            if (!DateTime.TryParseExact(next, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed)) continue;
            // 兼容投影是**本地日历日**；TryParseExact 返回 Unspecified，而 UtcText 会把
            // Unspecified 当成 UTC。必须显式标成 Local，否则手动覆盖会整体偏一个时区偏移。
            SyncCardDueToLocalDate(tx, id, DateTime.SpecifyKind(parsed, DateTimeKind.Local), "manual-review-complete");
        }
        tx.Commit();
    }

    private static string UtcText(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeKind.Local => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
    };

    private static string? UtcTextOrNull(DateTime? value) => value.HasValue ? UtcText(value.Value) : null;

    /// <summary>读取：RoundtripKind 解析后转 UTC。与 <see cref="UtcText"/> 严格互逆。</summary>
    private static DateTime UtcParse(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static string EnumText<T>(T value) where T : struct, Enum => value.ToString();

    private static string? EnumTextOrNull<T>(T? value) where T : struct, Enum => value?.ToString();

    private static T EnumParse<T>(string? text, T fallback) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<T>(text, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static T? EnumParseOrNull<T>(string? text) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<T>(text, ignoreCase: true, out var parsed) ? parsed : null;

    private static string? OptText(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? OptUtc(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : UtcParse(reader.GetString(ordinal));

    private static long? OptInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? OptInt32(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : (int)reader.GetInt64(ordinal);

    private static double? OptDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static bool BoolOf(SqliteDataReader reader, int ordinal) => !reader.IsDBNull(ordinal) && reader.GetInt64(ordinal) != 0;

    private static bool? OptBool(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;

    private static void BindNull(SqliteCommand cmd, string name) => cmd.Parameters.AddWithValue(name, DBNull.Value);

    /// <summary>空字符串一律写成 NULL（可选文本列的统一口径）。</summary>
    private static void BindTextOrNull(SqliteCommand cmd, string name, string? value) =>
        cmd.Parameters.AddWithValue(name, string.IsNullOrEmpty(value) ? DBNull.Value : value);

    private static void BindUtcOrNull(SqliteCommand cmd, string name, DateTime? value) =>
        cmd.Parameters.AddWithValue(name, value.HasValue ? UtcText(value.Value) : DBNull.Value);

    /// <summary>
    /// 低频 / 破坏性写操作统一收尾：提交后刷新一次自动备份（沿用既有约定）。
    /// 备份失败只设 BackupWarning，绝不回滚已提交的数据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>哪些写路径**不**调用它（评审 P1-7，性能回归）</b>：raw 事件（<see cref="AppendEvent"/>）、
    /// 作答前快照（<see cref="SaveContextSnapshot"/>）、决策日志（<see cref="SaveSchedulerDecision"/>）。
    /// 这三条是**追加型审计数据**，一次真实复习卡（呈现 + 3 次回忆作答 + 1 次改判 + 1 次定稿）
    /// 会产生 8–10 次写；每次写都做一次「全库拷贝 + 全库 JSON 校验」
    /// （完整 SQLite 备份发生在 UI 线程，不能在每次进度变化时执行）
    /// 线性放大成明显卡顿。
    /// </para>
    /// <para>
    /// 理由：这些行只追加、从不改写既有行，主库本身 <c>synchronous = FULL</c> 已提交；
    /// 而全库轮转备份面向的是「用户可见数据的完整性」——那由定稿
    /// （<see cref="CommitWordSession"/>，它才是同时改写 canonical + 卡片 + summary 的地方）
    /// 和撤销 / 个人化模型等低频路径负责。因此高频追加型写不再触发备份，
    /// 一次完整卡片流程只在**定稿**时备份一次。
    /// </para>
    /// <para>
    /// 例外：outbox 簿记（MarkMutationApplied / RecordMutationFailure）不改动用户数据，也不触发备份。
    /// </para>
    /// </remarks>
    private void BackupAfterMemoryWrite() => BackupCommittedState();

    // =====================================================================
    // 3. 轨迹（append-only）
    // =====================================================================

    private const string EventColumns =
        "event_id, session_id, word_key, presentation_id, occurred_at_utc, learning_mode, kind, response, previous_response, " +
        "session_appearance_index, word_appearance_index, recognition_count_before, recognition_count_after, " +
        "is_first_appearance_for_word, response_latency_ms, supersedes_event_id, is_recall";

    private static void InsertEvent(SqliteConnection connection, SqliteTransaction tx, LearningInteractionEvent e)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(e.EventId);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO learning_events (
                event_id, session_id, word_key, presentation_id, occurred_at_utc, learning_mode, kind, response, previous_response,
                session_appearance_index, word_appearance_index, recognition_count_before, recognition_count_after,
                is_first_appearance_for_word, response_latency_ms, supersedes_event_id, is_recall
            ) VALUES (
                $eventId, $sessionId, $wordKey, $presentationId, $occurredAt, $mode, $kind, $response, $previousResponse,
                $sessionIndex, $wordIndex, $recognitionBefore, $recognitionAfter,
                $firstAppearance, $latency, $supersedes, $isRecall
            )
            ON CONFLICT(event_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$eventId", e.EventId);
        cmd.Parameters.AddWithValue("$sessionId", e.SessionId);
        cmd.Parameters.AddWithValue("$wordKey", e.WordKey);
        cmd.Parameters.AddWithValue("$presentationId", e.PresentationId);
        cmd.Parameters.AddWithValue("$occurredAt", UtcText(e.OccurredAtUtc));
        cmd.Parameters.AddWithValue("$mode", EnumText(e.LearningMode));
        cmd.Parameters.AddWithValue("$kind", EnumText(e.Kind));
        BindTextOrNull(cmd, "$response", EnumTextOrNull(e.Response));
        BindTextOrNull(cmd, "$previousResponse", EnumTextOrNull(e.PreviousResponse));
        cmd.Parameters.AddWithValue("$sessionIndex", e.SessionAppearanceIndex);
        cmd.Parameters.AddWithValue("$wordIndex", e.WordAppearanceIndex);
        cmd.Parameters.AddWithValue("$recognitionBefore", e.RecognitionCountBefore);
        cmd.Parameters.AddWithValue("$recognitionAfter", e.RecognitionCountAfter);
        cmd.Parameters.AddWithValue("$firstAppearance", e.IsFirstAppearanceForWord ? 1 : 0);
        cmd.Parameters.AddWithValue("$latency", e.ResponseLatencyMs.HasValue ? e.ResponseLatencyMs.Value : DBNull.Value);
        BindTextOrNull(cmd, "$supersedes", e.SupersedesEventId);
        cmd.Parameters.AddWithValue("$isRecall", e.IsRecall ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static LearningInteractionEvent ReadEvent(SqliteDataReader r) => new()
    {
        EventId = r.GetString(0),
        SessionId = r.GetString(1),
        WordKey = r.GetString(2),
        PresentationId = r.GetString(3),
        OccurredAtUtc = UtcParse(r.GetString(4)),
        LearningMode = EnumParse(OptText(r, 5), LearningMode.FirstLearn),
        Kind = EnumParse(OptText(r, 6), InteractionEventKind.Presented),
        Response = EnumParseOrNull<StudyRating>(OptText(r, 7)),
        PreviousResponse = EnumParseOrNull<StudyRating>(OptText(r, 8)),
        SessionAppearanceIndex = (int)r.GetInt64(9),
        WordAppearanceIndex = (int)r.GetInt64(10),
        RecognitionCountBefore = (int)r.GetInt64(11),
        RecognitionCountAfter = (int)r.GetInt64(12),
        IsFirstAppearanceForWord = BoolOf(r, 13),
        ResponseLatencyMs = OptInt32(r, 14),
        SupersedesEventId = OptText(r, 15),
        IsRecall = BoolOf(r, 16),
    };

    /// <summary>追加一条原始事件。重复 EventId 被静默忽略（幂等）。</summary>
    public void AppendEvent(LearningInteractionEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                InsertEvent(_connection, tx, e);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        // 追加型审计数据：主库已 synchronous=FULL 提交，不在此触发全库备份（评审 P1-7）。
    }

    public IReadOnlyList<LearningInteractionEvent> LoadEvents(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var list = new List<LearningInteractionEvent>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {EventColumns} FROM learning_events WHERE session_id = $sessionId ORDER BY occurred_at_utc ASC, rowid ASC";
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEvent(r));
        return list;
    }

    public IReadOnlyList<LearningInteractionEvent> LoadEventsForWord(string wordKey)
    {
        ArgumentNullException.ThrowIfNull(wordKey);
        var list = new List<LearningInteractionEvent>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {EventColumns} FROM learning_events WHERE word_key = $wordKey ORDER BY occurred_at_utc ASC, rowid ASC";
        cmd.Parameters.AddWithValue("$wordKey", wordKey);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEvent(r));
        return list;
    }

    // =====================================================================
    // 4. 会话
    // =====================================================================

    public void UpsertSession(LearningSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                UpsertSession(_connection, tx, session);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        BackupAfterMemoryWrite();
    }

    private static void UpsertSession(SqliteConnection connection, SqliteTransaction tx, LearningSession session)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO learning_sessions (session_id, started_at_utc, ended_at_utc, mode, primary_source, planned_word_count, plan_id)
            VALUES ($sessionId, $startedAt, $endedAt, $mode, $source, $planned, $planId)
            ON CONFLICT(session_id) DO UPDATE SET
                started_at_utc = excluded.started_at_utc,
                ended_at_utc = excluded.ended_at_utc,
                mode = excluded.mode,
                primary_source = excluded.primary_source,
                planned_word_count = excluded.planned_word_count,
                plan_id = excluded.plan_id;
            """;
        cmd.Parameters.AddWithValue("$sessionId", session.SessionId);
        cmd.Parameters.AddWithValue("$startedAt", UtcText(session.StartedAtUtc));
        BindUtcOrNull(cmd, "$endedAt", session.EndedAtUtc);
        cmd.Parameters.AddWithValue("$mode", EnumText(session.Mode));
        cmd.Parameters.AddWithValue("$source", EnumText(session.PrimarySource));
        cmd.Parameters.AddWithValue("$planned", session.PlannedWordCount);
        cmd.Parameters.AddWithValue("$planId", session.PlanId);
        cmd.ExecuteNonQuery();
    }

    public LearningSession? GetSession(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT session_id, started_at_utc, ended_at_utc, mode, primary_source, planned_word_count, plan_id FROM learning_sessions WHERE session_id = $sessionId";
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new LearningSession
        {
            SessionId = r.GetString(0),
            StartedAtUtc = UtcParse(r.GetString(1)),
            EndedAtUtc = OptUtc(r, 2),
            Mode = EnumParse(OptText(r, 3), StudyMode.Review),
            PrimarySource = EnumParse(OptText(r, 4), WordSource.Archive),
            PlannedWordCount = (int)r.GetInt64(5),
            PlanId = r.GetString(6),
        };
    }

    // =====================================================================
    // 5. 定稿：单事务原子写入
    // =====================================================================

    /// <summary>
    /// 该词本轮定稿的**唯一**写入点。全部组成部分在一个 SQLite 事务里落盘，任一步失败即整单回滚并原样抛出。
    /// 幂等键 = CanonicalId（确定性 "cr:&lt;wordKey&gt;:&lt;sessionId&gt;"）：重复提交（双击 / 重试 / 重绘）
    /// 直接返回 AlreadyApplied，不重复应用 FSRS、不再写决策与 outbox。
    /// </summary>
    public MemorySessionCheckpoint? GetSessionCheckpoint(string surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT surface, session_id, queue_json, updated_at_utc FROM memory_session_checkpoints WHERE surface = $surface";
        cmd.Parameters.AddWithValue("$surface", surface);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new MemorySessionCheckpoint(r.GetString(0), r.GetString(1), r.GetString(2), UtcParse(r.GetString(3))) : null;
    }

    private static void UpsertSessionCheckpoint(SqliteConnection connection, SqliteTransaction tx, MemorySessionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.Surface);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.SessionId);
        using var json = JsonDocument.Parse(checkpoint.QueueJson);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("学习检查点必须是 JSON 对象。");
        using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_session_checkpoints (surface, session_id, queue_json, updated_at_utc)
            SELECT $surface, $session, $json, $updated
            WHERE EXISTS (SELECT 1 FROM learning_sessions WHERE session_id=$session AND ended_at_utc IS NULL)
            ON CONFLICT(surface) DO UPDATE SET session_id=excluded.session_id,
                queue_json=excluded.queue_json, updated_at_utc=excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("$surface", checkpoint.Surface);
        cmd.Parameters.AddWithValue("$session", checkpoint.SessionId);
        cmd.Parameters.AddWithValue("$json", checkpoint.QueueJson);
        cmd.Parameters.AddWithValue("$updated", UtcText(checkpoint.UpdatedAtUtc));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("学习会话不存在或已结束，无法保存检查点。");
    }

    public void SaveSessionCheckpoint(MemorySessionCheckpoint checkpoint)
    {
        using var tx = _connection.BeginTransaction();
        try { UpsertSessionCheckpoint(_connection, tx, checkpoint); tx.Commit(); }
        catch { tx.Rollback(); throw; }
        // High-frequency checkpoints do not copy the entire vocabulary database.
    }

    public void AppendEventWithCheckpoint(LearningInteractionEvent e, MemorySessionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(e); ArgumentNullException.ThrowIfNull(checkpoint);
        if (e.SessionId != checkpoint.SessionId) throw new InvalidDataException("事件与检查点会话不一致。");
        using var tx = _connection.BeginTransaction();
        try
        {
            InsertEvent(_connection, tx, e);
            UpsertSessionCheckpoint(_connection, tx, checkpoint);
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    public void DeleteSessionCheckpoint(string surface, string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        using var tx = _connection.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM memory_session_checkpoints WHERE surface=$surface AND ($session IS NULL OR session_id=$session)";
            cmd.Parameters.AddWithValue("$surface", surface);
            cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
            cmd.ExecuteNonQuery(); tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    public WordSessionCommitResult CommitWordSession(WordSessionCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var canonical = commit.Canonical ?? throw new ArgumentNullException(nameof(commit), "定稿载荷缺少 canonical。");
        ArgumentException.ThrowIfNullOrWhiteSpace(canonical.CanonicalId);
        if (commit.Checkpoint is { } cp && cp.SessionId != canonical.SessionId)
            throw new InvalidDataException("定稿与检查点会话不一致。");

        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                // 步骤 1：幂等探针。已存在且未失效 → 直接判定为重放，不做任何写入。
                using (var probe = _connection.CreateCommand())
                {
                    probe.Transaction = tx;
                    probe.CommandText = "SELECT invalidated FROM canonical_reviews WHERE canonical_id = $id";
                    probe.Parameters.AddWithValue("$id", canonical.CanonicalId);
                    if (probe.ExecuteScalar() is long invalidated && invalidated == 0)
                    {
                        tx.Rollback();
                        return new WordSessionCommitResult(false, true, canonical.CanonicalId);
                    }
                }

                // 步骤 2：尚未落库的原始事件逐条追加（重复 EventId 忽略；轨迹不可变）。
                foreach (var pending in commit.PendingEvents ?? [])
                    InsertEvent(_connection, tx, pending);

                // 步骤 3：会话
                if (commit.Session != null) UpsertSession(_connection, tx, commit.Session);

                // 步骤 4：word × session 轨迹摘要
                if (commit.Summary != null) UpsertSummary(_connection, tx, commit.Summary);

                // 步骤 5：canonical（含 pre-state 列）
                UpsertCanonical(_connection, tx, canonical, commit.CardPreState);

                // canonical 序号（rowid）：撤销时用它判断"上一条未失效 canonical"。
                long canonicalSeq;
                using (var seqCmd = _connection.CreateCommand())
                {
                    seqCmd.Transaction = tx;
                    seqCmd.CommandText = "SELECT rowid FROM canonical_reviews WHERE canonical_id = $id";
                    seqCmd.Parameters.AddWithValue("$id", canonical.CanonicalId);
                    canonicalSeq = Convert.ToInt64(seqCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                // 步骤 6：FSRS 卡片 + 幂等水位
                if (commit.CardAfter != null) UpsertCard(_connection, tx, commit.CardAfter, canonicalSeq);

                // 步骤 7：快照 upsert（不覆盖已有 label）+ 标签回填
                foreach (var snapshot in commit.SnapshotsToSave ?? [])
                    UpsertSnapshot(_connection, tx, snapshot);
                ApplyLabels(_connection, tx, commit.SnapshotLabels, canonical.CompletedAtUtc);

                // 步骤 8：决策审计
                if (commit.Decision != null) InsertDecision(_connection, tx, commit.Decision);

                // 步骤 9：跨存储 outbox
                EnqueueMutations(_connection, tx, commit.OutboxMutations, DateTime.UtcNow);
                if (commit.Checkpoint is { } checkpoint)
                    UpsertSessionCheckpoint(_connection, tx, checkpoint);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        // 步骤 10：已提交，再刷新自动备份（失败只设 BackupWarning，不回滚）。
        BackupAfterMemoryWrite();
        return new WordSessionCommitResult(true, false, canonical.CanonicalId);
    }

    private static void UpsertSummary(SqliteConnection connection, SqliteTransaction tx, WordSessionSummary s)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO word_session_summaries (
                word_key, session_id, first_presented_at_utc, completed_at_utc,
                first_initial_response, first_validated_response,
                total_presentations, final_known_count, final_fuzzy_count, final_forgotten_count,
                reset_count, response_revision_count, known_to_fuzzy, known_to_forgotten, fuzzy_to_forgotten,
                had_fuzzy, had_forgotten, had_response_revision, max_known_streak,
                presentations_to_mastery, time_to_mastery_ms
            ) VALUES (
                $wordKey, $sessionId, $firstPresentedAt, $completedAt,
                $firstInitial, $firstValidated,
                $total, $known, $fuzzy, $forgotten,
                $reset, $revisions, $knownToFuzzy, $knownToForgotten, $fuzzyToForgotten,
                $hadFuzzy, $hadForgotten, $hadRevision, $maxStreak,
                $toMastery, $timeToMastery
            )
            ON CONFLICT(word_key, session_id) DO UPDATE SET
                first_presented_at_utc = excluded.first_presented_at_utc,
                completed_at_utc = excluded.completed_at_utc,
                first_initial_response = excluded.first_initial_response,
                first_validated_response = excluded.first_validated_response,
                total_presentations = excluded.total_presentations,
                final_known_count = excluded.final_known_count,
                final_fuzzy_count = excluded.final_fuzzy_count,
                final_forgotten_count = excluded.final_forgotten_count,
                reset_count = excluded.reset_count,
                response_revision_count = excluded.response_revision_count,
                known_to_fuzzy = excluded.known_to_fuzzy,
                known_to_forgotten = excluded.known_to_forgotten,
                fuzzy_to_forgotten = excluded.fuzzy_to_forgotten,
                had_fuzzy = excluded.had_fuzzy,
                had_forgotten = excluded.had_forgotten,
                had_response_revision = excluded.had_response_revision,
                max_known_streak = excluded.max_known_streak,
                presentations_to_mastery = excluded.presentations_to_mastery,
                time_to_mastery_ms = excluded.time_to_mastery_ms;
            """;
        cmd.Parameters.AddWithValue("$wordKey", s.WordKey);
        cmd.Parameters.AddWithValue("$sessionId", s.SessionId);
        cmd.Parameters.AddWithValue("$firstPresentedAt", UtcText(s.FirstPresentedAtUtc));
        BindUtcOrNull(cmd, "$completedAt", s.CompletedAtUtc);
        BindTextOrNull(cmd, "$firstInitial", EnumTextOrNull(s.FirstInitialResponse));
        BindTextOrNull(cmd, "$firstValidated", EnumTextOrNull(s.FirstValidatedResponse));
        cmd.Parameters.AddWithValue("$total", s.TotalPresentations);
        cmd.Parameters.AddWithValue("$known", s.FinalKnownCount);
        cmd.Parameters.AddWithValue("$fuzzy", s.FinalFuzzyCount);
        cmd.Parameters.AddWithValue("$forgotten", s.FinalForgottenCount);
        cmd.Parameters.AddWithValue("$reset", s.ResetCount);
        cmd.Parameters.AddWithValue("$revisions", s.ResponseRevisionCount);
        cmd.Parameters.AddWithValue("$knownToFuzzy", s.KnownToFuzzy);
        cmd.Parameters.AddWithValue("$knownToForgotten", s.KnownToForgotten);
        cmd.Parameters.AddWithValue("$fuzzyToForgotten", s.FuzzyToForgotten);
        cmd.Parameters.AddWithValue("$hadFuzzy", s.HadFuzzy ? 1 : 0);
        cmd.Parameters.AddWithValue("$hadForgotten", s.HadForgotten ? 1 : 0);
        cmd.Parameters.AddWithValue("$hadRevision", s.HadResponseRevision ? 1 : 0);
        cmd.Parameters.AddWithValue("$maxStreak", s.MaxKnownStreak);
        cmd.Parameters.AddWithValue("$toMastery", s.PresentationsToMastery);
        cmd.Parameters.AddWithValue("$timeToMastery", s.TimeToMasteryMs.HasValue ? s.TimeToMasteryMs.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 写 canonical（含 pre-state）。
    /// 正常路径是纯 INSERT；只有"撤销后同一 canonical 再次定稿（redo）"才会走到 DO UPDATE，
    /// 此时把 invalidated 复位为 0 并让 revision 单调不减。
    /// 部分唯一索引 ux_canonical_active 保证任一 (word_key, session_id) 至多一条未失效 canonical。
    /// </summary>
    private static void UpsertCanonical(SqliteConnection connection, SqliteTransaction tx, CanonicalReview c, FsrsPreState? preState)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO canonical_reviews (
                canonical_id, word_key, session_id, source_presentation_id, origin, rating,
                reviewed_at_utc, completed_at_utc, aggregation_policy_version, revision,
                invalidated, invalidated_at_utc, invalidation_reason, supersedes_canonical_id,
                pre_state_existed, difficulty, stability, reps, lapses, state,
                last_review_at_utc, next_review_at_utc, last_canonical_rating
            ) VALUES (
                $canonicalId, $wordKey, $sessionId, $sourcePresentationId, $origin, $rating,
                $reviewedAt, $completedAt, $policyVersion, $revision,
                0, NULL, '', $supersedes,
                $preExisted, $preDifficulty, $preStability, $preReps, $preLapses, $preState,
                $preLastReviewAt, $preNextReviewAt, $preLastRating
            )
            ON CONFLICT(canonical_id) DO UPDATE SET
                word_key = excluded.word_key,
                session_id = excluded.session_id,
                source_presentation_id = excluded.source_presentation_id,
                origin = excluded.origin,
                rating = excluded.rating,
                reviewed_at_utc = excluded.reviewed_at_utc,
                completed_at_utc = excluded.completed_at_utc,
                aggregation_policy_version = excluded.aggregation_policy_version,
                revision = MAX(excluded.revision, canonical_reviews.revision + 1),
                invalidated = 0,
                invalidated_at_utc = NULL,
                invalidation_reason = '',
                supersedes_canonical_id = excluded.supersedes_canonical_id,
                pre_state_existed = excluded.pre_state_existed,
                difficulty = excluded.difficulty,
                stability = excluded.stability,
                reps = excluded.reps,
                lapses = excluded.lapses,
                state = excluded.state,
                last_review_at_utc = excluded.last_review_at_utc,
                next_review_at_utc = excluded.next_review_at_utc,
                last_canonical_rating = excluded.last_canonical_rating;
            """;
        var pre = preState ?? new FsrsPreState { Existed = false };
        cmd.Parameters.AddWithValue("$canonicalId", c.CanonicalId);
        cmd.Parameters.AddWithValue("$wordKey", c.WordKey);
        cmd.Parameters.AddWithValue("$sessionId", c.SessionId);
        BindTextOrNull(cmd, "$sourcePresentationId", c.SourcePresentationId);
        cmd.Parameters.AddWithValue("$origin", EnumText(c.Origin));
        cmd.Parameters.AddWithValue("$rating", EnumText(c.Rating));
        cmd.Parameters.AddWithValue("$reviewedAt", UtcText(c.ReviewedAtUtc));
        cmd.Parameters.AddWithValue("$completedAt", UtcText(c.CompletedAtUtc));
        cmd.Parameters.AddWithValue("$policyVersion", c.AggregationPolicyVersion ?? "");
        cmd.Parameters.AddWithValue("$revision", c.Revision);
        BindTextOrNull(cmd, "$supersedes", c.SupersedesCanonicalId);
        cmd.Parameters.AddWithValue("$preExisted", pre.Existed ? 1 : 0);
        cmd.Parameters.AddWithValue("$preDifficulty", pre.Difficulty);
        cmd.Parameters.AddWithValue("$preStability", pre.Stability);
        cmd.Parameters.AddWithValue("$preReps", pre.Reps);
        cmd.Parameters.AddWithValue("$preLapses", pre.Lapses);
        cmd.Parameters.AddWithValue("$preState", EnumText(pre.State));
        BindUtcOrNull(cmd, "$preLastReviewAt", pre.LastReviewAtUtc);
        BindUtcOrNull(cmd, "$preNextReviewAt", pre.NextReviewAtUtc);
        BindTextOrNull(cmd, "$preLastRating", EnumTextOrNull(pre.LastCanonicalRating));
        cmd.ExecuteNonQuery();
    }

    /// <summary>写 FSRS 卡片，并把幂等水位 last_applied_canonical_seq 推到本次 canonical 的 rowid。</summary>
    private static void UpsertCard(SqliteConnection connection, SqliteTransaction tx, FsrsCardState card, long canonicalSeq)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO fsrs_cards (
                word_key, difficulty, stability, reps, lapses, state,
                last_review_at_utc, next_review_at_utc, last_canonical_rating,
                fsrs_algorithm_version, fsrs_library_version, fsrs_parameter_version,
                last_applied_canonical_seq
            ) VALUES (
                $wordKey, $difficulty, $stability, $reps, $lapses, $state,
                $lastReviewAt, $nextReviewAt, $lastRating,
                $algoVersion, $libVersion, $paramVersion, $seq
            )
            ON CONFLICT(word_key) DO UPDATE SET
                difficulty = excluded.difficulty,
                stability = excluded.stability,
                reps = excluded.reps,
                lapses = excluded.lapses,
                state = excluded.state,
                last_review_at_utc = excluded.last_review_at_utc,
                next_review_at_utc = excluded.next_review_at_utc,
                last_canonical_rating = excluded.last_canonical_rating,
                fsrs_algorithm_version = excluded.fsrs_algorithm_version,
                fsrs_library_version = excluded.fsrs_library_version,
                fsrs_parameter_version = excluded.fsrs_parameter_version,
                last_applied_canonical_seq = excluded.last_applied_canonical_seq;
            """;
        cmd.Parameters.AddWithValue("$wordKey", card.WordKey);
        cmd.Parameters.AddWithValue("$difficulty", card.Difficulty);
        cmd.Parameters.AddWithValue("$stability", card.Stability);
        cmd.Parameters.AddWithValue("$reps", card.Reps);
        cmd.Parameters.AddWithValue("$lapses", card.Lapses);
        cmd.Parameters.AddWithValue("$state", EnumText(card.State));
        BindUtcOrNull(cmd, "$lastReviewAt", card.LastReviewAtUtc);
        BindUtcOrNull(cmd, "$nextReviewAt", card.NextReviewAtUtc);
        BindTextOrNull(cmd, "$lastRating", EnumTextOrNull(card.LastCanonicalRating));
        cmd.Parameters.AddWithValue("$algoVersion", card.FsrsAlgorithmVersion ?? "");
        cmd.Parameters.AddWithValue("$libVersion", card.FsrsLibraryVersion ?? "");
        cmd.Parameters.AddWithValue("$paramVersion", card.FsrsParameterVersion ?? "");
        cmd.Parameters.AddWithValue("$seq", canonicalSeq);
        cmd.ExecuteNonQuery();
    }

    /// <summary>快照 upsert。落库后特征不可变，因此冲突时**不覆盖** label/confident_recall/labeled_at_utc。</summary>
    private static void UpsertSnapshot(SqliteConnection connection, SqliteTransaction tx, ContextFeatureSnapshot s)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO context_snapshots (
                snapshot_id, word_key, session_id, presentation_id, captured_at_utc,
                fsrs_difficulty_at_capture, fsrs_stability_at_capture, fsrs_retrievability_at_capture,
                feature_schema_version, model_version, features_json, missing_flags_json,
                label, confident_recall, labeled_at_utc
            ) VALUES (
                $snapshotId, $wordKey, $sessionId, $presentationId, $capturedAt,
                $difficulty, $stability, $retrievability,
                $featureSchema, $modelVersion, $featuresJson, $missingJson,
                $label, $confident, $labeledAt
            )
            ON CONFLICT(snapshot_id) DO UPDATE SET
                word_key = excluded.word_key,
                session_id = excluded.session_id,
                presentation_id = excluded.presentation_id,
                captured_at_utc = excluded.captured_at_utc,
                fsrs_difficulty_at_capture = excluded.fsrs_difficulty_at_capture,
                fsrs_stability_at_capture = excluded.fsrs_stability_at_capture,
                fsrs_retrievability_at_capture = excluded.fsrs_retrievability_at_capture,
                feature_schema_version = excluded.feature_schema_version,
                model_version = excluded.model_version,
                features_json = excluded.features_json,
                missing_flags_json = excluded.missing_flags_json;
            """;
        cmd.Parameters.AddWithValue("$snapshotId", s.SnapshotId);
        cmd.Parameters.AddWithValue("$wordKey", s.WordKey);
        cmd.Parameters.AddWithValue("$sessionId", s.SessionId);
        cmd.Parameters.AddWithValue("$presentationId", s.PresentationId);
        cmd.Parameters.AddWithValue("$capturedAt", UtcText(s.CapturedAtUtc));
        cmd.Parameters.AddWithValue("$difficulty", s.FsrsDifficultyAtCapture);
        cmd.Parameters.AddWithValue("$stability", s.FsrsStabilityAtCapture);
        cmd.Parameters.AddWithValue("$retrievability", s.FsrsRetrievabilityAtCapture);
        cmd.Parameters.AddWithValue("$featureSchema", s.FeatureSchemaVersion ?? "");
        cmd.Parameters.AddWithValue("$modelVersion", s.ModelVersion ?? "");
        cmd.Parameters.AddWithValue("$featuresJson", s.FeaturesJson ?? "{}");
        cmd.Parameters.AddWithValue("$missingJson", s.MissingFlagsJson ?? "[]");
        BindTextOrNull(cmd, "$label", EnumTextOrNull(s.Label));
        cmd.Parameters.AddWithValue("$confident", s.ConfidentRecall.HasValue ? (s.ConfidentRecall.Value ? 1 : 0) : DBNull.Value);
        BindUtcOrNull(cmd, "$labeledAt", s.LabeledAtUtc);
        cmd.ExecuteNonQuery();
    }

    /// <summary>回填标签。context 快照的三分类以字符串落在 label 列（保留三分类）。</summary>
    private static void ApplyLabels(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<ContextLabel>? labels, DateTime fallbackAtUtc)
    {
        if (labels == null || labels.Count == 0) return;
        var at = UtcText(fallbackAtUtc);
        foreach (var label in labels)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE context_snapshots
                SET label = $label, confident_recall = $confident, labeled_at_utc = $labeledAt
                WHERE snapshot_id = $snapshotId;
                """;
            cmd.Parameters.AddWithValue("$label", EnumText(label.Rating));
            cmd.Parameters.AddWithValue("$confident", label.ConfidentRecall ? 1 : 0);
            cmd.Parameters.AddWithValue("$labeledAt", at);
            cmd.Parameters.AddWithValue("$snapshotId", label.SnapshotId);
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertDecision(SqliteConnection connection, SqliteTransaction tx, SchedulerDecision d)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO scheduler_decisions (
                decision_id, word_key, canonical_id, timestamp_utc,
                baseline_interval_days, baseline_retrievability, baseline_difficulty, baseline_stability,
                context_mode, context_delta, context_candidate_interval_days,
                final_interval_days, final_due_at_utc, desired_retention,
                fsrs_algorithm_version, fsrs_library_version, fsrs_parameter_version,
                aggregation_policy_version, trajectory_schema_version,
                context_feature_schema_version, context_model_version, scheduler_version
            ) VALUES (
                $decisionId, $wordKey, $canonicalId, $timestamp,
                $baselineInterval, $baselineRetrievability, $baselineDifficulty, $baselineStability,
                $contextMode, $contextDelta, $contextCandidate,
                $finalInterval, $finalDueAt, $desiredRetention,
                $algoVersion, $libVersion, $paramVersion,
                $policyVersion, $trajectoryVersion,
                $featureSchema, $contextModel, $schedulerVersion
            )
            ON CONFLICT(decision_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$decisionId", d.DecisionId);
        cmd.Parameters.AddWithValue("$wordKey", d.WordKey);
        cmd.Parameters.AddWithValue("$canonicalId", d.CanonicalId);
        cmd.Parameters.AddWithValue("$timestamp", UtcText(d.TimestampUtc));
        cmd.Parameters.AddWithValue("$baselineInterval", d.BaselineIntervalDays);
        cmd.Parameters.AddWithValue("$baselineRetrievability", d.BaselineRetrievability);
        cmd.Parameters.AddWithValue("$baselineDifficulty", d.BaselineDifficulty);
        cmd.Parameters.AddWithValue("$baselineStability", d.BaselineStability);
        cmd.Parameters.AddWithValue("$contextMode", EnumText(d.ContextMode));
        cmd.Parameters.AddWithValue("$contextDelta", d.ContextDelta.HasValue ? d.ContextDelta.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$contextCandidate", d.ContextCandidateIntervalDays.HasValue ? d.ContextCandidateIntervalDays.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$finalInterval", d.FinalIntervalDays);
        cmd.Parameters.AddWithValue("$finalDueAt", UtcText(d.FinalDueAtUtc));
        cmd.Parameters.AddWithValue("$desiredRetention", d.DesiredRetention);
        cmd.Parameters.AddWithValue("$algoVersion", d.FsrsAlgorithmVersion ?? "");
        cmd.Parameters.AddWithValue("$libVersion", d.FsrsLibraryVersion ?? "");
        cmd.Parameters.AddWithValue("$paramVersion", d.FsrsParameterVersion ?? "");
        cmd.Parameters.AddWithValue("$policyVersion", d.AggregationPolicyVersion ?? "");
        cmd.Parameters.AddWithValue("$trajectoryVersion", d.TrajectorySchemaVersion ?? "");
        cmd.Parameters.AddWithValue("$featureSchema", d.ContextFeatureSchemaVersion ?? "");
        cmd.Parameters.AddWithValue("$contextModel", d.ContextModelVersion ?? "");
        cmd.Parameters.AddWithValue("$schedulerVersion", d.SchedulerVersion ?? "");
        cmd.ExecuteNonQuery();
    }

    private static void EnqueueMutations(SqliteConnection connection, SqliteTransaction tx, IReadOnlyList<PendingMutation>? mutations, DateTime nowUtc)
    {
        if (mutations == null || mutations.Count == 0) return;
        var createdAt = UtcText(nowUtc);
        foreach (var mutation in mutations)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO mutation_outbox (kind, payload_json, created_at_utc, applied_at_utc, attempts, last_error) VALUES ($kind, $payload, $createdAt, NULL, 0, NULL)";
            cmd.Parameters.AddWithValue("$kind", mutation.Kind);
            cmd.Parameters.AddWithValue("$payload", mutation.PayloadJson);
            cmd.Parameters.AddWithValue("$createdAt", createdAt);
            cmd.ExecuteNonQuery();
        }
    }

    // =====================================================================
    // 6. 撤销 / 修正：失效 + pre-state 精确回放
    // =====================================================================

    /// <summary>
    /// 失效一条 canonical 并用其 pre-state 覆盖 FSRS 卡片（不重算历史）。
    /// **不删除任何 learning_events**——轨迹不可变，撤销只是"再写一条事件 + 标记失效"。
    /// </summary>
    /// <returns>
    /// true = 已失效；false = 该 canonical 不存在、已失效，或**它不是该词最新的有效 canonical**。
    /// 三种 false 都不产生任何副作用（幂等）。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>为什么只允许撤销最新那条</b>（评审 P1-2）：pre-state 回放是「把卡片退回这条 canonical
    /// 施加之前」。若被撤销的不是最新一条，回放会用**陈旧**的 pre-state 覆盖卡片
    /// （极端情况下 pre_state_existed=0 直接删卡），而后续更新的 canonical 仍然有效
    /// → 卡片状态与最新 canonical 不一致，甚至该词从到期队列里消失。
    /// </para>
    /// <para>
    /// 拦法用的是 <c>fsrs_cards.last_applied_canonical_seq</c>（原来的「只写不读」水位）：
    /// 它由 <see cref="UpsertCard"/> 写成该次 canonical 的 rowid，撤销时回退到仍有效的最新 rowid。
    /// 因此「水位 == 本条的 rowid」正是「本条是该词最新有效 canonical」的充要条件。
    /// </para>
    /// </remarks>
    public bool InvalidateCanonical(string canonicalId, DateTime atUtc, string reason)
        => InvalidateCanonicalCore(canonicalId, atUtc, reason, null, null);

    public bool InvalidateCanonicalWithEvent(string canonicalId, DateTime atUtc, string reason,
        LearningInteractionEvent undoEvent, MemorySessionCheckpoint? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(undoEvent);
        var valid = undoEvent.Kind switch
        {
            InteractionEventKind.Undone => undoEvent.Response is null,
            InteractionEventKind.Revised => undoEvent.Response is { } rating && Enum.IsDefined(rating)
                && undoEvent.PreviousResponse is not null && !string.IsNullOrWhiteSpace(undoEvent.SupersedesEventId),
            _ => false,
        };
        if (!valid || (checkpoint is not null && checkpoint.SessionId != undoEvent.SessionId))
            throw new InvalidDataException("修正/撤销事件或检查点会话无效。");
        return InvalidateCanonicalCore(canonicalId, atUtc, reason, undoEvent, checkpoint);
    }

    private bool InvalidateCanonicalCore(string canonicalId, DateTime atUtc, string reason,
        LearningInteractionEvent? undoEvent, MemorySessionCheckpoint? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalId);

        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                string wordKey;
                bool preExisted, alreadyInvalidated;
                long canonicalSeq;
                FsrsPreState pre = new() { Existed = false };

                using (var read = _connection.CreateCommand())
                {
                    read.Transaction = tx;
                    read.CommandText = """
                        SELECT word_key, invalidated, pre_state_existed,
                               difficulty, stability, reps, lapses, state,
                               last_review_at_utc, next_review_at_utc, last_canonical_rating, rowid, session_id
                        FROM canonical_reviews WHERE canonical_id = $id
                        """;
                    read.Parameters.AddWithValue("$id", canonicalId);
                    using var r = read.ExecuteReader();
                    if (!r.Read())
                    {
                        r.Close();
                        tx.Rollback();
                        return false;
                    }
                    wordKey = r.GetString(0);
                    if (undoEvent is not null && (undoEvent.WordKey != wordKey || undoEvent.SessionId != r.GetString(12)))
                        throw new InvalidDataException("撤销事件与canonical的词条/会话不一致。");
                    alreadyInvalidated = r.GetInt64(1) != 0;
                    preExisted = r.GetInt64(2) != 0;
                    canonicalSeq = r.GetInt64(11);
                    pre = new FsrsPreState
                    {
                        Existed = preExisted,
                        Difficulty = r.IsDBNull(3) ? 0 : r.GetDouble(3),
                        Stability = r.IsDBNull(4) ? 0 : r.GetDouble(4),
                        Reps = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                        Lapses = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                        State = EnumParse(OptText(r, 7), FsrsState.New),
                        LastReviewAtUtc = OptUtc(r, 8),
                        NextReviewAtUtc = OptUtc(r, 9),
                        LastCanonicalRating = EnumParseOrNull<StudyRating>(OptText(r, 10)),
                    };
                    r.Close();
                }

                if (alreadyInvalidated)
                {
                    tx.Rollback();
                    return false;
                }
                if (undoEvent is { Kind: InteractionEventKind.Revised })
                {
                    using var superseded = _connection.CreateCommand(); superseded.Transaction = tx;
                    superseded.CommandText = """
                        SELECT 1 FROM learning_events WHERE event_id=$id AND word_key=$word
                            AND session_id=$session AND presentation_id=$presentation
                            AND kind IN ('Rated','Revised') AND response=$previous LIMIT 1;
                        """;
                    superseded.Parameters.AddWithValue("$id", undoEvent.SupersedesEventId!);
                    superseded.Parameters.AddWithValue("$word", undoEvent.WordKey);
                    superseded.Parameters.AddWithValue("$session", undoEvent.SessionId);
                    superseded.Parameters.AddWithValue("$presentation", undoEvent.PresentationId);
                    superseded.Parameters.AddWithValue("$previous", EnumTextOrNull(undoEvent.PreviousResponse)!);
                    if (superseded.ExecuteScalar() is null)
                        throw new InvalidDataException("修正未指向同一词条/会话/呈现的真实原评分。");
                }

                // 水位守卫：只有「该词最新的一条有效 canonical」才允许被撤销。
                // 水位为 NULL（没有卡片或从未应用过）时一律拒绝——宁可失败也不要用陈旧 pre-state 覆盖卡片。
                long? appliedSeq = null;
                using (var cardRead = _connection.CreateCommand())
                {
                    cardRead.Transaction = tx;
                    cardRead.CommandText = "SELECT last_applied_canonical_seq FROM fsrs_cards WHERE word_key = $wordKey";
                    cardRead.Parameters.AddWithValue("$wordKey", wordKey);
                    if (cardRead.ExecuteScalar() is long seq) appliedSeq = seq;
                }
                if (appliedSeq != canonicalSeq)
                {
                    tx.Rollback();
                    return false;
                }

                using (var invalidate = _connection.CreateCommand())
                {
                    invalidate.Transaction = tx;
                    invalidate.CommandText = "UPDATE canonical_reviews SET invalidated = 1, invalidated_at_utc = $at, invalidation_reason = $reason WHERE canonical_id = $id";
                    invalidate.Parameters.AddWithValue("$at", UtcText(atUtc));
                    invalidate.Parameters.AddWithValue("$reason", reason ?? "");
                    invalidate.Parameters.AddWithValue("$id", canonicalId);
                    invalidate.ExecuteNonQuery();
                }

                if (!pre.Existed)
                {
                    // 该词此前没有卡片：撤销后回到"从未排期"。
                    using var remove = _connection.CreateCommand();
                    remove.Transaction = tx;
                    remove.CommandText = "DELETE FROM fsrs_cards WHERE word_key = $wordKey";
                    remove.Parameters.AddWithValue("$wordKey", wordKey);
                    remove.ExecuteNonQuery();
                }
                else
                {
                    using var replay = _connection.CreateCommand();
                    replay.Transaction = tx;
                    // 水位回退到"该词仍有效的前一条 canonical"的 rowid；没有则 NULL。
                    // 子查询在 invalidated 置 1 之后执行，因此不会选中刚被撤销的这条。
                    replay.CommandText = """
                        UPDATE fsrs_cards SET
                            difficulty = $difficulty,
                            stability = $stability,
                            reps = $reps,
                            lapses = $lapses,
                            state = $state,
                            last_review_at_utc = $lastReviewAt,
                            next_review_at_utc = $nextReviewAt,
                            last_canonical_rating = $lastRating,
                            -- 版本戳必须跟着一起回放：pre-state 的 D/S 属于**当前生效权重**那一套
                            -- （每次参数发布都会在同一事务里整库重写 pre-state），只回放 D/S 而不回放版本戳，
                            -- 就会产出"D/S 来自某套权重、戳却写着另一套"的撒谎审计行。
                            fsrs_algorithm_version = $algoVersion,
                            fsrs_library_version = $libVersion,
                            fsrs_parameter_version = $paramVersion,
                            last_applied_canonical_seq = (
                                SELECT MAX(rowid) FROM canonical_reviews
                                WHERE word_key = $wordKey AND invalidated = 0
                            )
                        WHERE word_key = $wordKey;
                        """;
                    replay.Parameters.AddWithValue("$difficulty", pre.Difficulty);
                    replay.Parameters.AddWithValue("$stability", pre.Stability);
                    replay.Parameters.AddWithValue("$reps", pre.Reps);
                    replay.Parameters.AddWithValue("$lapses", pre.Lapses);
                    replay.Parameters.AddWithValue("$state", EnumText(pre.State));
                    BindUtcOrNull(replay, "$lastReviewAt", pre.LastReviewAtUtc);
                    BindUtcOrNull(replay, "$nextReviewAt", pre.NextReviewAtUtc);
                    BindTextOrNull(replay, "$lastRating", EnumTextOrNull(pre.LastCanonicalRating));
                    replay.Parameters.AddWithValue("$algoVersion", SchedulingConfig.AlgorithmVersion);
                    replay.Parameters.AddWithValue("$libVersion", SchedulingConfig.LibraryVersion);
                    replay.Parameters.AddWithValue("$paramVersion", ActiveFsrsParameterVersion());
                    replay.Parameters.AddWithValue("$wordKey", wordKey);
                    replay.ExecuteNonQuery();
                }

                if (undoEvent is not null) InsertEvent(_connection, tx, undoEvent);
                if (checkpoint is not null) UpsertSessionCheckpoint(_connection, tx, checkpoint);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        BackupAfterMemoryWrite();
        return true;
    }

    // =====================================================================
    // 7. FSRS 卡片与到期队列
    // =====================================================================

    private const string CardColumns =
        "word_key, difficulty, stability, reps, lapses, state, last_review_at_utc, next_review_at_utc, " +
        "last_canonical_rating, fsrs_algorithm_version, fsrs_library_version, fsrs_parameter_version, last_applied_canonical_seq";

    private static FsrsCardState ReadCard(SqliteDataReader r) => new()
    {
        WordKey = r.GetString(0),
        Difficulty = r.IsDBNull(1) ? 0 : r.GetDouble(1),
        Stability = r.IsDBNull(2) ? 0 : r.GetDouble(2),
        Reps = r.IsDBNull(3) ? 0 : r.GetInt64(3),
        Lapses = r.IsDBNull(4) ? 0 : r.GetInt64(4),
        State = EnumParse(OptText(r, 5), FsrsState.New),
        LastReviewAtUtc = OptUtc(r, 6),
        NextReviewAtUtc = OptUtc(r, 7),
        LastCanonicalRating = EnumParseOrNull<StudyRating>(OptText(r, 8)),
        FsrsAlgorithmVersion = OptText(r, 9) ?? "",
        FsrsLibraryVersion = OptText(r, 10) ?? "",
        FsrsParameterVersion = OptText(r, 11) ?? "",
        LastAppliedCanonicalSeq = OptInt64(r, 12),
    };

    public FsrsCardState? GetCard(string wordKey)
    {
        ArgumentNullException.ThrowIfNull(wordKey);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {CardColumns} FROM fsrs_cards WHERE word_key = $wordKey";
        cmd.Parameters.AddWithValue("$wordKey", wordKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCard(r) : null;
    }

    /// <summary>All memory-card identities, including paused cards with a null due date.</summary>
    public IReadOnlySet<string> GetMemoryCardKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT word_key FROM fsrs_cards";
        using var reader = cmd.ExecuteReader(); while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    public bool HasCard(string wordKey)
    {
        ArgumentNullException.ThrowIfNull(wordKey);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM fsrs_cards WHERE word_key = $wordKey LIMIT 1";
        cmd.Parameters.AddWithValue("$wordKey", wordKey);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// 到期队列。<c>next_review_at_utc</c> 一律是 <see cref="UtcText"/> 写出的定长 28 字符
    /// UTC ISO-8601 "O" 串（yyyy-MM-ddTHH:mm:ss.fffffffZ），字典序与时间序一致，
    /// 因此这里可以直接做字符串比较，不需要逐行解析时间。
    /// </summary>
    public IReadOnlyList<DueCard> QueryDue(DateTime nowUtc, int limit)
    {
        if (limit <= 0) return [];
        var due = new List<DueCard>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT word_key, next_review_at_utc, difficulty, stability
            FROM fsrs_cards
            WHERE next_review_at_utc IS NOT NULL AND next_review_at_utc <= $now
            ORDER BY next_review_at_utc ASC, word_key ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$now", UtcText(nowUtc));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            due.Add(new DueCard(
                r.GetString(0),
                UtcParse(r.GetString(1)),
                r.IsDBNull(2) ? 0 : r.GetDouble(2),
                r.IsDBNull(3) ? 0 : r.GetDouble(3)));
        }
        return due;
    }

    /// <summary>到期数量。与 <see cref="QueryDue"/> 同一口径（同样的定长 UTC 串比较）。</summary>
    public int CountDue(DateTime nowUtc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fsrs_cards WHERE next_review_at_utc IS NOT NULL AND next_review_at_utc <= $now";
        cmd.Parameters.AddWithValue("$now", UtcText(nowUtc));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // =====================================================================
    // 8. Context 快照与标签
    // =====================================================================

    private const string SnapshotColumns =
        "snapshot_id, word_key, session_id, presentation_id, captured_at_utc, " +
        "fsrs_difficulty_at_capture, fsrs_stability_at_capture, fsrs_retrievability_at_capture, " +
        "feature_schema_version, model_version, features_json, missing_flags_json, label, confident_recall, labeled_at_utc";

    private static ContextFeatureSnapshot ReadSnapshot(SqliteDataReader r) => new()
    {
        SnapshotId = r.GetString(0),
        WordKey = r.GetString(1),
        SessionId = r.GetString(2),
        PresentationId = r.GetString(3),
        CapturedAtUtc = UtcParse(r.GetString(4)),
        FsrsDifficultyAtCapture = r.IsDBNull(5) ? 0 : r.GetDouble(5),
        FsrsStabilityAtCapture = r.IsDBNull(6) ? 0 : r.GetDouble(6),
        FsrsRetrievabilityAtCapture = r.IsDBNull(7) ? 0 : r.GetDouble(7),
        FeatureSchemaVersion = OptText(r, 8) ?? "",
        ModelVersion = OptText(r, 9) ?? "",
        FeaturesJson = OptText(r, 10) ?? "{}",
        MissingFlagsJson = OptText(r, 11) ?? "[]",
        Label = EnumParseOrNull<StudyRating>(OptText(r, 12)),
        ConfidentRecall = OptBool(r, 13),
        LabeledAtUtc = OptUtc(r, 14),
    };

    public void SaveContextSnapshot(ContextFeatureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                UpsertSnapshot(_connection, tx, snapshot);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        // 追加型审计数据（作答前快照）：不触发全库备份（评审 P1-7）。
    }

    public ContextFeatureSnapshot? GetContextSnapshot(string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {SnapshotColumns} FROM context_snapshots WHERE snapshot_id = $id";
        cmd.Parameters.AddWithValue("$id", snapshotId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSnapshot(r) : null;
    }

    /// <summary>同一 presentation 若存在多版特征，取捕获时刻最新的一条。</summary>
    public ContextFeatureSnapshot? GetSnapshotByPresentation(string presentationId)
    {
        ArgumentNullException.ThrowIfNull(presentationId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {SnapshotColumns} FROM context_snapshots WHERE presentation_id = $id ORDER BY captured_at_utc DESC, rowid DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", presentationId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSnapshot(r) : null;
    }

    public void LabelSnapshots(IReadOnlyList<ContextLabel> labels, DateTime labeledAtUtc)
    {
        if (labels == null || labels.Count == 0) return;
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                ApplyLabels(_connection, tx, labels, labeledAtUtc);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        BackupAfterMemoryWrite();
    }

    /// <summary>
    /// 训练样本。仅有效 FirstRetrieval canonical 的锚点；捕获、标签和定稿均必须早于 cutoff。
    /// Label 是二分类口径：Known/Unsure = 1、Forgot = 0（三分类保留在 label 列，置信另存 confident_recall）。
    /// </summary>
    public IReadOnlyList<LabeledContextSample> LoadLabeledSamples(DateTime? cutoffUtc = null)
    {
        var samples = new List<LabeledContextSample>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.snapshot_id, s.word_key, s.captured_at_utc, s.features_json, s.missing_flags_json,
                   s.label, s.confident_recall, s.fsrs_retrievability_at_capture
            FROM context_snapshots s
            JOIN canonical_reviews c ON c.word_key=s.word_key AND c.session_id=s.session_id
                AND c.source_presentation_id=s.presentation_id
                AND c.origin='FirstRetrieval' AND c.invalidated=0
            WHERE s.label IS NOT NULL AND s.labeled_at_utc IS NOT NULL
                AND ($cutoff IS NULL OR (s.captured_at_utc < $cutoff
                    AND s.labeled_at_utc < $cutoff AND c.completed_at_utc < $cutoff))
            ORDER BY s.captured_at_utc ASC, s.rowid ASC;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.HasValue ? UtcText(cutoffUtc.Value) : DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rating = EnumParseOrNull<StudyRating>(OptText(r, 5));
            if (!rating.HasValue) throw new InvalidDataException("Context 快照标签无效，已停止训练。");
            samples.Add(new LabeledContextSample(
                r.GetString(0),
                r.GetString(1),
                UtcParse(r.GetString(2)),
                DeserializeFeatures(OptText(r, 3)),
                OptText(r, 4) ?? "[]",
                rating.Value == StudyRating.Forgot ? 0 : 1,
                OptBool(r, 6) ?? false)
            {
                // 基线偏移量 logit(R) 的来源。没有它就无法区分「Context 比 FSRS 好」与「Context 在虚报」。
                FsrsRetrievabilityAtCapture = OptDouble(r, 7) ?? double.NaN,
            });
        }
        return samples;
    }

    private static double[] DeserializeFeatures(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<double[]>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Context 快照特征向量损坏，已停止训练。", ex);
        }
    }

    // =====================================================================
    // 8b. Context 作答前历史查询（P6）
    //
    // 三条查询是「作答前特征」**唯一的**数据入口，因此「不得读到快照时刻之后的数据」这条要求
    // 落在它们的 WHERE 上：全部用**严格小于** $before，绝不用 <=。
    // 任何一条都不得返回 `invalidated = 1` 的行——撤销掉的 canonical 不是发生过的事实。
    // =====================================================================

    private const string SummaryColumns =
        "word_key, session_id, first_presented_at_utc, completed_at_utc, first_initial_response, first_validated_response, " +
        "total_presentations, final_known_count, final_fuzzy_count, final_forgotten_count, reset_count, " +
        "response_revision_count, known_to_fuzzy, known_to_forgotten, fuzzy_to_forgotten, had_fuzzy, had_forgotten, " +
        "had_response_revision, max_known_streak, presentations_to_mastery, time_to_mastery_ms";

    private static WordSessionSummary ReadSummary(SqliteDataReader r) => new()
    {
        WordKey = r.GetString(0),
        SessionId = r.GetString(1),
        FirstPresentedAtUtc = UtcParse(r.GetString(2)),
        CompletedAtUtc = OptUtc(r, 3),
        FirstInitialResponse = EnumParseOrNull<StudyRating>(OptText(r, 4)),
        FirstValidatedResponse = EnumParseOrNull<StudyRating>(OptText(r, 5)),
        TotalPresentations = (int)r.GetInt64(6),
        FinalKnownCount = (int)r.GetInt64(7),
        FinalFuzzyCount = (int)r.GetInt64(8),
        FinalForgottenCount = (int)r.GetInt64(9),
        ResetCount = (int)r.GetInt64(10),
        ResponseRevisionCount = (int)r.GetInt64(11),
        KnownToFuzzy = (int)r.GetInt64(12),
        KnownToForgotten = (int)r.GetInt64(13),
        FuzzyToForgotten = (int)r.GetInt64(14),
        HadFuzzy = BoolOf(r, 15),
        HadForgotten = BoolOf(r, 16),
        HadResponseRevision = BoolOf(r, 17),
        MaxKnownStreak = (int)r.GetInt64(18),
        PresentationsToMastery = (int)r.GetInt64(19),
        TimeToMasteryMs = OptInt64(r, 20),
    };

    /// <inheritdoc />
    public IReadOnlyList<CanonicalHistoryRow> LoadCanonicalHistory(DateTime? fromUtc, DateTime beforeUtc, string? wordKey)
    {
        var rows = new List<CanonicalHistoryRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT canonical_id, word_key, session_id, origin, rating, reviewed_at_utc, completed_at_utc, revision, rowid
            FROM canonical_reviews
            WHERE invalidated = 0
              AND reviewed_at_utc < $before
              AND ($from IS NULL OR reviewed_at_utc >= $from)
              AND ($wordKey IS NULL OR word_key = $wordKey)
            ORDER BY reviewed_at_utc ASC, rowid ASC;
            """;
        cmd.Parameters.AddWithValue("$before", UtcText(beforeUtc));
        cmd.Parameters.AddWithValue("$from", fromUtc.HasValue ? UtcText(fromUtc.Value) : DBNull.Value);
        BindTextOrNull(cmd, "$wordKey", string.IsNullOrWhiteSpace(wordKey) ? null : wordKey);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new CanonicalHistoryRow(
                r.GetString(1),
                r.GetString(2),
                EnumParse(OptText(r, 3), CanonicalOrigin.FirstRetrieval),
                EnumParse(OptText(r, 4), StudyRating.Forgot),
                UtcParse(r.GetString(5)),
                UtcParse(r.GetString(6)))
            {
                CanonicalId = r.GetString(0),
                Revision = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                CanonicalSequence = r.GetInt64(8),
            });
        }
        return rows;
    }

    /// <inheritdoc />
    public IReadOnlyList<WordSessionSummary> LoadCompletedSummaries(string wordKey, DateTime beforeUtc)
    {
        var rows = new List<WordSessionSummary>();
        if (string.IsNullOrWhiteSpace(wordKey)) return rows;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SummaryColumns}
            FROM word_session_summaries
            WHERE word_key = $wordKey
              AND completed_at_utc IS NOT NULL
              AND completed_at_utc < $before
            ORDER BY completed_at_utc ASC, rowid ASC;
            """;
        cmd.Parameters.AddWithValue("$wordKey", wordKey);
        cmd.Parameters.AddWithValue("$before", UtcText(beforeUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(ReadSummary(r));
        return rows;
    }

    /// <inheritdoc />
    public IReadOnlyList<int> LoadRecentResponseLatencies(DateTime beforeUtc, int limit)
    {
        var rows = new List<int>();
        if (limit <= 0) return rows;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT response_latency_ms
            FROM learning_events
            WHERE kind = 'Rated'
              AND is_recall = 1
              AND response_latency_ms IS NOT NULL
              AND occurred_at_utc < $before
            ORDER BY occurred_at_utc DESC, rowid DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$before", UtcText(beforeUtc));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var value = r.GetInt64(0);
            if (value >= 0 && value <= int.MaxValue) rows.Add((int)value);
        }
        return rows;
    }

    // =====================================================================
    // 9. 个人化模型（Context 与 FSRS 个人参数共用一张表）
    // =====================================================================

    /// <summary>Context 校准模型在 personalization_models 里的 model_kind 取值。FSRS 个人参数用同表的 "fsrs_params"。</summary>
    private const string ContextModelKind = SchedulingConfig.ContextModelKind;

    private const string ModelColumns =
        "model_version, trained_at_utc, train_cutoff_utc, status, coefficients_json, scaler_json, metrics_json, last_good_model_version";

    private static PersonalizationModelState ReadModel(SqliteDataReader r) => new()
    {
        ModelVersion = r.GetString(0),
        TrainedAtUtc = UtcParse(r.GetString(1)),
        TrainCutoffUtc = UtcParse(r.GetString(2)),
        Status = EnumParse(OptText(r, 3), ContextMode.ColdStart),
        CoefficientsJson = OptText(r, 4) ?? "[]",
        ScalerJson = OptText(r, 5) ?? "{}",
        MetricsJson = OptText(r, 6) ?? "{}",
        LastGoodModelVersion = OptText(r, 7),
    };

    /// <summary>取最新一版 Context 校准模型（model_kind='context'）。</summary>
    public PersonalizationModelState? GetPersonalizationModel() => GetPersonalizationModel(ContextModelKind);

    /// <summary>
    /// 按 model_kind 取最新一版模型。**个人 FSRS 参数必须走显式 kind**：
    /// 无参重载只认 'context'，把权重行当成 Context 模型读回去会解析失败，
    /// 从而静默清掉用户已经通过资格验证的 Context 模型。
    /// </summary>
    public PersonalizationModelState? GetPersonalizationModel(string modelKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKind);
        using var cmd = _connection.CreateCommand();
        // 与 LoadPersonalizationModels 同口径：跳过训练记账行（recordKind='attempt'）。
        // 记账行的 CoefficientsJson 是 "[]"，被当成模型读回会静默清掉真正的模型。
        // Context 行从不写 recordKind（CASE 恒取真），因此这一过滤对 'context' 完全无影响。
        cmd.CommandText = $"""
            SELECT {ModelColumns} FROM personalization_models
            WHERE model_kind = $kind
              AND CASE WHEN json_valid(metrics_json)
                       THEN COALESCE(json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterRecordKindKey}'), '')
                            <> '{SchedulingConfig.FsrsParameterRecordKindAttempt}'
                       ELSE 1 END
            ORDER BY trained_at_utc DESC, rowid DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$kind", modelKind);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadModel(r) : null;
    }

    /// <summary>
    /// 按 model_kind 取最近 <paramref name="limit"/> 行，**新的在前**（trained_at 降序，同刻按 rowid 降序）。
    /// 个人参数用它找回 last-good、并读训练尝试记账行——冷却与新增量门槛必须跨重启生效。
    /// </summary>
    public IReadOnlyList<PersonalizationModelState> LoadPersonalizationModels(string modelKind, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKind);
        if (limit <= 0) return [];
        var rows = new List<PersonalizationModelState>();
        using var cmd = _connection.CreateCommand();
        // 窗口只限制**条数**，而"发布行"与"训练记账行"（recordKind='attempt'）共用同一个 model_kind：
        // 不推进水位的瞬时失败路径每小时会写一行记账，攒够 limit 行就会把唯一那行已发布参数挤出窗口，
        // 于是 holder、Context 与撤销回放的版本戳会**一起**静默回退 defaults（并触发整库按 defaults 重放）。
        // 因此除窗口内的行以外，**永远**额外保证"最新一行非记账行"出现在结果里。
        cmd.CommandText = $"""
            SELECT {ModelColumns} FROM personalization_models
            WHERE model_kind = $kind
              AND (
                    rowid IN (SELECT rowid FROM personalization_models
                              WHERE model_kind = $kind
                              ORDER BY trained_at_utc DESC, rowid DESC LIMIT $limit)
                 OR rowid = (SELECT rowid FROM personalization_models
                             WHERE model_kind = $kind
                               AND CASE WHEN json_valid(metrics_json)
                                        THEN COALESCE(json_extract(metrics_json, '$.{SchedulingConfig.FsrsParameterRecordKindKey}'), '')
                                             <> '{SchedulingConfig.FsrsParameterRecordKindAttempt}'
                                        ELSE 1 END
                             ORDER BY trained_at_utc DESC, rowid DESC LIMIT 1)
                  )
            ORDER BY trained_at_utc DESC, rowid DESC;
            """;
        cmd.Parameters.AddWithValue("$kind", modelKind);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(ReadModel(r));
        return rows;
    }

    /// <summary>写入/更新一版 Context 校准模型。FSRS 个人参数走同表的 model_kind='fsrs_params'。</summary>
    public void SavePersonalizationModel(PersonalizationModelState state) =>
        SavePersonalizationModel(state, ContextModelKind);

    /// <summary>
    /// 写入侧的纵深防御：<c>fsrs_params</c> 分区只允许两种 <c>recordKind</c>——发布行 <c>params</c> 与
    /// 训练记账行 <c>attempt</c>。读取侧的"永远额外并入最新一行非记账行"这条保证，依赖的正是这个不变式：
    /// 一旦有人写进第三种 kind（既不是 params 也不是 attempt），读取侧会把它当成"最新非记账行"保护起来，
    /// 而真正的参数行依然会被挤出窗口 —— 于是 B3 的静默回退 defaults 会重新出现。
    /// 这里把它变成**显式拒绝**，而不是依赖跨文件的口头约定。
    /// </summary>
    private static void RequireFsrsParameterRecordKind(PersonalizationModelState state, string modelKind)
    {
        if (!string.Equals(modelKind, SchedulingConfig.FsrsParameterModelKind, StringComparison.Ordinal)) return;
        var kind = FsrsPersonalization.ReadMetricString(state.MetricsJson, SchedulingConfig.FsrsParameterRecordKindKey);
        if (!string.Equals(kind, SchedulingConfig.FsrsParameterRecordKindParams, StringComparison.Ordinal)
            && !string.Equals(kind, SchedulingConfig.FsrsParameterRecordKindAttempt, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"model_kind='{SchedulingConfig.FsrsParameterModelKind}' 的行必须带 "
                + $"recordKind='{SchedulingConfig.FsrsParameterRecordKindParams}' 或 "
                + $"'{SchedulingConfig.FsrsParameterRecordKindAttempt}'（实际 '{kind}'）；"
                + "否则读取侧的'最新非记账行'保证会被绕过，个人参数可能被静默挤回 defaults。");
        }
    }

    /// <summary>
    /// 写入/更新一版模型（按 model_kind 分区）。model_version 是主键：同一版本号是**原地覆盖**，
    /// 因此"发布新参数"必须用新版本号，绝不复用一个已经存在的版本号。
    /// </summary>
    public void SavePersonalizationModel(PersonalizationModelState state, string modelKind)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireFsrsParameterRecordKind(state, modelKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKind);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ModelVersion);
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                UpsertPersonalizationModel(_connection, tx, state, modelKind);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        BackupAfterMemoryWrite();
    }

    /// <summary>写入/更新一版模型行（不含事务；供独立写入与 PublishFsrsParameters 的事务共用）。</summary>
    private static void UpsertPersonalizationModel(
        SqliteConnection connection, SqliteTransaction tx, PersonalizationModelState state, string modelKind)
    {
        RequireFsrsParameterRecordKind(state, modelKind);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO personalization_models (
                model_version, model_kind, trained_at_utc, train_cutoff_utc, status,
                coefficients_json, scaler_json, metrics_json, last_good_model_version
            ) VALUES (
                $version, $kind, $trainedAt, $cutoff, $status,
                $coefficients, $scaler, $metrics, $lastGood
            )
            ON CONFLICT(model_version) DO UPDATE SET
                model_kind = excluded.model_kind,
                trained_at_utc = excluded.trained_at_utc,
                train_cutoff_utc = excluded.train_cutoff_utc,
                status = excluded.status,
                coefficients_json = excluded.coefficients_json,
                scaler_json = excluded.scaler_json,
                metrics_json = excluded.metrics_json,
                last_good_model_version = excluded.last_good_model_version;
            """;
        cmd.Parameters.AddWithValue("$version", state.ModelVersion);
        cmd.Parameters.AddWithValue("$kind", modelKind);
        cmd.Parameters.AddWithValue("$trainedAt", UtcText(state.TrainedAtUtc));
        cmd.Parameters.AddWithValue("$cutoff", UtcText(state.TrainCutoffUtc));
        cmd.Parameters.AddWithValue("$status", EnumText(state.Status));
        cmd.Parameters.AddWithValue("$coefficients", state.CoefficientsJson ?? "[]");
        cmd.Parameters.AddWithValue("$scaler", state.ScalerJson ?? "{}");
        cmd.Parameters.AddWithValue("$metrics", state.MetricsJson ?? "{}");
        BindTextOrNull(cmd, "$lastGood", state.LastGoodModelVersion);
        cmd.ExecuteNonQuery();
    }

    // =====================================================================
    // 9b. 个人 FSRS 参数：一致性签名、卡片版本核对、**单事务重放发布**
    // =====================================================================

    /// <summary>当前生效的个人参数版本戳缓存（发布成功后更新；首次使用时从库里惰性恢复）。</summary>
    private string? _activeFsrsParameterVersion;

    /// <summary>
    /// 撤销回放时给卡片盖的参数版本戳：pre-state 的 D/S 是按**当前生效权重**算出来、并随每次发布整库重写的，
    /// 因此撤销恢复出来的那一对 D/S 天然属于当前版本。写死编译期常量会让换版后的审计列撒谎。
    /// </summary>
    private string ActiveFsrsParameterVersion()
    {
        if (_activeFsrsParameterVersion is { } cached) return cached;
        var version = SchedulingConfig.ParameterVersion;
        try
        {
            // 与集成层、Context 共用**同一个**有效参数解析器：撤销回放的版本戳必须等于
            // 真正生效的那一套权重，而不是某个"版本字符串写得很漂亮但系数已经损坏"的行。
            var rows = LoadPersonalizationModels(SchedulingConfig.FsrsParameterModelKind, 8);
            version = FsrsPersonalization.ResolveEffectiveWeights(rows).ParameterVersion;
        }
        catch (Exception)
        {
            // 读不出来就按冻结默认版本处理：那正是换版前所有卡片上的戳，不会造成"版本撒谎"。
        }
        _activeFsrsParameterVersion = version;
        return version;
    }

    /// <summary>
    /// 当前**有效 canonical 集合与手动 due 覆盖**的一致性签名：逐行定界后的流式 SHA256
    /// （见 <c>ComputeCanonicalSignature(SqliteTransaction?)</c>）。
    /// <para>刻意**不用** COUNT/SUM 之类的聚合量：那些聚合量不是集合身份，存在两组不同有效集合
    /// 产生完全相同聚合量的反例（撤销 101/104 再 revive 102/103 各 revision+1），
    /// 会让训练快照实际已变却通过 <c>StaleSnapshot</c> 检查。</para>
    /// </summary>
    public string ComputeCanonicalSignature() => ComputeCanonicalSignature(null);

    private string ComputeCanonicalSignature(SqliteTransaction? tx)
    {
        // Hash actual replay/training inputs. COUNT/SUM cannot prove set equality.
        // Stream rows; JSON arrays delimit strings and fields without concatenation ambiguity.
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        void AddRows(string sql)
        {
            using var cmd = _connection.CreateCommand();
            if (tx is not null) cmd.Transaction = tx;
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                hash.AppendData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(values));
                hash.AppendData(new byte[] { 10 });
            }
        }
        AddRows("""
            SELECT 'canonical', rowid, canonical_id, word_key, session_id, origin, rating,
                   reviewed_at_utc, completed_at_utc, revision
            FROM canonical_reviews WHERE invalidated=0 ORDER BY rowid;
            """);
        AddRows("""
            SELECT 'manual-due', override_id, word_key, anchor_canonical_id, anchor_revision, due_at_utc
            FROM fsrs_manual_due_overrides ORDER BY override_id;
            """);
        return "fsrs-input-v2:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// 训练门槛用的廉价范围查询：有效 canonical（且双截止）的**条数**与首末 <c>reviewed_at_utc</c>。
    /// <para>用途：条数是"真实训练目标数"的**上界**，跨度也是目标跨度的上界，因此
    /// <c>Count &lt; 门槛</c> 或 <c>Span &lt; 门槛</c> 时可以**确定**门槛未满足，
    /// 从而避免在每个轮末都整表物化一遍历史。返回 null 表示该实现不支持（调用方回落到完整读取）。</para>
    /// </summary>
    public CanonicalHistoryBounds? LoadCanonicalBounds(DateTime beforeUtc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), MIN(reviewed_at_utc), MAX(reviewed_at_utc)
            FROM canonical_reviews
            WHERE invalidated = 0 AND reviewed_at_utc < $before AND completed_at_utc < $before;
            """;
        cmd.Parameters.AddWithValue("$before", UtcText(beforeUtc));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var count = Convert.ToInt32(r.GetInt64(0), CultureInfo.InvariantCulture);
        if (count == 0) return new CanonicalHistoryBounds(0, null, null);
        return new CanonicalHistoryBounds(
            count,
            r.IsDBNull(1) ? null : UtcParse(r.GetString(1)),
            r.IsDBNull(2) ? null : UtcParse(r.GetString(2)));
    }

    /// <summary>
    /// 是否存在参数版本戳与 <paramref name="parameterVersion"/> 不符的 FSRS 卡片
    /// （只看**还有有效 canonical** 的词：没有有效 canonical 的卡片不参与重放，也不该让它永远触发核对）。
    /// 空/缺失的版本戳按冻结默认版本解释——那是换版前所有卡片上的戳，不是"不一致"，
    /// 否则升级后的首次启动会对一份完全正常的库做一次无谓的整库重放（并把经 Context 校准过的到期日改回基线）。
    /// </summary>
    public bool HasCardsOutsideParameterVersion(string parameterVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterVersion);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM fsrs_cards c
            WHERE COALESCE(NULLIF(c.fsrs_parameter_version, ''), $defaultVersion) <> $version
              AND EXISTS (SELECT 1 FROM canonical_reviews r WHERE r.word_key = c.word_key AND r.invalidated = 0)
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$version", parameterVersion);
        cmd.Parameters.AddWithValue("$defaultVersion", SchedulingConfig.ParameterVersion);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// **单事务**发布个人 FSRS 参数。事务内顺序（任一步失败即整体回滚；调用方据此保留 last-good）：
    /// <list type="number">
    /// <item>一致性签名核对：与训练/重放时读到的那份 canonical 集合不符 → 返回 <c>Applied=false</c>（不抛）。</item>
    /// <item>写 <c>personalization_models</c> 的参数行（<c>model_kind='fsrs_params'</c>，含版本戳）。</item>
    /// <item>逐词重放：每条 canonical 的 pre-state 列 + 该词的最终卡片（含版本戳与水位）。</item>
    /// </list>
    /// <para>**不改** <c>scheduler_decisions</c>（历史预测是审计记录，不可回写）、
    /// **不改** <c>words</c> 表（stage/status/next_review_date 是兼容投影，管理动作调整过的 due 必须原样保留）、
    /// **不删**任何 canonical、不重写任何 raw event。</para>
    /// <para>内存中的权重由调用方在**本方法返回成功之后**才更新——反过来会得到"内存已换、库里没有"的不可恢复态。</para>
    /// </summary>
    public FsrsParameterPublishResult PublishFsrsParameters(FsrsParameterPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!string.Equals(publication.RecordKind, SchedulingConfig.FsrsParameterRecordKindParams, StringComparison.Ordinal))
            throw new InvalidDataException("参数发布载荷的 recordKind 必须是 params。");
        ArgumentException.ThrowIfNullOrWhiteSpace(publication.ModelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(publication.ParameterVersion);
        if (!Fsrs6Weights.TryLoad(publication.CoefficientsJson, out var weights, out var loadError))
            throw new InvalidDataException("参数发布的权重 JSON 非法：" + loadError);
        if (!string.Equals(FsrsPersonalization.ParameterVersionOf(weights), publication.ParameterVersion, StringComparison.Ordinal))
            throw new InvalidDataException("参数版本戳与权重内容不一致，拒绝发布。");
        var words = publication.Words ?? [];
        foreach (var word in words)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.WordKey) || word.Card is null)
                throw new InvalidDataException("参数发布载荷包含空的词重放条目。");
            if (!string.Equals(word.Card.FsrsParameterVersion, publication.ParameterVersion, StringComparison.Ordinal))
                throw new InvalidDataException($"词 {word.WordKey} 的重放卡片版本戳与发布版本不一致，拒绝发布。");
        }

        var rewritten = 0;
        DateTime publishedAtUtc;
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                if (!string.Equals(ComputeCanonicalSignature(tx), publication.CanonicalSignature, StringComparison.Ordinal))
                {
                    tx.Rollback();
                    return new FsrsParameterPublishResult(false, false, "canonical 快照在重放与提交之间发生变化", 0, 0);
                }

                // 新基线的生效时刻 = **这次事务实际提交的时刻**，由 store 自己取并写进参数行。
                // 不能沿用"训练完成时刻"：候选可能因 UI 忙/可撤销呈现被延后数小时甚至跨天发布，
                // 而 Context 是**按这个时刻**过滤快照的——用训练时刻会把发布之前捕获的样本
                // 误算成新基线下的样本（反之亦然）。返回给调用方的就是同一个值，不可能漂移。
                publishedAtUtc = DateTime.UtcNow;
                var stampedMetrics = StampPublishInstant(publication.MetricsJson, publishedAtUtc);

                UpsertPersonalizationModel(_connection, tx, new PersonalizationModelState
                {
                    ModelVersion = publication.ModelVersion,
                    TrainedAtUtc = publication.TrainedAtUtc,
                    TrainCutoffUtc = publication.TrainCutoffUtc,
                    Status = ContextMode.Active,
                    CoefficientsJson = publication.CoefficientsJson,
                    ScalerJson = "{}",
                    MetricsJson = stampedMetrics,
                    LastGoodModelVersion = publication.ModelVersion,
                }, SchedulingConfig.FsrsParameterModelKind);

                foreach (var word in words)
                {
                    foreach (var pre in word.PreStates)
                    {
                        RewriteCanonicalPreState(_connection, tx, pre);
                        rewritten++;
                    }
                    UpsertReplayedCard(_connection, tx, word.Card);
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        // 事务已提交：现在才允许内存侧跟上（撤销回放的版本戳也跟着换）。
        _activeFsrsParameterVersion = publication.ParameterVersion;
        BackupAfterMemoryWrite();
        return new FsrsParameterPublishResult(true, false, "", words.Count, rewritten, publishedAtUtc);
    }

    /// <summary>
    /// 把权威发布时刻写进参数行的 metrics（覆盖调用方可能填的训练时刻）。
    /// 只动这两个键，其余字段原样保留——metrics 的形状仍由集成层定义，store 只负责"这一刻"这一件事实。
    /// </summary>
    private static string StampPublishInstant(string? metricsJson, DateTime publishedAtUtc)
    {
        var iso = publishedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        try
        {
            var node = string.IsNullOrWhiteSpace(metricsJson)
                ? new System.Text.Json.Nodes.JsonObject()
                : System.Text.Json.Nodes.JsonNode.Parse(metricsJson) as System.Text.Json.Nodes.JsonObject
                  ?? new System.Text.Json.Nodes.JsonObject();
            node[SchedulingConfig.FsrsParameterActiveSinceMetricKey] = iso;
            node["publishedAtUtc"] = iso;
            return node.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            var fallback = new System.Text.Json.Nodes.JsonObject
            {
                [SchedulingConfig.FsrsParameterActiveSinceMetricKey] = iso,
                ["publishedAtUtc"] = iso,
            };
            return fallback.ToJsonString();
        }
    }

    /// <summary>
    /// 重写一条 canonical 的 pre-state 列（重放的**核心**：撤销必须回放到新基线下的那一对 D/S，
    /// 否则一次撤销就会把卡片打回旧权重算出来的值）。受影响行数必须恰好为 1——
    /// 0 行说明这条 canonical 在事务内已经失效或不存在，此时抛出让整个事务回滚，绝不留下半套 pre-state。
    /// </summary>
    private static void RewriteCanonicalPreState(SqliteConnection connection, SqliteTransaction tx, FsrsPreStateRewrite pre)
    {
        ArgumentNullException.ThrowIfNull(pre);
        ArgumentException.ThrowIfNullOrWhiteSpace(pre.CanonicalId);
        var state = pre.PreState ?? new FsrsPreState { Existed = false };
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE canonical_reviews SET
                pre_state_existed = $preExisted,
                difficulty = $preDifficulty,
                stability = $preStability,
                reps = $preReps,
                lapses = $preLapses,
                state = $preState,
                last_review_at_utc = $preLastReviewAt,
                next_review_at_utc = $preNextReviewAt,
                last_canonical_rating = $preLastRating
            WHERE canonical_id = $id AND invalidated = 0;
            """;
        cmd.Parameters.AddWithValue("$id", pre.CanonicalId);
        cmd.Parameters.AddWithValue("$preExisted", state.Existed ? 1 : 0);
        cmd.Parameters.AddWithValue("$preDifficulty", state.Difficulty);
        cmd.Parameters.AddWithValue("$preStability", state.Stability);
        cmd.Parameters.AddWithValue("$preReps", state.Reps);
        cmd.Parameters.AddWithValue("$preLapses", state.Lapses);
        cmd.Parameters.AddWithValue("$preState", EnumText(state.State));
        BindUtcOrNull(cmd, "$preLastReviewAt", state.LastReviewAtUtc);
        BindUtcOrNull(cmd, "$preNextReviewAt", state.NextReviewAtUtc);
        BindTextOrNull(cmd, "$preLastRating", EnumTextOrNull(state.LastCanonicalRating));
        var affected = cmd.ExecuteNonQuery();
        if (affected != 1)
            throw new InvalidDataException(
                $"重放目标 canonical {pre.CanonicalId} 已不存在或已失效（受影响 {affected} 行），发布整体回滚。");
    }

    /// <summary>
    /// 写入重放后的卡片。水位 <c>last_applied_canonical_seq</c> 取该词当前**有效** canonical 的最大 rowid
    /// ——与撤销的水位守卫（<c>appliedSeq == canonicalSeq</c>）同一口径；水位不对，撤销会永久失效。
    /// </summary>
    private static void UpsertReplayedCard(SqliteConnection connection, SqliteTransaction tx, FsrsCardState card)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO fsrs_cards (
                word_key, difficulty, stability, reps, lapses, state,
                last_review_at_utc, next_review_at_utc, last_canonical_rating,
                fsrs_algorithm_version, fsrs_library_version, fsrs_parameter_version,
                last_applied_canonical_seq
            ) VALUES (
                $wordKey, $difficulty, $stability, $reps, $lapses, $state,
                $lastReviewAt, $nextReviewAt, $lastRating,
                $algoVersion, $libVersion, $paramVersion,
                (SELECT MAX(rowid) FROM canonical_reviews WHERE word_key = $wordKey AND invalidated = 0)
            )
            ON CONFLICT(word_key) DO UPDATE SET
                difficulty = excluded.difficulty,
                stability = excluded.stability,
                reps = excluded.reps,
                lapses = excluded.lapses,
                state = excluded.state,
                last_review_at_utc = excluded.last_review_at_utc,
                next_review_at_utc = excluded.next_review_at_utc,
                last_canonical_rating = excluded.last_canonical_rating,
                fsrs_algorithm_version = excluded.fsrs_algorithm_version,
                fsrs_library_version = excluded.fsrs_library_version,
                fsrs_parameter_version = excluded.fsrs_parameter_version,
                last_applied_canonical_seq = excluded.last_applied_canonical_seq;
            """;
        cmd.Parameters.AddWithValue("$wordKey", card.WordKey);
        cmd.Parameters.AddWithValue("$difficulty", card.Difficulty);
        cmd.Parameters.AddWithValue("$stability", card.Stability);
        cmd.Parameters.AddWithValue("$reps", card.Reps);
        cmd.Parameters.AddWithValue("$lapses", card.Lapses);
        cmd.Parameters.AddWithValue("$state", EnumText(card.State));
        BindUtcOrNull(cmd, "$lastReviewAt", card.LastReviewAtUtc);
        BindUtcOrNull(cmd, "$nextReviewAt", card.NextReviewAtUtc);
        BindTextOrNull(cmd, "$lastRating", EnumTextOrNull(card.LastCanonicalRating));
        cmd.Parameters.AddWithValue("$algoVersion", card.FsrsAlgorithmVersion ?? "");
        cmd.Parameters.AddWithValue("$libVersion", card.FsrsLibraryVersion ?? "");
        cmd.Parameters.AddWithValue("$paramVersion", card.FsrsParameterVersion ?? "");
        cmd.ExecuteNonQuery();
    }

    // =====================================================================
    // 10. 决策日志
    // =====================================================================

    private const string DecisionColumns =
        "decision_id, word_key, canonical_id, timestamp_utc, baseline_interval_days, baseline_retrievability, " +
        "baseline_difficulty, baseline_stability, context_mode, context_delta, context_candidate_interval_days, " +
        "final_interval_days, final_due_at_utc, desired_retention, fsrs_algorithm_version, fsrs_library_version, " +
        "fsrs_parameter_version, aggregation_policy_version, trajectory_schema_version, context_feature_schema_version, " +
        "context_model_version, scheduler_version";

    private static SchedulerDecision ReadDecision(SqliteDataReader r) => new()
    {
        DecisionId = r.GetString(0),
        WordKey = r.GetString(1),
        CanonicalId = r.GetString(2),
        TimestampUtc = UtcParse(r.GetString(3)),
        BaselineIntervalDays = r.IsDBNull(4) ? 0 : r.GetDouble(4),
        BaselineRetrievability = r.IsDBNull(5) ? 0 : r.GetDouble(5),
        BaselineDifficulty = r.IsDBNull(6) ? 0 : r.GetDouble(6),
        BaselineStability = r.IsDBNull(7) ? 0 : r.GetDouble(7),
        ContextMode = EnumParse(OptText(r, 8), ContextMode.Disabled),
        ContextDelta = OptDouble(r, 9),
        ContextCandidateIntervalDays = OptDouble(r, 10),
        FinalIntervalDays = r.IsDBNull(11) ? 0 : r.GetDouble(11),
        FinalDueAtUtc = UtcParse(r.GetString(12)),
        DesiredRetention = r.IsDBNull(13) ? 0 : r.GetDouble(13),
        FsrsAlgorithmVersion = OptText(r, 14) ?? "",
        FsrsLibraryVersion = OptText(r, 15) ?? "",
        FsrsParameterVersion = OptText(r, 16) ?? "",
        AggregationPolicyVersion = OptText(r, 17) ?? "",
        TrajectorySchemaVersion = OptText(r, 18) ?? "",
        ContextFeatureSchemaVersion = OptText(r, 19) ?? "",
        ContextModelVersion = OptText(r, 20) ?? "",
        SchedulerVersion = OptText(r, 21) ?? "",
    };

    public void SaveSchedulerDecision(SchedulerDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                InsertDecision(_connection, tx, decision);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        // 追加型审计数据（决策日志）：不触发全库备份（评审 P1-7）。
    }

    public IReadOnlyList<SchedulerDecision> LoadRecentDecisions(string wordKey, int limit)
    {
        ArgumentNullException.ThrowIfNull(wordKey);
        if (limit <= 0) return [];
        var list = new List<SchedulerDecision>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {DecisionColumns} FROM scheduler_decisions WHERE word_key = $wordKey ORDER BY timestamp_utc DESC, rowid DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$wordKey", wordKey);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadDecision(r));
        return list;
    }

    // =====================================================================
    // 11. outbox（跨存储协调；只保证可重放 + 最终一致）
    // =====================================================================

    public void EnqueueMutations(IReadOnlyList<PendingMutation> mutations)
    {
        if (mutations == null || mutations.Count == 0) return;
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                EnqueueMutations(_connection, tx, mutations, DateTime.UtcNow);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        BackupAfterMemoryWrite();
    }

    public IReadOnlyList<PendingMutationRow> LoadPendingMutations()
    {
        var list = new List<PendingMutationRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, kind, payload_json, attempts FROM mutation_outbox WHERE applied_at_utc IS NULL ORDER BY id ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PendingMutationRow(r.GetInt64(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3)));
        return list;
    }

    /// <summary>outbox 簿记：不改动用户数据，不触发自动备份。</summary>
    public void MarkMutationApplied(long id, DateTime atUtc)
    {
        var at = UtcText(atUtc);
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        // The JSON file was durably published before this acknowledgement. Keep
        // pending rows on rollback, but never retain acknowledged full snapshots.
        cmd.CommandText = """
            UPDATE mutation_outbox SET applied_at_utc = $at WHERE id = $id AND applied_at_utc IS NULL;
            DELETE FROM mutation_outbox WHERE id = $id AND kind = $kind AND applied_at_utc IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", CrossStoreJournal.MutationKind);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>outbox 簿记：不改动用户数据，不触发自动备份。</summary>
    public void RecordMutationFailure(long id, string error)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE mutation_outbox SET attempts = attempts + 1, last_error = $error WHERE id = $id";
        cmd.Parameters.AddWithValue("$error", error ?? "");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
