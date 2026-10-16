using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lexi.Social;
using Lexi.Core;
using System.Text.Json;

namespace Lexi;
internal static class SocialUiTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var exit = 0; var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        try
        {
            var navigation = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "NavSocial");
            Check(navigation is not null, "social navigation must exist");
            navigation!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
            var page = window.GetVisualDescendants().OfType<ScrollViewer>().SingleOrDefault(c => c.Name == "SocialPage");
            Check(page?.IsVisible == true, "social navigation opens private page host");
            Check(page!.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden && page.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, "the social page hides its vertical scrollbar while keeping vertical scrolling enabled");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialChromeLogin"), "explicit Chrome login action available");
            var field = typeof(MainWindow).GetField("_socialClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(field?.GetValue(window) is null, "automated run has no production transport or Keychain session");
            var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
            // Content inside a collapsed Expander is part of the logical tree but not the visual
            // tree, so text assertions walk the logical tree instead.
            static IEnumerable<string> LogicalTexts(Avalonia.LogicalTree.ILogical root)
            {
                foreach (var child in root.LogicalChildren)
                {
                    if (child is TextBlock block && block.Text is { } text) yield return text;
                    foreach (var nested in LogicalTexts(child)) yield return nested;
                }
            }
            static IEnumerable<Button> LogicalButtons(Avalonia.LogicalTree.ILogical root)
            {
                foreach (var child in root.LogicalChildren)
                {
                    if (child is Button button) yield return button;
                    foreach (var nested in LogicalButtons(child)) yield return nested;
                }
            }
            object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
            void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
            var actor = Guid.NewGuid(); var bindingId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            var offline = new OfflineHandler(); using var http = new HttpClient(offline);
            var secrets = new MemorySecrets();
            secrets.Set(JsonSerializer.Serialize(new { project = "https://uitest.supabase.co", session = Session(actor, now) }));
            var client = new SupabaseSocialClient(new(new Uri("https://uitest.supabase.co"), "sb_publishable_testonly"), secrets, http, () => DateTimeOffset.UtcNow);
            var store = new SocialStore(folder); var boundAt = now.AddDays(-1); var date = SocialDayRules.DateAt(now);
            store.PrepareBinding(actor, bindingId, "Fixture", boundAt, new Dictionary<string, IReadOnlySet<string>>(), true);
            store.ActivateBinding(actor, bindingId, boundAt, SocialDayRules.DateAt(boundAt));
            store.ConfirmBindingBaseline(actor, bindingId, boundAt, new Dictionary<string, IReadOnlySet<string>>());
            Set("_socialClient", client); Set("_socialStore", store); Set("_socialSync", new SocialSyncCoordinator(store, client));
            Call("RenderSocialEntry");
            var entryButtons = LogicalButtons(window).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
            Check(entryButtons.Contains("SocialPickAvatar"), "a signed-in profile renders its avatar action");
            Check(entryButtons.Contains("SocialCreateInvite") && entryButtons.Contains("SocialClaimInvite"), "a linked profile renders both invite creation and redemption actions");
            Call("RenderSocialEntry");
            entryButtons = LogicalButtons(window).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
            Check(entryButtons.Contains("SocialPickAvatar") && entryButtons.Contains("SocialCreateInvite") && entryButtons.Contains("SocialClaimInvite"), "re-rendering preserves the profile and both invite actions without reparenting failures");
            var plan = DailyStudyPlanRules.Create("Fixture", DailyStudyPlanSource.Ielts, "fixture", [new() { Id = "fixture", Word = "fixture" }], 1, false, 7);
            Set("_studyPlans", new List<DailyStudyPlan> { plan });
            Check((bool)Call("SaveStudyPlans")!, "ordinary plan save succeeds");
            Check(store.LoadDay(actor, date)?.Facts.Cause == QualificationCause.Correction, "durable ordinary plan save observes correction");
            Call("StopStudyPlan", plan);
            Check(store.LoadDay(actor, date)?.Facts.Cause == QualificationCause.PlanChange, "durable stop observes plan change");
            Check((bool)Call("CommitStudyPlanResume", plan)!, "plan resumes durably");
            Check(store.LoadDay(actor, date)?.Facts.Cause == QualificationCause.PlanChange, "durable resume observes plan change");
            Call("OpenStudyPlanAdjustment", plan);
            window.FindControl<TextBox>("PlanNameInput")!.Text = "Adjusted fixture";
            Call("SaveStudyPlanAdjustment");
            Check(store.LoadDay(actor, date)?.Facts.Cause == QualificationCause.PlanChange && ((List<DailyStudyPlan>)typeof(MainWindow).GetField("_studyPlans", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Single().Name == "Adjusted fixture", "actual successful plan adjustment observes plan change after durable list replacement");
            var previous = store.LoadDay(actor, date);
            Set("_planReadFailed", true);
            Check(!(bool)Call("SaveStudyPlans")! && JsonSerializer.Serialize(store.LoadDay(actor, date)) == JsonSerializer.Serialize(previous), "failed save never observes new social facts");
            Set("_planReadFailed", false);
            var archive = (IVocabularyArchive)typeof(MainWindow).GetField("_vocabService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var learningSecrets = typeof(VocabularyService).GetField("_secretStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(archive);
            Check(learningSecrets is not MacKeychainSecretStore and not WindowsSecretStore, "automated app learning configuration never accesses native secure stores");
            archive.AddWord("socialdurablefixture", "", "fixture", "fixture"); Call("RefreshWords");
            var word = archive.GetAllWords().Single(w => w.Word == "socialdurablefixture");
            plan = DailyStudyPlanRules.Create("Learning fixture", DailyStudyPlanSource.Archive, "fixture", [new() { Id = word.Id.ToString(), Word = word.Word }], 1, false, 7);
            Set("_studyPlans", new List<DailyStudyPlan> { plan }); Call("SaveStudyPlans");
            var unknownDuration = store.LoadSecondaryPending(actor, DateTimeOffset.UtcNow).Single(b => b.Kind == "duration");
            using (var json = JsonDocument.Parse(unknownDuration.Payload))
                Check(json.RootElement.GetProperty("p_seconds").ValueKind == JsonValueKind.Null && json.RootElement.GetProperty("p_coverage").GetString() == "unavailable", "missing durable activity retains unknown seconds instead of uploading zero");
            Call("StartStudyPlan", plan);
            await (Task)Call("CompleteFocusLearnAsync")!;
            var round = (StudyRound<string>)typeof(MainWindow).GetField("_focusRound", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            // Block only the JSON destination, keeping _planReadFailed=false to exercise real I/O failure.
            var deliveryPath = Path.Combine(folder, "daily-study-plans.json"); var preservedDelivery = deliveryPath + ".fixture-preserved";
            var blockedDelivery = false;
            for (var guard = 0; guard < 12 && !round.IsFinished; guard++)
            {
                if (!blockedDelivery && round.CurrentStreak == round.CurrentTarget - 1)
                { File.Move(deliveryPath, preservedDelivery); Directory.CreateDirectory(deliveryPath); blockedDelivery = true; }
                await (Task)Call("RatePlanFocusedWordAsync", StudyRating.Known)!;
                if (plan.CompletedWordIds.Count > 0) break;
                await (Task)Call("AdvanceFocusAsync")!;
            }
            Check(plan.CompletedWordIds.Count == 1, "canonical finalized completion awaits failed JSON delivery");
            Check(store.LoadDay(actor, date)?.Summary.Completed == 0, "canonical alone never projects undelivered JSON progress");
            var pendingMemory = (ILearningMemoryStore)typeof(MainWindow).GetField("_memoryStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(blockedDelivery && !(bool)typeof(MainWindow).GetField("_planReadFailed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! && pendingMemory.LoadPendingMutations().Count > 0, "real JSON I/O failure retains journal without the read-failed guard");
            var independentActivity = (List<StudyActivity>)typeof(MainWindow).GetField("_statisticsPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            independentActivity.Add(new("social-pending-journal-activity", StudyActivityKind.FirstLearn, now.AddSeconds(-1).UtcDateTime, now.UtcDateTime, 1));
            await (Task)Call("FlushStatisticsActivityAsync")!;
            Check(store.LoadDay(actor, date)?.Summary.Completed == 0 && store.LoadDay(actor, date)?.Summary.NewWords == 0 && pendingMemory.LoadPendingMutations().Count > 0, "successful independent activity append cannot project an undelivered plan journal or flush learning storage");
            Directory.Delete(deliveryPath); File.Move(preservedDelivery, deliveryPath);
            Call("MemoryTryCompletePending");
            var learned = store.LoadDay(actor, date);
            Check(learned?.Facts.Cause == QualificationCause.LearningComplete && learned.Summary.Completed == 1, "journal replay projects durable first learning with actual cause");
            var history = (IReadOnlyList<CanonicalHistoryRow>)Call("SocialHistory")!;
            Check(learned!.Summary.QualifiedAt == history.Max(r => SocialFactProjection.Utc(r.CompletedAtUtc)), "qualification time is canonical completion time");
            Check(!learned.Summary.TimeTrusted, "learning before clock anchor cannot create trusted extreme");
            offline.Status = JsonSerializer.Serialize(new { server_now = DateTimeOffset.UtcNow, binding = new { binding_id = bindingId, bound_at = boundAt, first_day = SocialDayRules.DateAt(boundAt).ToString("yyyy-MM-dd") } });
            await (Task)Call("SocialRefreshAsync", actor)!;
            Check(store.LoadDay(actor, date)?.Summary.Cause == QualificationCause.LearningComplete, "routine refresh preserves durable learning cause without startup recovery");
            await (Task)Call("UndoPlanFocusAsync")!;
            Check(store.LoadDay(actor, date)?.Summary.NewWords == 0 && store.LoadDay(actor, date)?.Summary.ReviewWords == 0, "durable undo removes invalidated canonical from social counts immediately");
            await (Task)Call("RatePlanFocusedWordAsync", StudyRating.Known)!;
            await (Task)Call("ReclassifyPlanFocusAsync")!;
            Check(store.LoadDay(actor, date)?.Summary.NewWords == 0 && store.LoadDay(actor, date)?.Summary.ReviewWords == 0, "durable reclassification removes invalidated canonical from social counts immediately");
            var pendingActivities = (List<StudyActivity>)typeof(MainWindow).GetField("_statisticsPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            pendingActivities.Add(new("social-activity-fixture", StudyActivityKind.FirstLearn, now.AddSeconds(-60).UtcDateTime, now.UtcDateTime, 60));
            await (Task)Call("FlushStatisticsActivityAsync")!;
            var duration = store.LoadSecondaryPending(actor, DateTimeOffset.UtcNow).Single(b => b.Kind == "duration");
            using (var json = JsonDocument.Parse(duration.Payload))
                Check(json.RootElement.GetProperty("p_seconds").ValueKind == JsonValueKind.Number && json.RootElement.GetProperty("p_seconds").GetDouble() >= 61, "durable foreground activity queues actual seconds");
            pendingActivities.Add(new("social-shutdown-fixture", StudyActivityKind.FirstLearn, now.AddSeconds(-30).UtcDateTime, now.UtcDateTime, 30));
            Call("StatisticsStopTracking");
            var shutdownDuration = store.LoadSecondaryPending(actor, DateTimeOffset.UtcNow).Single(b => b.Kind == "duration");
            using (var json = JsonDocument.Parse(shutdownDuration.Payload))
                Check(json.RootElement.GetProperty("p_seconds").GetDouble() >= 91 && pendingActivities.Count == 0 && new StudyActivityStore(Path.Combine(folder, "study-activity.json")).Read().Activities.Any(a => a.SessionId == "social-shutdown-fixture"), "shutdown observes successfully appended tracker data (seconds=" + json.RootElement.GetProperty("p_seconds").GetDouble() + ", pending=" + pendingActivities.Count + ")");
            Call("SocialSessionReady");
            offline.Status = JsonSerializer.Serialize(new { server_now = DateTimeOffset.UtcNow, binding = new { binding_id = bindingId, bound_at = boundAt, first_day = SocialDayRules.DateAt(boundAt).ToString("yyyy-MM-dd") } });
            await (Task)Call("SocialRefreshAsync", actor)!;
            Check(!(bool)typeof(MainWindow).GetField("_socialRecoveryPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! && store.GetSecondarySharing(actor) == new SocialSecondarySharing(false, false), "restored session resumes binding once with initial own sharing all off");
            var oauth = (SocialOAuthFlow)typeof(MainWindow).GetField("_socialOAuth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Set("_wordFocusActive", false); Set("_planCardActive", false);
            Call("RenderSocialEntry");
            var cancellation = (CancellationTokenSource)typeof(MainWindow).GetField("_socialCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var oldGeneration = (long)typeof(MainWindow).GetField("_socialGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            // Cancel must fence an exchange that has already consumed its one-use callback.
            Set("_socialClient", null); Call("RenderSocialEntry");
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SocialCancelLogin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(cancellation.IsCancellationRequested && (long)typeof(MainWindow).GetField("_socialGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! > oldGeneration, "cancel login cancels in-flight exchange and fences late responses");
            Set("_socialClient", client);
            var activation = typeof(App).GetMethod("SocialNativeActivated", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(activation is not null, "native OpenUri activation routes OAuth callbacks");
            var app = (App)Avalonia.Application.Current!;
            var emptySecrets = new MemorySecrets(); var delayed = new DelayedAuthHandler(actor);
            using var authHttp = new HttpClient(delayed);
            var authClient = new SupabaseSocialClient(new(new Uri("https://uitest.supabase.co"), "sb_publishable_testonly"), emptySecrets, authHttp, () => DateTimeOffset.UtcNow);
            client.Dispose(); Set("_socialClient", authClient); Call("RenderSocialEntry");
            var start = oauth.Begin(new Uri("https://uitest.supabase.co"), DateTimeOffset.UtcNow);
            var redirect = new Uri(start.AuthorizationUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p[0] == "redirect_to").Select(p => Uri.UnescapeDataString(p[1])).Single());
            var callback = new Uri("com.lexi.app://auth/callback" + redirect.Query + "&code=synthetic");
            Set("_socialLoginPending", true);
            typeof(App).GetField("_socialActivationWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, null);
            activation!.Invoke(app, [new ProtocolActivatedEventArgs(callback)]);
            await Task.Delay(30);
            Check(authClient.UserId is null && emptySecrets.Get() is null, "callback before window readiness does not exchange or persist session");
            typeof(App).GetMethod("SocialWindowReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [window]);
            await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SocialCancelLogin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            delayed.Release.TrySetResult(); await Task.Delay(100);
            Check(authClient.UserId is null && emptySecrets.Get() is null, "cancelled native callback cannot publish late Auth response");
            activation.Invoke(app, [new ProtocolActivatedEventArgs(callback)]); await Task.Delay(30);
            Check(authClient.UserId is null, "unsolicited callback without process PKCE is rejected");
            typeof(App).GetField("_socialActivationWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, null);
            typeof(App).GetField("_socialPendingCallback", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, null);
            activation.Invoke(app, [new ProtocolActivatedEventArgs(new Uri("com.lexi.app://auth/callback?code=" + new string('x', 8192)))]); await Task.Delay(30);
            Check(typeof(App).GetField("_socialPendingCallback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app) is null, "oversized native callback is rejected before pending storage");
            activation.Invoke(app, [new ProtocolActivatedEventArgs(callback)]); await Task.Delay(30);
            typeof(App).GetField("_socialPendingAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, DateTimeOffset.UtcNow.AddMinutes(-3));
            typeof(App).GetMethod("SocialWindowReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [window]); await Task.Delay(30);
            Check(typeof(App).GetField("_socialPendingCallback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app) is null && authClient.UserId is null, "expired native callback is consumed without exchange or session publication");
            var foreignSecrets = new MemorySecrets();
            foreignSecrets.Set(JsonSerializer.Serialize(new { project = "https://uitest.supabase.co", session = Session(Guid.NewGuid(), DateTimeOffset.UtcNow) }));
            var foreignClient = new SupabaseSocialClient(new(new Uri("https://uitest.supabase.co"), "sb_publishable_testonly"), foreignSecrets, http, () => DateTimeOffset.UtcNow);
            authClient.Dispose(); Set("_socialClient", foreignClient); Call("RenderSocialEntry"); Call("ShowPage", "social");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialCreateIndependentProfile"), "foreign-owner block offers executable independent profile creation and restart");
            var body = (StackPanel)typeof(MainWindow).GetField("_socialBody", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(!body.Children.OfType<Expander>().Any() && !window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialSaveConfiguration")
                && !window.GetVisualDescendants().OfType<TextBox>().Any(b => b.Name is "SocialProjectUrl" or "SocialPublicKey"),
                "the signed-in social entry never exposes public connection configuration controls");
            var failLate = new TaskCompletionSource();
            var staleButton = (Button)Call("SocialButton", "fixture", "SocialStaleFixture", new Func<Task>(async () => { await failLate.Task; throw new IOException("fixture"); }))!;
            staleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Call("SocialCancelOperations");
            var message = (TextBlock)typeof(MainWindow).GetField("_socialMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            message.Text = "current-generation"; failLate.SetResult(); await Task.Delay(50);
            Check(message.Text == "current-generation", "late action failure cannot replace a newer generation message");
            var logoutClient = foreignClient;
            var filesBefore = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(p => !Path.GetFileName(p).StartsWith("social", StringComparison.Ordinal) && !p.EndsWith("-shm") && !p.EndsWith("-wal") && !p.EndsWith(".lock")).ToDictionary(p => p, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
            var queueBeforeLogout = JsonSerializer.Serialize(store.LoadPending(actor));
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SocialLogout").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
            Check(logoutClient.UserId is null && foreignSecrets.Get() is null && JsonSerializer.Serialize(store.LoadPending(actor)) == queueBeforeLogout && filesBefore.All(p => File.Exists(p.Key) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "logout clears only fake Social session and preserves learning files plus A queue");
            var profileOnlyHandler = new DirectoryHandler
            {
                OwnProfileId = actor,
                OwnNickname = "Saved cloud nickname",
                OwnAvatarPath = $"{actor:D}/saved-avatar.webp"
            };
            using var profileOnlyHttp = new HttpClient(profileOnlyHandler);
            var profileOnlySecrets = new MemorySecrets();
            profileOnlySecrets.Set(JsonSerializer.Serialize(new { project = "https://uitest.supabase.co", session = Session(actor, DateTimeOffset.UtcNow) }));
            var profileOnlyClient = new SupabaseSocialClient(new(new Uri("https://uitest.supabase.co"), "sb_publishable_testonly"), profileOnlySecrets, profileOnlyHttp, () => DateTimeOffset.UtcNow);
            var profileOnlyStore = new SocialStore(Path.Combine(folder, "profile-restore-fixture"));
            Set("_socialClient", profileOnlyClient);
            Set("_socialStore", profileOnlyStore);
            Set("_socialSync", new SocialSyncCoordinator(profileOnlyStore, profileOnlyClient));
            Call("RenderSocialEntry");
            await (Task)Call("SocialRefreshAsync", actor)!;
            var restoredProfile = (SocialProfileView?)typeof(MainWindow).GetField("_socialProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            var restoredNickname = (TextBox)typeof(MainWindow).GetField("_socialNicknameBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var restoredAvatar = typeof(MainWindow).GetField("_socialAvatar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            Check(profileOnlyHandler.Requests.ContainsKey("lexi_profile") && restoredProfile?.Nickname == "Saved cloud nickname",
                "an unbound local dataset still reads the signed-in account's saved cloud profile");
            Check(restoredNickname.Text == "Saved cloud nickname" && profileOnlyHandler.AvatarReads == 1 && restoredAvatar is not null,
                "the restored profile nickname and private avatar are loaded without a local binding");
            Check(!profileOnlyHandler.Requests.ContainsKey("lexi_bind") && !profileOnlyHandler.Requests.ContainsKey("lexi_status"),
                "restoring account profile never binds or uploads the current learning dataset");
            profileOnlyClient.Dispose();
            Set("_socialClient", client);
            Set("_socialStore", store);
            Set("_socialSync", new SocialSyncCoordinator(store, client));
            Set("_socialAvatar", null);
            await SocialProfileProcessTests.RunAsync(Check);
            // ---------------- friends / invitations / sharing / avatar ----------------
            var directory = new DirectoryHandler();
            using var directoryHttp = new HttpClient(directory);
            var directorySecrets = new MemorySecrets();
            directorySecrets.Set(JsonSerializer.Serialize(new { project = "https://uitest.supabase.co", session = Session(actor, DateTimeOffset.UtcNow) }));
            var directoryClient = new SupabaseSocialClient(new(new Uri("https://uitest.supabase.co"), "sb_publishable_testonly"), directorySecrets, directoryHttp, () => DateTimeOffset.UtcNow);
            Set("_socialClient", directoryClient);
            Set("_socialSync", new SocialSyncCoordinator(store, directoryClient));
            Set("_socialAvatar", null);
            // The signed-in workspace hosts only exist after the entry is rendered for this account.
            Call("RenderSocialEntry");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialSaveNickname"), "a signed-in entry renders the profile and friend workspace");
            directory.FriendName = "小明"; directory.Streak = 28; directory.ShareTime = true;
            var generation = (long)typeof(MainWindow).GetField("_socialGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, generation])!;

            Check(directory.Requests.ContainsKey("lexi_friends") && directory.Requests.ContainsKey("lexi_invites") && directory.Requests.ContainsKey("lexi_profile"),
                "the directory load actually reached all three read RPCs");
            var cachedFriends = (System.Collections.IEnumerable?)typeof(MainWindow).GetField("_socialFriends", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            Check(cachedFriends is not null && cachedFriends.Cast<object>().Count() == 1, "a decoded friend list is cached for the current account");
            var rendered = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Check(rendered.Any(t => t.Contains("小明") && t.Contains("28")), "the friend card shows the cloud-confirmed continuity and name");
            Check(store.GetSecondarySharing(actor).Time && store.GetSecondarySharing(actor).Curve, "the own aggregate follows own switches once the friend list is parsed");

            // Revoking one direction must stop secondary upload immediately.
            directory.ShareTime = false;
            var revoke = (Dictionary<string, string>)directory.Requests;
            revoke.Clear();
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, generation])!;
            Check(!store.GetSecondarySharing(actor).Time && store.GetSecondarySharing(actor).Curve, "clearing one own switch turns that secondary direction off");

            // A failed directory load must revoke the aggregate rather than keep a stale one.
            directory.Fail = true;
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, generation])!;
            Check(!store.GetSecondarySharing(actor).Time && !store.GetSecondarySharing(actor).Curve, "a failed directory load suspends both secondary directions");
            Check(typeof(MainWindow).GetField("_socialFriends", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null, "a failed directory load drops the cached friend view");
            directory.Fail = false;

            // A reply from a superseded generation must never populate the view.
            var staleGeneration = (long)typeof(MainWindow).GetField("_socialGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Set("_socialGeneration", staleGeneration + 1);
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, staleGeneration])!;
            Check(typeof(MainWindow).GetField("_socialFriends", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null, "a late reply from a superseded generation cannot repopulate the friend view");
            Set("_socialGeneration", staleGeneration);
            // Recover the directory for the current generation before exercising writes.
            directory.Fail = false;
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, staleGeneration])!;
            Check(typeof(MainWindow).GetField("_socialFriends", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is not null,
                "a later confirmed load repopulates the friend view");

            // Sharing writes send exactly the three confirmed switches and adopt the reply.
            revoke.Clear();
            var coordinator = (SocialSyncCoordinator)typeof(MainWindow).GetField("_socialSync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var relationship = ((System.Collections.IEnumerable)typeof(MainWindow).GetField("_socialFriends", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Cast<SocialFriendView>().First().RelationshipId;
            var confirmedSharing = await coordinator.SetSharingAsync(actor, relationship, true, false, true);
            Check(directory.LastBody!.Contains("\"p_counts\":true") && directory.LastBody.Contains("\"p_curve\":false") && directory.LastBody.Contains("\"p_time\":true"),
                "setting sharing sends all three directions explicitly");
            Check(!confirmedSharing.Curve && confirmedSharing.Time, "the confirmed reply is what the caller adopts");

            // Invitations: creation is local entropy, the reply drives the shown state.
            revoke.Clear();
            var created = await coordinator.CreateInviteAsync(actor, new string('a', 64));
            Check(created.State == SocialInviteState.Pending && created.ExpiresAt > DateTimeOffset.UtcNow, "a created invitation carries its real expiry");
            Check(directory.LastBody!.Contains(new string('a', 64)), "the locally generated code is what the server hashes");
            var claimed = await coordinator.ClaimInviteAsync(actor, new string('b', 64));
            Check(claimed.Nickname == "小明", "claiming previews the inviting nickname before any acceptance");
            var answered = await coordinator.AnswerInviteAsync(actor, Guid.NewGuid(), true);
            Check(answered.State == SocialInviteState.Accepted && answered.RelationshipId is not null, "accepting an invitation returns the new relationship");

            // ---- invitation book is shown in both directions, from the confirmed reply only ----
            var book = await coordinator.InvitesAsync(actor);
            Check(book.Incoming.Count == 1 && book.Outgoing.Count == 1, "both directions of the invitation book decode");
            Check(book.Incoming[0].State == SocialInviteState.Pending && book.Outgoing[0].State == SocialInviteState.Expired,
                "each invitation keeps its own decoded state");
            var inbox = new StackPanel();
            var invitesField = typeof(MainWindow).GetField("_socialInvites", BindingFlags.Instance | BindingFlags.NonPublic);
            invitesField!.SetValue(window, book);
            Call("RenderSocialInvitePanel", actor);
            var renderedInvites = LogicalTexts(window).ToList();
            Check(renderedInvites.Any(t => t.Contains("来自") && t.Contains("待你回应")), "an incoming invitation is listed with its own state");
            Check(renderedInvites.Any(t => t.Contains("发给") && t.Contains("已过期")), "an outgoing invitation is listed with its own state");
            Check(LogicalButtons(window).Any(b => b.Name == "SocialAcceptInvite"),
                "a pending incoming invitation offers an explicit accept action");
            invitesField.SetValue(window, null);

            // ---- reopening a run: only a broken run offers it, and it reports the other side ----
            Call("RenderSocialFriends", actor, (System.Collections.IEnumerable)typeof(MainWindow).GetField("_socialFriends", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            Check(!window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialRestartRun"),
                "a live run offers no reopen control");
            directory.Broken = true;
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, staleGeneration])!;
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialRestartRun"),
                "a broken run offers the reopen control");
            revoke.Clear();
            var restart = await coordinator.RestartAsync(actor, relationship, true);
            Check(restart.State == SocialRestartState.Waiting && restart.RunId is null,
                "requesting a reopen reports that the other side still has to agree");
            Check(directory.Requests.ContainsKey("lexi_restart"), "reopening actually calls the restart RPC");
            directory.Broken = false;
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, staleGeneration])!;

            // ---- ending a relationship asks inside the app before calling the RPC ----
            revoke.Clear();
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SocialEndRelationship").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            Check(!directory.Requests.ContainsKey("lexi_end_relationship"),
                "the first end-relationship action only asks for confirmation and sends nothing");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialConfirmEndRelationship"),
                "the in-app confirmation offers an explicit confirm action");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialCancelEndRelationship"),
                "the in-app confirmation can be cancelled");
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SocialCancelEndRelationship").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            Check(!window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SocialConfirmEndRelationship"),
                "cancelling the confirmation drops the pending action");
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SocialEndRelationship").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SocialConfirmEndRelationship").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            Check(directory.Requests.ContainsKey("lexi_end_relationship"),
                "confirming actually calls the end-relationship RPC");
            Check(store.LoadJoint(actor, relationship).Days.Count == 0 && store.LoadJoint(actor, relationship).CoverageFrom is null,
                "ending a relationship clears its cached joint days rather than leaving stale history");
            directory.Broken = false;
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, staleGeneration])!;

            // ---- a background refresh must not replace a field the user is editing ----
            var nicknameField = (TextBox)typeof(MainWindow).GetField("_socialNicknameBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            nicknameField.Text = "half typed";
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, staleGeneration])!;
            Check(ReferenceEquals(nicknameField, typeof(MainWindow).GetField("_socialNicknameBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)),
                "a background refresh reuses the same nickname field instead of rebuilding it");
            Check(nicknameField.Text == "half typed",
                "a background refresh never overwrites text the user has typed");
            var codeField = (TextBox)typeof(MainWindow).GetField("_socialInviteCodeBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            codeField.Text = "half typed code";
            Call("RenderSocialInvitePanel", actor);
            Check(ReferenceEquals(codeField, typeof(MainWindow).GetField("_socialInviteCodeBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)) && codeField.Text == "half typed code",
                "re-rendering the invitation panel reuses the code field");

            // ---- friend detail: curves, weekly charts and the comparison ----
            // The friend card is rendered directly into the visual tree, so a plain walk finds it.
            var openDetail = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SocialOpenDetail");
            Check(openDetail is not null, "the friend card offers a real detail navigation");
            openDetail!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(400);
            var overlays = window.GetVisualDescendants().OfType<SocialCurveOverlay>().ToArray();
            var weeklyCharts = window.GetVisualDescendants().OfType<SocialWeeklyChart>().ToArray();
            Check(overlays.Length == 1, "the detail shows exactly one curve overlay");
            Check(weeklyCharts.Length == 2, "the detail shows the duration plot and the word-count plot separately");
            Check(weeklyCharts.All(c => c.Days.Count == 7), "both weekly plots cover seven finished Beijing days");
            Check(weeklyCharts.SelectMany(c => c.Days).All(d => d.Date < SocialDayRules.DateAt(DateTimeOffset.UtcNow)),
                "the weekly plots never include today or a future day");
            Check(overlays[0].Series.Count >= 1 && overlays[0].Series[0].IsSelf,
                "the legend leads with the current user's own curve");
            Check(overlays[0].Series.Select(s => s.Color).Distinct().Count() == overlays[0].Series.Count,
                "every curve in the legend gets its own colour");
            var detailText = LogicalTexts(window).ToList();
            Check(detailText.Any(t => t.Contains("朋友们的记忆曲线")), "the curve section is labelled");
            Check(detailText.Any(t => t.Contains("不做记忆能力排名")), "the page states that curves are not a ranking");
            Check(detailText.Any(t => t.Contains("缺失或未授权")), "the weekly section states that missing days are not zero");
            Check(detailText.Any(t => t.Contains("一起苟过")), "the lifetime joint-day figure is shown");
            Check(detailText.Any(t => t.Contains("最近 90 个完整学习日")), "the comparison windows are offered");
            Check(detailText.Any(t => t.Contains("不是因果结论")), "the comparison explicitly disclaims a causal reading");
            // A reply that includes the server's own today must still render the whole comparison.
            Check(detailText.Any(t => t.Contains("一共苟过")), "the lifetime figure renders even when the reply names the server's today");
            Check(detailText.Any(t => t.Contains("最近 120 个完整学习日")), "every comparison window renders with a realistic reply");
            Check(detailText.Any(t => t.Contains("待云端确认")), "a pending day is surfaced as pending rather than failing the page");
            // Opening the detail must actually persist the authoritative window; otherwise the
            // cached-joint-day layer is dead code and older days silently degrade to unknown.
            var persisted = store.LoadJoint(actor, relationship);
            Check(persisted.CoverageFrom is not null && persisted.Days.Count > 0,
                "opening the detail persists the server's authoritative joint-day window");
            Check(persisted.Days.All(kv => kv.Key < SocialDayRules.DateAt(DateTimeOffset.UtcNow)),
                "only finished days are cached");
            Check(persisted.Days.Values.All(v => v is JointDayKnowledge.Success or JointDayKnowledge.NonJoint),
                "a pending or unknown day is never cached as a known result");

            // Ending the relationship drops that relationship's cached history.
            var beforeEnd = store.LoadJoint(actor, relationship).Days.Count;
            Check(beforeEnd > 0, "the cache exists before the relationship ends");


            // Legend entries hide and show independently, without touching the server.
            revoke.Clear();
            var toggle = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SocialToggleSeries-0");
            var before = overlays[0].Series[0].Visible;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            Check(overlays[0].Series[0].Visible != before, "a legend entry toggles its own visibility");
            Check(revoke.Count == 0, "hiding a curve locally never sends anything to the server");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            Check(overlays[0].Series[0].Visible == before, "the legend entry can be shown again");

            // Reduced motion draws the curve immediately instead of animating it.
            var replay = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SocialReplayCurves");
            // The switch lives in the app's settings surface, not on this page, so it is taken
            // from the window field the way the other UI fixtures use it.
            var reduceMotion = (CheckBox?)typeof(MainWindow)
                .GetField("ReduceMotionBox", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(window);
            Check(reduceMotion is not null, "the application exposes a reduced-motion switch that the social charts honour");
            reduceMotion!.IsChecked = true;
            replay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            Check(!overlays[0].IsAnimating && overlays[0].RevealProgress == 1,
                "reduced motion shows the whole curve at once instead of animating");
            reduceMotion.IsChecked = false;
            replay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(overlays[0].IsAnimating && overlays[0].RevealProgress < 1,
                "with motion allowed the curve reveals progressively");

            // Leaving the page stops the chart timers instead of letting them run on.
            Call("ShowPage", "statistics");
            await Task.Delay(80);
            Check(!overlays[0].IsAnimating, "hiding the page stops the curve animation");
            Call("ShowPage", "social");

            // A transient screenshot for review, written outside the app data and removed exactly.
            var shot = Path.Combine(Path.GetTempPath(), "lexi-social-charts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(shot);
            try
            {
                var target = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1000, 1400),
                    new Avalonia.Vector(96, 96));
                target.Render(window);
                await using (var file = File.Create(Path.Combine(shot, "detail.png"))) target.Save(file);
                Check(File.Exists(Path.Combine(shot, "detail.png")) && new FileInfo(Path.Combine(shot, "detail.png")).Length > 0,
                    "the detail page renders to a review screenshot");
            }
            finally { try { Directory.Delete(shot, true); } catch { } }
            Check(!Directory.Exists(shot), "the review screenshot is removed exactly");

            // ---- an untimed but confirmed day is reported as a success, not as untouched ----
            directory.Untimed = true;
            await (Task)typeof(MainWindow).GetMethod("SocialLoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [actor, staleGeneration])!;
            var untimedText = LogicalTexts(window).ToList();
            Check(untimedText.Any(t => t.Contains("双双存活") || t.Contains("续命成功")),
                "a day the server confirms as done with no usable instant still reads as a success");
            directory.Untimed = false;

            try { await coordinator.CreateInviteAsync(actor, "not-hex"); Check(false, "malformed invite entropy must be rejected"); }
            catch (ArgumentException) { Check(true, "malformed invite entropy is rejected before any request"); }

            // Removing an avatar never leaves a dangling profile pointer.
            var avatarBytes = new byte[64];
            Check(!AvatarCodec.TryCreate(avatarBytes, out _), "an avatar that is not a real image is refused by the codec");
            Set("_socialAvatar", null);
            Set("_socialClient", client); Set("_socialSync", new SocialSyncCoordinator(store, client));
            Console.WriteLine($"Social UI: {checks} assertions passed");
        }
        // Print the whole exception: a message alone made a real failure undiagnosable in CI.
        catch (Exception error) { exit = 1; Console.Error.WriteLine("Social UI failed: " + error); }
        finally
        {
            window.ForceClose();
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exit);
        }
    }
    private static object Session(Guid actor, DateTimeOffset now) => new { access_token = "e30." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = actor }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic", refresh_token = "synthetic", expires_at = now.AddDays(1).ToUnixTimeSeconds(), user = new { id = actor } };
    private sealed class MemorySecrets : ISecureSecretStore { private string? _value; public string? Get() => _value; public void Set(string value) => _value = value; public void Delete() => _value = null; }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        public string? Status { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) =>
            request.RequestUri!.AbsolutePath == "/rest/v1/rpc/lexi_status" && Status is not null
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Status) })
                : throw new HttpRequestException("isolated offline fixture");
    }
    private sealed class DelayedAuthHandler(Guid actor) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(); public TaskCompletionSource Release { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri!.AbsolutePath != "/auth/v1/token") throw new HttpRequestException("isolated auth only");
            Started.TrySetResult(); await Release.Task;
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Session(actor, DateTimeOffset.UtcNow))) };
        }
    }
    private sealed class DirectoryHandler : HttpMessageHandler
    {
        public static readonly Guid ProfileOwner = Guid.Parse("44444444-4444-4444-4444-444444444444");
        private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        public string FriendName { get; set; } = "\u5c0f\u660e";
        public Guid OwnProfileId { get; set; } = ProfileOwner;
        public string OwnNickname { get; set; } = "Fixture";
        public string? OwnAvatarPath { get; set; }
        public int Streak { get; set; } = 28;
        public bool ShareTime { get; set; } = true;
        public bool Fail { get; set; }
        public bool Broken { get; set; }
        public bool Untimed { get; set; }
        public int AvatarReads { get; private set; }
        public string? LastBody { get; private set; }
        public Dictionary<string, string> Requests { get; } = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/storage/v1/object/authenticated/lexi-avatars/", StringComparison.Ordinal))
            {
                AvatarReads++;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(TinyPng) };
            }
            var name = request.RequestUri!.AbsolutePath.Replace("/rest/v1/rpc/", "", StringComparison.Ordinal);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellation);
            Requests[name] = LastBody ?? "";
            if (Fail && name is "lexi_friends" or "lexi_invites" or "lexi_profile") throw new HttpRequestException("isolated offline fixture");
            var now = DateTimeOffset.UtcNow; var today = SocialDayRules.DateAt(now); var relationship = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var friend = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var state = new
            {
                relationship_id = relationship, run_id = Guid.NewGuid(), date = today.ToString("yyyy-MM-dd"), server_now = now,
                lifecycle = Broken ? "broken" : "active", streak = Streak, lifetime_days = 21,
                self_done = !Broken, friend_done = !Broken,
                self_completed_at = Untimed ? (DateTimeOffset?)null : now.AddHours(-2),
                friend_completed_at = Untimed ? (DateTimeOffset?)null : now.AddHours(-1),
                self_cause = "learning_complete", friend_cause = "learning_complete",
                success = !Broken, extreme = false, pending_previous = false,
                broken_day = Broken ? today.AddDays(-1).ToString("yyyy-MM-dd") : (string?)null
            };
            object payload = name switch
            {
                "lexi_friends" => new object[] { new
                {
                    relationship_id = relationship, friend_id = friend, nickname = FriendName, avatar_path = (string?)null,
                    share_counts = true, share_curve = true, share_time = ShareTime,
                    friend_share_counts = true, friend_share_curve = true, friend_share_time = true,
                    pair_state = state,
                    today = new { date = today.ToString("yyyy-MM-dd"), self_new_words = 20, self_review_words = 36, friend_new_words = 18, friend_review_words = 30 }
                } },
                "lexi_invites" => new
                {
                    incoming = new object[] { new { id = Guid.NewGuid(), sender_id = friend, nickname = FriendName, status = "pending", created_at = now.AddHours(-1), expires_at = now.AddHours(23), handled_at = (DateTimeOffset?)null, relationship_id = (Guid?)null } },
                    outgoing = new object[] { new { id = Guid.NewGuid(), recipient_id = (Guid?)null, nickname = (string?)null, status = "expired", created_at = now.AddDays(-2), expires_at = now.AddDays(-1), handled_at = (DateTimeOffset?)null, relationship_id = (Guid?)null } },
                    server_now = now
                },
                "lexi_restart" => new { status = "waiting" },
                "lexi_curve_list" => new object[]
                {
                    new { user_id = DirectoryHandler.ProfileOwner, nickname = "Fixture", taken_at = now, sample_count = 12,
                          points = Enumerable.Range(0, 121).Select(i => (object)(1.0 - i / 240d)).ToArray(), protocol = "fsrs-6-average-stability-v1" }
                },
                "lexi_profile" => new { user_id = OwnProfileId, nickname = OwnNickname, avatar_path = OwnAvatarPath, updated_at = now },
                "lexi_create_invite" => new { id = Guid.NewGuid(), expires_at = now.AddHours(24), status = "pending" },
                "lexi_claim_invite" => new { id = Guid.NewGuid(), nickname = FriendName, expires_at = now.AddHours(24) },
                "lexi_answer_invite" => new { status = "accepted", relationship_id = relationship },
                "lexi_set_sharing" => new { share_counts = true, share_curve = false, share_time = true },
                "lexi_pair_detail" => new
                {
                    relationship_created_at = now.AddDays(-30), coverage_from = today.AddDays(-364).ToString("yyyy-MM-dd"),
                    // The server really does include its own today in both sets (reconcile writes a
                    // row for the current Beijing day). The empty-array fixture used to hide a
                    // blocker where today was treated as an error, so it is deliberately non-empty.
                    pending_days = new[] { today.AddDays(-2).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd") },
                    state,
                    weekly = new object[]
                    {
                        new { date = today.AddDays(-1).ToString("yyyy-MM-dd"), self_new_words = 20, self_review_words = 36,
                              self_seconds = 1800.0, self_time_coverage = "complete", self_scope = "full" }
                    },
                    friend_share_counts = true, friend_share_time = true,
                    friend_share_curve = true,
                    common_days = new[] { today.AddDays(-1).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd") },
                    runs = Array.Empty<object>()
                },
                _ => new { }
            };
            var json = JsonSerializer.Serialize(payload);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
