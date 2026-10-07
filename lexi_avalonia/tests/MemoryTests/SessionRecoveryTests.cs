using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lexi.Tests;

public static class SessionRecoveryTests
{
    private static readonly DateTime Anchor = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public static void Run()
    {
        UndoCountsRemainCorrect();
        RoundRestoresExactContinuation();
        InvalidRoundDoesNotMutate();
        CheckpointSurvivesRestartAndFailures();
        CoordinatorResumesRealEvents();
        AtomicEventAndCommitCheckpoints();
        TrainingUsesOnlyTimelyCanonicalAnchors();
    }

    private static void UndoCountsRemainCorrect()
    {
        var round = new StudyRound<string>();
        round.Reset(["a", "b"], StudyMode.FirstLearn, shuffle: false);
        round.CompleteLearn(); round.CompleteLearn();
        round.Commit(StudyRating.Known); round.UndoLast();
        Program.Check(round.Completed == 0 && round.Known == 0 && round.CurrentStreak == 0,
            "未出队的Known撤销不扣Completed到负数");
        for (var i = 0; i < 3; i++)
        {
            round.Commit(StudyRating.Known); round.UndoLast();
            round.Commit(StudyRating.Unsure); round.UndoLast();
            Program.Check(round.Completed == 0 && round.Known == 0 && round.Unsure == 0,
                "连续改判与撤销的统计不为负数");
        }
        round.Reset(["a"], StudyMode.Review, false);
        round.Commit(StudyRating.Known); round.UndoLast();
        Program.Check(round.Completed == 0 && round.CurrentTarget == 1,
            "复习首次认识完成后撤销恢复首次作答资格");
        round.Reset(["a"], StudyMode.FirstLearn, false);
        round.CompleteCurrent(); round.UndoLast();
        Program.Check(round.Completed == 0 && round.CurrentStep == StudyStep.Learn,
            "手动出队后撤销恢复Learn步骤和完成数量");
    }

    private static void RoundRestoresExactContinuation()
    {
        var original = new StudyRound<string>(new Random(17));
        original.Reset(["new-a", "old-b", "new-c"], w => w.StartsWith("new"), shuffle: false);
        original.CompleteLearn(); original.Commit(StudyRating.Unsure);
        var restored = new StudyRound<string>(new Random(17));
        restored.RestoreJson(original.CaptureJson(w => w), w => w);
        Compare(original, restored, "混合新旧词初次恢复");
        var steps = 0;
        while (!original.IsFinished && steps++ < 40)
        {
            if (original.CurrentStep == StudyStep.Learn)
            { original.CompleteLearn(); restored.CompleteLearn(); }
            else
            {
                var rating = steps == 3 ? StudyRating.Forgot : StudyRating.Known;
                var a = original.Commit(rating); var b = restored.Commit(rating);
                Program.Check(a == b, "恢复后的每次评分结果与原轮次相同");
                if (steps == 5)
                {
                    Program.Check(original.UndoLast() == restored.UndoLast(), "恢复后撤销词/评价/步骤相同");
                }
            }
            Compare(original, restored, "逐步A/B恢复");
            restored = new StudyRound<string>(new Random(17));
            restored.RestoreJson(original.CaptureJson(w => w), w => w);
            Compare(original, restored, "每步重启");
        }
        Program.Check(original.IsFinished && restored.IsFinished, "恢复没有阻断三次认识完成规则");
        var shuffled = new StudyRound<string>(new Random(71));
        shuffled.Reset(["a", "b", "c", "d"], StudyMode.FirstLearn);
        shuffled.CompleteLearn(); shuffled.CompleteLearn();
        var loaded = new StudyRound<string>(new Random(19));
        loaded.RestoreJson(shuffled.CaptureJson(w => w), w => w);
        Compare(shuffled, loaded, "已随机队列的当前与下一遍顺序原样恢复");
    }

    private static void Compare(StudyRound<string> a, StudyRound<string> b, string label)
        => Program.Check(a.CaptureJson(w => w) == b.CaptureJson(w => w), label);

    private static void InvalidRoundDoesNotMutate()
    {
        var round = new StudyRound<string>(); round.Reset(["a"], StudyMode.Review, false);
        var saved = round.CaptureJson(w => w);
        Reject(() => round.RestoreJson(saved.Replace("\"Version\":1", "\"Version\":99"), w => w),
            "未知checkpoint版本拒绝恢复");
        Reject(() => round.RestoreJson(saved, _ => throw new InvalidDataException("missing")),
            "不存在词条拒绝恢复");
        Reject(() => round.RestoreJson("{", w => w), "损坏JSON拒绝恢复");
        Program.Check(round.CaptureJson(w => w) == saved, "失败恢复不修改原内存队列");
    }

