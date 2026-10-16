namespace Lexi.Social;

public enum SocialCopyKind
{
    Pending, SyncFailed, Ended, Broken, Extreme, Arrived, Milestone, FirstDay,
    Success, SelfExtremeWaiting, WaitingFriend, WaitingSelf, NeitherDone,
    Night2300, Night2330, Night2350
}
public sealed record SocialCopy(SocialCopyKind Kind, string Text, bool Animate = false);
public sealed record SocialFriendListCopy(string Continuity, string Today);

public static class SocialCopyPolicy
{
    public static SocialCopy Select(SocialPairView pair, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.Streak < 0) throw new ArgumentOutOfRangeException(nameof(pair));
        // Ended access is authoritative even if this device still holds an outgoing batch.
        if (pair.Lifecycle == PairLifecycle.Ended) return new(SocialCopyKind.Ended, "散伙了，这局到此结束");
        if (pair.Sync == SocialSyncStatus.Failed) return new(SocialCopyKind.SyncFailed, "网络掉链子了，续命记录暂时还没传上去");
        if (pair.Sync == SocialSyncStatus.Pending) return new(SocialCopyKind.Pending, "别慌，你的续命记录还在路上！");
        if (pair.Lifecycle == PairLifecycle.Broken) return new(SocialCopyKind.Broken,
            pair.Streak > 0 ? $"坏了，{pair.Streak}天的努力就这么寄了！" : "寄！这局还是没苟住……");
        var self = DoneToday(pair.Self, pair.SelfDone, pair.Today, now);
        var friend = DoneToday(pair.Friend, pair.FriendDone, pair.Today, now);
        var confirmed = pair.CloudSuccess && pair.SuccessDate == pair.Today && self && friend;
        if (confirmed)
        {
            // No qualified instant means the day is complete but untimed, so it can never be
            // presented as a last-minute save even if the server flags the pair as extreme.
            var last = pair.Self is { } own && pair.Friend is { } peer
                ? (own.EligibleAt >= peer.EligibleAt ? own : peer) : null;
            var tiedPlanChange = pair.Self is { } a && pair.Friend is { } b && a.EligibleAt == b.EligibleAt &&
                (a.Cause != QualificationCause.LearningComplete || b.Cause != QualificationCause.LearningComplete);
            if (pair.CloudExtremeConfirmed && last is not null && !tiedPlanChange && IsExtremeLearning(last, pair.Today, now))
                return new(SocialCopyKind.Extreme, "卧槽，差点没苟住！", true);
            if (pair.BackfillConfirmed) return new(SocialCopyKind.Arrived, "记录到账！还好这局保住了！", true);
            var milestone = pair.Streak switch
            {
                3 => "不错嘛，居然连续苟了3天！",
                7 => "有点东西，你俩居然活过了一周！",
                30 => "一个月没寄？你俩是真能苟啊！",
                100 => "不是哥们，你俩真打算苟一辈子啊！",
                365 => "苟了一整年？！这局都快打成传家宝了！",
                _ => null
            };
            if (milestone is not null) return new(SocialCopyKind.Milestone, milestone, true);
            if (pair.Streak == 1) return new(SocialCopyKind.FirstDay, "首日存活成功！明天继续苟！", true);
            return new(SocialCopyKind.Success, "双双苟住！今日续命成功！");
        }
        if (self && friend) return new(SocialCopyKind.Pending, "别慌，你的续命记录还在路上！");
        if (self) return pair.Self is { } mine && IsExtremeLearning(mine, pair.Today, now)
            ? new(SocialCopyKind.SelfExtremeWaiting, $"你是极限苟住了，就看{FriendName(pair)}能不能赶上了！")
            : new(SocialCopyKind.WaitingFriend, "你先苟住了，坐等队友！");
        // Only remind an unfinished self, and only during this actual Beijing date.
        if (SocialDayRules.DateAt(now) == pair.Today)
        {
            var time = TimeOnly.FromDateTime(now.ToOffset(SocialDayRules.BeijingOffset).DateTime);
            if (time >= new TimeOnly(23, 50)) return new(SocialCopyKind.Night2350, "最后10分钟！队友，快救一下！");
            if (time >= new TimeOnly(23, 30)) return new(SocialCopyKind.Night2330, "危！再不背几个，这局可就悬了！");
            if (time >= new TimeOnly(23, 0)) return new(SocialCopyKind.Night2300, "今晚还苟不苟了？再不学真要寄了！");
        }
        return friend ? new(SocialCopyKind.WaitingSelf, "队友都苟住了，你可别掉链子！")
            : new(SocialCopyKind.NeitherDone, "今天还没开苟，你俩倒是挺淡定");
    }

    public static SocialFriendListCopy FriendList(SocialPairView pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var both = DoneToday(pair.Self, pair.SelfDone, pair.Today, DateTimeOffset.MaxValue) &&
            DoneToday(pair.Friend, pair.FriendDone, pair.Today, DateTimeOffset.MaxValue);
        var one = DoneToday(pair.Self, pair.SelfDone, pair.Today, DateTimeOffset.MaxValue) ||
            DoneToday(pair.Friend, pair.FriendDone, pair.Today, DateTimeOffset.MaxValue);
        var success = both && pair.CloudSuccess && pair.SuccessDate == pair.Today &&
            pair.Sync == SocialSyncStatus.Confirmed && pair.Lifecycle == PairLifecycle.Active;
        var status = pair.Lifecycle == PairLifecycle.Ended ? "散伙了，这局到此结束" :
            pair.Sync == SocialSyncStatus.Failed ? "网络掉链子了，续命记录暂时还没传上去" :
            pair.Sync == SocialSyncStatus.Pending || both && !success ? "别慌，你的续命记录还在路上！" :
            pair.Lifecycle == PairLifecycle.Broken ? "寄！这局还是没苟住……" :
            success ? "今日双双存活" : one ? "等待队友救援" : "今日尚未续命";
        // A relation whose first run has not opened yet is an ordinary zero-day wait. It is
        // never reported as broken, and it is never reported as a streak that already exists.
        var continuity = pair.RunId is null
            ? $"你和{FriendName(pair)}的续命局还没开始 · 0 天等待"
            : $"你和{FriendName(pair)}已经连续苟住了{pair.Streak}天";
        return new(continuity, status);
    }

    private static string FriendName(SocialPairView pair) => string.IsNullOrWhiteSpace(pair.FriendName) ? "TA" : pair.FriendName;
    private static bool HasValidDate(SocialCompletion? completion, DateOnly date) =>
        completion is not null && completion.Date == date && SocialDayRules.DateAt(completion.EligibleAt) == date;
    /// <summary>The server's own done flag is authoritative for whether today counts; a
    /// completion only adds the instant used for timing. A done day with no usable instant is
    /// still done, and the observed instant still has to have happened before we announce it.</summary>
    private static bool DoneToday(SocialCompletion? completion, bool done, DateOnly date, DateTimeOffset now)
    {
        // A recorded instant decides on its own: it must belong to the stated day and must
        // already have happened. Only when the server reports no instant at all does the day's
        // own done flag decide, which is how a complete-but-untimed day is still counted.
        if (completion is not null) return HasValidDate(completion, date) && completion.EligibleAt <= now;
        return done;
    }
    /// <summary>An untimed completion is stamped with the day's start, so it is never extreme.</summary>
    private static bool IsExtremeLearning(SocialCompletion completion, DateOnly date, DateTimeOffset now) =>
        HasValidDate(completion, date) && completion.EligibleAt <= now &&
        completion.Cause == QualificationCause.LearningComplete &&
        TimeOnly.FromDateTime(completion.EligibleAt.ToOffset(SocialDayRules.BeijingOffset).DateTime) >= new TimeOnly(23, 50);
}
