using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Lexi;

/// <summary>
/// 更新提示的界面行为：无新版/失败不显示；有新版显示当前与新版版本并给出更新入口；
/// 关闭后提示消失且核心功能照常可用。全程不联网、不打开浏览器。
/// </summary>
public static class UpdateNoticeUiTests
{
    private const string NewerTag = "v9.9.9";
    private const string NewerUrl = "https://github.com/DespairJasper/lexi-macos/releases/tag/v9.9.9";

    public static async Task RunAsync(MainWindow window, Action<bool, string> check)
    {
        var bar = window.FindControl<Border>("UpdateNoticeBar") ?? throw new Exception("Missing UpdateNoticeBar");
        var text = window.FindControl<TextBlock>("UpdateNoticeText") ?? throw new Exception("Missing UpdateNoticeText");
        var open = window.FindControl<Button>("UpdateNoticeOpenBtn") ?? throw new Exception("Missing UpdateNoticeOpenBtn");
        var dismiss = window.FindControl<Button>("UpdateNoticeDismissBtn") ?? throw new Exception("Missing UpdateNoticeDismissBtn");

        check(!bar.IsVisible, "update notice stays hidden until a newer release is found");
        window.ApplyUpdateCheckResult(UpdateCheckResult.UpToDate("up-to-date"));
        check(!bar.IsVisible, "an up-to-date result shows no notice");
        window.ApplyUpdateCheckResult(UpdateCheckResult.UpToDate("unparsable-tag"));
        check(!bar.IsVisible, "an unparsable tag shows no notice");
        window.ApplyUpdateCheckResult(UpdateCheckResult.Failed("network"));
        check(!bar.IsVisible, "a failed check shows no notice");
        window.ApplyUpdateCheckResult(UpdateCheckResult.Failed("rate-limited"));
        check(!bar.IsVisible, "a rate-limited check shows no notice");

        window.ApplyUpdateCheckResult(UpdateCheckResult.Available(NewerTag, NewerUrl));
        Dispatcher.UIThread.RunJobs();
        check(bar.IsVisible, "a newer release shows the notice");
        check(text.Text?.Contains(NewerTag, StringComparison.Ordinal) == true, "the notice shows the new version");
        check(text.Text?.Contains(AppVersion.Display, StringComparison.Ordinal) == true, "the notice shows the current version");
        check(UpdateCheck.IsSafeReleaseUrl(window.UpdateReleaseUrl ?? ""), "the notice carries a trusted release url");
        check(open.IsVisible && dismiss.IsVisible, "the notice offers both an update entry and a dismiss control");
        var openTip = ToolTip.GetTip(open) as string ?? "";
        var dismissTip = ToolTip.GetTip(dismiss) as string ?? "";
        // 断言的是"这个控件有可读标签"，不锚定具体措辞，避免文案微调就弄红门禁。
        check(openTip.Contains("更新", StringComparison.Ordinal) || openTip.Contains("release", StringComparison.OrdinalIgnoreCase),
            "the update entry is labelled, got: " + openTip);
        check(dismissTip.Contains("提示", StringComparison.Ordinal) || dismissTip.Contains("dismiss", StringComparison.OrdinalIgnoreCase),
            "the dismiss control is labelled, got: " + dismissTip);

        // 关闭提示后不得干扰使用：真实走一次离线查词。
        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        check(!bar.IsVisible, "dismissing hides the notice");
        var input = window.FindControl<TextBox>("LookupInput") ?? throw new Exception("Missing LookupInput");
        input.Text = "serendipity";
        await (Task)typeof(MainWindow).GetMethod("PerformLookupAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        check(window.FindControl<TextBlock>("ResultWordText")?.Text == "serendipity",
            "core lookup still works after dismissing the update notice");
        check(!bar.IsVisible, "the dismissed notice does not come back on its own");
    }
}
