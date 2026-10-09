using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Lexi.Controls;

/// <summary>Shared vector audio action; no font glyph dependency.</summary>
public sealed class InlineAudioButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    public InlineAudioButton() : this(UiText.Bilingual("朗读","Listen"),()=>{}) { }
    public InlineAudioButton(string accessibleName, Action action)
    {
        Classes.Add("audio-action");
        Width=32; Height=32; MinHeight=32; Padding=new Thickness(7);
        Background=Brushes.Transparent; BorderThickness=new Thickness(0);
        ToolTip.SetTip(this,accessibleName); AutomationProperties.SetName(this,accessibleName);
        var icon=new Avalonia.Controls.Shapes.Path { Data=Geometry.Parse("M2,7 L5,7 L9,3 L9,15 L5,11 L2,11 Z M12,6 C14,7 14,11 12,12 M14,3 C18,6 18,12 14,15"), StrokeThickness=1.5, Width=18, Height=18, Stretch=Stretch.Uniform };
        icon.Bind(Shape.StrokeProperty,this.GetResourceObservable("MutedBrush"));
        Content=icon;
        Click+=(_,_)=>action();
    }
}
