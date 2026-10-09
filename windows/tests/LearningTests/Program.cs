using Lexi;
using System.Text.Json;

void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
LearningWord W(string text) => new() { Id = text, Words = [text] };
DailyStudyPlanWord PW(string id) => new() { Id = id, Word = id, Meaning = "meaning " + id };
List<string> Deck(int n) => Enumerable.Range(1, n).Select(i => "w" + i).ToList();
List<LearningWord> SelectRound(int count, bool all, bool random, int seed) =>
    LearningRound.Select(new List<LearningWord> { W("apple"), new() { Id = "colour", Words = ["colour", "color"] }, W("ice cream") }, count, all, random, seed);

// ---- TypingFeedbackModel：淡写逐字母显示、默写不泄露答案 ----
var hintedPrefix = TypingFeedbackModel.Build("apple", "ap", true, TypingOutcome.Pending);
Check(new string(hintedPrefix.Select(letter => letter.Character).ToArray()) == "apple" &&
    hintedPrefix.Select(letter => letter.Tone).SequenceEqual([TypingLetterTone.Correct, TypingLetterTone.Correct, TypingLetterTone.Hint, TypingLetterTone.Hint, TypingLetterTone.Hint]),
    "淡写逐字母显示正确前缀与未输入字母");
var hintedError = TypingFeedbackModel.Build("apple", "ax", true, TypingOutcome.Retry);
Check(new string(hintedError.Select(letter => letter.Character).ToArray()) == "axple" &&
    hintedError.Select(letter => letter.Tone).SequenceEqual([TypingLetterTone.Correct, TypingLetterTone.Wrong, TypingLetterTone.Hint, TypingLetterTone.Hint, TypingLetterTone.Hint]),
    "淡写错误反馈标出错字并保留后续提示");
var dictationPrefix = TypingFeedbackModel.Build("apple", "ax", false, TypingOutcome.Pending);
Check(new string(dictationPrefix.Select(letter => letter.Character).ToArray()) == "ax" &&
    dictationPrefix.Select(letter => letter.Tone).SequenceEqual([TypingLetterTone.Correct, TypingLetterTone.Wrong]),
    "默写逐字母反馈已输入内容且不显示答案");
var dictationError = TypingFeedbackModel.Build("apple", "axple", false, TypingOutcome.Retry);
Check(new string(dictationError.Select(letter => letter.Character).ToArray()) == "axple" &&
    dictationError.Select(letter => letter.Tone).SequenceEqual([TypingLetterTone.Correct, TypingLetterTone.Wrong, TypingLetterTone.Correct, TypingLetterTone.Correct, TypingLetterTone.Correct]),
    "默写错误反馈逐字母标记正确与错误且不泄露答案");

// ---- TypingSession：完整拼写才通过，错误位置明确 ----
var session = new TypingSession();
session.Reset([W("apple"), W("ice cream")], true);
Check(!session.Advance(), "不能未答先推进");
Check(session.Submit("a") == TypingOutcome.Pending, "淡写正确前缀");
Check(session.Submit("ax") == TypingOutcome.Retry && session.Input == "" && session.Cursor == 0 && session.ErrorPositions.SequenceEqual([1]), "淡写中途错误立即清空，保留位置");
Check(session.Submit("apple") == TypingOutcome.Correct && session.Advance() && session.Current!.Word == "ice cream", "正确后才能进入下一词");
Check(session.Submit("ICE CREAM") == TypingOutcome.Correct && session.Advance() && session.Outcome == TypingOutcome.Finished, "大小写与Unicode空格、短语、完成");
Check(session.Retries == 1 && session.ErrorIds.Contains("apple") && session.Accuracy < 100, "重试与错词统计");
session.Reset([W("apple")], false);
Check(session.Submit("ax") == TypingOutcome.Pending && session.ErrorPositions.Count == 0 && session.Accuracy == 100, "无提示中途不泄露正误");
Check(session.Submit("axple") == TypingOutcome.Retry && session.Input == "" && session.Cursor == 0, "无提示满长度才重置");
Check(session.Submit("APPLe") == TypingOutcome.Correct && session.Advance(), "无提示正确重练完成");
session.Reset([W("well-being")], false);
Check(session.Submit("well") == TypingOutcome.Pending && session.Submit("wel") == TypingOutcome.Pending, "允许退格编辑");
Check(session.Submit("well-beingX") == TypingOutcome.Retry, "超长不绕过检查");
session.Reset([], true); Check(session.Outcome == TypingOutcome.Finished, "空词库安全完成");

// ---- LearningRound：三种模式共用选词算法 ----
Check(SelectRound(2, false, false, 31).Select(w => w.Word).SequenceEqual(["apple", "colour"]), "自定义词数且按部分顺序学习");
Check(SelectRound(2, true, false, 31).Select(w => w.Word).SequenceEqual(["apple", "colour", "color", "ice cream"]), "全部包含每个拼写变体");
Check(SelectRound(3, false, true, 31).Select(w => w.Word).SequenceEqual(SelectRound(3, false, true, 31).Select(w => w.Word)), "随机种子可复现");
Check(SelectRound(999, false, true, 31).Count == 4 && SelectRound(0, false, false, 31).Count == 1, "词数不越界或意外为空");

