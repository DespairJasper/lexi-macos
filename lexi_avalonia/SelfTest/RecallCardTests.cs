using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
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
        void Press(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
            C<Grid>("PageReview").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        string Label(string name) => ((TextBlock)((StackPanel)C<Button>(name).Content!).Children[0]).Text ?? "";
        double CardY() => C<Border>("ReviewCard").RenderTransform is TransformOperations t ? t.Value.M32 : 0;
        var store = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()); db.Open();
        void Sql(string query) { using var command = db.CreateCommand(); command.CommandText = query; command.ExecuteNonQuery(); }
        StudyRound<WordItem> Round() => (StudyRound<WordItem>)typeof(MainWindow).GetField("_reviewRound", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        WordItem Stored(string word) => store.GetAllWords().Single(x => x.Word == word);
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
            check(C<TextBlock>("ReviewWordText").Text == "recall-one" && C<TextBlock>("ReviewRemainingText").Text == "已完成 0/2 · 剩余 2",
                "recall ignores archive filters, excludes future words and counts this round");
            check(!C<Border>("ReviewAnswer").IsVisible && C<TextBlock>("ReviewMeaningText").Text == "" && C<Grid>("ReviewRatingBar").IsVisible,
                "recall front hides answers and exposes Q/W/E self-assessment");
            check(C<TextBlock>("ReviewHintText").Text!.Contains("瞬间想起词义")
                && Label("ReviewRememberBtn") == "认识  Q" && Label("ReviewUnsureBtn") == "模糊  W" && Label("ReviewUnfamiliarBtn") == "忘记了  E",
                "recall front shows the recall hint line and coloured rating actions");
            check(!C<Button>("ReviewRevealBtn").IsVisible && !C<Border>("ReviewBackTwo").IsVisible && !C<Border>("ReviewBackOne").IsVisible,
                "legacy contract controls stay hidden in the new flow");
            check(!C<Border>("SidebarShell").IsEffectivelyVisible && C<Border>("RootWindowBorder").Background is LinearGradientBrush,
                "review occupies a full-page glacier backdrop");
            await Snapshot("review-front");

            // 模糊：等级不变、当日仍到期，本轮排回队尾。
            var beforeOne = Stored("recall-one");
            Press(Key.W);
            await Task.Delay(40);
            var unsure = Stored("recall-one");
            check(unsure.Stage == beforeOne.Stage && unsure.NextReviewDate == DateTime.Today.ToString("yyyy-MM-dd"),
                "unsure keeps the stage and stays due today");
            check(C<Border>("ReviewAnswer").IsVisible && C<TextBlock>("ReviewMeaningText").Text == beforeOne.Translation
                && C<TextBlock>("ReviewHintText").Text!.Contains("已记为模糊"),
                "unsure reveals the answer with clear feedback");
            check(Label("ReviewRememberBtn") == "下一词  Space" && !C<Button>("ReviewUnsureBtn").IsVisible
                && Label("ReviewUnfamiliarBtn") == "记错了  E", "answer footer switches to Next Space and Wrong E");
            check(C<TextBlock>("ReviewRemainingText").Text == "已完成 0/2 · 剩余 2" && Round().Unsure == 1 && Round().Completed == 0,
                "unsure does not count as remembered and stays in this round");
            await Task.Delay(80);
            var ratingBar=C<Grid>("ReviewRatingBar"); var nextAction=C<Button>("ReviewRememberBtn"); var wrongAction=C<Button>("ReviewUnfamiliarBtn");
            var nextCenter=nextAction.TranslatePoint(new Point(nextAction.Bounds.Width/2,0),ratingBar)!.Value.X;
            var wrongCenter=wrongAction.TranslatePoint(new Point(wrongAction.Bounds.Width/2,0),ratingBar)!.Value.X;
            check(Math.Abs(nextCenter+wrongCenter-ratingBar.Bounds.Width)<1 && Math.Abs(nextCenter-ratingBar.Bounds.Width/4)<1, "review answer actions are symmetric about page center");
            await Snapshot("review-back-long");
            var button = C<Button>("ReviewRememberBtn"); var corner = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), w);
            check(button.IsEffectivelyVisible && corner.HasValue && corner.Value.Y < 600 && corner.Value.X < 840, "long answer keeps rating actions inside 840x600");

            // 换卡：旧卡上移淡出，新卡自下方上浮淡入。
            var advance = (Task)Call("AdvanceReviewAsync")!;
            var partialEntry = false;
            var sawExit = false;
            var entryBelow = true;
            var lastEntryOpacity = 0d;
            var lastExitOpacity = 1d;
            var monotonic = true;
            while (!advance.IsCompleted)
            {
                var opacity = C<Border>("ReviewCard").Opacity;
                if (C<TextBlock>("ReviewWordText").Text == "recall-two")
                {
                    partialEntry |= opacity < .95;
                    monotonic &= opacity >= lastEntryOpacity;
                    lastEntryOpacity = opacity;
                    entryBelow &= CardY() >= -0.5;
                }
                else
                {
                    sawExit |= CardY() < -2 && opacity < 1;
                    monotonic &= opacity <= lastExitOpacity;
                    lastExitOpacity = opacity;
                }
                await Task.Delay(8);
            }
            await advance;
            check(sawExit, "the outgoing card slides up while fading out");
            check(partialEntry && entryBelow, "the incoming card rises from below and enters progressively");
            check(monotonic && C<Border>("ReviewCard").Opacity == 1 && Math.Abs(CardY()) < 0.01,
                "card opacity and position settle at rest with no snap-back");
            check(C<TextBlock>("ReviewWordText").Text == "recall-two" && C<TextBlock>("ReviewMeaningText").Text == "", "next card hides its answer");

            // 记错了：先按认识，再改判为忘记。
            var beforeTwo = Stored("recall-two").Stage;
            Press(Key.Q);
            await Task.Delay(40);
            check(Round().Completed == 1 && Stored("recall-two").Stage == beforeTwo + 1, "know it advances the round and one stage");
            check(Label("ReviewUnfamiliarBtn") == "记错了  E" && C<Button>("ReviewUnfamiliarBtn").IsVisible, "a rating can still be corrected");
            Press(Key.E);
            await Task.Delay(40);
            var forgot = Stored("recall-two");
            check(forgot.Stage == 0 && forgot.NextReviewDate == DateTime.Today.ToString("yyyy-MM-dd"),
                "wrong E resets the word to the first stage and keeps it due today");
            check(C<TextBlock>("ReviewHintText").Text!.Contains("已改判为忘记") && Round().Completed == 0 && Round().Forgot == 1,
                "reclassification returns the word to this round");
            check(!C<Button>("ReviewUnfamiliarBtn").IsVisible, "forgot cannot be reclassified twice");
            await Task.Delay(80);
            check(Math.Abs(C<Button>("ReviewRememberBtn").TranslatePoint(new Point(C<Button>("ReviewRememberBtn").Bounds.Width/2,0),C<Grid>("ReviewRatingBar"))!.Value.X-C<Grid>("ReviewRatingBar").Bounds.Width/2)<1, "single review next action is centered after forgotten reclassification");

            // 模糊与忘记的词必须在本轮再次出现，并且要攒够 3 次「认识」才算完全记住。
            Press(Key.Space); await Task.Delay(450);
            check(Round().Remaining == 2 && Round().Completed == 0, "unsure and forgot words come back in the same round");
            var retryFirst = C<TextBlock>("ReviewWordText").Text!;
            Press(Key.Q); await Task.Delay(40);
            check(Round().Completed == 0 && Round().Known == 1 && C<TextBlock>("ReviewHintText").Text!.Contains("1/3"),
                "a known rating on a re-queued word only banks one streak point");
            check(C<Border>("ReviewStreakBar1").Background is ISolidColorBrush lit && lit.Color == Color.Parse("#268D98")
                && C<Border>("ReviewStreakBar3").Background is ISolidColorBrush dim && dim.Color != Color.Parse("#268D98"),
                "the review streak bars light from the bottom up");
            await Snapshot("review-streak-one");
            Press(Key.Space); await Task.Delay(450);
            check(C<TextBlock>("ReviewWordText").Text != retryFirst, "the next pass reaches the other re-queued word");
            for (var guard = 0; guard < 16 && !Round().IsFinished; guard++)
            {
                if (C<Border>("ReviewAnswer").IsVisible) { Press(Key.Space); await Task.Delay(450); }
                else { Press(Key.Q); await Task.Delay(40); }
            }
            check(Round().IsFinished && Round().Known == 6 && Round().Completed == 2,
                "both re-queued words need three known ratings to finish the round");
            Press(Key.Space);
            await Task.Delay(450);
            check(C<Border>("ReviewEmptyCard").IsVisible && C<TextBlock>("ReviewEmptyTitle").Text == "本轮完成"
                && C<TextBlock>("ReviewEmptyBody").Text!.Contains("全部通过「认识」"),
                "finishing the round shows the completion card with round statistics");
            check(C<TextBlock>("ReviewRemainingText").Text == "已完成 2/2 · 剩余 0" && C<Button>("ReviewRestartBtn").IsVisible,
                "completion card reports the fixed round total and offers a way back");
            check(Stored("recall-two").Stage == 1, "recalling a reset word again starts from the first stage");
            await Snapshot("review-complete");

            // 撤销：记录评分后 ⌥Space，单词回到队首而且进度完整还原。
            var beforeUndo = Stored("recall-one");
            // 模拟新的一天：清掉今天的同日去重记录，让这个词可以再次推进阶段。
            Sql($"DELETE FROM review_logs WHERE word_id = {beforeUndo.Id} AND action = 'batch_review' AND log_date = '{DateTime.Today:yyyy-MM-dd}'");
            store.ExecuteBatch([beforeUndo.Id], "today"); Call("RefreshWords"); Click("NavLookup"); Click("NavReview");
            Press(Key.Q); await Task.Delay(40);
            check(Stored("recall-one").Stage == beforeUndo.Stage + 1, "know it advances exactly one stage");
            Press(Key.Space, KeyModifiers.Alt);
            await Task.Delay(60);
            check(Stored("recall-one").Stage == beforeUndo.Stage && C<TextBlock>("ReviewWordText").Text == "recall-one"
                && !C<Border>("ReviewAnswer").IsVisible && C<TextBlock>("ReviewRemainingText").Text == "已完成 0/1 · 剩余 1",
                "Option Space restores progress and puts the word back on the front card");

            // 写入失败：保持当前卡，不产生半个评价。
            Sql("CREATE TRIGGER recall_fail BEFORE UPDATE ON words BEGIN SELECT RAISE(ABORT,'fixture write blocked'); END;");
            try
            {
                Press(Key.Q);
                await Task.Delay(60);
                check(C<TextBlock>("ReviewWordText").Text == "recall-one" && !C<Border>("ReviewAnswer").IsVisible
                    && C<TextBlock>("ReviewHintText").Text!.Contains("本次复习未完成")
                    && Stored("recall-one").Stage == beforeUndo.Stage, "failed rating keeps the front card and unchanged progress");
            }
            finally { Sql("DROP TRIGGER recall_fail"); }

            // 同日重复评价：batch_review 保持幂等；显式重新安排到今日后再次可用。
            Press(Key.Q); await Task.Delay(40);
            // 裁定 O-1（findings.md §P）：`today` 是显式手动覆盖，会把已建 FSRS 卡的词重新推回今日队列。
            store.ExecuteBatch([beforeUndo.Id], "today");
            Call("RefreshWords"); Click("NavLookup"); Click("NavReview");
            // 非空转前置条件：队列真的被重建过（否则下面看到的 ReviewWordText 只是上一张卡残留的文本，
            // 空队列时 OnReviewRatingAsync 会直接 return，后面两条断言都会假绿）。
            check(Round().HasCurrent && Round().Current.Word == "recall-one" && C<Grid>("ReviewRatingBar").IsVisible,
                "explicit reschedule really rebuilt a non-empty front card (not a stale label)");
            check(C<TextBlock>("ReviewWordText").Text == "recall-one", "same-date explicit reschedule invalidates handled revision");
            var reviewedStage = Stored("recall-one").Stage;
            Press(Key.Q); await Task.Delay(40);
            // 非空转前置条件：这次评价确实在轮内结算了（首评「认识」对复习词即出队），
            // 而旧列的 stage 仍被 batch_review 的同日去重挡住 —— 幂等才是有内容的幂等。
            check(Round().Completed == 1 && C<Border>("ReviewAnswer").IsVisible,
                "the same-day rating really executed on the deck (not an empty-queue no-op)");
            check(Stored("recall-one").Stage == reviewedStage, "same-day rating preserves scheduling idempotence");

            // 页面切换取消未完成的动画后，重新打开不能留下半透明的卡。
            Click("NavLookup"); Click("NavReview");
            await Task.Delay(500);
            check(C<Border>("ReviewCard").Opacity == 1 && Math.Abs(CardY()) < 0.01, "page navigation cancels old animation without fading the reopened deck");

            C<CheckBox>("HighContrastBox").IsChecked = true;
            C<CheckBox>("OpaqueMaterialBox").IsChecked = true;
            C<CheckBox>("ReduceMotionBox").IsChecked = true;
            store.ExecuteBatch([beforeUndo.Id], "today");
            Call("RefreshWords"); Click("NavLookup"); Click("NavReview");
            var instant = (Task)Call("AdvanceReviewAsync")!;
            check(instant.IsCompleted && C<Border>("ReviewCard").Opacity == 1 && Math.Abs(CardY()) < 0.01,
                "reduced motion changes cards immediately without transient opacity");
            await instant;
            var saved = store.LoadSettings();
            check(saved.HighContrast && saved.OpaqueMaterial && saved.ReduceMotion && w.TransparencyLevelHint.SequenceEqual(new[] { WindowTransparencyLevel.None }), "accessibility preferences persist and disable transparency");
            Click("NavSettings"); await Snapshot("settings-high-contrast");

            // 裁定 O-1 的 master 条款：掌握只把旧列置为 stage=5 / status='mastered'，**不碰** fsrs_cards。
            // 先做对照组证明这张卡此刻确实是「到期」的，再证明它照样进不了今日队列 ——
            // 排除来自 status（并集口径只认 status == 'learning'），不是来自「卡没到期」。
            store.ExecuteBatch([beforeUndo.Id], "master");
            Call("RefreshWords");
            var masteredKey = "archive:" + Stored("recall-one").Archive.Uuid;
            check(((ILearningMemoryStore)store).QueryDue(DateTime.UtcNow, int.MaxValue).Any(card => card.WordKey == masteredKey),
                "control: the mastered word still has a genuinely due FSRS card");
            // MemoryPendingReviewWords 是并集口径本身（未过 _reviewHandled），GetPendingReviewWords 是实际卡组。
            var unionDue = ((System.Collections.IEnumerable)Call("MemoryPendingReviewWords")!).Cast<WordItem>().ToList();
            var deckDue = ((System.Collections.IEnumerable)Call("GetPendingReviewWords")!).Cast<WordItem>().ToList();
            check(Stored("recall-one").Status == "mastered"
                && !unionDue.Any(word => word.Word == "recall-one")
                && !deckDue.Any(word => word.Word == "recall-one"),
                "master keeps a word out of today's deck even while its FSRS card is due");
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
