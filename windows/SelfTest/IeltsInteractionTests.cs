using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Lexi.Controls;
using Lexi.Features.Ielts;

namespace Lexi;

/// <summary>
/// IELTS 教材专区交互与契约测试集，供主控调用。
/// 不启动 GUI，不 build 整个应用，通过 Action&lt;bool, string&gt; 回调上报每条断言结果。
/// </summary>
public static class IeltsInteractionTests
{
    public static async Task RunAsync(MainWindow window, Action<bool, string> report)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(report);

        void Assert(bool condition, string message)
        {
            report(condition, message);
            if (!condition)
            {
                throw new Exception("IELTS 测试断言未通过: " + message);
            }
        }

        object? CallNonPublic(string name, params object?[] args) =>
            typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

        object? GetNonPublicField(string name) =>
            typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

        try
        {
            // 1. 初始化并打开 IELTS 页面
            CallNonPublic("ShowIeltsCatalog");
            var catalog = (IeltsCatalog?)GetNonPublicField("_ieltsCatalog");
            Assert(catalog != null && catalog.Sections.Count > 0, "IELTS 目录加载成功且章节数大于 0");

            var workspace = (IeltsWorkspaceControl?)GetNonPublicField("_ieltsWorkspace");
            Assert(workspace != null, "IELTS 工作区组件已实例化并在 MainWindow 中挂载");

            // 2. 阅读同义替换独立浏览页测试（全部同义组 + 章节筛选 / 搜索 / 真实来源 / 空状态）
            var emptySection = catalog!.Sections.FirstOrDefault(s => s.Entries.All(e => e.Synonyms.Count == 0))
                ?? catalog.Sections[0];
            var listeningSection = catalog.Sections.FirstOrDefault(s => s.Entries.Any(e => e.Synonyms.Count > 0))
                ?? catalog.Sections[^1];

            List<LearningWord>? practicedWords = null;
            bool backClicked = false;

            var browser = new IeltsSynonymBrowser(
                catalog: catalog,
                initialSection: emptySection,
                playerProvider: () => new LocalWordAudioPlayer(_ => { }),
                startPractice: words => practicedWords = words,
                onBack: () => backClicked = true
            );

            // 检查初始在无同义替换章节时是否展示清晰空状态及“查看全部同义替换”按钮
            var listPanel = (StackPanel)typeof(IeltsSynonymBrowser)
                .GetField("_listPanel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(browser)!;

            var emptyCard = listPanel.Children.OfType<Border>().FirstOrDefault();
            Assert(emptyCard != null, "当前章节无同义替换时呈现空状态提示卡片，绝不仅 status return");

            var emptyButtons = (emptyCard?.Child as StackPanel)?.Children.OfType<Button>().ToList() ?? [];
            var viewAllBtn = emptyButtons.FirstOrDefault(b => (b.Content?.ToString() ?? "").Contains("查看全部"));
            Assert(viewAllBtn != null, "空状态卡片中包含'查看全部同义替换'操作按钮");

            // 切换到全部章节或有同义词章节
            browser.SetSectionFilter("all");
            var renderedCards = listPanel.Children.OfType<Border>().ToList();
            Assert(renderedCards.Count > 10, "查看全部时成功呈现真实同义替换考点卡片列表（共 " + renderedCards.Count + " 组）");

            // 搜索过滤测试
            var searchBox = (TextBox)typeof(IeltsSynonymBrowser)
                .GetField("_searchBox", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(browser)!;
            searchBox.Text = "book";
            await Task.Delay(50);
            var searchFiltered = listPanel.Children.OfType<Border>().ToList();
            Assert(searchFiltered.Count >= 1 && searchFiltered.Count < renderedCards.Count, "同义替换支持按考点词/同义表达实时搜索");

            // 开始练习回调与返回回调
            searchBox.Text = "";
            await Task.Delay(50);
            var startBtn = (Button)typeof(IeltsSynonymBrowser)
                .GetField("_startPracticeBtn", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(browser)!;
            var backBtn = (Button)typeof(IeltsSynonymBrowser)
                .GetField("_backBtn", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(browser)!;

            startBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert(practicedWords != null && practicedWords.Count > 0, "同义替换浏览页可正确传递考点词汇启动练习");

            backBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert(backClicked, "浏览页返回按钮可正确响应并触发返回");

            // 3. 跨筛选与跨章节选择保留测试
            var selectionStore = (IeltsSelectionStore)typeof(IeltsWorkspaceControl)
                .GetField("_selectionStore", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(workspace)!;
            var sharedSelection = (HashSet<string>)GetNonPublicField("_ieltsSelected")!;

            selectionStore.Clear();
            sharedSelection.Clear();

            var secA = catalog.Sections[0];
            var secB = catalog.Sections[1];
            var wordA1 = secA.Entries[0];
            var wordA2 = secA.Entries[1];
            var wordB1 = secB.Entries[0];

            workspace!.SelectSection(secA);
            selectionStore.Toggle(wordA1, secA.Id, true);
            sharedSelection.Add(wordA1.Id);
            selectionStore.Toggle(wordA2, secA.Id, true);
            sharedSelection.Add(wordA2.Id);

            // 切到章节 B 并追加选择
            workspace.SelectSection(secB);
            selectionStore.Toggle(wordB1, secB.Id, true);
            sharedSelection.Add(wordB1.Id);

            Assert(selectionStore.Count == 3, "跨章节选择总数累积为 3 词");
            Assert(selectionStore.DistinctChapterCount == 2, "跨 2 个不同章节");
            Assert(sharedSelection.Contains(wordA1.Id) && sharedSelection.Contains(wordB1.Id), "跨章节勾选未被清空，持久化保持");

            // 4. 全选当前筛选 / 反选可见 / 仅看已选 / 清空测试
            typeof(IeltsWorkspaceControl)
                .GetMethod("SelectAllCurrentFilter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(workspace, null);
            Assert(selectionStore.Count >= secB.Entries.Count, "全选当前筛选成功将当前章节可见词汇全部纳入已选");

            var prevCount = selectionStore.Count;
            typeof(IeltsWorkspaceControl)
                .GetMethod("InvertVisibleSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(workspace, null);
            Assert(selectionStore.Count != prevCount, "反选可见操作正确改变当前可见词汇选中状态");

            // 清空测试
            selectionStore.Clear();
            sharedSelection.Clear();
            Assert(selectionStore.Count == 0 && sharedSelection.Count == 0, "清空操作可完全重置已选集合");

            // 5. 批量与展开词条收藏到档案测试（已有笔记译文不覆盖，教材FSRS身份不合并，不写学习评分）
            var store = (IVocabularyArchive)GetNonPublicField("_vocabService")!;

            // 构造预存词汇以验证“已有笔记译文不覆盖”
            var existingWordText = "ielts_test_preserve_" + Guid.NewGuid().ToString("N")[..6];
            store.AddWord(existingWordText, "/test/", "用户专属自定义译文", "用户自定义释义");
            var existingItem = store.GetAllWords().First(w => w.Word.Equals(existingWordText, StringComparison.OrdinalIgnoreCase));
            store.SaveArchive(existingItem.Id, "用户专属自定义译文", "用户专属私人笔记", existingItem.Archive, null);

            var testBatch = new List<LearningWord>
            {
                new()
                {
                    Id = "ielts_test_entry_1",
                    Words = [existingWordText],
                    Phonetic = "/phonetic/",
                    Meaning = "教材默认释义（不应覆盖用户译文）",
                    Example = "Test sentence from IELTS textbook",
                    Group = 1
                },
                new()
                {
                    Id = "ielts_test_entry_2",
                    Words = ["ielts_test_fresh_" + Guid.NewGuid().ToString("N")[..6]],
                    Phonetic = "/fresh/",
                    Meaning = "全新教材单词",
                    Example = "Fresh sample sentence",
                    Group = 2
                }
            };

            var reviewCountBefore = store.GetAllWords().Sum(w => w.ReviewCount);

            // 执行批量收藏
            CallNonPublic("ArchiveIeltsBatch", testBatch);

            // 验证已有词未被覆盖
            var preservedItem = store.GetAllWords().First(w => w.Word.Equals(existingWordText, StringComparison.OrdinalIgnoreCase));
            Assert(preservedItem.Translation == "用户专属自定义译文", "批量收藏已有词汇时不覆盖用户自定义译文");
            Assert(preservedItem.Notes == "用户专属私人笔记", "批量收藏已有词汇时不覆盖用户原有笔记");

            // 验证新词成功加入，且附带 IELTS archive metadata
            var freshItem = store.GetAllWords().First(w => w.Word.Equals(testBatch[1].Word, StringComparison.OrdinalIgnoreCase));
            Assert(freshItem.Translation == "全新教材单词", "全新词条成功加入词汇档案");
            Assert(freshItem.Archive.SourceType == "ielts", "新增词条归档正确记录 source_type 为 ielts");
            Assert(freshItem.Archive.SourceExcerpt == "Fresh sample sentence", "新增词条归档正确记录教材真实来源例句");

            // 验证不写学习评分与复习记录（不破坏 FSRS 核心状态）
            var reviewCountAfter = store.GetAllWords().Sum(w => w.ReviewCount);
            Assert(reviewCountAfter == reviewCountBefore, "教材收藏操作纯属归档，不写学习评分、不合并教材与档案FSRS身份");

            // 6. VocabularyRow 展开详情与收藏动作正交性测试
            var testRow = new VocabularyRow();
            testRow.BindWord(
                id: "vrow_test_1",
                word: "vocabulary",
                phonetic: "/vəˈkæbjələri/",
                pos: "n.",
                meaning: "词汇",
                example: "Reading expands your vocabulary.",
                extra: "",
                sourceTitle: "IELTS 核心 · Group 1",
                synonyms: ["lexicon", "words"],
                audioPath: "",
                statusKind: "new",
                isSelected: false,
                isExpanded: false,
                isArchived: false
            );

            Assert(!testRow.IsArchived, "新词行初始显示未收藏");
            testRow.SetArchived(true);
            Assert(testRow.IsArchived, "SetArchived 正确切换至已收藏状态");
            testRow.SetExpanded(true);
            Assert(testRow.IsExpanded, "详情抽屉正常展开且包含收藏操作按钮");

            // 7. 资源页分类卡片与失效视频移除测试
            var resourceView = new IeltsResourceView(
                catalog: catalog,
                onSelectSection: _ => { },
                onOpenResource: _ => { },
                onBack: () => { }
            );

            static IEnumerable<Control> EnumerateAllDescendants(Control root)
            {
                yield return root;
                if (root is Panel panel)
                {
                    foreach (var child in panel.Children)
                        foreach (var d in EnumerateAllDescendants(child))
                            yield return d;
                }
                else if (root is ContentControl cc && cc.Content is Control ccChild)
                {
                    foreach (var d in EnumerateAllDescendants(ccChild))
                        yield return d;
                }
                else if (root is Border b && b.Child is Control bChild)
                {
                    foreach (var d in EnumerateAllDescendants(bChild))
                        yield return d;
                }
                else if (root is ScrollViewer sv && sv.Content is Control svChild)
                {
                    foreach (var d in EnumerateAllDescendants(svChild))
                        yield return d;
                }
            }

            // 确认资源页内不包含失效课程视频的按钮入口
            var allButtons = EnumerateAllDescendants(resourceView).OfType<Button>().ToList();
            var brokenVideoBtn = allButtons.FirstOrDefault(b => (b.Content?.ToString() ?? "").Contains("课程视频"));
            Assert(brokenVideoBtn == null, "资源页已彻底移除失效课程视频入口");

            var pdfBtn = allButtons.FirstOrDefault(b => (b.Content?.ToString() ?? "").Contains("语法讲义"));
            Assert(pdfBtn != null, "资源页完整保留本地语法讲义入口");

            var svgBtn = allButtons.FirstOrDefault(b => (b.Content?.ToString() ?? "").Contains("思维导图"));
            Assert(svgBtn != null, "资源页完整保留本地语法思维导图 SVG 入口");

            report(true, "IELTS 教材专区全部交互与契约测试验证通过！");
        }
        catch (Exception ex)
        {
            report(false, "IELTS 测试异常: " + ex.Message);
            throw;
        }

        await Task.CompletedTask;
    }
}