// ---- StudyRound：首次学习 3 次「认识」；复习首次认识即过；模糊/忘记重排；撤销 ----
var reviewFirst = new StudyRound<string>(new Random(5));
reviewFirst.Reset(["a", "b"], StudyMode.Review);
Check(reviewFirst.Total == 2 && reviewFirst.CurrentStep == StudyStep.Recall && reviewFirst.CurrentTarget == 1,
    "复习卡首次评价为认识即可完成");
Check(reviewFirst.Commit(StudyRating.Known).Completed && reviewFirst.Completed == 1 && reviewFirst.Known == 1,
    "复习第一次认识直接出队");
Check(reviewFirst.Current == "b" && reviewFirst.Remaining == 1, "出队后进入下一个到期词");

var reviewRetry = new StudyRound<string>(new Random(5));
reviewRetry.Reset(["a"], StudyMode.Review);
Check(!reviewRetry.Commit(StudyRating.Unsure).Completed && reviewRetry.CurrentTarget == 3 && reviewRetry.CurrentStreak == 0,
    "复习首评模糊后转为需要 3 次认识");
Check(reviewRetry.Current == "a" && reviewRetry.Remaining == 1 && reviewRetry.CurrentStep == StudyStep.Recall,
    "模糊的复习词留在本轮继续回忆");
Check(!reviewRetry.Commit(StudyRating.Known).Completed && reviewRetry.CurrentStreak == 1, "第 1/3 次认识不出队");
Check(!reviewRetry.Commit(StudyRating.Known).Completed && reviewRetry.CurrentStreak == 2, "第 2/3 次认识不出队");
var third = reviewRetry.Commit(StudyRating.Known);
Check(third.Completed && third.Streak == 3 && reviewRetry.IsFinished && reviewRetry.Known == 3,
    "第 3 次认识完成本轮并出队");

var reviewReset = new StudyRound<string>(new Random(9));
reviewReset.Reset(["z"], StudyMode.Review);
reviewReset.Commit(StudyRating.Unsure);
reviewReset.Commit(StudyRating.Known);
Check(reviewReset.CurrentStreak == 1 && !reviewReset.Commit(StudyRating.Known).Completed && reviewReset.CurrentStreak == 2,
    "清零后连续认识累计到 2 仍不出队");
reviewReset.Commit(StudyRating.Forgot);
Check(reviewReset.CurrentStreak == 0 && reviewReset.Forgot == 1 && !reviewReset.IsFinished, "忘记把连击清零");
Check(!reviewReset.Commit(StudyRating.Known).Completed && reviewReset.CurrentStreak == 1, "清零后从 1 重新累计");
reviewReset.Commit(StudyRating.Unsure);
Check(reviewReset.CurrentStreak == 0, "模糊把连击减一且不会低于 0");

var learn = new StudyRound<string>(new Random(3));
learn.Reset(["x"], StudyMode.FirstLearn);
Check(learn.Mode == StudyMode.FirstLearn && learn.CurrentStep == StudyStep.Learn && learn.CurrentTarget == 3,
    "首次学习从学习卡开始且需 3 次认识");
learn.CompleteLearn();
Check(learn.CurrentStep == StudyStep.Recall && learn.Current == "x", "学完后进入回忆卡");
Check(!learn.Commit(StudyRating.Known).Completed && learn.CurrentStreak == 1, "首学第 1 次认识不出队");
Check(!learn.Commit(StudyRating.Known).Completed && learn.CurrentStreak == 2, "首学第 2 次认识不出队");
Check(learn.Commit(StudyRating.Known).Completed && learn.IsFinished && learn.Known == 3, "首学第 3 次认识完成");

var learnBack = new StudyRound<string>(new Random(3));
learnBack.Reset(["x", "y"], StudyMode.FirstLearn);
learnBack.CompleteLearn();
learnBack.CompleteLearn();
learnBack.Commit(StudyRating.Known);
var forgotten = learnBack.Current;
learnBack.Commit(StudyRating.Forgot);
Check(learnBack.Forgot == 1 && !learnBack.IsFinished, "首学忘记留在本轮");
var learnAgain = false;
for (var guard = 0; learnBack.HasCurrent && guard < 20; guard++)
{
    if (learnBack.Current == forgotten)
    {
        learnAgain = learnBack.CurrentStep == StudyStep.Learn && learnBack.CurrentStreak == 0 && learnBack.CurrentTarget == 3;
        break;
    }
    if (learnBack.CurrentStep == StudyStep.Learn) learnBack.CompleteLearn();
    else learnBack.Commit(StudyRating.Known);
}
Check(learnAgain, "首学忘记的词以学习卡回到本轮并清零连击");

var order = new StudyRound<string>(new Random(17));
order.Reset(Deck(6), StudyMode.FirstLearn);
var pass1 = new List<string>();
while (order.CurrentStep == StudyStep.Learn) { pass1.Add(order.Current); order.CompleteLearn(); }
var pass2 = new List<string>();
while (pass2.Count < 6) { pass2.Add(order.Current); order.Commit(StudyRating.Unsure); }
Check(pass1.Count == 6 && pass1.Distinct().Count() == 6, "学习遍每个词只出现一次");
Check(!pass1.SequenceEqual(Deck(6)), "学习遍在轮内打乱顺序");
Check(pass2.Count == 6 && pass2.Distinct().Count() == 6, "回忆遍每个词只出现一次");
Check(!pass2.SequenceEqual(pass1), "下一遍重新打乱顺序");
Check(pass2[0] != pass1[^1], "两遍衔接处不是同一个词");
Check(order.Unsure == 6 && order.Completed == 0 && order.Remaining == 6, "模糊的词全部留在本轮");

