using System.Reflection;
using System.Text.Json;
using Lexi;
using Lexi.Social;

if (args.Contains("--ui-config", StringComparer.Ordinal))
{
    var failures = new List<string>(); var focusedChecks = 0;
    SocialUiConfigTests.Run((value, message) => { focusedChecks++; if (!value) failures.Add(message); });
    Console.WriteLine($"UI config focused: {focusedChecks} assertions, {failures.Count} failures");
    if (failures.Count != 0) throw new Exception(string.Join("; ", failures));
    return;
}
if (args.Contains("--runtime-fix", StringComparer.Ordinal))
{
    var failures = new List<string>(); var focusedChecks = 0;
    await SocialRuntimeFixTests.Run((value, message) => { focusedChecks++; if (!value) failures.Add(message); });
    Console.WriteLine($"Runtime fix focused: {focusedChecks} assertions, {failures.Count} failures");
    if (failures.Count != 0) throw new Exception(string.Join("; ", failures));
    return;
}
var checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
void Reject(Action action, string message)
{
    checks++;
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception(message);
}
DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
var date = new DateOnly(2026, 10, 10);
var learned = At("2026-10-10T15:55:00Z");
SocialPlanTarget Target(string id, int total, int done, bool enabled = true) => new(id, enabled, total, done, learned);
var first = Target("a", 20, 20); var second = Target("b", 10, 6);
Check(!PlanDayEligibilityRules.Evaluate([first, second], QualificationCause.LearningComplete).Qualified,
    "one unfinished enabled plan must prevent qualification");
var stopped = PlanDayEligibilityRules.Evaluate([first, second with { Enabled = false }], QualificationCause.PlanChange);
Check(stopped.Qualified && stopped.Target == 20 && stopped.Completed == 20 && stopped.Cause == QualificationCause.PlanChange,
    "stopping a plan must immediately remove its target, with a plan-change cause");
Check(!PlanDayEligibilityRules.Evaluate([first, second], QualificationCause.PlanChange).Qualified,
    "resuming the unfinished plan must revoke today's qualification");
Check(!PlanDayEligibilityRules.Evaluate([first with { Enabled = false }], QualificationCause.PlanChange).Qualified,
    "stopping every plan must never produce a zero-target success");
Check(!PlanDayEligibilityRules.Evaluate([], QualificationCause.LearningComplete).Qualified, "no plans are not a success");
Check(!PlanDayEligibilityRules.Evaluate([Target("zero", 0, 0)], QualificationCause.LearningComplete).Qualified,
    "zero target is not a success");
Check(PlanDayEligibilityRules.Evaluate([first, Target("zero", 0, 0)], QualificationCause.LearningComplete).Qualified,
    "zero-target plan must not block a real complete plan");
Check(!PlanDayEligibilityRules.Evaluate([first with { Target = 21 }], QualificationCause.PlanChange).Qualified,
    "increasing the actual today target must revoke qualification");
Reject(() => PlanDayEligibilityRules.Evaluate([Target("bad", -1, 0)], QualificationCause.LearningComplete), "negative targets rejected");
Reject(() => PlanDayEligibilityRules.Evaluate([Target("bad", 2, 3)], QualificationCause.LearningComplete), "over-counting rejected");
Reject(() => PlanDayEligibilityRules.Evaluate([first, first], QualificationCause.LearningComplete), "duplicate plan IDs rejected");
var latest = PlanDayEligibilityRules.Evaluate([first, Target("b", 6, 6) with { LastLearnedAt = learned.AddSeconds(2) }], QualificationCause.LearningComplete);
Check(latest.QualifiedAt == learned.AddSeconds(2), "qualification timestamp comes from the last actual plan completion");

