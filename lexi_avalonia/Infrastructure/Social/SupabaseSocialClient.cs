using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexi.Core;

namespace Lexi.Social;

/// <summary>Account-scoped REST transport. Never accepts a service key or copies learning records.</summary>
public sealed class SupabaseSocialClient : IDisposable
{
    private static readonly HashSet<string> RpcNames = new(StringComparer.Ordinal)
    {
        "lexi_bind", "lexi_create_invite", "lexi_claim_invite", "lexi_answer_invite", "lexi_friends",
        "lexi_set_sharing", "lexi_end_relationship", "lexi_pair_state", "lexi_submit", "lexi_restart",
        "lexi_submit_curve", "lexi_curve_list", "lexi_submit_duration", "lexi_pair_detail",
        "lexi_publish_avatar", "lexi_profile", "lexi_update_profile", "lexi_invites", "lexi_status"
    };
    private readonly SocialCloudOptions _options;
    private readonly ISecureSecretStore _secrets;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource _accountCancellation = new();
    private Session? _session;
    private long _generation;
    private bool _exchangePending;
    private bool _disposed;
    public Guid? UserId { get { lock (_gate) return _session?.UserId; } }
    public string? Nickname { get { lock (_gate) return _session?.Nickname; } }

    public SupabaseSocialClient(SocialCloudOptions options, ISecureSecretStore secrets)
        : this(options, secrets, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20) }, () => DateTimeOffset.UtcNow, ownsHttp: true) { }

    internal SupabaseSocialClient(SocialCloudOptions options, ISecureSecretStore secrets,
        HttpClient http, Func<DateTimeOffset> now)
        : this(options, secrets, http, now, ownsHttp: false) { }

    private SupabaseSocialClient(SocialCloudOptions options, ISecureSecretStore secrets,
        HttpClient http, Func<DateTimeOffset> now, bool ownsHttp)
    {
        _options = options; _secrets = secrets; _http = http; _now = now; _ownsHttp = ownsHttp;
        Restore();
    }

    public async Task ExchangeAsync(SocialOAuthExchange exchange, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var stamp = BeginExchange();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stamp.Cancel);
            var json = await SendJsonAsync("/auth/v1/token?grant_type=pkce", new { auth_code = exchange.Code, code_verifier = exchange.Verifier }, null, linked.Token);
            var next = ParseSession(json);
            Publish(next, stamp.Generation, linked.Token);
        }
        finally
        {
            lock (_gate) { if (_generation == stamp.Generation) _exchangePending = false; }
        }
    }

    public async Task<JsonElement> RpcAsync(Guid actor, string function, object arguments, CancellationToken cancellation = default)
    {
        if (!RpcNames.Contains(function)) throw new ArgumentException("Unsupported social RPC.", nameof(function));
        var stamp = Capture(actor);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stamp.Cancel);
        var session = await RequireSessionAsync(actor, stamp.Generation, linked.Token);
        var result = await SendJsonAsync("/rest/v1/rpc/" + function, arguments, session.AccessToken, linked.Token);
        CheckGeneration(actor, stamp.Generation, linked.Token);
        return result;
    }

    // Keeps account identity/generation validation and a local durable receipt commit indivisible
    // with respect to logout/relogin. Coordinator never handles access or refresh tokens.
    internal Action<Action> AccountCommitFence(Guid actor)
    {
        var stamp = Capture(actor);
        return action =>
        {
            lock (_gate)
            {
                stamp.Cancel.ThrowIfCancellationRequested();
                CheckLocked(actor, stamp.Generation);
                action();
            }
        };
    }

    public async Task UploadAvatarAsync(Guid actor, string filename, ReadOnlyMemory<byte> webp, CancellationToken cancellation = default)
    {
        if (webp.Length is < 12 or > 200000 || Encoding.ASCII.GetString(webp.Span[..4]) != "RIFF" || Encoding.ASCII.GetString(webp.Span.Slice(8, 4)) != "WEBP")
            throw new ArgumentException("Expected a WebP avatar no larger than 200KB.", nameof(webp));
        ValidateAvatarFilename(filename);
        var stamp = Capture(actor);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stamp.Cancel);
        var session = await RequireSessionAsync(actor, stamp.Generation, linked.Token);
        using var request = Request(HttpMethod.Post, "/storage/v1/object/lexi-avatars/" + actor.ToString("D") + "/" + filename, session.AccessToken);
        request.Content = new ByteArrayContent(webp.ToArray()); request.Content.Headers.ContentType = new("image/webp");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        CheckGeneration(actor, stamp.Generation, linked.Token);
    }

    public async Task<byte[]> ReadAvatarAsync(Guid actor, string path, CancellationToken cancellation = default)
    {
        var parts = path.Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out _)) throw new ArgumentException("Invalid avatar path.");
        ValidateAvatarFilename(parts[1]);
        var stamp = Capture(actor);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stamp.Cancel);
        var session = await RequireSessionAsync(actor, stamp.Generation, linked.Token);
        using var request = Request(HttpMethod.Get, "/storage/v1/object/authenticated/lexi-avatars/" + path, session.AccessToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        var bytes = await ReadLimitedAsync(response.Content, 200000, linked.Token);
        CheckGeneration(actor, stamp.Generation, linked.Token); return bytes;
    }

    public async Task DeleteAvatarAsync(Guid actor, string filename, CancellationToken cancellation = default)
    {
        ValidateAvatarFilename(filename);
        var stamp = Capture(actor);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stamp.Cancel);
        var session = await RequireSessionAsync(actor, stamp.Generation, linked.Token);
        using var request = Request(HttpMethod.Delete, "/storage/v1/object/lexi-avatars", session.AccessToken);
        request.Content = JsonContent(new { prefixes = new[] { actor.ToString("D") + "/" + filename } });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        CheckGeneration(actor, stamp.Generation, linked.Token);
    }

    /// <summary>Clear local credentials and cancel account tasks before any network revocation.</summary>
    public async Task LogoutAsync(CancellationToken cancellation = default)
    {
        Session? prior;
        lock (_gate)
        {
            prior = _session; _session = null; _exchangePending = false; _generation++;
            _accountCancellation.Cancel(); _accountCancellation.Dispose(); _accountCancellation = new();
            _secrets.Delete();
        }
        if (prior is null) return;
        try { await SendJsonAsync("/auth/v1/logout?scope=local", null, prior.AccessToken, cancellation); }
        catch (Exception error) when (error is HttpRequestException or SocialCloudException or OperationCanceledException) { /* Local logout is already durable. */ }
    }

    private async Task<Session> RequireSessionAsync(Guid actor, long generation, CancellationToken cancellation)
    {
        CheckGeneration(actor, generation, cancellation);
        await _refresh.WaitAsync(cancellation);
        try
        {
            Session current;
            lock (_gate) { CheckLocked(actor, generation); current = _session!; }
            if (current.ExpiresAt > _now().AddSeconds(60)) return current;
            JsonElement json;
            try { json = await SendJsonAsync("/auth/v1/token?grant_type=refresh_token", new { refresh_token = current.RefreshToken }, null, cancellation); }
            catch (SocialCloudException error) when (error.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                lock (_gate)
                {
                    if (_generation == generation) { _session = null; _secrets.Delete(); }
                }
                throw new InvalidOperationException("社交登录已过期，请重新登录。");
            }
            var next = ParseSession(json);
            if (next.UserId != actor) throw new InvalidOperationException("The refreshed account changed; no records were sent.");
            Publish(next, generation, cancellation, current); return next;
        }
        finally { _refresh.Release(); }
    }

    private async Task<JsonElement> SendJsonAsync(string path, object? body, string? token, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Post, path, token);
        if (body is not null) request.Content = JsonContent(body);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.NoContent) return JsonSerializer.SerializeToElement(new { });
            var bytes = await ReadLimitedAsync(response.Content, 2 * 1024 * 1024, cancellation);
            if (bytes.Length == 0) return JsonSerializer.SerializeToElement(new { });
            try { using var document = JsonDocument.Parse(bytes); return document.RootElement.Clone(); }
            catch (JsonException) { throw new InvalidOperationException("Cloud response was not valid JSON."); }
        }
        // This token already links caller and account (and sharing for secondary RPCs).
        // HttpClient's internal timeout must enter network backoff; genuine cancellation propagates.
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new HttpRequestException("社交请求超时，请稍后重试。");
        }
    }
    private HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(_options.ProjectUrl, path));
        request.Headers.Add("apikey", _options.PublicKey); request.Headers.Accept.Add(new("application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidOperationException("Cloud response exceeded its size limit.");
        using var stream = await content.ReadAsStreamAsync(cancellation); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellation); if (read == 0) break;
            if (output.Length + read > maximum) throw new InvalidOperationException("Cloud response exceeded its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private void Restore()
    {
        var saved = _secrets.Get(); if (string.IsNullOrEmpty(saved)) return;
        try
        {
            using var document = JsonDocument.Parse(saved); var root = document.RootElement;
            if (root.GetProperty("project").GetString() != _options.ProjectUrl.GetLeftPart(UriPartial.Authority)) return;
            _session = ParseSession(root.GetProperty("session"), allowExpired: true);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { _session = null; /* Preserve unreadable credential until explicit logout; never replace the AI key. */ }
    }
    private Session ParseSession(JsonElement json, bool allowExpired = false)
    {
        try
        {
            var access = json.GetProperty("access_token").GetString()!; var refresh = json.GetProperty("refresh_token").GetString()!;
            var id = Guid.Parse(json.GetProperty("user").GetProperty("id").GetString()!);
            var expiry = DateTimeOffset.FromUnixTimeSeconds(json.GetProperty("expires_at").GetInt64());
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(refresh) || access.Length > 16384 || refresh.Length > 4096 ||
                access.Any(char.IsWhiteSpace) || refresh.Any(char.IsControl) || !allowExpired && expiry <= _now())
                throw new InvalidOperationException();
            var parts = access.Split('.'); if (parts.Length != 3) throw new InvalidOperationException();
            using var claims = JsonDocument.Parse(SocialCloudOptions.Decode(parts[1]));
            if (Guid.Parse(claims.RootElement.GetProperty("sub").GetString()!) != id) throw new InvalidOperationException();
            var nickname = "GitHub 用户";
            if (json.GetProperty("user").TryGetProperty("user_metadata", out var metadata))
                foreach (var key in new[] { "user_name", "full_name", "name" })
                    if (metadata.TryGetProperty(key, out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())) { nickname = name.GetString()!; break; }
            nickname = nickname.Length > 32 ? nickname[..32] : nickname;
            // Provider tokens and GitHub metadata are intentionally discarded, even if Auth returns them.
            var safe = JsonSerializer.SerializeToElement(new { access_token = access, refresh_token = refresh,
                expires_at = expiry.ToUnixTimeSeconds(), user = new { id, user_metadata = new { user_name = nickname } } });
            return new Session(id, nickname, access, refresh, expiry, safe);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        { throw new InvalidOperationException("The authentication response was invalid; no session was stored."); }
    }
    private void Publish(Session session, long generation, CancellationToken cancellation, Session? expected = null)
    {
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_disposed || _generation != generation || expected is not null && !ReferenceEquals(_session, expected)) throw new OperationCanceledException();
            _secrets.Set(JsonSerializer.Serialize(new { project = _options.ProjectUrl.GetLeftPart(UriPartial.Authority), session = session.Json }));
            _session = session;
        }
    }
    private (long Generation, CancellationToken Cancel) BeginExchange()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) throw new InvalidOperationException("Log out before starting another social login.");
            if (_exchangePending) throw new InvalidOperationException("A social login is already pending.");
            _generation++;
            _accountCancellation.Cancel(); _accountCancellation.Dispose(); _accountCancellation = new();
            _exchangePending = true;
            return (_generation, _accountCancellation.Token);
        }
    }
    private (long Generation, CancellationToken Cancel) Capture(Guid? actor)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (actor is not null && _session?.UserId != actor) throw new InvalidOperationException("Account cannot access this social session.");
            return (_generation, _accountCancellation.Token);
        }
    }
    private void CheckGeneration(Guid actor, long generation, CancellationToken cancellation)
    { cancellation.ThrowIfCancellationRequested(); lock (_gate) CheckLocked(actor, generation); }
    private void CheckLocked(Guid actor, long generation)
    { if (_disposed || _generation != generation || _session?.UserId != actor) throw new OperationCanceledException(); }
    private static void ValidateAvatarFilename(string filename)
    {
        if (filename.Length is < 6 or > 80 || !filename.EndsWith(".webp", StringComparison.Ordinal) || filename[..^5].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Invalid avatar filename.");
    }
    private static SocialCloudException Failure(HttpStatusCode status) => new(status);
    public override string ToString() => "SupabaseSocialClient([redacted])";
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; _session = null; _generation++;
            _accountCancellation.Cancel(); _accountCancellation.Dispose(); if (_ownsHttp) _http.Dispose();
        }
    }
    private sealed class Session(Guid userId, string nickname, string access, string refresh, DateTimeOffset expiry, JsonElement json)
    {
        public Guid UserId { get; } = userId; public string Nickname { get; } = nickname;
        public string AccessToken { get; } = access; public string RefreshToken { get; } = refresh;
        public DateTimeOffset ExpiresAt { get; } = expiry; public JsonElement Json { get; } = json;
        public override string ToString() => "SocialSession([redacted])";
    }
}

public sealed class SocialCloudException(System.Net.HttpStatusCode status) : Exception($"社交服务暂时无法完成请求（HTTP {(int)status}），学习记录仍保存在本地。")
{
    public System.Net.HttpStatusCode Status { get; } = status;
}
