using Lexi;
using System.Text.Json;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
LearningWord W(string text) => new() { Id = text, Words = [text] };
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
var session = new TypingSession();
session.Reset([W("apple"), W("ice cream")], true);
Check(!session.Advance(), "不能未答先推进");
Check(session.Submit("a") == TypingOutcome.Pending, "淡写正确前缀");
Check(session.Submit("ax") == TypingOutcome.Retry && session.Input == "" && session.Cursor == 0 && session.ErrorPositions.SequenceEqual([1]), "淡写中途错误立即清空，保留位置");
Check(session.Submit("apple") == TypingOutcome.Correct && session.Advance() && session.Current!.Word == "ice cream", "正确后才能进入下一词");
Check(session.Submit("ICE\u00a0CREAM") == TypingOutcome.Correct && session.Advance() && session.Outcome == TypingOutcome.Finished, "大小写与Unicode空格、短语、完成");
Check(session.Retries == 1 && session.ErrorIds.Contains("apple") && session.Accuracy < 100, "重试与错词统计");
session.Reset([W("apple")], false);
Check(session.Submit("ax") == TypingOutcome.Pending && session.ErrorPositions.Count == 0 && session.Accuracy == 100, "无提示中途不泄露正误");
Check(session.Submit("axple") == TypingOutcome.Retry && session.Input == "" && session.Cursor == 0, "无提示满长度才重置");
Check(session.Submit("APPLe") == TypingOutcome.Correct && session.Advance(), "无提示正确重练完成");
session.Reset([W("well-being")], false);
Check(session.Submit("well") == TypingOutcome.Pending && session.Submit("wel") == TypingOutcome.Pending, "允许退格编辑");
Check(session.Submit("well-beingX") == TypingOutcome.Retry, "超长不绕过检查");
session.Reset([], true); Check(session.Outcome == TypingOutcome.Finished, "空词库安全完成");
var catalog = IeltsCatalog.Load();
Check(catalog.Sections.Count(s => s.Kind == "vocabulary") == 22 && catalog.Sections.Where(s => s.Kind == "vocabulary").Sum(s => s.Entries.Count) == 3674, "22章3674完整词条");
Check(catalog.Sections.Single(s => s.Id == "listening179").Entries.Count == 179, "179听力词");
Check(catalog.Sections.Where(s => s.Kind == "reading").Sum(s => s.Entries.Count) == 376, "阅读实际376不伪造538");
Check(catalog.Sentences.Count == 100 && catalog.Sentences.All(s => s.Chinese.Length > 0 && s.BookAnswer.Length > 0), "100句写作及书中答案");
Check(catalog.Find("atmosphere")!.Example.Length > 0 && catalog.Find("jeopardize") != null, "例句及所有词形索引");
Check(catalog.AllWords.Select(w => w.Id).Distinct().Count() == catalog.AllWords.Count(), "稳定词条ID无碰撞");
Check(catalog.AllWords.Where(w => w.AudioPath.Length > 0).All(w => IeltsCatalog.ResolveAsset(w.AudioPath) != null), "所有单词录音引用存在");
Check(IeltsCatalog.ResolveAsset("../../../outside") == null, "资源路径防越界");
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(IeltsCatalog.AssetDirectory, "manifest.json")));
Check(manifest.RootElement.GetProperty("audioFallbacks").GetArrayLength() >= 0 && IeltsCatalog.ResolveAsset("grammar/雅思基础语法配套课程讲义.pdf") != null, "语法资源与发音回退清单");
Check(manifest.RootElement.GetProperty("assets").EnumerateObject().All(a =>
    a.Name.Normalize(System.Text.NormalizationForm.FormD) == a.Name), "媒体文件名不受HFS+重音规范化影响");
Check(manifest.RootElement.GetProperty("assets").EnumerateObject().All(a =>
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(IeltsCatalog.AssetDirectory, a.Name)))).Equals(a.Value.GetString(), StringComparison.OrdinalIgnoreCase)), "所有3877资源校验和匹配");
var roundType = typeof(TypingSession).Assembly.GetType("Lexi.LearningRound");
Check(roundType != null, "三种模式共用选词算法");
List<LearningWord> SelectRound(int count, bool all, bool random, int seed) =>
    (List<LearningWord>)roundType!.GetMethod("Select")!.Invoke(null, [new List<LearningWord> { W("apple"), new() { Id = "colour", Words = ["colour", "color"] }, W("ice cream") }, count, all, random, seed])!;
Check(SelectRound(2, false, false, 31).Select(w => w.Word).SequenceEqual(["apple", "colour"]), "自定义词数且按部分顺序学习");
Check(SelectRound(2, true, false, 31).Select(w => w.Word).SequenceEqual(["apple", "colour", "color", "ice cream"]), "全部包含每个拼写变体");
Check(SelectRound(3, false, true, 31).Select(w => w.Word).SequenceEqual(SelectRound(3, false, true, 31).Select(w => w.Word)), "随机种子可复现");
Check(SelectRound(999, false, true, 31).Count == 4 && SelectRound(0, false, false, 31).Count == 1, "词数不越界或意外为空");
// 记忆连击（StudyRound v2）：首次学习需 3 次「认识」；复习首次认识即完成，否则同样需要 3 次；
// 模糊 -1、忘记清零；每遍每个词只出现一次且遍内打乱，不把同一个词连着出现。
List<string> Deck(int n) => Enumerable.Range(1, n).Select(i => "w" + i).ToList();

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
DailyStudyPlanWord PW(string id) => new() { Id = id, Word = id, Meaning = "meaning " + id };
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

// Changing tomorrow's quota must never truncate a batch already in progress.
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


