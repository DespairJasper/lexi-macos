using System.Runtime.InteropServices;
using System.Text;

namespace Lexi;

internal static partial class MacOSNative
{
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(ApplicationServicesLib)] private static extern nint AXUIElementCreateApplication(int pid);
    [DllImport(ApplicationServicesLib)] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    [DllImport(ApplicationServicesLib)] private static extern int AXUIElementSetMessagingTimeout(nint element, float timeout);
    [DllImport(ApplicationServicesLib)] private static extern int AXUIElementGetPid(nint element, out int pid);
    [DllImport(CoreFoundationLib)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundationLib)] private static extern nint CFStringGetLength(nint text);
    [DllImport(CoreFoundationLib)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundationLib)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundationLib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint text, byte[] buffer, nint bufferSize, uint encoding);

    public static string? ReadFocusedSelectedText(nint source)
    {
        if (source <= 0 || source > int.MaxValue || !AXIsProcessTrusted() || GetFrontmostApplicationPid() != source) return null;
        nint app = 0, focusedAttribute = 0, selectedAttribute = 0, focused = 0, selected = 0;
        try
        {
            app = AXUIElementCreateApplication((int)source);
            if (app != 0) AXUIElementSetMessagingTimeout(app, 0.5f);
            focusedAttribute = CFStringCreateWithCString(0, "AXFocusedUIElement", 0x08000100);
            selectedAttribute = CFStringCreateWithCString(0, "AXSelectedText", 0x08000100);
            if (app == 0 || focusedAttribute == 0 || selectedAttribute == 0 || AXUIElementCopyAttributeValue(app, focusedAttribute, out focused) != 0 || focused == 0) return null;
            AXUIElementSetMessagingTimeout(focused, 0.5f);
            if (AXUIElementGetPid(focused, out var pid) != 0 || pid != (int)source || GetFrontmostApplicationPid() != source) return null;
            if (AXUIElementCopyAttributeValue(focused, selectedAttribute, out selected) != 0 || selected == 0 || CFGetTypeID(selected) != CFStringGetTypeID()) return null;
            var length = CFStringGetLength(selected);
            if (length <= 0 || length > 4096) return null;
            var buffer = new byte[(int)length * 4 + 1];
            if (!CFStringGetCString(selected, buffer, buffer.Length, 0x08000100) || GetFrontmostApplicationPid() != source) return null;
            var end = Array.IndexOf(buffer, (byte)0);
            return end < 0 ? null : Encoding.UTF8.GetString(buffer, 0, end);
        }
        catch { return null; }
        finally
        {
            foreach (var reference in new[] { selected, focused, selectedAttribute, focusedAttribute, app })
                if (reference != 0) CFRelease(reference);
        }
    }
}
