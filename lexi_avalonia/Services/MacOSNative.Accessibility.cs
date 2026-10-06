using System;
using System.Runtime.InteropServices;

namespace Lexi;

/// <summary>Accessibility (AX) trust request and the System Settings entry point.</summary>
internal static partial class MacOSNative
{
    private static bool _accessibilityPromptRequested;

    [DllImport(ApplicationServicesLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallbacks, IntPtr valueCallbacks);

    /// <summary>
    /// Asks macOS to show the “Lexi wants to control this computer” dialog and to register Lexi in
    /// System Settings → Privacy &amp; Security → Accessibility (AXIsProcessTrustedWithOptions with
    /// kAXTrustedCheckOptionPrompt = true). At most one dialog is requested per process; the live trust
    /// state is always re-read, so the call becomes a no-op once the user grants permission.
    /// </summary>
    public static bool RequestAccessibilityPermission()
    {
        if (!OperatingSystem.IsMacOS() || AXIsProcessTrusted()) return true;
        if (_accessibilityPromptRequested) return false;
        IntPtr servicesHandle = IntPtr.Zero, coreFoundationHandle = IntPtr.Zero, options = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(ApplicationServicesLib, out servicesHandle)
                || !NativeLibrary.TryGetExport(servicesHandle, "kAXTrustedCheckOptionPrompt", out var promptKeySymbol)
                || !NativeLibrary.TryLoad(CoreFoundationLib, out coreFoundationHandle)
                || !NativeLibrary.TryGetExport(coreFoundationHandle, "kCFBooleanTrue", out var trueSymbol)) return false;
            var promptKey = Marshal.ReadIntPtr(promptKeySymbol);
            var promptValue = Marshal.ReadIntPtr(trueSymbol);
            if (promptKey == IntPtr.Zero || promptValue == IntPtr.Zero) return false;
            options = CFDictionaryCreate(IntPtr.Zero, new[] { promptKey }, new[] { promptValue }, 1, IntPtr.Zero, IntPtr.Zero);
            if (options == IntPtr.Zero) return false;
            _accessibilityPromptRequested = true;
            return AXIsProcessTrustedWithOptions(options);
        }
        catch { return false; }
        finally
        {
            if (options != IntPtr.Zero) { try { CFRelease(options); } catch { } }
            if (servicesHandle != IntPtr.Zero) NativeLibrary.Free(servicesHandle);
            if (coreFoundationHandle != IntPtr.Zero) NativeLibrary.Free(coreFoundationHandle);
        }
    }

    /// <summary>Opens System Settings on the Accessibility pane so the user can switch Lexi on.</summary>
    public static void OpenAccessibilitySettings()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
            System.Diagnostics.Process.Start(start);
        }
        catch { }
    }
}
