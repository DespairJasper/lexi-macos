using Lexi;

static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
Check(SelectionCaptureService.NormalizeWord("  Resilient  ") == "Resilient", "selected word normalized");
Check(SelectionCaptureService.NormalizeWord("mother-in-law") == "mother-in-law", "hyphen supported");
Check(SelectionCaptureService.NormalizeWord("don't") == "don't", "apostrophe supported");
foreach (var input in new[] { "", "hello world", "中文", "abc123", "https://a.com", new string('a', 65) })
    Check(SelectionCaptureService.NormalizeWord(input) == null, "reject non-word selection");
Check(SelectionCaptureService.NormalizeText("  Evidence matters.  ") == "Evidence matters.", "sentence selection normalized");
Check(SelectionCaptureService.NormalizeText(new string('a',4097)) == null, "oversized sentence rejected");
var fake = new FakeBackend();
var capture = new SelectionCaptureService(fake);
Check(await capture.CaptureAsync((nint)10) == "selected", "fresh selection copied");
Check(fake.Restored, "original clipboard restored");
fake = new FakeBackend { CopyUpdates = false };
Check(await new SelectionCaptureService(fake).CaptureAsync((nint)10) == null, "no selection never returns old clipboard");
Check(!fake.Restored, "unchanged clipboard not rewritten");
fake = new FakeBackend { CanSnapshot = false };
Check(await new SelectionCaptureService(fake).CaptureAsync((nint)10) == null && !fake.CopySent, "unsafe clipboard untouched");
fake = new FakeBackend { Foreground = (nint)20 };
Check(await new SelectionCaptureService(fake).CaptureAsync((nint)10) == null && !fake.CopySent, "focus change prevents copy");
fake = new FakeBackend { ConcurrentCopy = true };
await new SelectionCaptureService(fake).CaptureAsync((nint)10);
Check(!fake.Restored, "newer user clipboard not overwritten");
fake = new FakeBackend { ModifiersReleased = false };
Check(await new SelectionCaptureService(fake).CaptureAsync((nint)10) == null && !fake.CopySent, "held modifiers never altered");
fake = new FakeBackend { SwitchDuringCopy = true };
Check(await new SelectionCaptureService(fake).CaptureAsync((nint)10) == null && !fake.Restored, "foreground switch during copy never restores over user copy");
fake = new FakeBackend { ForeignOwner = true };
Check(await new SelectionCaptureService(fake).CaptureAsync((nint)10) == null && !fake.Restored, "another clipboard owner never interpreted as captured selection");

var concurrent1 = new ConcurrentBackend(); var concurrent2 = new ConcurrentBackend();
await Task.WhenAll(new SelectionCaptureService(concurrent1).CaptureAsync((nint)10), new SelectionCaptureService(concurrent2).CaptureAsync((nint)10));
Check(!ConcurrentBackend.Overlapped, "overlapping selection captures never share a clipboard transaction");

sealed class FakeBackend : ISelectionClipboard
{
    public nint Foreground { get; set; } = (nint)10;
    public bool ModifiersReleased { get; set; } = true;
    public bool CanSnapshot = true, CopyUpdates = true, CopySent, Restored, ConcurrentCopy, SwitchDuringCopy, ForeignOwner;
    public uint Sequence { get; private set; } = 1;
    public bool TrySnapshot() => CanSnapshot;
    public bool SendCopy() { CopySent = true; if (CopyUpdates) Sequence++; if (SwitchDuringCopy) Foreground = (nint)20; return true; }
    public (string? Text, uint Sequence) ReadText(nint source) { if (ForeignOwner) return (null, 0); var seq = Sequence; if (ConcurrentCopy) Sequence++; return ("selected", seq); }
    public bool Restore(uint expectedSequence, nint source = 0) { if (Sequence != expectedSequence || ForeignOwner || (source != 0 && Foreground != source)) return false; Restored = true; return true; }
    public void Dispose() { }
}
sealed class ConcurrentBackend : ISelectionClipboard
{
    public static int Active; public static bool Overlapped;
    public nint Foreground => (nint)10;
    public bool ModifiersReleased => true;
    public uint Sequence { get; private set; } = 1;
    public bool TrySnapshot() { if (Interlocked.Increment(ref Active)>1) Overlapped=true; Thread.Sleep(80); return true; }
    public bool SendCopy() { Sequence++; return true; }
    public (string? Text,uint Sequence) ReadText(nint source) => ("selected",Sequence);
    public bool Restore(uint sequence,nint source=0)=>true;
    public void Dispose() { Interlocked.Decrement(ref Active); }
}