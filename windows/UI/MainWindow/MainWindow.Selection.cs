using Avalonia.Controls;

namespace Lexi;

public partial class MainWindow
{
    private bool _selectionCaptureBusy;

    public async Task HandleSelectionHotkeyAsync(nint source)
    {
        if (_isForceClose || _selectionCaptureBusy) return;
        if (IsActive && IsVisible && WindowState != WindowState.Minimized)
        {
            HideToTray();
            return;
        }
        _selectionCaptureBusy = true;
        string? selected = null;
        try
        {
            if (OperatingSystem.IsWindows())
                selected = await new SelectionCaptureService(new Win32SelectionClipboard()).CaptureAsync(source);
        }
        catch { /* Capture is best-effort; always retain the manual search path. */ }
        finally { _selectionCaptureBusy = false; }
        if (_isForceClose) return;
        // If the user switched apps during capture, do not steal the new focus.
        if (OperatingSystem.IsWindows() && Win32SelectionClipboard.CurrentForeground != source) return;
        ShowAndActivate();
        // Briefly raise to the front, then release topmost so other apps can cover us.
        Topmost = true;
        Activate();
        LookupInput.Focus();
        try
        {
            if (selected != null)
            {
                LookupInput.Text = selected;
                await PerformLookupAsync();
            }
            else SetStatus("未抓取到英文选词，请输入单词查询。支持在其他应用选中单词后按 Alt+D。");
            await Task.Delay(120);
        }
        finally { Topmost = false; }
    }
}
