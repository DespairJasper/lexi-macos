using System.Diagnostics;

namespace Lexi;

/// <summary>macOS shell adapter; uses /usr/bin/open with ArgumentList to safely open documents.</summary>
public sealed class MacDocumentLauncher : IExternalDocumentLauncher
{
    public void Open(string path)
    {
        var psi = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false
        };
        psi.ArgumentList.Add(path);
        Process.Start(psi);
    }
}
