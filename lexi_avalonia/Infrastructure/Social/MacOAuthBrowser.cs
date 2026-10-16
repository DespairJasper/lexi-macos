using System.Diagnostics;

namespace Lexi.Social;

/// <summary>Chrome authorization for the desktop public client. No shell interpolation or credentials in arguments.</summary>
public static class MacOAuthBrowser
{
    public static void Open(SocialOAuthStart start, SocialCloudOptions options)
    {
        ArgumentNullException.ThrowIfNull(start);
        var uri = start.AuthorizationUri;
        if (uri.Scheme != "https" || uri.GetLeftPart(UriPartial.Authority) != options.ProjectUrl.GetLeftPart(UriPartial.Authority) ||
            uri.AbsolutePath != "/auth/v1/authorize" || uri.Fragment.Length > 0)
            throw new ArgumentException("Unexpected OAuth destination.");
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This OAuth browser adapter requires macOS.");
        var process = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, CreateNoWindow = true };
        process.ArgumentList.Add("-a"); process.ArgumentList.Add("Google Chrome"); process.ArgumentList.Add(uri.AbsoluteUri);
        using var opened = Process.Start(process) ?? throw new InvalidOperationException("无法打开 Chrome，请确认已安装 Chrome。");
    }
}
