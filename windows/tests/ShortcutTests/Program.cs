using Lexi;

void Assert(bool condition, string message)
{
    if (!condition)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAIL] {message}");
        Console.ResetColor();
        throw new Exception($"Test failed: {message}");
    }
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[PASS] {message}");
    Console.ResetColor();
}

Console.WriteLine("=== 开始 ShortcutTests 快捷键系统单元测试 ===");
var guardedEngine=new StudyShortcutEngine();
var activeContext=new StudyShortcutContext(IsStudySessionActive:true,StudyPhase:StudyPhase.RecallRevealedUnrated,HasCurrentWord:true);
Assert(guardedEngine.Dispatch(new ShortcutInputEvent("Space",ShortcutModifiers.Alt),activeContext)==StudyShortcutDispatchResult.Ignored,"Windows Alt+Space remains available");
Assert(guardedEngine.Dispatch(new ShortcutInputEvent("Right"),activeContext with{StudyPhase=StudyPhase.RecallHidden})!=StudyShortcutDispatchResult.RateKnown,"hidden answers cannot be rated");
Assert(guardedEngine.Dispatch(new ShortcutInputEvent("Space"),activeContext)!=StudyShortcutDispatchResult.Ignored,"Space is consumed after reveal so focused Back cannot activate");

// 1. 快捷键手势解析与格式化
Console.WriteLine("\n-- 1. Gesture 解析与格式化 --");
var g1 = ShortcutGesture.Parse("Ctrl+Z");
Assert(g1.Key == "Z" && g1.Modifiers == ShortcutModifiers.Control, "解析 Ctrl+Z");
Assert(g1.ToString() == "Ctrl+Z", "格式化 Ctrl+Z");

var g2 = ShortcutGesture.Parse("←");
Assert(g2.Key == "Left" && g2.Modifiers == ShortcutModifiers.None, "解析字符箭头 ← 为 Left");
Assert(g2.ToString() == "Left", "格式化 Left");

var g3 = ShortcutGesture.Parse("F11");
Assert(g3.Key == "F11" && g3.Modifiers == ShortcutModifiers.None, "解析 F11");

var g4 = ShortcutGesture.Parse("Ctrl+D");
Assert(g4.Key == "D" && g4.Modifiers == ShortcutModifiers.Control, "解析 Ctrl+D");

// 2. 默认配置完整性与 JSON 序列化
Console.WriteLine("\n-- 2. 默认配置与原子存储 --");
var defaultConfig = ShortcutConfiguration.CreateDefault();
var valDefault = ShortcutValidator.Validate(defaultConfig);
Assert(valDefault.IsValid, "默认配置校验通过");

var testDir = Path.Combine(Path.GetTempPath(), "LexiShortcutTests_" + Guid.NewGuid().ToString("N"));
try
{
    var store = ShortcutConfigStore.ForDataDirectory(testDir);
    store.Save(defaultConfig);
    Assert(File.Exists(store.FilePath), "原子保存配置文件成功");

    var loaded = store.Load();
    Assert(loaded.EnableNumberRatings == true, "配置读取 EnableNumberRatings 正确");
    Assert(loaded.Bindings[ShortcutAction.Forgot].Contains("Left"), "读取 Forgot 绑定包含 Left");
    Assert(loaded.Bindings[ShortcutAction.Undo].Contains("Ctrl+Z"), "读取 Undo 绑定包含 Ctrl+Z");
    Assert(loaded.Bindings[ShortcutAction.Favorite].Contains("Ctrl+D"), "读取 Favorite 绑定包含 Ctrl+D");
    Assert(loaded.Bindings[ShortcutAction.ToggleGlobalFocus].Contains("F11"), "读取 ToggleGlobalFocus 包含 F11");

    // 测试管理器
    var manager = new ShortcutConfigManager(store);
    var updateResult = manager.TryUpdateBindings(ShortcutAction.Speak, ["Ctrl+Shift+U"]);
    Assert(updateResult.IsValid, "管理器更新绑定成功");
    Assert(manager.CurrentConfig.Bindings[ShortcutAction.Speak].Contains("Ctrl+Shift+U"), "配置已更新内存与持久化");

    manager.ResetToDefault();
    Assert(manager.CurrentConfig.Bindings[ShortcutAction.Speak].Contains("Up"), "恢复默认配置成功");
}
finally
{
    if (Directory.Exists(testDir))
        Directory.Delete(testDir, true);
}

