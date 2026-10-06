using System.Diagnostics;

namespace Lexi;

/// <summary>Desktop shell adapter; other hosts provide their document/share UI.</summary>
public sealed class WindowsDocumentLauncher : IExternalDocumentLauncher
{
    public void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
        }
    }
}
