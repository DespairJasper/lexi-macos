using System.Text;
using System.Text.Json;

namespace Lexi.Social;

// Only the public project key is accepted. Credentials are never included in diagnostics.
public sealed class SocialCloudOptions
{
    public Uri ProjectUrl { get; }
    public string PublicKey { get; }
    public SocialCloudOptions(Uri projectUrl, string publicKey)
    {
        _ = new SocialOAuthFlow().Begin(projectUrl, DateTimeOffset.UtcNow);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        if (!IsPublicKey(publicKey)) throw new ArgumentException("A Supabase publishable or anon key is required.", nameof(publicKey));
        ProjectUrl = projectUrl; PublicKey = publicKey;
    }
    public static SocialCloudOptions? FromEnvironment()
    {
        var origin = Environment.GetEnvironmentVariable("LEXI_SUPABASE_URL");
        var key = Environment.GetEnvironmentVariable("LEXI_SUPABASE_PUBLISHABLE_KEY");
        return string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(key) ? null : new(new Uri(origin), key);
    }
    public static string ConfigurationPath(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), "social-public.json");

    public static SocialCloudOptions? FromConfiguration(string dataDirectory, bool automatedTest)
    {
        // Test runs cannot accidentally load a production project or credential.
        if (automatedTest) return null;
        var environment = FromEnvironment();
        if (environment is not null) return environment;
        var path = Environment.GetEnvironmentVariable("LEXI_SOCIAL_CONFIG");
        if (string.IsNullOrWhiteSpace(path)) path = ConfigurationPath(dataDirectory);
        if (File.Exists(path)) return ReadConfiguration(path);

        // Published development output keeps this file beside the executable. The macOS
        // packager places it at Contents/Resources/social-public.json instead.
        var baseDirectory = AppContext.BaseDirectory;
        var publishPath = Path.Combine(baseDirectory, "social-public.json");
        if (File.Exists(publishPath)) return ReadConfiguration(publishPath);
        var bundlePath = Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources", "social-public.json"));
        return File.Exists(bundlePath) ? ReadConfiguration(bundlePath) : null;
    }

    private static SocialCloudOptions ReadConfiguration(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 16384) throw new InvalidDataException("社交连接配置无效；已保留原文件。");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return new(new Uri(root.GetProperty("project_url").GetString()!), root.GetProperty("public_key").GetString()!);
    }

    public void SaveConfiguration(string path) => SaveConfigurationCore(path, false);

    private void SaveConfigurationCore(string path, bool explicitlyRepairing)
    {
        path = Path.GetFullPath(path);
        // Never silently replace a file we cannot understand.
        if (!explicitlyRepairing && File.Exists(path)) _ = ReadConfiguration(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, new { project_url = ProjectUrl.GetLeftPart(UriPartial.Authority), public_key = PublicKey });
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Only the explicit recovery action can replace a malformed public file.
    // Copy the original first, so the active path remains present until atomic replacement.
    public void SaveConfigurationPreservingOriginal(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) { SaveConfiguration(path); return; }
        try { _ = ReadConfiguration(path); }
        catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {
            var preserved = path + ".preserved-" + Guid.NewGuid().ToString("N");
            File.Copy(path, preserved, false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(preserved, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            SaveConfigurationCore(path, true);
            return;
        }
        throw new InvalidOperationException("原配置可以读取，请使用保存本地连接配置。");
    }

    private static bool IsPublicKey(string key)
    {
        if (key.Length > 4096 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl)) return false;
        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal)) return key.Length > 15;
        try
        {
            var parts = key.Split('.'); if (parts.Length != 3) return false;
            using var json = JsonDocument.Parse(Decode(parts[1]));
            return json.RootElement.TryGetProperty("role", out var role) && role.GetString() == "anon";
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException) { return false; }
    }
    internal static byte[] Decode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
    }
    public override string ToString() => $"SocialCloudOptions({ProjectUrl.Host}, [redacted])";
}
