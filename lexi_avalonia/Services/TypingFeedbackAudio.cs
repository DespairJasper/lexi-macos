using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lexi;

/// <summary>Small Lexi-authored typing cues. Missing platform audio support never interrupts practice.</summary>
internal sealed class TypingFeedbackAudio : IDisposable
{
    private MacAudioRecording? _key, _correct, _wrong;

    public void PlayKey() => Play(ref _key, "key-tick.wav");
    public void PlayCorrect() => Play(ref _correct, "word-correct.wav");
    public void PlayWrong() => Play(ref _wrong, "word-wrong.wav");

    public void Dispose()
    {
        _key?.Dispose(); _correct?.Dispose(); _wrong?.Dispose();
        _key = _correct = _wrong = null;
    }

    private static void Play(ref MacAudioRecording? player, string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Typing", filename);
        if (!File.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                player ??= new MacAudioRecording(path);
                player.Seek(0);
                player.Play();
            }
            else if (OperatingSystem.IsWindows())
            {
                Native.PlaySound(path, 0, Native.SndAsync | Native.SndFilename | Native.SndNodefault);
            }
            else if (OperatingSystem.IsLinux())
            {
                try { PlayCommand("paplay", path); }
                catch (System.ComponentModel.Win32Exception) { PlayCommand("aplay", path); }
            }
        }
        catch (Exception) { }
    }

    private static void PlayCommand(string command, string path)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start);
    }

    private static class Native
    {
        public const int SndAsync = 0x0001;
        public const int SndNodefault = 0x0002;
        public const int SndFilename = 0x00020000;

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PlaySound(string sound, nint module, int flags);
    }
}
