namespace Lexi;

public enum StudyPhase
{
    None,
    Learn,
    RecallHidden,
    RecallRevealedUnrated,
    RecallRated
}

public sealed record ShortcutInputEvent(
    string Key,
    ShortcutModifiers Modifiers = ShortcutModifiers.None,
    bool IsHandled = false,
    bool IsRepeat = false,
    bool IsImeComposing = false
);

public sealed record StudyShortcutContext(
    bool IsInputFocused = false,
    bool IsSpellingActive = false,
    bool HasTopmostOverlay = false,
    bool IsGlobalFocusActive = false,
    bool IsStudySessionActive = false,
    StudyPhase StudyPhase = StudyPhase.None,
    bool CanUndo = false,
    bool HasCurrentWord = false,
    bool IsRestoringOrUnavailable = false
);

public enum StudyShortcutDispatchResult
{
    Ignored,
    SpellingSubmit,
    CloseTopmostOverlay,
    ExitGlobalFocus,
    ExitStudySession,
    ToggleGlobalFocus,
    AdvanceLearn,
    RevealHidden,
    NextWord,
    RateForgot,
    RateUnsure,
    RateKnown,
    Speak,
    Undo,
    Favorite,
    ProhibitedKeyIgnored
    ,Consumed
}

public sealed class StudyShortcutEngine
{
    private static readonly HashSet<string> RemovedBareKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q", "W", "E", "A", "S", "C", "DELETE", "DEL"
    };

    public ShortcutConfiguration Configuration { get; set; }

    public StudyShortcutEngine(ShortcutConfiguration? configuration = null)
    {
        Configuration = configuration ?? ShortcutConfiguration.CreateDefault();
    }

    public StudyShortcutDispatchResult Dispatch(ShortcutInputEvent input, StudyShortcutContext context)
    {
        if (input.IsHandled) return StudyShortcutDispatchResult.Ignored;
        if (input.IsImeComposing) return StudyShortcutDispatchResult.Ignored;

        var keyName = ShortcutGesture.NormalizeKeyName(input.Key);
        if (input.IsRepeat)
        {
            var bound = Enum.GetValues<ShortcutAction>().Any(action=>MatchesAction(input,action,keyName))
                || (Configuration.EnableNumberRatings && input.Modifiers==ShortcutModifiers.None && keyName is "D1" or "D2" or "D3" or "NumPad1" or "NumPad2" or "NumPad3");
            return context.IsStudySessionActive && !context.IsInputFocused && bound
                ? StudyShortcutDispatchResult.Consumed : StudyShortcutDispatchResult.Ignored;
        }

        if (MatchesAction(input,ShortcutAction.Escape,keyName) && context.HasTopmostOverlay)
            return StudyShortcutDispatchResult.CloseTopmostOverlay;
        if (MatchesAction(input,ShortcutAction.ToggleGlobalFocus,keyName) && (!context.IsInputFocused || keyName=="F11" && input.Modifiers==ShortcutModifiers.None) && !context.HasTopmostOverlay && !context.IsRestoringOrUnavailable)
            return StudyShortcutDispatchResult.ToggleGlobalFocus;
        // 1. 输入控件处理优先（文本框/IME/组合框/滑块/菜单等）
        if (context.IsInputFocused)
        {
            // 拼写练习输入框中，Enter 自身提交
            if (context.IsSpellingActive && keyName == "Enter" && input.Modifiers == ShortcutModifiers.None)
            {
                return StudyShortcutDispatchResult.SpellingSubmit;
            }
            // 其他输入焦点情况下，所有按键均由输入控件优先处理，穿透不拦截
            return StudyShortcutDispatchResult.Ignored;
        }

        // 2. 检查被明确剔除的裸键 (Q/W/E/A/S/C/Delete 及 Alt+Space)
        if (input.Modifiers == ShortcutModifiers.None && RemovedBareKeys.Contains(keyName))
        {
            return StudyShortcutDispatchResult.ProhibitedKeyIgnored;
        }
        if (input.Modifiers == ShortcutModifiers.Alt && keyName == "Space")
        {
            return StudyShortcutDispatchResult.Ignored;
        }

        // 3. Esc 单路由分层优先级：
        // 顶层菜单弹窗抽屉/chooser -> global focus -> 学习任务
        if (MatchesAction(input, ShortcutAction.Escape, keyName))
        {
            if (context.HasTopmostOverlay)
                return StudyShortcutDispatchResult.CloseTopmostOverlay;
            if (context.IsGlobalFocusActive)
                return StudyShortcutDispatchResult.ExitGlobalFocus;
            if (context.IsStudySessionActive)
                return StudyShortcutDispatchResult.ExitStudySession;

            return StudyShortcutDispatchResult.Ignored;
        }

        if (context.HasTopmostOverlay) return StudyShortcutDispatchResult.Ignored;
        // 如果数据库不可用或正在恢复，后续学习操作不触发
        if (context.IsRestoringOrUnavailable)
            return StudyShortcutDispatchResult.Ignored;

        // 4. F11 切换全局专注
        if (MatchesAction(input, ShortcutAction.ToggleGlobalFocus, keyName))
        {
            return StudyShortcutDispatchResult.ToggleGlobalFocus;
        }

        // 5. 撤销 (默认 Ctrl+Z)
        if (MatchesAction(input, ShortcutAction.Undo, keyName))
        {
            return context.CanUndo ? StudyShortcutDispatchResult.Undo : StudyShortcutDispatchResult.Ignored;
        }

        // 6. 收藏 (默认 Ctrl+D)
        if (MatchesAction(input, ShortcutAction.Favorite, keyName))
        {
            return context.HasCurrentWord ? StudyShortcutDispatchResult.Favorite : StudyShortcutDispatchResult.Ignored;
        }

        // 7. 发音朗读 (默认 ↑)
        if (MatchesAction(input, ShortcutAction.Speak, keyName))
        {
            return context.HasCurrentWord ? StudyShortcutDispatchResult.Speak : StudyShortcutDispatchResult.Ignored;
        }

        // 8. 评分快捷键：← Forgot, ↓ Unsure, → Known，以及可选的 1/2/3 备用
        if (MatchesRatingAction(input, ShortcutAction.Forgot, keyName, ["D1", "NumPad1"]))
        {
            if (context.StudyPhase == StudyPhase.RecallRevealedUnrated)
                return StudyShortcutDispatchResult.RateForgot;
        }
        if (MatchesRatingAction(input, ShortcutAction.Unsure, keyName, ["D2", "NumPad2"]))
        {
            if (context.StudyPhase == StudyPhase.RecallRevealedUnrated)
                return StudyShortcutDispatchResult.RateUnsure;
        }
        if (MatchesRatingAction(input, ShortcutAction.Known, keyName, ["D3", "NumPad3"]))
        {
            if (context.StudyPhase == StudyPhase.RecallRevealedUnrated)
                return StudyShortcutDispatchResult.RateKnown;
        }

        // 9. Space / Enter 主推进快捷键：
        // Learn 推进 / 隐藏揭晓 / 已评分下一词；
        // 关键：不隐式 Known，不退出！
        if (MatchesAction(input, ShortcutAction.Primary, keyName))
        {
            switch (context.StudyPhase)
            {
                case StudyPhase.Learn:
                    return StudyShortcutDispatchResult.AdvanceLearn;
                case StudyPhase.RecallHidden:
                    return StudyShortcutDispatchResult.RevealHidden;
                case StudyPhase.RecallRated:
                    return StudyShortcutDispatchResult.NextWord;
                case StudyPhase.RecallRevealedUnrated:
                    // 核心原则：已揭晓释义但未评分时，Space/Enter 绝不隐式标记 Known，也不退出！
                    return StudyShortcutDispatchResult.Consumed;
                default:
                    return context.IsStudySessionActive ? StudyShortcutDispatchResult.Consumed : StudyShortcutDispatchResult.Ignored;
            }
        }

        return StudyShortcutDispatchResult.Ignored;
    }

    private bool MatchesAction(ShortcutInputEvent input, ShortcutAction action, string normalizedKey)
    {
        var gestures = Configuration.GetGestures(action);
        for (int i = 0; i < gestures.Count; i++)
        {
            if (gestures[i].Matches(normalizedKey, input.Modifiers))
                return true;
        }
        return false;
    }

    private bool MatchesRatingAction(ShortcutInputEvent input, ShortcutAction action, string normalizedKey, string[] numberFallbackKeys)
    {
        if (MatchesAction(input, action, normalizedKey))
            return true;

        if (Configuration.EnableNumberRatings && input.Modifiers == ShortcutModifiers.None)
        {
            for (int i = 0; i < numberFallbackKeys.Length; i++)
            {
                if (string.Equals(normalizedKey, numberFallbackKeys[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