var reviewOrder = new StudyRound<string>(new Random(29));
reviewOrder.Reset(Deck(5), StudyMode.Review);
var reviewPass = new List<string>();
for (var i = 0; i < 5; i++) { reviewPass.Add(reviewOrder.Current); reviewOrder.Commit(StudyRating.Unsure); }
Check(reviewPass.Distinct().Count() == 5 && !reviewPass.SequenceEqual(Deck(5)), "复习遍同样每条一遍且打乱顺序");

var undoRun = new StudyRound<string>(new Random(4));
undoRun.Reset(["u"], StudyMode.Review);
undoRun.Commit(StudyRating.Unsure);
Check(!undoRun.Commit(StudyRating.Known).Completed && undoRun.CurrentStreak == 1, "累计一次认识");
Check(undoRun.UndoLast()!.Value.Word == "u" && undoRun.Known == 0 && undoRun.CurrentStreak == 0 && !undoRun.CanUndo,
    "撤销回退连击计数并把词放回队首");
Check(!undoRun.Commit(StudyRating.Known).Completed && undoRun.CurrentStreak == 1, "撤销后重新评价仍然有效");

var undoDone = new StudyRound<string>(new Random(4));
undoDone.Reset(["u"], StudyMode.Review);
Check(undoDone.Commit(StudyRating.Known).Completed && undoDone.IsFinished, "复习首次认识完成本轮");
undoDone.UndoLast();
Check(!undoDone.IsFinished && undoDone.Completed == 0 && undoDone.Current == "u" && undoDone.CurrentStreak == 0,
    "撤销完成后回到队首并回退统计");

var mixed = new StudyRound<string>(new Random(6));
mixed.Reset(["old", "new"], word => word == "new");
Check(mixed.Current == "old" && mixed.CurrentStep == StudyStep.Recall && mixed.CurrentTarget == 1,
    "已有档案的词直接回忆且首次认识即过");
Check(mixed.Commit(StudyRating.Known).Completed, "旧词首次认识完成本轮");
Check(mixed.Current == "new" && mixed.CurrentStep == StudyStep.Learn && mixed.CurrentTarget == 3,
    "新词在学习卡等待且需要 3 次认识");

var drop = new StudyRound<string>(new Random(2));
drop.Reset(["only"], StudyMode.Review);
drop.CompleteCurrent();
Check(drop.IsFinished && drop.Known == 1 && drop.UndoLast()!.Value.Rating == StudyRating.Known && drop.Current == "only",
    "标记已掌握直接完成且可撤销");

// ---- DailyStudyPlan：创建 / 今日批次 / 完成 / 停止恢复 / JSON 原子保存 ----
var monday = new DateOnly(2026, 10, 5);
var sourceWords = new List<DailyStudyPlanWord> { PW("a"), PW("b"), PW("c"), PW("d"), PW("e") };
var daily = DailyStudyPlanRules.Create("Daily", DailyStudyPlanSource.Archive, "Archive", sourceWords, 2, false, 7);
Check(daily.Words.Select(w => w.Id).SequenceEqual(["a", "b", "c", "d", "e"]) &&
    DailyStudyPlanRules.EstimatedDaysRemaining(daily) == 3, "计划冻结输入顺序，剩余天数向上取整");
sourceWords[0].Word = "changed";
sourceWords.Add(PW("f"));
Check(daily.Words.Count == 5 && daily.Words[0].Word == "a", "新归档词与源词修改不改变已有计划");
Check(DailyStudyPlanRules.GetTodayWords(daily, monday).Select(w => w.Id).SequenceEqual(["a", "b"]), "每日批次限制为设定词数");
Check(DailyStudyPlanRules.CompleteWord(daily, "a", monday) &&
    DailyStudyPlanRules.GetTodayWords(daily, monday).Select(w => w.Id).SequenceEqual(["b"]), "中断后继续当天未完成的批次");
Check(DailyStudyPlanRules.EstimatedDaysRemaining(daily) == 3, "部分完成的批次仍计入剩余天数");
Check(!DailyStudyPlanRules.CompleteWord(daily, "c", monday) &&
    !DailyStudyPlanRules.CompleteWord(daily, "a", monday), "不能越过批次或重复完成词条");
Check(DailyStudyPlanRules.CompleteWord(daily, "b", monday) &&
    DailyStudyPlanRules.GetTodayWords(daily, monday).Count == 0 &&
    DailyStudyPlanRules.EstimatedDaysRemaining(daily) == 2, "完成当天批次后当天不开放下一批");
Check(DailyStudyPlanRules.GetTodayWords(daily, monday.AddDays(1)).Select(w => w.Id).SequenceEqual(["c", "d"]), "次日开放下一批");
var resumed = DailyStudyPlanRules.Create("Resumed", DailyStudyPlanSource.Ielts, "IELTS", [PW("i1"), PW("i2"), PW("i3")], 2, false, 1);
Check(DailyStudyPlanRules.CompleteWord(resumed, "i1", monday) &&
    DailyStudyPlanRules.GetTodayWords(resumed, monday.AddDays(2)).Select(w => w.Id).SequenceEqual(["i2"]), "跨日中断仍继续原批次");
