using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Lexi;
public static class StudyKeySurfaceTests
{
    public static async Task RunAsync(MainWindow window,Action<bool,string> check)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,flags)!.GetValue(window)!;
        KeyEventArgs Down(Control target,Key key,KeyModifiers modifiers=KeyModifiers.None)
        {var e=new KeyEventArgs {RoutedEvent=InputElement.KeyDownEvent,Key=key,KeyModifiers=modifiers};target.RaiseEvent(e);return e;}
        void Up(Control target,Key key)=>target.RaiseEvent(new KeyEventArgs {RoutedEvent=InputElement.KeyUpEvent,Key=key});
        void Tap(Control target,Key key,KeyModifiers modifiers=KeyModifiers.None){Down(target,key,modifiers);Up(target,key);}
        var archive=Field<IVocabularyArchive>("_vocabService");
        archive.AddWord("keyboardfixture","","键盘测试","definition");Call("RefreshWords");
        var item=archive.GetAllWords().Single(w=>w.Word=="keyboardfixture");
        Call("ShowPage","vocab"); Call("EnterArchiveFocusWithFirst",item);
        var host=Field<Border>("_focusHost");
        var back=host.GetVisualDescendants().OfType<Button>().First();
        back.Focusable=true;back.Focus();
        var round=Field<StudyRound<string>>("_focusRound");
        Tap(back,Key.Right);
        check(!Field<bool>("_focusRated"),"actual focus surface cannot rate a hidden answer");
        var reveal=Down(back,Key.Space);
        check(reveal.Handled&&Field<bool>("_focusAnswerVisible")&&Field<bool>("_focusActive"),"Space on focused Back reveals without exiting");
        for(var i=0;i<4;i++) {var repeated=Down(back,Key.Space);check(repeated.Handled,"repeated Space event is consumed "+i);}
        Up(back,Key.Space);
        Tap(back,Key.Space);
        check(Field<bool>("_focusActive")&&!Field<bool>("_focusRated"),"held and repeated Space never rates or exits (active="+Field<bool>("_focusActive")+", rated="+Field<bool>("_focusRated")+")");
        Down(back,Key.Down);
        check(Field<bool>("_focusRated"),"Down rates unsure through the single UI router");
        var checkpoint=round.CaptureJson(w=>w);
        Down(back,Key.Down);Up(back,Key.Down);
        check(round.CaptureJson(w=>w)==checkpoint,"holding a rating key does not repeat the rating");
        Tap(back,Key.Z,KeyModifiers.Control);
        check(!Field<bool>("_focusRated"),"Ctrl+Z undoes the UI rating");
        var menu=new MenuFlyout(); menu.Items.Add(new MenuItem {Header="Keyboard menu"});
        typeof(MainWindow).GetMethod("TrackStudyFlyout",flags)!.Invoke(window,[menu]);menu.ShowAt(back);
        await Task.Delay(50);var before=round.CaptureJson(w=>w);
        Tap(back,Key.Right);
        check(before==round.CaptureJson(w=>w),"open menu owns arrow keys and does not rate the study round");
        menu.Hide();back.Focus();
        Call("OpenSettingsDrawer");Tap(window,Key.Escape);
        check(!Field<bool>("_settingsDrawerOpen")&&Field<bool>("_focusActive"),"Esc closes settings before exiting focus");
        Call("ExitFocus");

        Call("ShowPage","learning");
        var plan=DailyStudyPlanRules.Create("键盘行为计划",DailyStudyPlanSource.Archive,"档案",[new DailyStudyPlanWord{Id=item.Id.ToString(),Word=item.Word,Meaning=item.Translation}],1,false,1);
        Call("StartDailyLearning",plan);
        var session=Field<DailyStudyPlanSession>("_dailyLearningSession");
        var sessionId=Field<LearningMemoryCoordinator>("_planMemory").CurrentSessionId;
        var returnButton=Field<Grid>("_studyWorkspaceHost").GetVisualDescendants().OfType<Button>().First();returnButton.Focus();
        for(var i=0;i<3;i++)
        {
            returnButton=Field<Grid>("_studyWorkspaceHost").GetVisualDescendants().OfType<Button>().First();
            returnButton.Focus();Tap(returnButton,Key.Space);
        }
        check(session.Round.CurrentStep==StudyStep.Recall&&Field<bool>("_planAnswerVisible")&&session.Round.CurrentStreak==0&&Field<Grid>("_studyWorkspaceHost").IsVisible,"plan Space advances then reveals without silently rating or returning");
        var settings=new Lexi.Features.Settings.SettingsDrawerControl();
        Call("OpenSettingsDrawer");
        Field<Lexi.Features.Settings.SettingsPageControl>("_settingsDrawer").ShowSection(Lexi.Features.Settings.SettingsSection.StudyAndShortcuts);
        var editors=Field<Dictionary<ShortcutAction,TextBox>>("_shortcutEditors");
        editors[ShortcutAction.Known].Text="Ctrl+K";
        Call("SaveStudyShortcutEditors");
        var reloaded=ShortcutConfigStore.ForDataDirectory(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!).Load();
        check(reloaded.Bindings[ShortcutAction.Known].SequenceEqual(["Ctrl+K"]),"settings editor saves a custom rating key for restart");
        Call("CloseSettingsDrawer");
        returnButton=Field<Grid>("_studyWorkspaceHost").GetVisualDescendants().OfType<Button>().First();
        returnButton.Focus();Tap(returnButton,Key.K,KeyModifiers.Control);
        check(session.Round.CurrentStreak==1&&Field<LearningMemoryCoordinator>("_planMemory").CurrentSessionId==sessionId,"custom key rates once in the same plan memory session (streak="+session.Round.CurrentStreak+", phase="+Call("DetermineCurrentStudyPhase")+", focus="+window.FocusManager?.GetFocusedElement()?.GetType().Name+", session="+Field<LearningMemoryCoordinator>("_planMemory").CurrentSessionId+")");
        window.SaveShortcutConfiguration(ShortcutConfiguration.CreateDefault());
        Call("RefreshShortcutEditors");Call("ExitDailyLearning");

        Call("ShowIeltsCatalog");var catalog=Field<IeltsCatalog>("_ieltsCatalog");var source=catalog.AllWords.First(w=>!string.IsNullOrWhiteSpace(w.Meaning));
        archive.AddWord(source.Word,"","用户档案译文","archive definition");Call("RefreshWords");
        Call("EnterIeltsFocus",new List<LearningWord>{source});
        var presentation=Field<object>("_focusPresentation");var key=(WordKey)presentation.GetType().GetProperty("Key")!.GetValue(presentation)!;
        check(key.Key==WordKeyResolver.FromIelts(source).Key,"IELTS focus keeps textbook identity even when spelling exists in archive");
        Call("ExitFocus");

        Call("ShowPage","vocab");Call("EnterArchiveFocusWithFirst",item);
        check(Field<Dictionary<string,LearningWord>>("_focusIeltsWords").Count==0,"archive focus clears earlier IELTS identities");
        var savedRound=Field<StudyRound<string>>("_focusRound").CaptureJson(w=>w);
        var savedSession=Field<LearningMemoryCoordinator>("_focusMemory").CurrentSessionId;
        Call("ToggleGlobalFocus");Call("EnterArchiveFocusWithFirst",item);
        check(Field<StudyRound<string>>("_focusRound").CaptureJson(w=>w)==savedRound&&Field<LearningMemoryCoordinator>("_focusMemory").CurrentSessionId==savedSession,"exiting and reopening archive focus restores the same round and memory session");
        Call("ExitFocus");
    }
}
