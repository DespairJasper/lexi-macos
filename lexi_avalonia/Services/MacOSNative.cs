using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Lexi;

internal static partial class MacOSNative
{
    private const string CarbonLib = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServicesLib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";
    private const string AppKitLib = "/System/Library/Frameworks/AppKit.framework/AppKit";

    static MacOSNative()
    {
        try
        {
            NativeLibrary.Load(AppKitLib);
        }
        catch { }
    }

    #region Carbon Global Hotkey

    public const uint CarbonKeyD = 2; // kVK_ANSI_D = 0x02
    public const uint CarbonKeyC = 8; // kVK_ANSI_C = 0x08
    public const uint CarbonModOption = 0x0800; // optionKey = 2048
    public const uint CarbonEventClassKeyboard = 0x6B657962; // 'keyb'
    public const uint CarbonEventHotKeyPressed = 5; // kEventHotKeyPressed = 5
    public const uint CarbonTypeEventHotKeyId = 0x686B6964; // typeEventHotKeyID = 'hkid'
    public const uint CarbonParamDirectObject = 0x2D2D2D2D; // kEventParamDirectObject = '----'
    public const uint CarbonFourCharCodeLXHK = 0x4C58484B; // 'LXHK'

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHotKeyID
    {
        public uint signature;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTypeSpec
    {
        public uint eventClass;
        public uint eventKind;
    }

    public delegate int EventHandlerDelegate(IntPtr inHandlerCallRef, IntPtr inEvent, IntPtr inUserData);

    [DllImport(CarbonLib)]
    public static extern IntPtr GetEventDispatcherTarget();

    [DllImport(CarbonLib)]
    public static extern IntPtr GetApplicationEventTarget();

    [DllImport(CarbonLib)]
    public static extern int InstallEventHandler(
        IntPtr inTarget,
        IntPtr inHandler,
        uint inNumTypes,
        [In] EventTypeSpec[] inList,
        IntPtr inUserData,
        out IntPtr outRef);

    [DllImport(CarbonLib)]
    public static extern int RemoveEventHandler(IntPtr inHandlerRef);

    [DllImport(CarbonLib)]
    public static extern int RegisterEventHotKey(
        uint inHotKeyCode,
        uint inHotKeyModifiers,
        EventHotKeyID inHotKeyID,
        IntPtr inTarget,
        uint inOptions,
        out IntPtr outRef);

    [DllImport(CarbonLib)]
    public static extern int UnregisterEventHotKey(IntPtr inHotKeyRef);

    [DllImport(CarbonLib)]
    public static extern int GetEventParameter(
        IntPtr inEvent,
        uint inName,
        uint inDesiredType,
        out uint outActualType,
        uint inBufferSize,
        out uint outActualSize,
        out EventHotKeyID outData);

    #endregion

    #region CoreGraphics & Accessibility

    public const uint kCGHIDEventTap = 0;
    public const uint kCGSessionEventTap = 1;
    public const ulong kCGEventFlagMaskCommand = 0x00100000;
    public const ulong kCGEventFlagMaskAlternate = 0x00080000;
    public const ulong kCGEventFlagMaskControl = 0x00040000;
    public const ulong kCGEventFlagMaskShift = 0x00020000;

    [DllImport(CoreGraphicsLib)]
    public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventSetFlags(IntPtr eventRef, ulong flags);

    [DllImport(CoreGraphicsLib)]
    public static extern void CGEventPost(uint tap, IntPtr eventRef);

    [DllImport(CoreGraphicsLib)]
    public static extern ulong CGEventSourceFlagsState(uint stateID);

    [DllImport(CoreGraphicsLib)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(ApplicationServicesLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AXIsProcessTrusted();

    #endregion

    #region Objective-C Runtime

    [DllImport(ObjCLib)]
    public static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLib)]
    public static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr objc_msgSend(IntPtr self, IntPtr op);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr objc_msgSend(IntPtr self, IntPtr op, IntPtr arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr objc_msgSend(IntPtr self, IntPtr op, IntPtr arg1, IntPtr arg2);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr objc_msgSend(IntPtr self, IntPtr op, IntPtr arg1, nuint arg2);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static extern nint objc_msgSend_nint(IntPtr self, IntPtr op);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static extern nuint objc_msgSend_nuint(IntPtr self, IntPtr op);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool objc_msgSend_bool(IntPtr self, IntPtr op);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool objc_msgSend_bool(IntPtr self, IntPtr op, IntPtr arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool objc_msgSend_bool(IntPtr self, IntPtr op, IntPtr arg1, IntPtr arg2);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static extern int objc_msgSend_int(IntPtr self, IntPtr op);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr objc_msgSend(IntPtr self, IntPtr op, byte[] bytes, nuint length);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr self, IntPtr op);

    #endregion

    #region Objective-C Bridge Helpers

    private static IntPtr ClsNSWorkspace => objc_getClass("NSWorkspace");
    private static IntPtr ClsNSPasteboard => objc_getClass("NSPasteboard");
    private static IntPtr ClsNSPasteboardItem => objc_getClass("NSPasteboardItem");
    private static IntPtr ClsNSString => objc_getClass("NSString");
    private static IntPtr ClsNSData => objc_getClass("NSData");
    private static IntPtr ClsNSArray => objc_getClass("NSArray");

    private static readonly IntPtr SelSharedWorkspace = sel_registerName("sharedWorkspace");
    private static readonly IntPtr SelFrontmostApp = sel_registerName("frontmostApplication");
    private static readonly IntPtr SelProcessIdentifier = sel_registerName("processIdentifier");
    private static readonly IntPtr SelGeneralPasteboard = sel_registerName("generalPasteboard");
    private static readonly IntPtr SelChangeCount = sel_registerName("changeCount");
    private static readonly IntPtr SelPasteboardItems = sel_registerName("pasteboardItems");
    private static readonly IntPtr SelClearContents = sel_registerName("clearContents");
    private static readonly IntPtr SelWriteObjects = sel_registerName("writeObjects:");
    private static readonly IntPtr SelCount = sel_registerName("count");
    private static readonly IntPtr SelObjectAtIndex = sel_registerName("objectAtIndex:");
    private static readonly IntPtr SelTypes = sel_registerName("types");
    private static readonly IntPtr SelDataForType = sel_registerName("dataForType:");
    private static readonly IntPtr SelSetDataForType = sel_registerName("setData:forType:");
    private static readonly IntPtr SelStringForType = sel_registerName("stringForType:");
    private static readonly IntPtr SelStringWithUtf8 = sel_registerName("stringWithUTF8String:");
    private static readonly IntPtr SelUtf8String = sel_registerName("UTF8String");
    private static readonly IntPtr SelDataWithBytes = sel_registerName("dataWithBytes:length:");
    private static readonly IntPtr SelBytes = sel_registerName("bytes");
    private static readonly IntPtr SelLength = sel_registerName("length");
    private static readonly IntPtr SelAlloc = sel_registerName("alloc");
    private static readonly IntPtr SelInit = sel_registerName("init");
    private static readonly IntPtr SelArrayWithObjectsCount = sel_registerName("arrayWithObjects:count:");

    // Pool lifetime is synchronous and must remain on the creating thread.
    public sealed class AutoreleasePool : IDisposable
    {
        private IntPtr _handle;
        public AutoreleasePool()
        {
            _handle = objc_msgSend(objc_getClass("NSAutoreleasePool"), sel_registerName("new"));
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("Cannot create Cocoa autorelease pool.");
        }
        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            objc_msgSend_void(_handle, sel_registerName("drain"));
            _handle = IntPtr.Zero;
        }
    }

    public static nint GetFrontmostApplicationPid()
    {
        try
        {
            var clsWorkspace = ClsNSWorkspace;
            if (clsWorkspace == IntPtr.Zero) return 0;
            var workspace = objc_msgSend(clsWorkspace, SelSharedWorkspace);
            if (workspace == IntPtr.Zero) return 0;
            var app = objc_msgSend(workspace, SelFrontmostApp);
            if (app == IntPtr.Zero) return 0;
            return objc_msgSend_int(app, SelProcessIdentifier);
        }
        catch
        {
            return 0;
        }
    }

    public static IntPtr GetGeneralPasteboard()
    {
        var clsPasteboard = ClsNSPasteboard;
        if (clsPasteboard == IntPtr.Zero) return IntPtr.Zero;
        return objc_msgSend(clsPasteboard, SelGeneralPasteboard);
    }

    public static uint GetPasteboardChangeCount(IntPtr pasteboard)
    {
        if (pasteboard == IntPtr.Zero) return 0;
        return (uint)objc_msgSend_nint(pasteboard, SelChangeCount);
    }

    public static IntPtr CreateNSStringHandle(string str)
    {
        var clsString = ClsNSString;
        if (clsString == IntPtr.Zero) return IntPtr.Zero;
        var bytes = Encoding.UTF8.GetBytes(str + "\0");
        return objc_msgSend(clsString, SelStringWithUtf8, bytes, (nuint)bytes.Length - 1);
    }

    public static string? ReadNSString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8Ptr = objc_msgSend(nsString, SelUtf8String);
        return Marshal.PtrToStringUTF8(utf8Ptr);
    }