var shuffledA = DailyStudyPlanRules.Create("Shuffle", DailyStudyPlanSource.Ielts, "IELTS", sourceWords, 3, true, 31);
var shuffledB = DailyStudyPlanRules.Create("Shuffle", DailyStudyPlanSource.Ielts, "IELTS", sourceWords, 3, true, 31);
Check(shuffledA.Words.Select(w => w.Id).SequenceEqual(shuffledB.Words.Select(w => w.Id)) &&
    shuffledA.Words.Select(w => w.Id).Order().SequenceEqual(sourceWords.Select(w => w.Id).Order()) &&
    !shuffledA.Words.Select(w => w.Id).SequenceEqual(sourceWords.Select(w => w.Id)), "随机顺序按种子复现且不丢词");
var archiveOverlap = DailyStudyPlanRules.Create("Archive plan", DailyStudyPlanSource.Archive, "A", [PW("shared")], 1, false, 0);
var ieltsOverlap = DailyStudyPlanRules.Create("IELTS plan", DailyStudyPlanSource.Ielts, "I", [PW("shared")], 1, false, 0);
var archiveDraft = DailyStudyPlanRules.Create("Draft", DailyStudyPlanSource.Archive, "A", [PW("shared")], 1, false, 0);
Check(DailyStudyPlanRules.FindOverlaps([archiveOverlap, ieltsOverlap], archiveDraft)
    .Select(x => x.PlanName).SequenceEqual(["Archive plan"]), "重叠提示按来源隔离");
var sameWordDifferentId = PW("different-id");
sameWordDifferentId.Word = "  SHARED  ";
var ieltsDraft = DailyStudyPlanRules.Create("IELTS draft", DailyStudyPlanSource.Ielts, "I", [sameWordDifferentId], 1, false, 0);
Check(DailyStudyPlanRules.FindOverlaps([archiveOverlap, ieltsOverlap], ieltsDraft)
    .Select(x => x.PlanName).SequenceEqual(["IELTS plan"]), "同来源同词异 ID 仍提示重叠，忽略大小写与首尾空格");
DailyStudyPlanRules.Stop(archiveOverlap);
Check(archiveOverlap.Status == DailyStudyPlanStatus.Stopped &&
    DailyStudyPlanRules.GetTodayWords(archiveOverlap, monday).Count == 0 &&
    DailyStudyPlanRules.FindOverlaps([archiveOverlap, ieltsOverlap], archiveDraft).Count == 0,
    "停止计划不出词且不占用词条");
Check(!DailyStudyPlanRules.CompleteWord(archiveOverlap, "shared", monday), "停止计划不能继续完成词条");
Check(DailyStudyPlanRules.CompleteWord(ieltsOverlap, "shared", monday) &&
    ieltsOverlap.Status == DailyStudyPlanStatus.Completed &&
    DailyStudyPlanRules.GetTodayWords(ieltsOverlap, monday.AddDays(1)).Count == 0 &&
    DailyStudyPlanRules.EstimatedDaysRemaining(ieltsOverlap) == 0,
    "完成最后一词自动结束计划");
Check(DailyStudyPlanRules.FindOverlaps([ieltsOverlap],
    DailyStudyPlanRules.Create("I draft", DailyStudyPlanSource.Ielts, "I", [PW("shared")], 1, false, 0)).Count == 0,
    "已完成计划不占用词条");
void RejectPlan(Action action, string label)
{
    try { action(); throw new Exception("FAIL: " + label); }
    catch (ArgumentException) { Console.WriteLine("PASS: " + label); }
}
RejectPlan(() => DailyStudyPlanRules.Create(" ", DailyStudyPlanSource.Archive, "A", [PW("a")], 1, false, 0), "计划名不可为空");
RejectPlan(() => DailyStudyPlanRules.Create("X", DailyStudyPlanSource.Archive, "A", [], 1, false, 0), "计划词表不可为空");
RejectPlan(() => DailyStudyPlanRules.Create("X", DailyStudyPlanSource.Archive, "A", [PW("a")], 0, false, 0), "每日词数必须为正");
RejectPlan(() => DailyStudyPlanRules.Create("X", DailyStudyPlanSource.Archive, "A", [PW("a"), PW("a")], 1, false, 0), "词条 ID 必须唯一");
var planPath = Path.Combine(Path.GetTempPath(), "lexi-daily-plan-test-" + Guid.NewGuid().ToString("N"), "daily-plans.json");
try
{
    var planStore = new DailyStudyPlanStore(planPath);
    Check(planStore.Load().Count == 0, "独立计划文件不存在时返回空列表");
    planStore.Save([daily, resumed]);
    var loaded = planStore.Load();
    Check(loaded.Count == 2 && loaded[0].Words.Select(w => w.Id).SequenceEqual(["a", "b", "c", "d", "e"]) &&
        loaded[0].CompletedWordIds.SetEquals(["a", "b"]) && loaded[0].LastBatchCompletedDate == monday &&
        loaded[1].CompletedWordIds.SetEquals(["i1"]), "独立 JSON 保存并恢复批次和完成状态");
    planStore.Save([loaded[0]]);
    Check(planStore.Load().Count == 1, "原子替换已有计划文件");
    Check(Directory.GetFiles(Path.GetDirectoryName(planPath)!, "*.tmp").Length == 0, "保存后无临时文件残留");
    File.WriteAllText(planPath, "[{\"Name\":\"broken\",\"Words\":[]}]");
    try { planStore.Load(); throw new Exception("FAIL: 损坏计划结构必须拒绝"); }
    catch (InvalidDataException) { Console.WriteLine("PASS: 损坏计划结构必须拒绝"); }
}
finally { Directory.Delete(Path.GetDirectoryName(planPath)!, recursive: true); }

