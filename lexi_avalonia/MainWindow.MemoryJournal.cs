using System.Text.Json;

namespace Lexi;

public partial class MainWindow
{
    private CrossStoreJournal? _memoryJournal;
    private int _planRatingRecognitionBefore;
    private Action? _memoryPendingCompletion;
    private bool MemoryTryCompletePending()
    {
        if (_memoryPendingCompletion is not { } complete) return false;
        try
        {
            MemoryReplayJournal();
            _memoryPendingCompletion = null;
            complete();
        }
        catch (Exception ex) { SetStatus(T("学习计划进度保存失败，请重试。") + " " + ex.Message); }
        return true;
    }

    private void MemoryReplayJournal()
    {
        var journal = _memoryJournal ?? throw new IOException("Learning journal is unavailable.");
        var result = journal.Replay(DateTime.UtcNow);
        if (!result.Succeeded)
            throw new IOException(T("学习计划进度保存失败，请重试。") + " " + result.Error);
    }

    private PendingMutation MemoryPlanSnapshot() => CrossStoreJournal.CreateJsonSnapshot(
        "daily-study-plans.json", JsonSerializer.Serialize(_studyPlans));

    private PendingMutation MemoryIeltsSnapshot() => CrossStoreJournal.CreateJsonSnapshot(
        "ielts-learning.json", JsonSerializer.Serialize(_learningProgress));

    /// <summary>
    /// 计划 / IELTS 两个 JSON sidecar 的**任何一次新编辑之前**都必须先排空待办
    /// （CrossStoreJournal 的契约）：否则更早的未交付快照会在之后被重放，把这次新编辑覆盖掉。
    /// 长期层不可用时没有 journal、也就没有待办，直接放行（既有直写行为不变）。
    /// </summary>
    private bool MemoryDrainPendingBeforeJsonEdit()
    {
        if (Memory is null || _memoryJournal is null) return true;
        try { MemoryReplayJournal(); return true; }
        catch (Exception ex) { SetStatus(T("学习计划保存失败：") + ex.Message); return false; }
    }

    private bool MemorySavePlanProgress()
    {
        if (_planReadFailed) return false;
        try
        {
            if (_memoryStore is null || _memoryJournal is null) throw new IOException("Learning store is unavailable.");
            // 先排空在途待办：它可能是**已被回滚**的那次作答留下的旧目标快照。
            // 旧快照既不能越过这一次的新编辑，也不能因为"已经有待办"就把新状态整个丢掉
            // （否则 JSON 会停在旧状态，而内存里的计划已经是新状态）。排空失败就直接报失败。
            MemoryReplayJournal();
            var snapshot = MemoryPlanSnapshot();
            // 定稿路径已经在 canonical 同事务里入队了同一份状态；内容相同就不重复入队。
            if (!_memoryStore.LoadPendingMutations()
                .Any(row => string.Equals(row.PayloadJson, snapshot.PayloadJson, StringComparison.Ordinal)))
                _memoryStore.EnqueueMutations([snapshot]);
            MemoryReplayJournal();
            return true;
        }
        catch (Exception ex) { SetStatus(T("学习计划保存失败：") + ex.Message); return false; }
    }
}
