using Avalonia;
using Avalonia.Controls;
using Lexi;
using System.Runtime.InteropServices;
if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP native glass requires macOS"); return; }
AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
var window = new Window { Width = 240, Height = 160 };
var settings = new AppSettings { Material = "LiquidGlass", GlassIntensity = .65 };
Console.WriteLine("handle=" + window.TryGetPlatformHandle()?.HandleDescriptor);
if (MacGlassMaterial.Apply(window, settings) != "LiquidGlass") throw new Exception("native attachment did not activate");
var handle = window.TryGetPlatformHandle()!.Handle;
var parent = Native.IsKind(handle, Native.Sel("isKindOfClass:"), Native.GetClass("NSWindow"))
    ? Native.Send(handle, Native.Sel("contentView")) : Native.Send(handle, Native.Sel("superview"));
var children = Native.Send(parent, Native.Sel("subviews"));
var count = Native.Count(children, Native.Sel("count"));
nint glass = 0;
for (nuint i = 0; i < count; i++)
{
    var child = Native.Get(children, Native.Sel("objectAtIndex:"), i);
    if (Native.IsKind(child, Native.Sel("isKindOfClass:"), Native.GetClass("NSGlassEffectView"))) glass = child;
}
if (glass == 0) throw new Exception("no genuine NSGlassEffectView in renderer's siblings");
foreach(var strength in new[] { 0d, .37, 1d })
{
    settings.GlassIntensity = strength;
    MacGlassMaterial.Apply(window, settings);
    var actual = Native.Double(glass, Native.Sel("alphaValue"));
    if (Math.Abs(actual-strength)>1e-9) throw new Exception("native alpha endpoint mismatch");
    Console.WriteLine("PASS native alpha=" + actual);
}
if(Native.Hit(glass, Native.Sel("hitTest:"), new Native.Point { X=10, Y=10 }) != 0) throw new Exception("background intercepts clicks");
var before = Native.RectValue(glass, Native.Sel("frame"));
var target = new Native.Size { Width=before.Width+123, Height=before.Height+67 };
Native.SetSize(parent, Native.Sel("setFrameSize:"), target);
var resized = Native.RectValue(glass, Native.Sel("frame"));
if (Math.Abs(resized.Width-target.Width)>.01 || Math.Abs(resized.Height-target.Height)>.01)
    throw new Exception("glass background does not autoresize with content");
Console.WriteLine("PASS native glass resize follows parent");
var views = Native.Send(parent, Native.Sel("subviews"));
for (nuint i=0; i<Native.Count(views,Native.Sel("count")); i++)
{
    var child=Native.Get(views,Native.Sel("objectAtIndex:"),i);
    if(Native.IsKind(child, Native.Sel("isKindOfClass:"), Native.GetClass("NSVisualEffectView")) &&
        Native.Long(child, Native.Sel("blendingMode")) == 0 && !Native.Bool(child,Native.Sel("isHidden")))
        throw new Exception("previous behind-window blur is still visible");
}
Console.WriteLine("PASS old behind-window blur is hidden");
Native.Send(glass, Native.Sel("retain"));
settings.OpaqueMaterial=true;
if(MacGlassMaterial.Apply(window, settings)!="Opaque") throw new Exception("opaque policy not applied");
if(Native.Send(glass, Native.Sel("superview"))!=0) throw new Exception("old glass layer not detached");
Native.Send(glass, Native.Sel("release"));
window.Close();
Console.WriteLine("PASS genuine class, continuous native endpoints, click-through, opaque detachment; no window shown");
static class Native
{
 const string Lib="/usr/lib/libobjc.A.dylib";
 [StructLayout(LayoutKind.Sequential)] public struct Size { public double Width,Height; }
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public double X,Y,Width,Height; }
 [StructLayout(LayoutKind.Sequential)] public struct Point { public double X,Y; }
 [DllImport(Lib,EntryPoint="objc_msgSend")] public static extern void SetSize(nint self,nint sel,Size size);
 [DllImport(Lib,EntryPoint="objc_msgSend")] private static extern Rect GetRect(nint self,nint sel);
 [DllImport(Lib,EntryPoint="objc_msgSend_stret")] private static extern void GetRectStret(out Rect rect,nint self,nint sel);
 public static Rect RectValue(nint self,nint sel)
 { if(RuntimeInformation.ProcessArchitecture!=Architecture.X64) return GetRect(self,sel); GetRectStret(out var rect,self,sel); return rect; }
 [DllImport(Lib,EntryPoint="objc_msgSend")] public static extern nint Long(nint self,nint sel);
 [DllImport(Lib,EntryPoint="objc_msgSend")][return:MarshalAs(UnmanagedType.I1)] public static extern bool Bool(nint self,nint sel);
 [DllImport(Lib,EntryPoint="objc_getClass")] public static extern nint GetClass(string name);
 [DllImport(Lib,EntryPoint="sel_registerName")] public static extern nint Sel(string name);
 [DllImport(Lib,EntryPoint="objc_msgSend")] public static extern nint Send(nint self,nint sel);
 [DllImport(Lib,EntryPoint="objc_msgSend")] public static extern nuint Count(nint self,nint sel);
 [DllImport(Lib,EntryPoint="objc_msgSend")] public static extern nint Get(nint self,nint sel,nuint index);
 [DllImport(Lib,EntryPoint="objc_msgSend")][return:MarshalAs(UnmanagedType.I1)] public static extern bool IsKind(nint self,nint sel,nint cls);
 [DllImport(Lib,EntryPoint="objc_msgSend")] public static extern double Double(nint self,nint sel);
 [DllImport(Lib,EntryPoint="objc_msgSend")] public static extern nint Hit(nint self,nint sel,Point point);
}