    private static void CheckpointSurvivesRestartAndFailures()
    {
        using var box = new Sandbox();
        var coordinator = new LearningMemoryCoordinator(box.Store, new Fsrs6Scheduler(), clock: () => Anchor);
        var session = coordinator.BeginSession(StudyMode.Review, WordSource.Archive, "", 2);
        var round = new StudyRound<string>(); round.Reset(["a", "b"], StudyMode.Review, false);
        round.Commit(StudyRating.Known);
        var saved = new MemorySessionCheckpoint("archive", session, round.CaptureJson(w => w), Anchor);
        box.Store.SaveSessionCheckpoint(saved); box.Reopen();
        Program.Check(box.Store.GetSessionCheckpoint("archive") == saved, "关闭重开SQLite后检查点完整一致");
        var loaded = new StudyRound<string>(); loaded.RestoreJson(saved.QueueJson, w => w);
        loaded.UndoLast(); Program.Check(loaded.Current == "a" && loaded.Completed == 0, "重启后可撤销上次完成");
        Reject(() => box.Store.SaveSessionCheckpoint(saved with { QueueJson = "{" }), "损坏JSON写入失败");
        Reject(() => box.Store.SaveSessionCheckpoint(saved with { SessionId = "missing" }), "不存在session写入失败");
        using (var connection = new SqliteConnection("Data Source=" + box.Path))
        {
            connection.Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER checkpoint_test_fail BEFORE UPDATE ON memory_session_checkpoints BEGIN SELECT RAISE(ABORT,'fixture write failure'); END";
            cmd.ExecuteNonQuery();
        }
        Reject(() => box.Store.SaveSessionCheckpoint(saved with { QueueJson = "{\"different\":true}" }),
            "真实SQLite写失败原样抛出");
        Program.Check(box.Store.GetSessionCheckpoint("archive") == saved, "所有失败保留旧磁盘检查点");
        box.Store.DeleteSessionCheckpoint("archive", "another-session");
        Program.Check(box.Store.GetSessionCheckpoint("archive") == saved, "旧session删除不能误删新session检查点");
        box.Store.DeleteSessionCheckpoint("archive", session);
        Program.Check(box.Store.GetSessionCheckpoint("archive") is null, "完成后只删除匹配表面的检查点");
    }

    private static void CoordinatorResumesRealEvents()
    {
        using var box = new Sandbox(); var now = Anchor;
        var a = new LearningMemoryCoordinator(box.Store, new Fsrs6Scheduler(), clock: () => now);
        var key = WordKey.Archive("recovery");
        var id = a.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        a.OnPresented(key, "p1", true); now = now.AddSeconds(1);
        a.OnRated(key, "p1", StudyRating.Unsure, 0, 0, 1000);
        a.OnRevised(key, "p1", StudyRating.Unsure, StudyRating.Known, 1000);
        a.OnUndone(key, "p1");
        var before = box.Store.LoadEvents(id); var standing = before.First(e => e.Kind == InteractionEventKind.Rated).EventId;
        box.Reopen(); now = now.AddDays(1);
        var b = new LearningMemoryCoordinator(box.Store, new Fsrs6Scheduler(), clock: () => now);
        Program.Check(b.ResumeSession(id) && b.CurrentSessionId == id, "真正恢复原session身份");
        Program.Check(box.Store.LoadEvents(id).Count == before.Count, "Resume不伪造呈现/评分事件");
        b.OnRevised(key, "p1", StudyRating.Unsure, StudyRating.Forgot, 1000);
        var revised = box.Store.LoadEvents(id).Last();
        Program.Check(revised.SupersedesEventId == standing, "恢复后改判仍指向真实standing事件");
        b.OnUndone(key, "p1"); b.OnPresented(key, "p2", true);
        var presented = box.Store.LoadEvents(id).Last();
        Program.Check(presented.SessionAppearanceIndex == 2 && presented.WordAppearanceIndex == 2,
            "恢复后session与历史呈现次数连续");
        Program.Check(TrajectoryReducer.ProjectPresentations(box.Store.LoadEvents(id))[0].FinalValidatedResponse == StudyRating.Unsure,
            "恢复后的撤销与原归约器保持一致");
        Program.Check(!b.ResumeSession("missing") && b.CurrentSessionId == id, "不存在session不覆盖活跃状态");
        var session = box.Store.GetSession(id)!; session.EndedAtUtc = now; box.Store.UpsertSession(session);
        Program.Check(!b.ResumeSession(id), "已结束session拒绝恢复");
    }

