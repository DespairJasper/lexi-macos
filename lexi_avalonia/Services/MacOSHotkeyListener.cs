using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lexi;

internal sealed class MacOSHotkeyListener : IDisposable
{
    private IntPtr _handlerRef;
    private readonly Dictionary<QuickAction, IntPtr> _hotKeys = new();
    private readonly Dictionary<QuickAction, HotkeyRegistrationStatus> _statuses = new();
    private bool _started, _disposed;
    private readonly MacOSNative.EventHandlerDelegate _handlerDelegate;
    private static readonly Dictionary<char, uint> KeyCodes = new()
    {
        ['A'] = 0, ['S'] = 1, ['D'] = 2, ['F'] = 3, ['H'] = 4, ['G'] = 5, ['Z'] = 6,
        ['X'] = 7, ['C'] = 8, ['V'] = 9, ['B'] = 11, ['Q'] = 12, ['W'] = 13, ['E'] = 14,
        ['R'] = 15, ['Y'] = 16, ['T'] = 17, ['O'] = 31, ['U'] = 32, ['I'] = 34,
        ['P'] = 35, ['L'] = 37, ['J'] = 38, ['K'] = 40, ['N'] = 45, ['M'] = 46
    };

    public bool IsRegistered => GetStatus(QuickAction.Lookup).IsRegistered;
    public event Action? HotkeyPressed;
    public event Action<QuickAction>? QuickActionPressed;
    public MacOSHotkeyListener() { _handlerDelegate = OnHotKeyCallback; Configure("D", "A", "S"); }

    public HotkeyRegistrationStatus GetStatus(QuickAction action) => _statuses[action];

    public void Configure(string lookupKey, string translateKey, string quoteKey)
    {
        if (_started) throw new InvalidOperationException("Configure before Start.");
        foreach (var (action, key) in new[] { (QuickAction.Lookup, lookupKey), (QuickAction.Translate, translateKey), (QuickAction.SaveQuote, quoteKey) })
            _statuses[action] = new((key ?? "").Trim().ToUpperInvariant(), false, null);
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        try
        {
            // Cocoa delivers global hotkeys to the application target, not the Carbon dispatcher target.
            var target = MacOSNative.GetApplicationEventTarget();
            if (target == IntPtr.Zero) { FailAll("无法取得 macOS 应用事件目标。"); return; }
            var spec = new[] { new MacOSNative.EventTypeSpec { eventClass = MacOSNative.CarbonEventClassKeyboard, eventKind = MacOSNative.CarbonEventHotKeyPressed } };
            var error = MacOSNative.InstallEventHandler(target, Marshal.GetFunctionPointerForDelegate(_handlerDelegate), 1, spec, IntPtr.Zero, out _handlerRef);
            if (error != 0) { FailAll("快捷键事件处理器注册失败（" + error + "）。"); return; }
            var used = new HashSet<string>();
            foreach (var action in Enum.GetValues<QuickAction>())
            {
                var status = _statuses[action];
                if (status.Key.Length != 1 || !KeyCodes.TryGetValue(status.Key[0], out var code))
                { _statuses[action] = status with { Error = "快捷键须为 A–Z 的一个字母。" }; continue; }
                if (!used.Add(status.Key))
                { _statuses[action] = status with { Error = "与另一个 Lexi 快捷键重复。" }; continue; }
                if (Environment.GetEnvironmentVariable("LEXI_TEST_HOTKEY_BUSY") == "1")
                { _statuses[action] = status with { Error = "快捷键被其他程序占用。" }; continue; }
                var id = new MacOSNative.EventHotKeyID { signature = MacOSNative.CarbonFourCharCodeLXHK, id = (uint)(9001 + (int)action) };
                error = MacOSNative.RegisterEventHotKey(code, MacOSNative.CarbonModOption, id, target, 0, out var reference);
                if (error == 0 && reference != IntPtr.Zero)
                { _hotKeys[action] = reference; _statuses[action] = status with { IsRegistered = true }; }
                else _statuses[action] = status with { Error = error == -9878 ? "快捷键被其他程序占用。" : "快捷键注册失败（" + error + "）。" };
                if (Environment.GetEnvironmentVariable("LEXI_HOTKEY_TRACE") == "1")
                    Console.Error.WriteLine($"[Hotkey] register {action} Option+{status.Key}; status={error}; registered={_statuses[action].IsRegistered}");
            }
        }
        catch (Exception ex) { FailAll("快捷键注册失败：" + ex.Message); }
    }

    private void FailAll(string error)
    {
        foreach (var action in Enum.GetValues<QuickAction>())
            if (!_statuses[action].IsRegistered) _statuses[action] = _statuses[action] with { Error = error };
    }

    private int OnHotKeyCallback(IntPtr call, IntPtr ev, IntPtr userData)
    {
        try
        {
            if (ev == IntPtr.Zero) return -9874;
            // System hotkey events carry the ID in kEventParamDirectObject ('----'), not the legacy 'dire' code.
            var error = MacOSNative.GetEventParameter(ev, MacOSNative.CarbonParamDirectObject, MacOSNative.CarbonTypeEventHotKeyId, out _, (uint)Marshal.SizeOf<MacOSNative.EventHotKeyID>(), out _, out var id);
            var action = (QuickAction)(id.id - 9001);
            if (error != 0 || id.signature != MacOSNative.CarbonFourCharCodeLXHK || !_hotKeys.ContainsKey(action)) return -9874;
            if (Environment.GetEnvironmentVariable("LEXI_HOTKEY_TRACE") == "1")
                Console.Error.WriteLine($"[Hotkey] received {action}; sourcePid={MacOSNative.GetFrontmostApplicationPid()}");
            QuickActionPressed?.Invoke(action);
            if (action == QuickAction.Lookup) HotkeyPressed?.Invoke();
            return 0;
        }
        catch { return 0; } // Never propagate managed exceptions across the native callback boundary.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var reference in _hotKeys.Values) { try { MacOSNative.UnregisterEventHotKey(reference); } catch { } }
        _hotKeys.Clear();
        if (_handlerRef != IntPtr.Zero) { try { MacOSNative.RemoveEventHandler(_handlerRef); } catch { } _handlerRef = IntPtr.Zero; }
        foreach (var action in Enum.GetValues<QuickAction>()) _statuses[action] = _statuses[action] with { IsRegistered = false };
    }
}
