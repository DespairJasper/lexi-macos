using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Lexi.Features.Ielts;

namespace Lexi;

public partial class MainWindow
{
    private IeltsCatalog? _ieltsCatalog;
    private Grid? _ieltsPage;
    private Grid? _ieltsSubContent;
    private ScrollViewer? _ieltsSubContentPage;
    private IeltsWorkspaceControl? _ieltsWorkspace;
    private Button? _ieltsNav;
    private readonly HashSet<string> _ieltsSelected = [];
    private LearningProgress _ieltsProgress = new();
    private string? _ieltsProgressPath;
    private bool _ieltsProgressBlocked;
    private IWordAudioPlayer? _learningAudio;

    private IWordAudioPlayer GetLearningAudio() => _learningAudio ??= new LocalWordAudioPlayer(SetStatus);

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
    private void SystemFeedbackSound(bool correct) => MessageBeep(correct ? 0x40u : 0x30u);

    private void InitializeIelts()
    {
        // IELTS 根页面容器（Grid，包含主工作区与子页面宿主）
        _ieltsPage = new Grid { Name = "IeltsPage", IsVisible = false };

        // 子页面容器（用于听力资料、100句写作、同义替换听写）
        _ieltsSubContent = new Grid { Margin = new Thickness(20, 18), MaxWidth = 1040 };
        _ieltsSubContentPage = new ScrollViewer
        {
            Name = "IeltsSubContentPage",
            Content = _ieltsSubContent,
            IsVisible = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        _ieltsPage.Children.Add(_ieltsSubContentPage);

        this.FindControl<Panel>("PagesHost")!.Children.Add(_ieltsPage);

        // 导航按钮
        _ieltsNav = new Button { Name = "NavIelts", Content = IeltsI18n.T("IELTS 专题") };
        _ieltsNav.Classes.Add("nav");
        _ieltsNav.HorizontalAlignment = HorizontalAlignment.Stretch;
        _ieltsNav.Click += (_, _) => ShowIeltsCatalog();
        this.FindControl<StackPanel>("LearningNavHost")!.Children.Add(_ieltsNav);

        Closed += (_, _) =>
        {
            _ieltsWorkspace?.Dispose();
            _learningAudio?.Dispose();
            StopWindowsLearningAudio();
        };

        LoadIeltsProgress();

        // 绑定语言切换事件，支持动态即时刷新
        LanguageToggleBtn.Click += (_, _) => Dispatcher.UIThread.Post(RefreshIeltsLanguage);
        SettingsLanguageCombo.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(RefreshIeltsLanguage);

        if (Environment.GetEnvironmentVariable("LEXI_IELTS_TEST") == "1")
        {
            IeltsTests.RunTests();
        }
    }

    private void LoadIeltsProgress()
    {
        _ieltsProgressPath = Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "learning-progress.json");
        try
        {
            _ieltsProgress = LearningProgress.Load(_ieltsProgressPath);
            _ieltsProgressBlocked = false;
        }
        catch (Exception ex)
        {
            _ieltsProgress = new();
            _ieltsProgressBlocked = true;
            SetStatus("学习记录无法读取，原文件已保留：" + ex.Message);
        }
    }