// ══ TrajectoryAndCanonicalTests：轨迹归约 + 词身份解析（WS-A）══
{
    var memT0 = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    const string memWordA = "archive:uuid-a";
    const string memWordB = "archive:uuid-b";

    // 造一条轨迹：每个元素 = 一次呈现（是否 Learn 卡 / 初判 / 改判）。
    List<LearningInteractionEvent> MemTrack(string wordKey, string sessionId,
        params (bool IsRecall, StudyRating? Rating, StudyRating? Revised)[] cards)
    {
        var list = new List<LearningInteractionEvent>();
        var at = memT0;
        for (var i = 0; i < cards.Length; i++)
        {
            var presentationId = $"p{i + 1}";
            var isRecall = cards[i].IsRecall;
            var appearance = i + 1;
            void Add(InteractionEventKind kind, StudyRating? response)
                => list.Add(new LearningInteractionEvent
                {
                    EventId = Guid.NewGuid().ToString("N"), SessionId = sessionId, WordKey = wordKey,
                    PresentationId = presentationId, OccurredAtUtc = at, Kind = kind, Response = response,
                    IsRecall = isRecall, SessionAppearanceIndex = appearance,
                    IsFirstAppearanceForWord = i == 0,
                });
            at = at.AddMinutes(1);
            Add(InteractionEventKind.Presented, null);
            if (cards[i].Rating is { } rated) { at = at.AddSeconds(5); Add(InteractionEventKind.Rated, rated); }
            if (cards[i].Revised is { } revised) { at = at.AddSeconds(5); Add(InteractionEventKind.Revised, revised); }
        }
        return list;
    }

    LearningInteractionEvent MemRaw(string eventId, string wordKey, string sessionId, string presentationId,
        DateTime at, InteractionEventKind kind, StudyRating? response, bool isRecall = true, string? supersedes = null)
        => new()
        {
            EventId = eventId, SessionId = sessionId, WordKey = wordKey, PresentationId = presentationId,
            OccurredAtUtc = at, Kind = kind, Response = response, IsRecall = isRecall, SupersedesEventId = supersedes,
        };

    // 由事件流得到 (summary, canonical)，与 UI/持久化层的调用顺序一致。
    (WordSessionSummary Summary, CanonicalReview? Canonical) MemReduce(string wordKey, string sessionId,
        LearningMode mode, List<LearningInteractionEvent> events, DateTime completedAtUtc)
    {
        var presentations = TrajectoryReducer.ProjectPresentations(events);
        var summary = TrajectoryReducer.BuildSummary(wordKey, sessionId, presentations, completedAtUtc);
        var canonical = TrajectoryReducer.ResolveCanonical(wordKey, sessionId, mode, presentations, summary, completedAtUtc);
        return (summary, canonical);
    }

    string MemFingerprint(WordSessionSummary s) => string.Join("|", s.TotalPresentations, s.FinalKnownCount,
        s.FinalFuzzyCount, s.FinalForgottenCount, s.ResetCount, s.ResponseRevisionCount, s.KnownToFuzzy,
        s.KnownToForgotten, s.FuzzyToForgotten, s.HadFuzzy, s.HadForgotten, s.HadResponseRevision, s.MaxKnownStreak);

    // —— 对照轨迹 A/B/C/D：summary 与 canonical 必须可区分 ——
    var memA = MemReduce(memWordA, "sA", LearningMode.Review,
        MemTrack(memWordA, "sA", (true, StudyRating.Known, null)), memT0.AddMinutes(10));
    Check(memA.Canonical!.Rating == StudyRating.Known && memA.Canonical.Origin == CanonicalOrigin.FirstRetrieval &&
        memA.Canonical.SourcePresentationId == "p1" && memA.Summary.ResponseRevisionCount == 0 &&
        memA.Summary.ResetCount == 0 && memA.Summary.FinalKnownCount == 1 && memA.Summary.MaxKnownStreak == 1 &&
        !memA.Summary.HadResponseRevision, "轨迹A：一次认识 → canonical=Known，无修正无清零");

    var memB = MemReduce(memWordB, "sB", LearningMode.Review,
        MemTrack(memWordB, "sB", (true, StudyRating.Known, StudyRating.Forgot)), memT0.AddMinutes(10));
    Check(memB.Summary.KnownToForgotten == 1 && memB.Summary.HadResponseRevision && memB.Summary.ResponseRevisionCount == 1 &&
        memB.Canonical!.Rating == StudyRating.Forgot && memB.Canonical.SourcePresentationId == "p1",
        "轨迹B：认识后改判忘记 → KnownToForgotten=1，canonical 取终判 Forgot");

    var memC = MemReduce("archive:uuid-c", "sC", LearningMode.Review, MemTrack("archive:uuid-c", "sC",
        (true, StudyRating.Unsure, null), (true, StudyRating.Known, null), (true, StudyRating.Unsure, null),
        (true, StudyRating.Known, null), (true, StudyRating.Known, null), (true, StudyRating.Known, null)),
        memT0.AddMinutes(20));
    Check(memC.Summary.FinalFuzzyCount >= 2 && memC.Summary.HadFuzzy && memC.Summary.ResetCount == 0 &&
        memC.Canonical!.Rating == StudyRating.Unsure && memC.Summary.MaxKnownStreak == 3,
        "轨迹C：模糊/认识交替 → HadFuzzy，canonical 取首次真实 retrieval 的终判 Unsure");

    var memD = MemReduce("archive:uuid-d", "sD", LearningMode.Review, MemTrack("archive:uuid-d", "sD",
        (true, StudyRating.Forgot, null), (true, StudyRating.Known, null), (true, StudyRating.Unsure, null),
        (true, StudyRating.Forgot, null), (true, StudyRating.Known, null), (true, StudyRating.Known, null),
        (true, StudyRating.Known, null)), memT0.AddMinutes(20));
    Check(memD.Summary.ResetCount == 2 && memD.Summary.HadForgotten && memD.Canonical!.Rating == StudyRating.Forgot &&
        memD.Summary.FinalForgottenCount == 2 && memD.Summary.MaxKnownStreak == 3,
        "轨迹D：两次忘记清零 → ResetCount=2，canonical=Forgot");

    var memFingerprints = new[] { MemFingerprint(memA.Summary), MemFingerprint(memB.Summary),
        MemFingerprint(memC.Summary), MemFingerprint(memD.Summary) };
    Check(memFingerprints.Distinct(StringComparer.Ordinal).Count() == 4, "轨迹A/B/C/D 的 summary 两两可区分");

    // —— 首答不被后续强化成功覆盖 ——
    var memHardFirst = MemReduce("archive:uuid-e", "sE", LearningMode.Review, MemTrack("archive:uuid-e", "sE",
        (true, StudyRating.Unsure, null), (true, StudyRating.Known, null), (true, StudyRating.Known, null),
        (true, StudyRating.Known, null)), memT0.AddMinutes(20));
    Check(memHardFirst.Canonical!.Rating == StudyRating.Unsure && memHardFirst.Summary.FinalKnownCount == 3 &&
        memHardFirst.Summary.FirstInitialResponse == StudyRating.Unsure, "复习首答模糊，后续连对不覆盖 canonical");

    var memAgainFirst = MemReduce("archive:uuid-f", "sF", LearningMode.Review, MemTrack("archive:uuid-f", "sF",
        (true, StudyRating.Forgot, null), (true, StudyRating.Known, null), (true, StudyRating.Known, null)),
        memT0.AddMinutes(20));
    Check(memAgainFirst.Canonical!.Rating == StudyRating.Forgot && memAgainFirst.Summary.FirstValidatedResponse == StudyRating.Forgot,
        "复习首答忘记，后续连对不覆盖 canonical");

    // —— Learn 卡只看答案，不构成 retrieval ——
    var memLearnEvents = MemTrack("archive:uuid-g", "sG",
        (false, null, null), (true, StudyRating.Known, null), (true, StudyRating.Known, null), (true, StudyRating.Known, null));
    var memLearnPresentations = TrajectoryReducer.ProjectPresentations(memLearnEvents);
    var memLearn = MemReduce("archive:uuid-g", "sG", LearningMode.FirstLearn, memLearnEvents, memT0.AddMinutes(30));
    Check(memLearnPresentations.Count == 4 && !memLearnPresentations[0].IsRecall &&
        memLearnPresentations[0].FinalValidatedResponse == null && memLearnPresentations[0].InitialResponse == null,
        "Learn 卡被投影为 IsRecall=false 且没有有效判断");
    Check(memLearn.Summary.TotalPresentations == 4 && memLearn.Summary.FinalKnownCount == 3 &&
        memLearn.Summary.PresentationsToMastery == 4 && memLearn.Canonical!.Rating == StudyRating.Known &&
        memLearn.Canonical.Origin == CanonicalOrigin.FirstLearnAggregate && memLearn.Canonical.SourcePresentationId == null,
        "Learn 卡计入呈现总数但不计入 retrieval：canonical=Known、FinalKnownCount=3、PresentationsToMastery=4");
    Check(memLearn.Summary.FirstInitialResponse == StudyRating.Known &&
        memLearn.Summary.FirstPresentedAtUtc == memLearnPresentations[0].PresentedAtUtc &&
        memLearn.Canonical.ReviewedAtUtc == memLearnPresentations[0].PresentedAtUtc,
        "首次入参取第一张 Learn 卡的时刻，首答取第一个真实 retrieval");

    var memLearnFuzzy = MemReduce("archive:uuid-h", "sH", LearningMode.FirstLearn,
        MemTrack("archive:uuid-h", "sH", (false, null, null), (true, StudyRating.Unsure, null)), memT0.AddMinutes(5));
    Check(memLearnFuzzy.Canonical!.Rating == StudyRating.Unsure, "首次学习聚合：仅模糊无忘记 → Unsure");

    var memLearnForgot = MemReduce("archive:uuid-i", "sI", LearningMode.FirstLearn,
        MemTrack("archive:uuid-i", "sI", (true, StudyRating.Known, null), (true, StudyRating.Known, null),
            (true, StudyRating.Forgot, null)), memT0.AddMinutes(5));
    Check(memLearnForgot.Canonical!.Rating == StudyRating.Forgot, "首次学习聚合：任一忘记 → Forgot");

    // —— 未定稿时（completedAt=null）不产生 mastery 口径 ——
    var memUnfinished = TrajectoryReducer.BuildSummaryFromEvents("archive:uuid-a", "sA",
        MemTrack("archive:uuid-a", "sA", (true, StudyRating.Known, null)), null);
    Check(memUnfinished.PresentationsToMastery == 0 && memUnfinished.TimeToMasteryMs == null &&
        memUnfinished.CompletedAtUtc == null && memUnfinished.TotalPresentations == 1,
        "未定稿：PresentationsToMastery=0 且 TimeToMasteryMs=null");
    Check(memA.Summary.PresentationsToMastery == 1 && memA.Summary.TimeToMasteryMs == 540000 &&
        memA.Summary.CompletedAtUtc == memT0.AddMinutes(10), "TimeToMasteryMs = 定稿时刻 − 首次呈现时刻");

    // —— 一个 word × session 只产生一个 canonical ——
    var memKeyA = memA.Canonical!.WordKey;
    Check(memA.Canonical.CanonicalId == CanonicalReview.BuildId(memKeyA, "sA") &&
        memA.Canonical.CanonicalId == CanonicalReview.BuildId(memKeyA, "sA") &&
        memA.Canonical.CanonicalId != CanonicalReview.BuildId(memKeyA, "sA2") &&
        memLearn.Canonical.CanonicalId == CanonicalReview.BuildId("archive:uuid-g", "sG") &&
        memA.Canonical.CanonicalId != memLearn.Canonical.CanonicalId,
        "canonical 幂等键对同一 (wordKey, sessionId) 稳定，不同 session 不同");
    var memAReplay = MemReduce(memWordA, "sA", LearningMode.Review,
        MemTrack(memWordA, "sA", (true, StudyRating.Known, null)), memT0.AddMinutes(10));
    Check(memAReplay.Canonical!.CanonicalId == memA.Canonical.CanonicalId &&
        memAReplay.Summary.FinalKnownCount == memA.Summary.FinalKnownCount &&
        memAReplay.Canonical.Revision == 1 && !memAReplay.Canonical.Invalidated &&
        memAReplay.Canonical.AggregationPolicyVersion == AggregationPolicy.Version &&
        memAReplay.Canonical.CompletedAtUtc == memT0.AddMinutes(10),
        "同一 (wordKey, sessionId) 重放得到同一个 canonical（幂等）");

    // —— Undo 使 presentation 失效 ——
    var memUndoOnly = new List<LearningInteractionEvent>
    {
        MemRaw("u1", memWordA, "sU", "p1", memT0, InteractionEventKind.Presented, null),
        MemRaw("u2", memWordA, "sU", "p1", memT0.AddSeconds(3), InteractionEventKind.Rated, StudyRating.Known),
        MemRaw("u3", memWordA, "sU", "p1", memT0.AddSeconds(9), InteractionEventKind.Undone, null, supersedes: "u2"),
    };
    var memUndoPresentations = TrajectoryReducer.ProjectPresentations(memUndoOnly);
    var memUndoSummary = TrajectoryReducer.BuildSummaryFromEvents(memWordA, "sU", memUndoOnly, memT0.AddMinutes(5));
    Check(memUndoPresentations.Count == 1 && memUndoPresentations[0].InitialResponse == StudyRating.Known &&
        memUndoPresentations[0].FinalValidatedResponse == null && memUndoPresentations[0].RevisionPath.Count == 0 &&
        !memUndoPresentations[0].HadResponseRevision,
        "被 Undone 撤销后 FinalValidatedResponse=null 且 RevisionPath 清空");
    Check(memUndoSummary.FinalKnownCount == 0 && memUndoSummary.ResetCount == 0 && memUndoSummary.MaxKnownStreak == 0,
        "被撤销的判断不计入任何分布");
    Check(TrajectoryReducer.ResolveCanonical(memWordA, "sU", LearningMode.Review, memUndoPresentations,
        memUndoSummary, memT0.AddMinutes(5)) == null, "唯一真实 retrieval 被撤销 → canonical 为 null");

    var memUndoFirstThenSecond = new List<LearningInteractionEvent>
    {
        MemRaw("v1", memWordB, "sV", "p1", memT0, InteractionEventKind.Presented, null),
        MemRaw("v2", memWordB, "sV", "p1", memT0.AddSeconds(3), InteractionEventKind.Rated, StudyRating.Known),
        MemRaw("v3", memWordB, "sV", "p1", memT0.AddSeconds(6), InteractionEventKind.Undone, null, supersedes: "v2"),
        MemRaw("v4", memWordB, "sV", "p2", memT0.AddMinutes(1), InteractionEventKind.Presented, null),
        MemRaw("v5", memWordB, "sV", "p2", memT0.AddMinutes(1).AddSeconds(4), InteractionEventKind.Rated, StudyRating.Unsure),
    };
    var memUndoFallback = MemReduce(memWordB, "sV", LearningMode.Review, memUndoFirstThenSecond, memT0.AddMinutes(5));
    Check(memUndoFallback.Canonical!.Rating == StudyRating.Unsure && memUndoFallback.Canonical.SourcePresentationId == "p2" &&
        memUndoFallback.Summary.TotalPresentations == 2 && memUndoFallback.Summary.FinalFuzzyCount == 1,
        "第一张被撤销后 canonical 顺延到下一个有效 retrieval");
    Check(memUndoFallback.Summary.FirstInitialResponse == StudyRating.Known &&
        memUndoFallback.Summary.FirstValidatedResponse == null,
        "summary 的首答仍取第一个真实 retrieval（即使它被撤销），与 canonical 的顺延口径不同");

    // —— 组间按 PresentedAtUtc 排序（输入乱序也必须稳定归约） ——
    var memShuffled = MemTrack(memWordA, "sW", (true, StudyRating.Unsure, null), (true, StudyRating.Known, null));
    var memReversed = new List<LearningInteractionEvent>(memShuffled);
    memReversed.Reverse();
    Check(TrajectoryReducer.ProjectPresentations(memReversed).Select(p => p.PresentationId).SequenceEqual(["p1", "p2"]),
        "ProjectPresentations 按 PresentedAtUtc 排序，与输入顺序无关");
    Check(TrajectoryReducer.ProjectPresentations([]).Count == 0 &&
        TrajectoryReducer.BuildSummaryFromEvents(memWordA, "sX", [], null).TotalPresentations == 0,
        "空输入安全：无 presentation、无计数");

    // —— WordKeyResolver ——
    var memArchiveItem = new WordItem { Id = 7, Word = "apple" };
    memArchiveItem.Archive.Uuid = "u-1";
    var memIeltsWord = new LearningWord { Id = "c-9", Words = ["apple"] };
    WordItem? MemFindArchive(string form) => form.Trim().ToLowerInvariant() == "apple" ? memArchiveItem : null;
    LearningWord? MemFindIelts(string form) => form.Trim().ToLowerInvariant() == "apple" ? memIeltsWord : null;

    Check(WordKeyResolver.FormC("  Ice   Cream  ") == "ice cream" && WordKeyResolver.FormC("ICE CREAM") == "ice cream" &&
        WordKeyResolver.FormC("Apple") == "apple" && WordKeyResolver.FormC("苹果") == "苹果" &&
        WordKeyResolver.FormC(" a\t\nb ") == "a b" && WordKeyResolver.FormC("apple") == "apple",
        "FormC 归一化：Trim + 空白折叠 + 小写");

    Check(WordKeyResolver.Resolve("apple", "ielts", MemFindArchive, MemFindIelts) == WordKey.Ielts("c-9") &&
        WordKeyResolver.Resolve("apple", "review", MemFindArchive, MemFindIelts) == WordKey.Archive("u-1") &&
        WordKeyResolver.Resolve("apple", null, _ => null, MemFindIelts) == WordKey.Ielts("c-9") &&
        WordKeyResolver.Resolve("pear", "review", MemFindArchive, MemFindIelts) == WordKey.Form("pear") &&
        WordKeyResolver.Resolve("  Ice   Cream ", "review", MemFindArchive, MemFindIelts) == WordKey.Form("ice cream") &&
        WordKeyResolver.Resolve("apple", "ielts", MemFindArchive, _ => null) == WordKey.Archive("u-1"),
        "Resolve 三种 fallback：查词页优先 IELTS、档案优先于目录、都不中退化为 form");

    var memArchivePlan = new DailyStudyPlan { Id = "plan-1", Name = "档案计划", Source = DailyStudyPlanSource.Archive };
    var memArchivePlanWord = new DailyStudyPlanWord { Id = "7", Word = "apple" };
    var memIeltsPlan = new DailyStudyPlan { Id = "plan-2", Name = "目录计划", Source = DailyStudyPlanSource.Ielts };
    var memIeltsPlanWord = new DailyStudyPlanWord { Id = "c-9", Word = "apple" };
    var memPlanKey = WordKeyResolver.ResolvePlanWord(memArchivePlan, memArchivePlanWord, id => id == "7" ? memArchiveItem : null);
    Check(memPlanKey == WordKey.Archive("u-1") &&
        WordKeyResolver.ResolvePlanWord(memIeltsPlan, memIeltsPlanWord, _ => throw new Exception("IELTS 计划不应反查档案")) == WordKey.Ielts("c-9"),
        "ResolvePlanWord：档案源反查 uuid、IELTS 源直接用目录 Id");
    Check(memPlanKey == WordKeyResolver.Resolve("apple", "review", MemFindArchive, MemFindIelts),
        "同词多入口（计划卡 / 复习页）落到同一个 WordKey，共享同一张长期记忆卡");
    var memPlanMissing = false;
    try { WordKeyResolver.ResolvePlanWord(memArchivePlan, memArchivePlanWord, _ => null); }
    catch (InvalidOperationException) { memPlanMissing = true; }
    Check(memPlanMissing, "计划词在档案中查不到时抛 InvalidOperationException 交给调用方降级");

    Check(WordKeyResolver.FromArchive(memArchiveItem) == WordKey.Archive("u-1") &&
        WordKeyResolver.FromArchiveUuid("u-1") == WordKey.Archive("u-1") &&
        WordKeyResolver.FromIelts(memIeltsWord) == WordKey.Ielts("c-9") &&
        WordKeyResolver.FromForm("  Ice   Cream ") == WordKey.Form("ice cream"),
        "FromArchive / FromArchiveUuid / FromIelts / FromForm 直构口径正确");
    Check(WordKey.Parse(WordKey.Archive("u-1").Key) == WordKey.Archive("u-1") &&
        WordKey.Parse(WordKey.Ielts("c-9").Key) == WordKey.Ielts("c-9") &&
        WordKey.Parse(WordKey.Form("ice cream").Key) == WordKey.Form("ice cream") &&
        WordKey.Parse("archive:u-1").Key == "archive:u-1" && WordKey.Parse("form:ice cream").SourceId == "ice cream",
        "WordKey.Parse(Key) 与构造往返一致");
}

