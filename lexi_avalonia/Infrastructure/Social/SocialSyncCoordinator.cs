using System.Diagnostics;
using System.Text.Json;

namespace Lexi.Social;

/// <summary>Owns local binding/projections and serialized, account-fenced summary transport.
/// Receipts confirm transport only; pair success and streaks remain cloud presentation data.</summary>
public sealed partial class SocialSyncCoordinator
{
    private readonly SocialStore _store;
    private readonly SupabaseSocialClient _cloud;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan> _elapsed;
    private readonly SocialClock _clock = new();
    private readonly object _clockGate = new();
    private readonly SemaphoreSlim _network = new(1, 1);
    private TimeSpan _retryAt;
    private int _failures;
    private readonly object _sharingGate = new();
    private CancellationTokenSource _sharingCancellation = new();
    private TimeSpan _secondaryRetryAt;
    private int _secondaryFailures;

    public SocialSyncCoordinator(SocialStore store, SupabaseSocialClient cloud,
        Func<DateTimeOffset>? now = null, Func<TimeSpan>? elapsed = null)
    {
        _store = store; _cloud = cloud; _now = now ?? (() => DateTimeOffset.UtcNow);
        var watch = Stopwatch.StartNew(); _elapsed = elapsed ?? (() => watch.Elapsed);
    }

    public async Task<SocialBindingState> RequestBindingAsync(Guid actor, string nickname,
        IReadOnlyDictionary<string,IReadOnlySet<string>> exclusions, bool explicitlyAuthorized,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var commit = _cloud.AccountCommitFence(actor);
        // The entire consent snapshot commits before the first HTTP request.
        commit(() => _store.PrepareBinding(actor, Guid.NewGuid(), nickname, _now(), exclusions, explicitlyAuthorized));
        return await ResumeBindingAsync(actor, cancellation);
    }

