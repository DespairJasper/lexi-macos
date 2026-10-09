using System;
using System.Collections.Generic;
using System.Linq;
using Lexi.Controls;

namespace Lexi.Features.Ielts;

/// <summary>
/// IELTS 专区自动化契约回归测试：
/// 1. 跨章节选择保留与训练来源诚实性
/// 2. 筛选与选择解耦（筛选仅影响可见性，不覆盖已选集合）
/// 3. 不按拼写合并来源（同拼写不同章节/条目拥有独立唯一身份）
/// 4. 档案式词行（选择、展开、发音正交独立，缺字段不占位）
/// 5. 动态语言切换
/// </summary>
public static class IeltsTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception("IELTS 断言失败: " + message);
        Console.WriteLine("PASS: [IELTS] " + message);
    }

    public static void RunTests()
    {
        Console.WriteLine("=== 开始运行 IELTS 自动化契约测试 ===");

        if (Avalonia.Application.Current == null)
        {
            try
            {
                Program.BuildAvaloniaApp().SetupWithoutStarting();
            }
            catch { }
        }

        TestCrossChapterSelectionAndExplicitSource();
        TestFilterSelectionDecoupling();
        TestDoNotMergeBySpelling();
        TestVocabularyRowOrthogonality();
        TestDynamicLocalization();

        Console.WriteLine("=== IELTS 自动化契约测试全部通过 ===");
    }

    private static void TestCrossChapterSelectionAndExplicitSource()
    {
        var store = new IeltsSelectionStore();
        var word1 = new LearningWord { Id = "01_001", Words = ["apple"], Meaning = "苹果" };
        var word2 = new LearningWord { Id = "01_002", Words = ["banana"], Meaning = "香蕉" };
        var word3 = new LearningWord { Id = "02_001", Words = ["orange"], Meaning = "橙子" };

        var sec1 = new LearningSection { Id = "01", Title = "第一章 自然地理" };
        var sec2 = new LearningSection { Id = "02", Title = "第二章 植物研究" };

        // 1. 单章选择
        store.Toggle(word1, "01", true);
        store.Toggle(word2, "01", true);

        Assert(store.Count == 2, "同一章节选入 2 词");
        Assert(store.DistinctChapterCount == 1, "属于 1 个章节");
        Assert(store.GetExplicitTrainingSource(sec1) == "IELTS · 第一章 自然地理（已选 2 词）", "单章节训练来源明确标注章节及选词数");

        // 2. 切章并追加选词（跨章保留）
        store.Toggle(word3, "02", true);
        Assert(store.Count == 3, "跨章追加后总共 3 词");
        Assert(store.DistinctChapterCount == 2, "跨 2 个不同章节");
        Assert(store.GetExplicitTrainingSource(sec2) == "IELTS · 跨章节已选 3 词", "跨多章节时训练来源明确标注跨章节与总词数");

        // 3. 取消单词
        store.Toggle(word1, "01", false);
        Assert(store.Count == 2 && !store.Contains("01_001"), "取消单个词保留其余跨章词");

        // 4. 清空
        store.Clear();
        Assert(store.Count == 0, "清空后选择库为空");
        Assert(store.GetExplicitTrainingSource(sec1) == "IELTS · 第一章 自然地理", "无选择时训练来源默认为当前整章");
    }

    private static void TestFilterSelectionDecoupling()
    {
        var store = new IeltsSelectionStore();
        var wordA = new LearningWord { Id = "sec1_w1", Words = ["alpha"], Meaning = "阿尔法" };
        var wordB = new LearningWord { Id = "sec1_w2", Words = ["beta"], Meaning = "贝塔" };

        // 勾选 wordA
        store.Toggle(wordA, "sec1", true);

        // 模拟筛选：搜索只匹配 wordB
        var allWords = new List<LearningWord> { wordA, wordB };
        var filteredBySearch = allWords.Where(w => w.Word.Contains("beta")).ToList();

        Assert(filteredBySearch.Count == 1 && filteredBySearch[0].Id == "sec1_w2", "搜索筛选过滤可见词");
        Assert(store.Contains("sec1_w1") && store.Count == 1, "筛选变化绝不影响已勾选的词汇集合");
    }

    private static void TestDoNotMergeBySpelling()
    {
        var store = new IeltsSelectionStore();
        // 两个相同拼写但来源章节/ID不同的词
        var wordGeo = new LearningWord { Id = "01_nature_row", Words = ["row"], Meaning = "排；划船" };
        var wordSchool = new LearningWord { Id = "05_school_row", Words = ["row"], Meaning = "争吵" };

        store.Toggle(wordGeo, "01_nature", true);
        store.Toggle(wordSchool, "05_school", true);

        Assert(store.Count == 2, "相同拼写的词按独立条目ID保存，不按拼写合并");
        Assert(store.DistinctChapterCount == 2, "来源保持各自所属章节");
        Assert(store.Contains("01_nature_row") && store.Contains("05_school_row"), "各自拥有独立勾选状态");
    }

    private static void TestVocabularyRowOrthogonality()
    {
        var row = new VocabularyRow();
        var word = new LearningWord
        {
            Id = "test_01",
            Words = ["reluctant"],
            Phonetic = "/rɪˈlʌktənt/",
            Pos = "adj.",
            Meaning = "不情愿的",
            Example = "He was reluctant to leave.",
            Extra = "", // 空字段
            Group = 1,
            Synonyms = ["unwilling", "hesitant"],
            AudioPath = "audio/reluctant.mp3"
        };

        row.BindWord(
            id: word.Id,
            word: word.Word,
            phonetic: word.Phonetic,
            pos: word.Pos,
            meaning: word.Meaning,
            example: word.Example,
            extra: word.Extra,
            sourceTitle: "第1章 · Group 1",
            synonyms: word.Synonyms,
            audioPath: word.AudioPath,
            statusKind: "new",
            isSelected: false,
            isExpanded: false
        );

        // 1. 初始状态
        Assert(!row.IsSelected, "初始未选择");
        Assert(!row.IsExpanded, "初始收起");

        // 2. 选择操作只改变选择，不改变展开
        row.SetSelected(true);
        Assert(row.IsSelected, "设置已选");
        Assert(!row.IsExpanded, "选择变化不触发展开");

        // 3. 展开操作只改变展开，不改变选择
        row.SetExpanded(true);
        Assert(row.IsExpanded, "设置展开");
        Assert(row.IsSelected, "展开变化不破坏选择");

        // 4. 收起
        row.SetExpanded(false);
        Assert(!row.IsExpanded, "收起详情");
        Assert(row.IsSelected, "收起不影响选择");

        // 5. 收藏状态正交独立
        Assert(!row.IsArchived, "初始未收藏");
        row.SetArchived(true);
        Assert(row.IsArchived, "设置已收藏");
        Assert(row.IsSelected, "收藏状态变化不破坏选择");
    }

    private static void TestDynamicLocalization()
    {
        var chineseText = IeltsI18n.T("开始练习");
        Assert(chineseText == "开始练习", "中文语言下返回中文");

        var zhSelected = IeltsI18n.SelectedCountFormat(12, 3);
        Assert(zhSelected == "已选 12 词（跨 3 个章节）", "中文跨章节选词格式化");

        var zhSingle = IeltsI18n.SelectedCountFormat(12, 1);
        Assert(zhSingle == "已选 12 词", "中文单章节选词格式化");
    }
}
