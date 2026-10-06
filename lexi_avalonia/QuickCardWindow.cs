using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.Threading;

namespace Lexi;

/// <summary>A single selection card; generations and edits own their responses.</summary>
internal sealed class QuickCardWindow : Window
{
    private readonly IDictionaryLookup _dictionary;
    private readonly IVocabularyArchive _words;
    private readonly IQuoteArchive _quotes;
    private readonly Func<string,CancellationToken,Task<string>> _translate;
    private readonly Func<string,CancellationToken,Task<LookupResult?>>? _aiLookup;
    private readonly Action _changed;
    private readonly Action<string> _openWord;
    private readonly Action _openQuotes;
    private readonly LocalWordAudioPlayer _audio;
    private readonly TextBlock _heading = new() { FontSize=18,FontWeight=FontWeight.SemiBold };
    private readonly TextBlock _phonetic = new() { FontSize=13,Opacity=.7 };
    private readonly TextBlock _status = new() { FontSize=12,TextWrapping=TextWrapping.Wrap };
    internal TextBox OriginalInput { get; } = new() { Name="QuickOriginalInput",MaxLength=4096,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=58,MaxHeight=140 };
    internal TextBox TranslationInput { get; } = new() { Name="QuickTranslationInput",MaxLength=8192,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=100,MaxHeight=220 };
    private readonly TextBox _source = new() { Name="QuickSourceInput",MaxLength=1000 };
    private readonly TextBox _notes = new() { Name="QuickNotesInput",MaxLength=4096,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=90 };
    private readonly StackPanel _metadata = new() { Spacing=6 };
    private readonly Button _run = new() { Name="QuickRunBtn" };
    private readonly Button _save = new() { Name="QuickSaveBtn" };
    private readonly Button _cancel = new() { Name="QuickCancelBtn" };
    private readonly Button _copy = new() { Name="QuickCopyBtn" };
    private readonly Button _detail = new() { Name="QuickDetailsBtn" };
    private readonly Button _pronounce = new() { Name="QuickPronounceBtn" };
    private readonly Button _allow = new() { Name="QuickAllowBtn" };
    private QuickAction _mode;
    private LookupResult? _lookup;
    private long? _quoteId;
    private CancellationTokenSource? _request;
    private int _generation;
    private int _translationRevision;
    private bool _preparing;
    private bool _closed;
    private AppSettings _appearance;
    private Border? _surface;
    internal string Status => _status.Text ?? "";
    internal QuickAction Mode => _mode;
    internal string SelectionSource => _source.Text ?? "";
    private string L(string zh,string en) => _appearance.UiLanguage=="en"?en:zh;