// Exercise the real existing plan rules: a completed tail batch remains today's target.
var words = Enumerable.Range(0, 3).Select(i => new DailyStudyPlanWord { Id = "w" + i, Word = "word" + i }).ToList();
var plan = DailyStudyPlanRules.Create("tail", DailyStudyPlanSource.Archive, "test", words, 20, false, 7);
Check(PlanDayEligibilityRules.FromPlan(plan, date, learned).Target == 3, "short tail uses the actual batch size");
foreach (var word in words) Check(DailyStudyPlanRules.CompleteWord(plan, word.Id, date), "real learning completes a plan word");
var tail = PlanDayEligibilityRules.FromPlan(plan, date, learned);
Check(tail.Enabled && tail.Target == 3 && tail.Completed == 3, "completed status retains today's completed tail batch");
Check(PlanDayEligibilityRules.Evaluate([tail], QualificationCause.LearningComplete).Qualified, "completed tail qualifies today");
Check(!PlanDayEligibilityRules.Evaluate([PlanDayEligibilityRules.FromPlan(plan, date.AddDays(1), null)], QualificationCause.LearningComplete).Qualified,
    "yesterday's finished plan must not qualify a new day");
var excluded = words.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
Check(PlanDayEligibilityRules.FromPlan(plan, date, learned, excluded).Target == 0, "pre-binding completed work cannot be uploaded as new qualification");

Check(SocialDayRules.DateAt(At("2026-10-10T15:59:59Z")) == date, "23:59:59 belongs to the old Beijing day");
Check(SocialDayRules.DateAt(At("2026-10-10T16:00:00Z")) == date.AddDays(1), "00:00 opens the next Beijing day");
Check(SocialDayRules.JustEndedDate(At("2026-10-10T16:00:00Z")) == date, "midnight evaluates the just-ended day");
Check(SocialDayRules.Deadline(date) == At("2026-10-13T16:00:00Z"), "offline deadline is 72 hours after day end");
Check(SocialDayRules.AcceptsReceipt(date, At("2026-10-13T15:59:59Z")), "receipt just before deadline remains eligible");
Check(!SocialDayRules.AcceptsReceipt(date, At("2026-10-13T16:00:00Z")), "receipt at deadline cannot restore an old run");
Check(!SocialDayRules.AcceptsReceipt(date, At("2026-10-09T15:59:59Z")), "future-day record cannot be accepted early");
Check(SocialDayRules.CanFinalizeMissing(date, At("2026-10-13T16:00:00Z")), "server may finalize at the grace deadline");
Check(!SocialDayRules.CanFinalizeMissing(date, At("2026-10-10T16:00:00Z")), "unknown offline records prevent immediate midnight failure");
var noon = At("2026-10-10T12:00:00Z");
SocialCompletion Completion(string instant, QualificationCause cause = QualificationCause.LearningComplete) =>
    new(date, At(instant), cause);
var alive = new SocialPairView
{
    // A live run must be present, otherwise the pair is legitimately reported as not yet started.
    RunId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
    FriendName = "小明", Streak = 28, Today = date, CloudSuccess = true,
    SuccessDate = date, SelfDone = true, FriendDone = true,
    Self = Completion("2026-10-10T04:00:00Z"),
    Friend = Completion("2026-10-10T08:00:00Z")
};
Check(SocialCopyPolicy.Select(alive, noon).Kind == SocialCopyKind.Success, "ordinary confirmed completion stays ordinary");
Check(SocialCopyPolicy.Select(alive with { CloudExtremeConfirmed = true }, learned).Kind != SocialCopyKind.Extreme,
    "opening or synchronizing late must not turn an earlier completion into extreme");
var extreme = alive with { CloudExtremeConfirmed = true, Friend = Completion("2026-10-10T15:50:00Z") };
Check(SocialCopyPolicy.Select(extreme, learned).Kind == SocialCopyKind.Extreme, "real last completion at 23:50 is extreme after cloud confirmation");
Check(SocialCopyPolicy.Select(extreme with { Friend = Completion("2026-10-10T15:49:59Z") }, learned).Kind != SocialCopyKind.Extreme,
    "23:49:59 is outside the extreme window");
