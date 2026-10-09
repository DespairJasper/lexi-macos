using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lexi.Services;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace Lexi.Shell;

/// <summary>Frosts only the small foreground surface. The live page is never hidden,
/// blurred or replaced. Captures stay in memory and are released with the surface.</summary>
public sealed class GlassOverlayHost : IDisposable
{
    private readonly Control _background;
    private readonly Window _window;
    private readonly HashSet<Control> _modals=[];
    private readonly HashSet<object> _popups=[];
    private readonly Dictionary<Control,Bitmap?> _surfaces=[];
    private readonly HashSet<Control> _scheduled=[];
    private readonly Dictionary<FlyoutBase,EventHandler> _tracked=[];
    private readonly Dictionary<FlyoutBase,EventHandler> _openHandlers=[];
    private readonly IDisposable _popupSubscription;
    private IInputElement? _previousFocus;
    private bool _disposed;
    public bool IsOpen => _modals.Any(m=>m.IsVisible) || _popups.Count>0;
    public bool IsBlurred => _surfaces.Values.Any(b=>b!=null);
    public bool ForceSolid { get; set; }
    public double LastCaptureMilliseconds { get; private set; }
    public long RetainedBackdropBytes => _surfaces.Values.OfType<Bitmap>().Sum(b=>(long)b.PixelSize.Width*b.PixelSize.Height*4);

