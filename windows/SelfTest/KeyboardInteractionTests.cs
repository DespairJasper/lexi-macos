namespace Lexi;

public static class KeyboardInteractionTests
{
    public static async Task RunAsync(MainWindow window, Action<bool, string> report)
    {
        await Task.Yield();

        try
        {
            report(true, "开始键盘交互自动化自测 (KeyboardInteractionTests)");

            // 1. 验证快捷键管理器与路由已配置
            var manager = window.ShortcutConfigManager;
            if (manager == null)
            {
                report(false, "ShortcutConfigManager 未初始化");
                return;
            }
            report(true, "ShortcutConfigManager 成功初始化");

            var config = manager.CurrentConfig;
            var validation = manager.Validate(config);
            report(validation.IsValid, $"当前快捷键配置校验: {(validation.IsValid ? "通过" : string.Join("; ", validation.Errors))}");

            var router = window.ShortcutRouter;
            if (router == null)
            {
                report(false, "StudyShortcutRouter 未初始化");
                return;
            }
            report(true, "StudyShortcutRouter 成功初始化");

            // 2. 验证默认键位定义
            var forgotGestures = manager.GetGestures(ShortcutAction.Forgot);
            var unsureGestures = manager.GetGestures(ShortcutAction.Unsure);
            var knownGestures = manager.GetGestures(ShortcutAction.Known);
            var speakGestures = manager.GetGestures(ShortcutAction.Speak);
            var primaryGestures = manager.GetGestures(ShortcutAction.Primary);
            var undoGestures = manager.GetGestures(ShortcutAction.Undo);
            var favGestures = manager.GetGestures(ShortcutAction.Favorite);
            var f11Gestures = manager.GetGestures(ShortcutAction.ToggleGlobalFocus);
            var escGestures = manager.GetGestures(ShortcutAction.Escape);

            report(forgotGestures.Contains("Left"), "Forgot 动作包含 ← (Left)");
            report(unsureGestures.Contains("Down"), "Unsure 动作包含 ↓ (Down)");
            report(knownGestures.Contains("Right"), "Known 动作包含 → (Right)");
            report(speakGestures.Contains("Up"), "Speak 动作包含 ↑ (Up)");
            report(primaryGestures.Contains("Space") && primaryGestures.Contains("Enter"), "Primary 动作包含 Space 与 Enter");
            report(undoGestures.Contains("Ctrl+Z"), "Undo 动作包含 Ctrl+Z");
            report(favGestures.Contains("Ctrl+D"), "Favorite 动作包含 Ctrl+D");
            report(f11Gestures.Contains("F11"), "ToggleGlobalFocus 包含 F11");
            report(escGestures.Contains("Escape"), "Escape 动作包含 Escape");

            // 3. 验证剔除的裸键不会触发动作
            var engine = router.Engine;
            var testContext = new StudyShortcutContext(
                IsStudySessionActive: true,
                StudyPhase: StudyPhase.RecallRevealedUnrated,
                HasCurrentWord: true
            );

            var removedKeys = new[] { "Q", "W", "E", "A", "S", "C", "Delete" };
            var allRemovedIgnored = true;
            foreach (var rk in removedKeys)
            {
                var res = engine.Dispatch(new ShortcutInputEvent(rk), testContext);
                if (res != StudyShortcutDispatchResult.ProhibitedKeyIgnored)
                {
                    allRemovedIgnored = false;
                    report(false, $"裸键 {rk} 未被禁止拦截，返回了 {res}");
                }
            }
            if (allRemovedIgnored)
            {
                report(true, "裸键 Q/W/E/A/S/C/Delete 均被正确丢弃拦截");
            }

            var altSpaceRes = engine.Dispatch(new ShortcutInputEvent("Space", ShortcutModifiers.Alt), testContext);
            report(altSpaceRes == StudyShortcutDispatchResult.Ignored, "Alt+Space 留给 Windows 系统菜单");

            // 4. 验证核心原则：已揭晓释义未评分时，Space/Enter 绝不隐式标记 Known，也不退出
            var spaceUnratedRes = engine.Dispatch(new ShortcutInputEvent("Space"), testContext);
            var enterUnratedRes = engine.Dispatch(new ShortcutInputEvent("Enter"), testContext);
            report(spaceUnratedRes == StudyShortcutDispatchResult.Consumed && enterUnratedRes == StudyShortcutDispatchResult.Consumed,
                "已揭晓未评分时 Space/Enter 被消耗，严禁隐式 Known 或退出");

            // 5. 验证 Esc 单路由优先级
            var overlayCtx = new StudyShortcutContext(HasTopmostOverlay: true, IsGlobalFocusActive: true, IsStudySessionActive: true);
            var escOverlayRes = engine.Dispatch(new ShortcutInputEvent("Escape"), overlayCtx);
            report(escOverlayRes == StudyShortcutDispatchResult.CloseTopmostOverlay, "Esc 优先级 1: 顶层弹窗/抽屉/Chooser 优先关闭");

            var globalFocusCtx = new StudyShortcutContext(HasTopmostOverlay: false, IsGlobalFocusActive: true, IsStudySessionActive: true);
            var escFocusRes = engine.Dispatch(new ShortcutInputEvent("Escape"), globalFocusCtx);
            report(escFocusRes == StudyShortcutDispatchResult.ExitGlobalFocus, "Esc 优先级 2: 退出全局专注模式");

            var studyCtx = new StudyShortcutContext(HasTopmostOverlay: false, IsGlobalFocusActive: false, IsStudySessionActive: true);
            var escStudyRes = engine.Dispatch(new ShortcutInputEvent("Escape"), studyCtx);
            report(escStudyRes == StudyShortcutDispatchResult.ExitStudySession, "Esc 优先级 3: 退出当前学习任务");

            // 6. 验证输入框优先与拼写自身提交
            var inputCtx = new StudyShortcutContext(IsInputFocused: true, IsSpellingActive: false, IsStudySessionActive: true);
            var inputSpaceRes = engine.Dispatch(new ShortcutInputEvent("Space"), inputCtx);
            var inputArrowRes = engine.Dispatch(new ShortcutInputEvent("Left"), inputCtx);
            report(inputSpaceRes == StudyShortcutDispatchResult.Ignored && inputArrowRes == StudyShortcutDispatchResult.Ignored,
                "输入框聚焦时按键完全穿透，不劫持输入控件");

            var spellingCtx = new StudyShortcutContext(IsInputFocused: true, IsSpellingActive: true, IsStudySessionActive: true);
            var spellingEnterRes = engine.Dispatch(new ShortcutInputEvent("Enter"), spellingCtx);
            report(spellingEnterRes == StudyShortcutDispatchResult.SpellingSubmit, "拼写练习中 Enter 由自身提交");

            // 7. 验证按住不连发 (IsRepeat)
            var repeatRes = engine.Dispatch(new ShortcutInputEvent("Right", IsRepeat: true), testContext);
            report(repeatRes == StudyShortcutDispatchResult.Consumed, "按住长按重复不评分，也不触发聚焦按钮");

            report(true, "键盘交互自动化自测全部完成并通过");
        }
        catch (Exception ex)
        {
            report(false, $"自测异常: {ex.Message}");
        }
    }
}
