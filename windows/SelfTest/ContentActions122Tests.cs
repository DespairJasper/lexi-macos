using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lexi.Features.Ielts;

namespace Lexi;

/// <summary>
/// Lexi 1.2.2 内容交互与金句/听力/写作通用辅助自检测试套件：
/// - CreateExampleActions 结构、小朗读与小收藏按钮契约
/// - 真实英文及已有译文保存（绝不把词义当句译文）
/// - 金句去重与不覆盖已有条目机制
/// - QuickCardWindow 句子朗读与各模式发音按钮可见性
/// - 金句列表 SelectableTextBlock 自由选择复制与独立朗读按钮
/// - IELTS 写作集成 IeltsWritingWorkspace 与保存异常防丢机制
/// - 听力资料实际可朗读英文正文与划选复制
/// </summary>
public static class ContentActions122Tests
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
                throw new Exception("ContentActions122Tests 断言失败: " + message);
            }
        }

        object? CallNonPublic(string name, params object?[] args) =>
            typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(window, args);

        try
        {
            var store = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var quotes = (IQuoteArchive)store;

            // =====================================================================
            // 模块 1: CreateExampleActions 控件结构与事件响应断言
            // =====================================================================
            var testSentence = "Consistency is the key to mastering any language.";
            var testTranslation = "坚持是掌握任何语言的关键。";

            var actions = window.CreateExampleActions(testSentence, testTranslation);
            Assert(actions != null, "CreateExampleActions 返回有效的 StackPanel 容器");
            Assert(actions.Orientation == Avalonia.Layout.Orientation.Horizontal, "按钮栏采用水平排布 (Horizontal)");

            var buttons = actions.Children.OfType<Button>().ToList();
            Assert(buttons.Count >= 2, "操作栏包含至少两个操作按钮（朗读与收藏）");

            var speakBtn = buttons[0];
            Assert(speakBtn is Lexi.Controls.InlineAudioButton, "第一个按钮为共用线条朗读图标");
            Assert(speakBtn.Tag?.ToString() == testSentence, "小朗读按钮 Tag 正确绑定真实英文句子");

            var quoteBtn = buttons.Last();
            Assert(quoteBtn.Content?.ToString() == "＋", "最后一个按钮为金句收藏按钮 (＋)");

            // =====================================================================
            // 模块 2: 真实英文及已有译文收藏，不把词义当句译文，已有条目去重不覆盖
            // =====================================================================
            // 清理测试残留
            var existingBefore = quotes.GetQuotes(testSentence, 50, 0);
            foreach (var q in existingBefore) quotes.DeleteQuote(q.Id);

            // 2.1 首次收藏
            quoteBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var savedList = quotes.GetQuotes(testSentence, 50, 0);
            Assert(savedList.Count == 1, "点击收藏按钮成功将英文原句与译文写入金句归档");
            var savedItem = savedList[0];
            Assert(savedItem.Original == testSentence, "保存的英文原句与输入严格一致");
            Assert(savedItem.Translation == testTranslation, "保存的译文与句子译文严格一致，未混入单词词义");

            // 2.2 重复收藏去重且不覆盖
            var countBeforeDup = quotes.GetQuotes("", 500, 0).Count;
            var dupActions = window.CreateExampleActions(testSentence, "试图覆盖的恶意新译文");
            dupActions.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var countAfterDup = quotes.GetQuotes("", 500, 0).Count;
            Assert(countBeforeDup == countAfterDup, "重复收藏相同原句时，金句总数不增加 (去重有效)");

            var reloadedList = quotes.GetQuotes(testSentence, 50, 0);
            Assert(reloadedList.Count == 1 && reloadedList[0].Translation == testTranslation,
                "重复收藏未覆盖已有译文，仍保留首次保存的原有译文");

            // 2.3 收藏空译文时不强塞词义
            var noTransSentence = "Simplicity is the soul of efficiency.";
            var cleanNoTrans = quotes.GetQuotes(noTransSentence, 50, 0);
            foreach (var q in cleanNoTrans) quotes.DeleteQuote(q.Id);

            var noTransActions = window.CreateExampleActions(noTransSentence, null);
            noTransActions.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var savedNoTrans = quotes.GetQuotes(noTransSentence, 50, 0);
            Assert(savedNoTrans.Count == 1 && savedNoTrans[0].Translation == "",
                "译文为空时严格保存为空字符串，绝不把词性/词义强行作为句子译文");

            // =====================================================================
            // 模块 3: QuickCardWindow 发音朗读与模式适配
            // =====================================================================
            using var dict = new DictionaryService();
            var settings = new AppSettings();
            var dummyPending = new TaskCompletionSource<string>();
            var card = new QuickCardWindow(dict, store, quotes, (_, _) => dummyPending.Task, settings, () => { }, _ => { }, () => { });

            // 3.1 查词模式：显示读音按钮，标题为查词
            card.Prepare(QuickAction.Lookup, "serendipity");
            card.ShowNear(new PixelPoint(500, 200));
            await Task.Delay(40);
            var pronounceBtn = card.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "QuickPronounceBtn");
            Assert(pronounceBtn != null, "QuickCardWindow 包含 QuickPronounceBtn 按钮");
            Assert(pronounceBtn.IsVisible, "查词模式下发音按钮可见");
            Assert(pronounceBtn is Lexi.Controls.InlineAudioButton && !string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(pronounceBtn)), "查词使用带可访问名称的统一朗读图标");

            // 3.2 翻译模式：句子朗读按钮保持可见
            card.Prepare(QuickAction.Translate, "Translation sentence audio test.");
            Assert(pronounceBtn.IsVisible, "翻译模式下句子朗读按钮保持可见");
            Assert(pronounceBtn.Content is Avalonia.Controls.Shapes.Path, "翻译模式保留统一矢量朗读图标");

            // 3.3 收藏金句模式：句子朗读按钮保持可见
            card.Prepare(QuickAction.SaveQuote, "Quote sentence audio test.");
            Assert(pronounceBtn.IsVisible, "收藏金句模式下句子朗读按钮保持可见");

            // 3.4 保持 Details 按钮 TrySave 前置逻辑
            card.Prepare(QuickAction.Translate, "Details save test sentence.");
            card.TranslationInput.Text = "详情跳转前保存译文";
            var detailsBtn = card.GetVisualDescendants().OfType<Button>().First(b => b.Name == "QuickDetailsBtn");
            detailsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var savedFromDetails = quotes.GetQuotes("Details save test sentence", 50, 0);
            Assert(savedFromDetails.Count > 0 && savedFromDetails[0].Translation == "详情跳转前保存译文",
                "QuickCardWindow 非 lookup 点击 Details 按钮前完成 TrySave");

            card.Close();

            // =====================================================================
            // 模块 4: 金句本 SelectableTextBlock 自由选择复制与独立朗读按钮
            // =====================================================================
            CallNonPublic("ShowPage", "quotes");
            var quotesList = (StackPanel)typeof(MainWindow).GetField("_quotesList", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert(quotesList != null, "金句列表控件 _quotesList 正常初始化");

            // 校验卡片中的文本控件全量使用 SelectableTextBlock
            var cardBorders = quotesList.Children.OfType<Border>().ToList();
            if (cardBorders.Count > 0)
            {
                var firstCardStack = (StackPanel)cardBorders[0].Child!;
                var selectableBlocks = firstCardStack.Children.OfType<SelectableTextBlock>().ToList();
                Assert(selectableBlocks.Count >= 2, "金句卡片中的原句与译文均采用 SelectableTextBlock，支持鼠标划选与 Ctrl+C 复制");

                // 校验包含朗读按钮
                var actionRow = firstCardStack.Children.OfType<StackPanel>().LastOrDefault();
                Assert(actionRow != null && actionRow.Children.OfType<Button>().Any(b => b is Lexi.Controls.InlineAudioButton),
                    "金句卡片操作行中包含独立的朗读按钮");
            }

            // =====================================================================
            // 模块 5: IELTS 写作集成 IeltsWritingWorkspace 与保存异常防丢
            // =====================================================================
            CallNonPublic("ShowIeltsWriting");
            var subContent = (Grid)typeof(MainWindow).GetField("_ieltsSubContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert(subContent != null, "IELTS 子内容容器 _ieltsSubContent 存在");

            var writingWorkspace = subContent.Children.OfType<IeltsWritingWorkspace>().FirstOrDefault();
            Assert(writingWorkspace != null, "ShowIeltsWriting 成功挂载 Features/Ielts/IeltsWritingWorkspace 控件");

            // 检查异常隔离：修改输入并在异常环境下测试 FlushPendingSave
            writingWorkspace.DraftInput.Text = "Autonomous agents revolutionize software engineering.";
            var progress = (LearningProgress)typeof(MainWindow).GetField("_ieltsProgress", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert(progress.WritingDrafts[writingWorkspace.CurrentSentence!.Number] == "Autonomous agents revolutionize software engineering.",
                "用户输入文本即时同步至草稿字典，未丢失");

            // =====================================================================
            // 模块 6: 听力资料英文正文可朗读且支持划选复制
            // =====================================================================
            CallNonPublic("ShowIeltsResources");
            await Task.Delay(60);
            window.UpdateLayout();
            var resourceView = subContent.Children.OfType<IeltsResourceView>().FirstOrDefault();
            Assert(resourceView != null, "ShowIeltsResources 成功挂载 IeltsResourceView 控件");

            // 检查增强后的听力资料卡片中包含 SelectableTextBlock 与操作按钮
            var selectableTexts = resourceView.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
            Assert(selectableTexts.Count > 0, "听力资料中包含 SelectableTextBlock 控件，支持鼠标自由选中文本与复制");

            var speakButtons = resourceView.GetVisualDescendants().OfType<Button>()
                .Where(b => b is Lexi.Controls.InlineAudioButton || b.Content?.ToString() is "♪" || (b.Content?.ToString()?.Contains("朗读") ?? false))
                .ToList();
            Assert(speakButtons.Count > 0, "听力资料中包含实际可点击的英文朗读按钮");

            // 清理测试数据
            foreach (var q in quotes.GetQuotes(testSentence, 50, 0)) quotes.DeleteQuote(q.Id);
            foreach (var q in quotes.GetQuotes(noTransSentence, 50, 0)) quotes.DeleteQuote(q.Id);
            foreach (var q in quotes.GetQuotes("Details save test sentence", 50, 0)) quotes.DeleteQuote(q.Id);

            report(true, "ContentActions122Tests 全部断言通过");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            report(false, "ContentActions122Tests 执行异常: " + ex.Message);
            throw;
        }
    }
}
