using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace Lexi;

public static class RecallCardTests
{
    public static async Task RunAsync(MainWindow w, Action<bool, string> check)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Requires isolated data.");
        T C<T>(string name) where T : Control => w.FindControl<T>(name)!;
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, args);
        void Click(string name) => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var store = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()); db.Open();
        void Sql(string query) { using var command = db.CreateCommand(); command.CommandText = query; command.ExecuteNonQuery(); }
        var originals = store.GetAllWords();
        var settings = store.LoadSettings();
        var size = w.ClientSize;
        async Task Snapshot(string name)
        {
            await Task.Delay(80);
            using var bmp = new RenderTargetBitmap(new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height), new Vector(96,96));
            bmp.Render(w); bmp.Save(Path.Combine(folder, name + ".png"));
        }
        try
        {
            C<CheckBox>("ReduceMotionBox").IsChecked = false;
            Sql("UPDATE words SET next_review_date='2999-01-01' WHERE status='learning'");
            store.AddWord("recall-one", "/wʌn/", "第一张的隐藏释义", string.Join(" ", Enumerable.Repeat("A long definition for scrolling.", 100)));
            store.AddWord("recall-two", "/tuː/", "第二张的隐藏释义", "Second definition");
            store.AddWord("recall-future", "", "未来，不应显示", "");
            var ids = store.GetAllWords().Where(x => x.Word is "recall-one" or "recall-two").Select(x => x.Id).ToArray();
            store.ExecuteBatch(ids, "today");
            Call("RefreshWords");
            C<TextBox>("VocabSearchInput").Text = "does-not-match-anything";
            w.Width = 840; w.Height = 600;
            Click("NavReview");
            check(C<TextBlock>("ReviewWordText").Text == "recall-one" && C<TextBlock>("ReviewRemainingText").Text?.StartsWith("已完成 0 / 2") == true, "recall ignores archive filters and excludes future words; actual=" + C<TextBlock>("ReviewWordText").Text + " / " + C<TextBlock>("ReviewRemainingText").Text);
            check(!C<Border>("ReviewAnswer").IsVisible && C<TextBlock>("ReviewMeaningText").Text == "" && !C<Grid>("ReviewRatingBar").IsVisible, "recall front does not disclose answer or ratings");
            var before = store.GetAllWords().Single(x => x.Word == "recall-one");
            await (Task)Call("RateReviewAsync", true)!;
            check(store.GetAllWords().Single(x => x.Id == before.Id).Stage == before.Stage, "rating before reveal does not write progress");
            await Snapshot("review-front");
            C<Grid>("PageReview").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            C<Grid>("PageReview").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });
            check(C<Border>("ReviewAnswer").IsVisible && C<TextBlock>("ReviewMeaningText").Text == before.Translation, "Space reveals current answer");
            await Snapshot("review-back-long");
            var button = C<Button>("ReviewRememberBtn"); var corner = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), w);
            check(button.IsEffectivelyVisible && corner.HasValue && corner.Value.Y < 600 && corner.Value.X < 840, "long answer keeps rating actions inside 840x600");
            Sql("CREATE TRIGGER recall_fail BEFORE INSERT ON fsrs_cards BEGIN SELECT RAISE(ABORT,'fixture write blocked'); END;");
            try
            {
                await (Task)Call("RateReviewAsync", true)!;
                check(C<TextBlock>("ReviewWordText").Text == "recall-one" && C<Border>("ReviewAnswer").IsVisible && store.GetAllWords().Single(x => x.Id == before.Id).Stage == before.Stage, "failed rating retains revealed card and unchanged progress");
            }
            finally { Sql("DROP TRIGGER recall_fail"); }
            var first = (Task)Call("RateReviewAsync", true)!;
            var second = (Task)Call("RateReviewAsync", true)!;
            // At the handoff the new card must start invisible rather than pop in.
            var partialEntry = false;
            var lastExitOpacity = 1d;
            var lastEntryOpacity = 0d;
            var monotonic = true;
            while (!first.IsCompleted)
            {
                var opacity = C<Border>("ReviewCard").Opacity;
                if (C<TextBlock>("ReviewWordText").Text == "recall-two")
                {
                    partialEntry |= opacity < .95;
                    monotonic &= opacity >= lastEntryOpacity;
                    lastEntryOpacity = opacity;
                }
                else { monotonic &= opacity <= lastExitOpacity; lastExitOpacity = opacity; }
                await Task.Delay(8);
            }
            await Task.WhenAll(first, second);
            check(partialEntry, "next recall card enters progressively instead of flashing fully visible");
            check(monotonic && C<Border>("ReviewCard").Opacity == 1, "card opacity is monotonic across exit and entry with no snap-back");
            var ratedKey = WordKeyResolver.FromArchive(store.GetAllWords().Single(x => x.Id == before.Id)).Key;
            check(store.GetAllWords().Single(x => x.Id == before.Id).Stage == before.Stage && ServiceFactory.OpenMemory(store).GetCard(ratedKey)?.Reps == 1, "double rating commits exactly one FSRS review without advancing legacy stage");
            check(C<TextBlock>("ReviewWordText").Text == "recall-two" && C<TextBlock>("ReviewMeaningText").Text == "", "next card hides its answer");

            Click("ReviewRoundUndoBtn");
            check(C<TextBlock>("ReviewWordText").Text == "recall-one", "review undo restores the previous card");
            var firstKey = WordKeyResolver.FromArchive(store.GetAllWords().Single(x => x.Id == before.Id)).Key;
            check(ServiceFactory.OpenMemory(store).GetCard(firstKey) == null, "review undo restores FSRS pre-state");
            Click("ReviewRevealBtn");
            await (Task)Call("RateReviewAsync", true)!;
            store.ExecuteBatch([before.Id], "today");
            Call("RefreshWords"); Click("NavLookup"); Click("NavReview");
            check(ServiceFactory.OpenMemory(store).GetCard(firstKey)?.NextReviewAtUtc?.ToLocalTime().Date == DateTime.Today, "explicit today action overrides the actual FSRS due date");

            Click("ReviewRevealBtn"); Click("NavLookup"); Click("NavReview");
            check(!C<Border>("ReviewAnswer").IsVisible, "re-entering deck hides current answer");
            Click("ReviewRevealBtn");
            var keyboardRatedWord = C<TextBlock>("ReviewWordText").Text;
            C<Grid>("PageReview").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
            await Task.Delay(450);
            check(store.GetAllWords().Single(x => x.Word == keyboardRatedWord).NextReviewDate == DateTime.Today.ToString("yyyy-MM-dd"), "left-key unfamiliar remains due today for relearning");
            Click("NavLookup"); Click("NavReview");
            await Task.Delay(500);
            check(C<Border>("ReviewCard").Opacity == 1, "page navigation cancels old animation without fading the reopened deck");
            check(!C<Border>("ReviewEmptyCard").IsVisible && C<TextBlock>("ReviewRemainingText").Text?.StartsWith("已完成 ") == true, "forgotten word remains in the round until its streak is complete");
            C<CheckBox>("HighContrastBox").IsChecked = true;
            C<CheckBox>("OpaqueMaterialBox").IsChecked = true;
            C<CheckBox>("ReduceMotionBox").IsChecked = true;
            store.ExecuteBatch([before.Id], "today");
            Call("RefreshWords"); Click("NavReview"); Click("ReviewRevealBtn");
            var instant = (Task)Call("RateReviewAsync", false)!;
            check(instant.IsCompleted && C<Border>("ReviewCard").Opacity == 1, "reduced motion changes cards immediately without transient opacity");
            await instant;
            var saved = store.LoadSettings();
            check(saved.HighContrast && saved.OpaqueMaterial && saved.ReduceMotion && w.TransparencyLevelHint.SequenceEqual(new[] { WindowTransparencyLevel.None }), "accessibility preferences persist and disable transparency");
            Click("NavSettings"); await Snapshot("settings-high-contrast");
        }
        finally
        {
            Sql("DROP TRIGGER IF EXISTS recall_fail");
            var fixtures = store.GetAllWords().Where(x => x.Word.StartsWith("recall-", StringComparison.Ordinal)).Select(x => x.Id).ToArray();
            if (fixtures.Length > 0) store.ExecuteBatch(fixtures, "delete");
            foreach (var old in originals)
            {
                using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE words SET next_review_date=$date WHERE id=$id";
                cmd.Parameters.AddWithValue("$date", (object?)old.NextReviewDate ?? DBNull.Value); cmd.Parameters.AddWithValue("$id", old.Id); cmd.ExecuteNonQuery();
            }
            C<CheckBox>("HighContrastBox").IsChecked = settings.HighContrast;
            C<CheckBox>("OpaqueMaterialBox").IsChecked = settings.OpaqueMaterial;
            C<CheckBox>("ReduceMotionBox").IsChecked = settings.ReduceMotion;
            C<TextBox>("VocabSearchInput").Text = "";
            w.Width = size.Width; w.Height = size.Height;
            Call("RefreshWords"); Click("NavLookup");
        }
    }
}
