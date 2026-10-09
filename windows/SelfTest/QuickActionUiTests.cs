using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Controls.ApplicationLifetimes;
using System.Reflection;
using Avalonia.VisualTree;
namespace Lexi;
public static class QuickActionUiTests
{
    public static async Task RunAsync(MainWindow main)
    {
        var folder=Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;var report=new List<string>();var exit=0;
        void Check(bool ok,string name){report.Add((ok?"PASS ":"FAIL ")+name);if(!ok)throw new Exception(name);}
        var store=(IVocabularyArchive)typeof(MainWindow).GetField("_vocabService",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
        var quotes=(IQuoteArchive)store;using var dictionary=new DictionaryService();
        var settings=new AppSettings();
        var pending=new TaskCompletionSource<string>();
        var card=new QuickCardWindow(dictionary,store,quotes,(_,_)=>pending.Task,settings,()=>{},_=>{},()=>{});
        async Task Shot(Window window,string name){await Task.Delay(100);using var bmp=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));bmp.Render(window);bmp.Save(Path.Combine(folder,name+".png"));}
        try
        {
            var providerCombo=main.FindControl<ComboBox>("SettingsProviderCombo")!;
            Check(providerCombo.ItemCount==4,"provider list contains only public providers and custom");
            var baseInput=main.FindControl<TextBox>("SettingsBaseUrlInput")!;
            var modelInput=main.FindControl<TextBox>("SettingsModelInput")!;
            var keyInput=main.FindControl<TextBox>("SettingsApiKeyInput")!;
            var protocol=main.FindControl<ComboBox>("SettingsProtocolCombo")!;
            providerCombo.SelectedIndex=0;
            baseInput.Text="https://relay.example.org/agent";modelInput.Text="custom-model";keyInput.Text="test-only";protocol.SelectedIndex=1;
            providerCombo.SelectedIndex=3;
            Check(baseInput.Text=="https://relay.example.org/agent"&&modelInput.Text=="custom-model"&&keyInput.Text=="test-only"&&protocol.SelectedIndex==1,"selecting custom preserves endpoint model credential and Responses protocol");
            var legacy=new AppSettings {Provider="responses",AiProtocol="responses",BaseUrl="https://relay.example.org/agent",Model="custom-model"};
            store.SaveSettings(legacy);var migrated=store.LoadSettings();
            Check(migrated.Provider=="custom"&&migrated.AiProtocol=="responses"&&migrated.BaseUrl==legacy.BaseUrl&&migrated.Model==legacy.Model,"legacy private provider migrates to custom without changing endpoint or protocol");
            if(OperatingSystem.IsMacOS())
            {
                var menu=NativeMenu.GetMenu(Avalonia.Application.Current!);
                Check(menu?.Items.OfType<NativeMenuItem>().Any(i=>i.Header=="退出 lexi" && i.Gesture?.Key==Avalonia.Input.Key.Q)==true,"mac application menu has a working quit entry");
                Check(menu!.Items.OfType<NativeMenuItem>().Count(i=>i.Header is "查词" or "翻译句子" or "收藏金句")==3,"mac application menu exposes all three quick cards");
            }
            card.Prepare(QuickAction.Translate,"Translation to quotes.");card.TranslationInput.Text="翻译收藏测试";
            card.ShowNear(new PixelPoint(500,200));
            card.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="QuickDetailsBtn").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(quotes.GetQuotes("Translation to quotes",50,0).SingleOrDefault()?.Translation=="翻译收藏测试","translation Quotes action saves before opening the notebook");
            quotes.DeleteQuote(quotes.GetQuotes("Translation to quotes",50,0).Single().Id);
            card=new QuickCardWindow(dictionary,store,quotes,(_,_)=>pending.Task,settings,()=>{},_=>{},()=>{});
            card.Prepare(QuickAction.Lookup,"resilient");card.ShowNear(new PixelPoint(500,200));await card.RunAsync();
            Check(!string.IsNullOrWhiteSpace(card.TranslationInput.Text),"lookup card returns offline meaning");card.Save();Check(store.GetAllWords().Any(w=>w.Word=="resilient"),"lookup card saves a word");await Shot(card,"quick-lookup");
            card.Prepare(QuickAction.SaveQuote,"Evidence matters.");var request=card.RunAsync();card.TranslationInput.Text="我编辑的译文";pending.SetResult("模型译文");await request;
            Check(card.TranslationInput.Text=="我编辑的译文","AI response preserves user translation edits");card.Save();Check(quotes.GetQuotes("",50,0).Single().Translation=="我编辑的译文","quote card saves edited translation");await Shot(card,"quick-quote");
            pending=new TaskCompletionSource<string>();card.Prepare(QuickAction.Translate,"First sentence.");request=card.RunAsync();
            card.Prepare(QuickAction.Translate,"Second sentence.");pending.SetResult("旧选区的译文");await request;
            Check(card.TranslationInput.Text=="","late response cannot replace a newer selection");await Shot(card,"quick-translate");
            pending=new TaskCompletionSource<string>();card.Prepare(QuickAction.SaveQuote,"Saved while translating.");request=card.RunAsync();card.TranslationInput.Text="手动保存";card.Save();pending.SetResult("迟到的翻译");await request;
            Check(card.TranslationInput.Text=="手动保存"&&quotes.GetQuotes("Saved while",50,0).Single().Translation=="手动保存","saving during generation cancels stale translation");
            var duplicateBefore=quotes.GetQuotes("",50,0).Count;card.Prepare(QuickAction.SaveQuote,"Evidence matters.");card.Save();
            Check(quotes.GetQuotes("",50,0).Count==duplicateBefore && card.TranslationInput.Text=="我编辑的译文","duplicate card opens existing translation without overwrite");
            settings.AiProtocol="responses";settings.LookupShortcut="F";settings.TranslateShortcut="G";settings.QuoteShortcut="H";store.SaveSettings(settings);var loaded=store.LoadSettings();
            Check(loaded.AiProtocol=="responses"&&loaded.LookupShortcut=="F"&&loaded.TranslateShortcut=="G"&&loaded.QuoteShortcut=="H","protocol and shortcuts persist");
            typeof(MainWindow).GetField("_settings",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(main,loaded);
            typeof(MainWindow).GetMethod("LoadSettingsToUi",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,null);
            for(var attempt=0;attempt<100 && App.Hotkey!.GetStatus(QuickAction.Lookup).Key!="F";attempt++) await Task.Delay(10);
            Check(App.Hotkey!.GetStatus(QuickAction.Lookup).Key=="F"&&App.Hotkey.GetStatus(QuickAction.Translate).Key=="G"&&App.Hotkey.GetStatus(QuickAction.SaveQuote).Key=="H","loaded settings configure actual global shortcuts");
            Check(((TextBox)typeof(MainWindow).GetField("_translateShortcutInput",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!).Text=="G","shortcut inputs follow restored settings");
            main.Close();Check(!main.IsVisible,"closing main window hides instead of exiting");Check(store.GetAllWords().Count>0,"hidden window retains live storage");main.ShowAndActivate();
            Check(main.IsVisible,"hidden main window can reopen");await main.HandleQuickActionAsync(QuickAction.Translate,0);
            Check((typeof(MainWindow).GetField("_quickCard",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main) as Window)?.IsVisible==true,"manual quick action opens input card");
            QuickCardWindow CurrentCard()=>(QuickCardWindow)typeof(MainWindow).GetField("_quickCard",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
            var original="Evidence matters for every decision.";
            CurrentCard().Prepare(QuickAction.Lookup,original,source:"Test editor");
            CurrentCard().Activate();await Task.Delay(100);
            Check(CurrentCard().IsActive,"selection card owns focus before shortcut correction");
            foreach(var action in new[]{QuickAction.Lookup,QuickAction.Translate,QuickAction.SaveQuote,QuickAction.Translate})
            {
                await main.HandleQuickActionAsync(action,0);await Task.Delay(100);
                Check(CurrentCard().Mode==action&&CurrentCard().OriginalInput.Text==original,$"correcting shortcut to {action} retains the full original selection");
                Check(CurrentCard().SelectionSource=="Test editor",$"correcting shortcut to {action} preserves its source");
            }
            main.ShowAndActivate();await Task.Delay(100);
            await main.HandleQuickActionAsync(QuickAction.Lookup,0);
            Check(CurrentCard().OriginalInput.Text=="","main window focus does not reuse another card's selection");
            CurrentCard().Close();await main.HandleQuickActionAsync(QuickAction.Translate,0);
            Check(CurrentCard().OriginalInput.Text=="","closed card does not leak stale selection into a new session");
            CurrentCard().Close();card.Close();
            object? Call(string name,params object?[] arguments)=>typeof(MainWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,arguments);
            void Set(string name,object? value)=>typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(main,value);
            IVocabularyArchive CurrentStore()=>(IVocabularyArchive)typeof(MainWindow).GetField("_vocabService",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
            foreach(var (index,seconds) in new[]{(3,60),(4,120)})
            {
                baseInput.Text="https://relay.example.org/agent";modelInput.Text="test-model";keyInput.Text="";
                var timeout=main.FindControl<ComboBox>("SettingsTimeoutCombo")!;
                Check(timeout.ItemCount>=5,"long request timeouts are selectable");
                timeout.SelectedIndex=index;Call("OnSaveSettingsClicked");
                Check(CurrentStore().LoadSettings().Timeout==seconds,$"{seconds} second timeout persists through actual settings save");
                Call("LoadSettingsToUi");Check(timeout.SelectedIndex==index,$"{seconds} second timeout reloads into matching UI selection");
            }

            var oldQuote=quotes.GetQuotes("Evidence",50,0).Single();
            Call("ShowPage","quotes");main.OpenQuoteEditor(oldQuote);await Task.Delay(100);main.UpdateLayout();
            Border Editor()=>(Border)typeof(MainWindow).GetField("_quoteEditorHost",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
            TextBox EditorInput(string name)=>Editor().GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name==name);
            var oldEditor=Editor();EditorInput("QuoteTranslationInput").Text="旧库编辑中";
            var replacementPath=Path.Combine(folder,"quick-replacement.sqlite3");
            using(var replacement=new VocabularyService(replacementPath))
            {
                var replacementQuotes=(IQuoteArchive)replacement;
                for(long id=1;id<oldQuote.Id;id++)
                {
                    var placeholder=replacementQuotes.SaveQuote(null,"ID fixture "+id,"","","");
                    replacementQuotes.DeleteQuote(placeholder.Id);
                }
                var changed=((IQuoteArchive)replacement).SaveQuote(null,"Replacement sentence.","恢复后的不同金句","replacement source","");
                Check(changed.Id==oldQuote.Id,"restore fixture reuses the quote ID with different content");
                replacement.SaveSettings(new AppSettings {Provider="custom",AiProtocol="responses",Timeout=120,BaseUrl="https://relay.example.org/agent",Model="test-model"});
            }
            Set("_pendingRestore",replacementPath);await (Task)Call("RestoreChosenBackupAsync")!;
            Check(ReferenceEquals(oldEditor,Editor()) && ((IQuoteArchive)CurrentStore()).GetQuotes().Single(q=>q.Id==oldQuote.Id).Original==oldQuote.Original,"dirty quote editor blocks restore and preserves current archive");
            Call("CloseQuoteEditor");
            Set("_pendingRestore",replacementPath);await (Task)Call("RestoreChosenBackupAsync")!;
            Check(typeof(MainWindow).GetField("_quoteEditorHost",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)==null,"explicit cancellation releases quote editor before restore");
            var newQuotes=(IQuoteArchive)CurrentStore();
            Check(newQuotes.GetQuotes().Single(q=>q.Id==oldQuote.Id).Original=="Replacement sentence.","same quote ID resolves to restored content");
            var rendered=(StackPanel)typeof(MainWindow).GetField("_quotesList",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
            var visibleText=rendered.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t=>t.Text).ToList();
            Check(visibleText.Contains("Replacement sentence.")&&!visibleText.Contains(oldQuote.Original),"quotes page refreshes to restored records without stale same-ID content");
            main.OpenQuoteEditor(newQuotes.GetQuotes().Single(q=>q.Id==oldQuote.Id));
            await Task.Delay(100);main.UpdateLayout();
            Check(EditorInput("QuoteOriginalInput").Text=="Replacement sentence." && EditorInput("QuoteTranslationInput").Text=="恢复后的不同金句","new quote editor binds to the restored archive");
            Call("CloseQuoteEditor");

            var late=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var changeCallbacks=0;var lateStore=CurrentStore();var lateQuoteArchive=(IQuoteArchive)lateStore;
            var staleCard=new QuickCardWindow(dictionary,lateStore,lateQuoteArchive,(_,_)=>late.Task,new AppSettings(),()=>changeCallbacks++,_=>{},()=>{});
            staleCard.Prepare(QuickAction.SaveQuote,"Pending before restore.");staleCard.ShowNear(new PixelPoint(500,200));
            Set("_quickCard",staleCard);var lateRun=staleCard.RunAsync();
            Set("_pendingRestore",replacementPath);await (Task)Call("RestoreChosenBackupAsync")!;
            late.SetResult("恢复前迟到译文");await lateRun;
            Check(!staleCard.IsVisible && staleCard.TranslationInput.Text=="" && changeCallbacks==0,"late quote translation cannot update closed card or invoke old archive callbacks after restore");
            Check(((IQuoteArchive)CurrentStore()).GetQuotes("Pending before restore",50,0).Count==0,"late completion never writes the restored archive");
            Check(CurrentStore().LoadSettings().Timeout==120 && main.FindControl<ComboBox>("SettingsTimeoutCombo")!.SelectedIndex==4,"restore preserves 120 second timeout and its actual UI selection");
        }
        catch(Exception ex){report.Add(ex.ToString());exit=1;}
        finally
        {
            card.Close();await File.WriteAllLinesAsync(Path.Combine(folder,"quick-test-result.txt"),report);foreach(var line in report)Console.WriteLine(line);
            main.ForceClose();(Avalonia.Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(exit);
        }
    }
}
