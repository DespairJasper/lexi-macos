using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;
using System.Text.Json;

namespace Lexi;

public partial class MainWindow
{
    private QuickCardWindow? _quickCard;
    private int _captureEpoch;
    private ComboBox? _protocolCombo;
    private TextBox? _lookupShortcutInput,_translateShortcutInput,_quoteShortcutInput;
    private TextBlock? _quickShortcutStatus;
    private Button? _quotesNav;
    private ScrollViewer? _quotesPage;
    private StackPanel? _quotesList;
    private TextBox? _quoteSearch;
    private TextBlock? _quotesSummary;
    private int _quotePageIndex;
    private const int QuotePageSize=50;
    private string QuickText(string zh,string en)=>_settings.UiLanguage=="en"?en:zh;
    private bool AiLookupConfigured=>!string.IsNullOrWhiteSpace(_settings.ApiKey)&&!string.IsNullOrWhiteSpace(_settings.Model);
    private IQuoteArchive QuoteArchive => (IQuoteArchive)_vocabService;

    private void ConfigureQuickActions()
    {
        _protocolCombo=new ComboBox {Name="SettingsProtocolCombo",ItemsSource=new[]{"Chat Completions","Responses"},SelectedIndex=_settings.AiProtocol=="responses"?1:0};
        var modelPanel=(StackPanel)SettingsBaseUrlInput.Parent!;
        modelPanel.Children.Add(new TextBlock {Text=QuickText("接口协议","API protocol"),FontSize=12});modelPanel.Children.Add(_protocolCombo);
        var testButton=new Button {Name="SettingsTestConnectionBtn",Content=QuickText("测试连接","Test connection")};
        testButton.Click+=async(_,_)=>
        {
            var config=ReadQuickAiSettings();testButton.IsEnabled=false;
            using var request=new CancellationTokenSource();
            SetStatus(QuickText("正在测试连接…","Testing connection…"));
            try{var response=await new AiService().TranslateAsync("Evidence matters.",config,request.Token);SetStatus(QuickText("连接成功：","Connected: ")+response);}
            catch(Exception ex){SetStatus(ex.Message);}finally{testButton.IsEnabled=true;}
        };
        modelPanel.Children.Add(testButton);
        SettingsTimeoutCombo.Items.Add(new ComboBoxItem {Content=QuickText("60 秒","60 seconds")});SettingsTimeoutCombo.Items.Add(new ComboBoxItem {Content=QuickText("120 秒 · 慢速中转","120 seconds · slow relay")});
        var shortcuts=new StackPanel {Spacing=8};
        shortcuts.Children.Add(new TextBlock {Text=QuickText("全局快捷卡片","Global quick cards"),FontSize=16,FontWeight=FontWeight.Medium});
        shortcuts.Children.Add(new TextBlock {Text=QuickText("关闭主窗口后继续驻留；⌘Q 完全退出。先选中文本再按快捷键。","Closing the main window keeps shortcuts available; ⌘Q quits. Select text before pressing a shortcut."),TextWrapping=TextWrapping.Wrap,FontSize=12});
        TextBox Shortcut(string name,string value,string label){var box=new TextBox {Name=name,Text=value,MaxLength=1,Width=54};var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12};row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center,Width=130});row.Children.Add(new TextBlock {Text="⌥",VerticalAlignment=VerticalAlignment.Center});row.Children.Add(box);shortcuts.Children.Add(row);return box;}
        _lookupShortcutInput=Shortcut("LookupShortcutInput",_settings.LookupShortcut,QuickText("查词","Look up"));
        _translateShortcutInput=Shortcut("TranslateShortcutInput",_settings.TranslateShortcut,QuickText("句子翻译","Translate"));
        _quoteShortcutInput=Shortcut("QuoteShortcutInput",_settings.QuoteShortcut,QuickText("收藏金句","Save quote"));
        _quickShortcutStatus=new TextBlock {Name="QuickShortcutStatus",TextWrapping=TextWrapping.Wrap,FontSize=11};shortcuts.Children.Add(_quickShortcutStatus);
        var apply=new Button {Content=QuickText("应用快捷键","Apply shortcuts"),Name="ApplyQuickShortcutsBtn"};apply.Click+=(_,_)=>SaveQuickShortcuts();shortcuts.Children.Add(apply);
        SettingsContent.Children.Insert(2,new Border {Classes={"card"},Child=shortcuts});
        Opened+=(_,_)=>Dispatcher.UIThread.Post(UpdateQuickShortcutStatus);
        ConfigureQuotesPage();
        Closed+=(_,_)=>{_quickCard?.Close();_quickCard=null;++_captureEpoch;};
        Closing+=(_,_)=>{_quickCard?.CancelPending();SaveGlassSettings();};
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
        AiProtocol=_protocolCombo?.SelectedIndex==1?"responses":"chat",Timeout=SelectedTimeout(),AiContext=SelectedAiContext()
    };
    private int SelectedTimeout()=>SettingsTimeoutCombo.SelectedIndex switch {0=>5,1=>15,2=>30,3=>60,4=>120,_=>30};
    private void SaveQuickShortcuts()
    {
        var keys=new[]{_lookupShortcutInput!.Text,_translateShortcutInput!.Text,_quoteShortcutInput!.Text}.Select(s=>(s??"").Trim().ToUpperInvariant()).ToArray();
        if(keys.Any(k=>k.Length!=1 || k[0]<'A'||k[0]>'Z') || keys.Distinct().Count()!=3){_quickShortcutStatus!.Text=QuickText("请选择三个不同的英文字母。","Choose three different English letters.");return;}
        _settings.LookupShortcut=keys[0];_settings.TranslateShortcut=keys[1];_settings.QuoteShortcut=keys[2];
        try{_vocabService.SaveSettings(_settings);App.Hotkey?.Configure(keys[0],keys[1],keys[2]);UpdateQuickShortcutStatus();}
        catch(Exception ex){_quickShortcutStatus!.Text=ex.Message;}
    }
    private void UpdateQuickShortcutStatus()
    {
        if(_quickShortcutStatus==null)return;
        _quickShortcutStatus.Text=string.Join("\n",Enum.GetValues<QuickAction>().Select(a=>{var s=App.Hotkey?.GetStatus(a);return s is not { } status?QuickText("快捷键未启动","Shortcuts unavailable"):status.Shortcut+" · "+(status.IsRegistered?QuickText("已注册","Registered"):status.Error??QuickText("未注册","Not registered"));}));
        ApplyLayout();
    }

    public async Task HandleQuickActionAsync(QuickAction action,nint source)
    {
        if(_isForceClose||_restoring||!_databaseAvailable)return;
        var epoch=++_captureEpoch;_quickCard?.CancelPending();
        string? sourceName=null;
        // A quick card takes focus. A subsequent shortcut corrects its mode
        // using the same original text, rather than trying to copy from Lexi.
        var activeCard=source==Environment.ProcessId&&_quickCard is {IsActive:true}?_quickCard:null;
        if(source!=0&&source!=Environment.ProcessId)try{using var process=System.Diagnostics.Process.GetProcessById((int)source);sourceName=process.ProcessName;}catch(ArgumentException){ }
        var anchor=MacPointerLocation.Get(Screens,new PixelPoint(Position.X+100,Position.Y+100));
        string? selected=activeCard?.OriginalInput.Text;
        if(activeCard!=null)sourceName=activeCard.SelectionSource;
        if(source!=0 && source!=Environment.ProcessId)
        {
            try
            {
                var capture=SelectionCaptureService.CreateForCurrentPlatform();
                // Keep full text even when Lookup was pressed by mistake;
                // the card validates a word, and Translate/SaveQuote can reuse the sentence.
                if(capture!=null)selected=await capture.CaptureTextAsync(source);
            }
            catch(Exception ex){SetStatus(QuickText("无法读取选区：","Cannot capture selection: ")+ex.Message);}
            if(OperatingSystem.IsMacOS() && MacOSSelectionClipboard.CurrentForeground!=source)return;
        }
        if(epoch!=_captureEpoch||_isForceClose)return;
        var permissionMissing=OperatingSystem.IsMacOS()&&!MacOSSelectionClipboard.HasAccessibilityPermission;
        if(permissionMissing&&string.IsNullOrWhiteSpace(selected))MacOSNative.RequestAccessibilityPermission();
        var message=string.IsNullOrWhiteSpace(selected)&&permissionMissing
            ?QuickText("需开启「辅助功能」权限；也可直接输入。","Allow Accessibility for Lexi in System Settings, or type here."):null;
        var card=CreateQuickCard();card.Prepare(action,selected,captureMessage:message,source:sourceName);card.ShowNear(anchor);
        if(!string.IsNullOrWhiteSpace(selected))await card.RunAsync();
    }
    private QuickCardWindow CreateQuickCard()
    {
        _quickCard?.Close();
        var card=new QuickCardWindow(_dictService,_vocabService,QuoteArchive,(text,token)=>new AiService().TranslateAsync(text,_settings,token),_settings,
            ()=>{RefreshWords();if(_currentPage=="quotes")RenderQuotes();},
            word=>{ShowAndActivate(preservePage:false);LookupInput.Text=word;_ =PerformLookupAsync();},
            ()=>{ShowAndActivate();ShowPage("quotes");},
            async (word,token)=>AiLookupConfigured?await new AiService().LookupWordAsync(word,_settings,token):null);
        _quickCard=card;card.Closed+=(_,_)=>{if(ReferenceEquals(_quickCard,card))_quickCard=null;};return card;
    }
    internal void OpenQuoteEditor(QuoteItem? quote)
    {
        var card=CreateQuickCard();card.Prepare(QuickAction.SaveQuote,quote?.Original,quote);card.ShowNear(new PixelPoint(Position.X+120,Position.Y+100));
    }
    private void ConfigureQuotesPage()
    {
        var sidebar=(Grid)((Border)this.FindControl<Border>("SidebarShell")!).Child!;
        sidebar.RowDefinitions.Insert(5,new RowDefinition(GridLength.Auto));
        foreach(var child in sidebar.Children)if(Grid.GetRow(child)>=5)Grid.SetRow(child,Grid.GetRow(child)+1);
        _quotesNav=new Button {Name="NavQuotes",Content=QuickText("金句本","Quotes"),Classes={"nav"}};Grid.SetRow(_quotesNav,5);sidebar.Children.Add(_quotesNav);
        _quotesNav.Click+=(_,_)=>ShowPage("quotes");
        var content=new StackPanel {Spacing=16,MaxWidth=850,HorizontalAlignment=HorizontalAlignment.Stretch};
        content.Children.Add(new TextBlock {Text=QuickText("金句本","Quotes"),FontSize=28,FontWeight=FontWeight.SemiBold});
        var tools=new WrapPanel();_quoteSearch=new TextBox {Name="QuoteSearchInput",Watermark=QuickText("搜索原句、译文、来源或备注","Search sentences, translations, sources or notes"),Width=350,Margin=new Thickness(0,0,8,8)};tools.Children.Add(_quoteSearch);
        var add=new Button {Name="NewQuoteBtn",Content=QuickText("添加金句","Add quote"),Margin=new Thickness(0,0,8,8)};add.Click+=(_,_)=>OpenQuoteEditor(null);tools.Children.Add(add);
        var export=new Button {Name="ExportQuotesBtn",Content=QuickText("导出全部金句","Export all quotes"),Margin=new Thickness(0,0,8,8)};export.Click+=async(_,_)=>await ExportQuotesAsync();tools.Children.Add(export);content.Children.Add(tools);
        _quotesSummary=new TextBlock {Name="QuotesSummary",FontSize=12,Opacity=.7};content.Children.Add(_quotesSummary);
        _quotesList=new StackPanel {Name="QuotesList",Spacing=12};content.Children.Add(_quotesList);
        var pages=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12};var previous=new Button {Content=QuickText("上一页","Previous")};var next=new Button {Content=QuickText("下一页","Next")};
        previous.Click+=(_,_)=>{if(_quotePageIndex>0){--_quotePageIndex;RenderQuotes();}};next.Click+=(_,_)=>{if(QuoteArchive.GetQuotes(_quoteSearch.Text??"",1,(_quotePageIndex+1)*QuotePageSize).Count>0){++_quotePageIndex;RenderQuotes();}};pages.Children.Add(previous);pages.Children.Add(next);content.Children.Add(pages);
        _quoteSearch.TextChanged+=(_,_)=>{_quotePageIndex=0;RenderQuotes();};
        _quotesPage=new ScrollViewer {Name="PageQuotes",Content=content,Margin=new Thickness(36,24),IsVisible=false,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
        ((Panel)PageSettings.Parent!).Children.Add(_quotesPage);
    }
    private void RenderQuotes()
    {
        if(_quotesList==null)return;_quotesList.Children.Clear();var quotes=QuoteArchive.GetQuotes(_quoteSearch?.Text??"",QuotePageSize,_quotePageIndex*QuotePageSize);
        _quotesSummary!.Text=quotes.Count==0?QuickText("暂无金句。阅读时选中句子，按 ⌥S 收藏。","No quotes yet. Select a sentence while reading and press ⌥S."):QuickText($"第 {_quotePageIndex+1} 页 · {quotes.Count} 条",$"Page {_quotePageIndex+1} · {quotes.Count} quotes");
        foreach(var quote in quotes)
        {
            var stack=new StackPanel {Spacing=10};stack.Children.Add(new TextBlock {Text=quote.Original,TextWrapping=TextWrapping.Wrap,FontSize=17,FontWeight=FontWeight.Medium});
            stack.Children.Add(new TextBlock {Text=quote.Translation,TextWrapping=TextWrapping.Wrap,FontSize=14});
            if(quote.Source.Length>0)stack.Children.Add(new TextBlock {Text=quote.Source,FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
            if(quote.Notes.Length>0)stack.Children.Add(new TextBlock {Text=quote.Notes,FontSize=12,Opacity=.7,TextWrapping=TextWrapping.Wrap});
            var actions=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12};var edit=new Button {Content=QuickText("编辑","Edit")};edit.Click+=(_,_)=>OpenQuoteEditor(quote);var delete=new Button {Content=QuickText("删除…","Delete…")};
            delete.Click+=(_,_)=>
            {
                delete.IsVisible=false;
                var confirm=new Button {Content=QuickText("确认删除","Confirm delete")};var cancel=new Button {Content=QuickText("取消","Cancel")};actions.Children.Add(confirm);actions.Children.Add(cancel);
                cancel.Click+=(_,_)=>{actions.Children.Remove(confirm);actions.Children.Remove(cancel);delete.IsVisible=true;};
                confirm.Click+=(_,_)=>{try{QuoteArchive.DeleteQuote(quote.Id);RenderQuotes();SetStatus(QuickText("金句已删除，可从 SQLite 备份恢复。","Quote deleted. SQLite backups can restore it."));}catch(Exception ex){SetStatus(ex.Message);}};
            };
            actions.Children.Add(edit);actions.Children.Add(delete);stack.Children.Add(actions);_quotesList.Children.Add(new Border {Classes={"card"},Child=stack});
        }
    }
    private async Task ExportQuotesAsync()
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
