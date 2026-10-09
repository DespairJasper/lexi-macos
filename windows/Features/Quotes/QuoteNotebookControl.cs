using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lexi.Controls;

namespace Lexi.Features.Quotes;

// Presentation only. Persistence and confirmation belong to the application shell.
public sealed class QuoteNotebookControl
{
    public static Border CreateReadingRow(QuoteItem quote, Action speak, Action copy, Action edit, Action<Control> delete)
    {
        var text=new StackPanel {Spacing=10};
        SelectableTextBlock Passage(string value,double size,string brush)
        {
            var block=new SelectableTextBlock {Text=value,FontSize=size,LineHeight=size*1.55,TextWrapping=TextWrapping.Wrap};
            block.Bind(TextBlock.ForegroundProperty,block.GetResourceObservable(brush));return block;
        }
        text.Children.Add(Passage(quote.Original,17,"InkBrush"));
        if(!string.IsNullOrWhiteSpace(quote.Translation))text.Children.Add(Passage(quote.Translation,15,"MutedBrush"));
        if(!string.IsNullOrWhiteSpace(quote.Source))text.Children.Add(Passage(quote.Source,12,"MutedBrush"));
        if(!string.IsNullOrWhiteSpace(quote.Notes))text.Children.Add(Passage(quote.Notes,13,"MutedBrush"));
        var actions=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
        actions.Children.Add(new InlineAudioButton(UiText.Bilingual("朗读原句","Listen to original"),speak));
        Button Action(string label,Action callback){var b=new Button {Content=label,Classes={"ghost"},FontSize=13,Padding=new Thickness(8,4)};b.Click+=(_,_)=>callback();return b;}
        actions.Children.Add(Action(UiText.Bilingual("复制","Copy"),copy));
        actions.Children.Add(Action(UiText.Bilingual("编辑","Edit"),edit));
        var more=new Button {Content="…",Classes={"ghost"},Padding=new Thickness(8,4)};
        var menu=new MenuFlyout();var remove=new MenuItem {Header=UiText.Bilingual("删除…","Delete…")};remove.Click+=(_,_)=>delete(more);menu.Items.Add(remove);more.Flyout=menu;actions.Children.Add(more);
        text.Children.Add(actions);
        var row=new Border {Child=text,Padding=new Thickness(0,20),BorderThickness=new Thickness(0,0,0,1),MaxWidth=800,HorizontalAlignment=HorizontalAlignment.Stretch};
        row.Bind(Border.BorderBrushProperty,Avalonia.Application.Current!.GetResourceObservable("LineBrush"));return row;
    }
}
