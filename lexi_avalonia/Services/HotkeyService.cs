using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lexi;

public enum QuickAction { Lookup, Translate, SaveQuote }

public readonly record struct HotkeyRegistrationStatus(string Key, bool IsRegistered, string? Error)
{
    public string Shortcut => (OperatingSystem.IsMacOS() ? "Option+" : "Alt+") + Key;
}

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 9001;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkD = 0x44; // 'D'
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern ushort RegisterClassEx(ref Wndclassex lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct Wndclassex
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private WndProcDelegate? _wndProc;
    private readonly ManualResetEventSlim _started = new(false);
    private bool _isRegistered;
    private MacOSHotkeyListener? _macListener;

    public bool IsRegistered => OperatingSystem.IsMacOS() ? (_macListener?.IsRegistered ?? false) : _isRegistered;
    public event Action? HotkeyPressed;
    public event Action<QuickAction>? QuickActionPressed;
    private string _lookupKey = "D", _translateKey = "A", _quoteKey = "S";
    private bool _hasStarted, _disposed;

    public HotkeyRegistrationStatus GetStatus(QuickAction action)
    {
        if (_macListener != null) return _macListener.GetStatus(action);
        var key = action switch { QuickAction.Lookup => _lookupKey, QuickAction.Translate => _translateKey, _ => _quoteKey };
        return new(key, action == QuickAction.Lookup && _isRegistered, _hasStarted && action != QuickAction.Lookup ? "此平台尚未注册该快捷键。" : null);
    }

    public void Configure(string lookupKey, string translateKey, string quoteKey)
    {
        if (_disposed) return;
        _lookupKey = (lookupKey ?? "").Trim().ToUpperInvariant();
        _translateKey = (translateKey ?? "").Trim().ToUpperInvariant();
        _quoteKey = (quoteKey ?? "").Trim().ToUpperInvariant();
        if (OperatingSystem.IsMacOS() && _hasStarted)
        {
            _macListener?.Dispose();
            _macListener = null;
            _hasStarted = false;
            Start();
        }
    }

    public void Start()
    {
        if (_hasStarted || _disposed) return;
        _hasStarted = true;
        if (OperatingSystem.IsMacOS())
        {
            _macListener = new MacOSHotkeyListener();
            _macListener.Configure(_lookupKey, _translateKey, _quoteKey);
            _macListener.QuickActionPressed += action =>
            {
                QuickActionPressed?.Invoke(action);
                if (action == QuickAction.Lookup) HotkeyPressed?.Invoke();
            };
            _macListener.Start();
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "Lexi_Hotkey_Listener"
        };
        _thread.Start();

        _started.Wait(TimeSpan.FromSeconds(2));
    }

    private void RunMessageLoop()
    {
        try
        {
            var className = "Lexi_HotkeyWindow_" + Guid.NewGuid().ToString("N");
            _wndProc = WndProc;

            var wndClass = new Wndclassex
            {
                cbSize = (uint)Marshal.SizeOf<Wndclassex>(),
                lpfnWndProc = _wndProc,
                hInstance = GetModuleHandle(null),
                lpszClassName = className
            };

            var classAtom = RegisterClassEx(ref wndClass);
            if (classAtom == 0)
            {
                _started.Set();
                return;
            }

            // HWND_MESSAGE = -3
            var hwndMessage = new IntPtr(-3);
            _hwnd = CreateWindowEx(0, className, "LexiHotkey", 0, 0, 0, 0, 0, hwndMessage, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

            if (_hwnd != IntPtr.Zero)
            {
                _isRegistered = RegisterHotKey(_hwnd, HotkeyId, ModAlt | ModNoRepeat, VkD);
                if (!_isRegistered)
                {
                    // Try without MOD_NOREPEAT on older systems
                    _isRegistered = RegisterHotKey(_hwnd, HotkeyId, ModAlt, VkD);
                }
            }

            _started.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch
        {
            _started.Set();
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x0010)
        {
            UnregisterHotKey(hWnd, HotkeyId);
            DestroyWindow(hWnd);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            QuickActionPressed?.Invoke(QuickAction.Lookup);
            HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_macListener != null)
        {
            _macListener.Dispose();
            _macListener = null;
        }

        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                // WM_CLOSE = 0x0010
                PostMessage(_hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            _hwnd = IntPtr.Zero;
        }

        if (_thread?.Join(2000) != false) _started.Dispose();
    }
}