Check(SocialCopyPolicy.Select(extreme with { Friend = Completion("2026-10-10T15:59:59Z") }, At("2026-10-10T15:59:59Z")).Kind == SocialCopyKind.Extreme,
    "23:59:59 is inside the extreme window");
Check(SocialCopyPolicy.Select(extreme with { Friend = Completion("2026-10-10T16:00:00Z") }, At("2026-10-10T16:00:00Z")).Kind != SocialCopyKind.Extreme,
    "00:00 completion cannot extend the previous day");
Check(SocialCopyPolicy.Select(extreme with { Friend = Completion("2026-10-10T15:55:00Z", QualificationCause.PlanChange) }, learned).Kind != SocialCopyKind.Extreme,
    "plan change at 23:55 cannot trigger a learning extreme");
Check(SocialCopyPolicy.Select(extreme with { CloudSuccess = false }, learned).Kind != SocialCopyKind.Extreme,
    "an unconfirmed pair can never announce extreme success");
Check(SocialCopyPolicy.Select(extreme with { Sync = SocialSyncStatus.Pending }, learned).Kind == SocialCopyKind.Pending,
    "a pending newer local revision overrides cached success");
Check(SocialCopyPolicy.Select(extreme with { Sync = SocialSyncStatus.Failed }, learned).Kind == SocialCopyKind.SyncFailed,
    "failed transmission must not announce success");
Check(SocialCopyPolicy.Select(extreme with { BackfillConfirmed = true, Streak = 30 }, learned).Kind == SocialCopyKind.Extreme,
    "extreme outranks backfill and milestones");
Check(SocialCopyPolicy.Select(alive with { BackfillConfirmed = true, Streak = 30 }, noon).Kind == SocialCopyKind.Arrived,
    "confirmed offline arrival outranks milestones");
foreach (var milestone in new[] { 3, 7, 30, 100, 365 })
    Check(SocialCopyPolicy.Select(alive with { Streak = milestone }, noon).Kind == SocialCopyKind.Milestone,
        $"confirmed {milestone}-day milestone is selected");
Check(SocialCopyPolicy.Select(alive with { Streak = 1 }, noon).Kind == SocialCopyKind.FirstDay, "first day precedes ordinary success");
var waiting = alive with { CloudSuccess = false, FriendDone = false, Friend = null, Self = Completion("2026-10-10T15:55:00Z") };
Check(SocialCopyPolicy.Select(waiting, learned).Kind == SocialCopyKind.SelfExtremeWaiting,
    "one late learner can only wait, never claim pair success");
Check(SocialCopyPolicy.Select(waiting with { Self = Completion("2026-10-10T04:00:00Z") }, learned).Kind == SocialCopyKind.WaitingFriend,
    "a completed self must not get a nighttime nag");
var notDone = alive with { CloudSuccess = false, SelfDone = false, FriendDone = false, Self = null, Friend = null };
foreach (var (instant, expected) in new[]
{
    ("2026-10-10T14:59:59Z", SocialCopyKind.NeitherDone),
    ("2026-10-10T15:00:00Z", SocialCopyKind.Night2300),
    ("2026-10-10T15:29:59Z", SocialCopyKind.Night2300),
    ("2026-10-10T15:30:00Z", SocialCopyKind.Night2330),
    ("2026-10-10T15:49:59Z", SocialCopyKind.Night2330),
    ("2026-10-10T15:50:00Z", SocialCopyKind.Night2350),
    ("2026-10-10T15:59:59Z", SocialCopyKind.Night2350),
    ("2026-10-10T16:00:00Z", SocialCopyKind.NeitherDone)
}) Check(SocialCopyPolicy.Select(notDone with { Today = SocialDayRules.DateAt(At(instant)) }, At(instant)).Kind == expected,
    $"night reminder boundary {instant}");
