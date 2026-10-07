using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Data.Sqlite;

namespace Lexi;

/// <summary>
/// 真正的**双进程**重启恢复：阶段 A 在一次进程启动里开真实复习轮、作答一次后直接退出（不结束会话）；
/// 阶段 B 是另一次进程启动，用同一个数据目录，只从磁盘重建并断言恢复结果。
/// </summary>
/// <remarks>
/// 与 <see cref="MemoryRecoveryUiTests"/> 的「进程内重绑」互补：那里能覆盖 UI 接线与幂等，
/// 但进程内重绑与真实重启在恢复资格上并不等价（见 <c>MainWindow.MemoryProcessRunId</c>），
/// 所以恢复要求必须由本文件跨真实进程边界取证，不能拿进程内重绑冒充。
/// 两个阶段互不共享内存，只共享 LEXI_DATA_DIR 下的 SQLite 与 JSON 文件。
/// </remarks>
public static class MemoryRestartTests
{
    private const string StateFile = "recovery-restart-state.json";
    private const string PhaseAReport = "recovery-restart-phase-a.txt";
    private const string ResultFile = "recovery-restart-result.txt";
    private const string Surface = "review";

    private sealed record RestartState(string SessionId, string CheckpointSessionId, int Total, int Completed,
        long CurrentWordId, int CurrentStreak, int Unsure, int EventCount, string RatedEventId,
        string RatedWordKey, string RatedResponse);

    /// <summary>阶段 A：写现场后退出，不结束会话、不完成本轮。</summary>
    public static async Task RunPhaseAAsync(MainWindow window)
    {
        var folder = RequireFolder();
        var report = new List<string>();
        var exit = 0;
        try
        {
            var fixture = Archive(window);
            fixture.AddWord("restart-one", "/wʌn/", "重启甲", "First restart definition");
            fixture.AddWord("restart-two", "/tuː/", "重启乙", "Second restart definition");
            var ids = fixture.GetAllWords().Where(w => w.Word is "restart-one" or "restart-two").Select(w => w.Id).ToArray();
            fixture.ExecuteBatch(ids, "today");
            Call(window, "RefreshWords");
            // ShowPage("review") 内部已经 OpenReviewDeck；再显式开一次会重开一轮、作废刚恢复的队列。
            Call(window, "ShowPage", "review");

            var round = Round(window);
            Require(round.Total == 2 && round.HasCurrent, "phase A did not start a real two-card review round");
            var sessionId = Store(window).CurrentSessionId;
            Require(!string.IsNullOrEmpty(sessionId), "phase A has no live learning session");
            await Invoke(window, "OnReviewRatingAsync", StudyRating.Unsure);
            await Invoke(window, "AdvanceReviewAsync");
            Require(round.HasCurrent && round.Unsure == 1 && round.Completed == 0,
                "phase A lost the rated-but-unfinished reinforcement card");

            var store = MemoryStore(window);
            var events = store.LoadEvents(sessionId);
            var rated = events.Single(e => e.Kind == InteractionEventKind.Rated);
            var checkpoint = store.GetSessionCheckpoint(Surface);
            Require(checkpoint?.SessionId == sessionId, "phase A did not persist its unfinished session checkpoint");
            var state = new RestartState(sessionId, checkpoint!.SessionId, round.Total, round.Completed,
                round.Current.Id, round.CurrentStreak, round.Unsure, events.Count, rated.EventId, rated.WordKey,
                rated.Response?.ToString() ?? "");
            File.WriteAllText(Path.Combine(folder, StateFile), JsonSerializer.Serialize(state));
            report.Add("PASS phase A started and rated a real review round in this process");
            report.Add("PASS phase A persisted an unfinished session checkpoint before exit");
        }
        catch (Exception ex)
        {
            exit = 1;
            report.Add("FAIL " + ex);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, PhaseAReport), report);
            Exit(window, exit);
        }
    }

    /// <summary>阶段 B：全新进程，只凭磁盘断言恢复结果。</summary>
    public static async Task RunPhaseBAsync(MainWindow window)
    {
        var folder = RequireFolder();
        await Task.Yield();
        var report = new List<string>();
        var exit = 0;
        var failures = 0;
        void Check(bool condition, string message)
        {
            if (condition) report.Add("PASS " + message);
            else { failures++; report.Add("FAIL " + message); }
        }
        try
        {
            var state = JsonSerializer.Deserialize<RestartState>(File.ReadAllText(Path.Combine(folder, StateFile)))
                ?? throw new InvalidDataException("phase A state is missing.");
            Call(window, "RefreshWords");
            Call(window, "ShowPage", "review");

            var store = MemoryStore(window);
            var sessionId = Store(window).CurrentSessionId;
            Check(sessionId == state.SessionId, "fresh process resumes the unfinished session instead of opening a new one");
            var round = Round(window);
            Check(round.Total == state.Total && round.Completed == state.Completed && round.Unsure == state.Unsure,
                "fresh process restores the original round totals and counters");
            Check(round.HasCurrent && round.Current.Id == state.CurrentWordId && round.CurrentStreak == state.CurrentStreak,
                "fresh process restores the same current card and reinforcement streak");
            var events = store.LoadEvents(sessionId);
            Check(events.Count == state.EventCount, "restoring invents no extra presentation or rating event");
            var rated = events.SingleOrDefault(e => e.EventId == state.RatedEventId);
            Check(rated is not null && (rated.Response?.ToString() ?? "") == state.RatedResponse && rated.WordKey == state.RatedWordKey,
                "fresh process preserves the original Unsure response event");
            var checkpoint = store.GetSessionCheckpoint(Surface);
            Check(checkpoint?.SessionId == state.SessionId, "the resumed round keeps its durable checkpoint");
            Check(CountReviewSessions(store) == 1, "no second review session was created in the fresh process");
        }
        catch (Exception ex)
        {
            failures++;
            report.Add("FAIL " + ex);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, ResultFile), report);
            Exit(window, failures == 0 ? exit : 1);
        }
    }

    private static long CountReviewSessions(ILearningMemoryStore store)
    {
        var path = store.DatabasePath;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM learning_sessions WHERE mode = 'Review'";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string RequireFolder()
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Restart recovery tests require LEXI_DATA_DIR.");
        Directory.CreateDirectory(folder!);
        return folder!;
    }

    private static object? Call(MainWindow window, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static async Task Invoke(MainWindow window, string name, params object[] args) =>
        await (Task)Call(window, name, args)!;

    private static IVocabularyArchive Archive(MainWindow window) =>
        (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static ILearningMemoryStore MemoryStore(MainWindow window) =>
        (ILearningMemoryStore)typeof(MainWindow).GetField("_memoryStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static LearningMemoryCoordinator Store(MainWindow window) =>
        (LearningMemoryCoordinator)typeof(MainWindow).GetField("_memory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static StudyRound<WordItem> Round(MainWindow window) =>
        (StudyRound<WordItem>)typeof(MainWindow).GetField("_reviewRound", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Exit(MainWindow window, int exit)
    {
        try { window.ForceClose(); } catch { }
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
    }
}
