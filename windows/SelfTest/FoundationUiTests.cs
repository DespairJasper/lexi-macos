using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Input;

namespace Lexi;

public static class FoundationUiTests
{
    public static async Task RunAsync(MainWindow window, Action<bool, string> check)
    {
        T C<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception("Missing " + name);
        void Click(string name) => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Lookup(string word)
        {
            C<TextBox>("LookupInput").Text = word;
            await (Task)typeof(MainWindow).GetMethod("PerformLookupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        }
        Click("NavLookup");
        await Lookup("serendipity");
        check(C<TextBlock>("ArchiveStateText").Text!.Contains("已入库"), "saved lookup has explicit archive state");
        Click("EditCurrentArchiveBtn");
        C<TextBox>("ArchiveSourceTitleInput").Text = "A personal reading note";
        C<TextBox>("ArchiveExcerptInput").Text = "A moment of serendipity changed the plan.";
        C<TextBox>("ArchiveNotesInput").Text = "我的备注，保留原文。";
        C<TextBox>("ArchiveTagsInput").Text = "阅读, 工作";
        C<TextBox>("ArchiveExamplesInput").Text = "It was pure serendipity. | 这完全是机缘巧合。";
        Click("DialogEditConfirmBtn");
        check(!C<Border>("DialogEditOverlay").IsVisible, "archive editor saves and closes");
        Click("EditCurrentArchiveBtn");
        check(C<TextBox>("ArchiveNotesInput").Text == "我的备注，保留原文。", "notes survive archive reload");
        check(C<TextBox>("ArchiveSourceTitleInput").Text == "A personal reading note", "source survives archive reload");
        check(C<TextBox>("ArchiveExamplesInput").Text!.Contains("pure serendipity"), "edited AI examples survive archive reload");
        C<TextBox>("DialogEditInput").Text = "";
        Click("DialogEditConfirmBtn");
        check(C<Border>("DialogEditOverlay").IsVisible, "invalid archive remains open without discarding edits");
        Click("DialogEditCancelBtn");
        Click("RecordEncounterBtn");
        check(C<TextBlock>("ArchiveStateText").Text!.Contains("2"), "explicit encounter increments count once");
        await Lookup("serendipity");
        check(C<TextBlock>("ArchiveStateText").Text!.Contains("2"), "repeated query does not inflate encounter count");
        Click("NavSettings");
        check(C<TextBlock>("DataLocationText").Text!.Contains("vocab.sqlite3"), "data settings expose active database path");
        check(C<Button>("BackupNowBtn").IsVisible && C<Button>("RestoreBackupBtn").IsVisible, "backup and restore controls are available");

        // Exercise production restore against an isolated snapshot, never the user's database.
        var service = (VocabularyService)typeof(MainWindow).GetField("_vocabService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var backup = service.CreateManualBackup();
        service.AddWord("restore-fixture", "", "仅用于验证恢复", "");
        typeof(MainWindow).GetField("_pendingRestore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, backup);
        await (Task)typeof(MainWindow).GetMethod("RestoreChosenBackupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        var restored = (VocabularyService)typeof(MainWindow).GetField("_vocabService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        check(restored.GetAllWords().All(w => w.Word != "restore-fixture"), "restore replaces data using selected snapshot");
        check(restored.GetAllWords().Single(w => w.Word == "serendipity").Notes == "我的备注，保留原文。", "restore preserves archived context");
        check(Directory.GetDirectories(Path.GetDirectoryName(restored.DatabasePath)!, "before-restore-*").Length > 0, "restore preserves pre-restore database family");

        check(C<Border>("DragBar").Background != null, "title bar empty space is hit-testable");
        check(window.CanResize, "window resizing enabled");
        check(MainWindow.ResizeEdgeAt(new Point(2, 2), new Size(1060,760)) == WindowEdge.NorthWest
            && MainWindow.ResizeEdgeAt(new Point(1058,758), new Size(1060,760)) == WindowEdge.SouthEast
            && MainWindow.ResizeEdgeAt(new Point(500,300), new Size(1060,760)) == null, "resize hit testing distinguishes corners and content");
        Click("MaximizeBtn"); await Task.Delay(200);
        check(window.WindowState == WindowState.Maximized, "maximize button changes native window state");
        var workArea = window.Screens.ScreenFromWindow(window)!.WorkingArea;
        check(window.Position.X >= workArea.X - 2 && window.Position.Y >= workArea.Y - 2
            && window.Bounds.Width * window.RenderScaling <= workArea.Width + 2
            && window.Bounds.Height * window.RenderScaling <= workArea.Height + 2, "maximized window fits current monitor work area");
        window.HideToTray(); window.ShowAndActivate();
        check(window.WindowState == WindowState.Maximized, "tray wake preserves maximized state");
        Click("HideToTrayBtn"); await Task.Delay(200);
        check(window.WindowState == WindowState.Minimized && window.IsVisible, "minimize button minimizes rather than hides");
        window.ShowAndActivate(); await Task.Delay(200);
        check(window.WindowState == WindowState.Maximized, "wake restores pre-minimize state");
        Click("MaximizeBtn"); await Task.Delay(200);
        check(window.WindowState == WindowState.Normal, "restore button returns to normal window");
        window.Width = 840; window.Height = 600;
        await Task.Delay(200);
        await Lookup("serendipity");
        Click("EditCurrentArchiveBtn");
        await Task.Delay(150);
        check(C<Border>("DialogEditOverlay").IsEffectivelyVisible, "archive editor is visible at minimum window size");
        var save = C<Button>("DialogEditConfirmBtn");
        var position = save.TranslatePoint(default, window)!.Value;
        check(position.Y >= 0 && position.Y + save.Bounds.Height <= window.Bounds.Height, "archive save button stays inside smallest window");
        using (var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96,96)))
        {
            bmp.Render(window);
            bmp.Save(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, "foundation-editor.png"));
        }
        Click("DialogEditCancelBtn");
        window.Width = 1060; window.Height = 760;
    }
}
