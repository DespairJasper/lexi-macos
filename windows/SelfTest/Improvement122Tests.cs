using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Lexi.Features.Settings;

namespace Lexi;
public static class Improvement122Tests
{
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        var report = new List<string>(); var exit = 1;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string method, params object[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window,args);
        T Field<T>(string field) => (T)typeof(MainWindow).GetField(field,flags)!.GetValue(window)!;
        void Check(bool value, string label) { report.Add((value ? "PASS " : "FAIL ")+label); if(!value)throw new Exception(label); }
        async Task Shot(string name) { await Task.Delay(150); window.UpdateLayout(); using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96)); bitmap.Render(window); bitmap.Save(Path.Combine(folder,name+".png")); }
        try
        {
            var backCalled = false;
            var failingWriter = new Lexi.Features.Ielts.IeltsWritingWorkspace(
                new IeltsCatalog { Sentences = [new WritingSentence { Number = 1, Chinese = "测试保存失败", BookAnswer = "Test a failed save." }] },
                new Dictionary<int,string>(), () => throw new IOException("disk unavailable"), _=>{}, ()=>backCalled=true);
            failingWriter.DraftInput.Text = "Preserve this unsaved draft.";
            await Task.Delay(40);
            Check(!failingWriter.FlushPendingSave(),"writing reports a failed save");
            failingWriter.BackButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(!backCalled && failingWriter.DraftInput.Text=="Preserve this unsaved draft.","writing cannot leave on a failed save");
            var failureContent=Field<Grid>("_ieltsSubContent");
            failureContent.Children.Add(failingWriter);
            Check(!window.TryFlushWritingDraft(),"main window blocks closing while writing cannot save");
            failureContent.Children.Remove(failingWriter); failingWriter.Dispose();
            await SettingsPractice122Tests.RunAsync(window,Check);
            await StudyCanvas122Tests.RunAsync(window,Check);
            await Ielts122Tests.RunAsync(window,Check);
            await ContentActions122Tests.RunAsync(window,Check);
            var archive = Field<IVocabularyArchive>("_vocabService");
            typeof(MainWindow).GetField("_restoring",flags)!.SetValue(window,true);
            Check(window.SaveExampleToQuotes("Do not write during restore.") == null && !((IQuoteArchive)archive).GetQuotes("Do not write during restore.").Any(),"example collection cannot write during database restore");
            typeof(MainWindow).GetField("_restoring",flags)!.SetValue(window,false);
            Call("SetStatus","学习进度自动保存。");
            var audio = new RecordingAudio();
            var previousAudio = Field<IWordAudioPlayer>("_learningAudio");
            typeof(MainWindow).GetField("_learningAudio",flags)!.SetValue(window,audio);
            var actionButtons=window.CreateExampleActions("Listen to this exact sentence.","朗读这句原文。").Children.OfType<Button>().ToList();
            actionButtons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(audio.LastText=="Listen to this exact sentence.","example audio reads its sentence rather than the headword");
            window.FindControl<Button>("LookupSpeakButton")!.Tag="adaptive";
            window.FindControl<Button>("LookupSpeakButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(audio.LastText=="adaptive","lookup audio reads the displayed headword");
            typeof(MainWindow).GetField("_learningAudio",flags)!.SetValue(window,previousAudio);
            var passage = new SelectableTextBlock { Text = "An adaptive example.", SelectionStart = 0, SelectionEnd = 8 };
            var router = new StudyShortcutRouter();
            Check(router.RouteKeyDown(new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Right },passage,false,false,true,true,StudyPhase.RecallRevealedUnrated,false,true,false)==StudyShortcutDispatchResult.Ignored,"selected text owns arrow keys and cannot rate a word");
            Check(router.RouteKeyDown(new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.C, KeyModifiers = Avalonia.Input.KeyModifiers.Control },passage,false,false,true,true,StudyPhase.RecallRevealedUnrated,false,true,false)==StudyShortcutDispatchResult.Ignored,"Ctrl+C reaches selected text without a study action");
            var store = Field<IVocabularyArchive>("_vocabService");
            store.AddWord("adaptive","/əˈdæptɪv/","能够适应变化的；自适应的","Able to adjust to new conditions."); Call("RefreshWords");
            var word = store.GetAllWords().Single(w=>w.Word=="adaptive");
            var plan = DailyStudyPlanRules.Create("让词汇慢慢生长", DailyStudyPlanSource.Archive,"词汇档案",[new DailyStudyPlanWord{Id=word.Id.ToString(),Word=word.Word,Meaning=word.Translation}],1,false,1);
            Call("OpenPlanCreator",DailyStudyPlanSource.Archive,null!); await Shot("plan-creator");
            Check(Field<Border>("_planCreatorOverlay").IsVisible && Field<ContentControl>("_planCreatorContent").Bounds.Width>500,"plan creation uses an in-app workspace");
            Call("ClosePlanCreator");
            Call("ShowPage","learning"); Call("StartDailyLearning",plan);
            var session = Field<DailyStudyPlanSession>("_dailyLearningSession");
            Call("OpenSettingsDrawer");
            var settings = Field<SettingsPageControl>("_settingsDrawer");
            Check(Field<string>("_currentPage")=="settings" && !Field<Grid>("_learningHubPage").IsVisible,"settings replaces the source page");
            foreach(var size in new[]{(1100d,760d),(760d,520d)})
            {
                window.Width=size.Item1;window.Height=size.Item2;
                foreach(var section in new[]{SettingsSection.Appearance,SettingsSection.AiService,SettingsSection.StudyAndShortcuts,SettingsSection.DataAndBackup})
                {
                    settings.ShowSection(section); await Shot("settings-"+section+"-"+size.Item1);
                    Check(settings.BodyScroller.Viewport.Width>0 && settings.BodyScroller.Extent.Width<=settings.BodyScroller.Viewport.Width+1,"settings has no horizontal overflow "+section+" at "+size.Item1);
                }
            }
            Call("CloseSettingsDrawer");
            Check(Field<string>("_currentPage")=="learning" && ReferenceEquals(session,Field<DailyStudyPlanSession>("_dailyLearningSession")),"returning from settings preserves the exact session");
            window.Width=1100;window.Height=760; await Shot("plan-normal");
            Call("ToggleGlobalFocus"); await Shot("plan-focus");
            Check(ReferenceEquals(session,Field<DailyStudyPlanSession>("_dailyLearningSession")),"focus preserves plan session");
            Check(Field<Border>("_globalStudySurface").IsVisible,"plan focus uses the unified surface");
            var sharedSurface=Field<Border>("_globalStudySurface");
            var planSessionId=Field<LearningMemoryCoordinator>("_planMemory").CurrentSessionId;
            for(var tap=0;tap<3;tap++)
            {
                var back=sharedSurface.GetVisualDescendants().OfType<Button>().First();back.Focus();
                back.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent=Avalonia.Input.InputElement.KeyDownEvent,Key=Avalonia.Input.Key.Space });
                back.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent=Avalonia.Input.InputElement.KeyUpEvent,Key=Avalonia.Input.Key.Space });
                await Task.Delay(40);
            }
            Check(sharedSurface.IsVisible && Field<bool>("_globalFocusActive") && Field<bool>("_planAnswerVisible") && session.Round.CurrentStreak==0,"Space on the unified Back button reveals without exiting or rating");
            Check(Field<LearningMemoryCoordinator>("_planMemory").CurrentSessionId==planSessionId,"unified focus keyboard actions keep the original memory session");
            var beforeTheme=session.Round.CaptureJson(w=>w);
            window.RequestedThemeVariant=ThemeVariant.Dark;await Shot("plan-focus-dark");
            Check(((Avalonia.Media.ISolidColorBrush)sharedSurface.Background!).Color==((Avalonia.Media.ISolidColorBrush)window.FindResource(ThemeVariant.Dark,"PaperBrush")!).Color,"active focus background matches the application's dark theme");
            window.RequestedThemeVariant=ThemeVariant.Light;await Task.Delay(50);
            Check(((Avalonia.Media.ISolidColorBrush)sharedSurface.Background!).Color==((Avalonia.Media.ISolidColorBrush)window.FindResource(ThemeVariant.Light,"PaperBrush")!).Color && session.Round.CaptureJson(w=>w)==beforeTheme && Field<bool>("_planAnswerVisible") && Field<LearningMemoryCoordinator>("_planMemory").CurrentSessionId==planSessionId,"active focus theme changes preserve the round, revealed state and memory session");
            window.Width=760;window.Height=520; await Shot("plan-focus-small");
            Call("ToggleGlobalFocus"); Call("ShowPage","lookup");
            window.FindControl<TextBox>("LookupInput")!.Text="adaptive";
            await (Task)Call("PerformLookupAsync")!;
            Check(window.FindControl<Control>("ResultTranslationText") is SelectableTextBlock,"lookup definitions support text selection");
            Call("RenderExpansion",new LlmResult { Examples=[new ExampleItem("An adaptive approach makes learning easier.","自适应的方法让学习更轻松。") ] });
            await (Task)Call("OpenDrawerAsync")!;
            await Task.Delay(60);
            var exampleQuote=window.FindControl<StackPanel>("AiResultExamplesContainer")!.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="＋");
            exampleQuote.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(((IQuoteArchive)store).GetQuotes("An adaptive approach makes learning easier.").Single().Translation=="自适应的方法让学习更轻松。","lookup example button stores its original sentence and translation");
            var selectable=window.FindControl<SelectableTextBlock>("ResultTranslationText")!;
            var beforeLanguage=selectable.Text;
            window.SetUiLanguage("en");
            Check(selectable.Text==beforeLanguage,"language switching preserves vocabulary definitions");
            window.SetUiLanguage("zh-CN");
            var originalClipboard=await window.Clipboard!.TryGetTextAsync();
            async Task<bool> ClipboardEquals(string expected)
            {
                for(var attempt=0;attempt<20;attempt++)
                {
                    if(await window.Clipboard.TryGetTextAsync()==expected)return true;
                    await Task.Delay(25);
                }
                return false;
            }
            try
            {
                selectable.Focus(); selectable.SelectionStart=0; selectable.SelectionEnd=Math.Min(6,selectable.Text!.Length);
                selectable.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent=Avalonia.Input.InputElement.KeyDownEvent, Key=Avalonia.Input.Key.C, KeyModifiers=Avalonia.Input.KeyModifiers.Control });
                Check(await ClipboardEquals(selectable.Text[..selectable.SelectionEnd]),"selected lookup definition actually copies with Ctrl+C");
                selectable.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent=Control.ContextRequestedEvent });
                await Task.Delay(40);
                var contextMenu=selectable.ContextFlyout as MenuFlyout;
                Check(contextMenu?.IsOpen==true,"selectable content offers a context copy menu");
                await window.Clipboard.SetTextAsync("");
                contextMenu!.Items.OfType<MenuItem>().First().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                Check(await ClipboardEquals(selectable.SelectedText),"context menu copies the selected passage");
                contextMenu.Hide();
            }
            finally { await window.Clipboard.SetTextAsync(originalClipboard??""); selectable.SelectionStart=selectable.SelectionEnd=0; }
            await Shot("lookup-examples");
            Call("EnterFocusFromLookup"); await Shot("lookup-focus");
            Check(ReferenceEquals(sharedSurface,Field<Border>("_globalStudySurface")) && sharedSurface.IsVisible,"lookup uses the exact same focus surface");Call("ExitFocus");
            store.SaveExpansion(word.Id,new LlmResult { Examples=[new ExampleItem("Archive examples can be collected directly.","档案例句可以直接收藏。") ] });
            Call("RefreshWords"); Call("ShowPage","vocab");
            Field<List<WordItem>>("_allWords").Single(w=>w.Id==word.Id).IsExpanded=true;
            await Task.Delay(100); window.UpdateLayout();
            var archiveQuote=window.GetVisualDescendants().OfType<Button>().Single(b=>b.DataContext is ExampleItem ex && ex.English=="Archive examples can be collected directly." && b.Content?.ToString()=="＋");
            archiveQuote.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(((IQuoteArchive)store).GetQuotes("Archive examples can be collected directly.").Single().Translation=="档案例句可以直接收藏。","archive example button saves its actual sentence and translation");
            await Shot("archive-examples");
            Call("EnterArchiveFocusWithFirst",store.GetAllWords().Single(w=>w.Id==word.Id));await Task.Delay(80);
            Check(sharedSurface.IsVisible && ReferenceEquals(sharedSurface,Field<Border>("_globalStudySurface")),"archive uses the exact same focus surface");Call("ExitFocus");
            store.ExecuteBatch([word.Id],"today");Call("RefreshWords");Call("ShowPage","review");Call("ToggleGlobalFocus");await Shot("review-focus");
            Check(sharedSurface.IsVisible && ReferenceEquals(sharedSurface,Field<Border>("_globalStudySurface")),"today review uses the exact same focus surface");Call("ToggleGlobalFocus");
            Call("ShowIeltsCatalog"); await Shot("ielts-rows");
            var ieltsWord=Field<IeltsCatalog>("_ieltsCatalog").AllWords.First();Call("EnterIeltsFocus",new List<LearningWord>{ieltsWord});await Shot("ielts-focus");
            Check(sharedSurface.IsVisible && ReferenceEquals(sharedSurface,Field<Border>("_globalStudySurface")),"IELTS uses the exact same focus surface");Call("ExitFocus");
            var hintWord=new LearningWord { Id="122-hint-fixture",Words=["adaptive"],Meaning="自适应的" };
            Call("StartLearningTyping",new List<LearningWord>{hintWord},true);await Task.Delay(60);
            var typing=Field<TypingSession>("_learningTypingSession");
            var hint=Field<SelectableTextBlock>("_learningTypingHint");
            Check(hint.Text=="8 个字母" && !hint.Text.Any(c=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'),"assisted spelling initially shows length without answer letters");
            var spelling=Field<Lexi.Features.Learning.SpellingPracticeControl>("_spellingPractice");
            var hintButton=spelling.HintButton;
            hintButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(hint.Text!.Count(c=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')==1,"one hint reveals exactly one letter");
            Field<TextBox>("_learningTypingInput").Text="ad";
            hintButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(Field<TextBox>("_learningTypingInput").Text=="ad" && hint.Text!.Count(c=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')==2,"the next hint preserves typed input and reveals one more letter");
            await Shot("typing-progressive-hint");
            var confirm=spelling.SubmitButton;
            var confirmText=confirm.GetVisualDescendants().OfType<TextBlock>().First();
            Check(confirmText.Foreground!.ToString()==window.FindResource(window.ActualThemeVariant,"OnPrimaryBrush")!.ToString(),"primary action text uses the theme's contrasting button text color");
            spelling.AnswerButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(typing.ErrorIds.Contains(hintWord.Id),"viewing the full answer schedules the word for retry");
            Field<TextBox>("_learningTypingInput").Text=hintWord.Word;Call("SubmitLearningTyping");
            Check(typing.AssistedCount==1 && typing.UnassistedCorrectCount==0,"assisted spelling success is distinct from unassisted recall");Call("ExitLearningTyping");
            Call("ShowIeltsWriting"); await Shot("writing-small");
            var actualWriter=Field<Grid>("_ieltsSubContent").Children.OfType<Lexi.Features.Ielts.IeltsWritingWorkspace>().Single();
            var writingNumber=actualWriter.CurrentSentence!.Number;
            actualWriter.DraftInput.Text="A draft survives navigation and reopening.";
            Check(actualWriter.FlushPendingSave(),"actual writing workspace saves its draft");
            Call("ShowIeltsCatalog"); Call("ShowIeltsWriting");
            Check(Field<Grid>("_ieltsSubContent").Children.OfType<Lexi.Features.Ielts.IeltsWritingWorkspace>().Single().DraftInput.Text=="A draft survives navigation and reopening.","writing draft survives navigating away and reopening");
            var savedProgress=LearningProgress.Load(Path.Combine(folder,"learning-progress.json"));
            Check(savedProgress.WritingDrafts[writingNumber]=="A draft survives navigation and reopening.","writing draft is present in reopened disk storage");
            window.Width=1100;window.Height=760; await Shot("writing-wide");
            window.RequestedThemeVariant=ThemeVariant.Dark; await Shot("writing-dark");
            exit=0;
        }
        catch(Exception ex){report.Add("ERROR "+ex);}
        finally { File.WriteAllLines(Path.Combine(folder,"improvement-test-result.txt"),report); }
        (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
    }

    private sealed class RecordingAudio : IWordAudioPlayer
    {
        public string? LastText { get; private set; }
        public void Play(string text,string? localRecording=null) => LastText=text;
        public void Stop() { }
        public void Dispose() { }
    }
}
