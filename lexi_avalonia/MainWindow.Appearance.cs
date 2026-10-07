using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Animation;

namespace Lexi;

public partial class MainWindow
{
    private bool _loadingAppearance;
    private static readonly string[] AppearanceBrushes = ["PaperBrush", "CardBrush", "DialogSurfaceBrush", "InputBg", "InkBrush", "MutedBrush", "LineBrush", "PrimaryGreen", "PrimaryGreenHover", "PrimaryGreenPressed", "OnPrimaryBrush", "TintBrush", "SelectionBrush", "InsetBrush", "InsetBorderBrush", "CardBorderGradient"];

    private void BindAppearanceEvents()
    {
        foreach (var box in new[] { HighContrastBox, OpaqueMaterialBox, ReduceMotionBox })
            box.IsCheckedChanged += (_, _) =>
            {
                if (_loadingAppearance) return;
                _settings.HighContrast = HighContrastBox.IsChecked == true;
                _settings.OpaqueMaterial = OpaqueMaterialBox.IsChecked == true;
                _settings.ReduceMotion = ReduceMotionBox.IsChecked == true;
                ApplyAppearance();
                try { _vocabService.SaveSettings(_settings); }
                catch (Exception ex) { SetStatus(T("外观已预览，保存失败：") + ex.Message); }
            };
        SizeChanged += (_, _) => ApplyLayout();
        PageSettings.SizeChanged += (_, _) => SettingsContent.Width = Math.Max(100, PageSettings.Bounds.Width - 16);
    }

    private void LoadAppearance()
    {
        _loadingAppearance = true;
        HighContrastBox.IsChecked = _settings.HighContrast;
        OpaqueMaterialBox.IsChecked = _settings.OpaqueMaterial;
        ReduceMotionBox.IsChecked = _settings.ReduceMotion;
        _loadingAppearance = false;
        LoadGlassAppearance();
        ApplyAppearance(); ApplyLayout();
    }

    private void ApplyAppearance()
    {
        foreach (var key in AppearanceBrushes) Resources.Remove(key);
        var dark = _settings.Theme == "Dark";
        if (_settings.HighContrast)
        {
            void Brush(string key, string color) => Resources[key] = new SolidColorBrush(Color.Parse(color));
            var bg = dark ? "#101010" : "#FFFFFF";
            var fg = dark ? "#FFFFFF" : "#000000";
            foreach (var key in new[] { "PaperBrush", "CardBrush", "DialogSurfaceBrush", "InputBg", "InsetBrush" }) Brush(key, bg);
            foreach (var key in new[] { "InkBrush", "MutedBrush", "LineBrush", "InsetBorderBrush", "CardBorderGradient" }) Brush(key, fg);
            foreach (var key in new[] { "PrimaryGreen", "PrimaryGreenHover", "PrimaryGreenPressed" }) Brush(key, dark ? "#AAE8FF" : "#003E67");
            Brush("OnPrimaryBrush", dark ? "#000000" : "#FFFFFF");
            Brush("TintBrush", dark ? "#202020" : "#EEEEEE");
            Brush("SelectionBrush", dark ? "#303030" : "#DDDDDD");
        }
        else if (_settings.OpaqueMaterial)
        {
            Resources["PaperBrush"] = new SolidColorBrush(Color.Parse(dark ? "#131C29" : "#F3F7FC"));
            Resources["CardBrush"] = new SolidColorBrush(Color.Parse(dark ? "#202C3D" : "#FFFFFF"));
            Resources["InputBg"] = Resources["CardBrush"];
        }
        if (OperatingSystem.IsMacOS()) ApplyGlassAppearance();
        else
        {
            TransparencyLevelHint = _settings.OpaqueMaterial || _settings.HighContrast
                ? [WindowTransparencyLevel.None] : [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None];
            UpdateGlassControls();
        }
        Classes.Set("reduce-motion", _settings.ReduceMotion);
        ApplyLearningBackdrop();
        foreach (var key in new[] { "SoftCardShadow", "FloatingShadow", "FocusGlowShadow", "DrawerShadow" })
        {
            Resources.Remove(key);
            if (_settings.HighContrast || _settings.OpaqueMaterial) Resources[key] = default(BoxShadows);
        }
    }

    private void ApplyLayout()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        _cardWidthRatio = width / 1060;
        _cardHeightRatio = (Bounds.Height > 0 ? Bounds.Height : Height) / 760;
        LookupPageContent.MaxWidth = LookupActionBar.MaxWidth = 720 * _cardWidthRatio;
        SettingsContent.MaxWidth = 760 * _cardWidthRatio;
        ReviewCardHost.MaxWidth = 760 * _cardWidthRatio;
        if (_wordFocusActive)
        {
            LookupResultCard.MaxWidth = double.PositiveInfinity;
            LookupPageContent.MaxWidth = 780 * _cardWidthRatio;
            PageLookup.Margin = new Thickness(width * .26, 54, width * .26, 20);
        }
        if (_typingPlaying && _typingSession.Current != null) RenderTypingLetters();
        var compact = width < 960;
        Classes.Set("compact", compact);
        Classes.Set("medium", width >= 960 && width < 1280);
        Classes.Set("expanded", width >= 1280);
        PageReview.Margin = _reviewFullPage ? new Thickness(24, 52, 24, 20) : new Thickness(compact ? 18 : 32, 24);
        if (_reviewFullPage)
        {
            ReviewCardHost.MaxWidth = double.PositiveInfinity;
            ReviewCardHost.Margin = new Thickness(width * .26 - 24, 54, width * .26 - 24, 24);
        }
        PageSettings.Margin = new Thickness(compact ? 18 : 36, 24);
        var rendering = OperatingSystem.IsMacOS() ? "macOS Native / Skia" : Environment.GetEnvironmentVariable("LEXI_SOFTWARE_RENDERING") == "0" ? T("硬件优先") : T("软件");
        var osName = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Unix";
        var hotkeyLabel = OperatingSystem.IsMacOS() ? "Option+D" : "Alt+D";
        DiagnosticsText.Text = TF($"Lexi {AppVersion.Display} · {osName}\n布局：{(compact ? "Compact" : width < 1280 ? "Medium" : "Expanded")} · 显示缩放 {RenderScaling * 100:0}%\n渲染：{rendering} · 数据仅存本机\n{hotkeyLabel}：{(App.Hotkey?.IsRegistered == true ? T("已注册") : T("未注册或被占用"))}");
    }
}
