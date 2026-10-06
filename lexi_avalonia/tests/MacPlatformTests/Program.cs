using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Lexi;

if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("macOS native tests require macOS."); return 2; }
NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
var failures = 0;
void Run(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

// This mode exercises our process's Cocoa queue -> Carbon handler route. It does not
// synthesize keyboard input or verify WindowServer matching of physical global keys.
// F/G/H avoid the D/A/S registrations held by the user's running Lexi instance.
if (args.Contains("--cocoa-queue"))
{
    Run("Cocoa event queue dispatches registered Carbon actions (not physical keys)", () =>
    {
        using var pool = new MacOSNative.AutoreleasePool();
        var app = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSApplication"), MacOSNative.sel_registerName("sharedApplication"));
        Check(app != 0, "NSApplication initialization failed");
        using var service = new HotkeyService(); service.Configure("F", "G", "H");
        var received = new List<QuickAction>(); service.QuickActionPressed += received.Add; service.Start();
        foreach (var action in Enum.GetValues<QuickAction>())
        {
            Check(service.GetStatus(action).IsRegistered, "fixture registration failed: " + service.GetStatus(action).Error);
            Check(Native.CreateEvent(0, MacOSNative.CarbonEventClassKeyboard, MacOSNative.CarbonEventHotKeyPressed, 0, 0, out var ev) == 0, "CreateEvent failed");
            try
            {
                var id = new MacOSNative.EventHotKeyID { signature = MacOSNative.CarbonFourCharCodeLXHK, id = (uint)(9001 + (int)action) };
                Check(Native.SetEventParameter(ev, MacOSNative.CarbonParamDirectObject, MacOSNative.CarbonTypeEventHotKeyId, 8, ref id) == 0, "SetEventParameter failed");
                Check(Native.PostEventToQueue(Native.GetMainEventQueue(), ev, 1) == 0, "PostEventToQueue failed");
            }
            finally { Native.ReleaseEvent(ev); }
        }
        Check(received.Count == 0, "queue fixture dispatched before the Cocoa event pump");
        var mode = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"), "NSDefaultRunLoopMode"));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (received.Count < 3 && timer.ElapsedMilliseconds < 1500)
        {
            var date = Native.SendDate(MacOSNative.objc_getClass("NSDate"), MacOSNative.sel_registerName("dateWithTimeIntervalSinceNow:"), .05);
            var ev = Native.NextEvent(app, MacOSNative.sel_registerName("nextEventMatchingMask:untilDate:inMode:dequeue:"), ulong.MaxValue, date, mode, true);
            if (ev != 0) Native.SendVoid1(app, MacOSNative.sel_registerName("sendEvent:"), ev);
        }
        Check(received.SequenceEqual(Enum.GetValues<QuickAction>()), "Cocoa queue did not deliver registered actions: " + string.Join(",", received));
    });
    Console.WriteLine($"RESULT failures={failures}; Cocoa queue only; no physical keyboard or external UI tested.");
    return failures == 0 ? 0 : 1;
}

// Live registration check: registers the production MacOSHotkeyListener on Option+D / Option+A /
// Option+S, then runs a real Cocoa event loop so an externally posted key event exercises the
// full WindowServer -> Carbon -> managed callback route. Run it in the background, post the key
// from another process, then read stdout.
if (args.Contains("--hotkey-live"))
{
    using var pool = new MacOSNative.AutoreleasePool();
    var app = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSApplication"), MacOSNative.sel_registerName("sharedApplication"));
    Console.WriteLine($"LIVE nsapp={app} trusted={MacOSNative.AXIsProcessTrusted()}");
    using var service = new HotkeyService();
    service.Configure("D", "A", "S");
    service.QuickActionPressed += action => Console.WriteLine($"LIVE received {action}");
    service.Start();
    foreach (var action in Enum.GetValues<QuickAction>())
    {
        var status = service.GetStatus(action);
        Console.WriteLine($"LIVE status {action}: registered={status.IsRegistered} error={status.Error}");
    }
    Console.Out.Flush();
    MacOSNative.objc_msgSend(app, MacOSNative.sel_registerName("run"));
    return 0;
}

Run("kEventParamDirectObject matches the macOS SDK", () =>
{
    Check(MacOSNative.CarbonParamDirectObject == 0x2D2D2D2D, "real global hotkey events use '----'; any other code drops every callback");
});

Run("Carbon hotkey event parameter roundtrip", () =>
{
    Check(Native.CreateEvent(0, 0x6B657962, 5, 0, 0, out var ev) == 0, "CreateEvent failed");
    try
    {
        var expected = new MacOSNative.EventHotKeyID { signature = 0x4C58484B, id = 9001 };
        Check(Native.SetEventParameter(ev, MacOSNative.CarbonParamDirectObject, 0x686B6964, 8, ref expected) == 0, "SetEventParameter failed");
        var status = MacOSNative.GetEventParameter(ev, MacOSNative.CarbonParamDirectObject, MacOSNative.CarbonTypeEventHotKeyId, out _, 8, out _, out var got);
        Check(status == 0 && got.signature == expected.signature && got.id == expected.id, "registered event cannot decode its hotkey ID, status=" + status);
    }
    finally { Native.ReleaseEvent(ev); }
});

Run("registered hotkey dispatches through the application event target", () =>
{
    using var listener = new MacOSHotkeyListener();
    var received = 0;
    listener.HotkeyPressed += () => received++;
    listener.Start();
    Check(listener.IsRegistered, "hotkey registration failed");
    Check(Native.CreateEvent(0, MacOSNative.CarbonEventClassKeyboard, MacOSNative.CarbonEventHotKeyPressed, 0, 0, out var ev) == 0, "CreateEvent failed");
    try
    {
        var id = new MacOSNative.EventHotKeyID { signature = MacOSNative.CarbonFourCharCodeLXHK, id = 9001 };
        Check(Native.SetEventParameter(ev, MacOSNative.CarbonParamDirectObject, MacOSNative.CarbonTypeEventHotKeyId, 8, ref id) == 0, "SetEventParameter failed");
        var status = Native.SendEventToEventTarget(ev, Native.GetApplicationEventTarget());
        Check(received == 1, "native application dispatch did not reach registered handler; status=" + status);
    }
    finally { Native.ReleaseEvent(ev); }
});

Run("three quick actions have independent registration and native callbacks", () =>
{
    using var service = new HotkeyService();
    service.Configure("D", "A", "S");
    var received = new List<QuickAction>();
    var lookupCompatibilityCalls = 0;
    service.QuickActionPressed += received.Add;
    service.HotkeyPressed += () => lookupCompatibilityCalls++;
    service.Start();
    service.Start(); // Repeated initialization must not install duplicate native handlers.
    foreach (var action in Enum.GetValues<QuickAction>())
    {
        Check(service.GetStatus(action).IsRegistered, action + " registration: " + service.GetStatus(action).Error);
        DispatchHotkey((uint)(9001 + (int)action));
    }
    Check(received.SequenceEqual(Enum.GetValues<QuickAction>()), "native callback action mapping is wrong");
    Check(lookupCompatibilityCalls == 1, "compatibility lookup callback fired for sentence/quote");
    DispatchHotkey(9999);
    Check(received.Count == 3, "unregistered hotkey ID consumed");
});

Run("one conflicting action does not disable the other actions", () =>
{
    using var service = new HotkeyService();
    service.Configure("D", "D", "S");
    service.Start();
    Check(service.GetStatus(QuickAction.Lookup).IsRegistered, "lookup disabled by translation conflict");
    Check(!service.GetStatus(QuickAction.Translate).IsRegistered && service.GetStatus(QuickAction.Translate).Error != null, "conflict silently registered");
    Check(service.GetStatus(QuickAction.SaveQuote).IsRegistered, "quote disabled by translation conflict");
    service.Configure("F", "A", "S");
    Check(service.GetStatus(QuickAction.Lookup).IsRegistered && service.GetStatus(QuickAction.Lookup).Key == "F", "live reconfiguration failed");
    Check(service.GetStatus(QuickAction.Translate).IsRegistered, "independent conflict did not recover");
});

Run("system occupied shortcut leaves unrelated actions registered", () =>
{
    var occupiedId = new MacOSNative.EventHotKeyID { signature = 0x54455354, id = 12345 };
    Check(MacOSNative.RegisterEventHotKey(0, MacOSNative.CarbonModOption, occupiedId, Native.GetApplicationEventTarget(), 0, out var occupied) == 0, "could not reserve Option+A fixture");
    try
    {
        using var service = new HotkeyService(); service.Start();
        Check(!service.GetStatus(QuickAction.Translate).IsRegistered && service.GetStatus(QuickAction.Translate).Error != null, "occupied Option+A reported registered");
        Check(service.GetStatus(QuickAction.Lookup).IsRegistered && service.GetStatus(QuickAction.SaveQuote).IsRegistered, "occupied Option+A disabled independent shortcuts");
    }
    finally { MacOSNative.UnregisterEventHotKey(occupied); }
});

Run("invalid shortcut reports its own failure", () =>
{
    using var service = new HotkeyService();
    service.Configure("D", "", "S"); service.Start();
    Check(!service.GetStatus(QuickAction.Translate).IsRegistered && service.GetStatus(QuickAction.Translate).Error != null, "empty key accepted");
    Check(service.GetStatus(QuickAction.Lookup).IsRegistered && service.GetStatus(QuickAction.SaveQuote).IsRegistered, "valid actions disabled");
});

Run("one-byte native bool ABI declarations", () =>
{
    var methods = typeof(MacOSNative).GetMethods(BindingFlags.Public | BindingFlags.Static);
    foreach (var method in methods.Where(m => m.ReturnType == typeof(bool) && m.GetCustomAttribute<DllImportAttribute>() != null))
        Check(method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value == UnmanagedType.I1, method.Name + " bool return must be I1");
    var keyDown = methods.Single(m => m.Name == "CGEventCreateKeyboardEvent").GetParameters()[2];
    Check(keyDown.GetCustomAttribute<MarshalAsAttribute>()?.Value == UnmanagedType.I1, "keyDown must be I1");
});

Run("private pasteboard multi-item and empty format roundtrip", () => WithPasteboard(pb =>
{
    var fixture = Fixture(); WriteFixture(pb, fixture);
    var snapshot = MacOSNative.SnapshotPasteboard(pb);
    Check(Equal(fixture, snapshot), "snapshot dropped item, format or zero-length NSData");
    WriteFixture(pb, Fixture("replacement"));
    Check(MacOSNative.RestorePasteboard(pb, snapshot!), "restore failed");
    Check(Equal(fixture, MacOSNative.SnapshotPasteboard(pb)), "restore changed bytes or item order");
}));

Run("failed restore construction preserves existing private pasteboard", () => WithPasteboard(pb =>
{
    var fixture = Fixture(); WriteFixture(pb, fixture);
    var broken = new MacOSNative.PasteboardItemSnapshot(); broken.Entries.Add(("public.utf8-plain-text", null!));
    var sequence = MacOSNative.GetPasteboardChangeCount(pb);
    Check(!MacOSNative.RestorePasteboard(pb, [broken]), "invalid payload was accepted");
    Check(MacOSNative.GetPasteboardChangeCount(pb) == sequence && Equal(fixture, MacOSNative.SnapshotPasteboard(pb)), "failed construction cleared clipboard");
}));

Run("oversized private pasteboard snapshot rejects copy", () => WithPasteboard(pb =>
{
    var fixture = Fixture(); WriteFixture(pb, fixture);
    var sequence = MacOSNative.GetPasteboardChangeCount(pb);
    Check(MacOSNative.SnapshotPasteboard(pb, 1) == null, "budget overflow must reject snapshot");
    Check(MacOSNative.GetPasteboardChangeCount(pb) == sequence, "budget rejection changed clipboard");
}));

Run("unreadable promised format rejects snapshot without mutation", () => WithPasteboard(pb =>
{
    var type = MacOSNative.CreateNSStringHandle("org.lexi.fixture.unavailable");
    var types = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSArray"), MacOSNative.sel_registerName("arrayWithObject:"), type);
    MacOSNative.objc_msgSend(pb, MacOSNative.sel_registerName("declareTypes:owner:"), types, IntPtr.Zero);
    var sequence = MacOSNative.GetPasteboardChangeCount(pb);
    Check(MacOSNative.SnapshotPasteboard(pb) == null, "unavailable promised data accepted as partial snapshot");
    Check(MacOSNative.GetPasteboardChangeCount(pb) == sequence, "snapshot rejection mutated clipboard");
}));

Run("stale restore sequence preserves newer private pasteboard", () => WithPasteboard(pb =>
{
    WriteFixture(pb, Fixture());
    var sequence = MacOSNative.GetPasteboardChangeCount(pb);
    var newer = Fixture("newer-fixture"); WriteFixture(pb, newer);
    Check(!MacOSNative.RestorePasteboard(pb, Fixture(), sequence), "stale snapshot replaced newer data");
    Check(Equal(newer, MacOSNative.SnapshotPasteboard(pb)), "stale restore changed newer bytes");
}));

Run("foreground mismatch prevents restore", () => WithPasteboard(pb =>
{
    var fixture = Fixture(); WriteFixture(pb, fixture);
    var sequence = MacOSNative.GetPasteboardChangeCount(pb);
    Check(!MacOSNative.RestorePasteboard(pb, Fixture("other"), sequence, int.MaxValue), "foreign PID restore accepted");
    Check(MacOSNative.GetPasteboardChangeCount(pb) == sequence && Equal(fixture, MacOSNative.SnapshotPasteboard(pb)), "foreign PID restore mutated data");
}));

Run("empty private pasteboard snapshot and guarded restore", () => WithPasteboard(pb =>
{
    MacOSNative.objc_msgSend_nint(pb, MacOSNative.sel_registerName("clearContents"));
    Check(MacOSNative.SnapshotPasteboard(pb)?.Count == 0, "empty pasteboard rejected");
    WriteFixture(pb, Fixture());
    Check(MacOSNative.RestorePasteboard(pb, [], MacOSNative.GetPasteboardChangeCount(pb)), "empty snapshot restore failed");
    Check(MacOSNative.SnapshotPasteboard(pb)?.Count == 0, "empty snapshot left stale formats");
}));

Run("Liquid Glass native class and documented selectors", () =>
{
    var glass = MacOSNative.objc_getClass("NSGlassEffectView");
    Check(glass != 0, "this macOS must expose the genuine NSGlassEffectView");
    var type = typeof(MainWindow).Assembly.GetType("Lexi.MacGlassMaterial");
    Check(type != null, "shared native glass attachment is missing");
    Check((bool)type!.GetProperty("IsSupported")!.GetValue(null)!, "native glass capability was not detected");
});

Run("Liquid Glass strength endpoints clamp and preserve continuous range", () =>
{
    var type = typeof(MainWindow).Assembly.GetType("Lexi.MacGlassMaterial");
    Check(type != null, "glass intensity policy is missing");
    double Clamp(double value) => (double)type!.GetMethod("NormalizeIntensity")!.Invoke(null, [value])!;
    Check(Clamp(-1) == 0 && Clamp(2) == 1 && Clamp(.37) == .37 && Clamp(double.NaN) == .65,
        "strength must clamp safely without quantizing continuous values");
});

Run("high contrast and opaque appearance override native glass", () =>
{
    var type = typeof(MainWindow).Assembly.GetType("Lexi.MacGlassMaterial");
    Check(type != null, "glass material policy is missing");
    var settings = new AppSettings();
    typeof(AppSettings).GetProperty("Material")!.SetValue(settings, "LiquidGlass");
    bool Wants() => (bool)type!.GetMethod("WantsLiquidGlass")!.Invoke(null, [settings])!;
    Check(Wants(), "liquid glass preference was ignored");
    settings.HighContrast = true; Check(!Wants(), "high contrast must win");
    settings.HighContrast = false; settings.OpaqueMaterial = true; Check(!Wants(), "opaque must win");
});

Console.WriteLine($"RESULT failures={failures}; isolated named pasteboards only; no keyboard events posted.");
return failures == 0 ? 0 : 1;

static void DispatchHotkey(uint id)
{
    if (Native.CreateEvent(0, MacOSNative.CarbonEventClassKeyboard, MacOSNative.CarbonEventHotKeyPressed, 0, 0, out var ev) != 0) throw new Exception("CreateEvent failed");
    try
    {
        var key = new MacOSNative.EventHotKeyID { signature = MacOSNative.CarbonFourCharCodeLXHK, id = id };
        if (Native.SetEventParameter(ev, MacOSNative.CarbonParamDirectObject, MacOSNative.CarbonTypeEventHotKeyId, 8, ref key) != 0) throw new Exception("SetEventParameter failed");
        Native.SendEventToEventTarget(ev, Native.GetApplicationEventTarget());
    }
    finally { Native.ReleaseEvent(ev); }
}

static List<MacOSNative.PasteboardItemSnapshot> Fixture(string word = "fixture-only")
{
    var first = new MacOSNative.PasteboardItemSnapshot();
    first.Entries.Add(("public.utf8-plain-text", Encoding.UTF8.GetBytes(word)));
    first.Entries.Add(("org.lexi.fixture.binary", [0, 1, 127, 255]));
    var second = new MacOSNative.PasteboardItemSnapshot();
    second.Entries.Add(("public.utf8-plain-text", Encoding.UTF8.GetBytes("second")));
    second.Entries.Add(("org.lexi.fixture.empty", []));
    return [first, second];
}
static bool Equal(List<MacOSNative.PasteboardItemSnapshot> expected, List<MacOSNative.PasteboardItemSnapshot>? actual)
    => actual != null && expected.Count == actual.Count && expected.Zip(actual).All(pair =>
        pair.First.Entries.Count == pair.Second.Entries.Count && pair.First.Entries.All(a => pair.Second.Entries.Any(b => a.Type == b.Type && a.Data.SequenceEqual(b.Data))));

static void WithPasteboard(Action<nint> action)
{
    var pool = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSAutoreleasePool"), MacOSNative.sel_registerName("new"));
    var pb = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSPasteboard"), MacOSNative.sel_registerName("pasteboardWithUniqueName"));
    try { if (pb == 0) throw new Exception("private pasteboard allocation failed"); action(pb); }
    finally
    {
        if (pb != 0) Native.SendVoid(pb, MacOSNative.sel_registerName("releaseGlobally"));
        Native.SendVoid(pool, MacOSNative.sel_registerName("drain"));
    }
}

static void WriteFixture(nint pb, List<MacOSNative.PasteboardItemSnapshot> fixture)
{
    var items = new nint[fixture.Count]; var pin = GCHandle.Alloc(items, GCHandleType.Pinned);
    try
    {
        for (var i = 0; i < fixture.Count; i++)
        {
            items[i] = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSPasteboardItem"), MacOSNative.sel_registerName("new"));
            foreach (var (type, bytes) in fixture[i].Entries)
            {
                var data = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSData"), MacOSNative.sel_registerName("dataWithBytes:length:"), bytes, (nuint)bytes.Length);
                if (Native.SendBool2(items[i], MacOSNative.sel_registerName("setData:forType:"), data, MacOSNative.CreateNSStringHandle(type)) == 0)
                    throw new Exception("fixture native data write failed");
            }
        }
        var array = MacOSNative.objc_msgSend(MacOSNative.objc_getClass("NSArray"), MacOSNative.sel_registerName("arrayWithObjects:count:"), pin.AddrOfPinnedObject(), (nuint)items.Length);
        MacOSNative.objc_msgSend_nint(pb, MacOSNative.sel_registerName("clearContents"));
        if (Native.SendBool1(pb, MacOSNative.sel_registerName("writeObjects:"), array) == 0) throw new Exception("fixture writeObjects failed");
    }
    finally { foreach (var item in items) if (item != 0) Native.SendVoid(item, MacOSNative.sel_registerName("release")); pin.Free(); }
}

static class Native
{
    const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(Carbon)] internal static extern nint GetMainEventQueue();
    [DllImport(Carbon)] internal static extern int PostEventToQueue(nint queue, nint ev, uint priority);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendDate(nint self, nint selector, double seconds);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint NextEvent(nint self, nint selector, ulong mask, nint date, nint mode, [MarshalAs(UnmanagedType.I1)] bool dequeue);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SendVoid1(nint self, nint selector, nint value);
    [DllImport(Carbon)] internal static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)] internal static extern int SendEventToEventTarget(nint ev, nint target);
    [DllImport(Carbon)] internal static extern int CreateEvent(nint allocator, uint eventClass, uint kind, double time, uint attributes, out nint ev);
    [DllImport(Carbon)] internal static extern int SetEventParameter(nint ev, uint name, uint type, uint size, ref MacOSNative.EventHotKeyID data);
    [DllImport(Carbon)] internal static extern void ReleaseEvent(nint ev);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(nint self, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern byte SendBool1(nint self, nint selector, nint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern byte SendBool2(nint self, nint selector, nint a, nint b);
}
