using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Lexi;

public partial class MainWindow
{
    private readonly Dictionary<long, CancellationTokenSource> _rowAiRequests = new();

    private static Border CreateBilingualCard(string english, string chinese) => new()
    {
        Classes = { "example-card" },
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = english, FontSize = 15, LineHeight = 24, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = chinese, FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } }
            }
        }
    };

    public async void OnRowAiGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WordItem item } || item.IsGeneratingAi || !_databaseAvailable || _restoring) return;
        var modules = new List<string>();
        if (item.OptExamples) modules.Add("examples");
        if (item.OptSynonyms) modules.Add("synonyms");
        if (item.OptAntonyms) modules.Add("antonyms");
        if (item.OptPhrases) modules.Add("phrases");
        if (modules.Count == 0) { item.AiStatusText = T("请先勾选需要生成的内容。"); return; }
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            item.AiStatusText = T("请先在「模型与偏好」配置 API Key。");
            SetStatus(item.AiStatusText); ShowPage("settings"); return;
        }
        using var cts = new CancellationTokenSource();
        _rowAiRequests[item.Id] = cts;
        item.IsGeneratingAi = true;
        item.AiStatusText = T("正在生成所选内容…");
        try
        {
            var result = await _aiService.GenerateExpansionAsync(item.Word, modules, _settings, cts.Token, item.Archive.SourceExcerpt);
            if (cts.IsCancellationRequested || _isForceClose || _restoring) return;
            result = CommitExpansion(item.Word, item.Id, result, modules, cts.Token);
            if (result == null) return;
            item.AiStatusText = result.HasContent ? T("已生成并保存，可在编辑档案中修订。") : T("没有适合的扩展内容；不编造固定搭配。");
        }
        catch (OperationCanceledException) { item.AiStatusText = T("已取消生成。"); }
        catch (Exception ex) { item.AiStatusText = T("生成失败：") + ex.Message; }
        finally
        {
            item.IsGeneratingAi = false;
            if (_rowAiRequests.TryGetValue(item.Id, out var active) && ReferenceEquals(active, cts)) _rowAiRequests.Remove(item.Id);
        }
    }

    private LlmResult? CommitExpansion(string word, long? originalId, LlmResult result, IReadOnlyList<string> modules, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_isForceClose || _restoring || !_databaseAvailable) return null;
        // This runs synchronously on the UI dispatcher immediately after generation.
        // Read the shared connection now: neither entry point's cached model is authoritative.
        var latest = _vocabService.GetAllWords().FirstOrDefault(w => originalId.HasValue
            ? w.Id == originalId.Value && w.Word.Equals(word, StringComparison.OrdinalIgnoreCase)
            : w.Word.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (originalId.HasValue && latest == null) return null;
        var matchesLookup = word.Equals(ResultWordText.Text?.Trim(), StringComparison.OrdinalIgnoreCase);
        var merged = MergeExpansion(latest?.AiResult ?? (latest == null && matchesLookup ? _currentExpansion : null), result, modules);
        cancellation.ThrowIfCancellationRequested();
        if (latest != null)
        {
            _vocabService.SaveExpansion(latest.Id, merged);
            var visible = _allWords.FirstOrDefault(w => w.Id == latest.Id && w.Word.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (visible != null) visible.AiResult = merged;
        }
        if (matchesLookup)
        {
            _currentExpansion = merged;
            RenderExpansion(merged);
        }
        return merged;
    }

    private void CancelGenerationForEdit(long wordId)
    {
        if (_rowAiRequests.TryGetValue(wordId, out var rowRequest)) rowRequest.Cancel();
        _aiCts?.Cancel();
    }

    private static LlmResult MergeExpansion(LlmResult? previous, LlmResult next, IReadOnlyList<string> modules) => new()
    {
        Examples = modules.Contains("examples") ? next.Examples : previous?.Examples ?? [],
        Synonyms = modules.Contains("synonyms") ? next.Synonyms : previous?.Synonyms ?? [],
        Antonyms = modules.Contains("antonyms") ? next.Antonyms : previous?.Antonyms ?? [],
        Phrases = modules.Contains("phrases") ? next.Phrases : previous?.Phrases ?? []
    };

    public void OnRowAiCancelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WordItem item } && _rowAiRequests.TryGetValue(item.Id, out var cts)) cts.Cancel();
    }

    public async void OnRelatedWordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string word } || string.IsNullOrWhiteSpace(word)) return;
        ShowPage("lookup");
        LookupInput.Text = word;
        await PerformLookupAsync();
    }
}
