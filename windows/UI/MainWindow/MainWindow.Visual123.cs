using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lexi.Controls;
using Lexi.Shell;

namespace Lexi;

public partial class MainWindow
{
    private GlassOverlayHost? _glassHost;
    private void ConfigureVisual123()
    {
        var outer=(Grid)RootWindowBorder.Child!;
        _glassHost=new GlassOverlayHost(this,(Control)outer.Children[0]);
        _glassHost.ForceSolid=_settings.OpaqueMaterial||_settings.HighContrast;
        _typingHost!.Bind(Border.BackgroundProperty,this.GetResourceObservable("PaperBrush"));
        foreach(var overlay in new[]{DialogEditOverlay,DialogDeleteOverlay,DialogStageOverlay,RestoreOverlay})_glassHost.Register(overlay);
        if(_workspaceChooser!=null)_glassHost.Register(_workspaceChooser);
        foreach(var root in _settingsRoots.OfType<Border>())
        {
            root.Classes.Remove("card");root.Background=Brushes.Transparent;root.BorderThickness=new Thickness(0);root.BoxShadow=default;
            root.Padding=new Thickness(0,8);
        }
        foreach(var button in new[]{this.FindControl<Button>("LookupSpeakButton")})
        {
            if(button==null)continue;
            button.Content=new InlineAudioButton(UiText.Bilingual("朗读单词","Listen to word"),()=>{}).Content;
            button.Classes.Add("audio-action");button.Width=32;button.Height=32;button.Padding=new Thickness(7);button.BorderThickness=new Thickness(0);button.Background=Brushes.Transparent;
        }
        Closed+=(_,_)=>_glassHost?.Dispose();
    }
}
