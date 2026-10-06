using Lexi;
using System.Text.Json;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
LearningWord W(string text) => new() { Id = text, Words = [text] };
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
Console.WriteLine("All learning tests passed.");