    private static void AtomicEventAndCommitCheckpoints()
    {
        using var box = new Sandbox();
        var c = new LearningMemoryCoordinator(box.Store, new Fsrs6Scheduler(), clock: () => Anchor);
        var key = WordKey.Archive("atomic-checkpoint");
        var id = c.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        c.OnPresented(key, "atomic-p", true);
        var first = new MemorySessionCheckpoint("archive", id, "{\"state\":1}", Anchor);
        c.OnRated(key, "atomic-p", StudyRating.Known, 0, 1, 1000, checkpoint: first);
        Program.Check(box.Store.GetSessionCheckpoint("archive") == first, "Rated事件和队列检查点一起落库");
        var final = first with { QueueJson = "{\"state\":2}" };
        using (var connection = new SqliteConnection("Data Source=" + box.Path))
        {
            connection.Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER checkpoint_atomic_fail BEFORE UPDATE ON memory_session_checkpoints BEGIN SELECT RAISE(ABORT,'fixture atomic failure'); END";
            cmd.ExecuteNonQuery();
        }
        var count = box.Store.LoadEvents(id).Count;
        Reject(() => c.OnRevised(key, "atomic-p", StudyRating.Known, StudyRating.Forgot, 1000, final),
            "改判checkpoint写失败透传");
        Program.Check(box.Store.LoadEvents(id).Count == count, "checkpoint失败时改判事件也回滚");
        Reject(() => c.OnUndone(key, "atomic-p", final), "撤销checkpoint写失败透传");
        Program.Check(box.Store.LoadEvents(id).Count == count, "checkpoint失败时撤销事件也回滚");
        Reject(() => c.CommitWord(key, StudyMode.Review, checkpoint: final), "定稿checkpoint写失败透传");
        Program.Check(box.Store.GetCard(key.Key) is null && box.Store.GetSessionCheckpoint("archive") == first,
            "checkpoint失败时canonical/card/队列全部回滚");
        using (var connection = new SqliteConnection("Data Source=" + box.Path))
        {
            connection.Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TRIGGER checkpoint_atomic_fail"; cmd.ExecuteNonQuery();
        }
        var applied = c.CommitWord(key, StudyMode.Review, checkpoint: final);
        Program.Check(applied is { Applied: true } && box.Store.GetSessionCheckpoint("archive") == final,
            "解除失败后canonical与新checkpoint可原子重试");
        var replay = c.CommitWord(key, StudyMode.Review, checkpoint: final);
        Program.Check(replay is { AlreadyApplied: true } && box.Store.GetCard(key.Key)!.Reps == 1,
            "相同定稿重放不会重复评分排期");
        using (var connection = new SqliteConnection("Data Source=" + box.Path))
        {
            connection.Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER checkpoint_undo_fail BEFORE UPDATE ON memory_session_checkpoints BEGIN SELECT RAISE(ABORT,'fixture atomic undo failure'); END";
            cmd.ExecuteNonQuery();
        }
        var beforeUndo = box.Store.LoadEvents(id).Count;
        Reject(() => c.OnUndone(key, "atomic-p", first, applied!.CanonicalId),
            "canonical撤销与checkpoint写失败原样抛出");
        Program.Check(box.Store.GetCard(key.Key)?.Reps == 1
            && box.Store.LoadEvents(id).Count == beforeUndo && box.Store.GetSessionCheckpoint("archive") == final,
            "撤销事务任一步失败时card/standing事件/checkpoint共同回滚");
        using (var connection = new SqliteConnection("Data Source=" + box.Path))
        {
            connection.Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TRIGGER checkpoint_undo_fail"; cmd.ExecuteNonQuery();
        }
        c.OnUndone(key, "atomic-p", first, applied!.CanonicalId);
        Program.Check(box.Store.GetCard(key.Key) is null && box.Store.LoadEvents(id).Count == beforeUndo + 1
            && box.Store.GetSessionCheckpoint("archive") == first,
            "原子撤销回放prestate同时保留真实Undone事件与队列");
        c.OnRated(key, "atomic-p", StudyRating.Known, 0, 1, 1000, first);
        var recommitted = c.CommitWord(key, StudyMode.Review, checkpoint: final)!;
        var beforeRevision = box.Store.LoadEvents(id).Count;
        var priorRating = box.Store.LoadEvents(id).Last(e => e.Kind == InteractionEventKind.Rated).EventId;
        using (var connection = new SqliteConnection("Data Source=" + box.Path))
        {
            connection.Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER checkpoint_revision_fail BEFORE UPDATE ON memory_session_checkpoints BEGIN SELECT RAISE(ABORT,'fixture atomic revision failure'); END";
            cmd.ExecuteNonQuery();
        }
        var triggerFailed = false;
        try { c.OnRevised(key, "atomic-p", StudyRating.Known, StudyRating.Forgot, 1000, first, recommitted.CanonicalId); }
        catch (SqliteException) { triggerFailed = true; }
        Program.Check(triggerFailed, "原子修正checkpoint真实SQLite故障而非非法事件拒绝");
        Program.Check(box.Store.GetCard(key.Key)?.Reps == 1
            && box.Store.LoadEvents(id).Count == beforeRevision && box.Store.GetSessionCheckpoint("archive") == final,
            "修正失败时canonical/card/standing/checkpoint完整保留");
        using (var connection = new SqliteConnection("Data Source=" + box.Path))
        {
            connection.Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TRIGGER checkpoint_revision_fail"; cmd.ExecuteNonQuery();
        }
        c.OnRevised(key, "atomic-p", StudyRating.Known, StudyRating.Forgot, 1000, first, recommitted.CanonicalId);
        var revision = box.Store.LoadEvents(id).Last();
        Program.Check(box.Store.GetCard(key.Key) is null && revision.Kind == InteractionEventKind.Revised
            && revision.SupersedesEventId == priorRating && box.Store.GetSessionCheckpoint("archive") == first,
            "成功修正原子失效旧canonical/回放prestate/追加真实改判并保存队列");
    }

