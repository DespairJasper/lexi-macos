using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lexi.Features.Learning;
using Lexi.Features.Settings;

namespace Lexi;
public static class PlanDrawerInteractionTests
{
    public static async Task RunAsync(MainWindow window,Action<bool,string> check)
    {
        var type=typeof(MainWindow);
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        void Call(string name,params object[] args)=>type.GetMethod(name,flags)!.Invoke(window,args);
        T Field<T>(string name)=>(T)type.GetField(name,flags)!.GetValue(window)!;
        var original=Field<List<DailyStudyPlan>>("_learningPlans");
        var folder=Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        async Task Snapshot(string name)
        { await Task.Delay(120); window.UpdateLayout(); using var bitmap=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));bitmap.Render(window);bitmap.Save(Path.Combine(folder,name+".png")); }
        try
        {
            var word=Field<IVocabularyArchive>("_vocabService").GetAllWords().First();
            foreach(var count in new[]{0,1,10,30})
            {
                var plans=Enumerable.Range(1,count).Select(index=>DailyStudyPlanRules.Create("每日词汇便签 "+index+" — 一段较长的计划标题",DailyStudyPlanSource.Archive,"词汇档案",[new DailyStudyPlanWord{Id=word.Id.ToString(),Word=word.Word,Meaning=word.Translation}],1,false,index)).ToList();
                type.GetField("_learningPlans",flags)!.SetValue(window,plans);
                Call("ShowPage","learning");
                Field<Grid>("_studyWorkspaceHost").IsVisible=false;
                Field<ScrollViewer>("_managementHost").IsVisible=true;
                Call("RenderLearningPlans");
                window.UpdateLayout();
                check(count==0||Field<StackPanel>("_learningPlansPanel").GetVisualDescendants().OfType<Border>().Count(b=>b.Classes.Contains("plan-card"))==count,"plan board renders "+count+" real plans");
            }
            foreach(var item in new[]{(1100d,760d,3),(900d,640d,2),(760d,520d,1)})
            {
                window.Width=item.Item1;window.Height=item.Item2; await Task.Delay(160);window.UpdateLayout();
                var grid=Field<StackPanel>("_learningPlansPanel").GetVisualDescendants().OfType<ResponsivePlanGrid>().Single(g=>g.IsEffectivelyVisible);
                check(grid.Columns==item.Item3,"plan grid uses "+item.Item3+" columns at "+item.Item1);
                check(grid.Children.All(c=>c.Bounds.Right<=grid.Bounds.Width+1),"plan cards remain within content width at "+item.Item1);
                await Snapshot("plans-"+item.Item1+"-light");
            }
            window.RequestedThemeVariant=ThemeVariant.Dark;
            await Snapshot("plans-760-dark");
            Call("OpenSettingsDrawer");
            var drawer=Field<SettingsPageControl>("_settingsDrawer");
            await Snapshot("settings-categories-dark");
            drawer.ShowSection(SettingsSection.Appearance);
            await Snapshot("settings-appearance-dark");
            check(window.FindControl<Border>("ThemeSettingsCard")!.IsEffectivelyVisible,"appearance controls are reachable through settings drawer");
            Call("CloseSettingsDrawer");
            window.RequestedThemeVariant=ThemeVariant.Light;
            window.Width=1100;window.Height=760;
        }
        finally { type.GetField("_learningPlans",flags)!.SetValue(window,original);Call("RenderLearningPlans"); }
    }
}
