namespace Lexi;

/// <summary>
/// <c>--update-check</c> 的独立入口：真实发起一次 GitHub Release 检查，把结果打到 stdout 后退出。
/// 只用于人工与脚本验证真实网络路径（正式启动路径上的检查不阻塞启动且失败静默，看不到输出）。
/// 它不启动 UI、不打开用户数据目录、不写任何文件、不读任何用户数据。
/// 退出码：0 = 已确定（最新或发现新版本），1 = 未能确定（网络/限流/超时/响应异常）。
/// </summary>
internal static class UpdateCheckCommand
{
    internal static async Task<int> RunAsync()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        var result = await new UpdateChecker().CheckAsync(AppVersion.Current).ConfigureAwait(false);
        Console.WriteLine("current=" + AppVersion.Display);
        Console.WriteLine("outcome=" + result.Outcome);
        Console.WriteLine("reason=" + result.Reason);
        if (result.LatestTag != null) Console.WriteLine("latest=" + result.LatestTag);
        if (result.ReleaseUrl != null) Console.WriteLine("url=" + result.ReleaseUrl);
        return result.Outcome == UpdateCheckOutcome.Failed ? 1 : 0;
    }
}
