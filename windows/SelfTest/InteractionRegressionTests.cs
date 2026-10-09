using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Lexi;

public static class InteractionRegressionTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var lines = new List<string>(); var exit = 1;
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        void Check(bool value,string label) { if(!value) throw new Exception(label); lines.Add("PASS "+label); }
        object? Call(string name,params object[] args) => typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,args);
        object? Field(string name) => typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
        async Task Snapshot(string name) { await Task.Delay(100); using var bitmap=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96)); bitmap.Render(window); bitmap.Save(Path.Combine(folder,name+".png")); }
        try
        {
            Check(window.FindControl<Button>("GlobalFocusButton") != null,"global focus entry is available in title bar");
            var store=(IVocabularyArchive)Field("_vocabService")!;
            store.AddWord("interactionsample","/test/","测试词","definition"); Call("RefreshWords");
            var word=store.GetAllWords().Single(w=>w.Word=="interactionsample");
            var plan=DailyStudyPlanRules.Create("便签布局测试",DailyStudyPlanSource.Archive,"档案",[new DailyStudyPlanWord {Id=word.Id.ToString(),Word=word.Word,Meaning=word.Translation}],1,false,1);
            Call("ShowPage","learning"); Call("StartDailyLearning",plan);
            var session=Field("_dailyLearningSession"); var memory=(LearningMemoryCoordinator)Field("_planMemory")!; var sessionId=memory.CurrentSessionId;
            Call("ToggleGlobalFocus");
            Check(!window.FindControl<Border>("SidebarBorder")!.IsVisible,"global focus hides management navigation");
            Check(ReferenceEquals(session,Field("_dailyLearningSession")) && ((LearningMemoryCoordinator)Field("_planMemory")!).CurrentSessionId==sessionId,"focus enters plan using the same round and memory session");
            await Snapshot("plan-focus-light");
            Call("ToggleGlobalFocus");
            Check(window.FindControl<Border>("SidebarBorder")!.IsVisible && ReferenceEquals(session,Field("_dailyLearningSession")),"focus exits without losing plan session");
            window.FindControl<Button>("NavSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check((bool)Field("_settingsDrawerOpen")! && (string)Field("_currentPage")! == "settings" && window.FindControl<ScrollViewer>("PageSettings")!.IsVisible,"settings opens as a full page");
            Call("CloseSettingsDrawer");
            Check(!(bool)Field("_settingsDrawerOpen")! && ReferenceEquals(session,Field("_dailyLearningSession")),"closing settings preserves the task session");
            await PlanDrawerInteractionTests.RunAsync(window,Check);
            await StudyKeySurfaceTests.RunAsync(window,Check);
            await KeyboardInteractionTests.RunAsync(window,Check);
            await IeltsInteractionTests.RunAsync(window,Check);
            await HotkeyTransactionTests.RunAsync(window,Check);
            Call("ShowPage","vocab");
            var todayWord=store.GetAllWords().Single(w=>w.Id==word.Id);
            ((List<WordItem>)Field("_filteredWords")!).Single(w=>w.Id==word.Id).Selected=true;
            var beforeCount=todayWord.ReviewCount;
            Call("ExecuteArchiveMenuAction",Lexi.Controls.ArchiveActionKind.TodayReview);
            Check(((List<WordItem>)Call("GetPendingReviewWords")!).Any(w=>w.Id==word.Id),"today review menu really schedules the selected word in the due queue");
            Check(store.GetAllWords().Single(w=>w.Id==word.Id).ReviewCount==beforeCount,"today review scheduling does not invent a learning rating");

            var chooserPlan=DailyStudyPlanRules.Create("专注来源计划",DailyStudyPlanSource.Archive,"档案",[new DailyStudyPlanWord {Id=word.Id.ToString(),Word=word.Word,Meaning=word.Translation}],1,false,1);
            ((List<DailyStudyPlan>)Field("_learningPlans")!).Add(chooserPlan);
            Call("ShowPage","lookup");Call("ShowFocusSourceChooser");
            var chooser=(Border)Field("_workspaceChooser")!;
            chooser.GetLogicalDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==chooserPlan.Name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check((string)Field("_currentPage")! == "learning" && ((Grid)Field("_studyWorkspaceHost")!).IsEffectivelyVisible && (bool)Field("_globalFocusActive")!,"focus source chooser opens the chosen plan from another page");
            Call("ToggleGlobalFocus");Call("ExitDailyLearning");
            Call("ShowIeltsCatalog");
            var catalog=(IeltsCatalog)Field("_ieltsCatalog")!;
            Call("ShowIeltsSynonymsBrowser",catalog.Sections.First(s=>s.Entries.All(w=>w.Synonyms.Count==0)));
            Check(((Grid)Field("_ieltsSubContent")!).Children.OfType<Lexi.Features.Ielts.IeltsSynonymBrowser>().Single().IsEffectivelyVisible,"synonym browser is mounted and visible for an empty chapter");
            await Snapshot("synonyms-empty");
            Call("ShowIeltsResources"); await Snapshot("resources-light");
            exit=0;
        }
        catch(Exception ex) { lines.Add("FAIL "+ex); }
        finally { File.WriteAllLines(Path.Combine(folder,"interaction-result.txt"),lines); window.ForceClose(); (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(exit); }
    }
}