// 3. 冲突与保留键校验
Console.WriteLine("\n-- 3. 冲突与保留键校验 --");
// 3.1 冲突：两个动作绑定相同键
var conflictConfig = ShortcutConfiguration.CreateDefault();
conflictConfig.Bindings[ShortcutAction.Forgot] = ["Ctrl+Z"];
var conflictVal = ShortcutValidator.Validate(conflictConfig);
Assert(!conflictVal.IsValid, "检测到不同动作快捷键冲突");
Assert(conflictVal.Errors.Any(e => e.Contains("冲突")), "冲突错误信息包含'冲突'");

// 3.2 禁止 Alt+Space
var altSpaceConfig = ShortcutConfiguration.CreateDefault();
altSpaceConfig.Bindings[ShortcutAction.Favorite] = ["Alt+Space"];
var altSpaceVal = ShortcutValidator.Validate(altSpaceConfig);
Assert(!altSpaceVal.IsValid, "禁止 Alt+Space 校验拦截");
Assert(altSpaceVal.Errors.Any(e => e.Contains("Alt+Space")), "错误信息指出禁止 Alt+Space");

// 3.3 禁止裸键 Q, W, E, A, S, C, Delete
foreach (var bareKey in new[] { "Q", "W", "E", "A", "S", "C", "Delete" })
{
    var bareConfig = ShortcutConfiguration.CreateDefault();
    bareConfig.Bindings[ShortcutAction.Known] = [bareKey];
    var bareVal = ShortcutValidator.Validate(bareConfig);
    Assert(!bareVal.IsValid, $"禁止裸键 {bareKey} 校验拦截");
}

// 4. 单 Tunnel 路由引擎核心逻辑测试
Console.WriteLine("\n-- 4. 路由引擎状态与按键行为测试 --");
var engine = new StudyShortcutEngine();

// 4.1 评分按键：← Forgot, ↓ Unsure, → Known
var recallUnratedCtx = new StudyShortcutContext(
    IsStudySessionActive: true,
    StudyPhase: StudyPhase.RecallRevealedUnrated,
    HasCurrentWord: true
);

Assert(engine.Dispatch(new ShortcutInputEvent("Left"), recallUnratedCtx) == StudyShortcutDispatchResult.RateForgot, "← 触发 Forgot");
Assert(engine.Dispatch(new ShortcutInputEvent("Down"), recallUnratedCtx) == StudyShortcutDispatchResult.RateUnsure, "↓ 触发 Unsure");
Assert(engine.Dispatch(new ShortcutInputEvent("Right"), recallUnratedCtx) == StudyShortcutDispatchResult.RateKnown, "→ 触发 Known");
Assert(engine.Dispatch(new ShortcutInputEvent("Up"), recallUnratedCtx) == StudyShortcutDispatchResult.Speak, "↑ 触发 Speak");

// 4.2 1/2/3 备用按键与关闭备用
Assert(engine.Dispatch(new ShortcutInputEvent("1"), recallUnratedCtx) == StudyShortcutDispatchResult.RateForgot, "数字 1 触发 Forgot");
Assert(engine.Dispatch(new ShortcutInputEvent("2"), recallUnratedCtx) == StudyShortcutDispatchResult.RateUnsure, "数字 2 触发 Unsure");
Assert(engine.Dispatch(new ShortcutInputEvent("3"), recallUnratedCtx) == StudyShortcutDispatchResult.RateKnown, "数字 3 触发 Known");

// 关闭数字备用
engine.Configuration.EnableNumberRatings = false;
Assert(engine.Dispatch(new ShortcutInputEvent("1"), recallUnratedCtx) == StudyShortcutDispatchResult.Ignored, "关闭数字备用后数字 1 忽略");
Assert(engine.Dispatch(new ShortcutInputEvent("2"), recallUnratedCtx) == StudyShortcutDispatchResult.Ignored, "关闭数字备用后数字 2 忽略");
Assert(engine.Dispatch(new ShortcutInputEvent("3"), recallUnratedCtx) == StudyShortcutDispatchResult.Ignored, "关闭数字备用后数字 3 忽略");
engine.Configuration.EnableNumberRatings = true; // 恢复

