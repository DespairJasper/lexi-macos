using System.Runtime.InteropServices;

namespace Lexi;

/// <summary>Retained AVAudioPlayer for local chapter and word recordings; UI-thread owned.</summary>
internal sealed class MacAudioRecording : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private nint _player;
    private static readonly nint Framework = NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
    private static nint Selector(string name) => MacOSNative.sel_registerName(name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Initialize(nint self, nint selector, nint url, ref nint error);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern double ReadDouble(nint self, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void WriteDouble(nint self, nint selector, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint self, nint selector);
    public MacAudioRecording(string path)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(path)) throw new InvalidOperationException("本地录音不可用");
        using var pool = new MacOSNative.AutoreleasePool();
        var url = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSURL"), Selector("fileURLWithPath:"), MacOSNative.CreateNSStringHandle(path));
        var allocated = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("AVAudioPlayer"), Selector("alloc"));
        nint error = 0; _player = Initialize(allocated, Selector("initWithContentsOfURL:error:"), url, ref error);
        if (_player == 0) throw new InvalidOperationException("本地音频解码失败");
        MacOSNative.objc_msgSend_bool(_player, Selector("prepareToPlay"));
    }
    public double Position => _player == 0 ? 0 : ReadDouble(_player, Selector("currentTime"));
    public double Duration => _player == 0 ? 0 : ReadDouble(_player, Selector("duration"));
    public bool IsPlaying => _player != 0 && MacOSNative.objc_msgSend_bool(_player, Selector("isPlaying"));
    public void Play()
    {
        if (_player == 0 || !MacOSNative.objc_msgSend_bool(_player, Selector("play"))) throw new InvalidOperationException("无法播放本地录音");
    }
    public void Pause() { if (_player != 0) SendVoid(_player, Selector("pause")); }
    public void Seek(double seconds) { if (_player != 0) WriteDouble(_player, Selector("setCurrentTime:"), Math.Clamp(seconds, 0, Duration)); }
    public void Dispose()
    {
        if (_player == 0) return;
        SendVoid(_player, Selector("stop")); SendVoid(_player, Selector("release")); _player = 0;
    }
}
