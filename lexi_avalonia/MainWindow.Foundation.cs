using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lexi.Core;

namespace Lexi;

public partial class MainWindow
{
    private LlmResult? _currentExpansion;
    private string? _pendingRestore;
    private bool _restoring;
    private bool _databaseAvailable = true;
    public bool IsRestoring => _restoring;
    private static readonly string[] SourceTypes = ["", "book", "article", "work", "other"];
    private static readonly string[] Contexts = ["日常表达", "工作写作", "学术阅读", "技术文档"];

    private void BindFoundationEvents()
    {
        EditCurrentArchiveBtn.Click += (_, _) => { if (FindCurrentArchive() is { } item) OpenArchiveEditor(item); };
        RecordEncounterBtn.Click += (_, _) => { if (FindCurrentArchive() is { } item) RecordEncounter(item); };
        SettingsContextCombo.SelectionChanged += (_, _) => SettingsContextInput.IsVisible = SettingsContextCombo.SelectedIndex == 4;
        BackupNowBtn.Click += async (_, _) => await BackupNowAsync();
        RestoreBackupBtn.Click += async (_, _) => await ChooseRestoreAsync();
        RestoreCancelBtn.Click += (_, _) => { HideOverlay(RestoreOverlay); _pendingRestore = null; };
        RestoreConfirmBtn.Click += async (_, _) => await RestoreChosenBackupAsync();
        ImportCsvBtn.Click += async (_, _) => await ImportArchiveAsync();
        ExportCsvBtn.Click += async (_, _) => await ExportArchiveAsync(false);
        ExportJsonBtn.Click += async (_, _) => await ExportArchiveAsync(true);
        OpenDataFolderBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_vocabService.DatabasePath)!) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus(T("无法打开数据目录：") + ex.Message); }
        };
    }

    private WordItem? FindCurrentArchive() => _allWords.FirstOrDefault(w => w.Word.Equals(ResultWordText.Text?.Trim(), StringComparison.OrdinalIgnoreCase));

    private void SyncLookupActionBarVisibility() =>
        LookupActionBar.IsVisible = LookupResultCard.IsVisible && !_wordFocusActive;

    private void UpdateLookupArchiveState()
    {
        SyncLookupActionBarVisibility();
        var item = FindCurrentArchive();
        ArchiveStateText.Text = item == null ? T("未入库 · 收藏后可补充来源、原句与备注") : TF($"已入库 · 遇见 {item.Archive.EncounterCount} 次");
        AddWordBtn.IsEnabled = item == null;
        AddWordBtn.Content = item == null ? T("＋ 加入生词本 · Option+Space") : T("✓ 已加入档案");
        EditCurrentArchiveBtn.IsVisible = RecordEncounterBtn.IsVisible = item != null;
    }

    private void OpenArchiveEditor(WordItem item)
    {
        if (!_databaseAvailable || _restoring) return;
        CancelGenerationForEdit(item.Id);
        _editingWordId = item.Id;
        DialogEditTitle.Text = item.Word + T(" · 词汇档案");
        DialogEditInput.Text = item.Translation;
        ArchiveSourceTypeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SourceTypes, item.Archive.SourceType));
        ArchiveSourceTitleInput.Text = item.Archive.SourceTitle;
        ArchiveExcerptInput.Text = item.Archive.SourceExcerpt;
        ArchiveNotesInput.Text = item.Notes;
        ArchiveTagsInput.Text = string.Join(", ", item.Archive.Tags);
        ArchiveExamplesInput.Text = string.Join("\n", item.AiExamples.Select(e => e.English + " | " + e.Chinese));
        ArchiveSynonymsInput.Text = string.Join(", ", item.AiSynonyms);
        ArchiveAntonymsInput.Text = string.Join(", ", item.AiAntonyms);
        ArchivePhrasesInput.Text = string.Join("\n", item.AiPhrases.Select(e => e.English + " | " + e.Chinese));
        ArchiveEditErrorText.Text = "";
        ShowOverlay(DialogEditOverlay);
        DialogEditInput.Focus();
    }

    private static List<string> SplitTags(string? value) => (value ?? "").Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<(string English, string Chinese)> ParseBilingual(string? text)
    {
        var result = new List<(string, string)>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || pair.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(T("例句和词组请每行填写：英文 | 中文。"));
            result.Add((pair[0], pair[1]));
        }
        return result;
    }

    private void SaveArchiveEditor()
    {
        if (!_editingWordId.HasValue) return;
        try
        {
            var item = _allWords.Single(w => w.Id == _editingWordId);
            CancelGenerationForEdit(item.Id);
            var metadata = JsonSerializer.Deserialize<ArchiveMetadata>(JsonSerializer.Serialize(item.Archive))!;
            metadata.SourceType = SourceTypes[Math.Clamp(ArchiveSourceTypeCombo.SelectedIndex, 0, SourceTypes.Length - 1)];
            metadata.SourceTitle = ArchiveSourceTitleInput.Text?.Trim() ?? "";
            metadata.SourceExcerpt = ArchiveExcerptInput.Text?.Trim() ?? "";
            metadata.Tags = SplitTags(ArchiveTagsInput.Text).ToArray();
            var ai = new LlmResult
            {
                Examples = ParseBilingual(ArchiveExamplesInput.Text).Select(e => new ExampleItem(e.English, e.Chinese)).ToList(),
                Synonyms = SplitTags(ArchiveSynonymsInput.Text), Antonyms = SplitTags(ArchiveAntonymsInput.Text),
                Phrases = ParseBilingual(ArchivePhrasesInput.Text).Select(e => new PhraseItem(e.English, e.Chinese)).ToList()
            };
            _vocabService.SaveArchive(item.Id, DialogEditInput.Text?.Trim() ?? "", ArchiveNotesInput.Text?.Trim() ?? "", metadata, ai.HasContent ? ai : null);
            RefreshWords();
            // RefreshWords preserves selection/expansion. Rebind the edited visible row's metadata.
            VocabListBox.ItemsSource = null; VocabListBox.ItemsSource = _displayedWords;
            UpdateLookupArchiveState();
            if (FindCurrentArchive()?.Id == item.Id)
            {
                ResultTranslationText.Text = DialogEditInput.Text?.Trim();
                _currentExpansion = ai;
                RenderExpansion(ai);
            }
            HideOverlay(DialogEditOverlay);
            _editingWordId = null;
            SetStatus(T("词汇档案已保存。"));
        }
        catch (Exception ex) { ArchiveEditErrorText.Text = ex.Message; }
    }

    private void RecordEncounter(WordItem item)
    {
        try { _vocabService.RecordEncounter(item.Id); RefreshWords(); UpdateLookupArchiveState(); SetStatus(TF($"已记录再次遇见 {item.Word}，复习进度保持不变。")); }
        catch (Exception ex) { SetStatus(T("记录失败：") + ex.Message); }
    }

    public void OnEncounterWordClick(object? sender, RoutedEventArgs e) { if (sender is Button { DataContext: WordItem item }) RecordEncounter(item); }
    public void OnRememberWordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WordItem item }) ExecuteBatchAction([item.Id], "review");
    }
    public void OnUnfamiliarWordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WordItem item }) return;
        try { _vocabService.MarkUnfamiliar(item.Id); RefreshWords(); SetStatus(T("已安排明日重逢，不必急着记住。")); }
        catch (Exception ex) { SetStatus(T("安排失败：") + ex.Message); }
    }

    private void LoadFoundationSettings()
    {
        var index = Array.IndexOf(Contexts, _settings.AiContext);
        SettingsContextCombo.SelectedIndex = index < 0 ? 4 : index;
        SettingsContextInput.Text = index < 0 ? _settings.AiContext : "";
        SettingsIncludeSourceBox.IsChecked = _settings.IncludeSourceInAi;
        UpdateDataInfo();
        if (!string.IsNullOrEmpty(_vocabService.CredentialWarning)) SetStatus(T(_vocabService.CredentialWarning));
    }
    private string SelectedAiContext() => SettingsContextCombo.SelectedIndex is >= 0 and < 4
        ? Contexts[SettingsContextCombo.SelectedIndex] : SettingsContextInput.Text?.Trim() ?? "";

    private void UpdateDataInfo()
    {
        DataLocationText.Text = _vocabService.DatabasePath;
        DataIdentityText.Text = TF($"当前词库 · {_allWords.Count} 个词 · 最近收藏 {(_allWords.Count > 0 ? _allWords.Max(w => w.CreatedAt) : T("暂无"))}");
        var folder = Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "backups");
        var path = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "lexi-*.sqlite3").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault() : null;
        LastBackupText.Text = path != null && File.Exists(path) ? T("最近备份：") + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss") : T("暂无备份，收藏后自动备份。");
    }

    private async Task BackupNowAsync()
    {
        BackupNowBtn.IsEnabled = false;
        try
        {
            var databasePath = _vocabService.DatabasePath;
            var backup = await Task.Run(() => _backups.Create(databasePath));
            LastBackupText.Text = T("最近备份：") + File.GetLastWriteTime(backup).ToString("yyyy-MM-dd HH:mm:ss");
            SetStatus(T("备份已保存：") + backup);
        }
        catch (Exception ex) { SetStatus(T("备份未完成：") + ex.Message); }
        finally { BackupNowBtn.IsEnabled = true; }
    }

    private async Task ImportArchiveAsync()
    {
        try {
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title=T("导入词汇档案"),AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("Lexi CSV / JSON") {Patterns=["*.csv","*.json"]}] });
            if(files.Count==0)return;
            await using var input=await files[0].OpenReadAsync();
            using var buffer=new MemoryStream();var chunk=new byte[8192];int count;
            while((count=await input.ReadAsync(chunk))>0){if(buffer.Length+count>ArchiveTransferService.MaximumJsonBytes)throw new FormatException(T("档案超过 50 MB。"));buffer.Write(chunk,0,count);}
            var text=new UTF8Encoding(false,true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
            var csv=files[0].Name.EndsWith(".csv",StringComparison.OrdinalIgnoreCase);
            var entries=await Task.Run(()=>csv?ArchiveTransferService.ParseCsv(text):ArchiveTransferService.ParseJson(text));
            var report=_vocabService.ImportArchive(entries);
            RefreshWords();UpdateDataInfo();UpdateLookupArchiveState();
            SetStatus(TF($"已导入 {report.Added} 个词，跳过 {report.Skipped} 个重复词；已有词条未覆盖。导入前已备份。"));
        } catch(Exception ex){SetStatus(T("导入未完成：")+ex.Message);}
    }

    private async Task ExportArchiveAsync(bool json)
    {
        try
        {
            var selected = _filteredWords.Where(w => w.Selected).ToList();
            var words = selected.Count > 0 ? selected : _filteredWords;
            if (words.Count == 0) { SetStatus(T("当前筛选没有可以导出的词。")); return; }
            var content = json ? ArchiveTransferService.GenerateJson(words) : ArchiveTransferService.GenerateCsv(words);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = json ? T("导出完整词汇档案") : T("导出 CSV"), SuggestedFileName = $"Lexi-{DateTime.Now:yyyyMMdd-HHmmss}.{(json ? "json" : "csv")}",
                DefaultExtension = json ? "json" : "csv", ShowOverwritePrompt = true
            });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(!json));
            await writer.WriteAsync(content);
            SetStatus(TF($"已导出 {words.Count} 个词的{(json ? T("完整档案") : "CSV")}，不含 API Key。"));
        }
        catch (Exception ex) { SetStatus(T("导出失败：") + ex.Message); }
    }

    private async Task ChooseRestoreAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = T("选择 Lexi SQLite 备份"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(T("Lexi 词库备份")) { Patterns = ["*.sqlite3"] }] });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path == null) throw new IOException(T("请选择本机文件。"));
            // Open a read-only connection; never migrate or modify an unconfirmed backup.
            var count = await Task.Run(() => _backups.Inspect(path));
            _pendingRestore = path;
            RestoreDescriptionText.Text = TF($"{Path.GetFileName(path)}\n包含 {count} 个词。");
            ShowOverlay(RestoreOverlay);
        }
        catch (Exception ex) { SetStatus(T("不能恢复该文件：") + ex.Message); }
    }

    private async Task RestoreChosenBackupAsync()
    {
        if (_pendingRestore == null || _restoring) return;
        _quickCard?.Close();
        ++_captureEpoch;
        _restoring = true;
        _filterTimer.Stop();
        IsEnabled = false;
        RestoreConfirmBtn.IsEnabled = RestoreCancelBtn.IsEnabled = false;
        var path = _vocabService.DatabasePath;
        var selection = _pendingRestore;
        string? staged = null;
        var serviceDisposed = false;
        var replacementInstalled = false;
        try
        {
            ++_lookupVersion; _aiCts?.Cancel();
            foreach (var cts in _rowAiRequests.Values) cts.Cancel();
            // Freeze the selected file before CreateManualBackup rotates the oldest snapshot.
            staged = await Task.Run(() => _backups.Stage(selection, Path.GetDirectoryName(path)!));
            _vocabService.CreateManualBackup();
            _vocabService.Dispose();
            serviceDisposed = true;
            await Task.Run(() => _backups.Restore(staged, path));
            replacementInstalled = true;
            _vocabService = ServiceFactory.OpenArchive(path);
            serviceDisposed = false;
            _settings = _vocabService.LoadSettings();
            _allWords.Clear();
            _reviewHandled.Clear();
            RefreshWords(); LoadSettingsToUi(); UpdateLookupArchiveState();
            if (_currentPage == "review") OpenReviewDeck();
            _currentExpansion = null;
            LookupResultCard.IsVisible = LookupNotFoundCard.IsVisible = false;
            LookupEmptyCard.IsVisible = true;
            ResultWordText.Text = "";
            await CloseDrawerAsync(immediate: true);
            SetStatus(T("恢复完成。恢复前词库已另存于数据目录的 before-restore 文件夹。"));
        }
        catch (Exception ex)
        {
            if (!serviceDisposed && !replacementInstalled)
            {
                SetStatus(T("恢复尚未替换词库，当前词库继续使用：") + ex.Message);
            }
            else
            {
                // A constructor may have succeeded before a later load/refresh failed.
                if (!serviceDisposed) { _vocabService.Dispose(); serviceDisposed = true; }
                try
                {
                    _vocabService = ServiceFactory.OpenArchive(path); serviceDisposed = false;
                    _settings = _vocabService.LoadSettings();
                    _allWords.Clear(); RefreshWords(); LoadSettingsToUi(); UpdateLookupArchiveState();
                    SetStatus(replacementInstalled
                        ? T("备份已替换词库，但界面刷新未完整完成；当前已重新打开恢复后的词库。原库保存在 before-restore 文件夹。") + ex.Message
                        : T("恢复未完成，当前词库已重新打开。") + ex.Message);
                }
                catch
                {
                    if (!serviceDisposed) { _vocabService.Dispose(); serviceDisposed = true; }
                    _databaseAvailable = false;
                    PageLookup.IsEnabled = PageVocab.IsEnabled = PageSettings.IsEnabled = PageReview.IsEnabled = false;
                    NavLookup.IsEnabled = NavVocab.IsEnabled = NavReview.IsEnabled = NavSettings.IsEnabled = false;
                    ThemeToggleBtn.IsEnabled = false;
                    SetStatus(replacementInstalled
                        ? T("备份已替换，但词库不能重新打开。数据操作已停用，请退出并检查数据目录；原库在 before-restore 文件夹。")
                        : T("词库不能重新打开，数据操作已停用。请退出并检查数据目录与恢复记录，所有备份均已保留。"));
                }
            }
        }
        finally
        {
            _restoring = false; _pendingRestore = null;
            IsEnabled = true;
            HideOverlay(RestoreOverlay);
            RestoreConfirmBtn.IsEnabled = RestoreCancelBtn.IsEnabled = true;
            if (staged != null) _backups.ReleaseStage(staged);
            if (_databaseAvailable) UpdateDataInfo();
        }
    }
}