// 4.3 Space / Enter 核心要求：Learn 推进 / 隐藏揭晓 / 已评分下一词，不隐式 Known，不退出
var learnCtx = new StudyShortcutContext(IsStudySessionActive: true, StudyPhase: StudyPhase.Learn, HasCurrentWord: true);
Assert(engine.Dispatch(new ShortcutInputEvent("Space"), learnCtx) == StudyShortcutDispatchResult.AdvanceLearn, "Learn 阶段 Space 推进");
Assert(engine.Dispatch(new ShortcutInputEvent("Enter"), learnCtx) == StudyShortcutDispatchResult.AdvanceLearn, "Learn 阶段 Enter 推进");

var hiddenCtx = new StudyShortcutContext(IsStudySessionActive: true, StudyPhase: StudyPhase.RecallHidden, HasCurrentWord: true);
Assert(engine.Dispatch(new ShortcutInputEvent("Space"), hiddenCtx) == StudyShortcutDispatchResult.RevealHidden, "Recall 隐藏阶段 Space 揭晓");
Assert(engine.Dispatch(new ShortcutInputEvent("Enter"), hiddenCtx) == StudyShortcutDispatchResult.RevealHidden, "Recall 隐藏阶段 Enter 揭晓");

var ratedCtx = new StudyShortcutContext(IsStudySessionActive: true, StudyPhase: StudyPhase.RecallRated, HasCurrentWord: true);
Assert(engine.Dispatch(new ShortcutInputEvent("Space"), ratedCtx) == StudyShortcutDispatchResult.NextWord, "Recall 已评分阶段 Space 进入下一词");
Assert(engine.Dispatch(new ShortcutInputEvent("Enter"), ratedCtx) == StudyShortcutDispatchResult.NextWord, "Recall 已评分阶段 Enter 进入下一词");

// 关键检验：已揭晓但未评分阶段，Space 和 Enter 严禁标记 Known，也严禁退出！
Assert(engine.Dispatch(new ShortcutInputEvent("Space"), recallUnratedCtx) == StudyShortcutDispatchResult.Consumed, "已揭晓未评分阶段 Space 消费事件而不评分退出");
Assert(engine.Dispatch(new ShortcutInputEvent("Enter"), recallUnratedCtx) == StudyShortcutDispatchResult.Consumed, "已揭晓未评分阶段 Enter 消费事件而不评分退出");

// 4.4 撤销 Ctrl+Z 与收藏 Ctrl+D
var undoableCtx = new StudyShortcutContext(IsStudySessionActive: true, CanUndo: true, HasCurrentWord: true);
var notUndoableCtx = new StudyShortcutContext(IsStudySessionActive: true, CanUndo: false, HasCurrentWord: true);
Assert(engine.Dispatch(new ShortcutInputEvent("Z", ShortcutModifiers.Control), undoableCtx) == StudyShortcutDispatchResult.Undo, "Ctrl+Z 触发 Undo");
Assert(engine.Dispatch(new ShortcutInputEvent("Z", ShortcutModifiers.Control), notUndoableCtx) == StudyShortcutDispatchResult.Ignored, "不可撤销时 Ctrl+Z 忽略");
Assert(engine.Dispatch(new ShortcutInputEvent("D", ShortcutModifiers.Control), undoableCtx) == StudyShortcutDispatchResult.Favorite, "Ctrl+D 触发 Favorite");

// 4.5 裸键彻底剔除：Q, W, E, A, S, C, Delete, Alt+Space
foreach (var removedKey in new[] { "Q", "W", "E", "A", "S", "C", "Delete" })
{
    var res = engine.Dispatch(new ShortcutInputEvent(removedKey), recallUnratedCtx);
    Assert(res == StudyShortcutDispatchResult.ProhibitedKeyIgnored, $"裸键 {removedKey} 被明确丢弃忽略，不触发动作");
}
Assert(engine.Dispatch(new ShortcutInputEvent("Space", ShortcutModifiers.Alt), recallUnratedCtx) == StudyShortcutDispatchResult.Ignored, "Alt+Space 留给 Windows");