    public static string? ReadPasteboardString(IntPtr pasteboard)
    {
        if (pasteboard == IntPtr.Zero) return null;
        var typeStr = CreateNSStringHandle("public.utf8-plain-text");
        if (typeStr == IntPtr.Zero) return null;
        var strHandle = objc_msgSend(pasteboard, SelStringForType, typeStr);
        return ReadNSString(strHandle);
    }

    public sealed class PasteboardItemSnapshot
    {
        public List<(string Type, byte[] Data)> Entries { get; } = new();
    }

    public static List<PasteboardItemSnapshot>? SnapshotPasteboard(IntPtr pasteboard, long maxBytes = 16 * 1024 * 1024)
    {
        if (pasteboard == IntPtr.Zero || maxBytes < 0) return null;
        using var pool = new AutoreleasePool();
        var sequence = GetPasteboardChangeCount(pasteboard);
        var itemsArray = objc_msgSend(pasteboard, SelPasteboardItems);
        if (itemsArray == IntPtr.Zero)
        {
            var types = objc_msgSend(pasteboard, SelTypes);
            return (types == IntPtr.Zero || objc_msgSend_nuint(types, SelCount) == 0)
                && GetPasteboardChangeCount(pasteboard) == sequence ? new List<PasteboardItemSnapshot>() : null;
        }
        var nativeCount = objc_msgSend_nuint(itemsArray, SelCount);
        if (nativeCount > 4096) return null;
        var result = new List<PasteboardItemSnapshot>((int)nativeCount);
        long total = 0;
        for (var i = 0; i < (int)nativeCount; i++)
        {
            var item = objc_msgSend(itemsArray, SelObjectAtIndex, (IntPtr)i);
            if (item == IntPtr.Zero) return null;
            var typesArray = objc_msgSend(item, SelTypes);
            if (typesArray == IntPtr.Zero) return null;
            var typeCount = objc_msgSend_nuint(typesArray, SelCount);
            if (typeCount == 0 || typeCount > 4096) return null;
            var itemSnapshot = new PasteboardItemSnapshot();
            for (var j = 0; j < (int)typeCount; j++)
            {
                var typeObj = objc_msgSend(typesArray, SelObjectAtIndex, (IntPtr)j);
                var typeName = ReadNSString(typeObj);
                if (string.IsNullOrEmpty(typeName)) return null;
                var dataObj = objc_msgSend(item, SelDataForType, typeObj);
                if (dataObj == IntPtr.Zero) return null;
                var nativeLength = objc_msgSend_nuint(dataObj, SelLength);
                if (nativeLength > (nuint)Math.Min(int.MaxValue, maxBytes - total)) return null;
                var length = (int)nativeLength;
                total += length;
                var bytes = new byte[length];
                if (length > 0)
                {
                    var bytesPtr = objc_msgSend(dataObj, SelBytes);
                    if (bytesPtr == IntPtr.Zero) return null;
                    Marshal.Copy(bytesPtr, bytes, 0, length);
                }
                itemSnapshot.Entries.Add((typeName, bytes));
            }
            result.Add(itemSnapshot);
        }
        return GetPasteboardChangeCount(pasteboard) == sequence ? result : null;
    }

