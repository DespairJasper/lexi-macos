using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Lexi;

/// <summary>
/// 从 GitHub 官方 REST API 读取本项目的**最新正式 Release**。
/// 只做一次请求、不重试、不缓存、不写文件；任何失败都返回 <see cref="UpdateCheckOutcome.Failed"/>，
/// 绝不抛给调用方（沿用 MemoryOptimizer 的降级纪律：可选能力失败只留诊断）。
/// 匿名请求，不携带任何 token，也不上传任何本机数据。
/// </summary>
internal sealed class UpdateChecker
{
    internal const string Owner = "DespairJasper";
    internal const string Repository = "lexi-macos";

    /// <summary>官方端点，语义即"最新非 draft 非 prerelease 的正式 Release"。</summary>
    internal static readonly Uri LatestReleaseEndpoint =
        new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    /// <summary>检查的总时限。启动不被阻塞是硬要求，因此它必须短。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    });

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public UpdateChecker() : this(SharedHttpClient, DefaultTimeout) { }

    // 注入的 client 由调用方拥有；测试用假 handler 驱动真实 HTTP 语义。
    internal UpdateChecker(HttpClient httpClient, TimeSpan timeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
    }

    internal async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            // 版本取自程序集，不在此处硬编码第二份产品版本。
            request.Headers.UserAgent.ParseAdd($"Lexi/{AppVersion.Display} (+https://github.com/{Owner}/{Repository})");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 403/429 是匿名限额的正常结果，与断网同等对待：静默、不重试。
                var code = (int)response.StatusCode;
                return UpdateCheckResult.Failed(code is 403 or 429 ? "rate-limited" : $"http-{code}");
            }
            var body = await ReadBoundedAsync(response.Content, cts.Token).ConfigureAwait(false);
            if (body == null) return UpdateCheckResult.Failed("response-too-large");
            return UpdateCheck.Evaluate(body, current);
        }
        catch (OperationCanceledException)
        {
            // 调用方取消与自身超时必须区分，否则无法定位"为什么没提示"。
            return UpdateCheckResult.Failed(cancellationToken.IsCancellationRequested ? "cancelled" : "timeout");
        }
        catch (HttpRequestException)
        {
            // 传输层异常文本可能含 URL、代理配置与凭据；一律折叠成固定代码。
            return UpdateCheckResult.Failed("network");
        }
        catch (Exception)
        {
            return UpdateCheckResult.Failed("unexpected");
        }
    }

    /// <summary>有界读取：超限即放弃，避免异常大的响应拖垮进程。</summary>
    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0) break;
            if (memory.Length + read > UpdateCheck.MaxResponseBytes) return null;
            memory.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
