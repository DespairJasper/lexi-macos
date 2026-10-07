using System.Text.Json;

namespace Lexi;

internal enum UpdateCheckOutcome
{
    /// <summary>当前已是最新，或拿到了一个明确"不更新"的响应（draft / prerelease / 标签不可解析）。</summary>
    UpToDate,
    UpdateAvailable,
    /// <summary>网络、限流、超时、响应格式异常等。调用方必须静默降级。</summary>
    Failed,
}

/// <summary>
/// <paramref name="Reason"/> 是**短 ASCII 代码**，只用于诊断与测试断言：
/// 不含 URL、主机名、异常文本或任何用户内容（沿用 AiService 的错误脱敏约定）。
/// </summary>
internal sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, string? LatestTag, string? ReleaseUrl, string Reason)
{
    internal static UpdateCheckResult UpToDate(string reason) => new(UpdateCheckOutcome.UpToDate, null, null, reason);
    internal static UpdateCheckResult Failed(string reason) => new(UpdateCheckOutcome.Failed, null, null, reason);
    internal static UpdateCheckResult Available(string tag, string url) => new(UpdateCheckOutcome.UpdateAvailable, tag, url, "update-available");
}

/// <summary>
/// GitHub 正式 Release 响应的**纯解析与判定**：不联网、不读时钟、无副作用，可穷举测试。
/// </summary>
internal static class UpdateCheck
{
    /// <summary>响应体上限，防止异常大的响应占用内存（沿用 AiService 的有界读取思路）。</summary>
    internal const int MaxResponseBytes = 256 * 1024;

    /// <summary>
    /// 判定给定的 releases/latest 响应。默认排除 draft 与 prerelease。
    /// 任何无法确定的情况一律返回 UpToDate/Failed —— **绝不**把不确定当作"有新版本"。
    /// </summary>
    internal static UpdateCheckResult Evaluate(string? json, Version current)
    {
        if (string.IsNullOrWhiteSpace(json)) return UpdateCheckResult.Failed("empty-response");
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return UpdateCheckResult.Failed("malformed-json"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return UpdateCheckResult.Failed("malformed-json");
            // 防御：即使换用列表端点，也必须排除 draft 与 prerelease。
            if (ReadBool(root, "draft")) return UpdateCheckResult.UpToDate("draft");
            if (ReadBool(root, "prerelease")) return UpdateCheckResult.UpToDate("prerelease");
            var tag = ReadString(root, "tag_name");
            if (tag == null) return UpdateCheckResult.Failed("missing-tag");
            var url = ReadString(root, "html_url");
            if (url == null) return UpdateCheckResult.Failed("missing-url");
            var parsed = AppVersion.Parse(tag);
            if (parsed == null) return UpdateCheckResult.UpToDate("unparsable-tag");
            if (parsed <= current) return UpdateCheckResult.UpToDate("up-to-date");
            // 只接受指向本仓库 releases 页的 https 地址；不把响应里的任意串交给浏览器。
            if (!IsSafeReleaseUrl(url)) return UpdateCheckResult.Failed("unsafe-url");
            return UpdateCheckResult.Available(tag, url);
        }
    }

    internal static bool IsSafeReleaseUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{UpdateChecker.Owner}/{UpdateChecker.Repository}/releases/", StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString())
            : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
