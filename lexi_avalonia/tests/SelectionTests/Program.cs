using Lexi;

static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
Check(SelectionCaptureService.NormalizeWord("  Resilient  ") == "Resilient", "selected word normalized");
Check(SelectionCaptureService.NormalizeWord("mother-in-law") == "mother-in-law", "hyphen supported");
Check(SelectionCaptureService.NormalizeWord("don't") == "don't", "apostrophe supported");
foreach (var input in new[] { "", "hello world", "中文", "abc123", "https://a.com", new string('a', 65) })
    Check(SelectionCaptureService.NormalizeWord(input) == null, "reject non-word selection");
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

var textFake = new FakeBackend { Text = "  A sentence.\r\nSecond line.  " };
var sentence = await new SelectionCaptureService(textFake).CaptureTextAsync((nint)10);
Check(sentence == "A sentence.\nSecond line.", "sentence selection keeps content and normalizes line endings");
Check(textFake.Restored, "sentence capture restores clipboard");
textFake = new FakeBackend { Text = "Two words" };
Check(await new SelectionCaptureService(textFake).CaptureAsync((nint)10) == null, "word capture still rejects sentences");
textFake = new FakeBackend { SelectedText = "Accessibility selection", CanSnapshot = false };
Check(await new SelectionCaptureService(textFake).CaptureTextAsync((nint)10) == "Accessibility selection" && !textFake.CopySent, "accessibility selected text precedes clipboard fallback");
textFake = new FakeBackend { SelectedText = "word", SwitchDuringAccessibility = true };
Check(await new SelectionCaptureService(textFake).CaptureTextAsync((nint)10) == null && !textFake.CopySent, "accessibility focus change discards selection");
foreach (var value in new[] { new string('a', 4097), "  ", "invalid\0text" })
    Check(SelectionCaptureService.NormalizeText(value) == null, "text capture rejects unsafe or oversized text");
Check(SelectionCaptureService.NormalizeText(new string('a', 4096))?.Length == 4096, "text capture accepts exactly 4096 characters");
textFake = new FakeBackend { ConcurrentCopy = true };
Check(await new SelectionCaptureService(textFake).CaptureTextAsync((nint)10) == null && !textFake.Restored, "concurrent clipboard change discards copied result");

sealed class FakeBackend : ISelectionClipboard
{
    public nint Foreground { get; set; } = (nint)10;
    public bool ModifiersReleased { get; set; } = true;
    public string? Text = "selected", SelectedText;
    public bool SwitchDuringAccessibility;
    public string? ReadSelectedText(nint source) { if (SwitchDuringAccessibility) Foreground = (nint)20; return SelectedText; }
    public bool CanSnapshot = true, CopyUpdates = true, CopySent, Restored, ConcurrentCopy, SwitchDuringCopy, ForeignOwner;
    public uint Sequence { get; private set; } = 1;
    public bool TrySnapshot() => CanSnapshot;
    public bool SendCopy() { CopySent = true; if (CopyUpdates) Sequence++; if (SwitchDuringCopy) Foreground = (nint)20; return true; }
    public (string? Text, uint Sequence) ReadText(nint source) { if (ForeignOwner) return (null, 0); var seq = Sequence; if (ConcurrentCopy) Sequence++; return (Text, seq); }
    public bool Restore(uint expectedSequence, nint source = 0) { if (Sequence != expectedSequence || ForeignOwner || (source != 0 && Foreground != source)) return false; Restored = true; return true; }
    public void Dispose() { }
}
