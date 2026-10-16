using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace Lexi;

// Scoped incremental acceptance. Invoked only by --learning-test with LEXI_EXAM_BOOKS_ONLY=1.
public static class ExamBooksUiTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR") ?? throw new InvalidOperationException("Isolated test profile required.");
        var report = new List<string>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        async Task Invoke(string name, params object[] args) => await (Task)Call(name, args)!;
        T C<T>(string name) where T : Control => window.FindControl<T>(name) ?? window.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);
        void Click(string name) => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Check(bool ok, string message)
        {
            report.Add((ok ? "PASS " : "FAIL ") + message);
            File.WriteAllLines(Path.Combine(folder, "exam-books-result.txt"), report);
            if (!ok) throw new Exception(message);
        }
        async Task Snapshot(string name)
        {
            await Task.Delay(180);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(folder, name + ".png"));
        }
        var code = 0;
        try
        {
            window.Width = 1100; window.Height = 800;
            Check(window.FindControl<ComboBox>("PlanExamInput") != null, "study plans expose an explicit exam-topic selector before book and unit");
            var books = Field<ExamCatalog>("_examCatalog").Books;
            var topics = new[] { "cet4", "cet6", "kaoyan", "gmat", "toefl", "gre" };
            Check(books.Count == 20 && topics.Select(id => books.Count(b => b.TopicId == id)).SequenceEqual(new[] { 5, 4, 4, 3, 2, 2 }), "all upstream books are grouped into the six existing exam topics");
            Click("NavExams");
            Check(C<StackPanel>("ExamNavigationItems").Children.OfType<Button>().Count(b => b.Name?.StartsWith("NavExam_", StringComparison.Ordinal) == true) == 6,
                "twenty books do not duplicate the six sidebar topics");
            if (UiText.Language != "en") Click("LanguageToggleBtn");
            var archive = Field<IVocabularyArchive>("_vocabService");
            var catalog = Field<IeltsCatalog>("_ieltsCatalog");
            var archiveCount = archive.GetAllWords().Count;
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = archive.DatabasePath, Pooling = false }.ToString());
            db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            var testedBooks = books.Where(b => Environment.GetEnvironmentVariable("LEXI_EXAM_BOOKS_SMOKE") != "1" || b.BookId is "CET4_1" or "KaoYan_3").Where(b => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEXI_EXAM_BOOK_FILTER")) || Environment.GetEnvironmentVariable("LEXI_EXAM_BOOK_FILTER")!.Split(',').Contains(b.BookId)).ToList();
            foreach (var book in testedBooks)
            {
                Click("NavExams"); Click("NavExam_" + book.TopicId);
                var siblings = books.Where(b => b.TopicId == book.TopicId).ToList();
                var bookIndex = siblings.IndexOf(book);
                C<ComboBox>("IeltsKind").SelectedIndex = bookIndex;
                var first = book.Sections[0].Entries[0];
                var plansOnly = Environment.GetEnvironmentVariable("LEXI_EXAM_PLANS_ONLY") == "1";
                if (!plansOnly)
                {
                Check(C<ComboBox>("IeltsKind").ItemCount == siblings.Count && C<ComboBox>("IeltsKind").SelectedItem?.ToString() == book.EnglishTitle,
                    book.BookId + " left selector displays its actual English book name");
                Check(C<ComboBox>("IeltsSection").IsVisible && C<ComboBox>("IeltsSection").ItemCount == book.Sections.Count
                    && ((List<LearningWord>)Call("IeltsFilteredWords")!).Select(w => w.Id).SequenceEqual(book.Sections[0].Entries.Select(w => w.Id))
                    && C<Button>("IeltsPracticeStartBtn").IsEffectivelyVisible,
                    book.BookId + " right selector loads only that book's units, including book index four");
                C<ComboBox>("IeltsSection").SelectedIndex = 1;
                Check(((List<LearningWord>)Call("IeltsFilteredWords")!).Select(w => w.Id).SequenceEqual(book.Sections[1].Entries.Select(w => w.Id)),
                    book.BookId + " unit changes load the matching book words");
                Click("LanguageToggleBtn");
                Check(C<ComboBox>("IeltsKind").SelectedItem?.ToString() == book.Title && C<ComboBox>("IeltsSection").SelectedItem?.ToString() == "第 02 单元",
                    book.BookId + " Chinese actual book name and unit remain selected");
                if (book.BookId == "KaoYan_3") await Snapshot("postgraduate-books-chinese");
                Click("LanguageToggleBtn");
                Check(C<ComboBox>("IeltsKind").SelectedIndex == bookIndex && C<ComboBox>("IeltsKind").SelectedItem?.ToString() == book.EnglishTitle
                    && C<ComboBox>("IeltsSection").SelectedItem?.ToString() == "Unit 02"
                    && !System.Text.RegularExpressions.Regex.IsMatch(book.EnglishTitle, "[\\u4e00-\\u9fff]"),
                    book.BookId + " English book/unit labels switch live without resetting the selection");
                if (book.BookId == "KaoYan_3") await Snapshot("postgraduate-books-english");
                }
                C<ComboBox>("IeltsSection").SelectedIndex = 0;
                Click("ExamPlanCreateBtn");
                Check(C<ComboBox>("PlanExamInput").ItemCount == 7
                    && C<ComboBox>("PlanExamInput").SelectedIndex == Array.IndexOf(topics, book.TopicId) + 1
                    && C<ComboBox>("PlanBookInput").ItemCount == siblings.Count
                    && C<ComboBox>("PlanBookInput").SelectedIndex == bookIndex
                    && Field<List<DailyStudyPlanWord>>("_planCandidates").Select(w => w.Id).SequenceEqual(book.Sections[0].Entries.Select(w => w.Id)),
                    book.BookId + " plan defaults to the selected book and unit");
                if (plansOnly)
                {
                    var nextTopic = topics[(Array.IndexOf(topics, book.TopicId) + 1) % topics.Length];
                    C<ComboBox>("PlanExamInput").SelectedIndex = Array.IndexOf(topics, nextTopic) + 1;
                    var nextBooks = books.Where(b => b.TopicId == nextTopic).ToList();
                    Check(C<ComboBox>("PlanBookInput").ItemCount == nextBooks.Count
                        && Field<List<DailyStudyPlanWord>>("_planCandidates").Select(w => w.Id).SequenceEqual(nextBooks[0].AllWords.Select(w => w.Id)),
                        book.BookId + " changing exam clears stale books and units");
                    C<ComboBox>("PlanExamInput").SelectedIndex = Array.IndexOf(topics, book.TopicId) + 1;
                    C<ComboBox>("PlanBookInput").SelectedIndex = bookIndex;
                    C<ComboBox>("PlanUnitInput").SelectedIndex = 2;
                    Check(Field<List<DailyStudyPlanWord>>("_planCandidates").Select(w => w.Id).SequenceEqual(book.Sections[1].Entries.Select(w => w.Id)),
                        book.BookId + " changing plan book and unit selects the matching words");
                    Click("LanguageToggleBtn");
                    Check(C<ComboBox>("PlanBookInput").SelectedItem?.ToString() == book.Title && C<ComboBox>("PlanUnitInput").SelectedItem?.ToString() == "第 02 单元",
                        book.BookId + " plan exam/book/unit selectors switch to Chinese without losing selection");
                    Click("LanguageToggleBtn");
                    Check(C<ComboBox>("PlanBookInput").SelectedItem?.ToString() == book.EnglishTitle && C<ComboBox>("PlanUnitInput").SelectedItem?.ToString() == "Unit 02"
                        && !System.Text.RegularExpressions.Regex.IsMatch(C<ComboBox>("PlanExamInput").SelectedItem!.ToString()!, "[\\u4e00-\\u9fff]"),
                        book.BookId + " plan exam/book/unit selectors are English in English UI");
                    if (book.BookId == "KaoYan_3") await Snapshot("postgraduate-plan-three-selectors");
                }
                C<ComboBox>("PlanUnitInput").SelectedIndex = 0;
                Check(Field<List<DailyStudyPlanWord>>("_planCandidates").Count == book.AllWords.Count(), book.BookId + " entire-book plan contains its complete vocabulary");
                C<ComboBox>("PlanUnitInput").SelectedIndex = 1;
                Click("PlanClearSelectionBtn"); C<TextBox>("PlanWordSearch").Text = first.Word;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                var matches = C<StackPanel>("PlanWordChoices").Children.OfType<CheckBox>().Where(c => (c.Content as TextBlock)?.Text?.StartsWith(first.Word + "  · ", StringComparison.Ordinal) == true).ToList();
                Check(matches.Count == 1, book.BookId + " exact checkbox available: query=" + C<TextBox>("PlanWordSearch").Text
                    + "; first=" + first.Word + "; unit=" + C<ComboBox>("PlanUnitInput").SelectedIndex
                    + "; candidates=" + string.Join(",", Field<List<DailyStudyPlanWord>>("_planCandidates").Take(3).Select(w => w.Word))
                    + "; rows=" + string.Join(" | ", C<StackPanel>("PlanWordChoices").Children.OfType<CheckBox>().Take(3).Select(c => (c.Content as TextBlock)?.Text)));
                var choice = matches.Single();
                choice.IsChecked = true; C<TextBox>("PlanNameInput").Text = "Book fixture " + book.BookId;
                Click("PlanCreateConfirmBtn");
                if (C<StackPanel>("PlanOverlapActions").IsVisible)
                {
                    Check(!string.IsNullOrWhiteSpace(C<TextBlock>("PlanDialogError").Text), book.BookId + " retains the existing overlap confirmation for repeated word forms");
                    Click("PlanContinueOverlapBtn");
                }
                var stored = new DailyStudyPlanStore(Path.Combine(folder, "daily-study-plans.json")).Load().Single(p => p.Name == "Book fixture " + book.BookId);
                Check(stored.Words.Count == 1 && stored.Words[0].Id == first.Id, book.BookId + " saved plan freezes the correct original word ID");
                if (plansOnly) continue;
                C<NumericUpDown>("IeltsCount").Value = 1; C<CheckBox>("IeltsAll").IsChecked = false; C<ComboBox>("IeltsOrder").SelectedIndex = 0;
                C<ComboBox>("IeltsPracticeMode").SelectedIndex = 1; Click("IeltsPracticeStartBtn");
                Check(Field<TypingSession>("_typingSession").Current?.Id == first.Id && Field<TypingSession>("_typingSession").Hints,
                    book.BookId + " hinted spelling uses the selected book identity");
                Call("StopTypingRound"); Click("NavExam_" + book.TopicId);
                C<ComboBox>("IeltsPracticeMode").SelectedIndex = 2; Click("IeltsPracticeStartBtn");
                Check(Field<TypingSession>("_typingSession").Current?.Id == first.Id && !Field<TypingSession>("_typingSession").Hints,
                    book.BookId + " dictation uses the same selected book identity");
                Call("StopTypingRound"); Click("NavExam_" + book.TopicId);
                await Invoke("StartIeltsCardsAsync", new List<LearningWord> { first });
                var keyArgs = new object[] { first.Word, default(WordKey) };
                Check(Field<bool>("_wordFocusActive") && (bool)Call("MemoryTryFocusKey", keyArgs)!
                    && (WordKey)keyArgs[1] == WordKeyResolver.FromIelts(first)
                    && Field<string>("_memoryRecoveryScope") == "Ielts::exam:" + book.Id,
                    book.BookId + " recognition and unfinished-round scope are isolated by book");
                Call("ExitWordFocus");
                // The due-card fixture starts a separate completed session; an unfinished focus
                // checkpoint intentionally hides its words from today's review until it is finished.
                Sql("DELETE FROM memory_session_checkpoints WHERE surface='focus'");
                // A real due card must retain book-specific content through the old early-cache path.
                var entry = book.AllWords.First(w => w.Example.Length > 0 && w.Extra.Length > 0 && w.Phonetic.Length > 0);
                var key = WordKeyResolver.FromIelts(entry);
                var memory = Field<LearningMemoryCoordinator>("_memory");
                Sql("UPDATE fsrs_cards SET next_review_at_utc='2999-01-01T00:00:00.0000000Z'");
                var shown = "multibook-" + book.BookId;
                memory.BeginSession(StudyMode.FirstLearn, WordSource.Ielts, "", 1);
                memory.OnPresented(key, shown, isRecall: true); memory.OnRated(key, shown, StudyRating.Known, 0, 1, null);
                Check(memory.CommitWord(key, StudyMode.FirstLearn) != null, book.BookId + " creates a canonical on its stable source identity");
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "UPDATE fsrs_cards SET next_review_at_utc='2000-01-01T00:00:00.0000000Z' WHERE word_key=$key";
                    command.Parameters.AddWithValue("$key", key.Key); command.ExecuteNonQuery();
                }
                Field<Dictionary<long, WordKey>>("_memorySourceReviewKeys").Clear(); Field<Dictionary<string, WordItem>>("_memorySourceReviewWords").Clear();
                Set("_ieltsCatalog", null); Call("UpdateReviewBadge"); Set("_ieltsCatalog", catalog);
                Click("NavExamsBack"); Click("NavReview");
                Check(C<TextBlock>("ReviewWordPreview").Text == entry.Word, book.BookId + " today's review preview repairs cached internal IDs");
                Click("ReviewStartAllBtn");
                Check(C<TextBlock>("ReviewWordText").Text == entry.Word && Field<StudyRound<WordItem>>("_reviewRound").Current.Phonetic == entry.Phonetic
                    && Field<StudyRound<WordItem>>("_reviewRound").Current.Translation == entry.Meaning,
                    book.BookId + " due card displays real word, phonetics and meaning");
                await Invoke("OnReviewRatingAsync", StudyRating.Known);
                Check(C<StackPanel>("ReviewDetails").GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == entry.Example),
                    book.BookId + " due review renders its own examples rather than another book's content");
                Check(archive.GetAllWords().Count == archiveCount, book.BookId + " review does not change the personal archive");
                Call("ShowPage", "lookup");
            }
            Click("NavIelts");
            if (Environment.GetEnvironmentVariable("LEXI_EXAM_PLANS_ONLY") == "1")
            {
                Click("ExamPlanCreateBtn");
                Check(C<ComboBox>("PlanExamInput").SelectedIndex == 0 && C<ComboBox>("PlanBookInput").ItemCount == 4,
                    "IELTS plan retains its original four books under the IELTS exam selector");
                Click("PlanCancelBtn");
            }
            Check(C<ComboBox>("IeltsKind").ItemCount == 6 && C<ComboBox>("IeltsSection").ItemCount == 22, "IELTS keeps its original categories and chapters");
            File.WriteAllText(Path.Combine(folder, "exam-books-complete.txt"), Environment.GetEnvironmentVariable("LEXI_EXAM_BOOKS_SMOKE") == "1" ? "PASS packaged book smoke\n" : "PASS all twenty books\n");
            Console.WriteLine($"PASS scoped exam acceptance: {report.Count} assertions, {testedBooks.Count} books");
        }
        catch (Exception ex)
        {
            code = 1; File.WriteAllText(Path.Combine(folder, "exam-books-error.txt"), ex.ToString()); Console.Error.WriteLine(ex);
        }
        finally
        {
            ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown(code);
        }
    }
}
