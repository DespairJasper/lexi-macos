using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lexi.Features.Learning;
using Lexi.Shell;

namespace Lexi;

/// <summary>
/// 1.2.4 玻璃拟物化与局部材质 UI 回归测试：
/// 验证局部磨砂/半透明弹层下，主工作区背景始终保持锐利（Opacity == 1），
/// 排除历史全局快照将主背景设为 Opacity 0 导致菜单项及页面导航被遮挡的缺陷。
/// </summary>
internal static class Glass124Tests
{
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(Path.GetTempPath(), "Lexi_Glass124_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        var report = new List<string>();
        var exit = 1;

        void Check(bool condition, string message)
        {
            report.Add((condition ? "PASS " : "FAIL ") + message);
            if (!condition) throw new InvalidOperationException(message);
        }

        async Task Shot(string name)
        {
            try
            {
                await Task.Delay(100);
                var w = Math.Max(1, (int)window.Bounds.Width);
                var h = Math.Max(1, (int)window.Bounds.Height);
                using var bitmap = new RenderTargetBitmap(new PixelSize(w, h));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(folder, name + ".png"));
            }
            catch
            {
                // Headless/offscreen environment fallback
            }
        }

        void ShotPopup(Control popupVisual, string name)
        {
            try
            {
                var w = Math.Max(1, (int)popupVisual.Bounds.Width);
                var h = Math.Max(1, (int)popupVisual.Bounds.Height);
                if (w <= 1 || h <= 1) return;
                using var bitmap = new RenderTargetBitmap(new PixelSize(w, h));
                bitmap.Render(popupVisual);
                bitmap.Save(Path.Combine(folder, name + ".png"));
            }
            catch
            {
                // Fallback
            }
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;

        try
        {
            var glass = Field<GlassOverlayHost>("_glassHost");
            var mainLayer = (Control)((Grid)window.RootWindowBorder.Child!).Children[0];

            // 1. 在执行任何其他操作前，优先验证背景锐利度与弹窗显示中的主图层 Opacity == 1
            // 在旧实现中，RebuildBackdrop() 会执行 _background.Opacity = 0，
            // 此处首要检查可确保旧实现直接判定 FAIL，无法蒙混过关。
            Check(mainLayer.Opacity == 1, "main background layer starts sharp at opacity 1");

            var modal = window.FindControl<Border>("DialogDeleteOverlay")!;
            modal.IsVisible = true;
            await Task.Delay(100);
            await Shot("glass124-modal-open");

            Check(glass.IsOpen, "glass overlay host tracks modal visibility");
            Check(mainLayer.Opacity == 1 && mainLayer.IsEffectivelyVisible, "main background layer stays sharp at opacity 1 and visible while modal is open");
            Check(glass.IsBlurred == Lexi.Services.OverlayMaterialPolicy.TransparencyEnabled && glass.RetainedBackdropBytes < window.Bounds.Width * window.Bounds.Height * 4,
                "material is cropped to the small dialog, with no retained full-page backdrop");
            Check(!mainLayer.IsEnabled && !mainLayer.IsHitTestVisible, "modal still blocks input to the sharp background page");

            // 验证 forceSolid 纯色回退模式
            glass.ForceSolid = true;
            glass.Refresh();
            await Task.Delay(50);
            await Shot("glass124-modal-solid");
            Check(mainLayer.Opacity == 1 && !glass.IsBlurred && glass.RetainedBackdropBytes == 0, "forceSolid keeps main opacity 1 and releases local blur images");

            glass.ForceSolid = false;
            glass.Refresh();
            modal.IsVisible = false;
            await Task.Delay(60);
            Check(!glass.IsOpen && mainLayer.Opacity == 1, "closing confirmation modal updates glass host to closed and preserves main opacity 1");

            // 2. 验证 PlacementTarget 分离脱离视觉树后的 Popup 关闭与宿主清理
            // 旧实现在 PlacementTarget 脱离后 (target == null || TopLevel != _window) 会提前 return，
            // 导致 _popups 集合泄漏且 IsOpen 永远处于 true。
            var tempAnchor = new Button { Content = "TempAnchor" };
            var mainPanel = (Panel)mainLayer;
            mainPanel.Children.Add(tempAnchor);
            var detachedPopup = new Popup { PlacementTarget = tempAnchor, Child = new Border { Width = 80, Height = 40 } };
            mainPanel.Children.Add(detachedPopup);
            detachedPopup.IsOpen = true;
            await Task.Delay(80);
            Check(glass.IsOpen && mainLayer.Opacity == 1, "glass host tracks dynamically opened popup with main opacity 1");

            // 分离 anchor 并关闭 popup
            mainPanel.Children.Remove(tempAnchor);
            detachedPopup.IsOpen = false;
            mainPanel.Children.Remove(detachedPopup);
            await Task.Delay(80);
            Check(!glass.IsOpen && glass.RetainedBackdropBytes == 0, "closing popup after detaching PlacementTarget releases host state and material");

            // 3. 计划管理菜单项点击与计划编辑器真实渲染导航
            var store = Field<Lexi.IVocabularyArchive>("_vocabService");
            store.AddWord("translucent", "/trænzˈluːsnt/", "半透明的", "Allows light to pass through.");
            Call("RefreshWords");
            var word = store.GetAllWords().First(w => w.Word == "translucent");
            var plan = DailyStudyPlanRules.Create(
                "玻璃回归测试计划",
                DailyStudyPlanSource.Archive,
                "词汇档案",
                [new DailyStudyPlanWord { Id = word.Id.ToString(), Word = word.Word, Meaning = word.Translation }],
                1,
                false,
                1
            );
            plan.CurrentBatchWordIds = [word.Id.ToString()];
            var plans = Field<List<DailyStudyPlan>>("_learningPlans");
            plans.RemoveAll(p => p.Id == plan.Id);
            plans.Add(plan);

            Call("ShowPage", "learning");
            Call("RenderLearningPlans");
            await Task.Delay(100);

            // 打开计划管理菜单
            Call("ShowPlanManagementMenu", plan, window);
            await Task.Delay(100);

            var openStudyFlyouts = Field<HashSet<FlyoutBase>>("_openStudyFlyouts");
            var planMenu = openStudyFlyouts.OfType<MenuFlyout>().LastOrDefault();
            Check(planMenu != null && glass.IsOpen, "plan management MenuFlyout is tracked and opened in glass host");
            Check(mainLayer.Opacity == 1, "main background layer maintains opacity 1 during plan menu");

            var planMenuItems = planMenu!.Items.OfType<MenuItem>().ToList();
            var adjustItem = planMenuItems.FirstOrDefault(i => i.Header?.ToString()?.Contains("调整计划") == true);
            Check(adjustItem != null, "plan management menu includes adjust plan item");

            var planMenuRoot = adjustItem?.GetVisualRoot() as Control;
            if (planMenuRoot != null) ShotPopup(planMenuRoot, "glass124-plan-menu-popup");

            // 模拟 MenuItem.ClickEvent；若事件模拟未触发布局层关闭，显式调用 Hide 并记录
            adjustItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            planMenu.Hide();
            await Task.Delay(120);

            var planDrawer = Field<Border?>("_planEditDrawer");
            var editor = planDrawer?.GetVisualDescendants().OfType<PlanEditorControl>().FirstOrDefault();
            Check(planDrawer != null && editor != null && editor.IsEffectivelyVisible, "plan editor control is visibly rendered after clicking adjust menu item");
            Check(mainLayer.Opacity == 1 && mainLayer.IsEffectivelyVisible, "main layer remains sharp at opacity 1 and effectively visible after navigation to editor");
            await Shot("glass124-plan-editor");

            Call("ClosePlanEditor");
            await Task.Delay(60);

            // 4. 计划管理菜单：停止计划改变状态
            Call("ShowPlanManagementMenu", plan, window);
            await Task.Delay(100);
            planMenu = openStudyFlyouts.OfType<MenuFlyout>().LastOrDefault();
            var stopItem = planMenu?.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header?.ToString()?.Contains("停止计划") == true);
            Check(stopItem != null, "plan management menu includes stop plan item");

            stopItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            planMenu!.Hide();
            await Task.Delay(100);

            var updatedPlan = Field<List<DailyStudyPlan>>("_learningPlans").First(p => p.Id == plan.Id);
            Check(updatedPlan.Status == DailyStudyPlanStatus.Stopped, "clicking stop plan changes plan status to Stopped");
            Check(mainLayer.Opacity == 1, "main layer remains at opacity 1 after stop plan action");

            Call("ResumeLearningPlan", updatedPlan);
            await Task.Delay(60);

            // 5. 计划管理菜单：最近批次打开 PracticeSetupControl
            Call("ShowPlanManagementMenu", plan, window);
            await Task.Delay(100);
            planMenu = openStudyFlyouts.OfType<MenuFlyout>().LastOrDefault();
            var batchItem = planMenu?.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header?.ToString()?.Contains("最近批次") == true);
            Check(batchItem != null, "plan management menu includes recent batch practice item");

            batchItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            planMenu!.Hide();
            await Task.Delay(120);

            var workspaceHost = Field<Grid>("_studyWorkspaceHost");
            var setupControl = workspaceHost.GetVisualDescendants().OfType<PracticeSetupControl>().FirstOrDefault();
            Check(setupControl != null && setupControl.IsEffectivelyVisible, "recent batch opens PracticeSetupControl visibly");
            Check(mainLayer.Opacity == 1, "main layer opacity is 1 while PracticeSetupControl is open");

            setupControl!.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60);

            // 6. IELTS 练习菜单真实动作导航与深色对比度
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Call("ShowIeltsCatalog");
            await Task.Delay(120);

            var ieltsWorkspace = Field<Lexi.Features.Ielts.IeltsWorkspaceControl>("_ieltsWorkspace");
            var ieltsMenu = (MenuFlyout)ieltsWorkspace.GetType().GetField("_practiceMenu", flags)!.GetValue(ieltsWorkspace)!;
            var startPracticeBtn = (Button)ieltsWorkspace.GetType().GetField("_startPracticeBtn", flags)!.GetValue(ieltsWorkspace)!;

            ieltsMenu.ShowAt(startPracticeBtn);
            await Task.Delay(150);

            Check(glass.IsOpen && mainLayer.Opacity == 1, "IELTS practice menu is tracked by glass host with main opacity 1");

            var ieltsItems = ieltsMenu.Items.OfType<MenuItem>().ToList();
            var spellingItem = ieltsItems.FirstOrDefault(i => i.Header?.ToString()?.Contains("拼写") == true);
            Check(spellingItem != null, "IELTS menu contains spelling practice item");

            var expectedInk = window.FindResource(window.ActualThemeVariant, "InkBrush")!.ToString();
            Check(spellingItem!.Foreground?.ToString() == expectedInk, "dark menu item preserves contrasting ink foreground");

            var ieltsRoot = spellingItem.GetVisualRoot() as Control;
            if (ieltsRoot != null) ShotPopup(ieltsRoot, "glass124-ielts-menu-dark");

            // 点击菜单项执行真实拼写导航
            spellingItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ieltsMenu.Hide();
            await Task.Delay(120);

            var typingHost = Field<Border>("_typingHost");
            Check(typingHost != null && typingHost.IsEffectivelyVisible, "IELTS menu action navigates visibly to typing host");
            Check(mainLayer.Opacity == 1, "main background layer remains at opacity 1 after menu navigation");
            Check(!glass.IsOpen && glass.RetainedBackdropBytes == 0, "glass host and local material are released after navigation away from menu");

            Call("ExitLearningTyping");
            await Task.Delay(60);
            Check(mainLayer.Opacity == 1, "main layer stays sharp at opacity 1 after exiting practice");

            // Other popup types share the same global listener and must not hide the page.
            var anchor = window.FindControl<Button>("GlobalFocusButton")!;
            var context = new ContextMenu { Items = { new MenuItem { Header = "局部菜单检查" } } };
            context.Open(anchor);
            await Task.Delay(100);
            Check(context.IsOpen && glass.IsOpen && mainLayer.Opacity == 1, "context menu opens over a sharp page");
            context.Close();
            await Task.Delay(40);
            var combo = new ComboBox { ItemsSource = new[] { "第一项", "第二项" }, Width = 160, SelectedIndex = 0 };
            mainPanel.Children.Add(combo);
            await Task.Delay(40);
            combo.IsDropDownOpen = true;
            await Task.Delay(100);
            Check(combo.IsDropDownOpen && glass.IsOpen && mainLayer.Opacity == 1, "ComboBox dropdown opens without hiding the page");
            combo.IsDropDownOpen = false;
            mainPanel.Children.Remove(combo);
            await Task.Delay(60);
            Check(!glass.IsOpen && glass.RetainedBackdropBytes == 0, "context menu and dropdown close without retained overlays");

            exit = 0;
        }
        catch (Exception ex)
        {
            report.Add("ERROR " + ex);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "glass124-result.txt"), report);
        }

        (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
    }
}
