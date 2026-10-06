using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lexi;

public partial class MainWindow
{
    private bool AddShortcutContext => _currentPage == "lookup" && !_wordFocusActive && _databaseAvailable && !_restoring
        && !DialogEditOverlay.IsVisible && !RestoreOverlay.IsVisible
        && !DialogDeleteOverlay.IsVisible && !DialogStageOverlay.IsVisible;

    private void AddFromShortcut()
    {
        if (AddShortcutContext && LookupResultCard.IsVisible && AddWordBtn.IsEffectivelyEnabled)
            AddCurrentWordToVocab();
    }

    private void ConfigureLookupShortcut()
    {
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.Alt || !AddShortcutContext) return;
            e.Handled = true;
            AddFromShortcut();
        }, RoutingStrategies.Tunnel);
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(this, LookupShortcutWndProc);
    }

    // Intercept the window-local system key before DefWindowProc opens its Alt+Space menu.
    // No global registration: other applications keep their own Alt+Space behavior.
    private IntPtr LookupShortcutWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!AddShortcutContext || wParam.ToInt64() != 0x20) return IntPtr.Zero;
        if (message is not (0x0104 or 0x0106)) return IntPtr.Zero; // WM_SYSKEYDOWN / WM_SYSCHAR
        if ((lParam.ToInt64() & (1L << 29)) == 0 || (GetKeyState(0x11) & 0x8000) != 0 || (GetKeyState(0x10) & 0x8000) != 0)
            return IntPtr.Zero;
        handled = true;
        if (message == 0x0104 && (lParam.ToInt64() & (1L << 30)) == 0) AddFromShortcut();
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
