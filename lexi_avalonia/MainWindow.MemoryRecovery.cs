using System.Text.Json;

namespace Lexi;

public partial class MainWindow
{
    private MemorySurface _memorySurface;
    private string _memoryRecoveryScope = "";
    private sealed record SavedPresentation(string Identity, string WordKey, string PresentationId);
    private sealed record SavedMemoryRound(string RoundJson, string Scope, SavedPresentation[] Cards,
        string[] Committed, string[] NewWords, string[] Deck, bool Finished, string? RunId = null,
        SavedFinalize[]? PendingFinalize = null);

    /// <summary>
    /// 一次**已经作答、但 canonical 还没提交**的定稿承诺。
    /// Rated/Revised 事件与轮内 checkpoint 是同一次事务，canonical/卡/决策/标签是紧接着的另一次事务
    /// （见 CommitWord）；两次事务之间崩溃，checkpoint 可能已经 <c>Finished</c> 而派生事实还没写。
    /// 因此 <c>Finished</c> 只代表"轮内队列走完了"，绝不代表"派生事实都已提交"。
    /// </summary>
    private sealed record SavedFinalize(string Key, string Mode, string SessionId);

    /// <summary>进程内的待定稿登记（词身份 → 记录模式与所属会话）。</summary>
    private sealed record AwaitingFinalize(StudyMode Mode, string SessionId);

    /// <summary>
    /// 已作答但 canonical 尚未提交的词。随 checkpoint 落盘；只有定稿事务真的成功才移除，
    /// 失败（异常 / 崩溃）时保留，重启后由 <see cref="MemoryReplayPendingFinalize"/> 幂等重放。
    /// </summary>
    private readonly Dictionary<string, AwaitingFinalize> _memoryAwaitingFinalize = new(StringComparer.Ordinal);

    private void MemoryAwaitFinalize(WordKey key, StudyMode mode)
    {
        if (_memory is null || string.IsNullOrEmpty(_memory.CurrentSessionId)) return;
        _memoryAwaitingFinalize[key.Key] = new AwaitingFinalize(mode, _memory.CurrentSessionId);
    }

    private string MemoryCheckpointSurface => _memorySurface.ToString().ToLowerInvariant();
    private bool MemoryRestoreRound(StudyMode mode, WordSource source, string planId)
    {
        _memorySurface = !string.IsNullOrEmpty(planId) ? MemorySurface.Plan
            : mode == StudyMode.Review ? MemorySurface.Review : MemorySurface.Focus;
        _memoryRecoveryScope = $"{source}:{planId}";
        var memory = Memory;
        if (memory is null || _memoryStore is null || !string.IsNullOrEmpty(memory.CurrentSessionId)) return false;
        var checkpoint = _memoryStore.GetSessionCheckpoint(MemoryCheckpointSurface);
        if (checkpoint is null) return false;
        var saved = JsonSerializer.Deserialize<SavedMemoryRound>(checkpoint.QueueJson)
            ?? throw new InvalidDataException("Invalid learning checkpoint.");
        // 定稿重放先于一切，且**既不看运行标识也不看 scope**：它是幂等的持久承诺
        // （CommitWord 由 word×session 唯一约束保证重复定稿不产生第二条 canonical），
        // 与本进程要不要恢复这个轮、轮属于哪个 scope 都无关。重放没做完就保留 checkpoint 原样返回，
        // 下一次启动继续试。
        if (!MemoryReplayPendingFinalize(memory, saved.PendingFinalize)) return false;
        if (saved.Scope != _memoryRecoveryScope) return false;
        // The same plan can have first-learning and repeat-review checkpoints.
        var persistedRound = JsonSerializer.Deserialize<StudyRound<string>.PersistedRound>(saved.RoundJson);
        if (persistedRound is null || persistedRound.Mode != mode) return false;
        if (saved.Finished)
        {
            _memoryStore.DeleteSessionCheckpoint(MemoryCheckpointSurface, checkpoint.SessionId);
            return false;
        }
        // 恢复资格（语义见 MemoryProcessRunId）：只恢复「上一次进程启动」留下的未完成轮。
        // 同一次启动内的重绑会重建协调器（CurrentSessionId 为空）并重新按当前到期队列 Reset 本轮，
        // 此时若把旧 checkpoint 装回去，界面会显示一批已经不再到期的词，刚重建的队列被静默丢弃。
        // RunId 缺失（旧格式负载）按「更早的启动」处理，保持原有恢复行为。
        if (string.Equals(saved.RunId, _memoryRunId, StringComparison.Ordinal)) return false;
        if (!memory.ResumeSession(checkpoint.SessionId)) return false;
        try
        {
            if (_memorySurface == MemorySurface.Review)
                // 恢复队列里可能有负 id 的教材/词形临时卡：只查 _allWords 会直接抛，恢复整轮失败。
                _reviewRound.RestoreJson(saved.RoundJson,
                    id => MemoryResolveReviewIdentity(id) ?? throw new InvalidDataException("恢复复习队列时找不到词：" + id));
            else
                _focusRound.RestoreJson(saved.RoundJson, id => _memorySurface == MemorySurface.Plan
                    ? _activeStudyPlan!.Words.Single(w => w.Id == id).Id : id);
        }
        catch (InvalidDataException ex)
        {
            // 队列里的词已经不存在（档案行被删 / 计划词被移除）。RestoreJson 先整体校验再改活状态，
            // 所以这里安全地回滚到"没恢复"：丢弃这个再也恢复不了的检查点，本轮照常按当前队列重建。
            // 不丢检查点的后果是**永久锁死**——每次启动点开复习页都会在同一处抛出，
            // 而且没有任何地方会清掉这一行。
            MemoryDiagnostic("未完成学习会话已无法恢复（" + ex.Message + "）；已丢弃该检查点，本轮按当前队列重建。");
            try { _memoryStore.DeleteSessionCheckpoint(MemoryCheckpointSurface, checkpoint.SessionId); }
            catch (Exception deleteEx) { MemoryDiagnostic("丢弃无效检查点失败：" + deleteEx.Message); }
            return false;
        }
        ClearPresentationRegistrations();
        foreach (var card in saved.Cards)
            RegisterPresentation(_memorySurface, card.Identity, WordKey.Parse(card.WordKey), card.PresentationId);
        _committedWordKeys.Clear();
        foreach (var key in saved.Committed) _committedWordKeys.Add(key);
        _focusNewWords.Clear();
        foreach (var key in saved.NewWords) _focusNewWords.Add(key);
        if (_memorySurface != MemorySurface.Review)
        {
            _focusDeck = saved.Deck.ToList();
            if (_focusRound.HasCurrent)
            {
                _focusPresentedIdentity = _focusRound.Current;
                _focusPresentedStep = _focusRound.CurrentStep;
                _focusPresentedAnswered = false;
            }
        }
        MemoryDiagnostic("已恢复未完成学习会话与原队列。");
        return true;
    }