    internal QuickCardWindow(IDictionaryLookup dictionary,IVocabularyArchive words,IQuoteArchive quotes,
        Func<string,CancellationToken,Task<string>> translate,AppSettings settings,Action changed,Action<string> openWord,Action openQuotes,
        Func<string,CancellationToken,Task<LookupResult?>>? aiLookup=null)
    {
        _dictionary=dictionary;_words=words;_quotes=quotes;_translate=translate;_appearance=settings;
        _changed=changed;_openWord=openWord;_openQuotes=openQuotes;_aiLookup=aiLookup;_audio=new LocalWordAudioPlayer(s=>Avalonia.Threading.Dispatcher.UIThread.Post(()=>_status.Text=s));
        Title="lexi · Quick Card";Name="QuickCardWindow";Width=420;MinWidth=320;Height=470;MinHeight=300;MaxHeight=620;
        CanResize=true;ShowInTaskbar=false;Topmost=true;WindowStartupLocation=WindowStartupLocation.Manual;
        SystemDecorations=SystemDecorations.Full;ExtendClientAreaToDecorationsHint=true;ExtendClientAreaChromeHints=Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint=38;Background=Brushes.Transparent;
        RequestedThemeVariant=settings.Theme=="Dark"?ThemeVariant.Dark:ThemeVariant.Light;
        Foreground=new SolidColorBrush(Color.Parse(settings.Theme=="Dark"?"#ECF5FD":"#172E43"));
        var close=MakeButton(L("关闭","Close"),"QuickCloseBtn");close.Click+=(_,_)=>Close();
        var header=new Grid { Name="QuickDragHeader",Background=Brushes.Transparent,MinHeight=36,ColumnDefinitions=new ColumnDefinitions("*,Auto"),Margin=new Thickness(0,4,0,0),Cursor=new Cursor(StandardCursorType.SizeAll) };header.Children.Add(_heading);Grid.SetColumn(close,1);header.Children.Add(close);
        ToolTip.SetTip(header,L("拖动移动卡片","Drag to move the card"));close.Cursor=new Cursor(StandardCursorType.Arrow);
        header.PointerPressed+=(_,e)=>{ if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button){BeginMoveDrag(e);e.Handled=true;} };
        PositionChanged+=(_,_)=>{if(Environment.GetEnvironmentVariable("LEXI_HOTKEY_TRACE")=="1")Console.Error.WriteLine($"[QuickCard] position={Position.X},{Position.Y}");};
        _metadata.Children.Add(Label(L("来源 / 标题","Source / title")));_metadata.Children.Add(_source);
        _metadata.Children.Add(Label(L("备注","Notes")));_metadata.Children.Add(_notes);
        var body=new StackPanel {Spacing=12};body.Children.Add(header);body.Children.Add(OriginalInput);body.Children.Add(_phonetic);
        body.Children.Add(TranslationInput);body.Children.Add(_metadata);
        _run.Classes.Add("primary");_save.Classes.Add("primary");
        _allow.Content=L("打开系统设置","Open System Settings");_allow.Margin=new Thickness(0,0,6,6);_allow.IsVisible=false;
        _allow.Click+=(_,_)=>MacOSNative.OpenAccessibilitySettings();
        var actions=new WrapPanel {Orientation=Orientation.Horizontal};foreach(var button in new[]{_run,_save,_copy,_pronounce,_detail,_cancel}) {button.Margin=new Thickness(0,0,6,6);actions.Children.Add(button);}body.Children.Add(actions);body.Children.Add(_status);
        body.Children.Add(_allow);
        var surface=new Border {Padding=new Thickness(20,38,20,18),CornerRadius=new CornerRadius(14),Child=new ScrollViewer {Content=body,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden},
            Background=new SolidColorBrush(Color.Parse(settings.Theme=="Dark"?"#CC152332":"#DDF1F7FC"))};
        if(settings.OpaqueMaterial||settings.HighContrast)surface.Background=new SolidColorBrush(Color.Parse(settings.Theme=="Dark"?"#152332":"#F7FAFD"));
        if(settings.Material=="LiquidGlass"&&!settings.OpaqueMaterial&&!settings.HighContrast)surface.Background=new SolidColorBrush(Color.Parse(settings.Theme=="Dark"?"#40152332":"#40F1F7FC"));
        if(settings.HighContrast){surface.Background=settings.Theme=="Dark"?Brushes.Black:Brushes.White;Foreground=settings.Theme=="Dark"?Brushes.White:Brushes.Black;}
        else if(settings.Material=="LiquidGlass")surface.Background=new SolidColorBrush(MacGlassMaterial.SurfaceColor(settings,card:true));
        _surface=surface;
        Content=surface;
        _run.Click+=async(_,_)=>await RunAsync();_save.Click+=(_,_)=>Save();
        _cancel.Click+=(_,_)=>{CancelPending();_status.Text=L("已取消，可重新生成或手动填写。","Canceled. Retry or enter your own translation.");};
        _copy.Click+=async(_,_)=>{if(Clipboard!=null && !string.IsNullOrWhiteSpace(TranslationInput.Text)){await Clipboard.SetTextAsync(TranslationInput.Text);_status.Text=L("译文已复制。","Translation copied.");}};
        _detail.Click+=(_,_)=>{if(_mode==QuickAction.Lookup)_openWord(OriginalInput.Text?.Trim()??"");else _openQuotes();Close();};
        _pronounce.Click+=(_,_)=>_audio.Play(OriginalInput.Text??"");
        OriginalInput.PropertyChanged+=(_,e)=>{if(e.Property!=TextBox.TextProperty||_preparing)return;CancelPending();_lookup=null;_save.IsEnabled=_mode==QuickAction.SaveQuote;_status.Text=L("内容已修改，点击按钮重新查询或翻译。","Text changed. Search or translate again.");};
        TranslationInput.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty&&!_preparing)++_translationRevision;};
        KeyDown+=async(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}else if(e.Key==Key.Enter && (_mode==QuickAction.Lookup || e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))){e.Handled=true;await RunAsync();}};
        Closed+=(_,_)=>{_closed=true;CancelPending();_audio.Stop();};
        Opened+=(_,_)=>MacGlassMaterial.Apply(this,_appearance);
    }
    private static TextBlock Label(string text)=>new(){Text=text,FontSize=11,Opacity=.7};
    private static Button MakeButton(string content,string name)=>new(){Content=content,Name=name,Padding=new Thickness(10,6),MinHeight=30};

    internal void ApplySettings(AppSettings settings)
    {
        _appearance=settings;RequestedThemeVariant=settings.Theme=="Dark"?ThemeVariant.Dark:ThemeVariant.Light;
        if(_surface!=null)
        {
            _surface.Background=settings.HighContrast?(settings.Theme=="Dark"?Brushes.Black:Brushes.White):new SolidColorBrush(MacGlassMaterial.SurfaceColor(settings,card:true));
            Foreground=settings.HighContrast?(settings.Theme=="Dark"?Brushes.White:Brushes.Black):new SolidColorBrush(Color.Parse(settings.Theme=="Dark"?"#ECF5FD":"#172E43"));
        }
        MacGlassMaterial.Apply(this,settings);
    }

    internal void Prepare(QuickAction mode,string? text,QuoteItem? quote=null,string? captureMessage=null,string? source=null)
    {
        CancelPending();_mode=mode;_quoteId=quote?.Id;_lookup=null;_preparing=true;
        OriginalInput.Text=quote?.Original??text??"";TranslationInput.Text=quote?.Translation??"";_source.Text=quote?.Source??source??"";_notes.Text=quote?.Notes??"";
        _preparing=false;_translationRevision=0;
        _heading.Text=mode switch{QuickAction.Lookup=>L("查词","Look up"),QuickAction.Translate=>L("句子翻译","Translate sentence"),_=>L("收藏金句","Save quote")};
        Title="lexi · "+_heading.Text;
        _run.Content=mode==QuickAction.Lookup?L("查询","Search"):L("翻译","Translate");_save.Content=mode==QuickAction.Lookup?L("加入生词本","Save word"):L("保存金句","Save quote");
        _copy.Content=L("复制译文","Copy");_cancel.Content=L("取消生成","Cancel");_detail.Content=mode==QuickAction.Lookup?L("完整档案","Open word"):L("金句本","Quotes");_pronounce.Content=L("读音","Listen");
        _metadata.IsVisible=mode==QuickAction.SaveQuote;_save.IsVisible=mode!=QuickAction.Translate;_save.IsEnabled=mode==QuickAction.SaveQuote;
        _phonetic.IsVisible=_pronounce.IsVisible=mode==QuickAction.Lookup;_phonetic.Text="";
        TranslationInput.IsReadOnly=mode==QuickAction.Lookup;_cancel.IsVisible=false;
        Height=mode==QuickAction.SaveQuote?560:420;
        _allow.IsVisible=captureMessage!=null;_status.Text=captureMessage??"";
    }

    internal async Task RunAsync()
    {
        var text=OriginalInput.Text?.Trim()??"";CancelPending();var version=_generation;
        if(text.Length==0){_status.Text=L("请先输入原文。","Enter text first.");return;}
        if(_mode==QuickAction.Lookup && SelectionCaptureService.NormalizeWord(text)==null){_status.Text=L("请输入单个英文词；句子请用 ⌥A。","Enter one English word; use ⌥A for sentences.");return;}
        var revision=_translationRevision;var request=_request=new CancellationTokenSource();_run.IsEnabled=false;_cancel.IsVisible=_mode!=QuickAction.Lookup;
        _status.Text=_mode==QuickAction.Lookup?L("查询中…","Looking up…"):L("翻译中… 可取消","Translating… Cancel anytime");
        try
        {
            if(_mode==QuickAction.Lookup)
            {
                var personal=_words.GetAllWords().FirstOrDefault(w=>w.Word.Equals(text,StringComparison.OrdinalIgnoreCase));
                var result=personal==null?await _dictionary.LookupAsync(text):new LookupResult{Word=personal.Word,Phonetic=personal.Phonetic,Translation=personal.Translation,Definition=personal.Definition,Found=true};
                if(version!=_generation||_closed)return;
                var aiCompleted=false;string? aiFailure=null;
                if(!result.Found && _aiLookup!=null)
                {
                    _status.Text=L("本地未收录，正在用 AI 补全…","Not in the local dictionary. Completing with AI…");
                    try
                    {
                        var ai=await _aiLookup(text,request.Token);
                        if(version!=_generation||_closed)return;
                        if(ai is {Found:true}){result=ai;aiCompleted=true;}
                    }
                    catch(OperationCanceledException)when(request.IsCancellationRequested){throw;}
                    catch(Exception ex){aiFailure=L("本地未收录，AI 补全失败：","Not in the local dictionary; AI completion failed: ")+ex.Message;}
                }
                _lookup=result;_phonetic.Text=result.Phonetic;_preparing=true;TranslationInput.Text=result.Translation;_preparing=false;
                _save.IsEnabled=result.Found;
                _status.Text=result.Found?(aiCompleted?L("AI 已补全，可加入生词本。","AI completed. Save it to your words."):""):aiFailure??L("离线词典未找到，可打开完整档案手动补充。","Not in the offline dictionary. Open word to add manually.");
            }
            else
            {
                var translation=await _translate(text,request.Token);
                if(version!=_generation||_closed)return;
                if(revision==_translationRevision){_preparing=true;TranslationInput.Text=translation;_preparing=false;_status.Text=_mode==QuickAction.SaveQuote?L("翻译完成，可编辑后保存。","Translation ready. Edit before saving."):L("翻译完成，可复制译文。","Translation ready. Copy it when needed.");}
                else _status.Text=L("翻译已完成；已保留你正在编辑的译文。","Translation ready; your edited text was kept.");
            }
        }
        catch(Exception ex){if(version==_generation&&!_closed)_status.Text=ex.Message;}
        finally{if(version==_generation&&!_closed){_run.IsEnabled=true;_cancel.IsVisible=false;}if(ReferenceEquals(_request,request))_request=null;request.Dispose();}
    }
    internal void CancelPending(){++_generation;_request?.Cancel();_request=null;_run.IsEnabled=true;_cancel.IsVisible=false;}
    internal void Save()
    {
        try
        {
            if(_mode==QuickAction.Lookup)
            {
                if(_lookup==null||!_lookup.Found)return;
                var exists=_words.GetAllWords().Any(w=>w.Word.Equals(_lookup.Word,StringComparison.OrdinalIgnoreCase));
                if(!exists)_words.AddWord(_lookup.Word,_lookup.Phonetic,_lookup.Translation,_lookup.Definition);
                _status.Text=exists?L("已在生词本中。","Already saved."):L("已加入生词本。","Word saved.");
            }
            else
            {
                var saved=_quotes.SaveQuote(_quoteId,OriginalInput.Text??"",TranslationInput.Text??"",_source.Text??"",_notes.Text??"");
                _quoteId=saved.Id;
                if(saved.AlreadyExists)
                {
                    CancelPending();_preparing=true;TranslationInput.Text=saved.Translation;_source.Text=saved.Source;_notes.Text=saved.Notes;_preparing=false;
                    _status.Text=L("这句已收藏，已打开原记录；编辑后可保存。","Already saved. Existing quote opened for editing.");
                }
                else {CancelPending();_status.Text=L("金句已保存。","Quote saved.");}
            }
            _changed();if(!string.IsNullOrEmpty(_words.BackupWarning))_status.Text+=" "+_words.BackupWarning;
        }
        catch(Exception ex){_status.Text=ex.Message;}
    }
    internal void ShowNear(PixelPoint anchor)
    {
        var screen=Screens.ScreenFromPoint(anchor)??Screens.Primary??Screens.All.FirstOrDefault();
        if(screen!=null)
        {
            var area=screen.WorkingArea;var scale=screen.Scaling;
            Width=Math.Min(420,area.Width/scale-24);Height=Math.Min(Height,area.Height/scale-24);
            MinWidth=Math.Min(320,Width);MinHeight=Math.Min(300,Height);
            var w=(int)Math.Ceiling(Width*scale);var h=(int)Math.Ceiling(Height*scale);
            Position=new PixelPoint(Math.Clamp(anchor.X+16,area.X,Math.Max(area.X,area.Right-w)),Math.Clamp(anchor.Y+16,area.Y,Math.Max(area.Y,area.Bottom-h)));
        }
        Show();Activate();OriginalInput.Focus();
    }
}