var sequential = new StudyRound<string>(new Random(17));
sequential.Reset(Deck(6), StudyMode.FirstLearn, shuffle: false);
var sequentialLearn = new List<string>();
for (var i = 0; i < 6; i++) { sequentialLearn.Add(sequential.Current); sequential.CompleteLearn(); }
Check(sequentialLearn.SequenceEqual(Deck(6)), "顺序计划的学习遍保留原顺序");
var sequentialRecall = new List<string>();
for (var i = 0; i < 6; i++) { sequentialRecall.Add(sequential.Current); sequential.Commit(StudyRating.Unsure); }
Check(sequentialRecall.SequenceEqual(Deck(6)) && sequential.Current == "w1", "顺序计划的回忆遍保留原顺序");

var planSessionPlan = DailyStudyPlanRules.Create("Cards", DailyStudyPlanSource.Archive, "A", [PW("p1"), PW("p2"), PW("p3")], 2, false, 0);
var planSession = new DailyStudyPlanSession(planSessionPlan, monday);
Check(planSession.Round.CurrentStep == StudyStep.Learn && planSessionPlan.CurrentBatchWordIds.SequenceEqual(["p1", "p2"]), "计划使用独立首学卡片并冻结当天完整批次");
planSession.CompleteLearn(); planSession.CompleteLearn();
Check(planSession.Rate(StudyRating.Forgot, () => true) is { Completed: false } && planSessionPlan.ForgotWordIds.Contains("p1"), "仅忘记评价记入独立计划忘记集合");
for (var guard = 0; guard < 30 && !planSessionPlan.CompletedWordIds.Contains("p1"); guard++)
{
    if (planSession.Round.CurrentStep == StudyStep.Learn) planSession.CompleteLearn();
    else planSession.Rate(StudyRating.Known, () => true);
}
Check(planSessionPlan.CompletedWordIds.Contains("p1"), "计划仅在连击通过后保存完成词条");

var partialPlan = DailyStudyPlanRules.Create("Partial", DailyStudyPlanSource.Archive, "A", [PW("p1"), PW("p2"), PW("p3")], 2, false, 0);
new DailyStudyPlanSession(partialPlan, monday);
DailyStudyPlanRules.CompleteWord(partialPlan, "p1", monday);
var resumedSession = new DailyStudyPlanSession(partialPlan, monday.AddDays(1));
Check(partialPlan.CurrentBatchWordIds.SequenceEqual(["p1", "p2"]) && resumedSession.BatchWords.Select(w => w.Id).SequenceEqual(["p1", "p2"]), "中断恢复后拼写范围仍是完整原批次");

var saveFailurePlan = DailyStudyPlanRules.Create("Failure", DailyStudyPlanSource.Ielts, "I", [PW("f1"), PW("f2")], 2, false, 0);
var saveFailureSession = new DailyStudyPlanSession(saveFailurePlan, monday);
saveFailureSession.CompleteLearn(); saveFailureSession.CompleteLearn();
Check(saveFailureSession.Rate(StudyRating.Forgot, () => false) == null && saveFailureSession.Round.Current == "f1" && saveFailureSession.Round.Forgot == 0 && saveFailurePlan.ForgotWordIds.Count == 0, "保存失败恢复队列、评价统计和忘记集合");
Check(saveFailureSession.Rate(StudyRating.Known, () => true) is { Completed: false } && saveFailurePlan.CompletedWordIds.Count == 0, "首次认识不能提前完成计划词条");
Check(saveFailureSession.Undo(() => true) && saveFailureSession.Round.Completed == 0 && saveFailureSession.Round.Current == "f1", "计划撤销仅恢复独立进度与队列");
for (var guard = 0; guard < 10 && !saveFailurePlan.CompletedWordIds.Contains("f1"); guard++) saveFailureSession.Rate(StudyRating.Known, () => true);
Check(saveFailurePlan.CompletedWordIds.Contains("f1") && saveFailureSession.ReclassifyAsForgot(() => true) is { Completed: false } && !saveFailurePlan.CompletedWordIds.Contains("f1") && saveFailurePlan.ForgotWordIds.Contains("f1"), "完成后改判忘记撤销计划完成状态");

var finalSavePlan = DailyStudyPlanRules.Create("Final save", DailyStudyPlanSource.Archive, "A", [PW("final")], 1, false, 0);
var finalSaveSession = new DailyStudyPlanSession(finalSavePlan, monday);
finalSaveSession.CompleteLearn();
finalSaveSession.Rate(StudyRating.Known, () => true); finalSaveSession.Rate(StudyRating.Known, () => true);
Check(finalSaveSession.Rate(StudyRating.Known, () => false) == null && finalSaveSession.Round.CurrentStreak == 2 &&
    finalSaveSession.Round.Completed == 0 && finalSavePlan.CompletedWordIds.Count == 0 && finalSavePlan.LastBatchCompletedDate == null &&
    finalSavePlan.Status == DailyStudyPlanStatus.Active, "最后一次认识保存失败恢复连击、完成数与完成日期");
finalSaveSession.Rate(StudyRating.Known, () => true);
Check(!finalSaveSession.Undo(() => false) && finalSaveSession.Round.IsFinished && finalSavePlan.Status == DailyStudyPlanStatus.Completed,
    "撤销保存失败保留已经完成的队列与计划");
