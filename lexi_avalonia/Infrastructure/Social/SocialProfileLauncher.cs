using System.Diagnostics;
using System.Text.Json;

namespace Lexi.Social;

/// <summary>Owns only the local profile selection and fresh empty profile directories.
/// Learning databases, queues and credentials are never moved or copied.</summary>
public sealed class SocialProfileLauncher
{
    private readonly string _configurationPath;
    public static string DefaultConfigurationPath => Environment.GetEnvironmentVariable("LEXI_PROFILE_CONFIG") is { Length: > 0 } path
        ? Path.GetFullPath(path)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexiLauncher", "profile.json");

    public SocialProfileLauncher(string configurationPath) => _configurationPath = Path.GetFullPath(configurationPath);

    public string CreateIndependentProfile(string sourceDirectory, SocialCloudOptions? publicOptions)
    {
        sourceDirectory = AbsoluteDirectory(sourceDirectory);
        // Fail before creation if an existing launcher file cannot be understood.
        if (File.Exists(_configurationPath)) _ = ReadSelection();
        var profile = Path.Combine(Path.GetDirectoryName(_configurationPath)!, "profiles", Guid.NewGuid().ToString("N"));
        var physicalSource = StoragePathIdentity.Resolve(sourceDirectory);
        var physicalProfile = StoragePathIdentity.Resolve(profile);
        var physicalConfiguration = StoragePathIdentity.Resolve(_configurationPath);
        if (IsWithin(physicalProfile, physicalSource) || IsWithin(physicalConfiguration, physicalSource) || Directory.Exists(profile))
            throw new InvalidOperationException("无法在原学习目录中创建独立档案。原数据已保留。");
        Directory.CreateDirectory(profile);
        publicOptions?.SaveConfiguration(SocialCloudOptions.ConfigurationPath(profile));
        return profile;
    }

    public void SelectProfile(string profileDirectory, string previousDirectory)
    {
        profileDirectory = AbsoluteDirectory(profileDirectory); previousDirectory = AbsoluteDirectory(previousDirectory);
        if (!Directory.Exists(profileDirectory) || string.Equals(profileDirectory, previousDirectory, PathComparison))
            throw new InvalidOperationException("独立学习档案不可用。原数据已保留。");
        if (File.Exists(_configurationPath)) _ = ReadSelection();
        Directory.CreateDirectory(Path.GetDirectoryName(_configurationPath)!);
        var temporary = _configurationPath + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, new { version = 1, active_profile = profileDirectory, previous_profile = previousDirectory });
                stream.Flush(true);
            }
            File.Move(temporary, _configurationPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void ApplyStartupSelection(string[] args, bool automatedTest)
    {
        var index = Array.IndexOf(args, "--profile-dir");
        if (index >= 0)
        {
            if (index + 1 >= args.Length || args.Count(a => a == "--profile-dir") != 1)
                throw new ArgumentException("请指定独立学习档案的绝对路径。");
            var selected = AbsoluteDirectory(args[index + 1]);
            if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("独立学习档案目录不存在。原数据已保留。");
            Environment.SetEnvironmentVariable("LEXI_DATA_DIR", selected);
            return;
        }
        if (automatedTest || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")) || !File.Exists(_configurationPath)) return;
        var profile = ReadSelection();
        if (!Directory.Exists(profile)) throw new DirectoryNotFoundException("已选择的学习档案不存在。原数据已保留。");
        Environment.SetEnvironmentVariable("LEXI_DATA_DIR", profile);
    }

    public ProcessStartInfo CreateStartInfo(string executable, string? assemblyPath, string profileDirectory)
    {
        profileDirectory = AbsoluteDirectory(profileDirectory);
        var ready = Path.Combine(Path.GetDirectoryName(_configurationPath)!, "launch", "launch-" + Guid.NewGuid().ToString("N") + ".ready");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        if (assemblyPath is not null) start.ArgumentList.Add(Path.GetFullPath(assemblyPath));
        start.ArgumentList.Add("--profile-dir"); start.ArgumentList.Add(profileDirectory);
        // Forward a minimal OS/runtime environment. Inherited cloud/API credentials are not passed.
        start.Environment.Clear();
        foreach (var name in new[] { "HOME", "USER", "LOGNAME", "PATH", "TMPDIR", "TEMP", "TMP", "LANG", "LC_ALL", "DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "SystemRoot", "WINDIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["LEXI_DATA_DIR"] = profileDirectory;
        start.Environment["LEXI_PROFILE_CONFIG"] = _configurationPath;
        start.Environment["LEXI_PROFILE_READY_FILE"] = ready;
        return start;
    }

    public async Task<Process> StartAndWaitForReadyAsync(ProcessStartInfo start, CancellationToken cancellation = default)
    {
        var ready = start.Environment["LEXI_PROFILE_READY_FILE"] ?? throw new ArgumentException("缺少档案启动握手路径。");
        ValidateReadyPath(ready); Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动独立学习档案。原窗口已保留。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("独立学习档案未能启动。原窗口与学习数据已保留。");
                if (HandshakeCompleted(ready, process.Id))
                { timeout.Token.ThrowIfCancellationRequested(); return process; }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch
        {
            try { await StopOwnedChildAsync(process); }
            finally { process.Dispose(); }
            throw;
        }
        finally { if (File.Exists(ready)) File.Delete(ready); }
    }

    /// <summary>True only once the child has written and released its own handshake file.
    /// The child creates the file exclusively and holds it while writing, so a reader can meet a
    /// transient sharing violation — that just means "still being written", i.e. not ready yet.
    /// Treating it as a failure would abandon a perfectly healthy handover and, in the app, close
    /// the original window for a child that had in fact started.</summary>
    private static bool HandshakeCompleted(string ready, int childProcessId)
    {
        try
        {
            return File.Exists(ready) &&
                File.ReadAllText(ready) == childProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return false; }
    }

    internal static async Task StopOwnedChildAsync(Process process)    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void SignalReady()
    {
        var ready = Environment.GetEnvironmentVariable("LEXI_PROFILE_READY_FILE"); if (string.IsNullOrEmpty(ready)) return;
        ValidateReadyPath(ready);
        using var stream = new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ready, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var writer = new StreamWriter(stream); writer.Write(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); writer.Flush(); stream.Flush(true);
    }

    private string ReadSelection()
    {
        try
        {
            using var stream = File.OpenRead(_configurationPath);
            if (stream.Length > 16384) throw new InvalidDataException();
            using var json = JsonDocument.Parse(stream); var root = json.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException();
            _ = AbsoluteDirectory(root.GetProperty("previous_profile").GetString()!);
            return AbsoluteDirectory(root.GetProperty("active_profile").GetString()!);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or InvalidDataException)
        { throw new InvalidDataException("本地档案配置无法读取；已保留原文件。", error); }
    }

    private void ValidateReadyPath(string path)
    {
        var expected = Path.Combine(Path.GetDirectoryName(_configurationPath)!, "launch");
        var name = Path.GetFileName(path);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), expected, PathComparison) ||
            !name.StartsWith("launch-", StringComparison.Ordinal) || !name.EndsWith(".ready", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[7..^6], "N", out _)) throw new ArgumentException("无效的本地档案启动握手路径。");
    }

    private static bool IsWithin(string path, string directory) =>
        string.Equals(path, directory, PathComparison) || path.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string AbsoluteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("学习档案必须使用绝对路径。");
        var absolute = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (absolute.Length == 0 || absolute == Path.GetPathRoot(absolute)?.TrimEnd(Path.DirectorySeparatorChar)) throw new ArgumentException("不能把磁盘根目录作为学习档案。");
        return absolute;
    }
}
