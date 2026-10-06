using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lexi;

/// <summary>Owns one non-interactive AppKit glass background per window.</summary>
public static class MacGlassMaterial
{
    private static readonly ConditionalWeakTable<Window, Attachment> Attachments = new();
    public static bool IsSupported => OperatingSystem.IsMacOS() && Native.GlassClass != 0;
    public static double NormalizeIntensity(double intensity) => double.IsFinite(intensity) ? Math.Clamp(intensity, 0, 1) : .65;
    public static bool WantsLiquidGlass(AppSettings settings) => settings.Material == "LiquidGlass" && !settings.HighContrast && !settings.OpaqueMaterial;

    public static Avalonia.Media.Color SurfaceColor(AppSettings settings, bool card = false)
    {
        var dark = settings.Theme == "Dark";
        var opacity = card ? .96 - NormalizeIntensity(settings.GlassIntensity) * .34
            : .92 - NormalizeIntensity(settings.GlassIntensity) * .70;
        if (settings.HighContrast || settings.OpaqueMaterial) opacity = 1;
        var color = Avalonia.Media.Color.Parse(dark ? card ? "#202C3D" : "#182232" : card ? "#FFFFFF" : "#F5F9FF");
        return Avalonia.Media.Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);
    }

    /// <summary>Call on the UI thread, also before Show; repeated calls update the same attachment.</summary>
    public static string Apply(Window window, AppSettings settings)
    {
        Dispatcher.UIThread.VerifyAccess();
        var attachment = Attachments.GetValue(window, w => new Attachment(w));
        attachment.Update(settings);
        return attachment.Status;
    }

    public static string Status(Window window) => Attachments.TryGetValue(window, out var value) ? value.Status : "Pending";

    private sealed class Attachment
    {
        private readonly Window _window;
        private AppSettings? _settings;
        private nint _glass;
        private bool _closed;
        public string Status { get; private set; } = "Pending";
        internal Attachment(Window window)
        {
            _window = window;
            window.Opened += OnOpened;
            window.Closed += OnClosed;
        }
        private void OnOpened(object? sender, EventArgs args) { if (_settings != null) Update(_settings); }
        private void OnClosed(object? sender, EventArgs args)
        {
            _closed = true;
            RemoveGlass();
            _window.Opened -= OnOpened;
            _window.Closed -= OnClosed;
            Attachments.Remove(_window);
        }
        internal void Update(AppSettings settings)
        {
            if (_closed) return;
            _settings = settings;
            var opaque = settings.HighContrast || settings.OpaqueMaterial;
            if (!WantsLiquidGlass(settings) || !IsSupported)
            {
                RemoveGlass();
                SetTransparency(opaque ? WindowTransparencyLevel.None : WindowTransparencyLevel.AcrylicBlur);
                Status = opaque ? "Opaque" : settings.Material == "LiquidGlass" ? "FrostedFallback" : "Frosted";
                return;
            }
            if (!OperatingSystem.IsMacOS()) return;
            try
            {
                // Avalonia's Transparent mode hides its old NSVisualEffectView and clears NSWindow.
                SetTransparency(WindowTransparencyLevel.Transparent);
                var handle = _window.TryGetPlatformHandle();
                if (handle == null || handle.Handle == 0) { Status = "Pending"; return; }
                if (handle.HandleDescriptor is not ("NSWindow" or "NSView"))
                { FallBack("UnsupportedHandle"); return; }
                var native = handle.Handle;
                nint view;
                if (Native.IsKind(native, "NSWindow")) view = Native.Send(native, Native.Sel("contentView"));
                else if (Native.IsKind(native, "NSView")) view = native;
                else { FallBack("UnsupportedHandle"); return; }
                // Avalonia returns AvnView. Its parent is AutoFitContentView; a glass sibling
                // goes below the renderer and cannot lower the opacity of the text or controls.
                var parent = Native.Send(view, Native.Sel("superview"));
                if (Native.IsKind(native, "NSWindow")) parent = view;
                if (parent == 0) { Status = "Pending"; return; }
                if (_glass == 0)
                {
                    _glass = Native.Send(Native.PassThroughGlassClass, Native.Sel("new"));
                    if (_glass == 0) { FallBack("NativeAllocationFailed"); return; }
                    Native.VoidRect(_glass, Native.Sel("setFrame:"), Native.Bounds(parent));
                    Native.VoidInt(_glass, Native.Sel("setAutoresizingMask:"), 2 | 16);
                    Native.VoidDouble(_glass, Native.Sel("setCornerRadius:"), 0); // NSWindow owns its frame corners.
                    Native.VoidInt(_glass, Native.Sel("setStyle:"), 0); // Documented Regular enum.
                    Native.Void3(parent, Native.Sel("addSubview:positioned:relativeTo:"), _glass, -1,
                        Native.IsKind(native, "NSWindow") ? 0 : view);
                }
                var strength = NormalizeIntensity(settings.GlassIntensity);
                // Apple has no intensity property. Only the background's alpha and tint vary.
                Native.VoidDouble(_glass, Native.Sel("setAlphaValue:"), strength);
                var dark = settings.Theme == "Dark";
                var tint = Native.Color(Native.GetClass("NSColor"), Native.Sel("colorWithSRGBRed:green:blue:alpha:"),
                    dark ? .10 : .91, dark ? .17 : .96, dark ? .25 : 1, .10 + strength * .20);
                Native.VoidPtr(_glass, Native.Sel("setTintColor:"), tint);
                Status = "LiquidGlass";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Native glass fallback: {0}", ex.Message);
                FallBack("NativeError");
            }
        }
        private void SetTransparency(WindowTransparencyLevel desired)
        {
            // Native 11.3.12 falls through to None when the active level is re-submitted.
            if (_window.ActualTransparencyLevel != desired) _window.TransparencyLevelHint = [desired];
        }
        private void FallBack(string reason)
        {
            RemoveGlass();
            SetTransparency(WindowTransparencyLevel.AcrylicBlur);
            Status = "FrostedFallback:" + reason;
        }
        private void RemoveGlass()
        {
            if (_glass == 0) return;
            Native.Void(_glass, Native.Sel("removeFromSuperview"));
            Native.Void(_glass, Native.Sel("release"));
            _glass = 0;
        }
    }

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        internal static readonly nint GlassClass;
        private static nint _passThroughClass;
        // Root the reverse P/Invoke delegate for the entire lifetime of the ObjC class.
        private static readonly HitTestCallback HitTest = (_, _, _) => 0;
        static Native()
        {
            if (!OperatingSystem.IsMacOS()) return;
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            GlassClass = GetClass("NSGlassEffectView");
        }
        internal static nint PassThroughGlassClass
        {
            get
            {
                if (_passThroughClass != 0) return _passThroughClass;
                _passThroughClass = GetClass("LexiPassThroughGlassEffectView");
                if (_passThroughClass != 0) return _passThroughClass;
                var cls = AllocateClass(GlassClass, "LexiPassThroughGlassEffectView", 0);
                if (cls == 0) throw new InvalidOperationException("Unable to create background glass view class.");
                if (!AddMethod(cls, Sel("hitTest:"), Marshal.GetFunctionPointerForDelegate(HitTest), "@@:{CGPoint=dd}"))
                {
                    DisposeClass(cls);
                    throw new InvalidOperationException("Unable to disable background glass hit testing.");
                }
                RegisterClass(cls);
                return _passThroughClass = cls;
            }
        }
        internal static bool IsKind(nint obj, string name) => obj != 0 && BoolPtr(obj, Sel("isKindOfClass:"), GetClass(name));
        internal static Rect Bounds(nint view)
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return GetRect(view, Sel("bounds"));
            GetRectStret(out var rect, view, Sel("bounds")); return rect;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public double X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public double X, Y, Width, Height; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint HitTestCallback(nint self, nint selector, Point point);
        [DllImport(ObjC, EntryPoint = "objc_getClass")] internal static extern nint GetClass(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] internal static extern nint Sel(string name);
        [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")] private static extern nint AllocateClass(nint parent, string name, nuint bytes);
        [DllImport(ObjC, EntryPoint = "objc_registerClassPair")] private static extern void RegisterClass(nint cls);
        [DllImport(ObjC, EntryPoint = "objc_disposeClassPair")] private static extern void DisposeClass(nint cls);
        [DllImport(ObjC, EntryPoint = "class_addMethod")][return: MarshalAs(UnmanagedType.I1)] private static extern bool AddMethod(nint cls, nint selector, nint method, string signature);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send(nint self, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")][return: MarshalAs(UnmanagedType.I1)] private static extern bool BoolPtr(nint self, nint selector, nint argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void Void(nint self, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void VoidPtr(nint self, nint selector, nint argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void VoidInt(nint self, nint selector, nint argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void VoidDouble(nint self, nint selector, double argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void Void3(nint self, nint selector, nint view, nint order, nint relative);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void VoidRect(nint self, nint selector, Rect frame);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Rect GetRect(nint self, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void GetRectStret(out Rect frame, nint self, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Color(nint self, nint selector, double red, double green, double blue, double alpha);
    }
}
