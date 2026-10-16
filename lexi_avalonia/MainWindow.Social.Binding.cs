using System.Diagnostics;
using Avalonia.Controls;
using Lexi.Social;

namespace Lexi;
public partial class MainWindow
{
    private void RenderSocialBinding(Guid actor)
    {
        var panel = new StackPanel { Spacing = 10 };
        void AddCard() => _socialBody!.Children.Add(new Border { Classes = { "card" }, Child = panel });
        try
        {
            var binding = _socialStore!.GetBinding(actor);
            if (binding?.BoundAt is not null)
            {
                panel.Children.Add(SocialText("此学习档案已绑定。历史学习仅在本机展示，不补上传。"));
                panel.Children.Add(SocialButton("同步今天的学习摘要", "SocialSyncNow", () => SocialManualRefreshAsync(actor)));
                AddCard();
                return;
            }
        }
        catch
        {
            panel.Children.Add(SocialText("当前学习档案属于另一个 GitHub 账号；已暂停上传。请退出并重新登录原账号。"));
            panel.Children.Add(SocialText("也可以创建空的独立学习档案。原档案和待发摘要保留；新档案需要重新登录 GitHub。"));
            panel.Children.Add(SocialButton("创建独立学习档案并重启", "SocialCreateIndependentProfile", SocialCreateIndependentProfileAsync));
            AddCard();
            return;
        }
        var nickname = SocialInput("SocialBindNickname", "昵称（1–32 字）", 32);
        nickname.Text = _socialClient!.Nickname ?? T("学习者");
        panel.Children.Add(SocialText("绑定后只记录今天尚未完成的学习目标；不补传历史。成为好友后，词数、曲线和学习时长默认分享，可分别关闭。"));
        panel.Children.Add(nickname);
        panel.Children.Add(SocialButton("绑定当前学习档案", "SocialBindProfile", async () =>
        {
            var name = nickname.Text?.Trim() ?? ""; if (name.Length is < 1 or > 32) throw new ArgumentException();
            var exclusions = _studyPlans.ToDictionary(p => p.Id, p => (IReadOnlySet<string>)new HashSet<string>(p.CompletedWordIds, StringComparer.Ordinal));
            var generation = _socialGeneration;
            var binding = await _socialSync!.RequestBindingAsync(actor, name, exclusions, true, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            ConfirmSocialBaseline(actor, binding);
            RenderSocialEntry(); await SocialRefreshAsync(actor);
        }));
        AddCard();
    }

    private async Task SocialCreateIndependentProfileAsync()
    {
        if (App.IsAutomatedTestRun())
        { SocialSetMessage("自动测试模式：交互重启已禁用。"); return; }
        if (IsRestoring) return;
        SocialCancelOperations(); var generation = _socialGeneration;
        var launcher = new SocialProfileLauncher(SocialProfileLauncher.DefaultConfigurationPath);
        var profile = launcher.CreateIndependentProfile(SocialDirectory, _socialOptions);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确认本地应用启动程序。");
        var assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? typeof(Program).Assembly.Location : null;
        SocialSetMessage("正在打开独立学习档案…");
        using var child = await launcher.StartAndWaitForReadyAsync(launcher.CreateStartInfo(executable, assembly, profile), _socialCancellation.Token);
        try
        {
            if (generation != _socialGeneration) { await SocialProfileLauncher.StopOwnedChildAsync(child); return; }
            if (child.HasExited) throw new InvalidOperationException("独立学习窗口已关闭。原窗口与档案选择已保留。");
            launcher.SelectProfile(profile, SocialDirectory);
        }
        catch { await SocialProfileLauncher.StopOwnedChildAsync(child); throw; }
        ForceClose();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(0);
    }

    private IReadOnlyList<CanonicalHistoryRow> SocialHistory()
    {
        // Read only: independent activity saves must not expose canonical rows whose JSON is still pending.
        if (_memoryStore is null || _planReadFailed || _memoryStore.LoadPendingMutations().Count != 0)
            throw new InvalidOperationException("学习进度尚未完成持久交付。");
        return _memoryStore.LoadCanonicalHistory(null, DateTime.UtcNow.AddTicks(1), null);
    }

    private string SocialPlanWordKey(DailyStudyPlan plan, DailyStudyPlanWord word) =>
        WordKeyResolver.ResolvePlanWord(plan, word, id => _allWords.FirstOrDefault(w => w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == id)).ToString();

    private void ConfirmSocialBaseline(Guid actor, SocialBindingState binding)
    {
        if (binding.FirstDayBaselineConfirmed || binding.BoundAt is not { } boundAt || binding.FirstDay is not { } date) return;
        var history = SocialHistory();
        var baseline = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var plan in _studyPlans)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var word in plan.Words.Where(w => plan.CompletedWordIds.Contains(w.Id)))
            {
                var key = SocialPlanWordKey(plan, word);
                var rows = history.Where(r => r.WordKey == key).ToArray();
                // Missing canonical time leaves baseline unconfirmed, rather than inventing a timestamp.
                if (rows.Length == 0 || rows.Any(r => r.CompletedAtUtc == default)) throw new InvalidOperationException();
                if (rows.Any(r => SocialFactProjection.Utc(r.CompletedAtUtc) <= boundAt && SocialDayRules.DateAt(SocialFactProjection.Utc(r.CompletedAtUtc)) == date)) ids.Add(word.Id);
            }
            baseline.Add(plan.Id, ids);
        }
        _socialSync!.ConfirmBindingBaseline(actor, binding.BindingId, boundAt, baseline);
    }

    private void SocialObserveSaved(QualificationCause cause, bool recovery = false)
    {
        if (_socialClient?.UserId is not { } actor || _socialSync is null) return;
        try
        {
            var binding = _socialStore!.GetBinding(actor); if (binding?.BoundAt is not { } boundAt) return;
            ConfirmSocialBaseline(actor, binding);
            var now = DateTimeOffset.UtcNow; var date = SocialDayRules.DateAt(now); var history = SocialHistory();
            var eligible = history.Where(r => r.CompletedAtUtc != default && SocialFactProjection.Utc(r.CompletedAtUtc) >= boundAt && SocialDayRules.DateAt(SocialFactProjection.Utc(r.CompletedAtUtc)) == date).ToArray();
            DateTimeOffset? last = eligible.Length == 0 ? null : eligible.Max(r => SocialFactProjection.Utc(r.CompletedAtUtc));
            var targets = _studyPlans.Select(p =>
            {
                var keys = p.Words.Select(w => SocialPlanWordKey(p, w)).ToHashSet(StringComparer.Ordinal);
                var rows = eligible.Where(r => keys.Contains(r.WordKey)).ToArray();
                DateTimeOffset? actual = rows.Length == 0 ? null : rows.Max(r => SocialFactProjection.Utc(r.CompletedAtUtc));
                return PlanDayEligibilityRules.FromPlan(p, date, actual, _socialStore.GetBindingExclusions(actor, p.Id, date));
            }).ToArray();
            var counts = SocialFactProjection.Counts(history, date, boundAt);
            var stored = new StudyActivityStore(Path.Combine(SocialDirectory, "study-activity.json")).Read();
            var durationKnown = stored.Writable && File.Exists(Path.Combine(SocialDirectory, "study-activity.json"));
            var seconds = durationKnown ? SocialFactProjection.Seconds(stored.Activities, date, boundAt) : 0;
            var facts = new SocialDayObservation(date, targets, counts.NewWords, counts.ReviewWords, seconds, now, last, cause);
            if (recovery) _socialSync.RecoverLocal(actor, facts); else _socialSync.ObserveLocal(actor, facts);
            _socialSync.QueueDuration(actor, new(date, durationKnown ? seconds : null, durationKnown ? "partial" : "unavailable"));
            if (!recovery && _socialSyncTimer is not null) { _socialSyncTimer.Stop(); _socialSyncTimer.Start(); }
        }
        catch { SocialSetMessage("学习摘要暂无法确认；原始学习记录已保留，暂停上传未确认的学习事实。"); }
    }

    /// <summary>Load the friend list, invitations and profile in one pass. The private view is
    /// only replaced once a reply from the current generation has been fully decoded, and a
    /// failed load revokes the cached aggregate rather than leaving a stale one in place.</summary>
    private async Task SocialLoadDirectoryAsync(Guid actor, long generation)
    {
        if (_socialClient?.UserId != actor || _socialSync is null) return;
        try
        {
            var friends = await _socialSync.FriendsAsync(actor, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            var invites = await _socialSync.InvitesAsync(actor, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            var profile = await _socialSync.ProfileAsync(actor, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            _socialFriends = friends;
            _socialInvites = invites;
            if (_socialAvatarPath != profile.AvatarPath)
            { _socialAvatar?.Dispose(); _socialAvatar = null; _socialAvatarPath = null; }
            _socialProfile = profile;
            // Secondary uploads follow the switches the server just confirmed, never a local guess.
            _socialSync.ApplyOwnAggregate(actor, friends);
            RenderSocialFriends(actor, friends);
            RenderSocialInvitePanel(actor);
            RenderSocialProfilePanel(actor);
            if (_socialProfile.AvatarPath is { } path && _socialAvatar is null)
                await LoadOwnAvatarAsync(actor, path, generation);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (generation != _socialGeneration) return;
            // An unreadable or failed directory load revokes the aggregate and clears the cache,
            // including the invitation book and any avatar held for display.
            _socialFriends = null; _socialInvites = null; _socialAvatar?.Dispose(); _socialAvatar = null; _socialAvatarPath = null;
            // A failed or revoked load must also drop the curves, the weekly plots and the legend.
            SocialStopCharts();
            _socialSeries.Clear();
            _socialDetailHost?.Children.Clear();
            SocialSuspendSecondarySharing(actor);
        }
    }

    private bool SocialQueueCurrentCurve(Guid actor)
    {
        try
        {
            if (_socialSync is null || _memoryStore is null) return false;
            var keys = SocialHistory().Select(row => row.WordKey).Distinct(StringComparer.Ordinal);
            var cards = keys.Select(key => _memoryStore.GetCard(key))
                .Where(card => card is not null && double.IsFinite(card.Stability) && card.Stability > 0)
                .Select(card => card!).ToArray();
            if (cards.Length == 0) return false;

            var scheduler = _memoryPersonalization is not null
                ? new Fsrs6Scheduler(_memoryPersonalization.Weights)
                : new Fsrs6Scheduler();
            var points = SocialCurve.Build(cards.Select(card => card.Stability), scheduler.Retrievability);
            if (points.Count != SocialCurve.DefaultSampleCount) return false;

            // Share only the same 121-point predicted-recall curve shown in Statistics.
            // Individual review events and their scatter-plot coordinates are never serialized.
            _socialSync.QueueCurve(actor, new(DateTimeOffset.UtcNow, cards.Length,
                points.Select(point => point.Rate).ToArray()));
            return true;
        }
        catch (Exception error)
        {
            Debug.WriteLine("[social] curve snapshot unavailable: " + error.GetType().Name);
            return false;
        }
    }

    private async Task SocialLoadOwnProfileAsync(Guid actor, long generation)
    {
        if (_socialClient?.UserId != actor || _socialSync is null) return;
        try
        {
            var profile = await _socialSync.ProfileAsync(actor, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
            if (profile.UserId != actor) throw new InvalidDataException("The profile response belongs to another account.");
            if (_socialAvatarPath != profile.AvatarPath)
            {
                _socialAvatar?.Dispose(); _socialAvatar = null; _socialAvatarPath = null;
            }
            _socialProfile = profile;
            RenderSocialProfilePanel(actor);
            if (profile.AvatarPath is { } path && _socialAvatar is null)
                await LoadOwnAvatarAsync(actor, path, generation);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (generation == _socialGeneration && _socialClient?.UserId == actor)
                SocialSetMessage("云端昵称和头像暂时无法读取；本地学习数据未改动。",
                    "Your cloud nickname and avatar could not be loaded. Local study data is unchanged.");
        }
    }

    private Task SocialRefreshAsync(Guid actor) => SocialRefreshAsyncCore(actor, includeSecondary: false);

    private Task SocialManualRefreshAsync(Guid actor) => SocialRefreshAsyncCore(actor, includeSecondary: true);

    private async Task SocialRefreshAsyncCore(Guid actor, bool includeSecondary)
    {
        if (_socialClient?.UserId != actor || _socialSync is null) return;
        var generation = _socialGeneration;
        // The own profile belongs to the authenticated cloud account, not to this local
        // study-data binding. Restore it even when an app update left a fresh sidecar.
        await SocialLoadOwnProfileAsync(actor, generation);
        if (generation != _socialGeneration || _socialClient?.UserId != actor || _socialStore is null) return;
        try
        {
            var binding = _socialStore.GetBinding(actor); if (binding is null) return;
            binding = await _socialSync.ResumeBindingAsync(actor, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            ConfirmSocialBaseline(actor, binding);
            if (_socialRecoveryPending)
            { SocialObserveSaved(QualificationCause.Correction, true); _socialRecoveryPending = false; }
            var result = await _socialSync.FlushAsync(actor, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            SocialSetMessage(result.Error is not null ? "同步暂未完成，学习摘要留在本机等待重试。" : result.Pending > 0 ? "学习摘要等待同步。" : "学习摘要已同步。共同学习状态由云端确认。");
            _socialStore.PruneConfirmed(actor, SocialDayRules.DateAt(DateTimeOffset.UtcNow));
            _socialStore.BackupIfChanged(Path.GetFullPath(Path.Combine(SocialDirectory, "social-backups")), DateTimeOffset.UtcNow);
            await SocialLoadDirectoryAsync(actor, generation);
            if (!includeSecondary || generation != _socialGeneration || _socialClient?.UserId != actor) return;
            if (_socialFriends is null)
            {
                SocialSetMessage("好友与分享权限未能从云端确认；本次只处理每日摘要，曲线和时长保留在本机。",
                    "Friends and sharing permissions could not be confirmed. This sync handled only the daily summary; the curve and study time remain on this device.");
                return;
            }

            var curveAvailable = SocialQueueCurrentCurve(actor);
            var secondary = await _socialSync.FlushSecondaryAsync(actor, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
            if (result.Error is not null || secondary.Error is not null)
                SocialSetMessage("同步尚未全部确认；未确认的摘要、曲线或时长仍保留在本机等待重试。",
                    "Sync is not fully confirmed. Unconfirmed summaries, curve data, and study time remain on this device for retry.");
            else if (result.Pending > 0 || secondary.Pending > 0)
                SocialSetMessage("部分数据仍在本机等待同步；请检查好友分享权限或稍后重试。",
                    "Some data is still waiting to sync on this device. Check friend sharing permissions or try again later.");
            else if (!curveAvailable && secondary.Accepted > 0)
                SocialSetMessage("每日摘要和获准分享的学习时长已同步；当前没有可用的记忆卡来生成遗忘曲线。",
                    "The daily summary and approved study time synced. There are no eligible memory cards to build a forgetting curve yet.");
            else if (!curveAvailable)
                SocialSetMessage("每日摘要已同步；当前没有可用的记忆卡来生成遗忘曲线。",
                    "The daily summary synced. There are no eligible memory cards to build a forgetting curve yet.");
            else if (secondary.Accepted > 0)
                SocialSetMessage("每日摘要及获准分享的附加统计已同步。遗忘曲线只包含预测曲线，不含单条复习记录。",
                    "The daily summary and approved secondary statistics have synced. The forgetting curve contains predictions only, with no individual review records.");
            else
                SocialSetMessage("每日摘要已同步；当前没有新的获准分享曲线或时长待上传。",
                    "The daily summary synced. No new curve or study-time data is pending under the current sharing permissions.");
        }
        catch (OperationCanceledException) { }
        catch { if (generation == _socialGeneration) SocialSetMessage("同步暂未完成；本机学习数据和待发摘要已保留。"); }
    }
}
