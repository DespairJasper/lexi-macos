using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lexi;

/// <summary>JSON 侧写的重放结果；Pending=-1 表示待办读取失败，必须阻止继续完成。</summary>
public sealed record JournalReplayResult(int Applied, int Pending, string? Error)
{
    public bool Succeeded => Pending == 0 && Error is null;
}

/// <summary>
/// canonical 与目标 JSON 快照先在同一 SQLite 事务入库，再重放 JSON；崩溃重试幂等。
/// 调用方须在启动和任何后续 JSON 编辑之前排空待办，不能先写新 JSON 再处理旧快照。
/// </summary>
public sealed class CrossStoreJournal
{
    public const string MutationKind = "json.snapshot.v1";
    private const int PayloadVersion = 1;
    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.Ordinal)
    {
        "daily-study-plans.json", "ielts-learning.json",
    };
    private readonly ILearningMemoryStore _store;
    private readonly string _dataDirectory;
    private readonly object _gate = new();
    private sealed record SnapshotPayload(int Version, string FileName, string Json, string Sha256);

    public CrossStoreJournal(ILearningMemoryStore store, string dataDirectory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(store.DatabasePath));
        if (!string.Equals(_dataDirectory.TrimEnd(Path.DirectorySeparatorChar),
                databaseDirectory?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            throw new ArgumentException("journal 目录必须与当前数据库目录一致。", nameof(dataDirectory));
    }

    /// <summary>只允许两个固定文件名；payload 不接受任意路径，内容必须是合法 JSON。</summary>
    public static PendingMutation CreateJsonSnapshot(string fileName, string json)
    {
        ValidateFileName(fileName);
        var bytes = ValidateJson(json);
        return new PendingMutation(MutationKind,
            JsonSerializer.Serialize(new SnapshotPayload(PayloadVersion, fileName, json, Hash(bytes)), Options));
    }

    /// <summary>按 ID 顺序重放，首个失败即停止；失败条目保留，后续快照不能越过它。</summary>
    public JournalReplayResult Replay(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("重放时刻必须是 UTC。", nameof(nowUtc));
        lock (_gate)
        {
            IReadOnlyList<PendingMutationRow> rows;
            try { rows = _store.LoadPendingMutations().OrderBy(r => r.Id).ToArray(); }
            catch (Exception ex) { return new JournalReplayResult(0, -1, SafeError(ex)); }
            var applied = 0;
            foreach (var row in rows)
            {
                try
                {
                    Apply(row);
                    // 若这里失败，文件可能已完成；下次通过 hash 判定跳过文件写入再确认。
                    _store.MarkMutationApplied(row.Id, nowUtc);
                    applied++;
                }
                catch (Exception ex)
                {
                    var error = SafeError(ex);
                    try { _store.RecordMutationFailure(row.Id, error); }
                    catch { /* 原待办保持未确认，不能丢失原错误。 */ }
                    return new JournalReplayResult(applied, rows.Count - applied, error);
                }
            }
            return new JournalReplayResult(applied, 0, null);
        }
    }

    private void Apply(PendingMutationRow row)
    {
        if (!string.Equals(row.Kind, MutationKind, StringComparison.Ordinal))
            throw new InvalidDataException("未知 journal mutation kind。");
        var payload = JsonSerializer.Deserialize<SnapshotPayload>(row.PayloadJson, Options)
            ?? throw new InvalidDataException("journal payload 为空。");
        if (payload.Version != PayloadVersion) throw new InvalidDataException("不支持的 journal payload 版本。");
        ValidateFileName(payload.FileName);
        var bytes = ValidateJson(payload.Json);
        if (!string.Equals(Hash(bytes), payload.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("journal payload 内容校验失败。");
        if (!Directory.Exists(_dataDirectory)) throw new DirectoryNotFoundException("journal 数据目录不存在。");
        if ((File.GetAttributes(_dataDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("journal 数据目录不能是符号链接。");
        var target = Path.Combine(_dataDirectory, payload.FileName);
        if (Directory.Exists(target)) throw new IOException("journal JSON 目标被目录占用。");
        if (File.Exists(target))
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("journal JSON 目标不能是符号链接。");
            // SHA 匹配 = 之前已完成但未确认；重复应用不改写文件。
            using var input = File.OpenRead(target);
            if (string.Equals(Convert.ToHexString(SHA256.HashData(input)), payload.Sha256, StringComparison.Ordinal)) return;
        }
        var temporary = Path.Combine(_dataDirectory, ".lexi-journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            // 只移除本次创建的随机临时文件，不清理任何用户文件或目录。
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !AllowedFiles.Contains(fileName))
            throw new InvalidDataException("journal 仅接受已登记的 JSON 文件名。");
    }

    private static byte[] ValidateJson(string json)
    {
        if (json is null) throw new InvalidDataException("journal JSON 内容为空。");
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumJsonBytes) throw new InvalidDataException("journal JSON 超过大小上限。");
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new InvalidDataException("journal JSON 必须是对象或数组。");
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    // 不把 payload/路径/用户内容写入数据库错误日志。
    private static string SafeError(Exception ex) => "journal 重放失败（" + ex.GetType().Name + "）。";
}
