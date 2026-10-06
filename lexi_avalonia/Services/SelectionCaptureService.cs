namespace Lexi;

internal interface ISelectionClipboard : IDisposable
{
    nint Foreground { get; }
    bool ModifiersReleased { get; }
    string? ReadSelectedText(nint source) => null;
    uint Sequence { get; }
    bool TrySnapshot();
    bool SendCopy();
    (string? Text, uint Sequence) ReadText(nint source);
    bool Restore(uint expectedSequence, nint source = 0);
}

public sealed class SelectionCaptureService : ISelectionCapture
{
    private readonly ISelectionClipboard _clipboard;
    internal SelectionCaptureService(ISelectionClipboard clipboard) => _clipboard = clipboard;

    public static SelectionCaptureService? CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return new SelectionCaptureService(new Win32SelectionClipboard());
        if (OperatingSystem.IsMacOS()) return new SelectionCaptureService(new MacOSSelectionClipboard());
        return null;
    }

    public static string? NormalizeWord(string? text)
    {
        var word = text?.Trim();
        if (string.IsNullOrEmpty(word) || word.Length > 64) return null;
        return System.Text.RegularExpressions.Regex.IsMatch(word, @"^[A-Za-z]+(?:[-'’][A-Za-z]+)*$") ? word : null;
    }

    public static string? NormalizeText(string? text)
    {
        if (text == null || text.Length > 4096) return null;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return normalized.Length == 0 || normalized.Contains('\0') ? null : normalized;
    }

    public Task<string?> CaptureTextAsync(nint source) => CaptureOnWorkerAsync(source, false);

    // All clipboard work runs on one worker thread. Never block Avalonia's dispatcher.
    public Task<string?> CaptureAsync(nint source) => CaptureOnWorkerAsync(source, true);

    private Task<string?> CaptureOnWorkerAsync(nint source, bool wordOnly) => Task.Run(() =>
    {
        using var pool = OperatingSystem.IsMacOS() ? new MacOSNative.AutoreleasePool() : null;
        var text = Capture(source);
        return wordOnly ? NormalizeWord(text) : NormalizeText(text);
    });

    private string? Capture(nint source)
    {
        using var clipboard = _clipboard;
        if (source == 0 || clipboard.Foreground != source) return null;
        var selected = clipboard.ReadSelectedText(source);
        if (clipboard.Foreground != source) return null;
        if (selected != null) return selected;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!clipboard.ModifiersReleased && timer.ElapsedMilliseconds < 1000) Thread.Sleep(15);
        if (!clipboard.ModifiersReleased || clipboard.Foreground != source) return null;
        var before = clipboard.Sequence;
        if (!clipboard.TrySnapshot() || clipboard.Sequence != before || clipboard.Foreground != source || !clipboard.SendCopy()) return null;
        timer.Restart();
        while (clipboard.Sequence == before && timer.ElapsedMilliseconds < 1000) Thread.Sleep(15);
        if (clipboard.Sequence == before || clipboard.Foreground != source) return null; // Ctrl+C did nothing: never use stale text.
        var observed = clipboard.Sequence;
        var copied = clipboard.ReadText(source);
        if (copied.Sequence == 0 || copied.Sequence != observed || copied.Sequence == before || clipboard.Foreground != source) return null;
        // Restore only our copy; a later user clipboard update always wins.
        if (clipboard.Sequence != copied.Sequence) return null;
        clipboard.Restore(copied.Sequence, source);
        return clipboard.Foreground == source ? copied.Text : null;
    }
}
