using Lexi;
using System.Text.Json;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
var date = new DateOnly(2026, 10, 10);
DailyStudyPlan Plan(int count = 4, int quota = 2) => DailyStudyPlanRules.Create("test", DailyStudyPlanSource.Ielts, "source", Enumerable.Range(1, count).Select(i => new DailyStudyPlanWord { Id = "w" + i, Word = "word" + i }).ToList(), quota, false, 7);
void Finish(DailyStudyPlanSession session) { while (session.Round.HasCurrent) { if (session.Round.CurrentStep == StudyStep.Learn) session.CompleteLearn(); else Check(session.Rate(StudyRating.Known, () => true) is not null, "rating persisted"); } }
var plan = Plan(); var first = new DailyStudyPlanSession(plan, date); Finish(first);
Check(plan.CompletedWordIds.Count == 2 && plan.CurrentBatchWordIds.Count == 2, "completed first batch remains frozen");
DailyStudyPlanSession Review(DailyStudyPlan p) => new(p, date, mode: StudyMode.Review);
Check(DailyStudyPlanRules.GetTodayBatch(plan, date).Count == 2 && DailyStudyPlanRules.TodayCompleted(plan, date) == 2, "completed today denominator stays fixed");
var review = Review(plan);
Check(review.Round.Mode == StudyMode.Review && review.Round.Total == 2 && review.Round.CurrentStep == StudyStep.Recall && review.Round.CurrentTarget == 1, "completed today batch can enter real Review recall using current thresholds");
var before = JsonSerializer.Serialize(plan); Finish(review);
Check(plan.CompletedWordIds.Count == 2 && plan.CurrentBatchWordIds.Count == 2 && plan.LastBatchCompletedDate == date, "review never resets first learning progress or quota");
Finish(Review(plan));
Check(plan.CompletedWordIds.Count == 2, "same day repeat review keeps first learning count");
var undo = Review(plan); var word = undo.Round.Current;
Check(undo.Rate(StudyRating.Known, () => false) is null && undo.Round.Current == word && undo.Round.Completed == 0, "failed review save restores queue");
Check(undo.Rate(StudyRating.Known, () => true) is { Completed: true }, "review first known completes");
Check(undo.ReclassifyAsForgot(() => true) is { Completed: false }, "review reclassification returns card to review queue");
var revised = JsonSerializer.Deserialize<StudyRound<string>.PersistedRound>(undo.Round.CaptureJson(id => id))!;
Check(revised.Current.Concat(revised.Next).All(c => c.Step == StudyStep.Recall) && revised.States.Single(w => w.WordId == word).FirstRated, "review reclassification keeps Recall and existing later threshold");
Check(undo.Undo(() => true) && undo.Round.Current == word && undo.Round.CurrentTarget == 1 && plan.CompletedWordIds.Count == 2, "undo restores review and leaves first learning completed");
var failedMemory = Review(plan); var id = failedMemory.Round.Current;
failedMemory.Rate(StudyRating.Known, () => true);
var checkpoint = failedMemory.Round.CaptureJson(w => w); var planSnapshot = JsonSerializer.Serialize(plan);
try { failedMemory.Undo(() => true, _ => throw new IOException("memory unavailable")); throw new Exception("Expected memory failure"); }
catch (IOException) { }
Check(failedMemory.Round.CaptureJson(w => w) == checkpoint && JsonSerializer.Serialize(plan) == planSnapshot && failedMemory.CanUndo, "memory undo failure restores queue, plan and undo eligibility");
try { failedMemory.ReclassifyAsForgot(() => true, _ => throw new IOException("memory unavailable")); throw new Exception("Expected memory failure"); }
catch (IOException) { }
Check(failedMemory.Round.CaptureJson(w => w) == checkpoint && JsonSerializer.Serialize(plan) == planSnapshot, "memory revision failure restores queue and first learning state");
var dated = Plan(1, 1); var learning = new DailyStudyPlanSession(dated, date); learning.CompleteLearn();
Check(learning.Rate(StudyRating.Known, () => false) is null && dated.LearningDates.Count == 0, "failed response does not fabricate learning date");
learning.Rate(StudyRating.Known, () => true); learning.Rate(StudyRating.Known, () => true);
Check(dated.LearningDates.SetEquals([date]), "real unfinished learning responses record their actual date");
Check(learning.Rate(StudyRating.Known, () => false) is null && dated.CompletedWordIds.Count == 0, "failed completion restores first learning progress");
Check(learning.Rate(StudyRating.Known, () => true) is { Completed: true } && dated.LearningDates.SetEquals([date]), "only actual completed learning records its date");
Check(learning.Undo(() => true) && dated.LearningDates.SetEquals([date]), "undo keeps dates supported by earlier real responses");
var legacy = Plan(); legacy.CompletedWordIds.Add("w1");
var encoded = JsonSerializer.Serialize(legacy);
var restored = JsonSerializer.Deserialize<DailyStudyPlan>(encoded)!;
Check(restored.LearningDates.Count == 0 && restored.CurrentBatchDate is null, "legacy missing dates remain unknown");
var adjusted = DailyStudyPlanRules.Adjust(plan, "adjusted", 3, true, 23);
Check(DailyStudyPlanRules.GetTodayBatch(adjusted, date).Count == 2 && adjusted.LearningDates.SetEquals(plan.LearningDates) && adjusted.Activities.Count == plan.Activities.Count, "adjustment preserves frozen batch and real history");
var tomorrow = date.AddDays(1);
Check(DailyStudyPlanRules.GetTodayBatch(plan, tomorrow).Count == 2 && DailyStudyPlanRules.TodayCompleted(plan, tomorrow) == 0, "next day shows new quota rather than yesterday completed batch");
var subset = new DailyStudyPlanSession(plan, date, mode: StudyMode.Review, reviewWords: [plan.Words[0]]);
Check(subset.Round.Total == 1 && subset.BatchWords.Count == 1 && subset.Round.CurrentStep == StudyStep.Recall, "review supports explicit subset batches");
var revisionDates = JsonSerializer.Deserialize<DailyStudyPlan>(JsonSerializer.Serialize(plan))!;
var newDate = date.AddDays(2);
var datedReview = new DailyStudyPlanSession(revisionDates, newDate, mode: StudyMode.Review, reviewWords: [revisionDates.Words[0]]);
datedReview.Rate(StudyRating.Known, () => true); datedReview.ReclassifyAsForgot(() => true);
Check(revisionDates.LearningDates.Contains(newDate), "reclassified actual review keeps its real study date");
Check(datedReview.Undo(() => true) && !revisionDates.LearningDates.Contains(newDate), "undo of only effective review response removes its date");
var directory = Path.Combine(Path.GetTempPath(), "lexi-plan-dashboard-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new DailyStudyPlanStore(Path.Combine(directory, "plans.json")); store.Save([plan]);
    var loaded = store.Load().Single();
    Check(loaded.CurrentBatchDate == date && loaded.LearningDates.SetEquals([date]) && loaded.Activities.Count == plan.Activities.Count, "incremental history survives actual plan store round trip");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
Console.WriteLine("PlanDashboardTests passed.");
