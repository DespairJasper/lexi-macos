using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Controls.ApplicationLifetimes;
using System.Reflection;
namespace Lexi;
public static class QuickActionUiTests
{
    public static async Task RunAsync(MainWindow main)
    {
        var folder=Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;var report=new List<string>();var exit=0;
        void Check(bool ok,string name){report.Add((ok?"PASS ":"FAIL ")+name);if(!ok)throw new Exception(name);}
        var store=(IVocabularyArchive)typeof(MainWindow).GetField("_vocabService",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
        var quotes=(IQuoteArchive)store;using var dictionary=new DictionaryService();
        var settings=new AppSettings {Material="LiquidGlass",GlassIntensity=.37};
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
            var protocol=(ComboBox)typeof(MainWindow).GetField("_protocolCombo",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
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
            settings.AiProtocol="responses";settings.LookupShortcut="F";settings.TranslateShortcut="G";settings.QuoteShortcut="H";settings.GlassIntensity=.42;store.SaveSettings(settings);var loaded=store.LoadSettings();
            Check(loaded.AiProtocol=="responses"&&loaded.Material=="LiquidGlass"&&loaded.GlassIntensity==.42&&loaded.LookupShortcut=="F"&&loaded.TranslateShortcut=="G"&&loaded.QuoteShortcut=="H","protocol material intensity and shortcuts persist");
            typeof(MainWindow).GetField("_settings",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(main,loaded);
            typeof(MainWindow).GetMethod("LoadSettingsToUi",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,null);
            Check(App.Hotkey!.GetStatus(QuickAction.Lookup).Key=="F"&&App.Hotkey.GetStatus(QuickAction.Translate).Key=="G"&&App.Hotkey.GetStatus(QuickAction.SaveQuote).Key=="H","loaded settings configure actual global shortcuts");
            Check(((TextBox)typeof(MainWindow).GetField("_translateShortcutInput",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!).Text=="G","shortcut inputs follow restored settings");
            main.Close();Check(!main.IsVisible,"closing main window hides instead of exiting");Check(store.GetAllWords().Count>0,"hidden window retains live storage");main.ShowAndActivate();
            Check(main.IsVisible,"hidden main window can reopen");await main.HandleQuickActionAsync(QuickAction.Translate,0);
            Check((typeof(MainWindow).GetField("_quickCard",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main) as Window)?.IsVisible==true,"manual quick action opens input card");
            QuickCardWindow CurrentCard()=>(QuickCardWindow)typeof(MainWindow).GetField("_quickCard",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
            var original="Evidence matters for every decision.";
            CurrentCard().Prepare(QuickAction.Lookup,original,source:"Test editor");
            CurrentCard().Activate();
            // 激活是异步的（macOS 还要等窗口成为 key window）：用有界轮询代替固定等待，
            // 断言本身不变——拿不到焦点仍然失败。
            for(var wait=0;wait<40&&!CurrentCard().IsActive;wait++)await Task.Delay(25);
            
            Check(CurrentCard().IsActive,"selection card owns focus before shortcut correction");
            foreach(var action in new[]{QuickAction.Lookup,QuickAction.Translate,QuickAction.SaveQuote,QuickAction.Translate})
            {
                await main.HandleQuickActionAsync(action,Environment.ProcessId);await Task.Delay(100);
                Check(CurrentCard().Mode==action&&CurrentCard().OriginalInput.Text==original,$"correcting shortcut to {action} retains the full original selection");
                Check(CurrentCard().SelectionSource=="Test editor",$"correcting shortcut to {action} preserves its source");
            }
            main.ShowAndActivate();await Task.Delay(100);
            await main.HandleQuickActionAsync(QuickAction.Lookup,Environment.ProcessId);
            Check(CurrentCard().OriginalInput.Text=="","main window focus does not reuse another card's selection");
            CurrentCard().Close();await main.HandleQuickActionAsync(QuickAction.Translate,Environment.ProcessId);
            Check(CurrentCard().OriginalInput.Text=="","closed card does not leak stale selection into a new session");
            // Explicit quick-card navigation must retain its pre-fix behavior.
            typeof(MainWindow).GetMethod("ShowPage",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,new object[]{"settings"});
            CurrentCard().Prepare(QuickAction.Lookup,"resilient");
            var detail=(Button)typeof(QuickCardWindow).GetField("_detail",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(CurrentCard())!;
            detail.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            Check((string?)typeof(MainWindow).GetField("_currentPage",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)=="lookup"
                &&main.FindControl<TextBox>("LookupInput")!.Text=="resilient","explicit quick-card detail still navigates from settings to lookup");
        }
        catch(Exception ex){report.Add(ex.ToString());exit=1;}
        finally
        {
            card.Close();await File.WriteAllLinesAsync(Path.Combine(folder,"quick-test-result.txt"),report);foreach(var line in report)Console.WriteLine(line);
            main.ForceClose();(Avalonia.Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(exit);
        }
    }
}