    private void SaveIeltsProgress()
    {
        if (_restoring || !_databaseAvailable || _ieltsProgressBlocked) return;
        var path = Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "learning-progress.json");
        if (path != _ieltsProgressPath) { LoadIeltsProgress(); return; }
        try { _ieltsProgress.Save(path); }
        catch (Exception ex) { SetStatus("学习记录保存失败：" + ex.Message); }
    }

    private void ShowIeltsCatalog()
    {
        if (!TryFlushWritingDraft()) return;
        ShowPage("ielts");

        if (_ieltsPage == null) return;

        try { _ieltsCatalog ??= IeltsCatalog.Load(); }
        catch (Exception ex)
        {
            SetStatus("IELTS 资源加载失败：" + ex.Message);
            return;
        }

        if (_ieltsWorkspace == null)
        {
            _ieltsWorkspace = new IeltsWorkspaceControl(
                catalog: _ieltsCatalog,
                progress: _ieltsProgress,
                sharedSelection: _ieltsSelected,
                playerProvider: GetLearningAudio,
                setStatus: SetStatus,
                startTyping: (words, hints) => StartLearningTyping(words, hints),
                createPlan: section => OpenPlanCreator(DailyStudyPlanSource.Ielts, section),
                startSynonyms: words => StartIeltsSynonyms(words),
                openResources: ShowIeltsResources,
                openWriting: ShowIeltsWriting,
                openSynonyms: section => ShowIeltsSynonymsBrowser(section),
                archiveBatch: ArchiveIeltsBatch,
                archiveSingle: ArchiveSingleIeltsWord,
                viewArchive: ViewWordInArchive,
                isArchived: IsWordInArchive
            );

            _ieltsPage.Children.Add(_ieltsWorkspace);
            _ieltsWorkspace.ProgressChanged += SaveIeltsProgress;
            _ieltsWorkspace.SaveExampleRequested += english => SaveExampleToQuotes(english);
        }

        // 显示主工作区，隐藏子页面
        _ieltsSubContentPage!.IsVisible = false;
        _ieltsWorkspace.IsVisible = true;
    }

    private HashSet<string>? _archivedWordCache;

    private bool IsWordInArchive(string wordText)
    {
        if (string.IsNullOrWhiteSpace(wordText)) return false;
        if (_archivedWordCache == null)
        {
            _archivedWordCache = _vocabService.GetAllWords()
                .Select(w => w.Word.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return _archivedWordCache.Contains(wordText.Trim());
    }

    private void ArchiveIeltsBatch(List<LearningWord> words)
    {
        if (words == null || words.Count == 0)
        {
            SetStatus(IeltsI18n.T("未选择要收藏的词汇。"));
            return;
        }

        int addedCount = 0;
        int existingCount = 0;
        int failedCount = 0;

        // 避免全库扫描：一次读取并构建索引
        var allExisting = _vocabService.GetAllWords();
        var existingByWord = allExisting
            .GroupBy(w => w.Word.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var sectionTitleByWordId = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_ieltsCatalog != null)
        {
            foreach (var sec in _ieltsCatalog.Sections)
            {
                foreach (var entry in sec.Entries)
                {
                    sectionTitleByWordId[entry.Id] = sec.Title;
                }
            }
        }

        foreach (var word in words)
        {
            var cleanWord = word.Word.Trim();
            if (string.IsNullOrEmpty(cleanWord)) continue;

            try
            {
                var sectionTitle = sectionTitleByWordId.GetValueOrDefault(word.Id, "IELTS 教材");
                var metadata = new Core.ArchiveMetadata
                {
                    Uuid = Guid.NewGuid().ToString(),
                    SourceType = "ielts",
                    SourceTitle = $"{sectionTitle} · Group {word.Group}",
                    SourceExcerpt = word.Example ?? "",
                    Tags = ["IELTS", sectionTitle]
                };

                if (existingByWord.TryGetValue(cleanWord, out var existingItem))
                {
                    // 已有笔记译文不覆盖，教材FSRS身份不合并，不写学习评分
                    existingCount++;
                }
                else
                {
                    _vocabService.AddWord(cleanWord, word.Phonetic ?? "", word.Meaning ?? "", word.Example ?? "");
                    var newlyAdded = _vocabService.GetAllWords().FirstOrDefault(w => w.Word.Equals(cleanWord, StringComparison.OrdinalIgnoreCase));
                    if (newlyAdded != null)
                    {
                        _vocabService.SaveArchive(newlyAdded.Id, word.Meaning ?? "", "", metadata, null);
                        existingByWord[cleanWord] = newlyAdded;
                    }
                    if (newlyAdded == null) throw new InvalidOperationException("收藏后没有找到新词。");
                    addedCount++;
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                Debug.WriteLine($"[IELTS Batch Archive Error] {word.Word}: {ex.Message}");
            }
        }

        RefreshWords();
        _archivedWordCache = null;
        _ieltsWorkspace?.RefreshArchivedStatus();

        var msg = IeltsI18n.Format($"批量收藏完成：新增 {addedCount} 词，已存在 {existingCount} 词，失败 {failedCount} 词。");
        SetStatus(msg);
    }

    private void ArchiveSingleIeltsWord(LearningWord word, LearningSection? section)
    {
        var cleanWord = word.Word.Trim();
        if (string.IsNullOrEmpty(cleanWord)) return;

        var sectionTitle = section?.Title ?? "IELTS 教材";
        var existingItem = _vocabService.GetAllWords().FirstOrDefault(w => w.Word.Equals(cleanWord, StringComparison.OrdinalIgnoreCase));

        if (existingItem != null)
        {
            // 已有笔记译文不覆盖，教材FSRS身份不合并，不写学习评分
            RefreshWords();
            _archivedWordCache = null;
            _ieltsWorkspace?.RefreshArchivedStatus();
            SetStatus(IeltsI18n.Format($"'{cleanWord}' 已在词汇档案中，未覆盖已有笔记译文。"));
            return;
        }

        var metadata = new Core.ArchiveMetadata
        {
            Uuid = Guid.NewGuid().ToString(),
            SourceType = "ielts",
            SourceTitle = $"{sectionTitle} · Group {word.Group}",
            SourceExcerpt = word.Example ?? "",
            Tags = ["IELTS", sectionTitle]
        };

        _vocabService.AddWord(cleanWord, word.Phonetic ?? "", word.Meaning ?? "", word.Example ?? "");
        var added = _vocabService.GetAllWords().FirstOrDefault(w => w.Word.Equals(cleanWord, StringComparison.OrdinalIgnoreCase));
        if (added != null)
        {
            _vocabService.SaveArchive(added.Id, word.Meaning ?? "", "", metadata, null);
        }
        RefreshWords();
        _archivedWordCache = null;
        _ieltsWorkspace?.RefreshArchivedStatus();
        SetStatus(IeltsI18n.Format($"已将 '{cleanWord}' 加入词汇档案。"));
    }

    private void ViewWordInArchive(string wordText)
    {
        ShowPage("vocab");
        var searchBox = VocabSearchInput;
        if (searchBox != null)
        {
            searchBox.Text = wordText;
        }
    }

    public void SelectIeltsSection(LearningSection section)
    {
        ShowIeltsCatalog();
        _ieltsWorkspace?.SelectSection(section);
        _ieltsProgress.SelectedSection = section.Id;
        SaveIeltsProgress();
    }

    public void RefreshIeltsLanguage()
    {
        if (_ieltsNav != null)
            _ieltsNav.Content = IeltsI18n.T("IELTS 专题");

        _archivedWordCache = null;
        _ieltsWorkspace?.RefreshLanguage();
        foreach (var browser in _ieltsSubContent?.Children.OfType<IeltsSynonymBrowser>() ?? [])
            browser.RefreshLanguage();
        foreach (var writing in _ieltsSubContent?.Children.OfType<IeltsWritingWorkspace>() ?? [])
            writing.RefreshLanguage();
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, System.Text.StringBuilder? result, int length, IntPtr callback);

    private void PlayWindowsLearningAudio(string relative)
    {
        var file = IeltsCatalog.ResolveAsset(relative);
        if (file == null) { SetStatus(IeltsI18n.T("该条目暂无录音。")); return; }
        GetLearningAudio().Play("", file);
    }

    private void StopWindowsLearningAudio() => _learningAudio?.Stop();
}
