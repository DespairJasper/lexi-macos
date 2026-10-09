using Avalonia.Controls;
using Avalonia.Input;

namespace Lexi;

public sealed class StudyShortcutRouter
{
    private readonly StudyShortcutEngine _engine;
    private readonly HashSet<Key> _pressedKeys = [];
    private readonly HashSet<Key> _consumedKeys = [];

    public StudyShortcutEngine Engine => _engine;
    public ShortcutConfiguration Configuration => _engine.Configuration;

    public StudyShortcutRouter(ShortcutConfiguration? config = null)
    {
        _engine = new StudyShortcutEngine(config);
    }

    public void ResetKeyState()
    {
        _pressedKeys.Clear();
        _consumedKeys.Clear();
    }

    public void OnKeyUp(KeyEventArgs e)
    {
        _pressedKeys.Remove(e.Key);
        // Buttons activate on release, even when their press was intercepted.
        if (_consumedKeys.Remove(e.Key)) e.Handled = true;
    }

    public static bool IsInputElement(object? source, IInputElement? focused)
    {
        if (source is TextBox or AutoCompleteBox or ComboBox or Slider or MenuItem)
            return true;
        if (focused is TextBox or AutoCompleteBox or ComboBox or Slider or MenuItem)
            return true;
        return false;
    }

    public StudyShortcutDispatchResult RouteKeyDown(
        KeyEventArgs e,
        IInputElement? focusedElement,
        bool isSpellingActive,
        bool hasTopmostOverlay,
        bool isGlobalFocusActive,
        bool isStudySessionActive,
        StudyPhase studyPhase,
        bool canUndo,
        bool hasCurrentWord,
        bool isRestoringOrUnavailable)
    {
        if (e.Handled)
            return StudyShortcutDispatchResult.Ignored;

        // IME 正在输入合成中时穿透
        if (e.Key == Key.ImeProcessed)
            return StudyShortcutDispatchResult.Ignored;

        // A selected passage owns its copy and selection keys; it must never
        // become a study rating, including when the user customized Ctrl+C.
        if ((focusedElement as SelectableTextBlock ?? e.Source as SelectableTextBlock) is { } passage
            && (passage.SelectionStart != passage.SelectionEnd
                || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                || e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.C or Key.A))
            return StudyShortcutDispatchResult.Ignored;

        // 防按键按住连发重复
        var isRepeat = !_pressedKeys.Add(e.Key);

        var modifiers = ShortcutModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= ShortcutModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= ShortcutModifiers.Meta;

        var keyName = e.Key.ToString();
        var isInputFocused = IsInputElement(e.Source, focusedElement);

        var inputEvent = new ShortcutInputEvent(
            Key: keyName,
            Modifiers: modifiers,
            IsHandled: e.Handled,
            IsRepeat: isRepeat,
            IsImeComposing: false
        );

        var context = new StudyShortcutContext(
            IsInputFocused: isInputFocused,
            IsSpellingActive: isSpellingActive,
            HasTopmostOverlay: hasTopmostOverlay,
            IsGlobalFocusActive: isGlobalFocusActive,
            IsStudySessionActive: isStudySessionActive,
            StudyPhase: studyPhase,
            CanUndo: canUndo,
            HasCurrentWord: hasCurrentWord,
            IsRestoringOrUnavailable: isRestoringOrUnavailable
        );

        var result = _engine.Dispatch(inputEvent, context);
        if (result is not StudyShortcutDispatchResult.Ignored and not StudyShortcutDispatchResult.SpellingSubmit)
            _consumedKeys.Add(e.Key);
        return result;
    }
}
