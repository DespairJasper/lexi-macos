using System.Reflection;

namespace Lexi;

public static class NativeAudioTests
{
    public static int Run()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        try
        {
            var type = typeof(MainWindow).Assembly.GetType("Lexi.MacAudioRecording");
            if (type == null) throw new Exception("FAIL: chapter seek/pause player unavailable");
            using var player = (IDisposable)Activator.CreateInstance(type, IeltsCatalog.ResolveAsset("vocabulary/audio/01_自然地理.mp3")!)!;
            double Read(string name) => (double)type.GetProperty(name)!.GetValue(player)!;
            void Call(string name, params object[] args) => type.GetMethod(name)!.Invoke(player, args);
            if (Read("Duration") < 60) throw new Exception("FAIL: chapter duration not decoded");
            Call("Seek", 15d);
            if (Math.Abs(Read("Position") - 15) > .5) throw new Exception("FAIL: seek position");
            Call("Play"); Thread.Sleep(180); Call("Pause");
            var position = Read("Position"); Thread.Sleep(100);
            if (Math.Abs(Read("Position") - position) > .05) throw new Exception("FAIL: paused timeline advances");
            Call("Play"); Thread.Sleep(100); Call("Pause");
            if (Read("Position") <= position) throw new Exception("FAIL: resume does not advance");
            Console.WriteLine("PASS native chapter duration, seek, pause and resume"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
