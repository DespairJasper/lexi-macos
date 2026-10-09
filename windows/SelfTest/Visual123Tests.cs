using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lexi.Features.Learning;

namespace Lexi;

internal static class Visual123Tests
{
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        var report = new List<string>(); var exit = 1;
        void Check(bool condition, string message) { report.Add((condition ? "PASS " : "FAIL ") + message); if (!condition) throw new InvalidOperationException(message); }
        async Task Shot(string name) { await Task.Delay(150); using var bitmap=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height)); bitmap.Render(window); bitmap.Save(Path.Combine(folder,name+".png")); }
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,flags)!.GetValue(window)!;
        try
        {
            bool? hints=null;
            var setup=new PracticeSetupControl(20,1,(_,h)=>hints=h,()=>{});
            setup.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(hints==true,"default spelling setup starts assisted spelling");
            Check(!setup.GetVisualDescendants().OfType<RadioButton>().Any(r=>Convert.ToString(r.Content)?.Contains("默写")==true),"spelling setup has no dictation mode choice");
            await Forms123ControlTests.RunAsync(window,Check);
            await Learning123ControlTests.RunAsync(window,Check);
            window.Width=1100;window.Height=760;
            Call("OpenSettingsDrawer"); await Shot("settings-light");
            Check(!window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.IsEffectivelyVisible && (t.Text?.Contains("手风琴式")==true || t.Text=="系统偏好 · 保持离线")),"settings removes promotional and accordion header");
            Call("CloseSettingsDrawer");
            window.RequestedThemeVariant=ThemeVariant.Light;
            var modal=window.FindControl<Border>("DialogDeleteOverlay")!;
            modal.IsVisible=true;await Shot("glass-confirm-light");
            var glass=(Lexi.Shell.GlassOverlayHost)typeof(MainWindow).GetField("_glassHost",flags)!.GetValue(window)!;
            Check(glass.IsOpen,"glass host tracks actual confirmation visibility");
            Check(glass.IsBlurred==Lexi.Services.OverlayMaterialPolicy.TransparencyEnabled,"actual modal background follows Windows transparency preference");
            modal.IsVisible=false;await Task.Delay(40);Check(!glass.IsOpen && !glass.IsBlurred,"closing confirmation releases background blur");
            Environment.SetEnvironmentVariable("LEXI_SOLID_OVERLAYS","1");
            modal.IsVisible=true;await Shot("solid-confirm-light");Check(!glass.IsBlurred,"disabled transparency uses solid fallback");modal.IsVisible=false;
            Environment.SetEnvironmentVariable("LEXI_SOLID_OVERLAYS",null);
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var before=GC.GetTotalMemory(true);
            var captures=new List<double>();
            for(var i=0;i<50;i++){modal.IsVisible=true;captures.Add(glass.LastCaptureMilliseconds);modal.IsVisible=false;}
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var after=GC.GetTotalMemory(true);
            Check(!glass.IsOpen && !glass.IsBlurred,"fifty confirmation cycles leave no active blur or modal");
            Check(glass.RetainedBackdropBytes==0 && after-before<64*1024*1024,"closed confirmations release backdrop and do not retain excessive managed memory");
            captures.Sort();report.Add($"MEASURE modal capture p95={captures[47]:F1}ms; managed delta={after-before}; scaling={window.RenderScaling}; OS={Environment.OSVersion}; CPU={Environment.ProcessorCount}");
            Call("StartLearningTyping",new List<LearningWord>{new(){Id="123-word",Words=["adaptive"],Meaning="能够适应变化的；自适应的",Phonetic="/əˈdæptɪv/"}},false);
            var session=(TypingSession)typeof(MainWindow).GetField("_learningTypingSession",flags)!.GetValue(window)!;
            Check(session.Hints,"legacy caller cannot start a separate dictation mode");
            await Shot("spelling-light");
            var spelling=(SpellingPracticeControl)typeof(MainWindow).GetField("_spellingPractice",flags)!.GetValue(window)!;
            spelling.InputBox.Text="ad";spelling.HintButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(spelling.InputBox.Text=="ad","actual progressive hint preserves typed text");await Shot("spelling-hint");
            spelling.InputBox.Text="adaptive";spelling.SubmitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(session.Outcome==TypingOutcome.Correct,"actual compact spelling submit reaches existing session");
            Check(spelling.NextButton.IsVisible,"correct spelling exposes next-word action");
            Call("ExitLearningTyping");
            var store=Field<IVocabularyArchive>("_vocabService");
            store.AddWord("responsive","/rɪˈspɒnsɪv/","响应迅速的；适应窗口变化的","Adjusts to available space.");Call("RefreshWords");
            var word=store.GetAllWords().Single(w=>w.Word=="responsive");
            var plan=DailyStudyPlanRules.Create("尺寸与表单验收",DailyStudyPlanSource.Archive,"词汇档案",[new DailyStudyPlanWord{Id=word.Id.ToString(),Word=word.Word,Meaning=word.Translation}],1,false,1);
            Field<List<DailyStudyPlan>>("_learningPlans").Add(plan);
            Call("ShowPage","learning");Call("OpenPlanEditor",plan);await Shot("plan-edit-light");
            var editor=Field<Border>("_planEditDrawer").GetVisualDescendants().OfType<PlanEditorControl>().Single();
            Check(Field<Border>("_planEditDrawer").Bounds.Width>window.Bounds.Width*.8,"plan adjustment fills main content instead of a side drawer");
            editor.NameInput.Text="修改后的真实计划";editor.DailyQuotaInput.Value=3;editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<List<DailyStudyPlan>>("_learningPlans").Single(p=>p.Id==plan.Id).Name=="修改后的真实计划","shared plan editor saves through actual plan persistence");
            plan=Field<List<DailyStudyPlan>>("_learningPlans").Single(p=>p.Id==plan.Id);
            Call("StartDailyLearning",plan);Call("ToggleGlobalFocus");await Shot("focus-bounded-light");
            var reading=Field<Border>("_globalStudyContent");
            Check(reading.Bounds.Width>=600 && reading.Bounds.Width<=680,"focus reading width stays bounded even for a short definition");
            window.Width=760;window.Height=520;await Shot("focus-bounded-small");
            Check(reading.Bounds.Right<=window.Bounds.Width && reading.Bounds.Width<=window.Bounds.Width-48,"focus content responds to a narrow window");
            Field<DailyStudyPlan>("_activeLearningPlan").Words[0].Definition=string.Join(" ",Enumerable.Repeat("Long definitions must stay readable without hiding the study action.",30));
            Call("RefreshGlobalStudySurface");await Shot("focus-long-small");
            var studyActions=Field<WrapPanel>("_globalStudyActions");
            var actionsOrigin=studyActions.TranslatePoint(new Point(0,0),window)!.Value;
            Check(actionsOrigin.Y+studyActions.Bounds.Height<window.Bounds.Height-20,"long focus text scrolls while study actions remain visible in a short window");
            Call("ToggleGlobalFocus");Call("ExitDailyLearning");window.Width=1100;window.Height=760;
            var windowsBefore=(Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Windows.Count;
            Call("ShowLastPlanBatch",plan);await Shot("practice-setup-main");
            Check((Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Windows.Count==windowsBefore,"recent batch preparation creates no independent window");
            var recentSetup=Field<Grid>("_studyWorkspaceHost").GetVisualDescendants().OfType<PracticeSetupControl>().Single();recentSetup.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Call("ShowPage","vocab");Call("OpenPlanCreator",DailyStudyPlanSource.Archive,null);await Shot("plan-select-light");
            var creator=Field<Border>("_planCreatorOverlay");
            creator.GetVisualDescendants().OfType<PlanWordRowControl>().First().GetVisualDescendants().OfType<CheckBox>().First().IsChecked=true;
            creator.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="下一步：安排计划 →").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Shot("plan-create-form");
            Check(creator.GetVisualDescendants().OfType<PlanEditorControl>().Single().IsEffectivelyVisible,"plan creation uses the same visible form as adjustment");Call("ClosePlanCreator");
            window.OpenQuoteEditor(null);await Shot("quote-edit-light");
            var quoteHost=Field<Border>("_quoteEditorHost");
            TextBox QuoteField(string name)=>quoteHost.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name==name);
            var quoteSave=quoteHost.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="QuoteSaveButton");
            quoteSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(ReferenceEquals(quoteHost,Field<Border>("_quoteEditorHost")),"empty quote save retains editor and fields");
            QuoteField("QuoteOriginalInput").Text="A consistent interface keeps attention on learning.";QuoteField("QuoteTranslationInput").Text="统一界面让注意力回到学习。";
            Call("ShowPage","lookup");Check(ReferenceEquals(quoteHost,Field<Border>("_quoteEditorHost")),"unsaved quote input blocks navigation");
            quoteSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Shot("quote-notebook-light");
            var quote=((IQuoteArchive)store).GetQuotes("A consistent interface").Single();Check(quote.Translation=="统一界面让注意力回到学习。","main-page quote editor saves the actual translation");
            Call("ConfirmQuoteDelete",quote,window);await Shot("quote-delete-glass");Call("CloseVisualConfirmation");
            Check(((IQuoteArchive)store).GetQuotes("A consistent interface").Count==1,"cancelling glass delete preserves quote");
            window.RequestedThemeVariant=ThemeVariant.Dark;Call("ShowPage","quotes");await Shot("quote-notebook-dark");
            Call("ShowIeltsCatalog");await Task.Delay(120);
            var workspace=Field<Lexi.Features.Ielts.IeltsWorkspaceControl>("_ieltsWorkspace");
            var menu=(MenuFlyout)workspace.GetType().GetField("_practiceMenu",flags)!.GetValue(workspace)!;
            var start=(Button)workspace.GetType().GetField("_startPracticeBtn",flags)!.GetValue(workspace)!;
            menu.ShowAt(start);await Task.Delay(160);
            Check(glass.IsOpen,"actual IELTS practice menu uses the shared glass host");
            var menuItems=menu.Items.OfType<MenuItem>().ToList();
            Check(menuItems.Any(i=>i.Header?.ToString()=="拼写练习") && !menuItems.Any(i=>i.Header?.ToString()?.Contains("默写")==true),"IELTS practice menu exposes only one spelling mode");
            var root=menuItems[0].GetVisualRoot() as Control;
            if(root!=null){using var bmp=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height));bmp.Render(root);bmp.Save(Path.Combine(folder,"ielts-practice-menu-dark.png"));}
            Check(menuItems.All(i=>i.Foreground!.ToString()!=start.Foreground!.ToString()),"practice menu foreground is independent of its primary button");
            var expectedInk=window.FindResource(window.ActualThemeVariant,"InkBrush")!.ToString();
            Check(menuItems.All(i=>i.Foreground!.ToString()==expectedInk),"actual dark menu items use the owning window's contrasting foreground");
            Check(menuItems.SelectMany(i=>i.GetVisualDescendants().OfType<TextBlock>()).All(t=>t.Foreground!.ToString()==expectedInk),"actual rendered menu header text uses the same correct dark palette");
            menu.Hide();await Task.Delay(80);Check(!glass.IsOpen && glass.RetainedBackdropBytes==0,"closing real menu releases its blurred backdrop");
            var popupAction=new Button {Content="确认",Classes={"primary"}};
            var actionPopup=new Flyout {Content=popupAction};actionPopup.ShowAt(start);await Task.Delay(120);
            var popupLabel=popupAction.GetVisualDescendants().OfType<TextBlock>().First();
            Check(popupLabel.Foreground!.ToString()==window.FindResource(window.ActualThemeVariant,"OnPrimaryBrush")!.ToString(),"glass popup palette preserves contrasting text within a primary action");
            actionPopup.Hide();await Task.Delay(60);
            window.SetUiLanguage("en");await Task.Delay(120);
            Check(menuItems.Any(i=>i.Header?.ToString()=="Spelling practice"),"new IELTS spelling label follows actual English language switching");
            window.SetUiLanguage("zh-CN");await Task.Delay(120);
            window.RequestedThemeVariant=ThemeVariant.Dark;
            Call("OpenSettingsDrawer");await Shot("settings-dark");Call("CloseSettingsDrawer");
            exit=0;
        }
        catch(Exception ex){report.Add("ERROR "+ex);}
        finally {File.WriteAllLines(Path.Combine(folder,"visual123-result.txt"),report);}
        (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
    }
}