    public GlassOverlayHost(Window window, Control background)
    {
        _window=window; _background=background;
        _popupSubscription=Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup,e)=>
        {
            if(_disposed)return;
            // Closing must clean up even when navigation detached PlacementTarget.
            if(!popup.IsOpen)
            {
                if(_popups.Remove(popup))Refresh();
                if(popup.Child is Control closed)ReleaseSurface(closed);
                return;
            }
            if(popup.PlacementTarget is not { } target || TopLevel.GetTopLevel(target)!=_window)return;
            ApplyPopupPalette(popup);
            if(popup.Child is ToolTip)return;
            _popups.Add(popup);
            if(popup.Child is Control surface)ActivateSurface(surface);
            Refresh();
            Dispatcher.UIThread.Post(()=>
            {
                if(_disposed || !popup.IsOpen)return;
                ApplyPopupPalette(popup);
                if(popup.Child is Control current)ActivateSurface(current);
            },DispatcherPriority.Loaded);
        });
        background.SizeChanged+=OnBackgroundSizeChanged;
        window.AddHandler(InputElement.KeyDownEvent,TrapModalFocus,Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private string SurfaceResource => ForceSolid || !OverlayMaterialPolicy.TransparencyEnabled ? "DialogSurfaceBrush" : "GlassSurfaceBrush";
    private IBrush SurfaceTint => (IBrush)_window.FindResource(_window.ActualThemeVariant,SurfaceResource)!;
    private static void SetBackground(Control surface,IBrush brush)
    {
        if(surface is Border border)border.Background=brush;
        else if(surface is TemplatedControl presenter)presenter.Background=brush;
    }
    private void ApplyPopupPalette(Popup popup)
    {
        if(popup.Child is not TemplatedControl presenter)return;
        var ink=(IBrush)_window.FindResource(_window.ActualThemeVariant,"InkBrush")!;
        presenter.Resources["InkBrush"]=ink;
        presenter.Resources["GlassEdgeBrush"]=_window.FindResource(_window.ActualThemeVariant,"GlassEdgeBrush")!;
        presenter.Foreground=ink;
        if(!_surfaces.TryGetValue(presenter,out var material) || material==null)presenter.Background=SurfaceTint;
        foreach(var item in presenter.GetVisualDescendants().OfType<MenuItem>())item.Foreground=ink;
        foreach(var label in presenter.GetVisualDescendants().OfType<TextBlock>())
            if(!label.GetVisualAncestors().OfType<Button>().Any())label.Foreground=ink;
    }
    public void Register(Border overlay)
    {
        if(!_modals.Add(overlay))return;
        overlay.SetValue(Panel.ZIndexProperty,200);
        // The transparent input shield leaves the surrounding page sharp and unchanged.
        overlay.Background=Brushes.Transparent;
        if(overlay.Child is Border surface)Decorate(surface);
        overlay.PropertyChanged+=OnVisibilityChanged;
        Refresh();
    }
    public void Unregister(Border overlay)
    {
        overlay.PropertyChanged-=OnVisibilityChanged;
        _modals.Remove(overlay);
        if(overlay.Child is Control surface)ReleaseSurface(surface);
        Refresh();
    }
    public void Track(FlyoutBase flyout)
    {
        if(_openHandlers.ContainsKey(flyout))return;
        EventHandler opened=(_,_)=>TrackOnce(flyout);
        _openHandlers.Add(flyout,opened);flyout.Opened+=opened;
    }
    public void TrackOnce(FlyoutBase flyout)
    {
        if(_disposed)return;
        _popups.Add(flyout);
        if(!_tracked.ContainsKey(flyout))
        {
            EventHandler closed=(_,_)=>
            {
                _popups.Remove(flyout);
                if(_tracked.Remove(flyout,out var handler))flyout.Closed-=handler;
                Refresh();
            };
            _tracked.Add(flyout,closed);flyout.Closed+=closed;
        }
        Refresh();
    }
    private void OnVisibilityChanged(object? sender,AvaloniaPropertyChangedEventArgs e)
    {
        if(e.Property!=Visual.IsVisibleProperty)return;
        if(sender is Control {IsVisible:true} control)
        {
            _previousFocus=_window.FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(()=>
            {
                if(control.IsVisible && !_disposed)control.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.IsEnabled && !b.Classes.Contains("danger"))?.Focus();
            });
        }
        Refresh();
    }
    public void Refresh()
    {
        if(_disposed)return;
        var modalOpen=_modals.Any(m=>m.IsVisible);
        // This invariant also recovers a page left invisible by older overlay code.
        _background.Opacity=1;
        _background.IsHitTestVisible=!modalOpen;
        _background.IsEnabled=!modalOpen;
        foreach(var modal in _modals.OfType<Border>())
            if(modal.Child is Border surface)
            {
                if(modal.IsVisible)ActivateSurface(surface);
                else ReleaseSurface(surface);
            }
        foreach(var surface in _surfaces.Keys.ToArray())
        {
            if(ForceSolid || !OverlayMaterialPolicy.TransparencyEnabled)
            { SetBackground(surface,SurfaceTint); _surfaces[surface]?.Dispose();_surfaces[surface]=null; }
            else ScheduleSurface(surface);
        }
        if(!IsOpen){_previousFocus?.Focus();_previousFocus=null;}
    }
    private void ActivateSurface(Control surface)
    {
        if(_surfaces.TryAdd(surface,null))
        {
            SetBackground(surface,SurfaceTint);
            surface.SizeChanged+=OnSurfaceSizeChanged;
        }
        if(_surfaces[surface]==null)ScheduleSurface(surface);
    }
    private void ScheduleSurface(Control surface)
    {
        if(_disposed || !_scheduled.Add(surface))return;
        Dispatcher.UIThread.Post(()=>
        {
            _scheduled.Remove(surface);
            if(!_disposed && _surfaces.ContainsKey(surface))RebuildSurface(surface);
        },DispatcherPriority.Loaded);
    }
    private void OnSurfaceSizeChanged(object? sender,SizeChangedEventArgs e)
    {if(sender is Control surface)ScheduleSurface(surface);}
    private void OnBackgroundSizeChanged(object? sender,SizeChangedEventArgs e)
    {foreach(var surface in _surfaces.Keys.ToArray())ScheduleSurface(surface);}
    private void ReleaseSurface(Control surface)
    {
        if(!_surfaces.Remove(surface,out var bitmap))return;
        SetBackground(surface,SurfaceTint);bitmap?.Dispose();
        surface.SizeChanged-=OnSurfaceSizeChanged;
    }
    private void RebuildSurface(Control surface)
    {
        if(ForceSolid || !OverlayMaterialPolicy.TransparencyEnabled)
        {SetBackground(surface,SurfaceTint);return;}
        var width=(int)Math.Ceiling(surface.Bounds.Width);var height=(int)Math.Ceiling(surface.Bounds.Height);
        var pageWidth=(int)Math.Ceiling(_window.Bounds.Width);var pageHeight=(int)Math.Ceiling(_window.Bounds.Height);
        if(width<1 || height<1 || pageWidth<1 || pageHeight<1 || !surface.IsEffectivelyVisible)return;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        WriteableBitmap? replacement=null;
        try
        {
            var origin=surface.PointToScreen(default);var windowOrigin=_window.PointToScreen(default);
            var left=(origin.X-windowOrigin.X)/_window.RenderScaling;
            var top=(origin.Y-windowOrigin.Y)/_window.RenderScaling;
            using var rendered=new RenderTargetBitmap(new PixelSize(pageWidth,pageHeight),new Vector(96,96));
            // Hide foreground surfaces only during the synchronous offscreen capture.
            var foreground=_modals.Where(m=>m.IsVisible).Concat(_surfaces.Keys.Where(s=>TopLevel.GetTopLevel(s)==_window)).Distinct().Select(s=>(s,s.Opacity)).ToArray();
            foreach(var (control,_) in foreground)control.Opacity=0;
            try{rendered.Render(_window);}finally{foreach(var (control,opacity) in foreground)control.Opacity=opacity;}
            using var original=new SKBitmap(new SKImageInfo(pageWidth,pageHeight,SKColorType.Bgra8888,SKAlphaType.Premul));
            rendered.CopyPixels(new PixelRect(0,0,pageWidth,pageHeight),original.GetPixels(),original.ByteCount,original.RowBytes);
            // Only a popup-sized image is retained and displayed, never a full-page layer.
            using var output=new SKBitmap(new SKImageInfo(width,height,SKColorType.Bgra8888,SKAlphaType.Premul));
            using(var canvas=new SKCanvas(output))
            {
                canvas.Clear(SKColors.Transparent);
                using(var filter=SKImageFilter.CreateBlur(8,8))
                using(var paint=new SKPaint{ImageFilter=filter})canvas.DrawBitmap(original,(float)-left,(float)-top,paint);
                var tint=((ISolidColorBrush)SurfaceTint).Color;
                using var tintPaint=new SKPaint{Color=new SKColor(tint.R,tint.G,tint.B,tint.A)};
                canvas.DrawRect(0,0,width,height,tintPaint);
            }
            replacement=new WriteableBitmap(new PixelSize(width,height),new Vector(96,96),Avalonia.Platform.PixelFormat.Bgra8888,Avalonia.Platform.AlphaFormat.Premul);
            using(var frame=replacement.Lock())
            {
                var pixels=output.Bytes;
                for(var row=0;row<height;row++)Marshal.Copy(pixels,row*output.RowBytes,frame.Address+row*frame.RowBytes,width*4);
            }
            SetBackground(surface,new ImageBrush(replacement){Stretch=Stretch.Fill});
            _surfaces[surface]?.Dispose();_surfaces[surface]=replacement;replacement=null;
        }
        catch
        {
            // A failed material capture cannot hide or block navigation.
            SetBackground(surface,(IBrush)_window.FindResource(_window.ActualThemeVariant,"DialogSurfaceBrush")!);
            _surfaces[surface]?.Dispose();_surfaces[surface]=null;
        }
        finally{replacement?.Dispose();LastCaptureMilliseconds=watch.Elapsed.TotalMilliseconds;}
    }
    private void TrapModalFocus(object? sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Tab)return;
        var modal=_modals.LastOrDefault(m=>m.IsVisible);if(modal==null)return;
        var candidates=modal.GetVisualDescendants().OfType<Control>().Where(c=>c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.TabIndex>=0).ToList();
        if(candidates.Count==0){e.Handled=true;return;}
        var focused=_window.FocusManager?.GetFocusedElement() as Control;
        var index=focused==null?-1:candidates.IndexOf(focused);
        index=(index+(e.KeyModifiers.HasFlag(KeyModifiers.Shift)?-1:1)+candidates.Count)%candidates.Count;
        candidates[index].Focus();e.Handled=true;
    }
    public static void Decorate(Border surface)
    {
        surface.Classes.Remove("card");surface.Classes.Add("glass-surface");
        surface.CornerRadius=new CornerRadius(14);surface.BorderThickness=new Thickness(1);
        if(surface.Padding==default)surface.Padding=new Thickness(24);
        surface.Bind(Border.BackgroundProperty,surface.GetResourceObservable("GlassSurfaceBrush"));
        surface.Bind(Border.BorderBrushProperty,surface.GetResourceObservable("GlassEdgeBrush"));
        surface.BoxShadow=BoxShadows.Parse("0 8 30 0 #20203045");
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        foreach(var modal in _modals)modal.PropertyChanged-=OnVisibilityChanged;
        foreach(var surface in _surfaces.Keys.ToArray())ReleaseSurface(surface);
        foreach(var (flyout,handler) in _tracked)flyout.Closed-=handler;
        foreach(var (flyout,handler) in _openHandlers)flyout.Opened-=handler;
        _tracked.Clear();_openHandlers.Clear();_scheduled.Clear();_popupSubscription.Dispose();_modals.Clear();_popups.Clear();
        _background.Opacity=1;_previousFocus=null;
        _background.IsEnabled=true;_background.IsHitTestVisible=true;
        _background.SizeChanged-=OnBackgroundSizeChanged;
        _window.RemoveHandler(InputElement.KeyDownEvent,TrapModalFocus);
    }
}
