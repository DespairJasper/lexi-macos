using System.Diagnostics;

namespace Lexi;

/// <summary>One cancellable playback owner. Recordings fall back to native English speech.</summary>
public sealed class LocalWordAudioPlayer(Action<string> reportError) : IWordAudioPlayer
{
    private CancellationTokenSource? _cts;
    private MacAudioRecording? _recording;
    public bool CanSeek => _recording != null;
    public bool IsPlaying => _recording?.IsPlaying ?? false;
    public double Position => _recording?.Position ?? 0;
    public double Duration => _recording?.Duration ?? 0;
    public void TogglePause()
    {
        if (_recording == null) return;
        if (_recording.IsPlaying) _recording.Pause(); else _recording.Play();
    }
    public void Seek(double seconds) => _recording?.Seek(seconds);
    public void Play(string text, string? localRecording = null)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (OperatingSystem.IsMacOS() && localRecording != null && File.Exists(localRecording))
        {
            try { _recording = new MacAudioRecording(localRecording); _recording.Play(); return; }
            catch (Exception) { _recording?.Dispose(); _recording = null; }
        }
        var cts = _cts = new CancellationTokenSource();
        _ = PlayAsync(text, localRecording, cts.Token);
    }
    private async Task PlayAsync(string text, string? recording, CancellationToken token)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (recording != null && File.Exists(recording))
                {
                    var audio = new ProcessStartInfo("/usr/bin/afplay") { UseShellExecute = false };
                    audio.ArgumentList.Add(recording);
                    if (await RunAsync(audio, null, token) == 0) return;
                    token.ThrowIfCancellationRequested();
                }
                var speech = new ProcessStartInfo("/usr/bin/say") { UseShellExecute = false, RedirectStandardInput = true };
                speech.ArgumentList.Add("-v"); speech.ArgumentList.Add("Daniel");
                if (await RunAsync(speech, text, token) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    // Daniel may not be installed; request another native English voice.
                    var fallback = new ProcessStartInfo("/usr/bin/say") { UseShellExecute = false, RedirectStandardInput = true };
                    fallback.ArgumentList.Add("-v"); fallback.ArgumentList.Add("Samantha");
                    if (await RunAsync(fallback, text, token) != 0) reportError("本机英文语音不可用，请在系统设置中安装英文语音。");
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                var speech = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
                foreach (var arg in new[] { "-NoProfile", "-Command", "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::NotSet,[System.Speech.Synthesis.VoiceAge]::NotSet,0,[System.Globalization.CultureInfo]::GetCultureInfo('en-US')); $s.Speak([Console]::In.ReadToEnd()); $s.Dispose()" }) speech.ArgumentList.Add(arg);
                if (await RunAsync(speech, text, token) != 0) reportError("本机英文语音不可用。");
            }
            else reportError("当前平台尚未配置英文语音。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!token.IsCancellationRequested) { reportError("朗读失败：" + ex.Message); }
        catch (Exception) when (token.IsCancellationRequested) { }
    }
    private static async Task<int> RunAsync(ProcessStartInfo start, string? text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本地播放器");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        if (text != null) { await process.StandardInput.WriteAsync(text.AsMemory(), token); process.StandardInput.Close(); }
        await process.WaitForExitAsync(token);
        return process.ExitCode;
    }
    public void Stop() { _recording?.Dispose(); _recording = null; _cts?.Cancel(); _cts?.Dispose(); _cts = null; }
    public void Dispose() => Stop();
}