Check(SocialCopyPolicy.Select(notDone with { Friend = alive.Friend }, noon).Kind == SocialCopyKind.WaitingSelf,
    "friend complete prompts only unfinished self");
Check(SocialCopyPolicy.Select(notDone with { Lifecycle = PairLifecycle.Broken }, noon).Text.Contains("28"),
    "break text uses actual last streak");
Check(SocialCopyPolicy.Select(notDone with { Lifecycle = PairLifecycle.Ended }, noon).Kind == SocialCopyKind.Ended,
    "relationship end cannot be confused with a daily miss");
Check(SocialCopyPolicy.FriendList(alive).Continuity.Contains("小明") && SocialCopyPolicy.FriendList(alive).Continuity.Contains("28"),
    "friend list substitutes the actual friend and independent streak");
// A day the server reports as done with no qualified instant is a real, legally stored state
// (first binding day, or a completion at or before BoundAt). It must read as a confirmed
// success, never as "neither has started", and it must never be dressed up as an extreme.
var untimed = alive with { SelfDone = true, FriendDone = true, Self = null, Friend = null, CloudExtremeConfirmed = true };
Check(SocialCopyPolicy.Select(untimed, noon).Kind == SocialCopyKind.Success,
    "a complete-but-untimed day is an ordinary confirmed success, not an unfinished day");
Check(SocialCopyPolicy.Select(untimed, noon).Kind != SocialCopyKind.Extreme,
    "an untimed day can never be presented as a last-minute save");
Check(SocialCopyPolicy.FriendList(untimed).Today == "今日双双存活",
    "an untimed but confirmed day still counts for both in the friend list");
Check(SocialCopyPolicy.FriendList(alive with { SelfDone = false, Self = null }).Today != "今日双双存活",
    "clearing only the done flag is enough to stop counting the day");
// The legal no-current-run payload: the server cannot report success without an active run,
// so the fixture must not either.
var notStarted = alive with
{
    RunId = null, Streak = 0, CloudSuccess = false, SuccessDate = null,
    SelfDone = false, FriendDone = false, Self = null, Friend = null
};
Check(SocialCopyPolicy.FriendList(notStarted).Continuity.Contains("0 天等待"),
    "a relation whose first run has not opened is reported as a zero-day wait");
Check(SocialCopyPolicy.FriendList(notStarted).Today != "今日双双存活",
    "a runless pair is never reported as a joint success");
Check(SocialCopyPolicy.Select(notStarted, noon).Kind == SocialCopyKind.NeitherDone,
    "a runless pair with neither side finished is an ordinary untouched day");

Check(SocialCopyPolicy.FriendList(waiting).Today == "等待队友救援", "friend list has no success for one completion");
Check(SocialCopyPolicy.FriendList(notDone).Today == "今日尚未续命", "friend list has no success for no completions");
Check(SocialCopyPolicy.Select(extreme with
{
    Self = Completion("2026-10-10T15:50:00Z"),
    Friend = Completion("2026-10-10T15:50:00Z", QualificationCause.PlanChange)
}, learned).Kind != SocialCopyKind.Extreme, "a tied last plan-change event must not become an extreme learning event");
Check(SocialCopyPolicy.FriendList(alive with { Friend = Completion("2026-10-11T04:00:00Z") }).Today != "今日双双存活",
    "friend list rejects a completion timestamp outside its stated day");
var oauth = new SocialOAuthFlow();
var begin = oauth.Begin(new Uri("https://abcdefghijklmnopqrst.supabase.co"), noon);
Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
var query = Query(begin.AuthorizationUri);
Check(begin.AuthorizationUri.Scheme == "https" && begin.AuthorizationUri.AbsolutePath == "/auth/v1/authorize" && query["provider"] == "github",
    "desktop auth uses the intended HTTPS Supabase GitHub endpoint");
