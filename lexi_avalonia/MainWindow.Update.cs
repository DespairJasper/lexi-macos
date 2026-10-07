using System.Diagnostics;
using Avalonia.Threading;

namespace Lexi;

/// <summary>
/// 启动时的后台更新检查与提示。
/// 设计约束（不可放宽）：
/// 1. 绝不阻塞启动、主线程或任何核心功能；检查发生在窗口 Opened 之后，跑在线程池上。
/// 2. 每个进程最多一次请求，不重试、不轮询、不无限重试。
/// 3. 断网、限流、超时、代理、接口错误、异常响应一律静默降级，软件照常可用。
/// 4. 只提示并打开 Release 页；没有自动下载、自动安装或强制升级。
/// 5. 不上传任何用户数据，不内置任何 token。
/// 6. 自动化测试运行与隔离数据目录（LEXI_DATA_DIR）下完全不联网。
/// 7. 只把结果留在内存与设置页诊断行，不新增任何持久化文件。
/// </summary>
public partial class MainWindow
{
    private UpdateCheckResult? _updateCheckResult;
    private bool _updateCheckStarted;
    private string? _updateReleaseUrl;

    internal void ConfigureUpdateCheck()
    {
        UpdateNoticeOpenBtn.Click += (_, _) => OpenUpdateReleasePage();
        UpdateNoticeDismissBtn.Click += (_, _) => DismissUpdateNotice();
        // 窗口真正显示之后再发起，确保任何网络等待都不在启动路径上。
        Opened += (_, _) => StartUpdateCheck();
    }

    /// <summary>发起本次进程的更新检查。重复调用无副作用。绝不抛异常。</summary>
    internal void StartUpdateCheck()
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;
        try
        {
            if (App.IsAutomatedTestRun()) return;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEXI_DATA_DIR"))) return;
            var current = AppVersion.Current;
            _ = Task.Run(async () =>
            {
                var result = await new UpdateChecker().CheckAsync(current).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => ApplyUpdateCheckResult(result));
            });
        }
        catch (Exception ex) { UpdateDiagnostic("scheduler-failed:" + ex.GetType().Name); }
    }

    /// <summary>提示里携带的 Release 地址。只读，供 UI 测试核对它确实是受信任地址。</summary>
    internal string? UpdateReleaseUrl => _updateReleaseUrl;

    internal void ApplyUpdateCheckResult(UpdateCheckResult result)
    {
        try
        {
            _updateCheckResult = result;
            if (result is { Outcome: UpdateCheckOutcome.UpdateAvailable, LatestTag: { } tag, ReleaseUrl: { } url })
            {
                _updateReleaseUrl = url;
                UpdateNoticeText.Text = TF($"有新版本 {tag} · 当前 {AppVersion.Display}");
                UpdateNoticeBar.IsVisible = true;
            }
            UpdateCheckText.Text = UpdateCheckDiagnosticText();
        }
        catch (Exception ex) { UpdateDiagnostic("apply-failed:" + ex.GetType().Name); }
    }

    private void DismissUpdateNotice()
    {
        // 只隐藏本次会话的提示；不写任何持久状态，下次启动照常检查。
        UpdateNoticeBar.IsVisible = false;
    }

    private void OpenUpdateReleasePage()
    {
        // 地址在解析阶段已限定为 https://github.com/<owner>/<repo>/releases/…；此处再校验一次。
        if (_updateReleaseUrl is not { } url || !UpdateCheck.IsSafeReleaseUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(T("未能打开更新页面：") + ex.Message); }
    }

    /// <summary>设置页诊断行。原因码是短 ASCII，用于定位更新检查问题，不含任何用户内容。</summary>
    private string UpdateCheckDiagnosticText() => _updateCheckResult switch
    {
        null => "",
        { Outcome: UpdateCheckOutcome.UpdateAvailable, LatestTag: { } tag } => TF($"更新检查：发现新版本 {tag}"),
        { Outcome: UpdateCheckOutcome.UpToDate } => T("更新检查：已是最新版本"),
        { Reason: "timeout" or "network" } => T("更新检查：网络不可用，未完成"),
        { Reason: "rate-limited" } => T("更新检查：请求过于频繁，未完成"),
        { Reason: "cancelled" } => T("更新检查：未完成"),
        var other => TF($"更新检查：未完成（{other.Reason}）"),
    };

    private static void UpdateDiagnostic(string message)
    {
        try { Console.Error.WriteLine("[update] " + message); } catch { }
    }
}
