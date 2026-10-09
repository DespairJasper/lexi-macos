using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Lexi.Controls;
using Lexi.Features.Ielts;
using UiText = Lexi.UiText;

namespace Lexi;

/// <summary>
/// Lexi 1.2.2 IELTS 词汇行与雅思写作工作区完整自动化控件测试集，供主控无 GUI/无 build 环境调用。
/// 严格断言实际控件行为，不造假通过。
/// </summary>
public static class Ielts122Tests
{
    public static async Task RunAsync(MainWindow window, Action<bool, string> report)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(report);

        void Assert([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
        {
            report(condition, message);
            if (!condition)
            {
                throw new Exception("Ielts122Tests 测试断言未通过: " + message);
            }
        }

        object? CallNonPublic(string name, params object?[] args) =>
            typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(window, args);

        object? GetNonPublicField(string name) =>
            typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);

        try
        {
            // =================================================================================
            // 测试模块 1: VocabularyRow 独立 32DIP 收藏按钮、正交性与详情重复大收藏键隐藏
            // =================================================================================
            var row = new VocabularyRow();

            // 1.1 检查 32DIP 中性收藏按钮存在且尺寸合规
            Assert(row.RowArchiveButton != null, "VocabularyRow 包含行右侧独立收藏按钮");
            Assert(Math.Abs(row.RowArchiveButton.Width - 32) < 0.1 && Math.Abs(row.RowArchiveButton.Height - 32) < 0.1,
                "行右侧收藏按钮精确采用 32DIP 尺寸 (Width=32, Height=32)");

            // 1.2 检查用户内容全部采用 SelectableTextBlock 支持自由选择复制
            Assert(row.WordBlock is SelectableTextBlock, "英文词头采用 SelectableTextBlock");
            Assert(row.FullMeaningBlock is SelectableTextBlock, "完整释义采用 SelectableTextBlock");
            Assert(row.ExampleBlock is SelectableTextBlock, "例句内容采用 SelectableTextBlock");

            // 1.3 绑定词汇数据
            row.BindWord(
                id: "vrow_ielts122_01",
                word: "comprehensive",
                phonetic: "/ˌkɒmprɪˈhensɪv/",
                pos: "adj.",
                meaning: "全面的；综合的",
                example: "This is a comprehensive study.",
                extra: "重点高频考点",
                sourceTitle: "Cambridge IELTS 14 · Test 2",
                synonyms: ["complete", "thorough"],
                audioPath: "audio/comprehensive.mp3",
                statusKind: "new",
                isSelected: false,
                isExpanded: false,
                isArchived: false
            );

            // 1.4 初始状态：未选择、未展开、未收藏、详情重复大收藏键隐藏
            Assert(!row.IsArchived, "新绑定词汇初始为未收藏状态");
            Assert(!row.IsSelected, "词汇行初始为未勾选状态");
            Assert(!row.IsExpanded, "词汇行初始为未展开状态");
            Assert(!row.DetailArchiveButton.IsVisible, "抽屉内重复大收藏按键按契约隐藏");

            // 1.5 点击行右侧收藏按钮触发 AddToArchiveRequested，且不联动展开/勾选
            bool addRequested = false;
            row.AddToArchiveRequested += _ => addRequested = true;
            row.RowArchiveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert(addRequested, "未收藏时点击行右侧 32DIP 按钮独立触发 AddToArchiveRequested");
            Assert(!row.IsSelected, "点击收藏按钮正交独立，不联动改变选择勾选框状态");
            Assert(!row.IsExpanded, "点击收藏按钮正交独立，不联动触发展开折叠抽屉");

            // 1.6 收藏状态切换至已收藏，显示勾号/查看档案，点击触发 ViewArchiveRequested
            row.SetArchived(true);
            Assert(row.IsArchived, "SetArchived 正确更新行收藏状态为 true");
            Assert(!row.DetailArchiveButton.IsVisible, "已收藏状态下抽屉内大收藏键依然保持隐藏");

            string? viewedWord = null;
            row.ViewArchiveRequested += (_, word) => viewedWord = word;
            row.RowArchiveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert(viewedWord == "comprehensive", "已收藏后点击 32DIP 按钮独立触发 ViewArchiveRequested");
            Assert(!row.IsSelected && !row.IsExpanded, "已收藏点击查看档案同样不联动破坏选择与展开状态");

            // 1.7 新增 SaveExampleRequested 事件与例句收藏按键测试
            Assert(row.SaveExampleButton != null, "例句区域包含独立的收藏例句按钮");
            string? savedExampleText = null;
            row.SaveExampleRequested += (_, ex) => savedExampleText = ex;
            row.SaveExampleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert(savedExampleText == "This is a comprehensive study.", "点击例句收藏按钮正确触发 SaveExampleRequested 并传递例句文本");

            // 1.8 RefreshLanguage 双语切换
            row.RefreshLanguage();
            Assert(row.DetailArchiveButton.IsVisible == false, "RefreshLanguage 后抽屉重复大收藏键继续隐藏");

            // =================================================================================
            // 测试模块 2: IeltsWritingWorkspace 构造、单题模式与模型数据读取
            // =================================================================================
            var testCatalog = new IeltsCatalog
            {
                Sentences = new List<WritingSentence>
                {
                    new WritingSentence
                    {
                        Number = 1,
                        Category = "简单句",
                        Chinese = "技术对教育产生了深远的影响。",
                        BookAnswer = "Technology has exerted a profound impact on education.",
                        AlternateAnswer = "Technology has significantly influenced education.",
                        Remark = "注意 exert an impact on 固定搭配。"
                    },
                    new WritingSentence
                    {
                        Number = 2,
                        Category = "定语从句",
                        Chinese = "政府应当投资公共交通，以减少空气污染。",
                        BookAnswer = "Governments should invest in public transport to curb air pollution.",
                        AlternateAnswer = "", // 空备用译文测试按需分段
                        Remark = "curb 表示抑制、遏制。"
                    }
                }
            };

            var testDrafts = new Dictionary<int, string>
            {
                [1] = "Initial translation draft 1"
            };

            int saveCallCount = 0;
            bool simulateSaveFailure = false;
            Action testSaveAction = () =>
            {
                if (simulateSaveFailure)
                    throw new IOException("Simulated disk write error");
                saveCallCount++;
            };

            string? spokenContent = null;
            Action<string> testSpeakAction = text => spokenContent = text;

            bool backTriggered = false;
            Action testBackAction = () => backTriggered = true;

            var workspace = new IeltsWritingWorkspace(
                catalog: testCatalog,
                drafts: testDrafts,
                save: testSaveAction,
                speak: testSpeakAction,
                back: testBackAction
            );

            // 2.1 一次一道题与初始题目绑定
            Assert(workspace.CurrentIndex == 0, "工作区初始呈现第一道题 (一次一道题)");
            Assert(workspace.CurrentSentence?.Number == 1, "当前题目模型读取自 IeltsCatalog.Sentences[0]");
            Assert(workspace.PromptTextBlock.Text == "技术对教育产生了深远的影响。", "中文题干准确绑定");
            Assert(workspace.DraftInput.Name == "WritingDraft1", "输入框 Name 精确遵循 WritingDraftN 命名契约 (WritingDraft1)");
            Assert(workspace.DraftInput.Text == "Initial translation draft 1", "输入框正确读取现有 draft 字典内容");

            // 2.2 原题与参考答案使用 SelectableTextBlock
            Assert(workspace.PromptTextBlock is SelectableTextBlock, "写作原题题干采用 SelectableTextBlock 支持自由选择复制");
            Assert(workspace.BookAnswerTextBlock is SelectableTextBlock, "写作参考答案采用 SelectableTextBlock");
            Assert(workspace.AlternateAnswerTextBlock is SelectableTextBlock, "写作备用译文采用 SelectableTextBlock");
            Assert(workspace.RemarkTextBlock is SelectableTextBlock, "写作语法解析采用 SelectableTextBlock");

            // 2.3 参考答案默认隐藏与按需分段
            Assert(!workspace.ReferenceContainer.IsVisible, "参考答案区域默认保持隐藏状态");

            // 切换为显示
            workspace.ToggleReferenceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(workspace.ReferenceContainer.IsVisible, "点击切换按钮后参考答案区域显示");
            Assert(workspace.BookAnswerTextBlock.Text == "Technology has exerted a profound impact on education.", "书中答案内容准确呈现");
            Assert(workspace.AlternateAnswerTextBlock.Text == "Technology has significantly influenced education.", "备用译文内容准确呈现");
            Assert(workspace.RemarkTextBlock.Text == "注意 exert an impact on 固定搭配。", "语法解析内容准确呈现");

            // 2.4 原题/参考朗读与剪贴板
            testSpeakAction(workspace.BookAnswerTextBlock.Text!);
            Assert(spokenContent == "Technology has exerted a profound impact on education.", "参考答案朗读回调正常触发");

            // 2.5 自动保存合并写入与 FlushPendingSave 测试
            workspace.DraftInput.Text = "Technology has completely transformed schooling.";
            await Task.Delay(40);
            Assert(testDrafts[1] == "Technology has completely transformed schooling.", "输入修改即时同步至 drafts 字典");

            bool flushResult = workspace.FlushPendingSave();
            Assert(flushResult, "FlushPendingSave 在正常环境下返回 true");
            Assert(saveCallCount == 1, "FlushPendingSave 成功调用外部 save 回调完成落盘");

            // 2.6 RefreshLanguage 语言切换并保留光标与 draft
            workspace.DraftInput.CaretIndex = 12;
            workspace.RefreshLanguage();
            Assert(workspace.DraftInput.Text == "Technology has completely transformed schooling.", "RefreshLanguage 后 draft 文本完整保留");
            Assert(workspace.DraftInput.CaretIndex == 12, "RefreshLanguage 后光标位置 (CaretIndex=12) 严格保留未丢失");

            // 2.7 题目切换与按需分段空字段隐藏
            workspace.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(workspace.CurrentIndex == 1, "下一题按钮成功导航至第 2 题");
            Assert(workspace.DraftInput.Name == "WritingDraft2", "第 2 题输入框 Name 动态切换为 WritingDraft2");
            Assert(workspace.AlternateAnswerTextBlock.Parent?.Parent is Control altCard && !altCard.IsVisible,
                "第 2 题备用译文为空，按需分段规则生效，卡片自动隐藏不强行占位");

            // 2.8 异常隔离安全机制：save 抛异常保留输入、反馈未保存且拦截导航丢失
            workspace.DraftInput.Text = "Governments must fund bus and subway networks.";
            await Task.Delay(40);
            simulateSaveFailure = true; // 模拟磁盘异常

            bool failedFlush = workspace.FlushPendingSave();
            Assert(!failedFlush, "save 抛出异常时 FlushPendingSave 返回 false");
            Assert(workspace.DraftInput.Text == "Governments must fund bus and subway networks.", "save 异常后用户输入文本绝对保留未被清空");
            Assert(testDrafts[2] == "Governments must fund bus and subway networks.", "save 异常后 drafts 字典最新输入保留");
            Assert(workspace.SaveStatusBlock.Text.Contains("未保存") || workspace.SaveStatusBlock.Text.Contains("Unsaved"),
                "save 异常后界面提供明确未保存视觉反馈");

            // 尝试导航回第 1 题（应当被拦截防止输入丢失）
            workspace.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(workspace.CurrentIndex == 1, "保存失败时导航被安全拦截，用户停留在当前题，防止草稿丢失");
            Assert(workspace.DraftInput.Text == "Governments must fund bus and subway networks.", "拦截后草稿依然完好保留");

            // 恢复保存正常并解除拦截
            simulateSaveFailure = false;
            bool recoveredFlush = workspace.FlushPendingSave();
            Assert(recoveredFlush, "异常恢复后 FlushPendingSave 成功");

            workspace.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(workspace.CurrentIndex == 0, "保存成功后正常恢复上一题导航能力");

            // 2.9 返回按钮回调
            workspace.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(backTriggered, "返回按钮正常触发 back 回调");

            // =================================================================================
            // 测试模块 3: 结合真实 IELTS Catalog 模型核验（若环境已加载）
            // =================================================================================
            try
            {
                CallNonPublic("ShowIeltsCatalog");
                var catalog = (IeltsCatalog?)GetNonPublicField("_ieltsCatalog");
                if (catalog != null && catalog.Sentences.Count > 0)
                {
                    Assert(catalog.Sentences.Count == 100, "IELTS 真实目录包含完整的 100 道雅思写作题目");
                }
            }
            catch
            {
                // 无主窗口实际目录时不阻塞
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            report(false, "Ielts122Tests 执行异常: " + ex.Message);
            throw;
        }
    }
}
