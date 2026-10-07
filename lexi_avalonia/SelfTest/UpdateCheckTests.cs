using System.Net;
using System.Net.Http;
using System.Text;

namespace Lexi;

/// <summary>
/// 启动更新检查的离线测试：版本解析/比较矩阵、真实 GitHub 响应形状、
/// 以及用假 HttpMessageHandler 驱动的全部失败路径（断网、超时、限流、异常响应）。
/// 全程不联网、不读用户数据、不写文件。
/// </summary>
public static class UpdateCheckTests
{
    // 2026-10-07 从 https://api.github.com/repos/DespairJasper/lexi-macos/releases/latest
    // 实际取得的响应字段形状（已裁剪无关字段）。
    private const string LiveLatestReleaseJson = """
        {"url":"https://api.github.com/repos/DespairJasper/lexi-macos/releases/1","assets_url":"https://api.github.com/repos/DespairJasper/lexi-macos/releases/1/assets","upload_url":"u","html_url":"https://github.com/DespairJasper/lexi-macos/releases/tag/v3.1.1","id":1,"node_id":"n","tag_name":"v3.1.1","target_commitish":"main","name":"Lexi 3.1.1 · macOS","draft":false,"prerelease":false,"created_at":"2026-10-07T06:50:00Z","published_at":"2026-10-07T06:55:43Z","assets":[{"name":"Lexi-3.1.1-macOS-arm64.dmg"}]}
        """;

    public static async Task RunAsync()
    {
        VersionMatrix();
        EvaluateMatrix();
        LocalizationCoverage();
        await TransportMatrixAsync();
        Console.WriteLine("PASS: update check (version matrix, release evaluation, offline/timeout/rate-limit/oversize handling)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Update check failed: " + message);
    }

    private static void VersionMatrix()
    {
        var current = new Version(3, 1, 2, 0);
        // v 前缀、两段、四段、预发布与构建元数据后缀都必须正确归一化。
        Check(AppVersion.Parse("3.1.2") == current, "plain three-part tag did not normalize");
        Check(AppVersion.Parse("v3.1.2") == current, "lowercase v prefix was not stripped");
        Check(AppVersion.Parse("V3.1.2") == current, "uppercase V prefix was not stripped");
        Check(AppVersion.Parse("3.1") == new Version(3, 1, 0, 0), "two-part tag did not pad the missing segments");
        Check(AppVersion.Parse("4") == new Version(4, 0, 0, 0), "single-part tag did not pad the missing segments");
        Check(AppVersion.Parse("3.1.2.0") == current, "four-part tag did not normalize");
        Check(AppVersion.Parse("3.1.2-beta.1") == current, "prerelease suffix was not dropped");
        Check(AppVersion.Parse("3.1.2+build.7") == current, "build metadata was not dropped");
        Check(AppVersion.Parse("  v3.1.2  ") == current, "surrounding whitespace was not trimmed");
        // 无法确定必须返回 null，绝不能当成版本号。
        foreach (var bad in new string?[] { null, "", "   ", "v", "V", "latest", "3.x.1", "3..1", ".1.2", "3.1.2.3.4", "-1.2.3", "release-3", "vv3.1.2" })
            Check(AppVersion.Parse(bad) == null, "unparsable tag accepted: " + (bad ?? "<null>"));
        // 空后缀不算版本信息：尾部 "-" / "+" 只表示"没有预发布标签"。
        Check(AppVersion.Parse("3.1.2-") == current, "empty prerelease suffix must not invalidate the tag");
        Check(AppVersion.Parse("v3.1.2+") == current, "empty build suffix must not invalidate the tag");
        // 数值比较，不是字符串比较。
        Check(AppVersion.IsNewer("3.1.10", current), "3.1.10 must be newer than 3.1.2 (string compare would fail)");
        Check(AppVersion.IsNewer("v4.0.0", current), "major bump not detected");
        Check(AppVersion.IsNewer("3.2", current), "minor bump not detected");
        Check(AppVersion.IsNewer("3.1.2.1", current), "revision bump not detected");
        Check(!AppVersion.IsNewer("3.1.2", current), "equal version reported as newer");
        Check(!AppVersion.IsNewer("v3.1.1", current), "older version reported as newer");
        Check(!AppVersion.IsNewer("3.1.1.9", current), "older revision reported as newer");
        Check(!AppVersion.IsNewer("3.1.2-beta.9", current), "prerelease of the same version reported as newer");
        Check(!AppVersion.IsNewer("not-a-version", current), "unparsable tag reported as newer");
        Check(!AppVersion.IsNewer(null, current), "null tag reported as newer");
        // 未指定段不能被当成 -1（Version 的默认行为），否则 3.1 会被误判落后于 3.1.0。
        Check(AppVersion.Normalize(new Version(3, 1)) == new Version(3, 1, 0, 0), "unspecified segments must normalize to zero");
        Check(AppVersion.Current >= new Version(3, 1, 0, 0), "assembly version must resolve to a real product version");
    }

