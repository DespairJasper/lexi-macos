using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Lexi;

public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max);
    private QuickCardWindow? _quickCard;
    private int _captureEpoch;
    private TextBox? _lookupShortcutInput,_translateShortcutInput,_quoteShortcutInput;
    private TextBlock? _quickShortcutStatus;
    private Button? _quotesNav;
    private ScrollViewer? _quotesPage;
    private StackPanel? _quotesList;
    private TextBox? _quoteSearch;
    private TextBlock? _quotesSummary;
    private int _quotePageIndex;
    private const int QuotePageSize=50;
    private string QuickText(string zh,string en)=>UiText.Bilingual(zh,en,_settings.UiLanguage);
    private bool AiLookupConfigured=>!string.IsNullOrWhiteSpace(_settings.ApiKey)&&!string.IsNullOrWhiteSpace(_settings.Model);
    private IQuoteArchive QuoteArchive => (IQuoteArchive)_vocabService;

    private void ConfigureQuickActions()
    {
        var shortcuts=new StackPanel {Spacing=8};
        shortcuts.Children.Add(new TextBlock {Text=QuickText("全局快捷卡片","Global quick cards"),FontSize=16,FontWeight=FontWeight.Medium});
        shortcuts.Children.Add(new TextBlock {Text=QuickText("关闭主窗口后继续驻留；托盘菜单“退出” 完全退出。先选中文本再按快捷键。","Closing the main window keeps shortcuts available. Use Quit in the tray menu to exit. Select text before pressing a shortcut."),TextWrapping=TextWrapping.Wrap,FontSize=12});
        TextBox Shortcut(string name,string value,string label){var box=new TextBox {Name=name,Text=value,MaxLength=1,Width=54};var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12};row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center,Width=130});row.Children.Add(new TextBlock {Text="Alt+",VerticalAlignment=VerticalAlignment.Center});row.Children.Add(box);shortcuts.Children.Add(row);return box;}
        _lookupShortcutInput=Shortcut("LookupShortcutInput",_settings.LookupShortcut,QuickText("查词","Look up"));
        _translateShortcutInput=Shortcut("TranslateShortcutInput",_settings.TranslateShortcut,QuickText("句子翻译","Translate"));
        _quoteShortcutInput=Shortcut("QuoteShortcutInput",_settings.QuoteShortcut,QuickText("收藏金句","Save quote"));
        _quickShortcutStatus=new TextBlock {Name="QuickShortcutStatus",TextWrapping=TextWrapping.Wrap,FontSize=11};shortcuts.Children.Add(_quickShortcutStatus);
        var apply=new Button {Content=QuickText("应用快捷键","Apply shortcuts"),Name="ApplyQuickShortcutsBtn"};apply.Click+=(_,_)=>SaveQuickShortcuts();shortcuts.Children.Add(apply);
        SettingsContent.Children.Insert(2,new Border {Classes={"card"},Child=shortcuts});
        Opened+=(_,_)=>Dispatcher.UIThread.Post(UpdateQuickShortcutStatus);
        var translateButton = new Button { Name="OpenTranslateBtn", Content=QuickText("句子翻译","Translate sentence"), Classes={"nav"} };
        translateButton.Click += async (_,_) => await HandleQuickActionAsync(QuickAction.Translate,0); LearningNavHost.Children.Add(translateButton);
        ConfigureQuotesPage();
        if (App.Hotkey != null) App.Hotkey.RegistrationChanged += () => Dispatcher.UIThread.Post(UpdateQuickShortcutStatus);
        Closed+=(_,_)=>{_quickCard?.Close();_quickCard=null;++_captureEpoch;};
        Closing+=(_,_)=>{_quickCard?.CancelPending();};
    }

    private void LoadQuickActionSettings()
    {
        if(_lookupShortcutInput!=null)_lookupShortcutInput.Text=_settings.LookupShortcut;
        if(_translateShortcutInput!=null)_translateShortcutInput.Text=_settings.TranslateShortcut;
        if(_quoteShortcutInput!=null)_quoteShortcutInput.Text=_settings.QuoteShortcut;
        App.Hotkey?.Configure(_settings.LookupShortcut,_settings.TranslateShortcut,_settings.QuoteShortcut);
        UpdateQuickShortcutStatus();
    }

    private AppSettings ReadQuickAiSettings()=>new()
    {
        BaseUrl=SettingsBaseUrlInput.Text?.Trim()??"",Model=SettingsModelInput.Text?.Trim()??"",ApiKey=SettingsApiKeyInput.Text?.Trim()??"",
        AiProtocol=SettingsProtocolCombo.SelectedIndex==1?"responses":"chat",Timeout=SelectedTimeout(),AiContext=SelectedAiContext()
    };
    private int SelectedTimeout()=>SettingsTimeoutCombo.SelectedIndex switch {0=>5,1=>15,2=>30,3=>60,4=>120,_=>30};
    private void SaveQuickShortcuts()
    {
        var keys=new[]{_lookupShortcutInput!.Text,_translateShortcutInput!.Text,_quoteShortcutInput!.Text}.Select(s=>(s??"").Trim().ToUpperInvariant()).ToArray();
        if(keys.Any(k=>k.Length!=1 || k[0]<'A'||k[0]>'Z') || keys.Distinct().Count()!=3){_quickShortcutStatus!.Text=QuickText("请选择三个不同的英文字母。","Choose three different English letters.");return;}
        var oldLookup = _settings.LookupShortcut;
        var oldTranslate = _settings.TranslateShortcut;
        var oldQuote = _settings.QuoteShortcut;
        if (App.Hotkey != null && !App.Hotkey.TryConfigure(keys[0], keys[1], keys[2], out var hotkeyError))
        {
            _quickShortcutStatus!.Text = hotkeyError ?? QuickText("快捷键已被其他程序占用，保留原配置。", "Shortcuts occupied by another application. Kept previous configuration.");
            return;
        }
        _settings.LookupShortcut = keys[0];
        _settings.TranslateShortcut = keys[1];
        _settings.QuoteShortcut = keys[2];
        try
        {
            _vocabService.SaveSettings(_settings);
            UpdateQuickShortcutStatus();
        }
        catch (Exception ex)
        {
            _settings.LookupShortcut = oldLookup;
            _settings.TranslateShortcut = oldTranslate;
            _settings.QuoteShortcut = oldQuote;
            App.Hotkey?.TryConfigure(oldLookup, oldTranslate, oldQuote, out _);
            UpdateQuickShortcutStatus();
            _quickShortcutStatus!.Text = ex.Message;
        }
    }
    private void UpdateQuickShortcutStatus()
    {
        if(_quickShortcutStatus==null)return;
        _quickShortcutStatus.Text=string.Join("\n",Enum.GetValues<QuickAction>().Select(a=>{var s=App.Hotkey?.GetStatus(a);return s is not { } status?QuickText("快捷键未启动","Shortcuts unavailable"):status.Shortcut+" · "+(status.IsRegistered?QuickText("已注册","Registered"):status.Error??QuickText("未注册","Not registered"));}));

    }

    public async Task HandleQuickActionAsync(QuickAction action,nint source)
    {
        if(_isForceClose||_restoring||!_databaseAvailable)return;
        var epoch=++_captureEpoch;_quickCard?.CancelPending();
        string? sourceName=null;
        // A quick card takes focus. A subsequent shortcut corrects its mode
        // using the same original text, rather than trying to copy from Lexi.
        var activeCard=_quickCard is {IsActive:true}?_quickCard:null;
        
        var anchor=GetCursorPos(out var pointer)?new PixelPoint(pointer.X,pointer.Y):new PixelPoint(Position.X+100,Position.Y+100);
        if (source!=0 && activeCard==null) { var title=new StringBuilder(1001); GetWindowText(source,title,title.Capacity); sourceName=title.ToString(); }
        string? selected=activeCard?.OriginalInput.Text;
        if(activeCard!=null)sourceName=activeCard.SelectionSource;
        if(activeCard==null && source!=0 && source!=TryGetPlatformHandle()?.Handle)
        {
            try
            {
                var capture=OperatingSystem.IsWindows()?new SelectionCaptureService(new Win32SelectionClipboard()):null;
                // Keep full text even when Lookup was pressed by mistake;
                // the card validates a word, and Translate/SaveQuote can reuse the sentence.
                if(capture!=null)selected=await capture.CaptureTextAsync(source);
            }
            catch(Exception ex){SetStatus(QuickText("无法读取选区：","Cannot capture selection: ")+ex.Message);}
        }
        if(epoch!=_captureEpoch||_isForceClose||_restoring||!_databaseAvailable)return;
        if (activeCard==null && source!=0 && source!=TryGetPlatformHandle()?.Handle && Win32SelectionClipboard.CurrentForeground!=source) return;
        string? message=null;
        var card=activeCard??CreateQuickCard();card.Prepare(action,selected,captureMessage:message,source:sourceName);
        if(activeCard==null)card.ShowNear(anchor);
        else {card.Activate();card.OriginalInput.Focus();}
        if(!string.IsNullOrWhiteSpace(selected))await card.RunAsync();
    }
    private QuickCardWindow CreateQuickCard()
    {
        _quickCard?.Close();
        var card=new QuickCardWindow(_dictService,_vocabService,QuoteArchive,(text,token)=>new AiService().TranslateAsync(text,_settings,token),_settings,
            ()=>{RefreshWords();if(_currentPage=="quotes")RenderQuotes();},
            word=>{ShowAndActivate();LookupInput.Text=word;_ =PerformLookupAsync();},
            ()=>{ShowAndActivate();ShowPage("quotes");},
            async (word,token)=>AiLookupConfigured?await new AiService().LookupWordAsync(word,_settings,token):null);
        _quickCard=card;card.Closed+=(_,_)=>{if(ReferenceEquals(_quickCard,card))_quickCard=null;};return card;
    }
    internal void OpenQuoteEditor(QuoteItem? quote)
    {
        OpenQuoteEditorPage(quote);
    }
    private void ConfigureQuotesPage()
    {
        _quotesNav=new Button {Name="NavQuotes",Content=QuickText("金句本","Quotes"),Classes={"nav"}}; LearningNavHost.Children.Add(_quotesNav);
        _quotesNav.Click+=(_,_)=>ShowPage("quotes");
        var content=new StackPanel {Spacing=16,MaxWidth=850,HorizontalAlignment=HorizontalAlignment.Stretch};
        content.Children.Add(new TextBlock {Text=QuickText("金句本","Quotes"),FontSize=26,FontWeight=FontWeight.SemiBold});
        var tools=new WrapPanel();_quoteSearch=new TextBox {Name="QuoteSearchInput",Watermark=QuickText("搜索原句、译文、来源或备注","Search sentences, translations, sources or notes"),Width=350,Margin=new Thickness(0,0,8,8)};tools.Children.Add(_quoteSearch);
        var add=new Button {Name="NewQuoteBtn",Content=QuickText("添加金句","Add quote"),Classes={"secondary"},Margin=new Thickness(0,0,8,8)};add.Click+=(_,_)=>OpenQuoteEditor(null);tools.Children.Add(add);
        var export=new Button {Name="ExportQuotesBtn",Content=QuickText("导出全部金句","Export all quotes"),Classes={"ghost"},Margin=new Thickness(0,0,8,8)};export.Click+=async(_,_)=>await ExportQuotesAsync();tools.Children.Add(export);content.Children.Add(tools);
        _quotesSummary=new TextBlock {Name="QuotesSummary",FontSize=12,Opacity=.7};content.Children.Add(_quotesSummary);
        _quotesList=new StackPanel {Name="QuotesList",Spacing=12};content.Children.Add(_quotesList);
        var pages=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12};var previous=new Button {Name="QuotesPrevious",Classes={"ghost"},Content=QuickText("上一页","Previous")};var next=new Button {Name="QuotesNext",Classes={"ghost"},Content=QuickText("下一页","Next")};
        previous.Click+=(_,_)=>{if(_quotePageIndex>0){--_quotePageIndex;RenderQuotes();}};next.Click+=(_,_)=>{if(QuoteArchive.GetQuotes(_quoteSearch.Text??"",1,(_quotePageIndex+1)*QuotePageSize).Count>0){++_quotePageIndex;RenderQuotes();}};pages.Children.Add(previous);pages.Children.Add(next);content.Children.Add(pages);
        _quoteSearch.TextChanged+=(_,_)=>{_quotePageIndex=0;RenderQuotes();};
        _quotesPage=new ScrollViewer {Name="PageQuotes",Content=content,Margin=new Thickness(36,24),IsVisible=false,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
        ((Panel)PageSettings.Parent!).Children.Add(_quotesPage);
    }
    private void RenderQuotes()
    {
        if(_quotesList==null)return;_quotesList.Children.Clear();var quotes=QuoteArchive.GetQuotes(_quoteSearch?.Text??"",QuotePageSize,_quotePageIndex*QuotePageSize);
        while (quotes.Count==0 && _quotePageIndex>0) { --_quotePageIndex; quotes=QuoteArchive.GetQuotes(_quoteSearch?.Text??"",QuotePageSize,_quotePageIndex*QuotePageSize); }
        if(_quotesPage?.Content is Control content)
        {
            foreach(var button in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(content).OfType<Button>())
            {
                if(button.Name=="QuotesPrevious")button.IsEnabled=_quotePageIndex>0;
                if(button.Name=="QuotesNext")button.IsEnabled=QuoteArchive.GetQuotes(_quoteSearch?.Text??"",1,(_quotePageIndex+1)*QuotePageSize).Count>0;
            }
        }
        _quotesSummary!.Text=quotes.Count==0?QuickText("暂无金句。阅读时选中句子，按 Alt+S 收藏。","No quotes yet. Select a sentence while reading and press Alt+S."):QuickText($"第 {_quotePageIndex+1} 页 · {quotes.Count} 条",$"Page {_quotePageIndex+1} · {quotes.Count} quotes");
        foreach(var quote in quotes)
        {
            _quotesList.Children.Add(Lexi.Features.Quotes.QuoteNotebookControl.CreateReadingRow(quote,
                ()=>GetLearningAudio().Play(quote.Original),
                async()=>{if(Clipboard!=null)await Clipboard.SetTextAsync(quote.Original);},
                ()=>OpenQuoteEditor(quote),anchor=>ConfirmQuoteDelete(quote,anchor)));
        }
    }    private async Task ExportQuotesAsync()
    {
        var files=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {Title=QuickText("导出金句","Export quotes"),SuggestedFileName="lexi-quotes.json",DefaultExtension="json"});if(files==null)return;
        try
        {
            await using var stream=await files.OpenWriteAsync();stream.SetLength(0);
            var count=await QuoteExportService.ExportJsonAsync(stream,_vocabService);
            SetStatus(QuickText($"已导出 {count} 条金句。",$"Exported {count} quotes."));
        }
        catch(Exception ex){SetStatus(ex.Message);}
    }
}
