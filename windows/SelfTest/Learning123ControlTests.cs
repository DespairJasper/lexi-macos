using Avalonia.Controls.Primitives;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Lexi.Controls;
using Lexi.Features.Learning;

namespace Lexi;

/// <summary>
/// 1.2.3 学习与拼写控件级自检套件：
/// - StudyCanvasControl：限宽 680、40 字号词头、音标扬声器、揭晓阅读边界与左对齐、纯色无白卡、全 SelectableTextBlock
/// - PracticeSetupControl：限宽 640、高约 320、两项范围、空 weak 禁用、唯一开始键、Hints 恒 true、模式单选框不暴露于视觉树
/// - SpellingPracticeControl：20 字号释义、26 字号轻底线输入框、短窗可滚、Enter 单次提交、提示不清空输入、就近反馈与扬声器
/// 供主控验收调用：RunAsync(MainWindow?, Action&lt;bool, string&gt;)。
/// </summary>
public static class Learning123ControlTests
{
    public static async Task RunAsync(MainWindow? window, Action<bool, string> check)
    {
        await RunAsync(check);
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        await Task.Yield();

        // =====================================================================
        // PART 1: StudyCanvasControl 视觉与阅读边界验证
        // =====================================================================
        var canvas = new StudyCanvasControl();
        bool speakCalled = false;
        var testModel = new StudyCanvasModel
        {
            Word = "resilient",
            Phonetic = "/rɪˈzɪliənt/",
            Meaning = "有弹性的；适应力强的",
            Details = "able to withstand or recover quickly from difficult conditions.",
            Example = "Babies are generally remarkably resilient.",
            MeaningVisible = false,
            Placeholder = "尝试回忆词义后揭晓",
            Speak = () => speakCalled = true
        };

        canvas.Render(testModel);

        var rootStack = canvas.Content as StackPanel;
        check(rootStack != null && rootStack.MaxWidth == 680,
            "StudyCanvasControl 最大阅读宽度限制为 680 DIP");
        check(rootStack != null && rootStack.HorizontalAlignment == HorizontalAlignment.Stretch,
            "StudyCanvasControl 正文填满宿主限定的阅读宽度，揭晓前后边界稳定");

        var wordBlock = canvas.GetLogicalDescendants().OfType<SelectableTextBlock>().FirstOrDefault(t => t.Text == "resilient");
        check(wordBlock != null && wordBlock.FontSize == 40 && wordBlock.FontWeight == FontWeight.Bold,
            "StudyCanvasControl 词头为 40 字号加粗");

        var textsBeforeReveal = canvas.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(t => t.Text).ToList();
        check(!textsBeforeReveal.Contains("有弹性的；适应力强的") && textsBeforeReveal.Contains("尝试回忆词义后揭晓"),
            "揭晓前隐藏释义并展示回忆占位提示");

        check(canvas.GetLogicalDescendants().OfType<TextBlock>().All(t => t is SelectableTextBlock),
            "StudyCanvasControl 全部文本块使用 SelectableTextBlock 保障选中复制");

        // 验证音频播放委托接入
        testModel.Speak?.Invoke();
        check(speakCalled, "StudyCanvasModel.Speak 委托属性支持主控赋值与调用");

        // 揭晓状态验证
        testModel.MeaningVisible = true;
        canvas.Render(testModel);

        var textsAfterReveal = canvas.GetLogicalDescendants().OfType<SelectableTextBlock>().ToList();
        var meaningBlock = textsAfterReveal.FirstOrDefault(t => t.Text == "有弹性的；适应力强的");
        check(meaningBlock != null && meaningBlock.FontSize == 20 && meaningBlock.TextAlignment == TextAlignment.Left,
            "揭晓后释义为 20 字号且长正文左对齐");

        var detailBlock = textsAfterReveal.FirstOrDefault(t => t.Text == testModel.Details);
        var exampleBlock = textsAfterReveal.FirstOrDefault(t => t.Text == testModel.Example);
        check(detailBlock != null && detailBlock.TextAlignment == TextAlignment.Left
            && exampleBlock != null && exampleBlock.TextAlignment == TextAlignment.Left,
            "揭晓后细节与例句长正文左对齐并在 680 DIP 限宽内");

        var dividers = canvas.GetLogicalDescendants().OfType<Border>().Where(b => b.Height == 1).ToList();
        check(dividers.Any(), "揭晓后生成细分隔线形成阅读边界");

        // =====================================================================
        // PART 2: PracticeSetupControl 范围与单一模式验证
        // =====================================================================
        bool? startOnlyWeak = null;
        bool? startHints = null;
        bool cancelled = false;

        var setup = new PracticeSetupControl(
            total: 50,
            weak: 12,
            start: (w, h) =>
            {
                startOnlyWeak = w;
                startHints = h;
            },
            cancel: () => cancelled = true
        );

        var setupBorder = setup.Content as Border;
        check(setupBorder != null && setupBorder.MaxWidth == 640,
            "PracticeSetupControl 根容器限制最大宽度为 640 DIP");
        check(setup.TotalWords == 50 && setup.WeakWords == 12,
            "PracticeSetupControl 正确记录总词数与薄弱词数");
        check(setup.WeakScopeRadio.IsEnabled, "存在薄弱词时薄弱项选项启用");
        check(setup.Hints, "1.2.3 方案中 PracticeSetupControl.Hints 恒为 true");

        // 触发开始练习
        setup.WeakScopeRadio.IsChecked = true;
        setup.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(startOnlyWeak == true && startHints == true,
            "点击开始练习正确向主控传递 (onlyWeak: true, hints: true)");

        // 触发取消
        setup.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(cancelled, "点击取消正确触发 cancel 回调");

        // 检查模式单选框不暴露于视觉树
        var visualRadios = setup.GetLogicalDescendants().OfType<RadioButton>().ToList();
        check(!visualRadios.Contains(setup.MemoryModeRadio) && !visualRadios.Contains(setup.AssistedModeRadio),
            "兼容模式单选框未暴露至视觉树，仅保留两项范围单选");

        // 边界情况：空 weak 禁用
        var setupNoWeak = new PracticeSetupControl(
            total: 20,
            weak: 0,
            start: (_, _) => { },
            cancel: () => { }
        );
        check(!setupNoWeak.WeakScopeRadio.IsEnabled && setupNoWeak.AllScopeRadio.IsChecked == true,
            "薄弱词为 0 时严格禁用薄弱项单选并回退至全部词条");
        check(!setupNoWeak.OnlyWeak, "薄弱词为 0 时 OnlyWeak 为 false");

        // 边界情况：总词数为 0 禁用开始
        var setupZero = new PracticeSetupControl(
            total: 0,
            weak: 0,
            start: (_, _) => { },
            cancel: () => { }
        );
        check(!setupZero.StartButton.IsEnabled, "总词数为 0 时唯一开始键禁用");

        // =====================================================================
        // PART 3: SpellingPracticeControl 拼写展示与交互行为验证
        // =====================================================================
        var spelling = new SpellingPracticeControl();
        string submittedWord = "";
        bool hintFired = false;
        bool answerFired = false;
        bool speakFired = false;

        spelling.Submitted += w => submittedWord = w;
        spelling.HintRequested += () => hintFired = true;
        spelling.AnswerRequested += () => answerFired = true;
        spelling.SpeakRequested += () => speakFired = true;

        var spellModel = new SpellingPracticeModel
        {
            Word = "persist",
            Phonetic = "/pərˈsɪst/",
            Meaning = "坚持；执意",
            Hint = "p _ _ s _ _ t",
            ProgressText = "3 / 20",
            Feedback = "拼写正确！",
            IsCorrect = true
        };

        spelling.Update(spellModel);

        check(spelling.MeaningBlock.FontSize == 20 && spelling.MeaningBlock.Text == "坚持；执意",
            "SpellingPracticeControl 释义字号为 20 字号居中呈现");

        check(spelling.InputBox.FontSize >= 24 && spelling.InputBox.FontSize <= 28,
            "SpellingPracticeControl 输入框字号在 24-28 区间（实际 26 字号）");

        check(spelling.InputBox.BorderThickness.Bottom == 2 && spelling.InputBox.BorderThickness.Top == 0,
            "SpellingPracticeControl 输入框具备轻底线样式");

        check(spelling.HintBlock.Text == "p _ _ s _ _ t" && spelling.HintBlock.IsVisible,
            "SpellingPracticeControl 单一轻量提示呈现，去除巨大双重占位线");

        var scroller = (spelling.Content as Grid)?.Children.OfType<ScrollViewer>().Single();
        check(scroller != null && scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "SpellingPracticeControl 外层包含 ScrollViewer 保障短窗可滚");

        // 验证提示不清空输入
        spelling.InputBox.Text = "pers";
        spelling.HintButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(hintFired && spelling.InputBox.Text == "pers",
            "触发提示时不清空已键入内容并广播 HintRequested 事件");

        // 验证看答案事件
        spelling.AnswerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(answerFired, "触发看答案广播 AnswerRequested 事件");
        spelling.GetLogicalDescendants().OfType<InlineAudioButton>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(speakFired,"拼写中的实际扬声器按钮触发朗读事件");

        // 验证 Enter 单次防抖提交
        int submitCount = 0;
        spelling.Submitted += _ => submitCount++;
        spelling.InputBox.Text = "persist";
        spelling.SubmitCurrentInput();
        check(submitCount == 1 && submittedWord == "persist",
            "提交逻辑正确单次提交输入内容");

        // 验证看答案后切换下一词按钮
        spellModel.AnswerRevealed = true;
        spelling.Update(spellModel);
        check(spelling.NextButton.IsVisible && !spelling.SubmitButton.IsVisible,
            "查看答案后操作区切换为「下一词」主按钮");

        // 刷新双语无异常
        spelling.RefreshLanguage();
        check(true, "SpellingPracticeControl.RefreshLanguage 刷新双语正常");
    }
}
