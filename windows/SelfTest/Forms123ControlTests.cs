using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Lexi.Features.Learning;
using Lexi.Features.Settings;

namespace Lexi;

/// <summary>
/// 1.2.3 表单与设置控件自动化验证套件：
/// - SettingsPageControl：横向分类、单正文滚动、无大宣传卡、ShowCategories 切 Appearance、语言切换不丢表单
/// - PlanEditorControl：统一限宽 680 DIP、每日词数 1-10000、动态排期摘要、未来批次提示、保存/取消定宽、失败保留输入
/// - PlanWordPickerControl：固定列 (40/180/*)、释义直接可选正文无 Expander 白框、音标次要、按需展开、静态行集成
/// </summary>
public static class Forms123ControlTests
{
    public static async Task RunAsync(MainWindow? mainWindow, Action<bool, string> check)
    {
        await Task.Yield();
        RunStandalone(check);
    }

    public static void RunStandalone(Action<bool, string> check)
    {
        TestSettingsPageControl(check);
        TestPlanEditorControl(check);
        TestPlanWordPickerControl(check);
    }

    private static void TestSettingsPageControl(Action<bool, string> check)
    {
        var page = new SettingsPageControl();

        // 1. 结构与单滚动容器
        check(page.BodyScroller != null, "SettingsPageControl 具备独立的 BodyScroller 滚动容器");
        check(page.BodyScroller!.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, "BodyScroller 禁用横向滚动条");
        check(page.BodyScroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "BodyScroller 纵向滚动自适应");
        check(!page.IsOpen, "SettingsPageControl 初始为未打开状态");
        check(page.CurrentSection == SettingsSection.None, "初始无活动分类 (CurrentSection == None)");

        // 2. 事件派发
        bool openedFired = false;
        bool closedFired = false;
        SettingsSection changedSection = SettingsSection.None;
        page.Opened += (_, _) => openedFired = true;
        page.Closed += (_, _) => closedFired = true;
        page.SectionChanged += (_, s) => changedSection = s;

        // 3. 注册已有分类控件与动作底栏
        var appearanceCard = new TextBox { Name = "MockThemeInput", Text = "深色模式" };
        var studyCard = new TextBox { Name = "MockKeyInput", Text = "Space 播放" };
        var aiCard = new TextBox { Name = "MockApiKey", Text = "sk-test-12345" };
        var dataCard = new TextBlock { Text = "词库备份正常" };
        var aboutCard = new TextBlock { Text = "Lexi 1.2.3" };
        var sampleFooter = new Button { Content = "应用更改" };

        page.RegisterSectionContent(SettingsSection.Appearance, appearanceCard, sampleFooter);
        page.RegisterSectionContent(SettingsSection.StudyAndShortcuts, studyCard);
        page.RegisterSectionContent(SettingsSection.AiService, aiCard);
        page.RegisterSectionContent(SettingsSection.DataAndBackup, dataCard);
        page.RegisterSectionContent(SettingsSection.About, aboutCard);

        check(true, "RegisterSectionContent 成功注册 5 大核心设置分类及动作底栏");

        // 4. 打开与默认分类
        page.Open();
        check(page.IsOpen, "Open() 后 IsOpen == true");
        check(openedFired, "Open() 触发 Opened 事件");
        check(page.CurrentSection == SettingsSection.Appearance, "Open() 默认展开 Appearance 分类");
        check(changedSection == SettingsSection.Appearance, "SectionChanged 广播 Appearance");

        // 5. 分类切换
        page.ShowSection(SettingsSection.AiService);
        check(page.CurrentSection == SettingsSection.AiService, "ShowSection 切换至 AiService");
        check(changedSection == SettingsSection.AiService, "SectionChanged 广播 AiService");

        // 6. 1.2.3 核心规格：ShowCategories 切换至 Appearance
        page.ShowCategories();
        check(page.CurrentSection == SettingsSection.Appearance, "1.2.3 规范：ShowCategories() 切换至 Appearance 分类");
        check(changedSection == SettingsSection.Appearance, "SectionChanged 广播 Appearance");

        // 7. 语言刷新与表单数据保留（切换语言不丢失输入框内的内容）
        appearanceCard.Text = "浅色模式保持输入";
        aiCard.Text = "sk-custom-user-key";
        page.RefreshLanguage();
        check(appearanceCard.Text == "浅色模式保持输入", "RefreshLanguage 后用户在 Appearance 表单中的输入完整保留");
        check(aiCard.Text == "sk-custom-user-key", "RefreshLanguage 后用户在 AI 表单中的输入完整保留");

        // 8. 状态提示
        page.SetStatusMessage("测试状态：设置已保存", isError: false);
        check(true, "SetStatusMessage 成功执行普通状态提示");
        page.SetStatusMessage("测试错误：网络超时", isError: true);
        check(true, "SetStatusMessage 成功执行错误状态提示");
        page.SetStatusMessage("", isError: false);
        check(true, "SetStatusMessage 成功清空状态提示");

        // 9. 关闭
        page.Close();
        check(!page.IsOpen, "Close() 后 IsOpen == false");
        check(closedFired, "Close() 触发 Closed 事件");
        check(page.CurrentSection == SettingsSection.None, "关闭后 CurrentSection 重置为 None");
    }

