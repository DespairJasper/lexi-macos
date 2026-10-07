using System;
using System.IO;

namespace Lexi;

/// <summary>
/// P8：**个人 FSRS 参数**的界面侧接线。
///
/// <para><b>分工</b>：门槛/训练/重放/事务发布/一致性核对在 <see cref="FsrsPersonalization"/>；
/// 训练与发布的在途标志、取消、rebind 代际、待发布候选在
/// <see cref="FsrsPersonalizationRuntime"/>（不依赖窗口，因此可以在真实
/// <see cref="System.Threading.SynchronizationContext"/> 下被测试）；本文件只剩三件窗口才有资格做的事：
/// ① 装配与启动核对；② 把窗口状态折算成"现在能不能发布"；③ 把结果写成 stderr 诊断。</para>
///
/// <para><b>为什么不在窗口里同步等待发布</b>：<c>PublishAsync</c> 的续体必须回到调用上下文才能
/// 用那条无锁 SqliteConnection 提交；在 UI 线程上 <c>GetAwaiter().GetResult()</c> 会把续体永远排在
/// 自己后面 → 死锁。这里全程 async，窗口只负责触发。</para>
///
/// <para><b>降级纪律</b>：训练/发布链路的任何失败都只写一条 <c>[memory]</c> 诊断，
/// 绝不置 <c>_memoryDisabled</c>、绝不抛给调用方、绝不改变任何学习流程的可见状态。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>个人参数的运行期生命周期（训练/发布在途、代际、待发布候选）。</summary>
    private FsrsPersonalizationRuntime? _parameterRuntime;

    /// <summary>个人参数的集成服务；构造失败时为 null，训练整体停用但长期记忆层照常工作。</summary>
    private FsrsPersonalization? _memoryPersonalization;

    /// <summary>"helper 缺失"这条诊断只报一次，避免每个轮末都刷一行。</summary>
    private bool _parameterHelperMissingReported;

    /// <summary>解析出来的 helper 路径（null = 未找到）。</summary>
    private string? _parameterHelperPath;

    // ==================================================================================
    // 装配（由 MainWindow.Memory 的 Memory getter 调用）
    // ==================================================================================

    /// <summary>
    /// 装配个人参数：解析 helper、构造优化器、交给 <see cref="FsrsPersonalizationStartup.Create"/>
    /// 完成"恢复 + 一致性核对"。判定逻辑本身在服务层（可测），这里只负责定位 helper 与写诊断。
    /// </summary>
    private FsrsPersonalizationStartup MemoryBuildPersonalization(ILearningMemoryStore store, SchedulerWeights holder)
    {
        _parameterHelperPath = MemoryResolveOptimizerHelperPath();
        if (_parameterHelperPath is null && !_parameterHelperMissingReported)
        {
            _parameterHelperMissingReported = true;
            MemoryParameterDiagnostic(
                "未找到 FSRS optimizer helper 可执行文件（已查 LEXI_FSRS_OPTIMIZER_BIN 与应用程序目录），"
                + "个人参数训练将不可用；官方默认参数照常工作。");
        }

        return FsrsPersonalizationStartup.Create(
            store, holder,
            _ => new FsrsParameterOptimizer(new FsrsOptimizerOptions
            {
                HelperPath = _parameterHelperPath,
                Timeout = TimeSpan.FromSeconds(SchedulingConfig.OptimizerTrainingTimeoutSeconds),
            }),
            diagnostic: MemoryParameterDiagnostic);
    }

    /// <summary>把服务包成运行期生命周期；窗口状态只以委托形式注入（保持可测）。</summary>
    private FsrsPersonalizationRuntime MemoryBuildParameterRuntime(FsrsPersonalization? service) =>
        new(service,
            deferPublish: MemoryParametersPublishDeferred,
            diagnostic: MemoryParameterDiagnostic,
            clock: () => DateTime.UtcNow,
            beforePublish: MemoryCancelContextTrainingInFlight);

    /// <summary>
    /// helper 的定位顺序：显式环境变量 → 应用程序目录下的固定文件名。
    /// <para>生产路径必须是 <see cref="AppContext.BaseDirectory"/>（helper 由构建流程复制进
    /// <c>.app/Contents/MacOS</c> 与 <c>Lexi.dll</c> 同级）；只认环境变量会让发布包永远找不到它，
    /// 而失败又只表现为一行 stderr。</para>
    /// </summary>
    private static string? MemoryResolveOptimizerHelperPath()
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable("LEXI_FSRS_OPTIMIZER_BIN");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var candidate = Path.Combine(AppContext.BaseDirectory, "fsrs-optimizer");
            if (File.Exists(candidate)) return candidate;
        }
        catch (Exception)
        {
            // 路径探测本身失败 → 当作"没找到"，由上面的诊断说明。
        }
        return null;
    }

    // ==================================================================================
    // 触发（唯一入口：由 MemoryTryTrainContext 在同一组轮级时机上转发）
    // ==================================================================================

    /// <summary>
    /// 轮级触发：问一次"现在该不该训 / 该不该发布个人参数"。
    /// <para><b>线程纪律</b>：样本/记账/签名的读取发生在**调用线程**（UI 线程）上；
    /// 纯优化与重放整体进 <c>Task.Run</c>；发布全程 await，续体回到 UI 线程后再提交事务。</para>
    /// </summary>
    private void MemoryTryTrainParameters(string trigger) => _parameterRuntime?.NotifyRoundBoundary(trigger);

    /// <summary>"现在不适合发布"的判定（纯判定，不碰 store、不读时钟）。</summary>
    private bool MemoryParametersPublishDeferred()
    {
        if (MemoryDisabled) return true;
        if (_contextTrainingRunning) return true; // UI 忙，且它的样本基线马上要变
        if (MemoryHasUndoablePresentation()) return true;
        return false;
    }

    /// <summary>
    /// 是否还有用户**此刻仍可能撤销**的呈现。有的话就不换版：换版会重写全库 pre-state，
    /// 而用户手上那个撤销栈指向的是刚刚这次会话里定稿的卡片——等他离开/进入下一轮再换，语义最清楚。
    /// </summary>
    private bool MemoryHasUndoablePresentation()
    {
        if (_memoryAwaitingFinalize.Count > 0) return true; // 有已承诺待定稿的呈现
        if (_committedWordKeys.Count == 0) return false;
        if (_reviewRound.HasCurrent && !_reviewRound.IsFinished) return true; // 复习轮还没走完
        return _focusMemorySessionLive; // 卡片表面仍活着
    }

    /// <summary>词库被替换 / 重绑 / 真退出时作废在途训练与发布（推进代际，旧续体自行放弃）。</summary>
    private void MemoryCancelParameters()
    {
        _memoryPersonalization = null;
        var runtime = _parameterRuntime;
        _parameterRuntime = null;
        runtime?.Invalidate();
    }

    /// <summary>个人参数链路的诊断通道（与 Context 同风格：stderr + <c>[memory]</c> 前缀）。</summary>
    private static void MemoryParameterDiagnostic(string message) => MemoryDiagnostic("个人参数训练｜" + message);
}
