using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Lexi;

public partial class MainWindow
{
    private ComboBox? _materialCombo;
    private TextBlock? _materialLabel;
    private Slider? _glassStrengthSlider;
    private TextBlock? _glassStrengthText;
    private TextBlock? _glassStateText;
    private DispatcherTimer? _glassSaveTimer;
    private bool _loadingGlass;
    private bool _glassSettingsDirty;

    private void ConfigureGlassAppearance()
    {
        if (_materialCombo != null) return;
        var card = this.FindControl<Border>("ThemeSettingsCard");
        if (card?.Child is not StackPanel panel) return;
        _materialCombo = new ComboBox
        {
            Name = "SettingsMaterialCombo",
            ItemsSource = new[] { new ComboBoxItem { Content = "毛玻璃" }, new ComboBoxItem { Content = "液态玻璃" } }
        };
        _materialCombo.SelectionBoxItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((_, _) =>
        {
            var label = new TextBlock();
            label.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("SelectedItem.Content") { Source = _materialCombo });
            return label;
        });
        _materialLabel = new TextBlock { FontSize = 12 };
        _glassStrengthSlider = new Slider
        {
            Name = "GlassIntensitySlider", Minimum = 0, Maximum = 100,
            TickFrequency = 1, IsSnapToTickEnabled = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        _glassStrengthText = new TextBlock { Name = "GlassIntensityText", FontSize = 12 };
        _glassStateText = new TextBlock { Name = "GlassMaterialStatusText", FontSize = 11, TextWrapping = TextWrapping.Wrap };
        _glassStateText.Classes.Add("muted");
        // Put material controls beside the existing theme and language controls.
        var insertAt = Math.Max(0, panel.Children.IndexOf(HighContrastBox));
        panel.Children.Insert(insertAt++, _materialLabel);
        panel.Children.Insert(insertAt++, _materialCombo);
        panel.Children.Insert(insertAt++, _glassStrengthText);
        panel.Children.Insert(insertAt++, _glassStrengthSlider);
        panel.Children.Insert(insertAt, _glassStateText);
        _glassSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _glassSaveTimer.Tick += (_, _) => { _glassSaveTimer.Stop(); SaveGlassSettings(); };
        _materialCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingGlass) return;
            _settings.Material = _materialCombo.SelectedIndex == 1 ? "LiquidGlass" : "Frosted";
            PreviewAndSaveGlass();
        };
        _glassStrengthSlider.PropertyChanged += (_, e) =>
        {
            if (_loadingGlass || e.Property != Slider.ValueProperty) return;
            _settings.GlassIntensity = MacGlassMaterial.NormalizeIntensity(_glassStrengthSlider.Value / 100);
            PreviewAndSaveGlass();
        };
        SettingsLanguageCombo.SelectionChanged += (_, _) => UpdateGlassControls();
        Opened += (_, _) => ApplyGlassAppearance();
        Closed += (_, _) => _glassSaveTimer?.Stop();
        LoadGlassAppearance();
    }

    private void LoadGlassAppearance()
    {
        if (_materialCombo == null || _glassStrengthSlider == null) return;
        _loadingGlass = true;
        _materialCombo.SelectedIndex = _settings.Material == "LiquidGlass" ? 1 : 0;
        _glassStrengthSlider.Value = MacGlassMaterial.NormalizeIntensity(_settings.GlassIntensity) * 100;
        _loadingGlass = false;
        UpdateGlassControls();
    }

    private void PreviewAndSaveGlass()
    {
        _glassSettingsDirty = true;
        ApplyAppearance();
        _glassSaveTimer?.Stop();
        _glassSaveTimer?.Start();
    }

    private void SaveGlassSettings()
    {
        if (!_glassSettingsDirty) return;
        try { _vocabService.SaveSettings(_settings); _glassSettingsDirty = false; }
        catch (Exception ex) { SetStatus(T("外观已预览，保存失败：") + ex.Message); }
    }

    private void ApplyGlassAppearance()
    {
        MacGlassMaterial.Apply(this, _settings);
        UpdateGlassControls();
        if (!MacGlassMaterial.WantsLiquidGlass(_settings)) return;
        // Only backgrounds change opacity. Foreground brushes stay crisp.
        Resources["PaperBrush"] = new SolidColorBrush(MacGlassMaterial.SurfaceColor(_settings));
        Resources["CardBrush"] = new SolidColorBrush(MacGlassMaterial.SurfaceColor(_settings, card: true));
    }

    private void UpdateGlassControls()
    {
        if (_glassStrengthSlider == null || _glassStrengthText == null || _glassStateText == null) return;
        var english = _settings.UiLanguage == "en";
        string Label(string zh, string en) => english ? en : zh;
        if (_materialLabel != null) _materialLabel.Text = Label("窗口材质", "Window material");
        if (_materialCombo?.ItemsSource is ComboBoxItem[] items)
        {
            items[0].Content = Label("毛玻璃", "Frosted");
            items[1].Content = Label("液态玻璃", "Liquid Glass");
        }
        var preferred = _settings.Material == "LiquidGlass";
        _glassStrengthSlider.IsEnabled = preferred && !_settings.HighContrast && !_settings.OpaqueMaterial;
        _glassStrengthText.Text = $"{Label("玻璃强度", "Glass intensity")} · {MacGlassMaterial.NormalizeIntensity(_settings.GlassIntensity) * 100:0}%";
        var status = MacGlassMaterial.Status(this);
        _glassStateText.Text = status switch
        {
            "LiquidGlass" => Label("当前：原生液态玻璃", "Active: native Liquid Glass"),
            "Opaque" => Label("当前：不透明（高对比度或不透明材质优先）", "Active: opaque (high contrast or opaque appearance)"),
            "Pending" => Label("窗口显示后启用材质", "Material activates when the window opens"),
            "Frosted" => OperatingSystem.IsMacOS() ? Label("当前：原生毛玻璃", "Active: native frosted glass") : Label("当前：系统透明材质", "Active: system translucent material"),
            _ => Label("当前：毛玻璃回退（系统不支持原生液态玻璃或无法附着）", "Active: frosted fallback (native Liquid Glass unavailable)")
        };
    }
}
