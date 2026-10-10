using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;

namespace Lexi;

public partial class MainWindow
{
    private bool _loadingLanguage;
    private static string T(string chinese) => UiText.Text(chinese);
    private static string TF(FormattableString chinese) => UiText.Format(chinese);

    private void BindLanguageEvents()
    {
        // Avalonia 11.3 caches a selected ComboBoxItem's Content. Bind the displayed
        // caption to Content directly so changing resources never changes the selection.
        foreach (var combo in new[] { VocabStatusFilter, BatchActionCombo, SettingsThemeCombo,
                     SettingsProviderCombo, SettingsContextCombo, SettingsTimeoutCombo,
                     ArchiveSourceTypeCombo, DialogStageCombo })
            combo.SelectionBoxItemTemplate = new FuncDataTemplate<object>((_, _) =>
            {
                var label = new TextBlock();
                label.Bind(TextBlock.TextProperty, new Binding("SelectedItem.Content") { Source = combo });
                return label;
            });
        LanguageToggleBtn.Click += (_, _) => ToggleUiLanguage();
        SettingsLanguageCombo.SelectionChanged += (_, _) =>
        {
            if (!_loadingLanguage) SetUiLanguage(SettingsLanguageCombo.SelectedIndex == 1 ? "en" : "zh-CN");
        };
    }
    public void ToggleUiLanguage() => SetUiLanguage(_settings.UiLanguage == "en" ? "zh-CN" : "en");
    public void SetUiLanguage(string language)
    {
        var normalized = UiText.Normalize(language);
        if (_settings.UiLanguage == normalized) return;
        _settings.UiLanguage = normalized;
        LoadLanguage();
        try { _vocabService.SaveSettings(_settings); SetStatus(T("界面语言已切换并保存。")); }
        catch (Exception ex) { SetStatus(T("语言已切换，但未能保存：") + ex.Message); }
    }
    private void LoadLanguage()
    {
        _loadingLanguage = true;
        UiText.Apply(_settings.UiLanguage);
        SettingsLanguageCombo.SelectedIndex = _settings.UiLanguage == "en" ? 1 : 0;
        LanguageToggleBtn.Content = _settings.UiLanguage == "en" ? "中文" : "EN";
        ThemeToggleBtn.Content = T(_settings.Theme == "Dark" ? "切换浅色" : "切换深色");
        _loadingLanguage = false;
        // Explicit UI-owned labels only. Never inspect or replace all visual-tree text.
        foreach (var text in new[] { GlobalStatusText, UpdateCheckText, ReviewHintText, DialogEditTitle, DialogDeleteTitle, RestoreDescriptionText, ArchiveEditErrorText })
            text.Text = UiText.Redisplay(text.Text);
        if (_editingWordId is { } id && _allWords.FirstOrDefault(w => w.Id == id) is { } entry)
            DialogEditTitle.Text = entry.Word + T(" · 词汇档案");
        VocabPageTitle.Text = T(_isReviewMode ? "今日重逢" : "词汇档案");
        VocabPageSubtitle.Text = "";
        UpdateReviewProgressText();
        ApplyReviewActionLabels();
        AiDrawerToggleBtn.Content = T(AiDrawerSlot.IsVisible ? "收起扩展 ↑" : "展开扩展 ↓");
        AiGenerateBtn.Content = T(AiCancelBtn.IsVisible ? "生成中…" : "✦ 生成");
        LookupBtn.Content = T(LookupBtn.IsEnabled ? "查询 ↵" : "查询中…");
        if (ResultPhoneticText.Text is "暂无音标" or "No pronunciation available") ResultPhoneticText.Text = T("暂无音标");
        foreach (var panel in new[] { AiResultSynonymsPanel, AiResultAntonymsPanel })
            foreach (var pill in panel.Children.OfType<Button>())
                ToolTip.SetTip(pill, TF($"点击离线查询 '{pill.Content}'"));
        ApplyVocabFilters();
        // Rebind only the list so UI value converters update without changing word state.
        VocabListBox.ItemsSource = null;
        VocabListBox.ItemsSource = _displayedWords;
        UpdateLookupArchiveState();
        UpdateDataInfo();
        ApplyLayout();
        RefreshWordFocusLabels();
        RefreshLearningLabels();
        if (_planFeedback != null) _planFeedback.Text = UiText.Redisplay(_planFeedback.Text);
        RefreshStudyPlanLanguage();
        RefreshStatisticsLanguage();
        RefreshQuotesLanguage();
    }
}