Check(finalSaveSession.ReclassifyAsForgot(() => false) == null && finalSaveSession.Round.IsFinished && finalSavePlan.ForgotWordIds.Count == 0 &&
    finalSavePlan.CompletedWordIds.Count == 1, "改判保存失败保留原评价、完成与忘记集合");
var completedSession = new DailyStudyPlanSession(finalSavePlan, monday.AddDays(1));
Check(!completedSession.Round.HasCurrent && completedSession.BatchWords.Single().Id == "final", "完成计划仍恢复最后批次用于可选拼写");

var metadataPath = Path.Combine(Path.GetTempPath(), "lexi-plan-metadata-" + Guid.NewGuid().ToString("N"), "plans.json");
try
{
    var metadataStore = new DailyStudyPlanStore(metadataPath);
    metadataStore.Save([saveFailurePlan]);
    var persistedMetadata = metadataStore.Load().Single();
    Check(persistedMetadata.CurrentBatchWordIds.SequenceEqual(["f1", "f2"]) && persistedMetadata.ForgotWordIds.SetEquals(["f1"]),
        "完整批次与曾忘记词条集合在独立 JSON 中持久化");
    var oldPlans = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metadataPath))!.AsArray();
    oldPlans[0]!.AsObject().Remove("CurrentBatchWordIds"); oldPlans[0]!.AsObject().Remove("ForgotWordIds");
    oldPlans[0]!.AsObject().Remove("CurrentBatchRandomOrder"); oldPlans[0]!.AsObject().Remove("OriginalWordIds");
    File.WriteAllText(metadataPath, oldPlans.ToJsonString());
    var legacyPlan = metadataStore.Load().Single();
    Check(legacyPlan.CurrentBatchWordIds.Count == 0 && legacyPlan.ForgotWordIds.Count == 0 &&
        new DailyStudyPlanSession(legacyPlan, monday).BatchWords.Count == 2, "旧计划 JSON 缺少新字段时兼容加载并恢复原批次");
}
finally { Directory.Delete(Path.GetDirectoryName(metadataPath)!, recursive: true); }

// 降低次日配额不得截断正在进行的批次
var countRegression = DailyStudyPlanRules.Create("Quota", DailyStudyPlanSource.Archive, "A", [PW("a"), PW("b"), PW("c"), PW("d"), PW("e")], 4, false, 0);
new DailyStudyPlanSession(countRegression, monday);
DailyStudyPlanRules.CompleteWord(countRegression, "a", monday);
countRegression = DailyStudyPlanRules.Adjust(countRegression, "Quota adjusted", 1, false, 11);
Check(DailyStudyPlanRules.GetTodayWords(countRegression, monday).Select(w => w.Id).SequenceEqual(["b", "c", "d"]),
    "降低每日词数不截断正在进行的原批次");
var reducedBatchSession = new DailyStudyPlanSession(countRegression, monday.AddDays(2));
Check(reducedBatchSession.BatchWords.Select(w => w.Id).SequenceEqual(["a", "b", "c", "d"]) &&
    DailyStudyPlanRules.EstimatedDaysRemaining(countRegression) == 2, "降低配额后跨日保留完整批次且正确估算天数");
foreach (var id in new[] { "b", "c", "d" }) Check(DailyStudyPlanRules.CompleteWord(countRegression, id, monday.AddDays(2)), "原批次继续完成 " + id);
Check(countRegression.LastBatchCompletedDate == monday.AddDays(2) && DailyStudyPlanRules.GetTodayWords(countRegression, monday.AddDays(2)).Count == 0 &&
    DailyStudyPlanRules.GetTodayWords(countRegression, monday.AddDays(3)).Select(w => w.Id).SequenceEqual(["e"]), "完成原批次后当天等待且次日采用新配额");

var adjustSource = DailyStudyPlanRules.Create("Original", DailyStudyPlanSource.Archive, "A", Enumerable.Range(1, 9).Select(i => PW("a" + i)).ToList(), 2, false, 1);
new DailyStudyPlanSession(adjustSource, monday);
DailyStudyPlanRules.CompleteWord(adjustSource, "a1", monday); adjustSource.ForgotWordIds.Add("a2");
var sourceBeforeAdjustment = JsonSerializer.Serialize(adjustSource);
var increased = DailyStudyPlanRules.Adjust(adjustSource, "  Increased  ", 4, true, 17);
Check(JsonSerializer.Serialize(adjustSource) == sourceBeforeAdjustment && !ReferenceEquals(adjustSource, increased) &&
    increased.Id == adjustSource.Id && increased.CreatedAt == adjustSource.CreatedAt && increased.Name == "Increased" &&
    increased.CompletedWordIds.SetEquals(["a1"]) && increased.ForgotWordIds.SetEquals(["a2"]) &&
    increased.CurrentBatchWordIds.SequenceEqual(["a1", "a2"]) && increased.LastBatchCompletedDate == adjustSource.LastBatchCompletedDate &&
    increased.Status == DailyStudyPlanStatus.Active, "调整返回同一身份的独立克隆，保留全部进度及日期供事务保存");
Check(DailyStudyPlanRules.GetTodayWords(increased, monday).Select(w => w.Id).SequenceEqual(["a2"]) &&
    DailyStudyPlanRules.EstimatedDaysRemaining(increased) == 3 && increased.CurrentBatchRandomOrder == false,
    "提高配额和开启随机不扩张或重排正在进行的批次");