    public async Task<SocialBindingState> ResumeBindingAsync(Guid actor, CancellationToken cancellation = default)
    {
        var commit = _cloud.AccountCommitFence(actor);
        var intent = _store.GetBinding(actor) ?? throw new InvalidOperationException("Explicit dataset binding is required.");
        await _network.WaitAsync(cancellation);
        try
        {
            var status = await _cloud.RpcAsync(actor, "lexi_status", new { }, cancellation);
            var serverNow = status.GetProperty("server_now").GetDateTimeOffset();
            commit(() =>
            {
                var localNow = _now(); var elapsed = _elapsed();
                lock (_clockGate)
                {
                    _clock.Observe(serverNow, localNow, elapsed);
                    _store.RecordClockDiagnostic(actor, new(serverNow, localNow, elapsed, _clock.IsTrusted(localNow, elapsed)));
                }
            });
            var binding = status.GetProperty("binding");
            if (binding.ValueKind == JsonValueKind.Null)
            {
                if (intent.BoundAt is not null) throw new InvalidOperationException("Active local binding is missing on the server.");
                binding = await _cloud.RpcAsync(actor, "lexi_bind", new { p_binding_id = intent.BindingId, p_nickname = intent.Nickname }, cancellation);
            }
            var id = binding.GetProperty("binding_id").GetGuid();
            var boundAt = binding.GetProperty("bound_at").GetDateTimeOffset();
            var firstDay = DateOnly.ParseExact(binding.GetProperty("first_day").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            cancellation.ThrowIfCancellationRequested();
            commit(() => _store.ActivateBinding(actor, id, boundAt, firstDay));
            return _store.GetBinding(actor)!;
        }
        finally { _network.Release(); }
    }

    /// <summary>IDs must be completed at or BEFORE the actual cloud BoundAt, read from durable local facts.
    /// Never include completions after BoundAt while HTTP was still waiting. Confirm once, then use FromPlan.</summary>
    public void ConfirmBindingBaseline(Guid actor, Guid bindingId, DateTimeOffset boundAt,
        IReadOnlyDictionary<string,IReadOnlySet<string>> completedIdsByPlan) =>
        _store.ConfirmBindingBaseline(actor, bindingId, boundAt, completedIdsByPlan);

    public SocialDayProjection ObserveLocal(Guid actor, SocialDayObservation facts)
    {
        bool trusted;
        lock (_clockGate) trusted = _clock.IsTrusted(_now(), _elapsed());
        return _store.ObserveDay(actor, facts, trusted);
    }

    /// <summary>Use when reconstructing from original saved learning facts after startup/crash.
    /// Correction/untrusted prevents a new extreme event being inferred from recovery time.</summary>
    public SocialDayProjection RecoverLocal(Guid actor, SocialDayObservation facts) =>
        _store.ObserveDay(actor, facts, false, recovery: true);

    // Aggregate flags mean at least one currently accepted active friend permits this direction.
    // UI must persist the cloud-confirmed aggregate, and disable locally immediately on revocation.
    public void SetSecondarySharing(Guid actor, bool time, bool curve)
    {
        lock (_sharingGate)
        {
            if (_store.GetSecondarySharing(actor) == new SocialSecondarySharing(time, curve)) return;
            _store.SetSecondarySharing(actor, time, curve);
            _sharingCancellation.Cancel(); _sharingCancellation.Dispose(); _sharingCancellation = new();
        }
    }
    public bool QueueDuration(Guid actor, SocialDurationChange change) => _store.QueueDuration(actor, change, _now());
    public bool QueueCurve(Guid actor, SocialCurveChange change) => _store.QueueCurve(actor, change, _now());

    public async Task<SocialSecondaryFlushResult> FlushSecondaryAsync(Guid actor, CancellationToken cancellation = default)
    {
        var commit = _cloud.AccountCommitFence(actor);
        var initial = _store.LoadSecondaryPending(actor, _now()).Count;
        if (_elapsed() < _secondaryRetryAt || !await _network.WaitAsync(0, cancellation)) return new(0, 0, 0, initial, Deferred: true);
        CancellationToken sharingToken;
        lock (_sharingGate) sharingToken = _sharingCancellation.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, sharingToken);
        var accepted = 0; var late = 0; var notShared = 0;
        try
        {
            foreach (var batch in _store.LoadSecondaryPending(actor, _now()))
            {
                linked.Token.ThrowIfCancellationRequested();
                var sharing = _store.GetSecondarySharing(actor);
                if (batch.Kind == "duration" ? !sharing.Time : !sharing.Curve) continue;
                using var payload = JsonDocument.Parse(batch.Payload);
                var reply = await _cloud.RpcAsync(actor, batch.Kind == "duration" ? "lexi_submit_duration" : "lexi_submit_curve", payload.RootElement, linked.Token);
                var status = reply.GetString();
                if (status is not ("accepted" or "late" or "not_shared")) throw new InvalidOperationException("Invalid secondary receipt.");
                lock (_sharingGate)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    commit(() => _store.AcknowledgeSecondary(actor, batch));
                }
                if (status == "accepted") accepted++; else if (status == "late") late++; else notShared++;
            }
            _secondaryFailures = 0; _secondaryRetryAt = TimeSpan.Zero;
            return new(accepted, late, notShared, _store.LoadSecondaryPending(actor, _now()).Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or SocialCloudException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
        {
            _secondaryFailures = Math.Min(_secondaryFailures + 1, 9);
            _secondaryRetryAt = _elapsed() + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, _secondaryFailures)));
            return new(accepted, late, notShared, _store.LoadSecondaryPending(actor, _now()).Count, Error: "社交附加统计暂未确认，数据已保存在本地。");
        }
        finally { _network.Release(); }
    }

    public async Task<SocialFlushResult> FlushAsync(Guid actor, CancellationToken cancellation = default)
    {
        var commit = _cloud.AccountCommitFence(actor);
        var initial = _store.CountPending(actor);
        if (_elapsed() < _retryAt || !await _network.WaitAsync(0, cancellation)) return new(0, 0, 0, initial, Deferred: true);
        var accepted = 0; var stale = 0; var late = 0;
        try
        {
            foreach (var batch in _store.LoadPending(actor))
            {
                cancellation.ThrowIfCancellationRequested();
                var reply = await _cloud.RpcAsync(actor, "lexi_submit", new { p_batch_id = batch.BatchId, p_payload = batch.RawPayload }, cancellation);
                var id = reply.GetProperty("batch_id").GetGuid();
                var hash = reply.GetProperty("hash").GetString();
                var status = reply.GetProperty("status").GetString();
                var receivedAt = reply.GetProperty("received_at").GetDateTimeOffset();
                if (id != batch.BatchId || hash != batch.Hash || status is not ("accepted" or "stale" or "late"))
                    throw new InvalidOperationException("Cloud receipt did not match the pending batch.");
                cancellation.ThrowIfCancellationRequested();
                commit(() => _store.RecordReceipt(actor, id, hash, status, receivedAt));
                if (status == "accepted") accepted++; else if (status == "stale") stale++; else late++;
            }
            _failures = 0; _retryAt = TimeSpan.Zero;
            return new(accepted, stale, late, _store.CountPending(actor));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or SocialCloudException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
        {
            _failures = Math.Min(_failures + 1, 9);
            _retryAt = _elapsed() + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, _failures)));
            return new(accepted, stale, late, _store.CountPending(actor), Error: "社交同步暂未确认，学习记录已保存在本地。");
        }
        finally { _network.Release(); }
    }
}
