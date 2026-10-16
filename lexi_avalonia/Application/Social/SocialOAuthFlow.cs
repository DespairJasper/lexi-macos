using System.Security.Cryptography;
using System.Text;

namespace Lexi.Social;

public sealed record SocialOAuthStart(Uri AuthorizationUri);

// Deliberately not a positional record: auto-generated ToString would expose credentials.
public sealed class SocialOAuthExchange
{
    public string Code { get; }
    public string Verifier { get; }
    internal SocialOAuthExchange(string code, string verifier) { Code = code; Verifier = verifier; }
    public override string ToString() => "SocialOAuthExchange([redacted])";
}

/// <summary>Single in-memory PKCE attempt. Process restart/cancel invalidates the attempt.</summary>
public sealed class SocialOAuthFlow
{
    public const string CallbackBase = "com.lexi.app://auth/callback";
    private readonly object _gate = new();
    private string? _verifier;
    private string? _flowId;
    private DateTimeOffset _expiresAt;

    public SocialOAuthStart Begin(Uri projectUrl, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(projectUrl);
        if (!projectUrl.IsAbsoluteUri || projectUrl.Scheme != "https" || projectUrl.UserInfo.Length > 0 ||
            !projectUrl.IsDefaultPort || projectUrl.AbsolutePath != "/" || projectUrl.Query.Length > 0 ||
            projectUrl.Fragment.Length > 0 || !projectUrl.Host.EndsWith(".supabase.co", StringComparison.Ordinal) ||
            projectUrl.Host[..^".supabase.co".Length].Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Expected an HTTPS Supabase project origin.", nameof(projectUrl));
        lock (_gate)
        {
            if (_verifier is not null && now < _expiresAt)
                throw new InvalidOperationException("A GitHub login is already pending.");
            _verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            _flowId = Base64Url(RandomNumberGenerator.GetBytes(32));
            _expiresAt = now.AddMinutes(10);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier)));
            var redirect = CallbackBase + "?flow_id=" + Uri.EscapeDataString(_flowId);
            var uri = new Uri(projectUrl, "/auth/v1/authorize?provider=github&scopes=user%3Aemail&redirect_to=" +
                Uri.EscapeDataString(redirect) + "&code_challenge=" + challenge + "&code_challenge_method=s256");
            return new(uri);
        }
    }

    public bool TryConsumeCallback(Uri callback, DateTimeOffset now,
        out SocialOAuthExchange? exchange, out string? publicError)
    {
        exchange = null; publicError = null;
        if (callback is null || !callback.IsAbsoluteUri || callback.Scheme != "com.lexi.app" ||
            callback.Host != "auth" || callback.AbsolutePath != "/callback" || callback.UserInfo.Length > 0 ||
            !callback.IsDefaultPort || callback.Fragment.Length > 0 || callback.Query.Length > 4096) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2) return false;
            try
            {
                if (!values.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]))) return false;
            }
            catch (UriFormatException) { return false; }
        }
        lock (_gate)
        {
            if (_verifier is null || _flowId is null) return false;
            if (now >= _expiresAt) { Clear(); publicError = "登录等待已过期，请重新登录。"; return false; }
            if (!values.TryGetValue("flow_id", out var id) || id.Length != _flowId.Length ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(id), Encoding.ASCII.GetBytes(_flowId))) return false;
            if (values.ContainsKey("error"))
            {
                Clear(); publicError = "GitHub 登录未完成，可以再试一次。"; return false;
            }
            if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 1024 ||
                code.Any(char.IsControl)) return false;
            exchange = new(code, _verifier);
            Clear();
            return true;
        }
    }

    public void Cancel() { lock (_gate) Clear(); }
    private void Clear() { _verifier = null; _flowId = null; _expiresAt = default; }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
