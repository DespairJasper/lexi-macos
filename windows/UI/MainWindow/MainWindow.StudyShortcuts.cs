using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lexi;

public partial class MainWindow
{
    private StudyShortcutRouter? _studyShortcutRouter;
    private IShortcutConfigManager? _shortcutConfigManager;

    public IShortcutConfigManager ShortcutConfigManager => _shortcutConfigManager!;
    public StudyShortcutRouter ShortcutRouter => _studyShortcutRouter!;

    public void ReloadShortcutConfiguration()
    {
        _shortcutConfigManager?.Load();
    }

    public ShortcutValidationResult SaveShortcutConfiguration(ShortcutConfiguration config)
    {
        if (_shortcutConfigManager == null)
            return ShortcutValidationResult.Failure("快捷键管理器尚未初始化。");

        var val = _shortcutConfigManager.Validate(config);
        if (!val.IsValid) return val;

        _shortcutConfigManager.Store.Save(config);
        _shortcutConfigManager.Load();
        return ShortcutValidationResult.Success;
    }

    private void ConfigureStudyShortcuts()
    {
        if (_studyShortcutRouter != null) return;

        var dataDir = Path.GetDirectoryName(_vocabService?.DatabasePath)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lexi");

        var store = ShortcutConfigStore.ForDataDirectory(dataDir);
        try { _shortcutConfigManager = new ShortcutConfigManager(store); }
        catch (Exception ex)
        {
            _shortcutConfigManager = new ShortcutConfigManager(ShortcutConfigStore.ForDataDirectory(dataDir,"study-shortcuts.recovery.json"));
            SetStatus(UiText.Bilingual("学习快捷键配置无法读取，原文件已保留：","Study shortcuts could not be loaded. The original file was preserved: ")+ex.Message);
        }
        _studyShortcutRouter = new StudyShortcutRouter(_shortcutConfigManager.CurrentConfig);

        _shortcutConfigManager.ConfigChanged += cfg =>
        {
            if (_studyShortcutRouter != null)
                _studyShortcutRouter.Engine.Configuration = cfg;
            RefreshStudyShortcutHints();
        };

        AddHandler(KeyDownEvent, HandleUnifiedTunnelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, HandleUnifiedTunnelKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _studyShortcutRouter.ResetKeyState();
    }

    private void HandleUnifiedTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        _studyShortcutRouter?.OnKeyUp(e);
    }

