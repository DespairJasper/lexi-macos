using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lexi;

public enum QuickAction { Lookup, Translate, SaveQuote }
public readonly record struct HotkeyRegistrationStatus(string Key, bool IsRegistered, string? Error)
{ public string Shortcut => "Alt+" + Key; }

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 9001;
    private const int TempHotkeyId = 9101;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint ReconfigureMessage = 0x8001;
    private const uint TryReconfigureMessage = 0x8002;
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

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
    private readonly object _gate = new();
    private string[] _keys = ["D", "A", "S"];
    private HotkeyRegistrationStatus[] _statuses = [new("D", false, null), new("A", false, null), new("S", false, null)];
    private sealed class ReconfigureRequest
    {
        public string[] NewKeys = null!;
        public bool Success;
        public string? ErrorMessage;
    }
    private ReconfigureRequest? _pendingRequest;
    private readonly object _configurationGate=new();
    private int[] _registeredIds=[9001,9002,9003];
    private int _nextRegistrationId=10001;

    public event Action<QuickAction>? QuickActionPressed;
    public event Action? RegistrationChanged;
    public HotkeyRegistrationStatus GetStatus(QuickAction action) { lock (_gate) return _statuses[(int)action]; }

    public void Configure(string lookup, string translate, string quote)
    {
        var keys = new[] { lookup, translate, quote }.Select(k => (k ?? "").Trim().ToUpperInvariant()).ToArray();
        if (keys.Any(k => k.Length != 1 || k[0] < 'A' || k[0] > 'Z') || keys.Distinct().Count() != 3)
            throw new ArgumentException("请选择三个不同的英文字母。");
        TryConfigure(keys[0], keys[1], keys[2], out _);
    }

    public bool TryConfigure(string lookup, string translate, string quote)
        => TryConfigure(lookup, translate, quote, out _);

    public bool TryConfigure(string lookup, string translate, string quote, out string? error)
    {
        error = null;
        var keys = new[] { lookup, translate, quote }.Select(k => (k ?? "").Trim().ToUpperInvariant()).ToArray();
        if (keys.Any(k => k.Length != 1 || k[0] < 'A' || k[0] > 'Z') || keys.Distinct().Count() != 3)
        {
            error = "请选择三个不同的英文字母。";
            return false;
        }

        lock (_configurationGate)
        {
            if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            {
                _keys = keys;
                _statuses = keys.Select(k => new HotkeyRegistrationStatus(k, false, null)).ToArray();
                RegistrationChanged?.Invoke();
                return true;
            }

            lock(_gate) _pendingRequest = new ReconfigureRequest { NewKeys = keys };
            SendMessage(_hwnd, TryReconfigureMessage, IntPtr.Zero, IntPtr.Zero);
            var req = _pendingRequest;
            _pendingRequest = null;
            if (req != null)
            {
                error = req.ErrorMessage;
                if (req.Success)
                {
                    RegistrationChanged?.Invoke();
                    return true;
                }
                return false;
            }
            return false;
        }
    }

    private void RegisterConfiguredKeys()
    {
        lock (_gate)
        {
            for (var i = 0; i < 3; i++) UnregisterHotKey(_hwnd, HotkeyId + i);
            for (var i = 0; i < 3; i++)
            {
                var ok = RegisterHotKey(_hwnd, HotkeyId + i, ModAlt | ModNoRepeat, _keys[i][0]);
                _statuses[i] = new(_keys[i], ok, ok ? null : "被其他程序占用，仍可从应用内打开。");
            }
        }
    }

    private void HandleTryReconfigure()
    {
        lock (_gate)
        {
            var req = _pendingRequest;
            if (req == null) return;
            var ids = new int[3];
            var added = new List<int>();
            for (var i = 0; i < 3; i++)
            {
                var oldIndex = Array.FindIndex(_keys, k => k == req.NewKeys[i]);
                if (oldIndex >= 0 && _statuses[oldIndex].IsRegistered)
                { ids[i] = _registeredIds[oldIndex]; continue; }
                var id = _nextRegistrationId++;
                if (!RegisterHotKey(_hwnd, id, ModAlt | ModNoRepeat, req.NewKeys[i][0]))
                {
                    foreach (var held in added) UnregisterHotKey(_hwnd, held);
                    req.ErrorMessage = "快捷键 Alt+" + req.NewKeys[i] + " 被其他程序占用，保留原配置。";
                    return;
                }
                added.Add(id); ids[i] = id;
            }
            foreach (var oldId in _registeredIds.Where(id => !ids.Contains(id))) UnregisterHotKey(_hwnd, oldId);
            _registeredIds = ids;
            _keys = req.NewKeys;
            _statuses = _keys.Select(k => new HotkeyRegistrationStatus(k, true, null)).ToArray();
            req.Success = true;
        }
    }
    public bool IsRegistered => GetStatus(QuickAction.Lookup).IsRegistered;
    public event Action? HotkeyPressed;

    public void Start()
    {
        if (_thread != null) return;
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
                RegisterConfiguredKeys();
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
            foreach(var id in _registeredIds) UnregisterHotKey(hWnd, id);
            DestroyWindow(hWnd);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        if (msg == ReconfigureMessage) { RegisterConfiguredKeys(); RegistrationChanged?.Invoke(); return IntPtr.Zero; }
        if (msg == TryReconfigureMessage) { HandleTryReconfigure(); return IntPtr.Zero; }
        if (msg == WmHotkey && Array.IndexOf(_registeredIds,wParam.ToInt32()) is var actionIndex && actionIndex>=0)
        {
            var action = (QuickAction)actionIndex;
            QuickActionPressed?.Invoke(action);
            if (action == QuickAction.Lookup) HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
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