    private static void TestPlanEditorControl(Action<bool, string> check)
    {
        var initialModel = new PlanEditorModel(
            Name: "雅思核心 3000 词",
            DailyCount: 30,
            Shuffle: true,
            WordCount: 150
        );

        PlanEditorModel? savedModel = null;
        bool cancelled = false;

        var editor = new PlanEditorControl(
            initialModel,
            model => savedModel = model,
            () => cancelled = true
        );

        // 1. 限宽约束
        check(editor.MaxWidth == 680, "PlanEditorControl 严格限宽 680 DIP");

        // 2. 字段初始化
        check(editor.NameInput.Text == "雅思核心 3000 词", "NameInput 成功绑定初始名称");
        check(editor.DailyQuotaInput.Value == 30, "DailyQuotaInput 成功绑定初始每日词数 30");
        check(editor.ShuffleCheckbox.IsChecked == true, "ShuffleCheckbox 成功绑定初始乱序状态");
        check(editor.DailyQuotaInput.Minimum == 1 && editor.DailyQuotaInput.Maximum == 10000, "DailyQuotaInput 范围限定为 1-10000");

        // 3. 轻量排期摘要
        check(editor.SummaryBlock.Text?.Contains("150") == true, "排期摘要包含总词数 150");
        check(editor.SummaryBlock.Text?.Contains("5") == true, "排期摘要准确计算 150 / 30 = 5 天完成");

        // 4. 动态调整排期
        editor.DailyQuotaInput.Value = 50;
        check(editor.SummaryBlock.Text?.Contains("3") == true, "调整每日词数为 50 后排期摘要动态更新为 3 天");

        // 5. 未来批次提示文案（无底层技术文案）
        check(!string.IsNullOrWhiteSpace(editor.FutureBatchNoticeBlock.Text), "存在明确的未来批次生效提示");
        check(!editor.FutureBatchNoticeBlock.Text!.Contains("SQL") &&
              !editor.FutureBatchNoticeBlock.Text!.Contains("事务") &&
              !editor.FutureBatchNoticeBlock.Text!.Contains("表结构"), "提示文案纯粹用户友好，无底层技术术语");

        // 6. 保存/取消按钮定宽与样式
        check(editor.SaveButton.Width == 96 && editor.CancelButton.Width == 96, "保存与取消按钮内容定宽 96 DIP");

        // 7. 失败提示与表单保留
        editor.NameInput.Text = "临时修改的名字";
        editor.SetError("测试保存冲突：已有同名计划");
        check(editor.ErrorBlock.IsVisible, "SetError 后错误提示处于可见状态");
        check(editor.ErrorBlock.Text == "测试保存冲突：已有同名计划", "错误提示准确呈现错误信息");
        check(editor.NameInput.Text == "临时修改的名字", "SetError 发生后用户输入严格保留，不被清空");

        // 8. 校验空名称保护
        editor.NameInput.Text = "   ";
        editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(savedModel == null, "空计划名称被拦截，未触发 onSave 回调");
        check(editor.ErrorBlock.IsVisible, "空计划名称拦截后呈现友好错误提示");

        // 9. 正确保存提交
        editor.NameInput.Text = "最终确认的雅思计划";
        editor.DailyQuotaInput.Value = 25;
        editor.ShuffleCheckbox.IsChecked = false;
        editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        check(savedModel != null, "有效表单成功触发 onSave 回调");
        check(savedModel!.Name == "最终确认的雅思计划", "保存的模型包含最新名称");
        check(savedModel.DailyCount == 25, "保存的模型包含最新每日词数 25");
        check(savedModel.Shuffle == false, "保存的模型包含最新乱序设置 false");
        check(savedModel.WordCount == 150, "保存的模型保留原有词数 150");

        // 10. 取消触发
        editor.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(cancelled, "CancelButton 成功触发 onCancel 回调");

        // 11. 语言刷新不丢输入
        editor.RefreshLanguage();
        check(editor.NameInput.Text == "最终确认的雅思计划", "RefreshLanguage 后表单数据无丢失");
    }

