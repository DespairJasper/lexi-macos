using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Lexi.Controls;
using Lexi.Features.Learning;

namespace Lexi;

/// <summary>
/// 1.2.2 统一背词画布自检（主题适配版）：
/// 专注 / 计划 / 今日复习共享 StudyCanvasControl，背景与字色全部读取当前 Avalonia
/// 主题资源（PaperBrush / InkBrush / MutedBrush / LineBrush / PrimaryGreen），
/// 浅 / 深主题切换即时刷新，复用同一全局 Surface 且不重开 session、不重置评分 / 揭晓。
/// 独立暖纸 / 雾灰 / 夜墨切换入口与独立偏好已删除。
/// 供主控验收调用：RunAsync(MainWindow, Action&lt;bool,string&gt;)。
/// </summary>
public static class StudyCanvas122Tests
{
    public static async Task RunAsync(MainWindow window, Action<bool, string> check)
    {
        await Task.Yield();

        object? Call(string name, params object[] args) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        object? Field(string name) => typeof(MainWindow)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window);

        bool MatchesTheme(Control control, AvaloniaProperty property, string resource, ThemeVariant theme)
        {
            var value = control.GetValue(property) as IBrush;
            var expected = window.FindResource(theme, resource) as IBrush;
            return value != null && expected != null && value.ToString() == expected.ToString();
        }

        // ---- PART 1: 独立背景偏好机制已删除，改为跟随应用主题 ----
        check(typeof(MainWindow).GetMethod("SetStudyCanvasTheme", BindingFlags.NonPublic | BindingFlags.Instance) == null
            && typeof(MainWindow).GetMethod("CycleStudyCanvasTheme", BindingFlags.NonPublic | BindingFlags.Instance) == null,
            "已删除 SetStudyCanvasTheme / CycleStudyCanvasTheme 独立背景切换入口");
        check(typeof(MainWindow).GetProperty("ActiveStudyCanvasTheme", BindingFlags.NonPublic | BindingFlags.Instance) == null
            && typeof(MainWindow).GetField("_studyCanvasTheme", BindingFlags.NonPublic | BindingFlags.Instance) == null
            && typeof(MainWindow).GetField("_focusPaletteChoice", BindingFlags.NonPublic | BindingFlags.Instance) == null,
            "已删除 ActiveStudyCanvasTheme / _studyCanvasTheme / _focusPaletteChoice 独立偏好状态");
        check(typeof(MainWindow).GetMethod("EnsureStudyCanvasThemeLoaded", BindingFlags.NonPublic | BindingFlags.Instance) == null,
            "已删除 EnsureStudyCanvasThemeLoaded（study-canvas.json 不再覆盖主题）");
        check(typeof(StudyCanvasModel).GetProperty("Theme") == null,
            "StudyCanvasModel 不再携带独立 Theme 字段");
        var refresh = typeof(MainWindow).GetMethod("RefreshUnifiedStudyCanvas", BindingFlags.NonPublic | BindingFlags.Instance);
        check(refresh != null && !refresh.IsPublic, "保留非公开 RefreshUnifiedStudyCanvas() 统一刷新入口");

        // ---- PART 2: StudyCanvasControl 内容组件渲染（focus / 计划 / 复习共享）----
        var canvas = new StudyCanvasControl();
        canvas.Render(new StudyCanvasModel { Word = "apple", Phonetic = "/ˈæpəl/", Meaning = "苹果", Details = "a round fruit", Example = "I ate an apple.", MeaningVisible = true });
        var texts = canvas.GetLogicalDescendants().OfType<SelectableTextBlock>().ToList();
        check(texts.Any(t => t.Text == "apple") && texts.Any(t => t.Text == "苹果") && texts.Any(t => t.Text == "/ˈæpəl/"),
            "内容组件渲染单词 / 释义 / 音标");
        check(canvas.GetLogicalDescendants().OfType<TextBlock>().Any()
            && canvas.GetLogicalDescendants().OfType<TextBlock>().All(t => t is SelectableTextBlock),
            "正文全部使用 SelectableTextBlock 支持选中复制");
        var wordBlock = texts.First(t => t.Text == "apple");
        check(wordBlock.FontSize == 40 && wordBlock.FontWeight == FontWeight.Bold,
            "单词正文大字号加粗随宿主宽度换行");

        canvas.Render(new StudyCanvasModel { Word = "apple", Meaning = "苹果", MeaningVisible = false, Placeholder = "先回忆这个词的含义" });
        var hidden = canvas.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(t => t.Text).ToList();
        check(!hidden.Contains("苹果") && hidden.Contains("先回忆这个词的含义"),
            "揭晓释义前隐藏释义并显示回忆占位提示");

        // ---- PART 3: 实际浅 / 深主题资源一致 + 切换不重置 session ----
        var originalTheme = window.RequestedThemeVariant;
        var surface = Field("_globalStudySurface") as Border;
        check(surface != null && surface.Name == "UnifiedFocusSurface" && surface.GetLogicalAncestors().Contains(window), "已挂载真正全局统一画布 UnifiedFocusSurface");
        Call("EnsureStudySurfaceTheming");
        Call("ApplyReviewCanvasTheme");