    private static void TrainingUsesOnlyTimelyCanonicalAnchors()
    {
        using var box = new Sandbox(); var now = Anchor;
        var c = new LearningMemoryCoordinator(box.Store, new Fsrs6Scheduler(), clock: () => now);
        var key = WordKey.Archive("anchor");
        var id = c.BeginSession(StudyMode.Review, WordSource.Archive, "", 1);
        c.OnPresented(key, "anchor-p1", true); now = now.AddSeconds(1);
        c.OnRated(key, "anchor-p1", StudyRating.Unsure, 0, 0, 1000);
        now = Anchor.AddDays(1); c.CommitWord(key, StudyMode.Review);
        Program.Check(box.Store.LoadLabeledSamples(Anchor.AddHours(12)).Count == 0,
            "截止点之前捕获但之后打标签的样本不能泄漏进训练");
        Program.Check(box.Store.LoadLabeledSamples(now.AddSeconds(1)).Count == 1, "真实有效canonical锚点可训练");
        var anchor = box.Store.GetSnapshotByPresentation("anchor-p1")!;
        box.Store.SaveContextSnapshot(new ContextFeatureSnapshot
        {
            SnapshotId = "not-anchor", WordKey = key.Key, SessionId = id, PresentationId = "later-presentation",
            CapturedAtUtc = Anchor.AddHours(1), FeaturesJson = "[]", MissingFlagsJson = "[]",
        });
        box.Store.LabelSnapshots([new ContextLabel("not-anchor", StudyRating.Known, true)], now);
        Program.Check(box.Store.LoadLabeledSamples().Count == 1, "同词同session非canonical锚点不混入训练");
        now = now.AddDays(1);
        var first = WordKey.Archive("new-acquisition");
        c.BeginSession(StudyMode.FirstLearn, WordSource.Archive, "", 1);
        c.OnPresented(first, "acquisition-p", true); c.OnRated(first, "acquisition-p", StudyRating.Known, 0, 1, 1000);
        c.CommitWord(first, StudyMode.FirstLearn);
        Program.Check(box.Store.LoadLabeledSamples().Count == 1, "首次学习聚合不冒充真实长期retrieval");
        box.Store.InvalidateCanonical(CanonicalReview.BuildId(key.Key, id), now, "fixture");
        Program.Check(box.Store.LoadLabeledSamples().Count == 0, "失效canonical对应标签不再参与训练");
    }

    private static void Reject(Action action, string label)
    {
        var rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or JsonException or SqliteException)
        { rejected = true; }
        Program.Check(rejected, label);
    }

    private sealed class Sandbox : IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lexi-recovery-test-" + Guid.NewGuid().ToString("N"));
        private VocabularyService _service = null!;
        public string Path => System.IO.Path.Combine(_dir, "vocab.sqlite3");
        public ILearningMemoryStore Store => _service;
        public Sandbox() { Directory.CreateDirectory(_dir); Open(); }
        private void Open() => _service = (VocabularyService)ServiceFactory.OpenArchive(Path);
        public void Reopen() { _service.Dispose(); Open(); }
        public void Dispose() { _service.Dispose(); Directory.Delete(_dir, true); }
    }
}
