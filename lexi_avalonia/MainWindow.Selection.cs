namespace Lexi;

public partial class MainWindow
{
    // Keep the original entry point for existing integrations; all actions share one capture flow.
    public Task HandleSelectionHotkeyAsync(nint source) => HandleQuickActionAsync(QuickAction.Lookup, source);
}