    /// <summary>
    /// 更新提示用到的每一条中文文案都必须有英文对照，否则英文界面会出现中文残留。
    /// XAML 里的静态标签走 static-resources.json，代码里的动态文案走 strings.json，两条链都查。
    /// </summary>
    private static void LocalizationCoverage()
    {
        foreach (var resource in new[] { "Ui_UpdateOpenTip", "Ui_UpdateDismissTip" })
        {
            Check(UiCatalog.Resources.TryGetValue(resource, out var chinese), "missing UI resource: " + resource);
            Check(UiCatalog.English.ContainsKey(chinese!), "resource has no English translation: " + resource);
        }
        foreach (var text in new[]
                 {
                     "有新版本 {0} · 当前 {1}",
                     "更新检查：发现新版本 {0}",
                     "更新检查：已是最新版本",
                     "更新检查：网络不可用，未完成",
                     "更新检查：请求过于频繁，未完成",
                     "更新检查：未完成",
                     "更新检查：未完成（{0}）",
                     "未能打开更新页面：",
                 })
        {
            Check(UiCatalog.English.TryGetValue(text, out var english) && !string.IsNullOrWhiteSpace(english),
                "update notice text has no English translation: " + text);
        }
        // 中英必须真的不同，防止把中文原样抄成"英文"。
        Check(UiCatalog.English["更新检查：已是最新版本"] != "更新检查：已是最新版本", "English entry still contains Chinese");
    }

    private static void EvaluateMatrix()
    {
        var current = new Version(3, 1, 2, 0);
        // 真实响应：tag 比当前旧 → 不提示。
        var older = UpdateCheck.Evaluate(LiveLatestReleaseJson, current);
        Check(older.Outcome == UpdateCheckOutcome.UpToDate && older.Reason == "up-to-date", "live response shape not understood");
        // 同一份响应，当前版本更低 → 提示，且带上 Release 页地址。
        var newer = UpdateCheck.Evaluate(LiveLatestReleaseJson, new Version(3, 0, 4, 0));
        Check(newer.Outcome == UpdateCheckOutcome.UpdateAvailable, "newer release not detected");
        Check(newer.LatestTag == "v3.1.1", "latest tag not reported");
        Check(newer.ReleaseUrl == "https://github.com/DespairJasper/lexi-macos/releases/tag/v3.1.1", "release url not reported");
        // draft / prerelease 必须排除，即使 tag 更高。
        foreach (var flag in new[] { "draft", "prerelease" })
        {
            var body = LiveLatestReleaseJson.Replace($"\"{flag}\":false", $"\"{flag}\":true");
            var result = UpdateCheck.Evaluate(body, new Version(1, 0, 0, 0));
            Check(result.Outcome == UpdateCheckOutcome.UpToDate && result.Reason == flag, flag + " release was offered as an update");
        }
        // 异常响应一律失败，绝不提示。
        foreach (var body in new[] { "", "   ", "not json", "[]", "null", "{\"tag_name\":123}",
                                     "{\"tag_name\":\"v3.1.1\"}", "{\"html_url\":\"https://github.com/DespairJasper/lexi-macos/releases/tag/v3.1.1\"}",
                                     "{\"tag_name\":\"weird\",\"html_url\":\"https://github.com/DespairJasper/lexi-macos/releases/tag/weird\",\"draft\":false,\"prerelease\":false}" })
        {
            var result = UpdateCheck.Evaluate(body, new Version(1, 0, 0, 0));
            Check(result.Outcome != UpdateCheckOutcome.UpdateAvailable, "malformed response produced an update prompt: " + body);
        }
        // 响应里的地址不能任意：只接受本仓库 releases 页的 https 地址。
        foreach (var hostile in new[] { "http://github.com/DespairJasper/lexi-macos/releases/tag/v9.9.9",
                                        "https://evil.example/DespairJasper/lexi-macos/releases/x",
                                        "https://github.com/Other/repo/releases/tag/v9.9.9",
                                        "javascript:alert(1)" })
        {
            var body = "{\"tag_name\":\"v9.9.9\",\"html_url\":" + System.Text.Json.JsonSerializer.Serialize(hostile) + ",\"draft\":false,\"prerelease\":false}";
            var result = UpdateCheck.Evaluate(body, new Version(1, 0, 0, 0));
            Check(result.Outcome == UpdateCheckOutcome.Failed, "unsafe release url was accepted: " + hostile);
        }
    }

