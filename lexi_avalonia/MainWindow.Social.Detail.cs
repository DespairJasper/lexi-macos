using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Lexi.Social;

namespace Lexi;

public partial class MainWindow
{
    private SocialCurveOverlay? _socialCurveOverlay;
    private readonly List<SocialWeeklyChart> _socialWeekCharts = [];
    private readonly List<SocialCurveOverlay.SocialCurveSeries> _socialSeries = [];

    /// <summary>Stop every chart timer. Called when the page is hidden, the account changes or the
    /// window closes, so no animation outlives the view that owns it.</summary>
    private void SocialStopCharts()
    {
        _socialCurveOverlay?.StopAnimation();
        foreach (var chart in _socialWeekCharts) chart.Days = [];
        _socialWeekCharts.Clear();
    }

    /// <summary>Turns a legend entry on or off. Visibility is per series and purely local; it
    /// never changes what the server shares.</summary>
    private void SocialToggleSeries(int index)
    {
        if (index < 0 || index >= _socialSeries.Count) return;
        _socialSeries[index].Visible = !_socialSeries[index].Visible;
        _socialCurveOverlay?.InvalidateVisual();
    }

    private async Task BuildSocialDetailAsync(Guid actor, SocialFriendView friend, SocialPairDetail detail,
        StackPanel host, long generation)
    {
        // ---- curves: our own model plus every friend still authorized to share one ----
        var ownName = _socialProfile?.Nickname ?? _socialClient?.Nickname;
        var self = new SocialCurveView(actor, ownName ?? "我",
            DateTimeOffset.UtcNow, 0, BuildOwnCurveRates(), SocialCurve.Protocol);
        var friends = await LoadFriendCurvesAsync(actor, generation);
        if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
        var legends = SocialCurveLegend.Build(self, friends);
        _socialSeries.Clear();
        foreach (var entry in legends)
        {
            var points = entry.IsSelf ? self.Points
                : friends.FirstOrDefault(f => f.UserId == entry.UserId)?.Points ?? [];
            var displayLabel = entry.IsSelf ? ownName is null ? T("我") : TF($"自己 · {ownName}") : entry.Label;
            _socialSeries.Add(new(displayLabel, entry.Color, points, entry.SampleCount, entry.IsSelf));
        }
        var curvePanel = new StackPanel { Spacing = 6 };
        curvePanel.Children.Add(SocialText("朋友们的记忆曲线", 16));
        curvePanel.Children.Add(SocialText("自己 + 全部已授权好友 · 对照当前状态。模型预测，不做记忆能力排名。"));
        var overlay = new SocialCurveOverlay { Series = _socialSeries,
            Text = (chinese, english) => UiText.Language == "en" ? english : chinese };
        _socialCurveOverlay = overlay;
        curvePanel.Children.Add(overlay);
        var legendRow = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, ItemSpacing = 10 };
        for (var index = 0; index < legends.Count; index++)
        {
            var captured = index;
            var button = new Button { Classes = { "secondary" }, FontSize = 11,
                Name = "SocialToggleSeries-" + index };
            if (legends[index].IsSelf) SocialLocalize(button, ownName is null ? T("我") : TF($"自己 · {ownName}"));
            else button.Content = legends[index].Label;
            button.Click += (_, _) => SocialToggleSeries(captured);
            legendRow.Children.Add(button);
        }
        curvePanel.Children.Add(legendRow);
        curvePanel.Children.Add(SocialButton("重新播放曲线动画", "SocialReplayCurves", () =>
        {
            overlay.Replay(ReduceMotionBox.IsChecked.GetValueOrDefault());
            return Task.CompletedTask;
        }));
        host.Children.Add(new Border { Classes = { "card" }, Child = curvePanel });
        // Same 720 ms reveal as the statistics curve, skipped entirely under reduced motion.
        overlay.Replay(ReduceMotionBox.IsChecked.GetValueOrDefault());

