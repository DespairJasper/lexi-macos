using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace Lexi.Tests;

/// <summary>只用仓库外临时目录和假store；模拟文件完成/SQLite确认之间的崩溃。</summary>
public static class JournalTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lexi-journal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = DispatchProxy.Create<ILearningMemoryStore, JournalFakeStore>();
        var fake = (JournalFakeStore)(object)store;
        fake.Path = Path.Combine(root, "vocab.sqlite3");
        var journal = new CrossStoreJournal(store, root);
        foreach (var invalid in new[] { "../ielts-learning.json", "/tmp/ielts-learning.json", "other.json" })
            Reject(() => CrossStoreJournal.CreateJsonSnapshot(invalid, "{}"), "拒绝非白名单/任意路径：" + invalid);
        Reject(() => CrossStoreJournal.CreateJsonSnapshot("ielts-learning.json", "{"), "拒绝损坏JSON");
        Reject(() => CrossStoreJournal.CreateJsonSnapshot("ielts-learning.json", "null"), "拒绝非对象/数组JSON");
        Reject(() => new CrossStoreJournal(store, Path.Combine(root, "other")), "目录必须与当前store一致");

        const string initial = "{\"completed\":[\"a\"],\"version\":1}";
        fake.Add(CrossStoreJournal.CreateJsonSnapshot("ielts-learning.json", initial));
        Program.Check(!File.Exists(Path.Combine(root, "ielts-learning.json")), "入outbox不提前写JSON");
        var replay = journal.Replay(Now);
        Program.Check(replay.Succeeded && replay.Applied == 1, "目标JSON重放完成且确认outbox");
        Program.Check(File.ReadAllText(Path.Combine(root, "ielts-learning.json")) == initial, "目标状态精确写入");
        Program.Check(journal.Replay(Now).Applied == 0, "已确认待办重放为no-op");

        // 文件成功但数据库确认失败：重启仍pending，重放必须不重复改写目标。
        fake.Add(CrossStoreJournal.CreateJsonSnapshot("ielts-learning.json", "{\"completed\":[\"a\",\"b\"]}"));
        fake.FailMarkOnce = true;
        replay = journal.Replay(Now);
        Program.Check(!replay.Succeeded && replay.Pending == 1 && fake.Rows[0].Attempts == 1,
            "文件已写而确认失败时待办保留并计失败");
        var target = Path.Combine(root, "ielts-learning.json");
        File.SetLastWriteTimeUtc(target, Now.AddDays(-1));
        var stamp = File.GetLastWriteTimeUtc(target);
        var restarted = new CrossStoreJournal(store, root);
        Program.Check(restarted.Replay(Now).Succeeded, "重启重放完成未确认待办");
        Program.Check(File.GetLastWriteTimeUtc(target) == stamp, "hash相同不重复写文件");

        // 首行失败必须阻止后行越过：避免更晚快照写入后旧快照覆盖。
        Directory.CreateDirectory(Path.Combine(root, "daily-study-plans.json"));
        fake.Add(CrossStoreJournal.CreateJsonSnapshot("daily-study-plans.json", "[]"));
        fake.Add(CrossStoreJournal.CreateJsonSnapshot("ielts-learning.json", "{\"completed\":[\"later\"]}"));
        replay = journal.Replay(Now);
        Program.Check(replay.Pending == 2 && replay.Applied == 0, "目标被目录占用时停止并保留全部待办");
        Program.Check(!File.ReadAllText(target).Contains("later"), "失败不让后续JSON越过");
        Directory.Move(Path.Combine(root, "daily-study-plans.json"), Path.Combine(root, "blocked-directory"));
        Program.Check(restarted.Replay(Now).Succeeded, "恢复可写后按原顺序重放成功");
        Program.Check(File.ReadAllText(target).Contains("later"), "后续最终目标状态写入");

        // 修改/换版/未知kind不得写文件，且不能丢弃待办。
        foreach (var kind in new[] { "version", "fileName", "sha256", "json", "kind" })
        {
            var mutation = CrossStoreJournal.CreateJsonSnapshot("ielts-learning.json", "{}");
            var node = JsonNode.Parse(mutation.PayloadJson)!;
            switch (kind)
            {
                case "version": node["version"] = 99; break;
                case "fileName": node["fileName"] = "../outside.json"; break;
                case "sha256": node["sha256"] = "WRONG"; break;
                case "json": node["json"] = "{"; break;
            }
            fake.Rows.Clear();
            fake.Add(new PendingMutation(kind == "kind" ? "unknown" : mutation.Kind, node.ToJsonString()));
            Program.Check(journal.Replay(Now).Pending == 1, "非法payload保留待办：" + kind);
        }
        Program.Check(!File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "outside.json")), "拒绝payload路径穿越");
        Program.Check(Directory.GetFiles(root, ".lexi-journal-*.tmp").Length == 0, "没有遗留原子写临时文件");
        Reject(() => journal.Replay(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)), "重放时钟要求UTC");

        // 读待办本身失败绝不能被当成"没有待办"：那会让上层显示成功而静默丢掉待办。
        fake.Rows.Clear();
        fake.FailLoadOnce = true;
        var unreadable = journal.Replay(Now);
        Program.Check(!unreadable.Succeeded && unreadable.Pending == -1 && unreadable.Error is not null,
            "读待办失败必须报未成功（Pending=-1），不能当作没有待办");
        Program.Check(journal.Replay(Now).Succeeded, "恢复可读且确实没有待办时才算完成");
    }

    private static void Reject(Action action, string message)
    {
        var rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { rejected = true; }
        Program.Check(rejected, message);
    }

    public class JournalFakeStore : DispatchProxy
    {
        public string Path = "";
        public List<PendingMutationRow> Rows = [];
        public bool FailMarkOnce;
        public bool FailLoadOnce;
        private long _next;
        public void Add(PendingMutation mutation) => Rows.Add(new(++_next, mutation.Kind, mutation.PayloadJson, 0));
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_DatabasePath": return Path;
                case "LoadPendingMutations":
                    if (FailLoadOnce) { FailLoadOnce = false; throw new InvalidOperationException("fake unreadable outbox"); }
                    return Rows.ToArray();
                case "MarkMutationApplied":
                    if (FailMarkOnce) { FailMarkOnce = false; throw new IOException("fake confirmation failure"); }
                    Rows.RemoveAll(r => r.Id == (long)args![0]!); return null;
                case "RecordMutationFailure":
                    var i = Rows.FindIndex(r => r.Id == (long)args![0]!);
                    Rows[i] = Rows[i] with { Attempts = Rows[i].Attempts + 1 }; return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