    public static bool RestorePasteboard(IntPtr pasteboard, List<PasteboardItemSnapshot> snapshot,
        uint? expectedSequence = null, nint source = 0)
    {
        if (pasteboard == IntPtr.Zero || snapshot == null) return false;
        using var pool = new AutoreleasePool();
        var items = new IntPtr[snapshot.Count];
        var pinned = GCHandle.Alloc(items, GCHandleType.Pinned);
        try
        {
            // Construct and validate the complete replacement before changing the pasteboard.
            for (var i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i] == null || snapshot[i].Entries.Count == 0) return false;
                var pbItem = objc_msgSend(ClsNSPasteboardItem, SelAlloc);
                if (pbItem == IntPtr.Zero) return false;
                pbItem = objc_msgSend(pbItem, SelInit);
                items[i] = pbItem;
                if (pbItem == IntPtr.Zero) return false;
                foreach (var (type, data) in snapshot[i].Entries)
                {
                    if (string.IsNullOrEmpty(type) || type.Contains('\0') || data == null) return false;
                    var typeHandle = CreateNSStringHandle(type);
                    var dataHandle = objc_msgSend(ClsNSData, SelDataWithBytes, data, (nuint)data.Length);
                    if (typeHandle == IntPtr.Zero || dataHandle == IntPtr.Zero
                        || !objc_msgSend_bool(pbItem, SelSetDataForType, dataHandle, typeHandle)) return false;
                }
            }
            var array = snapshot.Count == 0 ? IntPtr.Zero
                : objc_msgSend(ClsNSArray, SelArrayWithObjectsCount, pinned.AddrOfPinnedObject(), (nuint)items.Length);
            if (snapshot.Count > 0 && array == IntPtr.Zero) return false;
            // NSPasteboard offers no cross-process CAS. Recheck immediately before clear.
            if (expectedSequence.HasValue && GetPasteboardChangeCount(pasteboard) != expectedSequence.Value) return false;
            if (source != 0 && GetFrontmostApplicationPid() != source) return false;
            var clearedSequence = (uint)objc_msgSend_nint(pasteboard, SelClearContents);
            if (snapshot.Count == 0) return true;
            if (GetPasteboardChangeCount(pasteboard) != clearedSequence) return false;
            return objc_msgSend_bool(pasteboard, SelWriteObjects, array);
        }
        catch { return false; }
        finally
        {
            foreach (var item in items)
                if (item != IntPtr.Zero) objc_msgSend_void(item, sel_registerName("release"));
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    #endregion
}