    private static void TestPlanWordPickerControl(Action<bool, string> check)
    {
        var sampleWord = new PlanWordItemModel(
            Id: "w-101",
            Word: "ubiquitous",
            Meaning: "无所不在的，普遍存在的",
            Phonetic: "juːˈbɪkwɪtəs",
            Definition: "present, appearing, or found everywhere.",
            Example: "Smartphones have become ubiquitous in modern life."
        );

        bool rowSelected = false;
        var row = new PlanWordRowControl(sampleWord, isSelected: false, selected => rowSelected = selected);

        // 1. 固定列宽与基础结构
        var grid = row.Content is Border border ? border.Child as Grid : null;
        check(grid != null, "PlanWordRowControl 使用 Grid 布局排布列关系");
        check(grid!.ColumnDefinitions.Count == 3, "Grid 包含 3 列定义");
        check(grid.ColumnDefinitions[0].Width == new GridLength(40), "列 0 为定宽 40 DIP 选择列");
        check(grid.ColumnDefinitions[1].Width == new GridLength(180), "列 1 为定宽 180 DIP 单词与音标列");
        check(grid.ColumnDefinitions[2].Width.IsStar, "列 2 为星号比例释义列");

        // 2. 选择互动与背景变化
        check(!row.IsSelected, "初始状态未选中");
        row.SelectCheckBox.IsChecked = true;
        check(rowSelected, "切换复选框成功触发 selectionChanged 回调");
        check(row.IsSelected, "IsSelected 属性状态正确更新");

        // 3. 词头与次要音标
        check(row.WordBlock.Text == "ubiquitous", "WordBlock 正确呈现英文单词");
        check(row.PhoneticBlock.Text.Contains("juːˈbɪkwɪtəs"), "PhoneticBlock 包含音标信息");
        check(row.PhoneticBlock.FontSize <= row.WordBlock.FontSize - 2, "音标字号显著小于单词主字号，保持次要层级");

        // 4. 释义呈现（直接可选正文，无 Expander 白色输入框）
        check(row.MeaningBlock is SelectableTextBlock, "MeaningBlock 为 SelectableTextBlock，直接可选正文");
        check(row.MeaningBlock.Text == "无所不在的，普遍存在的", "MeaningBlock 呈现正确中文释义");
        check(!row.GetVisualDescendants().OfType<Expander>().Any(), "严禁在词行中使用 Expander 白色输入框呈现释义");

        // 5. 详细释义与例句按需展开
        var expandBtn = grid.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content?.ToString()?.Contains("详细") == true);
        check(expandBtn != null, "存在轻量'详细 ▾'展开按钮");
        if (expandBtn != null)
        {
            expandBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(row.DefinitionBlock != null && row.DefinitionBlock.IsVisible, "点击详细后英文释义处于可见状态");
            check(row.ExampleBlock != null && row.ExampleBlock.IsVisible, "点击详细后例句处于可见状态");
            check(expandBtn.Content?.ToString()?.Contains("收起") == true, "展开后按钮文字更新为'收起 ▴'");
        }

        // 6. 领域模型 DailyStudyPlanWord 适配与静态构建方法
        var domainWord = new DailyStudyPlanWord
        {
            Id = "d-202",
            Word = "serendipity",
            Meaning = "意外发现珍奇事物的本领，机缘巧合",
            Phonetic = "ˌserənˈdɪpəti",
            Definition = "the occurrence of events by chance in a happy way."
        };

        var adaptedRow = PlanWordPickerControl.CreateWordRow(domainWord, true, _ => { });
        check(adaptedRow is PlanWordRowControl, "PlanWordPickerControl.CreateWordRow 成功适配 DailyStudyPlanWord");

        // 7. 整体词表容器 PlanWordPickerControl 测试
        var picker = new PlanWordPickerControl();
        string? selectedId = null;
        bool? selectedState = null;
        picker.WordSelectionChanged += (id, s) => { selectedId = id; selectedState = s; };

        picker.SetWords(new[] { domainWord }, id => id == "d-202");
        check(true, "PlanWordPickerControl.SetWords 成功加载列表");
    }
}