    private async void HandleUnifiedTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_studyShortcutRouter == null) return;

        var focused = FocusManager?.GetFocusedElement();
        if (focused is Control control && !control.IsEffectivelyVisible) focused = null;
        if (focused is Avalonia.Controls.Primitives.PopupRoot || focused is MenuItem || _openStudyFlyouts.Count>0) return;
        var isSpellingActive = _typingHost?.IsVisible == true;

        var hasTopmostOverlay = _visualConfirmation!=null || _quoteEditorHost!=null || _settingsDrawerOpen || _planCreatorOverlay != null
            || _planEditDrawer?.IsVisible == true
            || (_workspaceChooser != null && _workspaceChooser.IsVisible)
            || DialogDeleteOverlay.IsVisible
            || DialogEditOverlay.IsVisible
            || DialogStageOverlay.IsVisible
            || RestoreOverlay.IsVisible
            || isSpellingActive;

        var isGlobalFocusActive = _globalFocusActive || _focusActive;
        var isStudySessionActive = _focusActive
            || _currentPage == "review"
            || (_currentPage == "learning" && _studyWorkspaceHost.IsVisible);

        var studyPhase = DetermineCurrentStudyPhase();
        var canUndo = DetermineCanUndo();
        var hasCurrentWord = DetermineHasCurrentWord();
        var isRestoringOrUnavailable = _restoring || !_databaseAvailable;

        var result = _studyShortcutRouter.RouteKeyDown(
            e,
            focused,
            isSpellingActive,
            hasTopmostOverlay,
            isGlobalFocusActive,
            isStudySessionActive,
            studyPhase,
            canUndo,
            hasCurrentWord,
            isRestoringOrUnavailable
        );

        switch (result)
        {
            case StudyShortcutDispatchResult.Ignored:
                // 穿透给控件自身处理
                return;

            case StudyShortcutDispatchResult.ProhibitedKeyIgnored:
            case StudyShortcutDispatchResult.Consumed:
                // 明确丢弃已禁用的裸键（Q/W/E/A/S/C/Delete/Alt+Space），防止意外触发
                e.Handled = true;
                return;

            case StudyShortcutDispatchResult.SpellingSubmit:
                // 拼写练习文本框中的 Enter 自身提交，不设置 Handled 让文本框的 KeyDown 接收
                return;

            case StudyShortcutDispatchResult.CloseTopmostOverlay:
                e.Handled = true;
                ExecuteCloseTopmostOverlay();
                return;

            case StudyShortcutDispatchResult.ExitGlobalFocus:
                e.Handled = true;
                if (_focusActive) ExitFocus();
                SetGlobalFocusChrome(false);
                return;

            case StudyShortcutDispatchResult.ExitStudySession:
                e.Handled = true;
                if (_focusActive) ExitFocus();
                else if (_currentPage == "learning" && _studyWorkspaceHost.IsVisible) ExitDailyLearning();
                return;

            case StudyShortcutDispatchResult.ToggleGlobalFocus:
                e.Handled = true;
                ToggleGlobalFocus();
                return;

            case StudyShortcutDispatchResult.Undo:
                e.Handled = true;
                ExecuteUndoCurrent();
                return;

            case StudyShortcutDispatchResult.Favorite:
                e.Handled = true;
                ExecuteFavoriteCurrent();
                return;

            case StudyShortcutDispatchResult.Speak:
                e.Handled = true;
                ExecuteSpeakCurrent();
                return;

            case StudyShortcutDispatchResult.AdvanceLearn:
                e.Handled = true;
                ExecuteAdvanceLearn();
                return;

            case StudyShortcutDispatchResult.RevealHidden:
                e.Handled = true;
                ExecuteRevealHidden();
                return;

            case StudyShortcutDispatchResult.NextWord:
                e.Handled = true;
                ExecuteNextWord();
                return;

            case StudyShortcutDispatchResult.RateForgot:
                e.Handled = true;
                await ExecuteRatingAsync(StudyRating.Forgot);
                return;

            case StudyShortcutDispatchResult.RateUnsure:
                e.Handled = true;
                await ExecuteRatingAsync(StudyRating.Unsure);
                return;

            case StudyShortcutDispatchResult.RateKnown:
                e.Handled = true;
                await ExecuteRatingAsync(StudyRating.Known);
                return;
        }
    }

    private StudyPhase DetermineCurrentStudyPhase()
    {
        if (_focusActive)
        {
            if (_focusRated) return StudyPhase.RecallRated;
            if (_focusRound == null || !_focusRound.HasCurrent) return StudyPhase.None;
            if (_focusRound.CurrentStep == StudyStep.Learn) return StudyPhase.Learn;
            if (!_focusAnswerVisible) return StudyPhase.RecallHidden;
            if (!_focusRated) return StudyPhase.RecallRevealedUnrated;
            return StudyPhase.RecallRated;
        }

        if (_currentPage == "review")
        {
            if (_reviewWord == null) return StudyPhase.None;
            if (!_reviewRevealed) return StudyPhase.RecallHidden;
            return StudyPhase.RecallRevealedUnrated;
        }

        if (_currentPage == "learning" && _studyWorkspaceHost.IsVisible && _dailyLearningSession != null)
        {
            if (_dailyLearningSession.Round.CurrentStep == StudyStep.Learn) return StudyPhase.Learn;
            if (!_planAnswerVisible) return StudyPhase.RecallHidden;
            return StudyPhase.RecallRevealedUnrated;
        }

        return StudyPhase.None;
    }

    private bool DetermineCanUndo()
    {
        if (_focusActive) return _focusUndo != null;
        if (_currentPage == "review") return _reviewUndo != null;
        if (_currentPage == "learning" && _dailyLearningSession != null) return _dailyLearningSession.CanUndo;
        return false;
    }

    private bool DetermineHasCurrentWord()
    {
        if (_focusActive) return (_focusRound?.HasCurrent == true) || _focusRated;
        if (_currentPage == "review") return _reviewWord != null;
        if (_currentPage == "learning" && _dailyLearningSession != null) return _dailyLearningSession.Round.HasCurrent;
        if (_currentPage == "lookup") return LookupResultCard.IsVisible;
        return false;
    }

    private void ExecuteCloseTopmostOverlay()
    {
        if(_visualConfirmation!=null){CloseVisualConfirmation();return;}
        if(_quoteEditorHost!=null){TryLeaveQuoteEditor();return;}
        if (_planCreatorOverlay != null) { ClosePlanCreator(); return; }
        if (_planEditDrawer?.IsVisible == true) { ClosePlanEditor(); return; }
        if (_settingsDrawerOpen)
        {
            CloseSettingsDrawer();
            return;
        }
        if (_workspaceChooser != null && _workspaceChooser.IsVisible)
        {
            _workspaceChooser.IsVisible = false;
            return;
        }
        if (DialogDeleteOverlay.IsVisible) { DialogDeleteOverlay.IsVisible = false; return; }
        if (DialogEditOverlay.IsVisible) { DialogEditOverlay.IsVisible = false; return; }
        if (DialogStageOverlay.IsVisible) { DialogStageOverlay.IsVisible = false; return; }
        if (RestoreOverlay.IsVisible) { RestoreOverlay.IsVisible = false; return; }
        if (_typingHost?.IsVisible == true) { ExitLearningTyping(); return; }
    }

    private void ExecuteUndoCurrent()
    {
        if (_focusActive) UndoFocus();
        else if (_currentPage == "review") UndoRoundRating();
        else if (_currentPage == "learning") UndoDailyLearning();
    }

    private void ExecuteFavoriteCurrent()
    {
        if (_focusActive) SaveFocusCurrent();
        else if (_currentPage == "lookup") AddFromShortcut();
        else if (_currentPage == "learning") ToggleCurrentLearningPlanFavorite();
        else if (_currentPage == "review") SetStatus(UiText.Bilingual("当前词已在词汇档案中。","This word is already in vocabulary."));
    }

    private void ExecuteSpeakCurrent()
    {
        if (_focusActive)
        {
            var word = _focusRated ? _focusRatedWord ?? "" : _focusRound?.HasCurrent == true ? _focusRound.Current : "";
            if (!string.IsNullOrWhiteSpace(word)) GetLearningAudio().Play(FocusDisplayWord(word),_focusIeltsWords.TryGetValue(word,out var source)?IeltsCatalog.ResolveAsset(source.AudioPath):null);
        }
        else if (_currentPage == "review" && _reviewWord != null)
        {
            GetLearningAudio().Play(_reviewWord.Word);
        }
        else if (_currentPage == "learning")
        {
            PlayCurrentLearningWordAudio();
        }
        else if (_currentPage == "lookup" && LookupResultCard.IsVisible)
        {
            var text = ResultWordText.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) GetLearningAudio().Play(text);
        }
    }

    private void ExecuteAdvanceLearn()
    {
        if (_focusActive && _focusRound != null)
        {
            _focusRound.CompleteLearn();
            _focusPresentation = null;
            _focusAnswerVisible = false;
            _focusRated = false;
            RenderFocus();
        }
        else if (_currentPage == "learning")
        {
            CompleteDailyLearn();
        }
    }

    private void ExecuteRevealHidden()
    {
        if (_focusActive)
        {
            _focusAnswerVisible = true;
            RenderFocus();
        }
        else if (_currentPage == "review")
        {
            RevealReview();
        }
        else if (_currentPage == "learning")
        {
            _planAnswerVisible = true;
            RenderDailyLearning();
        }
    }

    private void ExecuteNextWord()
    {
        if (_focusActive)
        {
            _focusRated = false;
            _focusAnswerVisible = false;
            RenderFocus();
        }
    }

    private async Task ExecuteRatingAsync(StudyRating rating)
    {
        if (_focusActive)
        {
            RateFocus(rating);
        }
        else if (_currentPage == "review")
        {
            await RateReviewRatingAsync(rating);
        }
        else if (_currentPage == "learning")
        {
            RateDailyLearning(rating);
        }
    }

    private void ToggleCurrentLearningPlanFavorite()
    {
        if (_dailyLearningSession?.Round.HasCurrent == true && _activeLearningPlan != null)
        {
            var word=_activeLearningPlan.Words.Single(w=>w.Id==_dailyLearningSession.Round.Current);
            if(_activeLearningPlan.Source==DailyStudyPlanSource.Ielts)
            { _ieltsCatalog??=IeltsCatalog.Load();var source=_ieltsCatalog.AllWords.FirstOrDefault(w=>w.Id==word.Id);if(source!=null)ArchiveSingleIeltsWord(source,null); }
            else SetStatus(UiText.Bilingual("当前词已在词汇档案中。","This word is already in vocabulary."));
        }
    }
}
