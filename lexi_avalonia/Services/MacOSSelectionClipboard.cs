using System;
using System.Collections.Generic;

namespace Lexi;

internal sealed class MacOSSelectionClipboard : ISelectionClipboard
{
    private readonly IntPtr _pasteboard;
    private List<MacOSNative.PasteboardItemSnapshot>? _snapshot;
    private bool _disposed;

    public MacOSSelectionClipboard()
    {
        _pasteboard = MacOSNative.GetGeneralPasteboard();
    }

    public nint Foreground => MacOSNative.GetFrontmostApplicationPid();

    public static nint CurrentForeground => MacOSNative.GetFrontmostApplicationPid();

    public static bool HasAccessibilityPermission => MacOSNative.AXIsProcessTrusted();

    public uint Sequence => MacOSNative.GetPasteboardChangeCount(_pasteboard);

    public bool ModifiersReleased
    {
        get
        {
            var flags = MacOSNative.CGEventSourceFlagsState(1);
            var activeModifiers = flags & (
                MacOSNative.kCGEventFlagMaskAlternate |
                MacOSNative.kCGEventFlagMaskCommand |
                MacOSNative.kCGEventFlagMaskControl |
                MacOSNative.kCGEventFlagMaskShift);
            return activeModifiers == 0;
        }
    }

    public string? ReadSelectedText(nint source) => MacOSNative.ReadFocusedSelectedText(source);

    public bool TrySnapshot()
    {
        if (_pasteboard == IntPtr.Zero) return false;
        var snapshot = MacOSNative.SnapshotPasteboard(_pasteboard, 16 * 1024 * 1024);
        if (snapshot == null) return false;
        _snapshot = snapshot;
        return true;
    }

    public bool SendCopy()
    {
        if (!MacOSNative.AXIsProcessTrusted())
        {
            // Accessibility permission is required to post synthetic keyboard events on macOS.
            return false;
        }

        IntPtr eventDown = IntPtr.Zero, eventUp = IntPtr.Zero;
        var downPosted = false;
        try
        {
            // Allocate both events before publishing either half of the key press.
            eventDown = MacOSNative.CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)MacOSNative.CarbonKeyC, true);
            eventUp = MacOSNative.CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)MacOSNative.CarbonKeyC, false);
            if (eventDown == IntPtr.Zero || eventUp == IntPtr.Zero) return false;
            MacOSNative.CGEventSetFlags(eventDown, MacOSNative.kCGEventFlagMaskCommand);
            MacOSNative.CGEventSetFlags(eventUp, MacOSNative.kCGEventFlagMaskCommand);
            MacOSNative.CGEventPost(MacOSNative.kCGHIDEventTap, eventDown);
            downPosted = true;
            MacOSNative.CGEventPost(MacOSNative.kCGHIDEventTap, eventUp);
            downPosted = false;
            return true;
        }
        catch { return false; }
        finally
        {
            if (downPosted && eventUp != IntPtr.Zero)
            {
                try { MacOSNative.CGEventPost(MacOSNative.kCGHIDEventTap, eventUp); } catch { }
            }
            if (eventDown != IntPtr.Zero) MacOSNative.CFRelease(eventDown);
            if (eventUp != IntPtr.Zero) MacOSNative.CFRelease(eventUp);
        }
    }

    public (string? Text, uint Sequence) ReadText(nint source)
    {
        if (_pasteboard == IntPtr.Zero) return (null, 0);
        if (source != 0 && Foreground != source) return (null, 0);

        var sequence = Sequence;
        var text = MacOSNative.ReadPasteboardString(_pasteboard);
        if (Sequence != sequence || (source != 0 && Foreground != source)) return (null, 0);
        if (string.IsNullOrEmpty(text) || text.Length > 4096)
        {
            return (null, sequence);
        }

        return (text, sequence);
    }

    public bool Restore(uint expectedSequence, nint source = 0)
    {
        if (_pasteboard == IntPtr.Zero || _snapshot == null) return false;
        // Never overwrite if user updated clipboard after our synthetic copy or switched apps
        if (Sequence != expectedSequence) return false;
        if (source != 0 && Foreground != source) return false;

        return MacOSNative.RestorePasteboard(_pasteboard, _snapshot, expectedSequence, source);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _snapshot = null;
    }
}