    /// <summary>
    /// 幂等重放"已作答、canonical 未提交"的定稿承诺。返回 false 表示还有没做完的，
    /// 调用方必须保留 checkpoint（绝不因为 <c>Finished</c> 就把它删掉）。
    /// </summary>
    private bool MemoryReplayPendingFinalize(LearningMemoryCoordinator memory, SavedFinalize[]? pending)
    {
        if (pending is null || pending.Length == 0) return true;
        foreach (var item in pending)
        {
            if (!Enum.TryParse<StudyMode>(item.Mode, out var mode))
            {
                MemoryDiagnostic("定稿重放：无法识别的记录模式（" + item.Mode + "），已跳过。");
                continue;
            }
            _memoryAwaitingFinalize[item.Key] = new AwaitingFinalize(mode, item.SessionId);
        }
        var completed = true;
        foreach (var item in pending)
        {
            if (!_memoryAwaitingFinalize.TryGetValue(item.Key, out var awaiting)) continue;
            try
            {
                if (!memory.ResumeSession(awaiting.SessionId))
                {
                    // 会话已结束或已不存在 → 这个承诺永远不可能重放成功。明确丢弃并留诊断，
                    // 否则它会永远挡住这个 surface 后续的轮恢复。
                    _memoryAwaitingFinalize.Remove(item.Key);
                    MemoryDiagnostic("定稿重放：原会话已不可用（" + item.Key + "），该承诺无法完成，已放弃。");
                    continue;
                }
                memory.CommitWord(WordKey.Parse(item.Key), awaiting.Mode);
                _memoryAwaitingFinalize.Remove(item.Key);
            }
            catch (Exception ex)
            {
                completed = false;
                MemoryDiagnostic("定稿重放失败（" + item.Key + "）：" + ex.Message + "；已保留待办。");
            }
        }
        return completed;
    }

    private MemorySessionCheckpoint? MemoryBuildCheckpoint()
    {
        if (_memoryStore is null || _memory is null || string.IsNullOrEmpty(_memory.CurrentSessionId)) return null;
        var round = _memorySurface == MemorySurface.Review
            ? _reviewRound.CaptureJson(word => word.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : _focusRound.CaptureJson(word => word);
        var saved = new SavedMemoryRound(round, _memoryRecoveryScope,
            CardsOf(_memorySurface).Values.Select(c => new SavedPresentation(c.Identity, c.Key.Key, c.PresentationId)).ToArray(),
            _committedWordKeys.ToArray(), _focusNewWords.ToArray(), _focusDeck.ToArray(),
            _memorySurface == MemorySurface.Review ? _reviewRound.IsFinished : _focusRound.IsFinished,
            _memoryRunId,
            _memoryAwaitingFinalize.Select(pair => new SavedFinalize(pair.Key, pair.Value.Mode.ToString(), pair.Value.SessionId)).ToArray());
        return new MemorySessionCheckpoint(MemoryCheckpointSurface,
            _memory.CurrentSessionId, JsonSerializer.Serialize(saved), DateTime.UtcNow);
    }
    private void MemorySaveRound()
    {
        if (MemoryBuildCheckpoint() is { } checkpoint) _memoryStore!.SaveSessionCheckpoint(checkpoint);
    }
}
