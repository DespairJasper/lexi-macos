namespace Lexi;

internal static partial class UiCatalog
{
    static UiCatalog()
    {
        var additions = new Dictionary<string, string>
        {
            ["创建学习计划 ▾"] = "Create study plan ▾",
            ["从词汇档案创建计划..."] = "From your vocabulary…",
            ["从 IELTS 教材创建计划..."] = "From IELTS materials…",
            ["暂无学习计划"] = "No study plans yet",
            ["回忆卡测试"] = "Recall",
            ["先学后测模式 · 请仔细阅读释义与例句"] = "Learn first · read the meaning and example",
            ["释义已隐藏，请在心中回忆词义"] = "Recall the meaning before revealing it",
            ["查看释义 ↵"] = "Reveal meaning ↵",
            ["← 返回计划"] = "← Back to plans",
            ["撤销上一次"] = "Undo last rating",
            ["改判为忘记"] = "Change to forgotten",
            ["忘记了 (1)"] = "Forgot (1)",
            ["模糊 (2)"] = "Unsure (2)",
            ["认识 (3)"] = "Known (3)",
            ["朗读 🔊"] = "Listen 🔊",
            ["看完了，开始回忆 → ↵"] = "Start recall → ↵",
            ["返回计划概览"] = "Back to plans",
            ["已完成与已停止"] = "Completed and stopped",
            ["展开 ▾"] = "Expand ▾",
            ["收起 ▴"] = "Collapse ▴",
            ["进度已实时保存至计划"] = "Progress saved",
            ["FSRS 自适应"] = "FSRS adaptive",
            ["按记忆表现动态排期"] = "Scheduled from recall performance",
            ["例句："] = "Example",
            ["计划名称"] = "Plan name",
            ["每日学习词数"] = "Words per day",
            ["学习排期估算"] = "Study estimate",
            ["退出练习"] = "Exit practice",
            ["重启后完整恢复未完成轮次与连击状态。"] = "Unfinished rounds and streaks resume after restart.",
            ["已完成的计划进度会自动保存；切换页面保留本轮连击，重启后完整恢复未完成轮次与连击状态。"] = "Progress is saved. Unfinished rounds and streaks resume after restart.",
            ["{0} 个活动计划 · 今日 {1} 词待学"] = "{0} active plans · {1} words to learn today",
            ["已完成 {0} / {1} · 剩余 {2} 词"] = "Completed {0} / {1} · {2} words left",
            ["连击 {0}/{1}"] = "Streak {0}/{1}",
        };
        foreach (var pair in additions) English[pair.Key] = pair.Value;
        AddInteractionTranslations();
        foreach (var key in English.Keys.ToList())
            English[key] = English[key].Replace("this Mac", "this PC").Replace("the menu bar", "the system tray");
    }
}