Check(query["code_challenge_method"] == "s256" && query["code_challenge"].Length == 43,
    "desktop auth uses a SHA256 PKCE challenge");
Check(query["scopes"] == "user:email", "GitHub login never requests repository access");
Check(!query.ContainsKey("code_verifier") && !query.ContainsKey("client_secret"), "browser URL carries no verifier or secret");
var redirect = new Uri(query["redirect_to"]);
var flowId = Query(redirect)["flow_id"];
Check(!oauth.TryConsumeCallback(new Uri("https://attacker.invalid/auth/callback?flow_id=" + flowId + "&code=fake"), noon, out _, out _),
    "foreign callback scheme is rejected without consuming the pending login");
Check(!oauth.TryConsumeCallback(new Uri("com.lexi.app://evil/callback?flow_id=" + flowId + "&code=fake"), noon, out _, out _),
    "wrong callback host is rejected");
Check(!oauth.TryConsumeCallback(new Uri("com.lexi.app://auth/callback?flow_id=wrong&code=fake"), noon, out _, out _),
    "uncorrelated callback cannot consume a login");
Check(!oauth.TryConsumeCallback(new Uri("com.lexi.app://auth/callback?flow_id=" + flowId + "&code=a&code=b"), noon, out _, out _),
    "ambiguous duplicate auth codes are rejected");
var callback = new Uri("com.lexi.app://auth/callback?flow_id=" + flowId + "&code=test-authorization-code");
Check(oauth.TryConsumeCallback(callback, noon.AddMinutes(1), out var exchange, out _) && exchange is not null,
    "correlated callback produces exactly one exchange request");
Check(exchange!.Code == "test-authorization-code" && exchange.Verifier.Length >= 43,
    "exchange uses the authorization code and original private verifier");
var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(exchange.Verifier)))
    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
Check(challenge == query["code_challenge"], "exchange verifier matches the challenge actually sent to Supabase");
Check(!exchange.ToString()!.Contains(exchange.Code) && !exchange.ToString()!.Contains(exchange.Verifier),
    "diagnostic formatting must not disclose auth code or verifier");
Check(!oauth.TryConsumeCallback(callback, noon.AddMinutes(1), out _, out _), "consumed callback cannot be replayed");
var another = oauth.Begin(new Uri("https://abcdefghijklmnopqrst.supabase.co"), noon);
Check(Query(another.AuthorizationUri)["code_challenge"] != query["code_challenge"], "every login uses fresh cryptographic entropy");
var anotherId = Query(new Uri(Query(another.AuthorizationUri)["redirect_to"]))["flow_id"];
Check(!oauth.TryConsumeCallback(new Uri("com.lexi.app://auth/callback?flow_id=" + anotherId + "&code=fake"), noon.AddMinutes(10), out _, out _),
    "expired local login cannot consume callbacks");
oauth.Cancel();
Check(!oauth.TryConsumeCallback(callback, noon, out _, out _), "logout or cancel invalidates a pending login");
Reject(() => oauth.Begin(new Uri("http://ynoideqstwrvavbafyzm.supabase.co"), noon), "unencrypted auth destinations rejected");
Reject(() => oauth.Begin(new Uri("https://supabase.co.attacker.invalid"), noon), "lookalike auth destinations rejected");
var localDays = new[]
{
    new SocialLocalDay(date.AddDays(-2), JointDayKnowledge.Success, 120, 10),
    new SocialLocalDay(date.AddDays(-1), JointDayKnowledge.Success, null, 20),
    new SocialLocalDay(date.AddDays(-3), JointDayKnowledge.NonJoint, 0, 0),
    new SocialLocalDay(date.AddDays(-4), JointDayKnowledge.NonJoint, 60, 5),
    new SocialLocalDay(date.AddDays(-5), JointDayKnowledge.Pending, 9999, 999),
    new SocialLocalDay(date.AddDays(-6), JointDayKnowledge.Unknown, 9999, 999),
    new SocialLocalDay(date, JointDayKnowledge.Success, 9999, 999)
};
var comparison = SocialStatistics.Build(localDays, date);
Check(comparison.JointDays == 2 && comparison.NonJointDays == 2, "comparison excludes today, pending, and unknown dates");
Check(comparison.Words.Joint.Total == 30 && comparison.Words.Joint.Days == 2 && comparison.Words.Joint.Mean == 15,
    "joint word totals sum daily distinct words as word-days, with their own coverage denominator");
