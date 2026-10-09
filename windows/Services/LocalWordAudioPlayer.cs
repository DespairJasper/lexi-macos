using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Lexi;

/// <summary>Native Windows recordings and SAPI speech, with one playback owner.</summary>
public sealed class LocalWordAudioPlayer(Action<string> reportError) : IWordAudioPlayer
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int mciSendString(string command, System.Text.StringBuilder? result, int length, IntPtr callback);
    private Process? _speech;
    private readonly string _alias = "LexiAudio" + Guid.NewGuid().ToString("N");
    private bool _recording;
    public bool CanSeek => _recording;
    private string Query(string property)
    {
        var result = new System.Text.StringBuilder(256);
        return mciSendString("status " + _alias + " " + property, result, result.Capacity, IntPtr.Zero) == 0 ? result.ToString() : "";
    }
    public bool IsPlaying => _recording && Query("mode") == "playing";
    public double Position => double.TryParse(Query("position"), out var value) ? value / 1000 : 0;
    public double Duration => double.TryParse(Query("length"), out var value) ? value / 1000 : 0;
    public void TogglePause() { if (_recording) mciSendString((IsPlaying ? "pause " : "play ") + _alias, null, 0, IntPtr.Zero); }
    public void Seek(double seconds)
    {
        if (!_recording) return; var playing = IsPlaying;
        mciSendString("seek " + _alias + " to " + (int)(Math.Clamp(seconds, 0, Duration) * 1000), null, 0, IntPtr.Zero);
        if (playing) mciSendString("play " + _alias, null, 0, IntPtr.Zero);
    }
    public void Play(string text, string? localRecording = null)
    {
        Stop();
        if (!string.IsNullOrEmpty(localRecording) && File.Exists(localRecording))
        {
            if (mciSendString("open \"" + localRecording.Replace("\"", "") + "\" type mpegvideo alias " + _alias, null, 0, IntPtr.Zero) == 0 && mciSendString("play " + _alias, null, 0, IntPtr.Zero) == 0) { _recording = true; return; }
            reportError("无法播放录音，改用系统朗读。");
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var script = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Speak([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)) + "'))); $s.Dispose()";
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
            _speech = Process.Start(info);
        }
        catch (Exception ex) { reportError("系统朗读不可用：" + ex.Message); }
    }
    public void Stop()
    {
        mciSendString("close " + _alias, null, 0, IntPtr.Zero);
        _recording = false;
        try { if (_speech is { HasExited: false }) _speech.Kill(); } catch { }
        _speech?.Dispose(); _speech = null;
    }
    public void Dispose() => Stop();
}