var independentClone = DailyStudyPlanRules.Adjust(adjustSource, "Independent", 2, false, 9);
independentClone.Words[0].Meaning = "clone only"; independentClone.ForgotWordIds.Clear(); independentClone.CurrentBatchWordIds.Clear();
Check(JsonSerializer.Serialize(adjustSource) == sourceBeforeAdjustment, "调整克隆的词条与集合修改不会污染原计划");
var randomizedAgain = DailyStudyPlanRules.Adjust(adjustSource, "Random again", 4, true, 17);
Check(increased.Words.Select(w => w.Id).SequenceEqual(randomizedAgain.Words.Select(w => w.Id)) &&
    increased.Words.Select(w => w.Id).Order().SequenceEqual(adjustSource.Words.Select(w => w.Id).Order()) &&
    !increased.Words.Skip(2).Select(w => w.Id).SequenceEqual(["a3", "a4", "a5", "a6", "a7", "a8", "a9"]),
    "未来随机顺序可复现，词表不增删且当前批次固定");
var orderedAgain = DailyStudyPlanRules.Adjust(increased, "Ordered", 4, false, 2);
Check(orderedAgain.Words.Select(w => w.Id).SequenceEqual(["a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8", "a9"]),
    "切回顺序恢复未来词条的原始输入顺序");
DailyStudyPlanRules.CompleteWord(orderedAgain, "a2", monday);
var afterCompleteAdjustment = DailyStudyPlanRules.Adjust(orderedAgain, "Next quota", 3, false, 3);
Check(DailyStudyPlanRules.GetTodayWords(afterCompleteAdjustment, monday).Count == 0 &&
    DailyStudyPlanRules.GetTodayWords(afterCompleteAdjustment, monday.AddDays(1)).Select(w => w.Id).SequenceEqual(["a3", "a4", "a5"]),
    "今日完成后修改配额仍等待次日并按新词数开批");
var stopped = DailyStudyPlanRules.Adjust(adjustSource, "Resume", 2, false, 1);
DailyStudyPlanRules.Stop(stopped);
var stoppedBefore = JsonSerializer.Serialize(stopped);
Check(DailyStudyPlanRules.Resume(stopped) && stopped.Status == DailyStudyPlanStatus.Active &&
    stopped.CompletedWordIds.SetEquals(["a1"]) && stopped.ForgotWordIds.SetEquals(["a2"]) &&
    stopped.CurrentBatchWordIds.SequenceEqual(["a1", "a2"]) && stopped.LastBatchCompletedDate == adjustSource.LastBatchCompletedDate &&
    DailyStudyPlanRules.GetTodayWords(stopped, monday.AddDays(5)).Select(w => w.Id).SequenceEqual(["a2"]), "停止后恢复保留完成、忘记与原批次，跨日继续");
Check(!DailyStudyPlanRules.Resume(stopped) && !DailyStudyPlanRules.Resume(ieltsOverlap), "仅停止状态允许恢复且不会重复计划");
var stoppedForValidation = JsonSerializer.Deserialize<DailyStudyPlan>(stoppedBefore)!;
RejectPlan(() => DailyStudyPlanRules.Adjust(stoppedForValidation, "No", 2, false, 0), "停止计划不可调整");
RejectPlan(() => DailyStudyPlanRules.Adjust(ieltsOverlap, "No", 2, false, 0), "已完成计划不可调整");
RejectPlan(() => DailyStudyPlanRules.Adjust(adjustSource, " ", 2, false, 0), "调整拒绝空名称");
RejectPlan(() => DailyStudyPlanRules.Adjust(adjustSource, "Invalid", 0, false, 0), "调整拒绝非正配额");
var legacyAdjusted = JsonSerializer.Deserialize<DailyStudyPlan>(sourceBeforeAdjustment)!;
legacyAdjusted.CurrentBatchWordIds.Clear(); legacyAdjusted.OriginalWordIds.Clear(); legacyAdjusted.CurrentBatchRandomOrder = null;
legacyAdjusted = DailyStudyPlanRules.Adjust(legacyAdjusted, "Legacy", 1, true, 17);
Check(legacyAdjusted.CurrentBatchWordIds.SequenceEqual(["a1", "a2"]) &&
    DailyStudyPlanRules.GetTodayWords(legacyAdjusted, monday.AddDays(1)).Select(w => w.Id).SequenceEqual(["a2"]),
    "旧 JSON 调整前先按旧配额恢复中断批次");
var randomBatch = DailyStudyPlanRules.Create("Random batch", DailyStudyPlanSource.Archive, "A", Enumerable.Range(1, 9).Select(i => PW("r" + i)).ToList(), 3, true, 17);
new DailyStudyPlanSession(randomBatch, monday);
var randomBatchAdjusted = DailyStudyPlanRules.Adjust(randomBatch, "Ordered future", 2, false, 19);
Check(randomBatchAdjusted.CurrentBatchRandomOrder == true && randomBatchAdjusted.CurrentBatchWordIds.SequenceEqual(randomBatch.CurrentBatchWordIds),
    "关闭随机仍保留当前批次原有随机模式");