// ══ Fsrs6CoreTests：FSRS-6 纯 C# 核心 + 官方 golden 逐点回归（WS-B）══
{
    var fsrsWeights = Fsrs6Weights.Defaults;
    IReadOnlyList<double> fsrsW = fsrsWeights.Weights;
    var goldenDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    var fsrsScheduler = new Fsrs6Scheduler(fsrsWeights);
    string GoldenPath(string file) => Path.Combine(goldenDir, file);

    // —— 常量锁定：decay / factor 必须与 audit §3.2 ① 的实算值一致 ——
    Check(SchedulingConfig.Decay == -0.1542 &&
        Math.Abs(SchedulingConfig.Factor - 0.9803464944134797) < 1e-15,
        $"FSRS-6 派生常量正确（DECAY={SchedulingConfig.Decay:R}, FACTOR={SchedulingConfig.Factor:R}）");
    Check(Math.Abs(Fsrs6Model.FactorOf(fsrsW) - SchedulingConfig.Factor) < 1e-15 &&
        Fsrs6Model.DecayOf(fsrsW) == SchedulingConfig.Decay,
        "默认权重派生的 decay/factor 与 SchedulingConfig 常量逐位一致");

    // —— 任务 1：Fsrs6Weights 的默认值 / JSON 往返 / 合法性 ——
    Check(fsrsWeights.Source == Fsrs6Weights.SourceDefaults && fsrsWeights.OptimizedAtUtc is null &&
        Fsrs6Weights.Algorithm == "FSRS-6" && Fsrs6Weights.ParameterCount == 21 && Fsrs6Weights.ParameterVersion == 1,
        "Fsrs6Weights 默认实例的 algorithm/parameterCount/parameterVersion/source 符合依赖决策");
    Check(Fsrs6Weights.OfficialDefaults.SequenceEqual([
            0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001,
            1.8722, 0.1666, 0.796, 1.4835, 0.0614, 0.2629, 1.6483, 0.6014,
            1.8729, 0.5425, 0.0912, 0.0658, 0.1542]),
        "21 个官方默认权重与 audit §3.1 逐项一致（含 w20=0.1542）");
    Check(Fsrs6Weights.OfficialClipBounds.Length == 21 &&
        Fsrs6Weights.OfficialClipBounds[0] == (0.001, 100.0) && Fsrs6Weights.OfficialClipBounds[4] == (1.0, 10.0) &&
        Fsrs6Weights.OfficialClipBounds[15] == (0.0, 1.0) && Fsrs6Weights.OfficialClipBounds[16] == (1.0, 6.0) &&
        Fsrs6Weights.OfficialClipBounds[20] == (0.1, 0.8) && Fsrs6Weights.TryValidate([.. Fsrs6Weights.OfficialDefaults], out _),
        "21 项官方 clip 区间与 py-fsrs LOWER/UPPER_BOUNDS_PARAMETERS 一致且默认值全部在区间内");

    var fsrsJson = fsrsWeights.ToJson();
    Check(fsrsJson.Contains("\"algorithm\":\"FSRS-6\"") && fsrsJson.Contains("\"parameterCount\":21") &&
        fsrsJson.Contains("\"parameterVersion\":1") && fsrsJson.Contains("\"source\":\"defaults\"") &&
        fsrsJson.Contains("\"optimizedAtUtc\":null") && fsrsJson.Contains("\"weights\":"),
        "参数持久化 JSON 形状与 dependency_decision.md §参数持久化格式 的字段名一致（camelCase）");
    Check(Fsrs6Weights.TryLoad(fsrsJson, out var fsrsReloaded, out var fsrsLoadError) &&
        fsrsReloaded!.ValueEquals(fsrsWeights) && fsrsReloaded.ToJson() == fsrsJson,
        "Fsrs6Weights JSON 往返逐位一致（weights/source/optimizedAtUtc），错误=" + fsrsLoadError);
    var fsrsOptimized = Fsrs6Weights.Create([.. Fsrs6Weights.OfficialDefaults], Fsrs6Weights.SourceOptimized,
        new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
    Check(Fsrs6Weights.TryLoad(fsrsOptimized.ToJson(), out var fsrsReloaded2, out _) &&
        fsrsReloaded2!.ValueEquals(fsrsOptimized) &&
        fsrsReloaded2.OptimizedAtUtc == new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc),
        "带 optimizedAtUtc 的非默认 source 也能逐位往返");
    var fsrsClone = fsrsWeights.Clone();
    Check(fsrsClone.ValueEquals(fsrsWeights) && !ReferenceEquals(fsrsClone.ToArray(), fsrsWeights.ToArray()),
        "Fsrs6Weights.Clone 深拷贝权重数组");

    var fsrsInvalid = new (string Json, string Label)[]
    {
        (fsrsJson.Replace("\"parameterVersion\":1", "\"parameterVersion\":2"), "parameterVersion 不符"),
        (fsrsJson.Replace("\"parameterCount\":21", "\"parameterCount\":19"), "parameterCount 不符"),
        (fsrsJson.Replace("\"algorithm\":\"FSRS-6\"", "\"algorithm\":\"FSRS-5\""), "algorithm 不符"),
        (fsrsJson.Replace("\"source\":\"defaults\"", "\"source\":\"guessed\""), "source 非法"),
        (fsrsJson.Replace("0.1542]", "0.1542, 0.2]"), "权重个数不是 21"),
        (fsrsJson.Replace("0.212,", "\"NaN\","), "权重非有限值"),
        (fsrsJson.Replace("0.1542]", "0.05]"), "w20 低于 clip 下界 0.1"),
        ("not json at all", "不是 JSON"),
        ("", "空字符串"),
    };
    var fsrsRejected = fsrsInvalid.Count(candidate => !Fsrs6Weights.TryLoad(candidate.Json, out _, out _));
    Check(fsrsRejected == fsrsInvalid.Length,
        $"非法参数 JSON 全部被 TryLoad 拒绝（{fsrsRejected}/{fsrsInvalid.Length}）");
    Check(Fsrs6Weights.TryCreate([0.212, 1.2931], Fsrs6Weights.SourceDefaults, null, out _, out var fsrsShortError) == false &&
        fsrsShortError.Contains("21"),
        "TryCreate 对长度不符返回 false 并说明原因：" + fsrsShortError);

    // —— 任务 4(a)：官方 golden 692 行逐点回归 ——
    // 夹具来源 Overmiind/FSRS-Sharp Tests/Golden（见 Fixtures/PROVENANCE.md）。
    // 生产路径（steps 为空）只需 Fsrs6Model：D/S 轨迹与 learning steps 无关（下方参考运行器一节实测印证）。
    using var fsrsScenarioDoc = JsonDocument.Parse(File.ReadAllText(GoldenPath("scenarios.json")));
    var fsrsStart = DateTimeOffset.Parse(fsrsScenarioDoc.RootElement.GetProperty("start").GetString()!,
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime;
    var fsrsScenarios = fsrsScenarioDoc.RootElement.GetProperty("scenarios").EnumerateArray()
        .Select(s => (
            Name: s.GetProperty("name").GetString()!,
            Steps: s.GetProperty("steps").EnumerateArray()
                .Select(x => (Rating: x.GetProperty("rating").GetInt32(), Hours: x.GetProperty("offset_hours").GetDouble()))
                .ToList()))
        .ToList();

    var fsrsExpected = new Dictionary<(string, int), (int State, int? Step, double Stability, double Difficulty, double IntervalSeconds)>();
    foreach (var line in File.ReadAllLines(GoldenPath("expected.csv")))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var f = line.Split(',');
        fsrsExpected[(f[0], int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture))] = (
            int.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture),
            f[3].Length == 0 ? null : int.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture));
    }

    Check(fsrsExpected.Count == 692 && fsrsScenarios.Count == 64 &&
        fsrsScenarios.Sum(s => s.Steps.Count) == fsrsExpected.Count,
        $"golden 夹具完整：64 个场景 / {fsrsExpected.Count} 行，scenarios.json 与 expected.csv 行数自洽");
    Check(SchedulingConfig.MinimumIntervalAfterAgainDays == 1.0 &&
        SchedulingConfig.MinimumIntervalAfterHardDays == 1.0 && SchedulingConfig.MinimumIntervalDays == 1.0,
        "前提：三种评级的最小间隔下限都是 1 天，因此 golden 的 Easy 行可用 Known 口径比较 interval");

    var fsrsFailures = new List<string>();
    int fsrsRows = 0, fsrsSdHits = 0, fsrsReviewRows = 0, fsrsIntervalHits = 0;
    double fsrsMaxStabDiff = 0, fsrsMaxDiffDiff = 0, fsrsMaxIntervalDaysDiff = 0;
    foreach (var (name, steps) in fsrsScenarios)
    {
        var now = fsrsStart;
        double? stability = null, difficulty = null;
        DateTime? lastReview = null;
        for (var i = 0; i < steps.Count; i++)
        {
            now = now.AddHours(steps[i].Hours);
            var rating = steps[i].Rating;
            double nextStability, nextDifficulty;
            if (stability is null)
            {
                nextStability = Fsrs6Model.ClampStability(fsrsW[rating - 1]);
                nextDifficulty = Fsrs6Model.InitialDifficulty(fsrsW, rating);
            }
            else
            {
                var elapsedDays = Fsrs6Model.ElapsedWholeDays(lastReview!.Value, now);
                var retrievability = Fsrs6Model.Retrievability(stability.Value, elapsedDays);
                nextStability = elapsedDays == 0
                    ? Fsrs6Model.ShortTermStability(fsrsW, stability.Value, rating)
                    : rating == Fsrs6Model.RatingAgain
                        ? Fsrs6Model.NextStabilityFailure(fsrsW, difficulty!.Value, stability.Value, retrievability)
                        : Fsrs6Model.NextStabilitySuccess(fsrsW, difficulty!.Value, stability.Value, retrievability, rating);
                nextStability = Fsrs6Model.ClampStability(nextStability);
                nextDifficulty = Fsrs6Model.NextDifficulty(fsrsW, difficulty!.Value, rating);
            }
            stability = nextStability;
            difficulty = nextDifficulty;
            lastReview = now;

            if (!fsrsExpected.TryGetValue((name, i), out var want)) continue;
            fsrsRows++;
            var stabilityDiff = Math.Abs(stability.Value - want.Stability);
            var difficultyDiff = Math.Abs(difficulty.Value - want.Difficulty);
            fsrsMaxStabDiff = Math.Max(fsrsMaxStabDiff, stabilityDiff);
            fsrsMaxDiffDiff = Math.Max(fsrsMaxDiffDiff, difficultyDiff);
            var tolerance = 1e-6 * Math.Max(1.0, Math.Abs(want.Stability));
            if (stabilityDiff <= tolerance && difficultyDiff <= 1e-6 * Math.Max(1.0, Math.Abs(want.Difficulty)))
            {
                fsrsSdHits++;
            }
            else if (fsrsFailures.Count < 12)
            {
                fsrsFailures.Add($"{name}[{i}] rating={rating} S={stability.Value:R} want {want.Stability:R} (Δ={stabilityDiff:E3}); " +
                    $"D={difficulty.Value:R} want {want.Difficulty:R} (Δ={difficultyDiff:E3})");
            }

            // interval 只在 golden 的 Review 态行上比较：其余行排的是 learning/relearning step（60s/600s），
            // 属于生产路径明确不接管的轮内强化（用户需求 §6），不做静默跳过——计数在下方一并打印。
            if (want.State == Fsrs6ReferenceCard.StateReview)
            {
                fsrsReviewRows++;
                var floorRating = rating switch
                {
                    Fsrs6Model.RatingAgain => StudyRating.Forgot,
                    Fsrs6Model.RatingHard => StudyRating.Unsure,
                    _ => StudyRating.Known,
                };
                var intervalDays = MemoryScheduler.ApplyMinimumInterval(
                    Fsrs6Model.IntervalForRetention(stability.Value, SchedulingConfig.DesiredRetention),
                    floorRating, false);
                var wantDays = want.IntervalSeconds / 86400.0;
                fsrsMaxIntervalDaysDiff = Math.Max(fsrsMaxIntervalDaysDiff, Math.Abs(intervalDays - wantDays));
                if (Math.Abs(Math.Round(intervalDays) - wantDays) <= 1e-9) fsrsIntervalHits++;
                else if (fsrsFailures.Count < 12)
                {
                    fsrsFailures.Add($"{name}[{i}] interval={intervalDays:R} → {Math.Round(intervalDays)} want {wantDays:R}");
                }
            }
        }
    }

    foreach (var failure in fsrsFailures) Console.WriteLine("  golden 差异: " + failure);
    Console.WriteLine($"INFO: golden S/D 命中 {fsrsSdHits}/{fsrsRows}；interval(Review 行) 命中 {fsrsIntervalHits}/{fsrsReviewRows}；" +
        $"因依赖 learning/relearning steps 而跳过 interval 的行数 = {fsrsRows - fsrsReviewRows}（这些行的 S/D 仍然全部参与比较）；" +
        $"maxStabDiff={fsrsMaxStabDiff:E3} maxDiffDiff={fsrsMaxDiffDiff:E3} maxIntervalDaysDiff={fsrsMaxIntervalDaysDiff:E3}");
    Check(fsrsRows == 692 && fsrsSdHits == fsrsRows,
        $"golden 692 行逐点回归：stability/difficulty 命中 {fsrsSdHits}/{fsrsRows}");
    Check(fsrsIntervalHits == fsrsReviewRows && fsrsReviewRows == 399,
        $"golden Review 态行的 interval 命中 {fsrsIntervalHits}/{fsrsReviewRows}（另 {fsrsRows - fsrsReviewRows} 行依赖 learning steps，已显式跳过）");

    // —— 任务 4(a) 加强：官方参考状态机（learning_steps=[1min,10min]）逐列复现 692 行 ——
    int refRows = 0, refFullHits = 0;
    var refFailures = new List<string>();
    foreach (var (name, steps) in fsrsScenarios)
    {
        var refCard = new Fsrs6ReferenceCard();
        var refNow = fsrsStart;
        for (var i = 0; i < steps.Count; i++)
        {
            refNow = refNow.AddHours(steps[i].Hours);
            refCard = Fsrs6Reference.Review(fsrsW, refCard, steps[i].Rating, refNow,
                Fsrs6Reference.OfficialLearningSteps, Fsrs6Reference.OfficialRelearningSteps);
            if (!fsrsExpected.TryGetValue((name, i), out var want)) continue;
            refRows++;
            var intervalSeconds = (refCard.DueAtUtc - refNow).TotalSeconds;
            var ok = refCard.State == want.State && refCard.Step == want.Step &&
                Math.Abs(refCard.Stability!.Value - want.Stability) <= 1e-6 * Math.Max(1.0, Math.Abs(want.Stability)) &&
                Math.Abs(refCard.Difficulty!.Value - want.Difficulty) <= 1e-6 * Math.Max(1.0, Math.Abs(want.Difficulty)) &&
                Math.Abs(intervalSeconds - want.IntervalSeconds) <= 1e-3;
            if (ok) refFullHits++;
            else if (refFailures.Count < 12)
            {
                refFailures.Add($"{name}[{i}] state={refCard.State}/{want.State} step={refCard.Step?.ToString() ?? "-"}/{want.Step?.ToString() ?? "-"} " +
                    $"S={refCard.Stability!.Value:R}/{want.Stability:R} D={refCard.Difficulty!.Value:R}/{want.Difficulty:R} ivl={intervalSeconds}/{want.IntervalSeconds}");
            }
        }
    }
    foreach (var failure in refFailures) Console.WriteLine("  参考状态机差异: " + failure);
    Console.WriteLine($"INFO: 参考状态机全列命中 {refFullHits}/{refRows}（state + card_step + stability + difficulty + interval_seconds）");
    Check(refFullHits == refRows && refRows == 692,
        $"官方 py-fsrs 参考状态机（含 learning steps）在 692 行上 state/step/S/D/interval 五列全中：{refFullHits}/{refRows}");

    // —— 任务 4(b)：py-fsrs test_basic 官方间隔向量 ——
    // tests/test_basic.py::TestPyFSRS.test_review_card（v6.3.2, 9446cb06）；enable_fuzzing=False。
    var officialRatings = new[] { 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3 };
    var officialCard = new Fsrs6ReferenceCard();
    var officialAt = new DateTime(2022, 11, 29, 12, 30, 0, DateTimeKind.Utc);
    var officialHistory = new List<int>();
    foreach (var rating in officialRatings)
    {
        officialCard = Fsrs6Reference.Review(fsrsW, officialCard, rating, officialAt,
            Fsrs6Reference.OfficialLearningSteps, Fsrs6Reference.OfficialRelearningSteps);
        officialHistory.Add((int)Math.Floor((officialCard.DueAtUtc - officialAt).TotalDays));
        officialAt = officialCard.DueAtUtc;
    }
    Check(officialHistory.SequenceEqual([0, 2, 11, 46, 163, 498, 0, 0, 2, 4, 7, 12, 21]),
        "py-fsrs 官方向量 (Good×6, Again, Again, Good×5) 的 13 个间隔逐一相等：[" + string.Join(", ", officialHistory) + "]");

    // —— 任务 4(c)：§10 硬性要求 3 ——同日 Hard 不得降低稳定性 ——
    var hardLockOk = true;
    var hardLockDetail = "";
    foreach (var seed in new[] { 0.001, 0.01, 0.1, 0.212, 1.0, 2.3065, 10.0, 100.0, 1000.0, 36500.0 })
    {
        var hard = Fsrs6Model.ShortTermStability(fsrsW, seed, Fsrs6Model.RatingHard);
        var good = Fsrs6Model.ShortTermStability(fsrsW, seed, Fsrs6Model.RatingGood);
        var easy = Fsrs6Model.ShortTermStability(fsrsW, seed, Fsrs6Model.RatingEasy);
        if (!(hard >= seed - 1e-12 && good >= seed - 1e-12 && easy >= seed - 1e-12))
        {
            hardLockOk = false;
            hardLockDetail += $" S={seed:R}→Hard={hard:R}/Good={good:R}/Easy={easy:R};";
        }
    }
    Check(hardLockOk, "同日 Hard/Good/Easy 都不会降低稳定性（max(SInc,1.0) 对 G∈{2,3,4} 全部生效）" + hardLockDetail);
    Check(Fsrs6Model.ShortTermStability(fsrsW, 1.0, Fsrs6Model.RatingHard) == 1.0,
        "同日 Hard 的下限确实被触发（S=1 时 SInc≈0.6109<1，被抬到 S′==1.0 而非降到 0.6109）");
    Check(Fsrs6Model.ShortTermStability(fsrsW, 1.0, Fsrs6Model.RatingAgain) < 1.0,
        "对照：同日 Again 不受下限保护，稳定性必须下降（S′=" + Fsrs6Model.ShortTermStability(fsrsW, 1.0, Fsrs6Model.RatingAgain).ToString("R") + "）");

    var sameDayCard = new FsrsCardState
    {
        WordKey = "archive:same-day", Stability = 1.0, Difficulty = 5.0, State = FsrsState.Review,
        LastReviewAtUtc = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc),
        NextReviewAtUtc = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc), Reps = 1,
    };
    var sameDayOutcome = new Fsrs6Scheduler(fsrsWeights).Review(
        sameDayCard, StudyRating.Unsure, new DateTime(2026, 3, 1, 20, 0, 0, DateTimeKind.Utc));
    Check(sameDayOutcome.Card.Stability >= sameDayCard.Stability - 1e-12 &&
        sameDayOutcome.Card.Stability == 1.0,
        $"端到端同日 Hard（12h 后 Unsure）：S 不下降（{sameDayCard.Stability:R} → {sameDayOutcome.Card.Stability:R}）");

    // —— 任务 4(d)：单调性与边界 ——
    var monotonicOk = true;
    foreach (var stability in new[] { 0.001, 0.5, 2.3065, 37.0, 1000.0, 36500.0 })
    {
        if (Fsrs6Model.Retrievability(stability, 0) != 1.0) monotonicOk = false;
        var previous = 1.0;
        for (var day = 1; day <= 400; day += 7)
        {
            var current = Fsrs6Model.Retrievability(stability, day);
            if (!(current < previous)) { monotonicOk = false; break; }
            previous = current;
        }
    }
    Check(monotonicOk, "R(S,0)==1 且 R 随 elapsedDays 严格递减（S 覆盖 0.001..36500）");

    var intervalMonotonicOk = true;
    const double intervalStability = 12.5;
    var previousInterval = double.PositiveInfinity;
    for (var retention = 0.70; retention <= 0.99; retention += 0.01)
    {
        var interval = Fsrs6Model.IntervalForRetention(intervalStability, retention);
        if (!(interval < previousInterval)) { intervalMonotonicOk = false; break; }
        previousInterval = interval;
    }
    Check(intervalMonotonicOk, "IntervalForRetention 随 retention 严格递减");

    // 只在「反解结果不会被 S_MAX=36500 上限截断」的区间内断言往返：
    // fsrs-rs 的 next_interval_scalar 本身也 clamp 到 [0, S_MAX]，被截断时 R(I) > r 属预期边界（下一行单独锁定）。
    var roundTripOk = true;
    var roundTripDetail = "";
    var roundTripCases = 0;
    foreach (var retention in new[] { 0.75, 0.80, 0.85, 0.90, 0.95, 0.97 })
    {
        foreach (var stability in new[] { 0.001, 0.212, 2.3065, 37.0, 500.0 })
        {
            roundTripCases++;
            var interval = Fsrs6Model.IntervalForRetention(stability, retention);
            var back = Fsrs6Model.Retrievability(stability, interval);
            if (Math.Abs(back - retention) > 1e-6)
            {
                roundTripOk = false;
                roundTripDetail += $" S={stability:R},r={retention}: R(I)={back:R};";
            }
        }
    }
    Check(roundTripOk, $"R(S, I(S,r)) ≈ r（容差 1e-6，{roundTripCases} 组）" + roundTripDetail);
    Check(Fsrs6Model.IntervalForRetention(36500.0, 0.75) == SchedulingConfig.StabilityMax &&
        Fsrs6Model.Retrievability(36500.0, SchedulingConfig.StabilityMax) is > 0.899999 and < 0.900001,
        "S=36500 且 r<0.9 时反解超上界，按 fsrs-rs next_interval_scalar 截断到 S_MAX（此时 R(I) > r，属预期边界）");
    Check(Fsrs6Model.Retrievability(2.3065, Fsrs6Model.IntervalForRetention(2.3065, SchedulingConfig.DesiredRetention)) is > 0.899999 and < 0.900001,
        "R(S, IntervalForRetention(S, 0.90)) ≈ 0.90（生产 DesiredRetention）");

    // —— 任务 4(e)：合法性与 S0/D0 数值 ——
    Check(Fsrs6Model.InitialStability(fsrsW).SequenceEqual([0.212, 1.2931, 2.3065, 8.2956]),
        "S0(Again/Hard/Good/Easy) = [0.212, 1.2931, 2.3065, 8.2956]（audit §3.2 ③ 实测值）");
    var fsrsD0 = new[] { 1, 2, 3, 4 }.Select(r => Fsrs6Model.InitialDifficulty(fsrsW, r)).ToArray();
    Check(Math.Abs(fsrsD0[0] - 6.4133) < 1e-12 && Math.Abs(fsrsD0[1] - 5.112170705601055) < 1e-9 &&
        Math.Abs(fsrsD0[2] - 2.118103970459015) < 1e-9 && fsrsD0[3] == 1.0,
        "D0 对外值 = [6.4133, 5.112170705601055, 2.118103970459015, 1.0]（D0(4) 已 clamp）");
    var fsrsD0Raw4 = Fsrs6Model.InitialDifficultyRaw(fsrsW, Fsrs6Model.RatingEasy);
    Check(Math.Abs(fsrsD0Raw4 - (-4.771630703161737)) < 1e-9,
        $"§10 硬性要求 1：D0(4) 未截断值 = {fsrsD0Raw4:R}（均值回归用它，对外才 clamp）");
    var fsrsMeanReversion = Fsrs6Model.NextDifficulty(fsrsW, 5.0, Fsrs6Model.RatingGood);
    var fsrsWithClampedTarget = fsrsW[7] * 1.0 + (1.0 - fsrsW[7]) * (5.0 + (10.0 - 5.0) * (-(fsrsW[6] * 0)) / 9.0);
    Check(Math.Abs(fsrsMeanReversion - fsrsWithClampedTarget) > 1e-6,
        $"均值回归确实使用未截断的 D0(4)（正确={fsrsMeanReversion:R}，若误用 clamp 后的 1.0 会得到 {fsrsWithClampedTarget:R}）");

    var fsrsRangeOk = true;
    var fsrsRangeDetail = "";
    foreach (var rating in new[] { 1, 2, 3, 4 })
    {
        var s = Fsrs6Model.InitialStability(fsrsW)[rating - 1];
        var d = Fsrs6Model.InitialDifficulty(fsrsW, rating);
        for (var step = 0; step < 40; step++)
        {
            var r = Fsrs6Model.Retrievability(s, step % 5 == 0 ? 0 : 1 + step * 3);
            var g = (step * 7 + rating) % 4 + 1;
            var useShortTerm = step % 5 == 0;
            s = useShortTerm ? Fsrs6Model.ShortTermStability(fsrsW, s, g)
                : g == Fsrs6Model.RatingAgain ? Fsrs6Model.NextStabilityFailure(fsrsW, d, s, r)
                : Fsrs6Model.NextStabilitySuccess(fsrsW, d, s, r, g);
            s = Fsrs6Model.ClampStability(s);
            d = Fsrs6Model.NextDifficulty(fsrsW, d, g);
            if (!(MemoryScheduler.IsFinite(s) && s >= SchedulingConfig.StabilityMin && s <= SchedulingConfig.StabilityMax))
            { fsrsRangeOk = false; fsrsRangeDetail += $" S={s:R}@rating{rating}/step{step};"; }
            if (!(MemoryScheduler.IsFinite(d) && d is >= 1.0 and <= 10.0))
            { fsrsRangeOk = false; fsrsRangeDetail += $" D={d:R}@rating{rating}/step{step};"; }
        }
    }
    Check(fsrsRangeOk, "160 步混合推进后 S 恒在 [0.001, 36500]、D 恒在 [1,10]" + fsrsRangeDetail);

    // —— 任务 4(f)：时间轴整数化（§6.2 记录的坑） ——
    var fsrsAxisStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    Check(Fsrs6Model.ElapsedWholeDays(fsrsAxisStart, fsrsAxisStart.AddDays(3.875)) == 3 &&
        Fsrs6Model.ElapsedWholeDays(fsrsAxisStart, fsrsAxisStart.AddDays(10.58)) == 10 &&
        Fsrs6Model.ElapsedWholeDays(fsrsAxisStart, fsrsAxisStart.AddMinutes(600)) == 0,
        "小数日一律整天截断（3.875→3、10.58→10、600s→0），不四舍五入");
    Check(Fsrs6Model.ElapsedWholeDays(fsrsAxisStart, fsrsAxisStart.AddTicks(3 * TimeSpan.TicksPerDay - 1)) == 2 &&
        Fsrs6Model.ElapsedWholeDays(fsrsAxisStart, fsrsAxisStart.AddTicks(3 * TimeSpan.TicksPerDay)) == 3,
        "整天边界按整数 ticks 判定：差 1 tick 即为 2 天，恰好 3 天即为 3 天");
    var fsrsAccumulated = fsrsAxisStart;
    for (var i = 0; i < 720; i++) fsrsAccumulated = fsrsAccumulated.AddHours(0.1);
    double fsrsFloatDays = 0;
    for (var i = 0; i < 720; i++) fsrsFloatDays += 0.1 / 24.0;
    Console.WriteLine($"INFO: 720×0.1h 累加 → 整数 ticks 口径 {(fsrsAccumulated - fsrsAxisStart).Ticks / (double)TimeSpan.TicksPerDay:R} 天" +
        $"（ElapsedWholeDays={Fsrs6Model.ElapsedWholeDays(fsrsAxisStart, fsrsAccumulated)}）；" +
        $"同一条时间轴若按浮点「天」累加 = {fsrsFloatDays:R} 天（≠ 3）");
    Check(Fsrs6Model.ElapsedWholeDays(fsrsAxisStart, fsrsAccumulated) == 3 && fsrsFloatDays != 3.0,
        "720×0.1h 累加后整数 ticks 口径恰好得到 3 天，而浮点「天」累加已漂移（§6.2 的坑，故实现禁用浮点天累加）");

    // 端到端：3.875 天的间隔必须按 3 天参与 R 计算（截断而非四舍五入、也不保留小数）
    var fsrsTruncCard = new FsrsCardState
    {
        WordKey = "archive:truncate", Stability = 20.0, Difficulty = 5.0, Reps = 1, State = FsrsState.Review,
        LastReviewAtUtc = fsrsAxisStart, NextReviewAtUtc = fsrsAxisStart.AddDays(20),
    };
    var fsrsTruncOutcome = fsrsScheduler.Review(fsrsTruncCard, StudyRating.Known, fsrsAxisStart.AddDays(3.875));
    Check(fsrsTruncOutcome.BaselineRetrievability == Fsrs6Model.Retrievability(20.0, 3) &&
        fsrsTruncOutcome.BaselineRetrievability != Fsrs6Model.Retrievability(20.0, 4) &&
        fsrsTruncOutcome.BaselineRetrievability != Fsrs6Model.Retrievability(20.0, 3.875),
        "端到端：3.875 天的间隔在排期器里按 3 整天参与 R 计算（既不四舍五入到 4，也不保留小数）");
    var fsrsAxisScenario = fsrsScenarios.Single(s => s.Name == "learn_graduate_integral");
    var fsrsAxisNow = fsrsStart;
    DateTime? fsrsAxisLast = null;
    var fsrsAxisElapsed = new List<long>();
    foreach (var step in fsrsAxisScenario.Steps)
    {
        fsrsAxisNow = fsrsAxisNow.AddHours(step.Hours);
        if (fsrsAxisLast is { } fsrsAxisPrevious) fsrsAxisElapsed.Add(Fsrs6Model.ElapsedWholeDays(fsrsAxisPrevious, fsrsAxisNow));
        fsrsAxisLast = fsrsAxisNow;
    }
    Check(fsrsAxisElapsed.SequenceEqual([0L, 1L, 3L, 10L, 30L]),
        "golden 场景 learn_graduate_integral 的整数天序列 = [0,1,3,10,30]（首行是初始化，无 elapsed）");

    // —— 任务 4(g)：纯函数 / 不修改入参 / 结果可复现 ——
    var fsrsPureCard = new FsrsCardState
    {
        WordKey = "archive:pure", Difficulty = 5.5, Stability = 9.0, Reps = 4, Lapses = 1,
        State = FsrsState.Review, LastAppliedCanonicalSeq = 42,
        LastReviewAtUtc = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
        NextReviewAtUtc = new DateTime(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc),
        LastCanonicalRating = StudyRating.Known,
    };
    var fsrsPureBefore = fsrsPureCard.Clone();
    var fsrsPureAt = new DateTime(2026, 2, 12, 9, 0, 0, DateTimeKind.Utc);
    var first = fsrsScheduler.Review(fsrsPureCard, StudyRating.Unsure, fsrsPureAt);
    var second = fsrsScheduler.Review(fsrsPureCard, StudyRating.Unsure, fsrsPureAt);
    Check(fsrsPureCard.Difficulty == fsrsPureBefore.Difficulty && fsrsPureCard.Stability == fsrsPureBefore.Stability &&
        fsrsPureCard.Reps == fsrsPureBefore.Reps && fsrsPureCard.State == fsrsPureBefore.State &&
        fsrsPureCard.NextReviewAtUtc == fsrsPureBefore.NextReviewAtUtc &&
        fsrsPureCard.LastCanonicalRating == fsrsPureBefore.LastCanonicalRating,
        "Review 不修改入参 card");
    Check(!ReferenceEquals(first.Card, fsrsPureCard) && !ReferenceEquals(first.Card, second.Card),
        "Review 返回全新卡片对象（不复用入参、不复用上一次结果）");
    Check(first.Card.Stability == second.Card.Stability && first.Card.Difficulty == second.Card.Difficulty &&
        first.Card.NextReviewAtUtc == second.Card.NextReviewAtUtc &&
        first.BaselineIntervalDays == second.BaselineIntervalDays &&
        first.BaselineRetrievability == second.BaselineRetrievability &&
        first.BaselineDueAtUtc == second.BaselineDueAtUtc && first.Card.State == second.Card.State &&
        first.Card.Reps == second.Card.Reps && first.Card.Lapses == second.Card.Lapses,
        "同输入调两次逐字段完全一致（幂等/纯函数）");
    Check(first.Card.WordKey == "archive:pure" && first.Card.LastAppliedCanonicalSeq == 42,
        "Review 不改写 WordKey 与 LastAppliedCanonicalSeq（持久化层的身份与幂等水位）");

    // —— 生产路径：初始化 / 状态映射 / 版本戳 / 最小间隔 / 时钟注入 ——
    var fsrsNewAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
    var fsrsNewKnown = fsrsScheduler.Review(null, StudyRating.Known, fsrsNewAt);
    Check(fsrsNewKnown.Card.State == FsrsState.Review && fsrsNewKnown.Card.Reps == 1 && fsrsNewKnown.Card.Lapses == 0 &&
        fsrsNewKnown.Card.Stability == 2.3065 && Math.Abs(fsrsNewKnown.Card.Difficulty - 2.118103970459015) < 1e-9 &&
        fsrsNewKnown.Card.WordKey == "",
        "新卡 Known：State=Review、Reps=1、Lapses=0、S=S0(Good)=2.3065、D=D0(Good)");
    Check(Math.Abs(fsrsNewKnown.BaselineIntervalDays - 2.3065) < 1e-12 &&
        fsrsNewKnown.Card.NextReviewAtUtc == fsrsNewAt.AddDays(2.3065) &&
        fsrsNewKnown.Card.NextReviewAtUtc!.Value.Kind == DateTimeKind.Utc &&
        fsrsNewKnown.BaselineDueAtUtc == fsrsNewKnown.Card.NextReviewAtUtc,
        "新卡 Known：基线间隔 = I(S0,0.90) = S0 = 2.3065 天，NextReviewAtUtc == BaselineDueAtUtc 且为 UTC");
    Check(fsrsNewKnown.BaselineRetrievability == 1.0,
        "初始化时 BaselineRetrievability 约定为 1.0（无上一次复习，R 不参与任何公式）");

    var fsrsNewForgot = fsrsScheduler.Review(null, StudyRating.Forgot, fsrsNewAt);
    var fsrsNewUnsure = fsrsScheduler.Review(null, StudyRating.Unsure, fsrsNewAt);
    Check(fsrsNewForgot.Card.State == FsrsState.Relearning && fsrsNewForgot.Card.Lapses == 1 &&
        fsrsNewUnsure.Card.State == FsrsState.Learning && fsrsNewUnsure.Card.Lapses == 0,
        "新卡 Forgot→Relearning(Lapses=1)、Unsure→Learning(Lapses=0)");
    Check(fsrsNewForgot.Card.Stability == 0.212 && fsrsNewForgot.BaselineIntervalDays == SchedulingConfig.MinimumIntervalDays,
        "新卡 Forgot：S=S0(Again)=0.212，基线间隔被既有 ApplyMinimumInterval 抬到 1 天上限（0.212→1.0）");
    Check(fsrsNewKnown.Card.FsrsAlgorithmVersion == SchedulingConfig.AlgorithmVersion &&
        fsrsNewKnown.Card.FsrsLibraryVersion == SchedulingConfig.LibraryVersion &&
        fsrsNewKnown.Card.FsrsParameterVersion == SchedulingConfig.ParameterVersion &&
        fsrsNewKnown.Card.LastCanonicalRating == StudyRating.Known &&
        fsrsNewKnown.Card.LastReviewAtUtc == fsrsNewAt,
        "三个版本戳 + LastCanonicalRating + LastReviewAtUtc 全部写入");

    var fsrsClock = new DateTime(2031, 7, 4, 6, 30, 0, DateTimeKind.Utc);
    var fsrsClockScheduler = new Fsrs6Scheduler(fsrsWeights, () => fsrsClock);
    var fsrsClockOutcome = fsrsClockScheduler.ReviewAtNow(null, StudyRating.Known);
    Check(fsrsClockScheduler.UtcNow == fsrsClock && fsrsClockOutcome.Card.LastReviewAtUtc == fsrsClock &&
        fsrsClockOutcome.Card.NextReviewAtUtc == fsrsClock.AddDays(2.3065),
        "注入时钟生效：ReviewAtNow 以注入的 UTC 时刻为基准（Review 本身不读时钟）");

    // —— 失败模式：非法卡片状态必须抛 InvalidOperationException，不静默返回垃圾值 ——
    int fsrsThrew = 0;
    foreach (var broken in new[]
    {
        new FsrsCardState { WordKey = "archive:x", State = FsrsState.Review, Stability = 0, Difficulty = 5 },
        new FsrsCardState { WordKey = "archive:x", State = FsrsState.Review, Stability = double.NaN, Difficulty = 5 },
        new FsrsCardState { WordKey = "archive:x", State = FsrsState.Review, Stability = 5, Difficulty = 0 },
        new FsrsCardState { WordKey = "archive:x", State = FsrsState.Review, Stability = 5, Difficulty = 11 },
    })
    {
        try { fsrsScheduler.Review(broken, StudyRating.Known, fsrsNewAt); }
        catch (InvalidOperationException) { fsrsThrew++; }
    }
    Check(fsrsThrew == 4, $"4 种非法卡片状态（S=0/NaN、D=0/11）全部抛 InvalidOperationException（{fsrsThrew}/4）");

    // —— 生产排期器的 R / I 接口与模型口径一致 ——
    Check(fsrsScheduler.Retrievability(10.0, 10.0) is > 0.899999 and < 0.900001 &&
        fsrsScheduler.IntervalForRetention(10.0, 0.9) is > 9.999999 and < 10.000001,
        "IMemoryScheduler.Retrievability / IntervalForRetention 与 §3.2 ①② 公式一致（R(S,S)=0.9）");
}

Console.WriteLine("All learning tests passed.");
