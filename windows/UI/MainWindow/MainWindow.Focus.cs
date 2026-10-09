using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Lexi.Controls;

namespace Lexi;

/// <summary>Focused study surface shared by lookup and archive decks.</summary>
public partial class MainWindow
{
    private Border? _focusHost;
    private StudyCanvasControl? _focusCanvas;
    private Button? _focusEntry;
    private TextBlock? _focusProgress, _focusFeedback;
    private StackPanel? _focusActions;
    private StackPanel? _focusTopActions;
    private StackPanel? _focusStreak;
    private StudyRound<string>? _focusRound;
    private string? _focusWordId;
    private bool _focusAnswerVisible;
    private bool _focusRated;
    private StudyRating _focusLastRating;
    private StudyRound<string>.Checkpoint? _focusUndo;
    private long? _focusUndoArchiveId;
    private int? _focusUndoRevision;
    private bool _focusUndoIsRating;
    private string _focusPreviousPage = "lookup";
    private bool _focusActive;
    private string? _focusRatedWord;
    private string _focusOriginalTranslation = "";

    private void InitializeFocus()
    {
        if (_focusHost != null) return;
        _focusEntry = new Button { Name = "FocusEntryButton", Content = "专注学习", Padding = new Thickness(12, 7) };
        _focusEntry.Classes.Add("secondary"); _focusEntry.Click += (_, _) => EnterFocusFromLookup();
        if (LookupResultCard.Child is Panel headerHost)
        {
            headerHost.Children.Add(_focusEntry);
        }
        else
        {
            _focusEntry.IsVisible = false;
        }

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 18, 24, 22) };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var back = FocusButton("返回", ExitFocus);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        top.Children.Add(back);
        _focusProgress = new TextBlock { Name = "FocusProgress", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(_focusProgress, 1); top.Children.Add(_focusProgress);
        _focusStreak = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_focusStreak, 2); top.Children.Add(_focusStreak);
        _focusTopActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_focusTopActions, 3); top.Children.Add(_focusTopActions);
        layout.Children.Add(top);

        var studyScroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _focusCanvas = CreateStudyCanvas();
        var canvasShell = new Border { Child = _focusCanvas, MaxWidth = 760, Margin = new Thickness(0, 24, 0, 24), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        studyScroll.Content = canvasShell; Grid.SetRow(studyScroll, 1); layout.Children.Add(studyScroll);

        var bottom = new StackPanel { Spacing = 20, HorizontalAlignment = HorizontalAlignment.Center };
        _focusFeedback = new TextBlock { Name = "FocusFeedback", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = .68 };
        _focusActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        bottom.Children.Add(_focusFeedback); bottom.Children.Add(_focusActions);
        Grid.SetRow(bottom, 2); layout.Children.Add(bottom);
        _focusHost = new Border { Name = "FocusHost", Child = layout, IsVisible = false, Focusable = true };
        Grid.SetRow(_focusHost, 1);
        _focusHost.SetValue(Panel.ZIndexProperty, 100);
        ((Grid)RootWindowBorder.Child!).Children.Add(_focusHost);
        var archiveFocus = LearningButton("选中词汇专注学习", EnterArchiveFocus);
        archiveFocus.Name = "ArchiveFocusButton";
        if (this.FindControl<Panel>("VocabFocusActionHost") is { } actionHost) actionHost.Children.Add(archiveFocus);
        VocabListBox.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control control && control.DataContext is WordItem item) EnterArchiveFocusWithFirst(item);
        };

    }

    private void EnterFocusFromLookup()
    {
        if (_focusActive || _restoring || !_databaseAvailable || !LookupResultCard.IsVisible) return;
        _focusIeltsWords.Clear();
        var text = ResultWordText.Text?.Trim(); if (string.IsNullOrWhiteSpace(text)) return;
        var archive = _allWords.FirstOrDefault(w => w.Word.Equals(text, StringComparison.OrdinalIgnoreCase));
        _focusWordId = archive?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? text;
        _focusRound = new StudyRound<string>(); _focusRound.Reset([text], archive == null ? StudyMode.FirstLearn : StudyMode.Review, shuffle: false);
        _focusPreviousPage = _currentPage; _focusActive = true; _focusAnswerVisible = false; _focusRated = false; _focusUndo = null; _focusUndoArchiveId = null; _focusUndoRevision = null;
        _focusOriginalTranslation = ResultTranslationText.Text ?? "";
        BeginFocusMemory(_focusRound.Mode, _focusRound.Total);
        PageLookup.IsVisible = false; LookupPageHost.IsVisible = false; _learningHubPage.IsVisible = false; _focusHost!.IsVisible = true;
        SetGlobalFocusChrome(true);
        RenderFocus();
        _focusHost.Focus();
    }

    private void ExitFocus()
    {
        if (!_focusActive) return;
        PersistLearningSurface("focus");
        _focusActive = false; _focusHost!.IsVisible = false; PageLookup.IsVisible = _focusPreviousPage == "lookup"; LookupPageHost.IsVisible = _focusPreviousPage == "lookup";
        SetGlobalFocusChrome(false);
        PageVocab.IsVisible = _focusPreviousPage == "vocab"; PageReview.IsVisible = _focusPreviousPage == "review"; PageSettings.IsVisible = _focusPreviousPage == "settings"; _learningHubPage.IsVisible = _focusPreviousPage == "learning";
        if (_ieltsPage != null) _ieltsPage.IsVisible = _focusPreviousPage == "ielts";
        if (_quotesPage != null) _quotesPage.IsVisible = _focusPreviousPage == "quotes";
        _focusEntry?.Focus(); SetStatus("已退出专注学习，当前词库进度已保留。");
    }

    private void RenderFocus()
    {
        if (!_focusActive || _focusRound == null) return;
        _focusActions!.Children.Clear();
        _focusTopActions!.Children.Clear();
        _focusProgress!.Text = $"专注学习 · 已完成 {_focusRound.Completed}/{_focusRound.Total} · 连击 {_focusRound.CurrentStreak}/{_focusRound.CurrentTarget}";
        _focusStreak!.Children.Clear();
        for (var index = 0; index < StudyRound<string>.RequiredStreak; index++)
        {
            var dot = new Border { Width = 16, Height = 6, CornerRadius = new CornerRadius(3) };
            dot.Bind(Border.BackgroundProperty, this.GetResourceObservable(index < _focusRound.CurrentStreak ? "PrimaryGreen" : "LineBrush"));
            _focusStreak.Children.Add(dot);
        }
        var current = _focusRated ? _focusRatedWord ?? "" : _focusRound.HasCurrent ? _focusRound.Current : "";
        var sourceWord = _focusIeltsWords.GetValueOrDefault(current);
        if (_focusRound.HasCurrent && !_focusRated) PresentFocusMemory(current, _focusRound.CurrentStep);
        if (_focusRound.HasCurrent && _focusRound.CurrentStep == StudyStep.Learn) _focusAnswerVisible = true;
        var archive = sourceWord == null ? _allWords.FirstOrDefault(w => w.Word.Equals(current, StringComparison.OrdinalIgnoreCase)) : null;
        var phonetic = archive?.Phonetic ?? (string.Equals(ResultWordText.Text, current, StringComparison.OrdinalIgnoreCase) ? ResultPhoneticText.Text ?? "" : "");
        var meaning = _focusAnswerVisible ? archive?.Translation ?? _focusOriginalTranslation : "";
        var details = archive == null
            ? string.Equals(ResultWordText.Text, current, StringComparison.OrdinalIgnoreCase) ? ResultDefinitionText.Text ?? "" : ""
            : string.Join("\n", new[] { archive.Definition }.Concat(archive.AiExamples.Select(e => e.English + "\n" + e.Chinese)));
        if (sourceWord != null)
        {
            phonetic = sourceWord.Phonetic;
            meaning = sourceWord.Meaning;
            details = string.Join("\n", new[] { sourceWord.Pos, sourceWord.Example, sourceWord.Extra }.Where(t => !string.IsNullOrWhiteSpace(t)));
        }
        _focusCanvas!.Render(new StudyCanvasModel
        {
            Word = FocusDisplayWord(current),
            Phonetic = phonetic,
            Meaning = meaning,
            Details = details,
            MeaningVisible = _focusAnswerVisible,
            Placeholder = _focusAnswerVisible ? "" : "先回忆这个词的含义"
        });
        PersistLearningSurface("focus");
        _focusFeedback!.Text = _focusRound.IsFinished ? "本轮已完成。你可以退出专注。" : _focusRound.CurrentStep == StudyStep.Learn ? "学习卡：看过释义后开始回忆。" : _focusRated ? (_focusLastRating == StudyRating.Known ? "已记为认识。" : "稍后会再次出现。") : "先自己回忆，再揭晓释义。";
        if(_focusCanvas!=null)_focusCanvas.Speak=()=>GetLearningAudio().Play(FocusDisplayWord(current),sourceWord==null?null:IeltsCatalog.ResolveAsset(sourceWord.AudioPath));
        if (_focusRound.HasCurrent && !_focusRated)
        {
            _focusTopActions.Children.Add(FocusButton("收藏", SaveFocusCurrent));
            var more = FocusButton("…", () => { });
            var menu = new MenuFlyout();
            TrackStudyFlyout(menu);
            var mastered = new MenuItem { Header = UiText.Text("标记已掌握") };
            mastered.Click += (_,_) => MasterFocus(); menu.Items.Add(mastered); more.Flyout = menu;
            if(sourceWord==null)_focusTopActions.Children.Add(more);
        }
        if (!_focusRound.HasCurrent)
        {
            if (_focusUndo != null) _focusActions.Children.Add(FocusButton("撤销", UndoFocus));
            if (_focusRated && _focusLastRating != StudyRating.Forgot) _focusActions.Children.Add(FocusButton("记错了，改为忘记", ReclassifyFocus));
            return;
        }
        if (_focusRated)
        {
            _focusActions.Children.Add(FocusButton("撤销", UndoFocus));
            if (_focusLastRating != StudyRating.Forgot) _focusActions.Children.Add(FocusButton("记错了，改为忘记", ReclassifyFocus));
            _focusActions.Children.Add(FocusButton("下一词", () => { _focusRated = false; _focusAnswerVisible = false; RenderFocus(); }, true));
            return;
        }
        if (_focusRound.CurrentStep == StudyStep.Learn)
        {
            _focusAnswerVisible = true;
            _focusActions.Children.Add(FocusButton("看完了，开始回忆", () => { _focusRound.CompleteLearn(); _focusPresentation = null; _focusAnswerVisible = false; _focusRated = false; RenderFocus(); }, true));
        }
        else if (!_focusAnswerVisible)
            _focusActions.Children.Add(FocusButton("揭晓释义", () => { _focusAnswerVisible = true; RenderFocus(); }, true));
        else if (!_focusRated)
        {
            var btnKnown = FocusButton(UiText.Text("认识")+" ("+ShortcutHint(ShortcutAction.Known)+")", () => RateFocus(StudyRating.Known));
            var btnUnsure = FocusButton(UiText.Text("模糊")+" ("+ShortcutHint(ShortcutAction.Unsure)+")", () => RateFocus(StudyRating.Unsure));
            var btnForgot = FocusButton(UiText.Text("忘记")+" ("+ShortcutHint(ShortcutAction.Forgot)+")", () => RateFocus(StudyRating.Forgot));
            ToolTip.SetTip(btnKnown, ShortcutHint(ShortcutAction.Known));
            ToolTip.SetTip(btnUnsure, ShortcutHint(ShortcutAction.Unsure));
            ToolTip.SetTip(btnForgot, ShortcutHint(ShortcutAction.Forgot));
            _focusActions.Children.Add(btnForgot);
            _focusActions.Children.Add(btnUnsure);
            _focusActions.Children.Add(btnKnown);
        }
        else
        {
            _focusActions.Children.Add(FocusButton("撤销", UndoFocus));
            if (!_focusRound.IsFinished) _focusActions.Children.Add(FocusButton("下一词", () => { _focusRated = false; _focusAnswerVisible = false; RenderFocus(); }, true));
        }
    }

    private Button FocusButton(string label, Action action, bool primary = false)
    {
        var b = new Button { Content = UiText.Text(label), Padding = new Thickness(14, 9) }; b.Classes.Add(primary ? "primary" : "secondary"); b.Click += (_, _) => action(); return b;
    }

    private void RateFocus(StudyRating rating)
    {
        if (_restoring || !_databaseAvailable || !_focusActive || _focusRound == null || !_focusRound.HasCurrent || _focusRound.CurrentStep != StudyStep.Recall || !_focusAnswerVisible || _focusRated) return;
        var checkpoint = _focusRound.CaptureCheckpoint(); var word = _focusRound.Current;
        var memoryRated = false;
        try
        {
            var before = _focusRound.CurrentStreak;
            var result = _focusRound.Commit(rating); var archive = _allWords.FirstOrDefault(w => w.Word.Equals(word, StringComparison.OrdinalIgnoreCase));
            RateFocusMemory(rating, before, result.Streak, result.Completed, _focusRound.Mode);
            memoryRated = true;
            _focusUndoIsRating = true;
            RefreshWords();
            _focusUndo = checkpoint; _focusUndoArchiveId = null; _focusUndoRevision = null; _focusLastRating = rating; _focusRated = true; _focusRatedWord = word;
            PersistLearningSurface("focus");
            SetStatus(rating == StudyRating.Known ? "已记录认识。" : rating == StudyRating.Unsure ? "已记录模糊，将再次出现。" : "已记录忘记，将重新学习。"); RenderFocus();
        }
        catch (Exception ex) { if (memoryRated) { try { UndoFocusMemory(); } catch (Exception undoError) { SetStatus("专注回滚失败：" + undoError.Message); return; } } checkpoint.Restore(); SetStatus("专注评分失败：" + ex.Message); RenderFocus(); }
    }

    private void UndoFocus()
    {
        if (_restoring || !_databaseAvailable || !_focusActive || _focusRound == null || _focusUndo == null) return;
        try
        {
            if (_focusUndoArchiveId is { } id)
            {
                var current = _vocabService.GetAllWords().FirstOrDefault(w => w.Id == id);
                if (_focusUndoRevision is { } rev && current?.Archive.Revision != rev)
                    throw new InvalidOperationException("该词已有新的变更，无法撤销。");
            }
            if (_focusUndoIsRating) UndoFocusMemory();
            if (_focusUndoArchiveId is { } undoId && !_vocabService.UndoLastLearningAction(undoId))
                throw new InvalidOperationException("该词已有新的变更，无法撤销。");
            _focusUndo.Restore(); _focusUndo = null; _focusUndoArchiveId = null; _focusUndoRevision = null; _focusRated = false; _focusAnswerVisible = true; RefreshWords(); RenderFocus(); SetStatus("已撤销上一次专注评分。");
            PersistLearningSurface("focus");
        }
        catch (Exception ex) { SetStatus("撤销失败：" + ex.Message); }
    }

    private void HandleFocusSpace()
    {
        if (_restoring || !_databaseAvailable || !_focusActive) return;
        if (_focusRound == null || !_focusRound.HasCurrent) return;
        if (_focusRated) { _focusRated = false; _focusAnswerVisible = false; RenderFocus(); return; }
        if (_focusRound.CurrentStep == StudyStep.Learn)
        {
            _focusRound.CompleteLearn(); _focusPresentation = null; _focusAnswerVisible = false; _focusRated = false; RenderFocus(); return;
        }
        if (!_focusAnswerVisible) { _focusAnswerVisible = true; RenderFocus(); return; }
        // 关键：已揭晓未评分时 Space/Enter 不隐式判定 Known，不退出
    }

    private void MasterFocus()
    {
        if (_restoring || !_databaseAvailable || !_focusActive || _focusRound?.HasCurrent != true || _focusRated) return;
        var word = _focusRound.Current; var checkpoint = _focusRound.CaptureCheckpoint();
        try
        {
            var item = _allWords.FirstOrDefault(w => w.Word.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (item == null) { SaveFocusCurrent(); item = _allWords.FirstOrDefault(w => w.Word.Equals(word, StringComparison.OrdinalIgnoreCase)); }
            if (item == null) throw new InvalidOperationException("收藏单词失败。");
            _vocabService.ExecuteBatch([item.Id], "master"); _focusRound.CompleteCurrent(); RefreshWords();
            _focusUndoIsRating = false;
            _focusUndo = checkpoint; _focusUndoArchiveId = item.Id; _focusUndoRevision = _vocabService.GetAllWords().Single(w => w.Id == item.Id).Archive.Revision; _focusLastRating = StudyRating.Known; _focusRated = true; _focusRatedWord = word; _focusAnswerVisible = true; RenderFocus();
        }
        catch (Exception ex) { checkpoint.Restore(); SetStatus("标记掌握失败：" + ex.Message); }
    }
    private void EnterArchiveFocus() => EnterArchiveFocusWithFirst(null);
    private void EnterArchiveFocusWithFirst(WordItem? first)
    {
        if (_restoring || !_databaseAvailable || _focusActive) return;
        _focusIeltsWords.Clear();
        var words = _allWords.Where(w => w.Selected).ToList(); if (words.Count == 0) words = _allWords.ToList();
        if (first != null) { words.RemoveAll(w => w.Id == first.Id); words.Insert(0, first); }
        if (words.Count == 0) { SetStatus("词汇档案为空。"); return; }
        _focusPreviousPage = _currentPage; _focusRound = new StudyRound<string>(); _focusRound.Reset(words.Select(w => w.Word), StudyMode.Review);
        _focusActive = true; _focusAnswerVisible = false; _focusRated = false; _focusUndo = null; _focusUndoArchiveId = null; _focusUndoRevision = null;
        BeginFocusMemory(_focusRound.Mode, _focusRound.Total);
        PageLookup.IsVisible = false; LookupPageHost.IsVisible = false; PageVocab.IsVisible = false; PageReview.IsVisible = false; PageSettings.IsVisible = false; _learningHubPage.IsVisible = false;
        if (_ieltsPage != null) _ieltsPage.IsVisible = false;
        if (_quotesPage != null) _quotesPage.IsVisible = false;
        _focusHost!.IsVisible = true; SetGlobalFocusChrome(true); RenderFocus(); _focusHost.Focus();
    }
    private void ReclassifyFocus()
    {
        if (!_focusRated || _focusLastRating == StudyRating.Forgot || _focusUndo == null) return;
        UndoFocus(); if (_focusUndo == null) RateFocus(StudyRating.Forgot);
    }
    private void SaveFocusCurrent()
    {
        if (_restoring || !_databaseAvailable || !_focusActive || _focusRound?.HasCurrent != true) return;
        var word = _focusRound.Current;
        if(_focusIeltsWords.TryGetValue(word,out var source)) {ArchiveSingleIeltsWord(source,null);return;}
        if (_allWords.Any(w => w.Word.Equals(word, StringComparison.OrdinalIgnoreCase))) { SetStatus("该词已收藏。"); return; }
        if (!string.Equals(ResultWordText.Text, word, StringComparison.OrdinalIgnoreCase))
        { SetStatus("当前词已不在档案中，请从查词页重新查询后收藏。"); return; }
        _vocabService.AddWord(word, ResultPhoneticText.Text ?? "", _focusOriginalTranslation, ResultDefinitionText.Text ?? ""); RefreshWords(); SetStatus("当前专注词已收藏。");
    }
}
