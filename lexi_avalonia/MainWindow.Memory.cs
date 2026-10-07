using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Lexi;

/// <summary>
/// P7：长期记忆层（FSRS 卡 / 交互轨迹 / canonical）与界面之间的**唯一**接线点。
/// 所有新逻辑集中在这里，既有界面文件只加一两行调用。
/// </summary>
/// <remarks>
/// <para>
/// <b>旧列与 FSRS 的分工（规格书 §5）</b>：<c>words.stage / status / next_review_date</c> 是
/// <b>兼容投影 + 既有界面显示</b>——评分路径仍然写它们（既有 UI 文案、批量管理动作与既有 UI
/// 测试都依赖这些列，见 ApplyReviewRating / ApplyFocusRating 的注释）；但排期的唯一来源是
/// <see cref="FsrsCardState"/>：一旦某词在真实 retrieval 后建立了 FSRS 卡，它的到期资格就
/// **只**由 <c>fsrs_cards.next_review_at_utc</c> 决定，旧的 MarkUnsure / MarkForgot /
/// ExecuteBatch("review") 再也无法把它拉回今日队列（见 <see cref="MemoryPendingReviewWords"/>）。
/// 两者**不是同一事务**：旧列先写、长期层后写；长期层失败不回滚旧列（与既有「DB 写失败即回滚
/// 轮内状态」的语义一致，下层 CommitWordSession 自身是原子的）。
/// </para>
/// <para>
/// <b>可降级（规格书 §9.4）</b>：长期层是新增旁路。任何一步失败都只把 <see cref="_memoryDisabled"/>
/// 置位并写一条 stderr 诊断，**不**打断轮内状态机、**不**改变界面文案、**不**在普通界面显示
/// FSRS/D/S/R/Context 等术语；QueryDue 失败则回退纯旧口径。
/// </para>
/// <para>
/// <b>呈现标识与词身份成对登记（评审 P0-2）</b>：<c>presentationId</c> 永远不单独存在——
/// 每次呈现都同时登记 <c>pid → 词身份</c> 与 <c>表面 + 词身份 → pid</c>。
/// 撤销时只信「被撤销那个词记下来的那一对」，绝不读「当前显示卡」的标识；
/// 换卡（Advance/Next）之后撤销仍然指向**被撤销的那张卡的呈现**。
/// </para>
/// <para>
/// <b>明确不接入（规格书 §7）</b>：淡写/默写（TypingSession）、听力/自动发音、同义替换、写作草稿、
/// RecordEncounter，以及 ExecuteBatch 的 master/today/stage/restart/delete 与 MarkUnfamiliar
/// 等管理入口，一律不产生 presentation / canonical，也不更新 FSRS。
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>卡片表面。三个表面各有自己的「呈现登记表」，互不串味。</summary>
    private enum MemorySurface
    {
        Review,
        Focus,
        Plan,
    }

    /// <summary>降级标志与诊断的互斥（规格书 §9.5：可能在 UI 线程与后台线程之间被读写）。</summary>
    private readonly object _memoryGate = new();

    private LearningMemoryCoordinator? _memory;
    private ILearningMemoryStore? _memoryStore;

    /// <summary>当前绑定的词库实例。恢复备份会替换 _vocabService（旧连接已 Dispose），据此重新绑定。</summary>
    private IVocabularyArchive? _memoryBoundService;

    private bool _memoryInitialized;
    private bool _memoryDisabled;

    /// <summary>
    /// "个人参数与现存卡片不同源、已暂停写入等待重试"的状态。它**不是** <see cref="_memoryDisabled"/>：
    /// 前者是"这一层暂时不可用，稍后可以恢复"，后者是"本次会话不再重试"。
    /// </summary>
    private bool _memoryRecoveryPending;
    private string _memoryRecoveryError = "";
    private DateTime _memoryRecoveryNotBeforeUtc = DateTime.MinValue;

    /// <summary>恢复重试的最小间隔（避免每次访问都重跑初始化与潜在整库重放）。</summary>
    private static readonly TimeSpan MemoryRecoveryRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 本次**进程启动**的标识。写进每个会话 checkpoint，恢复时比对（规格书 §5 未完成 session 恢复）。
    /// 只有「上一次启动留下的」未完成轮才允许恢复；同一次启动内的重绑（例如从备份恢复数据替换词库）
    /// 并不重启进程，此时轮内状态以内存为准，旧 checkpoint 不得覆盖刚按当前到期队列重建的轮。
    /// 见 <see cref="MemoryRestoreRound"/>。
    /// </summary>
    private static readonly string MemoryProcessRunId = Guid.NewGuid().ToString("N");

    /// <summary>当前进程运行的标识；测试可整体替换以模拟一次新的进程启动。</summary>
    private string _memoryRunId = MemoryProcessRunId;

    /// <summary>Context 特征提供者。P6 之前保持 null——**不写死**，保留注入位。</summary>
    private IContextFeatureProvider? ContextFeatures { get; set; }

    /// <summary>Context 校准器。P6 之前保持 null——**不写死**，保留注入位。</summary>
    private IContextCalibrator? ContextCalibrator { get; set; }

    /// <summary>
    /// 当前挂在协调器上的 Context 校准器实例（触发训练时必须拿到**同一个**实例，
    /// 否则训练的模型不会体现在排期路径上）。词库重新绑定时置空，由 <see cref="Memory"/> 重建。
    /// </summary>
    private IContextCalibrator? _contextTrainer;

    /// <summary>是否已有一次 Context 训练在途；同时只允许一个训练任务。</summary>
    private bool _contextTrainingRunning;

    /// <summary>在途训练的取消源（墙钟预算由它承担）。</summary>
    private CancellationTokenSource? _contextTrainingCts;

    /// <summary>上一次训练尝试**正常结束**的时刻（拟合完成、或门槛未满足只收集）——冷却窗口的基准之一。</summary>
    private DateTime? _contextCompletedAtUtc;

    /// <summary>上一次训练尝试**失败**的时刻（异常 / 取消 / 超时 / 安全回退）——失败同样受同一个冷却窗口约束。</summary>
    private DateTime? _contextFailedAtUtc;

    /// <summary>
    /// 一次已登记的呈现：界面身份（复习页=词 id / 查词页=归一化词形 / 计划卡=计划词 Id）、
    /// 词身份与呈现标识三者在**同一处、同一时刻**写入（评审 P0-2）。
    /// 评分、改判、撤销、定稿一律用这条记录，**不再重新解析词身份**——
    /// 否则「轮中把新词写进档案」会让同一个词的 key 从 <c>form:</c> 变成 <c>archive:</c>，
    /// 呈现与作答配不上对。
    /// </summary>
    private sealed record PresentedCard(string Identity, WordKey Key, string PresentationId);

    private readonly Dictionary<string, PresentedCard> _reviewCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PresentedCard> _focusCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PresentedCard> _planCards = new(StringComparer.Ordinal);

    /// <summary>呈现标识 → 词身份（只作防御性断言：写事件前确认这条事件确实属于这个呈现）。</summary>
    private readonly Dictionary<string, string> _presentationKeys = new(StringComparer.Ordinal);

    // —— 查词页 / IELTS 卡片：同一张卡被重复装填时的去重判据 ——
    private string? _focusPresentedIdentity;
    private StudyStep? _focusPresentedStep;
    private bool _focusPresentedAnswered;

    /// <summary>
    /// §9.2：本轮「新词」——在 <c>StudyRound.Reset</c> 的**同一时刻**捕获（档案里查不到）。
    /// 不能事后重算：RateFocusedWordAsync 里的 AddCurrentWordToVocab 会在轮中把新词写进档案。
    /// 键一律用 <see cref="WordKeyResolver.FormC"/> 归一化后的词形，与词身份解析同一口径。
    /// </summary>
    private readonly HashSet<string> _focusNewWords = new(StringComparer.Ordinal);

    /// <summary>本次会话内已定稿的词身份；撤销该词时据此决定是否要失效 canonical。</summary>
    private readonly HashSet<string> _committedWordKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// 本次「进入卡片」是否已经开过学习会话。IELTS 单词卡入口会连续开两轮队列
    /// （MainWindow.Ielts.cs 的 EnterWordFocus 会先按查词页开一轮，紧接着按词表重开），
    /// 两次属于同一次进入，不能开两个会话。
    /// </summary>
    private bool _focusMemorySessionLive;
    private bool _memoryDeferFocusInitialization;

    // ==================================================================================
    // 初始化与降级
    // ==================================================================================

    /// <summary>长期记忆协调器：惰性建立一次；不可用时返回 null 并保持降级，绝不向上抛。</summary>
    private LearningMemoryCoordinator? Memory
    {
        get
        {
            // 恢复备份会替换 _vocabService 并 Dispose 旧连接：先确认绑定，避免打到死连接上。
            if (!ReferenceEquals(_memoryBoundService, _vocabService)) RebindMemory();
            if (MemoryDisabled) return null;
            if (_memory is not null) return _memory;
            if (_memoryInitialized) return null;
            // 恢复待重试：节流，避免每次访问都重跑一遍初始化（含可能的整库重放）。
            if (_memoryRecoveryPending && DateTime.UtcNow < _memoryRecoveryNotBeforeUtc) return null;
            _memoryInitialized = true;
            try
            {
                var store = ServiceFactory.OpenMemory(_vocabService);
                _memoryStore = store;
                _memoryJournal = new CrossStoreJournal(store, Path.GetDirectoryName(store.DatabasePath)!);
                var recovery = _memoryJournal.Replay(DateTime.UtcNow);
                if (!recovery.Succeeded) throw new IOException("Pending learning writes could not be replayed: " + recovery.Error);
                if (recovery.Applied > 0)
                {
                    if (_studyPlanStore is not null) _studyPlans = _studyPlanStore.Load();
                    if (File.Exists(LearningProgressPath)) _learningProgress = LearningProgress.Load(LearningProgressPath);
                }
                // P6：Context 特征提供者 + 校准器接上真实实现（两者都挂在同一条 store 上）。
                // ContextCalibrator 在构造时从 personalization_models 恢复模型与阶段
                // （PersonalizationModelState），因此重启后不会把已通过资格验证的 Active 状态丢掉。
                // 这里的 `??` 保留注入位：ContextFeatures / ContextCalibrator 若非 null（测试 / 诊断）优先。
                // 权重的**单一持有者**：排期器、Context 特征提供者（算基线 R）与 Context 校准器
                // （解候选间隔）共享同一个实例，因此个人参数换版对三者同时可见。
                // 各持一份权重会得到"排期新权重、快照 R 用旧权重"的静默不一致。
                var weights = new SchedulerWeights();
                var scheduler = new Fsrs6Scheduler(weights);

                // ① 先解出**有效个人参数**并做重启一致性核对（必要时用目标权重整库重放）。
                //    必须在暴露协调器之前完成：核对失败意味着 holder 的权重与库里现存的
                //    D/S/pre-state 不同源，此时把协调器交出去就等于允许"新权重 + 旧 D/S"继续写库。
                var startup = MemoryBuildPersonalization(store, weights);
                if (!startup.Consistent)
                {
                    // 安全边界：不暴露可写的长期记忆层，保留库与可重试的恢复状态。
                    // 不置 _memoryDisabled（那是"本次会话不再重试"），而是留一条有节流的重试路径。
                    _memoryRecoveryPending = true;
                    _memoryRecoveryError = startup.Error ?? startup.Reconcile?.Detail ?? "";
                    _memoryRecoveryNotBeforeUtc = DateTime.UtcNow + MemoryRecoveryRetryDelay;
                    _memoryInitialized = false;
                    MemoryDiagnostic("长期记忆层暂停写入：个人参数与现存卡片不同源，"
                        + "已保留数据并在稍后重试恢复。原因：" + _memoryRecoveryError);
                    return null;
                }

                // ② 有效基线确定之后才构造 Context：它与集成层共用同一个有效参数解析器
                //    （见 FsrsPersonalization.ResolveEffectiveWeights），因此不会出现
                //    "Context 按一个从未生效的版本恢复 Active、而 holder 已经回退"的分裂。
                var calibrator = ContextCalibrator ?? new ContextCalibrator(store, scheduler);
                // 训练触发点（本文件）必须操作**协调器正在用的那一个**校准器实例，否则训出来的模型
                // 不会出现在排期路径上。只保存引用，不改变所有权。
                _contextTrainer = calibrator;
                _memoryPersonalization = startup.Service;
                if (_memoryPersonalization is not null) _memoryPersonalization.AttachContextCalibrator(calibrator);
                _parameterRuntime = MemoryBuildParameterRuntime(_memoryPersonalization);
                _memory = new LearningMemoryCoordinator(store, scheduler,
                    ContextFeatures ?? new ContextFeatureProvider(store, scheduler),
                    calibrator);
                return _memory;
            }
            catch (Exception ex)
            {
                MarkMemoryDisabled("Initialize：" + ex.Message);
                _memory = null;
                return null;
            }
        }
    }

    /// <summary>
    /// 重新绑定到当前词库实例（首次访问时 _memoryBoundService 为 null，也走这里）。
    /// 恢复备份后旧连接已 Dispose，协调器与 store 必须重建；呈现登记表随之作废。
    /// </summary>
    private void RebindMemory()
    {
        var replaced = _memoryBoundService is not null;
        _memoryBoundService = _vocabService;
        _memory = null;
        _memoryStore = null;
        _memoryJournal = null;
        _memoryInitialized = false;
        _memoryRecoveryPending = false;
        _memoryRecoveryNotBeforeUtc = DateTime.MinValue;
        MemoryCancelContextTraining();
        // 在途的个人参数训练守着旧连接，且它的候选引用的是旧库的 canonical 快照签名——一并作废。
        // Invalidate 会推进代际，因此"已经拟合完但续体还没跑"的旧任务也不会把候选交给新库。
        MemoryCancelParameters();
        ClearPresentationRegistrations();
        _committedWordKeys.Clear();
        // 词库被替换 = 换了一份数据；旧库里登记的待定稿承诺不再适用于新库，改由新库自己的 checkpoint 带出来。
        _memoryAwaitingFinalize.Clear();
        // source（教材/词形）临时卡的身份映射与实例缓存同样属于"旧库"。不清掉的话，
        // 负 id 仍能解析到一个已经随旧库释放的 WordItem，复习轮会拿着死对象继续跑。
        _memorySourceReviewKeys.Clear();
        _memorySourceReviewWords.Clear();
        // 撤销栈引用的是**旧库**的词（words.id / 档案 uuid）。换库后它们不再属于当前数据集，
        // 留着会让撤销落到一个已经不属于这份数据的词上；真实进程重启时这些本来就是空的。
        _reviewUndoWordId = null;
        _focusUndo.Clear();
        _focusMemorySessionLive = false;
        lock (_memoryGate) _memoryDisabled = false;
        if (replaced) MemoryDiagnostic("词库已被替换（恢复备份），长期记忆层已重新绑定到新实例。");
    }

    /// <summary>长期记忆旁路是否已停用（本次会话内不再重试）。</summary>
    private bool MemoryDisabled
    {
        get { lock (_memoryGate) return _memoryDisabled; }
    }

    private void MarkMemoryDisabled(string detail)
    {
        bool first;
        lock (_memoryGate) { first = !_memoryDisabled; _memoryDisabled = true; }
        if (first)
            MemoryDiagnostic(detail + " —— 已停用长期记忆旁路（本次会话不再重试）；轮内状态机与旧列表照常工作。");
    }

    /// <summary>
    /// 诊断输出走既有 stderr 通道（与 LEXI_HOTKEY_TRACE / LEXI_DEBUG_STACK 一致）：
    /// 普通界面**不出现** FSRS / D / S / R / Context 等术语（规格书 §8）。
    /// </summary>
    private static void MemoryDiagnostic(string message)
    {
        try { Console.Error.WriteLine("[memory] " + message); }
        catch { /* 诊断本身绝不能影响流程 */ }
    }

    /// <summary>记录型调用的统一外壳：失败只降级，不打断调用方。</summary>
    private void MemoryRun(string what, Action<LearningMemoryCoordinator> action)
    {
        var memory = Memory;
        if (memory is null) return;
        try { action(memory); }
        catch (Exception ex) { MarkMemoryDisabled(what + "：" + ex.Message); }
    }

    // Persistence of a real response is required: surface handlers restore their checkpoint
    // and report failure. Training and timing may fall back, retrieval writes may not.
    private void MemoryRequiredRun(string what, Action<LearningMemoryCoordinator> action)
    {
        var memory = Memory ?? throw new InvalidOperationException(T("本次复习未完成，请重试：") + what);
        action(memory);
    }

    /// <summary>窗口失焦 / 回到前台时暂停与恢复作答延迟计时（规格书 §6）。</summary>
    private void ConfigureLearningMemory()
    {
        Activated += (_, _) => MemoryResumeLatency();
        Deactivated += (_, _) => MemoryPauseLatency();
    }

    private void MemoryPauseLatency() => MemoryRun("PauseLatency", m => m.PauseLatency());

    private void MemoryResumeLatency() => MemoryRun("ResumeLatency", m => m.ResumeLatency());

    // ==================================================================================
    // 会话
    // ==================================================================================

    /// <summary>开轮：四个开轮点（复习页 / 查词页专注轮 / IELTS 单词卡 / 计划单词卡）各调一次。</summary>
    private void MemoryBeginSession(StudyMode mode, WordSource primarySource, string planId, int plannedWordCount)
    {
        if (MemoryRestoreRound(mode, primarySource, planId)) return;
        _committedWordKeys.Clear();
        // 上一轮留下的延迟完成回调渲染的是**别的表面**的卡；新的一轮开始后它不该再抢走
        // 这一次点击（Rate*/OnReviewRating 开头都会先执行它）。待交付的 JSON 目标仍在 outbox 里，
        // 由下一次成功排空或下次启动的 replay 交付，不会因为丢掉这个回调而消失。
        if (_memoryPendingCompletion is not null)
        {
            _memoryPendingCompletion = null;
            MemoryDiagnostic("已放弃上一轮的延迟完成回调（表面已切换）；待交付目标仍保留在 outbox。");
        }
        // 新会话下协调器的呈现状态整体清空：本地登记表必须同步作废，避免复用上一会话的标识。
        ClearPresentationRegistrations();
        // 开轮**不是**一次真实 retrieval 的持久化承诺：长期层不可用时必须照常开轮（本类契约：
        // 任何一步失败只降级、不打断轮内状态机）。若这里用会抛的 MemoryRequiredRun，
        // 一个可选的写入失败就会让复习页/卡片轮从此打不开——那比"记不下这次评分"严重得多。
        var memory = Memory;
        if (memory is null)
        {
            MemoryDiagnostic("长期记忆层不可用，本轮按轮内状态机与旧列表照常进行。");
            return;
        }
        memory.BeginSession(mode, primarySource, planId, plannedWordCount);
        MemorySaveRound();
        // T1（触发时机 ①）：一轮开始——检查训练资格；冷却内 / 有在途任务 / 样本不足时**只记一条诊断**。
        MemoryTryTrainContext("一轮开始");
    }

    /// <summary>
    /// 查词页 / IELTS 单词卡的会话：本次「进入卡片」只开一次。
    /// 返回 true 表示本次调用真的开了新会话（即这是一次新的进入）。
    /// </summary>
    private bool MemoryEnsureFocusSession()
    {
        if (_focusMemorySessionLive) return false;
        _focusMemorySessionLive = true;
        _focusPresentedIdentity = null;
        _focusPresentedStep = null;
        _focusPresentedAnswered = false;
        MemoryBeginSession(StudyMode.FirstLearn, MemoryFocusSource(), "", _focusDeck.Count);
        return true;
    }

    /// <summary>离开卡片表面（ShowPage 已确认 _wordFocusActive == false）：下一次进入重新开会话。</summary>
    private void MemoryFocusSurfaceExited()
    {
        // ShowPage 每次导航都会调到这里；只有「本次真的开过卡片会话」才算一次「离开学习页」。
        // 提前返回与原先「无条件置 false」语义相同（置 false 本身幂等），但避免每次导航都做一次资格检查。
        if (!_focusMemorySessionLive) return;
        _focusMemorySessionLive = false;
        // T1（触发时机 ②，兜底）：用户可能在轮末直接离开学习页而不点「完成」，
        // 因此离开卡片表面同样是一次「一轮结束」的检查点。
        MemoryTryTrainContext("离开学习页");
    }

    /// <summary>§1：source 按词卡入口判定（查词页快照的 Page == "ielts" → Ielts）。</summary>
    private WordSource MemoryFocusSource() =>
        _wordFocusSnapshot?.Page == WordKeyResolver.IeltsPageKind ? WordSource.Ielts : WordSource.Archive;

    // ==================================================================================
    // Context 训练触发（机制在 Application/Memory；这里只决定「什么时候值得训一次」）
    // ==================================================================================

    /// <summary>
    /// 「现在该不该发起一次 Context 训练」的**纯判定**：不读时钟、不碰 store、无副作用。
    /// <list type="bullet">
    /// <item>已有训练在途 → 不训练（绝不并发第二次）。</item>
    /// <item>合格样本数不足 <see cref="SchedulingConfig.ContextMinimumSamples"/> → 不训练（保持 ColdStart，只收集）。</item>
    /// <item>距上次正常结束不足 <see cref="SchedulingConfig.ContextTrainingCooldownMinutes"/> 分钟 → 不训练。</item>
    /// <item>上次尝试失败且失败后不足同一个冷却窗口 → 不训练（失败既不缩短冷却，也不延长它）。</item>
    /// </list>
    /// <para>
    /// 时钟回拨时 <c>now - last</c> 为负，会落在「不足冷却」一侧 → 保守地不训练（不做补偿）：
    /// 宁可少训一次，也不因为系统时间异常而反复全表读。
    /// </para>
    /// <para>
    /// 判定所需的全部常量都取自 <see cref="SchedulingConfig"/>，这里不新造任何魔数。
    /// </para>
    /// </summary>
    /// <param name="nowUtc">当前 UTC 时刻（由调用方传入）。</param>
    /// <param name="lastCompletedAtUtc">上一次训练尝试**正常结束**的时刻；从未有过为 null。</param>
    /// <param name="lastFailedAtUtc">上一次训练尝试**失败**的时刻；从未失败过为 null。</param>
    /// <param name="hasPendingTask">是否已有训练在途。</param>
    /// <param name="evaluableSampleCount">当前合格样本数（已加标签的快照数）。</param>
    internal static bool ShouldTrainContext(
        DateTime nowUtc, DateTime? lastCompletedAtUtc, DateTime? lastFailedAtUtc,
        bool hasPendingTask, int evaluableSampleCount)
    {
        if (hasPendingTask) return false;
        if (evaluableSampleCount < SchedulingConfig.ContextMinimumSamples) return false;
        var cooldown = TimeSpan.FromMinutes(SchedulingConfig.ContextTrainingCooldownMinutes);
        if (lastCompletedAtUtc is { } completedAt && nowUtc - completedAt < cooldown) return false;
        if (lastFailedAtUtc is { } failedAt && nowUtc - failedAt < cooldown) return false;
        return true;
    }

    /// <summary>
    /// Context 训练的**唯一**触发入口：一轮开始、一轮结束各检查一次资格。
    /// <para>
    /// <b>绝不</b>在每次评价（<c>CommitWord</c> / <c>OnRated</c> / <c>MemoryCommitCard</c>）里调用——
    /// 那正是用户明确禁止的「每点一次就全量训练」。触发频率由冷却、在途标志与样本门槛三者共同限死：
    /// 一次学习会话里至多训一次（冷却 30 分钟）。
    /// </para>
    /// <para>
    /// <b>线程纪律（关键约束）</b>：<c>VocabularyService</c> 只有一条 <c>SqliteConnection</c> 且不加锁，
    /// 因此样本读取（<see cref="ILearningMemoryStore.LoadLabeledSamples"/>）必须落在**调用线程**，
    /// 也就是 UI 线程上（一次快速只读查询）。纯 CPU 的拟合由 <c>ContextCalibrator</c> 自己在
    /// <c>Task.Run</c> 上跑，所以本方法**不阻塞 UI**。
    /// </para>
    /// <para>
    /// <b>降级纪律</b>：读样本失败、训练异常、取消、超时都只写一条 stderr 诊断并（失败时）等一个冷却，
    /// 不置 <c>_memoryDisabled</c>、不抛给调用方、不改变任何学习流程的可见状态。
    /// </para>
    /// </summary>
    /// <param name="trigger">触发时机（只进诊断，不进界面）。</param>
    private void MemoryTryTrainContext(string trigger)
    {
        // 轮级触发的**唯一**入口同时服务两个训练器：Context 残差校准与个人 FSRS 参数。
        // 两者各有独立的门槛、冷却与在途标志，因此这里只是"在同一个时机各查一次资格"，
        // 不会互相阻塞。放在这里而不是新增调用点，是为了不碰 MainWindow.Review.cs /
        // MainWindow.Learning.cs（它们各自只调一次本方法，触发时机已经覆盖一轮开始/结束/离开学习页）。
        MemoryTryTrainParameters(trigger);
        MemoryTryTrainContextCore(trigger);
    }

    /// <summary>Context 训练的本体（触发时机由 <see cref="MemoryTryTrainContext"/> 统一转发）。</summary>
    private void MemoryTryTrainContextCore(string trigger)
    {
        if (MemoryDisabled) return;
        var store = _memoryStore;
        var trainer = _contextTrainer;
        if (store is null || trainer is null) return;

        var nowUtc = DateTime.UtcNow;
        // 第一道闸：不碰数据库就能断定的部分（在途 / 冷却 / 上次失败）。样本数按「刚好达到门槛」代入，
        // 在这里只用于判「不训练」；真要训练前会再用**真实**样本数判一次——
        // 这样每次开轮 / 轮末都不会白白全表扫一遍已加标签的快照。
        if (!ShouldTrainContext(nowUtc, _contextCompletedAtUtc, _contextFailedAtUtc,
                _contextTrainingRunning, SchedulingConfig.ContextMinimumSamples))
        {
            MemoryContextDiagnostic($"{trigger}｜跳过：{MemoryContextSkipReason(nowUtc)}。");
            return;
        }

        int sampleCount;
        try { sampleCount = store.LoadLabeledSamples(nowUtc).Count; }
        catch (Exception ex)
        {
            // 连样本都读不出来：按失败记账并等一个冷却，绝不打断本轮学习。
            _contextFailedAtUtc = nowUtc;
            MemoryContextDiagnostic($"{trigger}｜样本读取失败，跳过（{ex.GetType().Name}：{ex.Message}）。");
            return;
        }

        if (!ShouldTrainContext(nowUtc, _contextCompletedAtUtc, _contextFailedAtUtc, _contextTrainingRunning, sampleCount))
        {
            MemoryContextDiagnostic(
                $"{trigger}｜跳过：合格样本 {sampleCount} < {SchedulingConfig.ContextMinimumSamples}（保持 ColdStart，只收集）。");
            return;
        }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(SchedulingConfig.ContextTrainingTimeoutSeconds));
        _contextTrainingCts = cts;
        _contextTrainingRunning = true;
        MemoryContextDiagnostic($"{trigger}｜开始训练（合格样本 {sampleCount} 条）。");
        // 返回的 Task 已在方法内吞掉全部异常（含收尾阶段的），因此这里不需要再观察它。
        _ = MemoryTrainContextAsync(trigger, trainer, nowUtc, sampleCount, cts);
    }

    /// <summary>
    /// 跑一次训练并把结果折算成冷却时钟。**在 UI 线程上启动**——<c>TrainAsync</c> 的样本读取发生在
    /// 调用线程上，这正是那条单连接不被跨线程访问的前提；<c>await</c> 之后续跑回 UI 线程，
    /// 所以状态复位与诊断也在 UI 线程上（无需再 Post）。
    /// <para>本方法不向外抛任何异常：这里已经在 UI 线程上，抛出去就是用户可见的崩溃。</para>
    /// </summary>
    private async Task MemoryTrainContextAsync(
        string trigger, IContextCalibrator trainer, DateTime nowUtc, int sampleCount, CancellationTokenSource cts)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ContextTrainingResult? result = null;
        string? error = null;
        try { result = await trainer.TrainAsync(nowUtc, cts.Token); }
        catch (Exception ex) { error = ex.GetType().Name + "：" + ex.Message; }

        try
        {
            // 先复位标志，再做任何可能出问题的事（诊断 / 字符串拼接）——否则一次异常会让训练永久卡住。
            _contextTrainingRunning = false;
            if (ReferenceEquals(_contextTrainingCts, cts)) _contextTrainingCts = null;
            cts.Dispose();

            var elapsedMs = (long)Math.Round((Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency);
            var finishedAt = DateTime.UtcNow;

            if (result is null)
            {
                _contextFailedAtUtc = finishedAt;
                MemoryContextDiagnostic(
                    $"{trigger}｜训练失败（{elapsedMs} ms / 样本 {sampleCount}）——{error}；学习流程不受影响。");
                return;
            }

            if (result.Outcome == ContextTrainingOutcome.SafeFallback)
            {
                // 取消 / 超时 / 极端系数 / 数值非法：保留 last-good，退到 Shadow，同样等一个冷却再试。
                _contextFailedAtUtc = finishedAt;
                MemoryContextDiagnostic(
                    $"{trigger}｜安全回退（{elapsedMs} ms / 样本 {sampleCount}）——保留 last-good「{result.ModelVersion}」，"
                    + $"阶段 {result.Mode}：{result.Detail}；学习流程不受影响。");
                return;
            }

            // 拟合完成、门槛未满足只收集、冷却内跳过：都算一次「正常结束」，下次要再等一个冷却。
            _contextCompletedAtUtc = finishedAt;
            _contextFailedAtUtc = null;
            MemoryContextDiagnostic(
                $"{trigger}｜结束（{elapsedMs} ms / 样本 {sampleCount}）——结果 {result.Outcome}，阶段 {result.Mode}，"
                + $"模型「{result.ModelVersion}」：{result.Detail}");
        }
        catch (Exception ex)
        {
            MemoryContextDiagnostic($"{trigger}｜收尾异常（{ex.GetType().Name}：{ex.Message}），已忽略。");
        }
    }

    /// <summary>词库被替换（恢复备份）时把在途训练取消掉：它守着旧连接，让它尽快收敛。</summary>
    private void MemoryCancelContextTraining()
    {
        _contextTrainer = null;
        MemoryCancelContextTrainingInFlight();
    }

    /// <summary>
    /// 只取消**在途**的那一次 Context 训练，保留 <c>_contextTrainer</c> 绑定
    /// （换版前让路用：取消之后仍然要能再次触发训练，所以不能把校准器引用也清掉）。
    /// <para>取消不是"撤销"：在途任务仍会跑完并调用 <c>TrainAsync</c> 的收尾。真正防止它把旧基线的模型
    /// 写回去的是校准器内部的**基线世代检查**——世代在 <c>ApplyFsrsBaseline</c> 里自增，
    /// 收尾时发现已变就丢弃结果且不落库。</para>
    /// </summary>
    private void MemoryCancelContextTrainingInFlight()
    {
        var cts = _contextTrainingCts;
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已经收尾，无需再取消 */ }
    }

    /// <summary>「为什么这次不训练」的可审计说明（只进诊断，绝不进界面）。</summary>
    private string MemoryContextSkipReason(DateTime nowUtc)
    {
        var cooldown = TimeSpan.FromMinutes(SchedulingConfig.ContextTrainingCooldownMinutes);
        if (_contextTrainingRunning) return "已有一轮训练在途";
        if (_contextFailedAtUtc is { } failedAt && nowUtc - failedAt < cooldown)
            return $"距上次训练失败 {Math.Max(0, (nowUtc - failedAt).TotalMinutes):F0} 分钟，未过 {SchedulingConfig.ContextTrainingCooldownMinutes} 分钟冷却";
        if (_contextCompletedAtUtc is { } completedAt && nowUtc - completedAt < cooldown)
            return $"距上次训练 {Math.Max(0, (nowUtc - completedAt).TotalMinutes):F0} 分钟，未过 {SchedulingConfig.ContextTrainingCooldownMinutes} 分钟冷却";
        return "样本数未达门槛";
    }

    /// <summary>
    /// 训练触发与结果的诊断通道（与 <see cref="_memoryDisabled"/> 同一处风格：stderr + <c>[memory]</c> 前缀）。
    /// 规格书 §8：普通界面**不出现**任何算法术语。
    /// </summary>
    private static void MemoryContextDiagnostic(string message) => MemoryDiagnostic("Context 训练｜" + message);

    // ==================================================================================
    // 呈现登记（P0-2：标识与词身份成对，绝不各存各的）
    // ==================================================================================

    private Dictionary<string, PresentedCard> CardsOf(MemorySurface surface) => surface switch
    {
        MemorySurface.Review => _reviewCards,
        MemorySurface.Focus => _focusCards,
        _ => _planCards,
    };

    /// <summary>登记一次呈现：界面身份、词身份、呈现标识三者同时写入。</summary>
    private void RegisterPresentation(MemorySurface surface, string identity, WordKey key, string presentationId)
    {
        CardsOf(surface)[identity] = new PresentedCard(identity, key, presentationId);
        _presentationKeys[presentationId] = key.Key;
    }

    /// <summary>取该表面为这个界面身份登记的呈现记录（撤销/评分/定稿都只信它）。</summary>
    private bool MemoryTryPresentedCard(MemorySurface surface, string? identity, out PresentedCard card)
    {
        card = null!;
        if (string.IsNullOrEmpty(identity)) return false;
        if (!CardsOf(surface).TryGetValue(identity, out var found)) return false;
        card = found;
        return true;
    }

    /// <summary>
    /// 防御性校验：这个呈现是否确实属于这个词。不该触发（登记表已经是成对的），
    /// 一旦触发说明接线出错，只记诊断并跳过该条记录，不把错配的标识交给协调器。
    /// </summary>
    private bool MemoryPresentationMatches(string presentationId, WordKey key)
    {
        if (!_presentationKeys.TryGetValue(presentationId, out var owner))
        {
            MemoryDiagnostic("呈现 " + presentationId + " 没有登记过，跳过这条记录。");
            return false;
        }
        if (string.Equals(owner, key.Key, StringComparison.Ordinal)) return true;
        MemoryDiagnostic("呈现 " + presentationId + " 属于 " + owner + "，本次事件却是 " + key.Key + "：跳过该条记录（不降级）。");
        return false;
    }

    private void ClearPresentationRegistrations()
    {
        _presentationKeys.Clear();
        _reviewCards.Clear();
        _focusCards.Clear();
        _planCards.Clear();
        _focusPresentedIdentity = null;
        _focusPresentedStep = null;
        _focusPresentedAnswered = false;
    }

    // ==================================================================================
    // 词身份解析（唯一入口 WordKeyResolver）
    // ==================================================================================

    private static bool MemoryTryArchiveKey(WordItem? item, out WordKey key)
    {
        key = default;
        var uuid = item?.Archive.Uuid;
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        key = WordKey.Archive(uuid);
        return true;
    }

    /// <summary>
    /// 复习页的词身份：先认教材/词形临时卡（负 id，登记表里记着 source 身份），再回落到档案 uuid。
    /// 两者**不合并**——同一个词形在 IELTS 与档案里是两条互不相干的身份（规格书 §3）。
    /// </summary>
    private bool MemoryTryReviewKey(WordItem? item, out WordKey key) =>
        MemoryTrySourceReviewKey(item, out key) || MemoryTryArchiveKey(item, out key);

    /// <summary>
    /// 该词是否是档案词（正 id）。source 临时卡是负 id、**没有档案行**：
    /// 旧列（兼容投影）、ExecuteBatch / MarkUnsure / MarkForgot / UndoLastLearningAction 只对档案词生效。
    /// </summary>
    private static bool MemoryIsArchiveWord(WordItem? item) => item is { Id: > 0 };

    /// <summary>复习页按界面身份（词 id）取回词条：档案词查 _allWords，source 临时卡查登记表。</summary>
    private WordItem? MemoryReviewWordByIdentity(long? id)
    {
        if (id is not { } value) return _reviewWord;
        return _allWords.FirstOrDefault(w => w.Id == value)
            ?? MemoryResolveReviewIdentity(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>复习页的界面身份 = 词 id（只在本次运行内使用，仅用于取回呈现记录）。</summary>
    private static string MemoryReviewIdentity(WordItem? item) =>
        item is null ? "" : item.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>查词页 / IELTS 卡片的界面身份 = 归一化词形（与词身份解析同一口径）。</summary>
    private static string MemoryFocusIdentity(string? form) =>
        string.IsNullOrWhiteSpace(form) ? "" : WordKeyResolver.FormC(form);

    /// <summary>
    /// 查词页 / IELTS 单词卡：Resolve(归一化词形, pageKind, FindArchive, _ieltsCatalog.Find)。
    /// 查表一律用 <see cref="WordKeyResolver.FormC"/> 归一化后的词形（评审 P1-5）：
    /// 否则「两个空格」之类的原始词形会让档案/目录双双 miss，退回 <c>form:</c> 而把同一个词劈成两份身份。
    /// </summary>
    private bool MemoryTryFocusKey(string? form, out WordKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(form)) return false;
        try
        {
            key = WordKeyResolver.Resolve(WordKeyResolver.FormC(form),
                _wordFocusSnapshot?.Page == WordKeyResolver.IeltsPageKind ? WordKeyResolver.IeltsPageKind : null,
                word => FindArchive(WordKeyResolver.FormC(word)),
                word => _ieltsCatalog?.Find(WordKeyResolver.FormC(word)));
            return true;
        }
        catch (Exception ex)
        {
            MarkMemoryDisabled("Resolve：" + ex.Message);
            return false;
        }
    }

    /// <summary>计划单词卡：ResolvePlanWord（Archive 源用 words.id 反查 uuid，Ielts 源直接用目录 Id）。</summary>
    private bool MemoryTryPlanKey(DailyStudyPlan? plan, string? wordId, out WordKey key)
    {
        key = default;
        if (plan is null || string.IsNullOrWhiteSpace(wordId)) return false;
        var word = plan.Words.FirstOrDefault(w => string.Equals(w.Id, wordId, StringComparison.Ordinal));
        if (word is null) return false;
        try
        {
            key = WordKeyResolver.ResolvePlanWord(plan, word, id => _allWords
                .FirstOrDefault(w => string.Equals(w.Id.ToString(CultureInfo.InvariantCulture), id, StringComparison.Ordinal)));
            return true;
        }
        catch (Exception ex)
        {
            // 计划陈旧 / 档案行已删：不猜身份，跳过该词（不让长期层影响计划流程）。
            MarkMemoryDisabled("ResolvePlanWord：" + ex.Message);
            return false;
        }
    }

    // ==================================================================================
    // 事件（记录型：失败只降级）
    // ==================================================================================

    private void MemoryPresented(WordKey key, string? presentationId, bool isRecall)
    {
        if (presentationId is null) return;
        MemoryRun("OnPresented", m => m.OnPresented(key, presentationId, isRecall));
    }

    private void MemoryRated(MemorySurface surface, string? identity, StudyRating rating,
        int recognitionBefore, int recognitionAfter, StudyMode? awaitingFinalize = null)
    {
        // 只信「这个表面为这张卡登记过的那一条」：换卡不会把它改写成别的词（评审 P0-2）。
        if (!MemoryTryPresentedCard(surface, identity, out var card) || !MemoryPresentationMatches(card.PresentationId, card.Key))
            throw new InvalidOperationException("No matching persisted presentation for response.");
        // 计划卡与查词页共用同一套 _focusPresented* 状态（都走 _focusRound），
        // 所以"这张卡已经作答"必须对两个表面一起置位——否则计划卡重绘时会被去重守卫
        // 当成"已呈现未作答"而复用旧呈现，多次认识会塌成一次呈现。
        if (surface is MemorySurface.Focus or MemorySurface.Plan) _focusPresentedAnswered = true;
        // 这一次作答会让该词本轮完成（接下来还会单独走一次 CommitWord）——必须在构造本调用的
        // checkpoint **之前**登记，否则 Rated 事件与 checkpoint 的事务里不会带上"待定稿"。
        if (awaitingFinalize is { } finalizeMode) MemoryAwaitFinalize(card.Key, finalizeMode);
        // latencyMs 传 null：协调器自己按 Pause/Resume 区间计时，本层不提供第二套时钟（§6）。
        MemoryRequiredRun("OnRated", m => m.OnRated(card.Key, card.PresentationId, rating, recognitionBefore, recognitionAfter, null, checkpoint: MemoryBuildCheckpoint()));
    }

    private void MemoryRevised(MemorySurface surface, string? identity, StudyRating from, StudyRating to)
    {
        if (!MemoryTryPresentedCard(surface, identity, out var card) || !MemoryPresentationMatches(card.PresentationId, card.Key))
            throw new InvalidOperationException("No matching persisted presentation for response.");
        if (surface is MemorySurface.Focus or MemorySurface.Plan) _focusPresentedAnswered = true;
        // 改判必须是**一次**事务：修正事件 + 已有 canonical 的失效 + pre-state 回放 + 轮内 checkpoint
        // 同事务提交。分步 OnRevised→UndoCommit 会在第二步失败时留下「事件已记、canonical 还在」的
        // 不一致状态，所以这里走协调器的 canonicalId 原子签名。
        var committed = _committedWordKeys.Contains(card.Key.Key);
        // 这个事务会把该词的 canonical 置为失效，因此 checkpoint 里的 Committed **不能**再包含它：
        // 否则重启后恢复出来的集合会声称"已定稿"，再次撤销/改判时用同一个确定性 canonicalId
        // 去失效一条已经失效的行 → InvalidateCanonicalCore 走 alreadyInvalidated 分支返回 false →
        // 协调器抛"长期复习记录已改变"，而失败路径不会清掉这条陈旧记录，于是每次重试都失败。
        if (committed) _committedWordKeys.Remove(card.Key.Key);
        try
        {
            MemoryRequiredRun("OnRevised", m => m.OnRevised(card.Key, card.PresentationId, from, to, null,
                MemoryBuildCheckpoint(), committed ? CanonicalReview.BuildId(card.Key.Key, m.CurrentSessionId) : null));
        }
        catch
        {
            if (committed) _committedWordKeys.Add(card.Key.Key);
            throw;
        }
    }

    /// <summary>
    /// 撤销：追加 Undone 事件 + 失效已定稿的 canonical。
    /// 两者**必须在同一处、同一 try/catch 内**（评审 P0-2 要求 3）：要么都做、要么都只记诊断，
    /// 绝不出现「轨迹没回滚但 canonical 被失效」的不一致。
    /// 用的是**被撤销那个词**记下来的那一对 <c>(key, pid)</c>，与当前显示的是哪张卡无关。
    /// </summary>
    private void MemoryUndone(MemorySurface surface, string? identity)
    {
        if (!MemoryTryPresentedCard(surface, identity, out var card))
        {
            // 该词在本会话从未被呈现过：只回滚旧列与轮内状态，不碰长期层。这不是异常路径。
            MemoryDiagnostic("撤销 " + (identity ?? "(未知词)") + "：本会话没有它的呈现记录，长期层不参与本次撤销。");
            return;
        }

        var memory = Memory ?? throw new IOException("Learning store is unavailable.");
        var key = card.Key;
        var committed = _committedWordKeys.Contains(key.Key);
        // checkpoint 与 Undone 事件、canonical 失效同事务落盘：撤销后的轮内状态必须和持久轨迹
        // 在同一次提交里，否则崩溃后恢复出来的轮会和已经失效的 canonical 对不上。
        // 同时：这个事务已经让该 canonical 失效，Committed 集合必须先摘掉它（理由同 MemoryRevised）。
        if (committed) _committedWordKeys.Remove(key.Key);
        try
        {
            memory.OnUndone(key, card.PresentationId, MemoryBuildCheckpoint(), canonicalId: committed
                ? CanonicalReview.BuildId(key.Key, memory.CurrentSessionId) : null);
        }
        catch
        {
            if (committed) _committedWordKeys.Add(key.Key);
            throw;
        }
    }

    // ==================================================================================
    // 定稿（commit barrier）
    // ==================================================================================

    /// <summary>
    /// 单词本轮定稿。<paramref name="mode"/> **只信调用方**（§9.2 不按呈现次数二次推断）。
    /// 返回 null（没有有效 retrieval）时什么都不做；异常只降级——旧列已经写完，
    /// 既有界面照常显示，不出现「半个完成」。
    /// </summary>
    private void MemoryCommitCard(MemorySurface surface, string? identity, StudyMode mode, IReadOnlyList<PendingMutation>? mutations = null)
    {
        if (!MemoryTryPresentedCard(surface, identity, out var card))
            throw new InvalidOperationException("No presentation to finalize.");
        MemoryRequiredRun("CommitWord", memory =>
        {
            _committedWordKeys.Add(card.Key.Key);
            // 定稿即将在本事务里完成，checkpoint 不能再声称该词待定稿；失败则把登记原样放回。
            _memoryAwaitingFinalize.TryGetValue(card.Key.Key, out var awaiting);
            _memoryAwaitingFinalize.Remove(card.Key.Key);
            WordSessionCommitResult? result;
            try { result = memory.CommitWord(card.Key, mode, outboxMutations: mutations, checkpoint: MemoryBuildCheckpoint()); }
            catch
            {
                _committedWordKeys.Remove(card.Key.Key);
                if (awaiting is { } pending) _memoryAwaitingFinalize[card.Key.Key] = pending;
                throw;
            }
            var applied = result
                ?? throw new InvalidOperationException("No validated retrieval to finalize.");
            _committedWordKeys.Add(card.Key.Key);
        });
    }

    /// <summary>§9.2：查词页 / IELTS 单词卡的长期模式必须**逐词**判定，且用 Reset 那一刻捕获的集合。</summary>
    private StudyMode MemoryFocusCommitMode(string form) =>
        _focusNewWords.Contains(WordKeyResolver.FormC(form)) ? StudyMode.FirstLearn : StudyMode.Review;

    /// <summary>计划单词卡的定稿入口（由 <see cref="DailyStudyPlanSession.OnWordCompleted"/> 回调）。</summary>
    private void MemoryCommitPlanCard(string wordId) =>
        // 计划轮全部按「首次学习」记录（§9.2：Round.Reset 的 2 参重载把所有卡都当新词）。
        MemoryCommitCard(MemorySurface.Plan, wordId, StudyMode.FirstLearn, [MemoryPlanSnapshot()]);

    // ==================================================================================
    // 呈现点：装填新卡时生成 presentationId（重绘 / 改判 / 语言切换一律复用）
    // ==================================================================================

    /// <summary>复习页装填新卡（OpenReviewDeck / AdvanceReviewCoreAsync / 撤销后重装）。</summary>
    private void MemoryPresentReviewCard()
    {
        if (!_reviewRound.HasCurrent) return;
        var word = _reviewRound.Current;
        if (!MemoryTryReviewKey(word, out var key)) return;
        if (MemoryTryPresentedCard(MemorySurface.Review, MemoryReviewIdentity(word), out var existing)
            && _memoryStore!.LoadEvents(_memory!.CurrentSessionId).Any(e => e.PresentationId == existing.PresentationId)
            && TrajectoryReducer.ProjectPresentations(_memoryStore.LoadEvents(_memory.CurrentSessionId))
                .Any(p => p.PresentationId == existing.PresentationId && p.FinalValidatedResponse is null)) return;
        var presentationId = Guid.NewGuid().ToString("N");
        RegisterPresentation(MemorySurface.Review, MemoryReviewIdentity(word), key, presentationId);
        // 复习页全部是回忆卡：StudyRound.Reset(words, StudyMode.Review) 下所有卡 IsNew == false。
        MemoryPresented(key, presentationId, isRecall: true);
        MemorySaveRound();
    }

    /// <summary>查词页 / IELTS 单词卡装填新卡（StartFocusRound / LoadFocusWordAsync）。</summary>
    private void MemoryPresentFocusCard()
    {
        if (_memoryDeferFocusInitialization) return;
        if (_planCardActive) return; // 计划卡走 MemoryPresentPlanCard
        if (!_focusRound.HasCurrent) return;
        var newEntry = MemoryEnsureFocusSession();
        var form = _focusRound.Current;
        var step = _focusRound.CurrentStep;
        if (!MemoryTryFocusKey(form, out var key)) return;

        var identity = MemoryFocusIdentity(form);
        // IELTS 单词卡入口会把同一张卡连续装填两次（先查词页一轮、再按词表重开一轮）：
        // 同一张卡、同一步骤、且尚未作答 → 沿用已登记的呈现，不重复记录。
        if (!_focusPresentedAnswered && _focusPresentedStep == step
            && string.Equals(_focusPresentedIdentity, identity, StringComparison.Ordinal))
            return;

        var presentationId = Guid.NewGuid().ToString("N");
        _focusPresentedIdentity = identity;
        _focusPresentedStep = step;
        _focusPresentedAnswered = false;
        RegisterPresentation(MemorySurface.Focus, identity, key, presentationId);
        // Learn 卡只看答案，不构成 retrieval（§4）。
        MemoryPresented(key, presentationId, isRecall: step == StudyStep.Recall);
        MemorySaveRound();
    }

    /// <summary>计划单词卡装填新卡（RenderPlanFocusWord）。</summary>
    private void MemoryPresentPlanCard(DailyStudyPlan? plan, string? wordId)
    {
        if (!MemoryTryPlanKey(plan, wordId, out var key)) return;
        var step = _focusRound.HasCurrent ? _focusRound.CurrentStep : StudyStep.Recall;
        // 与复习页/查词页同一口径：同一张卡、同一步骤、且尚未作答时沿用已登记的呈现。
        // 没有这道守卫的话，每次重绘（含恢复后重绘）都会 mint 一个新的 presentationId，
        // 把恢复出来的那条呈现顶掉，并多记一条 Presented。
        if (_memoryStore is not null && _memory is not null
            && MemoryTryPresentedCard(MemorySurface.Plan, wordId, out var existing)
            && _focusPresentedStep == step && !_focusPresentedAnswered
            && _memoryStore.LoadEvents(_memory.CurrentSessionId).Any(e => e.PresentationId == existing.PresentationId))
            return;
        var presentationId = Guid.NewGuid().ToString("N");
        _focusPresentedIdentity = wordId;
        _focusPresentedStep = step;
        _focusPresentedAnswered = false;
        RegisterPresentation(MemorySurface.Plan, wordId!, key, presentationId);
        MemoryPresented(key, presentationId, isRecall: step == StudyStep.Recall);
        MemorySaveRound();
    }

    // ==================================================================================
    // 到期队列（规格书 §9.1 的并集口径）
    // ==================================================================================

    /// <summary>
    /// 今日待复习的档案词：
    /// <c>status == 'learning'</c> 且
    /// （有 fsrs_card 且 next_review_at_utc &lt;= nowUtc 或 无 fsrs_card 且旧 next_review_date &lt;= 本地今日）。
    /// </summary>
    /// <remarks>
    /// 零迁移过渡资格：没有 FSRS 卡的词沿用旧口径（行为逐字节不变）；一旦某词在真实 retrieval 后
    /// 建立了 FSRS 卡，legacy 分支对它**永久失效**（旧列不再决定 due）。
    /// QueryDue 失败 → 回退纯旧口径并降级。排序与 _reviewHandled 过滤由调用方（GetPendingReviewWords）负责。
    /// </remarks>
    private List<WordItem> MemoryPendingReviewWords()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var memory = Memory;
        if (memory is null)
        {
            // 恢复待重试**不是**故障降级：这一层稍后会自动恢复，不能把它永久停用。
            if (_memoryRecoveryPending)
            {
                MemoryDiagnostic("到期队列暂不可用（等待个人参数恢复重试），本轮不装填复习卡。");
                return [];
            }
            MarkMemoryDisabled("到期队列不可用，已暂停复习装填；未回退旧排期。");
            return [];
        }

        IReadOnlySet<string> cardKeys;
        HashSet<string> dueKeys;
        try
        {
            var store = _memoryStore as VocabularyService
                ?? throw new InvalidOperationException("到期队列存储未绑定。");
            // 批量查身份，包含没有 due 的卡；不能把这些卡重新当作 legacy 词。
            cardKeys = store.GetMemoryCardKeys();
            dueKeys = new HashSet<string>(
                memory.QueryDue(DateTime.UtcNow, int.MaxValue).Select(card => card.WordKey),
                StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            MarkMemoryDisabled("到期队列查询失败：" + ex.Message + "（已暂停复习装填，未回退旧排期）");
            return [];
        }

        var result = new List<WordItem>();
        foreach (var word in _allWords)
        {
            if (!string.Equals(word.Status, "learning", StringComparison.Ordinal)) continue;
            var schedulable = MemoryTryArchiveKey(word, out var key);
            // ① 已有 FSRS 卡且到期：由 FSRS 唯一排期。
            if (schedulable && dueKeys.Contains(key.Key)) { result.Add(word); continue; }
            // ② 旧口径（过渡资格）：仅当还没有 FSRS 卡时生效。
            if (word.NextReviewDate is null || string.CompareOrdinal(word.NextReviewDate, today) > 0) continue;
            if (schedulable && cardKeys.Contains(key.Key)) continue;
            result.Add(word);
        }
        return result;
    }

    /// <summary>档案页「复习模式」筛选用的同一到期集合（规格书 §9.1：必须与复习页成对改）。</summary>
    private HashSet<long> MemoryDueWordIds() =>
        MemoryPendingReviewWords().Select(word => word.Id).ToHashSet();

    /// <summary>该词身份是否已经有 FSRS 卡（查询失败按「没有卡」处理 → 走旧口径，绝不隐藏卡片）。</summary>
    private bool MemoryHasCard(string wordKey)
    {
        var store = _memoryStore;
        if (store is null) return false;
        try { return store.HasCard(wordKey); }
        catch (Exception ex) { MarkMemoryDisabled("HasCard：" + ex.Message); return false; }
    }
}
