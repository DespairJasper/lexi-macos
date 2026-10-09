namespace Lexi;

public partial class MainWindow
{
    private bool AddShortcutContext => _currentPage == "lookup" && _databaseAvailable && !_restoring
        && !DialogEditOverlay.IsVisible && !RestoreOverlay.IsVisible
        && !DialogDeleteOverlay.IsVisible && !DialogStageOverlay.IsVisible;

    private void AddFromShortcut()
    {
        if (AddShortcutContext && LookupResultCard.IsVisible && AddWordBtn.IsEffectivelyEnabled)
            AddCurrentWordToVocab();
    }

    private void ConfigureLookupShortcut()
    {
        // 统一单 tunnel 路由接入，替代原 Alt+Space
        ConfigureStudyShortcuts();
    }
}