        // 挂一个探针内容组件到全局 Surface 上，使其继承窗口主题后断言字色
        var probe = new StudyCanvasControl();
        var probeHost = new Border { Child = probe };
        ((Grid)surface!.Child!).Children.Add(probeHost);
        probe.Render(new StudyCanvasModel { Word = "theme", Phonetic = "/θiːm/", Meaning = "主题", Details = "detail", Example = "example", MeaningVisible = true });

        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            await Task.Delay(30);
            check(MatchesTheme(surface, Border.BackgroundProperty, "PaperBrush", theme),
                $"{theme.Key} 全局统一画布背景读取 PaperBrush 主题资源");
            var reviewCard = window.FindControl<Border>("ReviewCard");
            check(reviewCard != null && MatchesTheme(reviewCard, Border.BackgroundProperty, "PaperBrush", theme)
                && MatchesTheme(reviewCard, Border.BorderBrushProperty, "LineBrush", theme),
                $"{theme.Key} 今日复习卡去白卡：背景 / 边线读取 PaperBrush / LineBrush");
            var reviewWord = window.FindControl<SelectableTextBlock>("ReviewWordText");
            var reviewMeaning = window.FindControl<SelectableTextBlock>("ReviewMeaningText");
            var reviewPhonetic = window.FindControl<SelectableTextBlock>("ReviewPhoneticText");
            check(reviewWord != null && reviewMeaning != null && reviewPhonetic != null
                && MatchesTheme(reviewWord, TextBlock.ForegroundProperty, "InkBrush", theme)
                && MatchesTheme(reviewMeaning, TextBlock.ForegroundProperty, "InkBrush", theme)
                && MatchesTheme(reviewPhonetic, TextBlock.ForegroundProperty, "MutedBrush", theme),
                $"{theme.Key} 复习单词 / 释义 / 音标字色读取 InkBrush / MutedBrush");
            var probeWord = probe.GetLogicalDescendants().OfType<SelectableTextBlock>().First(t => t.Text == "theme");
            var probePhonetic = probe.GetLogicalDescendants().OfType<SelectableTextBlock>().First(t => t.Text == "/θiːm/");
            check(MatchesTheme(probeWord, TextBlock.ForegroundProperty, "InkBrush", theme)
                && MatchesTheme(probePhonetic, TextBlock.ForegroundProperty, "MutedBrush", theme),
                $"{theme.Key} 内容组件词头 / 弱化字色读取主题资源");
        }

        string SnapshotState()
        {
            string P(object? o, string name)
            {
                var p = o?.GetType().GetProperty(name);
                return p == null ? "<none>" : (p.GetValue(o)?.ToString() ?? "<null>");
            }
            var round = Field("_reviewRound");
            var handled = Field("_reviewHandled");
            var handledCount = handled?.GetType().GetProperty("Count")?.GetValue(handled)?.ToString() ?? "<none>";
            return string.Join("|",
                P(round, "Completed"), P(round, "Total"), P(round, "CurrentStreak"), P(round, "HasCurrent"),
                Field("_reviewRevealed")?.ToString() ?? "<null>",
                Field("_focusActive")?.ToString() ?? "<null>",
                Field("_focusAnswerVisible")?.ToString() ?? "<null>",
                Field("_focusRated")?.ToString() ?? "<null>",
                handledCount,
                window.FindControl<SelectableTextBlock>("ReviewWordText")?.Text ?? "",
                window.FindControl<SelectableTextBlock>("ReviewMeaningText")?.Text ?? "");
        }

        var before = SnapshotState();
        window.RequestedThemeVariant = ThemeVariant.Dark;
        await Task.Delay(30);
        window.RequestedThemeVariant = ThemeVariant.Light;
        await Task.Delay(30);
        check(ReferenceEquals(surface, Field("_globalStudySurface")) && before == SnapshotState(),
            "浅 / 深主题切换复用同一全局 Surface，session 评分 / 揭晓状态不重置");

        ((Grid)surface.Child!).Children.Remove(probeHost);
        window.RequestedThemeVariant = originalTheme;

        // ---- PART 4: 拼写练习复用既有 PracticeSetupControl（唯一开始键）----
        var setup = new PracticeSetupControl(total: 10, weak: 2, start: (_, _) => { }, cancel: () => { });
        var setupButtons = setup.GetLogicalDescendants().OfType<Button>().ToList();
        check(setup.StartButton != null && setup.StartButton.Classes.Contains("primary"),
            "完成页 / 末批使用 PracticeSetupControl 唯一主开始键");
        check(setup.CancelButton != null && setup.CancelButton.Classes.Contains("ghost"),
            "PracticeSetupControl 保留取消键");
        check(setupButtons.Count(b => b.Classes.Contains("primary")) == 1,
            "拼写准备仅一个开始键，替代四巨按钮");

        // ---- PART 5: 拼写辅助复用 TypingSession.HintText / RevealHint / RevealAnswer / AssistedCount ----
        check(typeof(TypingSession).GetProperty("HintText") != null, "拼写提示复用 TypingSession.HintText（逐字占位）");
        check(typeof(TypingSession).GetMethod("RevealHint") != null, "逐字提示复用 TypingSession.RevealHint()");
        check(typeof(TypingSession).GetMethod("RevealAnswer") != null, "查看答案复用 TypingSession.RevealAnswer()（计入重练）");
        check(typeof(TypingSession).GetProperty("AssistedCount") != null, "拼写统计复用 TypingSession.AssistedCount");

        check(typeof(MainWindow).GetField("_focusCanvas", BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType == typeof(StudyCanvasControl),
            "focus 表面挂载共享 StudyCanvasControl 内容组件");
    }
}
