using System.Runtime.InteropServices;
using System.Text;

namespace Lexi;

// Conservatively snapshot HGLOBAL formats (text, HTML/RTF, file lists, PNG, etc.).
// If any format cannot be copied safely (bitmap handles, delayed data, oversized
// payloads), skip Ctrl+C entirely. Original clipboard data is more important.
internal sealed class Win32SelectionClipboard : ISelectionClipboard
{
    private const uint UnicodeText = 13;
    private readonly List<(uint Format, byte[] Data)> _snapshot = new();
    private nint _owner;
    public nint Foreground => GetForegroundWindow();
    public uint Sequence => GetClipboardSequenceNumber();
    public bool ModifiersReleased => new[] { 0x12, 0x11, 0x10, 0x5B, 0x5C, 0x44 }.All(k => (GetAsyncKeyState(k) & 0x8000) == 0);
    public static nint CurrentForeground => GetForegroundWindow();

    private bool Open()
    {
        if (_owner == 0) _owner = CreateWindowEx(0, "STATIC", "LexiSelectionClipboard", 0, 0, 0, 0, 0, (nint)(-3), 0, 0, 0);
        if (_owner == 0) return false;
        for (var i = 0; i < 8; i++)
        {
            if (OpenClipboard(_owner)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    public bool TrySnapshot()
    {
        _snapshot.Clear();
        if (!Open()) return false;
        try
        {
            long total = 0;
            uint format = 0;
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                format = EnumClipboardFormats(format);
                if (format == 0) return Marshal.GetLastPInvokeError() == 0;
                // GDI/metafile/owner-display/private handle formats are not HGLOBAL.
                if (format is 2 or 3 or 9 or 14 or >= 0x80 and <= 0x8F or >= 0x200 and <= 0x3FF) return false;
                var handle = GetClipboardData(format);
                var size = (long)GlobalSize(handle);
                if (handle == 0 || size <= 0 || (total += size) > 16 * 1024 * 1024) return false;
                var ptr = GlobalLock(handle);
                if (ptr == 0) return false;
                try
                {
                    var bytes = new byte[(int)size];
                    Marshal.Copy(ptr, bytes, 0, bytes.Length);
                    _snapshot.Add((format, bytes));
                }
                finally { GlobalUnlock(handle); }
            }
        }
        finally { CloseClipboard(); }
    }

    public bool SendCopy()
    {
        Input Key(ushort vk, bool up = false) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = vk, Flags = up ? 2u : 0u } } };
        Input[] inputs = [Key(0x11), Key(0x43), Key(0x43, true), Key(0x11, true)];
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return true;
        // A partially accepted sequence must not leave synthetic Control/C held.
        if (sent > 0) SendInput(2, [Key(0x43, true), Key(0x11, true)], Marshal.SizeOf<Input>());
        return false;
    }

    private static bool BelongsToSource(nint source)
    {
        if (source == 0) return true;
        if (GetForegroundWindow() != source) return false;
        var owner = GetClipboardOwner();
        if (owner == 0) return false;
        GetWindowThreadProcessId(source, out var sourcePid);
        GetWindowThreadProcessId(owner, out var ownerPid);
        return sourcePid != 0 && sourcePid == ownerPid;
    }

    public (string? Text, uint Sequence) ReadText(nint source)
    {
        if (!Open()) return (null, 0);
        try
        {
            if (!BelongsToSource(source)) return (null, 0);
            var sequence = Sequence;
            var handle = GetClipboardData(UnicodeText);
            var size = (long)GlobalSize(handle);
            if (handle == 0 || size <= 0 || size > 4096) return (null, sequence);
            var ptr = GlobalLock(handle);
            if (ptr == 0) return (null, sequence);
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(ptr, bytes, 0, bytes.Length);
                return (Encoding.Unicode.GetString(bytes).Split('\0')[0], sequence);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    public bool Restore(uint expectedSequence, nint source = 0)
    {
        // Prepare every allocation before emptying the clipboard.
        var allocations = new List<(uint Format, nint Handle)>();
        try
        {
            foreach (var (format, data) in _snapshot)
            {
                var handle = GlobalAlloc(0x42, (nuint)data.Length);
                if (handle == 0) return false;
                allocations.Add((format, handle));
                var ptr = GlobalLock(handle);
                if (ptr == 0) return false;
                try { Marshal.Copy(data, 0, ptr, data.Length); }
                finally { GlobalUnlock(handle); }
            }
            if (!Open()) return false;
            try
            {
                if (Sequence != expectedSequence || !BelongsToSource(source) || !EmptyClipboard()) return false;
                var ok = true;
                for (var i = 0; i < allocations.Count; i++)
                {
                    var item = allocations[i];
                    if (SetClipboardData(item.Format, item.Handle) != 0) allocations[i] = (item.Format, 0);
                    else ok = false;
                }
                return ok;
            }
            finally { CloseClipboard(); }
        }
        finally { foreach (var item in allocations) if (item.Handle != 0) GlobalFree(item.Handle); }
    }

    public void Dispose() { if (_owner != 0) { DestroyWindow(_owner); _owner = 0; } }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
}