    private static async Task TransportMatrixAsync()
    {
        // 真实响应里的 tag 是 v3.1.1；用 3.0.4 当作"当前版本"来验证"确实检测到更新"的分支。
        var current = new Version(3, 0, 4, 0);
        var json = Encoding.UTF8.GetBytes(LiveLatestReleaseJson);

        // 正常响应：一次请求、GET、带 UA 与 Accept、绝不带 Authorization。
        var ok = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, json)));
        var okResult = await new UpdateChecker(new HttpClient(ok), TimeSpan.FromSeconds(5)).CheckAsync(current);
        Check(okResult.Outcome == UpdateCheckOutcome.UpdateAvailable, "successful response not handled");
        Check(ok.Calls == 1, "checker must issue exactly one request");
        Check(ok.LastRequest!.Method == HttpMethod.Get, "checker must use GET");
        Check(ok.LastRequest.RequestUri == UpdateChecker.LatestReleaseEndpoint, "checker must use the official releases/latest endpoint");
        Check(ok.LastRequest.Headers.UserAgent.ToString().Contains("Lexi/" + AppVersion.Display), "user agent must carry the product version");
        Check(ok.LastRequest.Headers.UserAgent.ToString().Contains($"{UpdateChecker.Owner}/{UpdateChecker.Repository}"), "user agent must carry the repository slug");
        Check(ok.LastRequest.Headers.Accept.ToString().Contains("application/vnd.github+json"), "checker must request the GitHub media type");
        Check(ok.LastRequest.Headers.Authorization == null, "checker must never send credentials");

        // 已是最新：不提示。
        var upToDate = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, json)));
        var upToDateResult = await new UpdateChecker(new HttpClient(upToDate), TimeSpan.FromSeconds(5)).CheckAsync(new Version(9, 9, 9, 9));
        Check(upToDateResult.Outcome == UpdateCheckOutcome.UpToDate, "newer local version should not prompt");

        // HTTP 失败与限流：静默失败，不重试。
        foreach (var (code, reason) in new[] { (HttpStatusCode.Forbidden, "rate-limited"), (HttpStatusCode.TooManyRequests, "rate-limited"),
                                               (HttpStatusCode.NotFound, "http-404"), (HttpStatusCode.InternalServerError, "http-500"),
                                               (HttpStatusCode.BadGateway, "http-502") })
        {
            var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(code)));
            var result = await new UpdateChecker(new HttpClient(handler), TimeSpan.FromSeconds(5)).CheckAsync(current);
            Check(result.Outcome == UpdateCheckOutcome.Failed && result.Reason == reason, $"HTTP {(int)code} not handled as {reason}");
            Check(handler.Calls == 1, "failed check must not retry");
        }
        // 断网：传输层异常被折叠成固定代码，异常文本不外泄。
        var offline = new StubHandler((_, _) => throw new HttpRequestException("secret-host https://internal.example token=abc"));
        var offlineResult = await new UpdateChecker(new HttpClient(offline), TimeSpan.FromSeconds(5)).CheckAsync(current);
        Check(offlineResult.Outcome == UpdateCheckOutcome.Failed && offlineResult.Reason == "network", "network failure not folded into a fixed code");
        Check(!offlineResult.Reason.Contains("secret") && !offlineResult.Reason.Contains("internal.example"), "transport details leaked into the result");

        // 超时：请求悬挂时必须按时放弃，且与调用方取消区分开。
        var hanging = new StubHandler(async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(30), token); return new HttpResponseMessage(HttpStatusCode.OK); });
        var timeoutResult = await new UpdateChecker(new HttpClient(hanging), TimeSpan.FromMilliseconds(120)).CheckAsync(current);
        Check(timeoutResult.Outcome == UpdateCheckOutcome.Failed && timeoutResult.Reason == "timeout", "hanging request did not time out");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var cancelHandler = new StubHandler((_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
            var cancelledResult = await new UpdateChecker(new HttpClient(cancelHandler), TimeSpan.FromSeconds(5)).CheckAsync(current, cancelled.Token);
            Check(cancelledResult.Reason == "cancelled", "caller cancellation must be distinguishable from timeout");
        }

        // 异常响应体与超长响应：不提示、不崩溃。
        var malformed = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>not the api</html>"))));
        Check((await new UpdateChecker(new HttpClient(malformed), TimeSpan.FromSeconds(5)).CheckAsync(current)).Reason == "malformed-json", "non-JSON body not rejected");
        var oversize = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, new byte[UpdateCheck.MaxResponseBytes + 4096])));
        Check((await new UpdateChecker(new HttpClient(oversize), TimeSpan.FromSeconds(5)).CheckAsync(current)).Reason == "response-too-large", "oversize body not bounded");
    }

    private static HttpResponseMessage Json(HttpStatusCode code, byte[] body) => new(code)
    {
        Content = new ByteArrayContent(body) is var content && SetJson(content) ? content : content
    };

    private static bool SetJson(ByteArrayContent content)
    {
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return true;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        internal StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        internal int Calls { get; private set; }
        internal HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return _respond(request, cancellationToken);
        }
    }
}