Check(comparison.Seconds.Joint.Total == 120 && comparison.Seconds.Joint.Days == 1 && comparison.Seconds.Joint.Mean == 120,
    "missing historical duration is absent, not a fabricated zero");
Check(comparison.Words.NonJoint.Mean == 2.5 && comparison.Seconds.NonJoint.Mean == 30,
    "known zero-activity baseline days belong in the default denominator");
Check(comparison.Words.MeanDifference == 12.5 && comparison.Words.PercentDifference == 500 && comparison.Seconds.PercentDifference == 300,
    "differences use means of the two groups with their respective valid denominators");
var active = SocialStatistics.Build(localDays, date, activeOnly: true);
Check(active.NonJointDays == 1 && active.Words.NonJoint.Mean == 5 && active.Seconds.NonJoint.Mean == 60,
    "optional active-only comparison excludes a known inactive day");
var filtered = SocialStatistics.Build(localDays, date, from: date.AddDays(-2));
Check(filtered.JointDays == 2 && filtered.NonJointDays == 0 && filtered.Words.PercentDifference is null,
    "window start is inclusive and an absent baseline has no percentage");
var zeroBaseline = SocialStatistics.Build([localDays[0], localDays[2]], date);
Check(zeroBaseline.Words.MeanDifference == 10 && zeroBaseline.Words.PercentDifference is null,
    "zero baseline keeps the absolute difference but has no percentage");
var missing = SocialStatistics.Build([localDays[0] with { Words = null, ActiveSeconds = null }], date);
Check(missing.Words.Joint.Total is null && missing.Seconds.Joint.Mean is null,
    "all-missing metrics are unavailable rather than zero");
Reject(() => SocialStatistics.Build([localDays[0], localDays[0]], date), "duplicate local day cannot double count");
Reject(() => SocialStatistics.Build([localDays[0] with { ActiveSeconds = double.NaN }], date), "nonfinite duration rejected");
Reject(() => SocialStatistics.Build([localDays[0] with { Words = -1 }], date), "negative words rejected");
SocialDirectoryTests.Run(Check);
SocialCurveTests.Run(Check);
SocialWeekTests.Run(Check);
AvatarCodecTests.Run(Check);
SocialStoreTests.Run(Check);
await SocialRuntimeTests.Run(Check);
await SocialCoordinatorTests.Run(Check);
await SocialRuntimeFixTests.Run(Check);
await SocialTransportTests.Run(Check);
var socialClock=new SocialClock();
Check(!socialClock.IsTrusted(noon,TimeSpan.Zero),"new process has no trusted time anchor");
socialClock.Observe(noon,noon,TimeSpan.FromSeconds(10));
Check(socialClock.IsTrusted(noon.AddHours(1),TimeSpan.FromHours(1)+TimeSpan.FromSeconds(10)),"offline monotonic interval retains trusted time");
Check(!socialClock.IsTrusted(noon.AddHours(3),TimeSpan.FromHours(1)+TimeSpan.FromSeconds(10)),"wall clock jump revokes extreme trust");
Check(!socialClock.IsTrusted(noon.AddDays(4),TimeSpan.FromDays(4)+TimeSpan.FromSeconds(10)),"time anchor has bounded offline validity");
Check(!socialClock.IsTrusted(noon,TimeSpan.FromSeconds(9)),"monotonic rewind invalidates anchor");
Console.WriteLine($"Social rules passed: {checks} assertions");