// 4.6 Esc 路由优先级：顶层 overlay -> global focus -> 学习任务
var overlayCtx = new StudyShortcutContext(
    HasTopmostOverlay: true,
    IsGlobalFocusActive: true,
    IsStudySessionActive: true
);
Assert(engine.Dispatch(new ShortcutInputEvent("Escape"), overlayCtx) == StudyShortcutDispatchResult.CloseTopmostOverlay, "Esc 优先关闭顶层抽屉/Chooser/弹窗");

var globalFocusCtx = new StudyShortcutContext(
    HasTopmostOverlay: false,
    IsGlobalFocusActive: true,
    IsStudySessionActive: true
);
Assert(engine.Dispatch(new ShortcutInputEvent("Escape"), globalFocusCtx) == StudyShortcutDispatchResult.ExitGlobalFocus, "顶层无弹窗时 Esc 退出全局专注");

var studySessionCtx = new StudyShortcutContext(
    HasTopmostOverlay: false,
    IsGlobalFocusActive: false,
    IsStudySessionActive: true
);
Assert(engine.Dispatch(new ShortcutInputEvent("Escape"), studySessionCtx) == StudyShortcutDispatchResult.ExitStudySession, "仅学习任务时 Esc 退出学习任务");

// 4.7 输入控件焦点穿透与拼写回车自身提交
var textInputCtx = new StudyShortcutContext(
    IsInputFocused: true,
    IsSpellingActive: false,
    IsStudySessionActive: true,
    StudyPhase: StudyPhase.RecallRevealedUnrated
);
Assert(engine.Dispatch(new ShortcutInputEvent("Space"), textInputCtx) == StudyShortcutDispatchResult.Ignored, "输入框聚焦时 Space 穿透");
Assert(engine.Dispatch(new ShortcutInputEvent("Left"), textInputCtx) == StudyShortcutDispatchResult.Ignored, "输入框聚焦时 ← 穿透给光标移动");
Assert(engine.Dispatch(new ShortcutInputEvent("Enter"), textInputCtx) == StudyShortcutDispatchResult.Ignored, "普通输入框 Enter 穿透");
var inputFocusConfig=ShortcutConfiguration.CreateDefault();
inputFocusConfig.Bindings[ShortcutAction.ToggleGlobalFocus]=["Ctrl+G"];
var inputFocusEngine=new StudyShortcutEngine(inputFocusConfig);
Assert(inputFocusEngine.Dispatch(new ShortcutInputEvent("G",ShortcutModifiers.Control),textInputCtx)==StudyShortcutDispatchResult.Ignored,"自定义专注键让位于输入编辑");
Assert(engine.Dispatch(new ShortcutInputEvent("F11"),textInputCtx)==StudyShortcutDispatchResult.ToggleGlobalFocus,"默认F11可在输入时切换全局专注");

var spellingCtx = new StudyShortcutContext(
    IsInputFocused: true,
    IsSpellingActive: true,
    IsStudySessionActive: true
);
Assert(engine.Dispatch(new ShortcutInputEvent("Enter"), spellingCtx) == StudyShortcutDispatchResult.SpellingSubmit, "拼写练习输入框中 Enter 自身提交");
Assert(engine.Dispatch(new ShortcutInputEvent("Left"), spellingCtx) == StudyShortcutDispatchResult.Ignored, "拼写练习输入框中 ← 穿透给光标移动");

// 4.8 按住不重复与已处理不重复触发
var repeatInput = new ShortcutInputEvent("Right", IsRepeat: true);
Assert(engine.Dispatch(repeatInput, recallUnratedCtx) == StudyShortcutDispatchResult.Consumed, "按住按键消费重复而不再次执行");

var handledInput = new ShortcutInputEvent("Right", IsHandled: true);
Assert(engine.Dispatch(handledInput, recallUnratedCtx) == StudyShortcutDispatchResult.Ignored, "已处理按键 (IsHandled) 忽略");

// 4.9 F11 切换全局专注
Assert(engine.Dispatch(new ShortcutInputEvent("F11"), new StudyShortcutContext()) == StudyShortcutDispatchResult.ToggleGlobalFocus, "F11 切换全局专注");

Console.WriteLine("\n=== 所有 ShortcutTests 单元测试全部通过 (GREEN) ===");
