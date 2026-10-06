namespace Lexi;

/// <summary>The installer opens this same path with share mode 0 and keeps it open.
/// The OS releases the handle on normal exit or crash, including hidden windows.</summary>
public sealed class InstallationGuard : IDisposable
{
    private readonly FileStream _handle;
    public InstallationGuard(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            _handle = new FileStream(Path.Combine(directory, "install.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) { throw new IOException("Lexi 正在运行或安装更新，请先退出托盘中的 Lexi 或等待安装完成。", ex); }
    }
    public void Dispose() => _handle.Dispose();
}
