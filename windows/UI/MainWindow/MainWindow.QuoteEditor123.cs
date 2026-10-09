using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lexi.Controls;
using Lexi.Shell;

namespace Lexi;

public partial class MainWindow
{
    private Border? _quoteEditorHost;
    private Border? _visualConfirmation;
    private bool _quoteEditorDirty;
    private Action? _saveQuoteEditor;

    private bool TryLeaveQuoteEditor()
    {
        if(_quoteEditorHost==null)return true;
        if(_quoteEditorDirty){SetStatus(UiText.Bilingual("请先保存金句，或点击取消放弃修改。","Save the quote or cancel your changes before leaving."));return false;}
        CloseQuoteEditor();return true;
    }
    private void CloseQuoteEditor()
    {
        if(_quoteEditorHost?.Parent is Panel parent)parent.Children.Remove(_quoteEditorHost);
        _quoteEditorHost=null;_quoteEditorDirty=false;_saveQuoteEditor=null;
    }
    private void OpenQuoteEditorPage(QuoteItem? quote)
    {
        if(!TryLeaveQuoteEditor() || _restoring || !_databaseAvailable)return;
        ShowPage("quotes");
        var form=new StackPanel {Spacing=14,MaxWidth=680,Margin=new Thickness(24),HorizontalAlignment=HorizontalAlignment.Stretch};
        form.Children.Add(new TextBlock {Text=UiText.Bilingual(quote==null?"添加金句":"编辑金句",quote==null?"Add quote":"Edit quote"),FontSize=26,FontWeight=FontWeight.SemiBold});
        TextBox Field(string label,string name,string value,int limit,int height)
        {
            form.Children.Add(new TextBlock {Text=label,FontSize=14});
            var box=new TextBox {Name=name,Text=value,MaxLength=limit,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=height,FontSize=16};
            box.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty)_quoteEditorDirty=true;};form.Children.Add(box);return box;
        }
        var original=Field(UiText.Bilingual("原句","Original"),"QuoteOriginalInput",quote?.Original??"",4096,84);
        var translated=Field(UiText.Bilingual("译文","Translation"),"QuoteTranslationInput",quote?.Translation??"",8192,84);
        var source=Field(UiText.Bilingual("来源","Source"),"QuoteSourceInput",quote?.Source??"",1000,36);
        var notes=Field(UiText.Bilingual("备注","Notes"),"QuoteNotesInput",quote?.Notes??"",4096,60);
        var error=new TextBlock {TextWrapping=TextWrapping.Wrap};form.Children.Add(error);
        var actions=new WrapPanel {Orientation=Orientation.Horizontal};
        actions.Children.Add(new InlineAudioButton(UiText.Bilingual("朗读原句","Listen to original"),()=>GetLearningAudio().Play(original.Text??"")));
        var save=new Button {Name="QuoteSaveButton",Content=UiText.Bilingual("保存","Save"),Classes={"primary"},Margin=new Thickness(8,0)};
        _saveQuoteEditor=()=>
        {
            if(_restoring || !_databaseAvailable){error.Text=UiText.Bilingual("词库暂不可用，内容已保留。","Archive unavailable; input preserved.");return;}
            if(string.IsNullOrWhiteSpace(original.Text)){error.Text=UiText.Bilingual("原句不能为空。","Original sentence is required.");return;}
            try {var result=QuoteArchive.SaveQuote(quote?.Id,original.Text.Trim(),translated.Text??"",source.Text??"",notes.Text??"");CloseQuoteEditor();RenderQuotes();SetStatus(result.AlreadyExists?UiText.Bilingual("原句已存在，保留已有金句。","Already saved; existing quote preserved."):UiText.Bilingual("金句已保存。","Quote saved."));}
            catch(Exception ex){error.Text=UiText.Bilingual("保存失败，输入已保留：","Save failed; input preserved: ")+ex.Message;}
        };
        save.Click+=(_,_)=>_saveQuoteEditor?.Invoke();actions.Children.Add(save);
        var cancel=new Button {Name="QuoteCancelButton",Content=UiText.Bilingual("取消","Cancel"),Classes={"ghost"}};cancel.Click+=(_,_)=>CloseQuoteEditor();actions.Children.Add(cancel);form.Children.Add(actions);
        _quoteEditorHost=new Border {Name="QuoteEditorPage",Child=new ScrollViewer {Content=form,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled}};
        _quoteEditorHost.Bind(Border.BackgroundProperty,this.GetResourceObservable("PaperBrush"));
        var main=(Grid)((Grid)RootWindowBorder.Child!).Children[0];Grid.SetRow(_quoteEditorHost,1);_quoteEditorHost.SetValue(Panel.ZIndexProperty,170);main.Children.Add(_quoteEditorHost);
        _quoteEditorDirty=false;original.Focus();
    }
    private void ConfirmQuoteDelete(QuoteItem quote,Control anchor)
    {
        var body=new StackPanel {Spacing=16};
        body.Children.Add(new TextBlock {Text=UiText.Bilingual("删除这条金句？","Delete this quote?"),FontSize=20,FontWeight=FontWeight.SemiBold});
        body.Children.Add(new SelectableTextBlock {Text=quote.Original,FontSize=16,TextWrapping=TextWrapping.Wrap,MaxLines=4});
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button {Content=UiText.Bilingual("取消","Cancel"),Classes={"ghost"}};cancel.Click+=(_,_)=>CloseVisualConfirmation();
        var remove=new Button {Content=UiText.Bilingual("删除","Delete"),Classes={"danger"}};
        remove.Click+=(_,_)=>{if(_restoring || !_databaseAvailable)return;try {QuoteArchive.DeleteQuote(quote.Id);CloseVisualConfirmation();RenderQuotes();}catch(Exception ex){SetStatus(ex.Message);}};
        buttons.Children.Add(cancel);buttons.Children.Add(remove);body.Children.Add(buttons);
        var surface=new Border {Child=body,MaxWidth=480,Margin=new Thickness(24),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        _visualConfirmation=new Border {Child=surface};((Panel)RootWindowBorder.Child!).Children.Add(_visualConfirmation);_glassHost!.Register(_visualConfirmation);_glassHost.Refresh();cancel.Focus();
    }
    private void CloseVisualConfirmation()
    {
        if(_visualConfirmation==null)return;
        _visualConfirmation.IsVisible=false;
        if(_visualConfirmation.Parent is Panel parent)parent.Children.Remove(_visualConfirmation);
        _glassHost?.Unregister(_visualConfirmation);_visualConfirmation=null;
    }
}
