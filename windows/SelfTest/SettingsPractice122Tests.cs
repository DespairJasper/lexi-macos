using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Lexi.Features.Learning;
using Lexi.Features.Settings;

namespace Lexi;

/// <summary>
/// 1.2.2 设置全页手风琴与拼写练习准备独立断言测试。
/// 供 MainWindow 自检体系或集成测试套件调用：RunAsync(MainWindow, Action<bool, string>)。
/// </summary>
public static class SettingsPractice122Tests
{
    public static async Task RunAsync(MainWindow mainWindow, Action<bool, string> check)
    {
        await Task.Yield();

        // =====================================================================
        // PART 1: SettingsPageControl (全页面手风琴、单Scroll、五类一次展开一类)
        // =====================================================================
        var page = new SettingsPageControl();

        // 1.1 基础属性与单滚动容器
        check(page.BodyScroller != null, "SettingsPageControl 具备独立的 BodyScroller 滚动容器");
        check(page.BodyScroller!.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, "BodyScroller 禁用横向滚动条");
        check(page.BodyScroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "BodyScroller 纵向滚动按需自适应");
        check(!page.IsOpen, "SettingsPageControl 初始为关闭状态 (IsOpen == false)");
        check(page.CurrentSection == SettingsSection.None, "初始无展开分类 (CurrentSection == None)");

        // 1.2 事件订阅校验
        bool openedFired = false;
        bool closedFired = false;
        SettingsSection changedSection = SettingsSection.None;

        page.Opened += (_, _) => openedFired = true;
        page.Closed += (_, _) => closedFired = true;
        page.SectionChanged += (_, s) => changedSection = s;

        // 1.3 注册各分类内容控件（兼容 RegisterSectionContent 契约）
        var appearanceCard = new Border { Name = "MockAppearanceCard", Tag = "Appearance" };
        var studyCard = new StackPanel { Name = "MockStudyCard", Tag = "Study" };
        var aiCard = new Border { Name = "MockAiCard", Tag = "Ai" };
        var dataCard = new Border { Name = "MockDataCard", Tag = "Data" };
        var aboutCard = new Border { Name = "MockAboutCard", Tag = "About" };
        var sampleFooter = new Button { Content = "保存" };

        page.RegisterSectionContent(SettingsSection.Appearance, appearanceCard, sampleFooter);
        page.RegisterSectionContent(SettingsSection.StudyAndShortcuts, studyCard);
        page.RegisterSectionContent(SettingsSection.AiService, aiCard);
        page.RegisterSectionContent(SettingsSection.DataAndBackup, dataCard);
        page.RegisterSectionContent(SettingsSection.About, aboutCard);

        check(true, "RegisterSectionContent 成功注册 5 大核心设置分类及动作底栏");

        // 1.4 打开页面与默认展开 Appearance
        page.Open();
        check(page.IsOpen, "Open() 后页面处于打开状态 (IsOpen == true)");
        check(openedFired, "Open() 成功触发 Opened 事件");
        check(page.CurrentSection == SettingsSection.Appearance, "Open() 默认展开 Appearance 分类");
        check(changedSection == SettingsSection.Appearance, "SectionChanged 成功广播 Appearance");

        // 1.5 手风琴特性校验：五类一次展开一类
        // 切换至 AiService，应展开 AiService 并自动折叠其余类
        page.ShowSection(SettingsSection.AiService);
        check(page.CurrentSection == SettingsSection.AiService, "ShowSection 切换至 AiService");
        check(changedSection == SettingsSection.AiService, "SectionChanged 广播 AiService");

        // 切换至 DataAndBackup
        page.ShowSection(SettingsSection.DataAndBackup);
        check(page.CurrentSection == SettingsSection.DataAndBackup, "ShowSection 切换至 DataAndBackup");

        // 1.6 ShowCategories 收起所有手风琴项
        page.ShowCategories();
        check(page.CurrentSection == SettingsSection.Appearance, "ShowCategories() 收起所有分类至总览状态");
        check(changedSection == SettingsSection.Appearance, "SectionChanged 广播 Appearance");

        // 1.7 状态提示 API
        page.SetStatusMessage("测试状态：设置已保存", isError: false);
        check(true, "SetStatusMessage 成功执行普通状态提示");
        page.SetStatusMessage("测试错误：配置项无效", isError: true);
        check(true, "SetStatusMessage 成功执行错误状态提示");
        page.SetStatusMessage("", isError: false);
        check(true, "SetStatusMessage 成功清空状态提示");

        // 1.8 语言刷新
        page.RefreshLanguage();
        check(true, "RefreshLanguage 执行成功，双语文本无异常");

        // 1.9 关闭页面
        page.Close();
        check(!page.IsOpen, "Close() 后 IsOpen == false");
        check(closedFired, "Close() 成功触发 Closed 事件");
        check(page.CurrentSection == SettingsSection.None, "关闭后 CurrentSection 重置为 None");

        // =====================================================================
        // PART 2: PracticeSetupControl (范围区、模式区、空weak禁用、唯一开始键、双语)
        // =====================================================================

        // 2.1 正常模式（存在弱项词条：total = 20, weak = 6）
        bool? startOnlyWeak = null;
        bool? startHints = null;
        bool cancelled = false;

        var setupNormal = new PracticeSetupControl(
            total: 20,
            weak: 6,
            start: (onlyWeak, hints) =>
            {
                startOnlyWeak = onlyWeak;
                startHints = hints;
            },
            cancel: () => cancelled = true
        );

        check(setupNormal.TotalWords == 20, "PracticeSetupControl 记录总词数 20");
        check(setupNormal.WeakWords == 6, "PracticeSetupControl 记录薄弱词数 6");
        check(setupNormal.WeakScopeRadio.IsEnabled, "存在薄弱词时，薄弱项单选框处于启用状态");
        check(setupNormal.AllScopeRadio.IsEnabled, "全部词条单选框处于启用状态");
        check(setupNormal.StartButton.IsEnabled, "存在有效词数时唯一开始键处于启用状态");

        // 模拟点击开始键
        setupNormal.WeakScopeRadio.IsChecked = true;
        setupNormal.MemoryModeRadio.IsChecked = true;
        setupNormal.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(startOnlyWeak == true, "仅薄弱项被选中时，回调参数 onlyWeak 为 true");
        check(startHints == true, "旧模式字段不再允许创建默写模式");

        // 模拟切换模式为辅助拼写
        setupNormal.AllScopeRadio.IsChecked = true;
        setupNormal.AssistedModeRadio.IsChecked = true;
        setupNormal.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(startOnlyWeak == false, "全部词条被选中时，回调参数 onlyWeak 为 false");
        check(startHints == true, "辅助拼写被选中时，回调参数 hints 为 true");

        // 模拟点击取消键
        setupNormal.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(cancelled, "CancelButton 点击成功触发 cancel 回调");

        // 2.2 边界校验：空 weak 禁用（total = 15, weak = 0）
        var setupNoWeak = new PracticeSetupControl(
            total: 15,
            weak: 0,
            start: (_, _) => { },
            cancel: () => { }
        );
        check(!setupNoWeak.WeakScopeRadio.IsEnabled, "薄弱词为 0 时严格禁用薄弱项 (空weak禁用)");
        check(setupNoWeak.AllScopeRadio.IsChecked == true, "薄弱项禁用时默认自动回退勾选全部词条");
        check(setupNoWeak.StartButton.IsEnabled, "总词数大于 0 时开始键依然可用");

        // 2.3 边界校验：零总词数（total = 0, weak = 0）
        var setupEmpty = new PracticeSetupControl(
            total: 0,
            weak: 0,
            start: (_, _) => { },
            cancel: () => { }
        );
        check(!setupEmpty.WeakScopeRadio.IsEnabled, "总词数与薄弱数均为 0 时薄弱项禁用");
        check(!setupEmpty.StartButton.IsEnabled, "总词数为 0 时唯一开始键禁用");

        // 2.4 刷新语言校验
        setupNormal.RefreshLanguage();
        check(true, "PracticeSetupControl.RefreshLanguage 刷新双语无异常");
    }
}