        // ---- weekly detail: seven finished Beijing days, two separate plots ----
        var week = BuildSocialWeek(detail);
        var weeklyPanel = new StackPanel { Spacing = 10 };
        weeklyPanel.Children.Add(SocialText("过去一周，各自苟得怎么样", 16));
        weeklyPanel.Children.Add(SocialText("最近 7 个完整北京时间学习日 · 缺失或未授权日期断线，不补成 0"));
        var minutes = new SocialWeeklyChart
        {
            Days = week, Title = T("有效学习时长"), Unit = T("分钟 / 天"),
            Text = (chinese, english) => UiText.Language == "en" ? english : chinese,
            Value = day => day.SelfSeconds is { } seconds ? seconds / 60 : null,
            Tooltip = day => WeekTooltip(day, day.SelfSeconds is { } s
                ? TF($"{(s / 60).ToString("0.#", CultureInfo.CurrentCulture)} 分钟") : T("时长未知"))
        };
        var words = new SocialWeeklyChart
        {
            Days = week, Title = T("学习词数"), Unit = T("词 / 天"),
            Text = (chinese, english) => UiText.Language == "en" ? english : chinese,
            Value = day => day.SelfNewWords + day.SelfReviewWords,
            Tooltip = day => WeekTooltip(day, TF($"{day.SelfNewWords} 新 / {day.SelfReviewWords} 复习"))
        };
        _socialWeekCharts.Add(minutes); _socialWeekCharts.Add(words);
        weeklyPanel.Children.Add(minutes);
        weeklyPanel.Children.Add(words);
        if (week.Any(d => d.SelfScope == SocialWeek.ScopeBindingPartial))
            weeklyPanel.Children.Add(SocialText("绑定当天只统计绑定之后的时段，该日标注为部分覆盖。"));
        host.Children.Add(new Border { Classes = { "card" }, Child = weeklyPanel });

        // ---- lifetime and the joint / non-joint comparison ----
        var cache = _socialStore!.LoadJoint(actor, friend.RelationshipId);
        BuildSocialComparison(actor, friend, detail, cache, host);
    }

    private string WeekTooltip(SocialWeekDay day, string detail) =>
        day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · " + detail
        + (day.SelfScope == SocialWeek.ScopeBindingPartial ? " · " + T("绑定当天部分覆盖") : "");

    private IReadOnlyList<double> BuildOwnCurveRates()
    {
        try
        {
            if (_memoryStore is null || _statisticsHistoryFailed) return [];
            var keys = _memoryStore.LoadCanonicalHistory(null, DateTime.UtcNow.AddTicks(1), null)
                .Select(r => r.WordKey).Distinct(StringComparer.Ordinal).ToArray();
            var stabilities = keys.Select(k => _memoryStore.GetCard(k))
                .Where(c => c != null && double.IsFinite(c.Stability) && c.Stability > 0)
                .Select(c => c!.Stability).ToArray();
            var scheduler = _memoryPersonalization != null ? new Fsrs6Scheduler(_memoryPersonalization.Weights) : new Fsrs6Scheduler();
            return SocialCurve.Build(stabilities, scheduler.Retrievability).Select(p => p.Rate).ToArray();
        }
        catch { return []; }
    }

    private async Task<IReadOnlyList<SocialCurveView>> LoadFriendCurvesAsync(Guid actor, long generation)
    {
        try
        {
            var reply = await _socialClient!.RpcAsync(actor, "lexi_curve_list", new { }, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return [];
            // Fully qualified: MainWindow also has a SocialDirectory property holding a path.
            var all = Lexi.Social.SocialDirectory.CurveList(reply);
            // Our own row is the server's copy of the curve we just built locally; the local one wins.
            return all.Where(c => c.UserId != actor).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Any failure clears the friend curves rather than showing a stale or partial legend.
            return [];
        }
    }

    private IReadOnlyList<SocialWeekDay> BuildSocialWeek(SocialPairDetail detail)
    {
        var today = SocialDayRules.DateAt(DateTimeOffset.UtcNow);
        var counts = new Dictionary<DateOnly, SocialWordCounts>();
        var seconds = new Dictionary<DateOnly, double?>();
        try
        {
            if (_memoryStore is not null && !_planReadFailed && _memoryStore.LoadPendingMutations().Count == 0)
            {
                var history = _memoryStore.LoadCanonicalHistory(null, DateTime.UtcNow.AddTicks(1), null);
                var first = history.Where(r => !string.IsNullOrWhiteSpace(r.WordKey) && r.CompletedAtUtc != default)
                    .GroupBy(r => r.WordKey, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Min(r => SocialFactProjection.Utc(r.CompletedAtUtc)), StringComparer.Ordinal);
                for (var offset = SocialWeek.Days; offset >= 1; offset--)
                {
                    var date = today.AddDays(-offset);
                    var keys = history.Where(r => r.CompletedAtUtc != default
                        && SocialDayRules.DateAt(SocialFactProjection.Utc(r.CompletedAtUtc)) == date)
                        .Select(r => r.WordKey).Distinct(StringComparer.Ordinal).ToArray();
                    counts[date] = new(keys.Count(k => first.TryGetValue(k, out var at) && SocialDayRules.DateAt(at) == date),
                        keys.Length - keys.Count(k => first.TryGetValue(k, out var at) && SocialDayRules.DateAt(at) == date));
                }
                var stored = new StudyActivityStore(Path.Combine(SocialDirectory, "study-activity.json")).Read();
                var known = stored.Writable && File.Exists(Path.Combine(SocialDirectory, "study-activity.json"));
                for (var offset = SocialWeek.Days; offset >= 1; offset--)
                {
                    var date = today.AddDays(-offset);
                    seconds[date] = known ? SocialFactProjection.Seconds(stored.Activities, date, DateTimeOffset.MinValue) : null;
                }
            }
        }
        catch { counts.Clear(); seconds.Clear(); }
        var actor = _socialClient?.UserId;
        var binding = actor is { } account ? _socialStore?.GetBinding(account) : null;
        return SocialWeek.Build(today, counts, seconds, binding?.FirstDay, detail.Weekly);
    }

    private void BuildSocialComparison(Guid actor, SocialFriendView friend, SocialPairDetail detail,
        SocialStore.SocialJointCache cache, StackPanel host)
    {
        // The server's own Beijing day, not the device clock: a device running ahead would
        // otherwise treat a day the server still considers pending as a known finished day.
        var today = detail.State.Today;
        var friendship = SocialDayRules.DateAt(detail.RelationshipCreatedAt);
        var common = detail.CommonDays.ToHashSet();
        var pending = detail.PendingDays.ToHashSet();
        var local = new List<SocialLocalDay>();
        try
        {
            if (_memoryStore is not null && !_planReadFailed && _memoryStore.LoadPendingMutations().Count == 0)
            {
                var history = _memoryStore.LoadCanonicalHistory(null, DateTime.UtcNow.AddTicks(1), null);
                var stored = new StudyActivityStore(Path.Combine(SocialDirectory, "study-activity.json")).Read();
                var known = stored.Writable && File.Exists(Path.Combine(SocialDirectory, "study-activity.json"));
                var observed = history.Where(r => r.CompletedAtUtc != default)
                    .Select(r => SocialDayRules.DateAt(SocialFactProjection.Utc(r.CompletedAtUtc)))
                    .Concat(known ? stored.Activities.Where(StudyStatistics.IsValid)
                        .Select(a => SocialDayRules.DateAt(SocialFactProjection.Utc(a.StartUtc))) : [])
                    .Distinct().Where(d => d < today).OrderBy(d => d).ToArray();
                foreach (var date in observed)
                {
                    var rows = history.Where(r => r.CompletedAtUtc != default
                        && SocialDayRules.DateAt(SocialFactProjection.Utc(r.CompletedAtUtc)) == date).ToArray();
                    // A day known only through the activity sidecar has no canonical words at all,
                    // so its word count is unknown rather than a confirmed zero.
                    int? words = rows.Length > 0
                        ? rows.Select(r => r.WordKey).Distinct(StringComparer.Ordinal).Count()
                        : null;
                    local.Add(new(date, JointDayKnowledge.Unknown, known ? SocialFactProjection.Seconds(stored.Activities, date, DateTimeOffset.MinValue) : null, words));
                }
            }
        }
        catch { local.Clear(); }
        // Persist what the server just told us as the authoritative window, so a day that falls
        // out of the window tomorrow is still known instead of silently becoming unknown.
        try
        {
            var confirmed = new List<(DateOnly Date, JointDayKnowledge Joint)>();
            for (var date = detail.CoverageFrom; date < today; date = date.AddDays(1))
            {
                if (pending.Contains(date)) continue;   // never cached as a known result
                confirmed.Add((date, common.Contains(date) ? JointDayKnowledge.Success : JointDayKnowledge.NonJoint));
            }
            // No local history means we cannot say which older days are still comparable, so every
            // older observation is kept. MinValue means "do not prune" and is never a real date,
            // which is what stops an unreadable history from wiping the whole out-of-window cache.
            var earliest = local.Count > 0 ? local.Min(d => d.Date) : DateOnly.MinValue;
            _socialStore!.RecordJointWindow(actor, friend.RelationshipId, detail.CoverageFrom, confirmed, earliest, DateTimeOffset.UtcNow);
            cache = _socialStore.LoadJoint(actor, friend.RelationshipId);
        }
        catch (Exception error) when (error is not OperationCanceledException) { /* a cache failure must not block the page */ }
        var projected = SocialJointDays.Project(today, friendship, detail.CoverageFrom, common, pending, cache.Days, local);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(SocialText(TF($"和{friend.Nickname}，一起苟过的日子"), 16));
        panel.Children.Add(SocialText(TF($"本次成为好友以来，一共苟过 {detail.State.LifetimeDays} 天（跨局累计 · 同日去重 · 云端确认）")));
        panel.Children.Add(SocialText(TF($"这段关系当前连续 {detail.State.Streak} 天；累计天数与当前连续互相独立。")));
        foreach (var (label, from) in new (string, DateOnly?)[] { ("可用全部历史", null), ("最近 30 个完整学习日", today.AddDays(-30)),
            ("最近 90 个完整学习日", today.AddDays(-90)), ("最近 120 个完整学习日", today.AddDays(-120)) })
        {
            var stats = SocialStatistics.Build(projected, today, from);
            var joint = stats.JointDays;
            var comparison = stats.NonJointDays;
            var minutesDifference = Format(stats.Seconds.MeanDifference);
            var wordsDifference = Format(stats.Words.MeanDifference);
            var percentage = Format(stats.Words.PercentDifference);
            panel.Children.Add(SocialTextPair(
                $"{label}：共同成功 {joint} 天 · 对照 {comparison} 天 · 时长日均差 {minutesDifference} 分钟/天 · 词数日均差 {wordsDifference} 词/天 · 相对 {percentage}%",
                $"{UiText.English(label)}: {joint} shared-success days · {comparison} comparison days · {minutesDifference} min/day difference · {wordsDifference} words/day difference · {percentage}% relative difference",
                13));
        }
        panel.Children.Add(SocialText("只统计本人学习；待确认与无法核对的历史日期不计入任何一组。这不是因果结论，也不做排名。", 12));
        host.Children.Add(new Border { Classes = { "card" }, Child = panel });
    }

    private static string Format(double? value) => value is { } number ? number.ToString("0.#", CultureInfo.CurrentCulture) : "—";
}