var adjustedPath = Path.Combine(Path.GetTempPath(), "lexi-adjusted-" + Guid.NewGuid().ToString("N"), "plans.json");
try
{
    var adjustedStore = new DailyStudyPlanStore(adjustedPath); adjustedStore.Save([increased]);
    var restoredAdjustment = adjustedStore.Load().Single();
    Check(restoredAdjustment.Id == adjustSource.Id && restoredAdjustment.DailyWordCount == 4 && restoredAdjustment.RandomOrder &&
        restoredAdjustment.CurrentBatchRandomOrder == false && restoredAdjustment.OriginalWordIds.SequenceEqual(adjustSource.OriginalWordIds) &&
        restoredAdjustment.CurrentBatchWordIds.SequenceEqual(["a1", "a2"]) && restoredAdjustment.ForgotWordIds.SetEquals(["a2"]) &&
        restoredAdjustment.CompletedWordIds.SetEquals(["a1"]), "调整后所有元数据独立持久化且不新增身份");
}
finally { Directory.Delete(Path.GetDirectoryName(adjustedPath)!, recursive: true); }

var previewPlan = DailyStudyPlanRules.Create("Preview", DailyStudyPlanSource.Archive, "A", [PW("v1"), PW("v2"), PW("v3"), PW("v4"), PW("v5")], 4, false, 0);
var previewBefore = JsonSerializer.Serialize(previewPlan);
DailyStudyPlanRules.GetTodayWords(previewPlan, monday); DailyStudyPlanRules.EstimatedDaysRemaining(previewPlan);
Check(JsonSerializer.Serialize(previewPlan) == previewBefore, "计划列表查询不会冻结批次或修改状态");
var unstartedAdjusted = DailyStudyPlanRules.Adjust(previewPlan, "Preview updated", 2, false, 17);
Check(unstartedAdjusted.CurrentBatchWordIds.Count == 0 && DailyStudyPlanRules.GetTodayWords(unstartedAdjusted, monday).Select(w => w.Id).SequenceEqual(["v1", "v2"]),
    "尚未开始的计划立即按新词数提供首批");
new DailyStudyPlanSession(previewPlan, monday);
var switchedPreview = DailyStudyPlanRules.Adjust(previewPlan, "Future random", 1, true, 17);
var preservedOrderSession = new DailyStudyPlanSession(switchedPreview, monday, new StudyRound<string>(new Random(17)));
var preservedLearningOrder = new List<string>();
for (var i = 0; i < 4; i++) { preservedLearningOrder.Add(preservedOrderSession.Round.Current); preservedOrderSession.CompleteLearn(); }
Check(preservedLearningOrder.SequenceEqual(["v1", "v2", "v3", "v4"]), "计划会话使用冻结顺序模式，编辑后不打乱当前学习卡");
var sameDayStopped = DailyStudyPlanRules.Adjust(orderedAgain, "Resume gate", 2, false, 17);
DailyStudyPlanRules.Stop(sameDayStopped);
var resumeExpected = JsonSerializer.Deserialize<DailyStudyPlan>(JsonSerializer.Serialize(sameDayStopped))!;
resumeExpected.Status = DailyStudyPlanStatus.Active;
Check(DailyStudyPlanRules.Resume(sameDayStopped) && JsonSerializer.Serialize(sameDayStopped) == JsonSerializer.Serialize(resumeExpected) &&
    DailyStudyPlanRules.GetTodayWords(sameDayStopped, monday).Count == 0 &&
    DailyStudyPlanRules.GetTodayWords(sameDayStopped, monday.AddDays(1)).Select(w => w.Id).SequenceEqual(["a3", "a4"]),
    "恢复只修改状态并保留今日完成后的日期门槛");
var invalidOrderPath = Path.Combine(Path.GetTempPath(), "lexi-invalid-order-" + Guid.NewGuid().ToString("N"), "plans.json");
try
{
    var invalidOrderStore = new DailyStudyPlanStore(invalidOrderPath); invalidOrderStore.Save([increased]);
    var invalidOrderJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(invalidOrderPath))!.AsArray();
    invalidOrderJson[0]!["OriginalWordIds"] = System.Text.Json.Nodes.JsonNode.Parse("[\"a1\",\"a1\"]");
    File.WriteAllText(invalidOrderPath, invalidOrderJson.ToJsonString());
    try { invalidOrderStore.Load(); throw new Exception("FAIL: 原始词序重复或缺词必须拒绝"); }
    catch (InvalidDataException) { Console.WriteLine("PASS: 原始词序重复或缺词必须拒绝"); }
}
finally { Directory.Delete(Path.GetDirectoryName(invalidOrderPath)!, recursive: true); }

var assisted = new TypingSession();
assisted.Reset([new LearningWord {Id="assisted",Words=["apple"]}],true);
Check(assisted.HintText == "_____", "辅助拼写初始隐藏完整答案");
Check(assisted.RevealHint() && assisted.HintText == "a____", "主动提示只揭示一个字母");
Check(assisted.Submit("apple") == TypingOutcome.Correct && assisted.AssistedCount == 1 && assisted.UnassistedCorrectCount == 0, "提示完成不计入无提示正确");
assisted.Reset([new LearningWord {Id="answer",Words=["apple"]}],false);
assisted.RevealAnswer();
Check(assisted.HintText=="apple"&&assisted.ErrorIds.Contains("answer"), "查看答案加入待重练词");
Check(assisted.Submit("apple")==TypingOutcome.Correct&&assisted.UnassistedCorrectCount==0,"查看答案后不能计为无提示正确");
Console.WriteLine("All learning tests passed.");
