using Avalonia;
using Avalonia.Controls;
using System.Runtime.InteropServices;
namespace Lexi;
internal static class MacPointerLocation
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public double X,Y; }
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern nint CGEventCreate(nint source);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern Point CGEventGetLocation(nint ev);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint obj);
    internal static PixelPoint Get(Screens screens,PixelPoint fallback)
    {
        if(!OperatingSystem.IsMacOS())return fallback;var ev=CGEventCreate(0);if(ev==0)return fallback;
        try
        {
            var p=CGEventGetLocation(ev);
            foreach(var screen in screens.All)
            {
                var candidate=new PixelPoint((int)Math.Round(p.X*screen.Scaling),(int)Math.Round(p.Y*screen.Scaling));
                if(screen.Bounds.Contains(candidate))return candidate;
            }
            var scale=screens.Primary?.Scaling??1;return new PixelPoint((int)(p.X*scale),(int)(p.Y*scale));
        }
        finally{CFRelease(ev);}
    }
}
