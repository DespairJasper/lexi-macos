using Lexi;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [STAThread]
    static void Main()
    {
        using var original = new Win32SelectionClipboard();
        if (!original.TrySnapshot()) { Console.WriteLine("SKIP native test: cannot safely snapshot original clipboard"); Environment.ExitCode = 2; return; }
        using var form = new Form { Text = "Lexi isolated selection test", Width = 420, Height = 160, TopMost = true };
        var input = new TextBox { Text = "resilient", Dock = DockStyle.Top };
        form.Controls.Add(input);
        form.Shown += async (_, _) =>
        {
            try
            {
                Clipboard.SetText("lexi-test-original");
                input.Focus(); input.SelectAll(); SetForegroundWindow(form.Handle);
                await Task.Delay(150);
                var selection = await new SelectionCaptureService(new TraceClipboard(new Win32SelectionClipboard())).CaptureAsync(form.Handle);
                if (selection != "resilient") throw new Exception("native selected text missing");
                if (Clipboard.GetText() != "lexi-test-original") throw new Exception("native clipboard restore failed");
                Console.WriteLine("PASS real Win32 Ctrl+C selected text and original clipboard restoration");
                input.SelectionLength = 0;
                selection = await new SelectionCaptureService(new Win32SelectionClipboard()).CaptureAsync(form.Handle);
                if (selection != null || Clipboard.GetText() != "lexi-test-original") throw new Exception("stale clipboard consumed");
                Console.WriteLine("PASS real Win32 no-selection leaves original clipboard intact");
            }
            catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
            finally { original.Restore(original.Sequence); form.Close(); }
        };
        Application.Run(form);
    }

    private sealed class TraceClipboard(ISelectionClipboard inner) : ISelectionClipboard
    {
        public nint Foreground => inner.Foreground;
        public bool ModifiersReleased => inner.ModifiersReleased;
        public uint Sequence => inner.Sequence;
        public bool TrySnapshot() { var ok = inner.TrySnapshot(); Console.WriteLine($"DIAGNOSTIC snapshot={ok}"); return ok; }
        public bool SendCopy() { var ok = inner.SendCopy(); Console.WriteLine($"DIAGNOSTIC copy={ok}"); return ok; }
        public (string? Text,uint Sequence) ReadText(nint source) { var r=inner.ReadText(source); Console.WriteLine($"DIAGNOSTIC text={r.Text}, sequence={r.Sequence}"); return r; }
        public bool Restore(uint expectedSequence,nint source=0) => inner.Restore(expectedSequence,source);
        public void Dispose() => inner.Dispose();
    }
}
