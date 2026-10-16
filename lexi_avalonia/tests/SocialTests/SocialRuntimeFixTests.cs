using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexi.Core;
using Lexi.Social;

internal static class SocialRuntimeFixTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "lexi-runtime-fix-test-" + Guid.NewGuid().ToString("N"));
        var actor = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-10-11T04:00:00Z");
        var day = new DateOnly(2026, 10, 11);
        var mode = "summary"; var submits = 0; var secondarySubmits = 0;
        var timeoutTokenWasCancelled = false;
        var callerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/token")
            {
                var jwt = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = actor })))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
                return Reply(new { access_token = jwt, refresh_token = "synthetic-refresh", expires_at = now.AddDays(1).ToUnixTimeSeconds(), user = new { id = actor } });
            }
            if (mode == "caller")
            {
                callerEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (path.EndsWith("lexi_submit"))
            {
                submits++;
                if (mode == "summary" && submits == 2)
                {
                    timeoutTokenWasCancelled |= token.IsCancellationRequested;
                    throw new TaskCanceledException("synthetic timeout with uncancelled effective request token");
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var payload = body.RootElement.GetProperty("p_payload").GetString()!;
                return Reply(new { batch_id = body.RootElement.GetProperty("p_batch_id").GetGuid(),
                    hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(), status = "accepted", received_at = now });
            }
            secondarySubmits++;
            if (secondarySubmits == 2)
            {
                timeoutTokenWasCancelled |= token.IsCancellationRequested;
                throw new TaskCanceledException("synthetic secondary timeout");
            }
            return Reply("accepted");
        }));
        using var client = new SupabaseSocialClient(new(new Uri("https://abcdefghijklmnopqrst.supabase.co"), "sb_publishable_test"), new MemorySecrets(), http, () => now);
        try
        {
            var flow = new SocialOAuthFlow(); var begin = flow.Begin(new Uri("https://abcdefghijklmnopqrst.supabase.co"), now);
            var redirect = begin.AuthorizationUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]))["redirect_to"];
            var nonce = new Uri(redirect).Query.TrimStart('?').Split('=')[1];
            flow.TryConsumeCallback(new Uri(SocialOAuthFlow.CallbackBase + "?flow_id=" + nonce + "&code=synthetic-code"), now, out var exchange, out _);
            await client.ExchangeAsync(exchange!);
            SocialStore Store(string name)
            {
                var store = new SocialStore(Path.Combine(root, name)); var binding = Guid.NewGuid();
                store.PrepareBinding(actor, binding, "test", now, new Dictionary<string, IReadOnlySet<string>>(), true);
                store.ActivateBinding(actor, binding, now, day);
                store.ConfirmBindingBaseline(actor, binding, now, new Dictionary<string, IReadOnlySet<string>>());
                return store;
            }
            void Seed(SocialStore store, int count)
            {
                var binding = store.GetBinding(actor)!.BindingId;
                for (var revision = 1; revision <= count; revision++)
                    store.Enqueue(actor, Guid.NewGuid(), new(binding, day, revision, 1, 1, 1, 0, now.AddMinutes(1), QualificationCause.LearningComplete));
            }
            var summaryStore = Store("summary"); Seed(summaryStore, 2);
            var coordinator = new SocialSyncCoordinator(summaryStore, client, () => now, () => TimeSpan.Zero);
            SocialFlushResult? result = null;
            try { result = await coordinator.FlushAsync(actor); }
            catch (OperationCanceledException) { check(false, "internal summary timeout must return Error rather than propagate cancellation"); }
            check(result is { Error: not null, Accepted: 1, Pending: 1 }, "summary timeout preserves partial ack and reports remaining queue");
            check(summaryStore.LoadPending(actor).Count == 1 && summaryStore.LoadReceipts(actor).Count == 1, "summary timeout keeps unsent batch and committed receipt");
            var calls = submits;
            check((await coordinator.FlushAsync(actor)).Deferred && submits == calls, "summary timeout immediately enters bounded backoff");

            mode = "secondary";
            var secondaryStore = Store("secondary"); coordinator = new(secondaryStore, client, () => now, () => TimeSpan.Zero);
            coordinator.SetSecondarySharing(actor, true, true);
            coordinator.QueueDuration(actor, new(day, 30, "partial"));
            coordinator.QueueCurve(actor, new(now, 1, Enumerable.Repeat(0.5, 121).ToArray()));
            SocialSecondaryFlushResult? secondary = null;
            try { secondary = await coordinator.FlushSecondaryAsync(actor); }
            catch (OperationCanceledException) { check(false, "internal secondary timeout must return Error rather than propagate cancellation"); }
            check(secondary is { Error: not null, Accepted: 1, Pending: 1 }, "secondary timeout preserves partial ack and returns Error");
            check(secondaryStore.LoadSecondaryPending(actor, now).Count == 1, "secondary timeout retains unsent facts");
            calls = secondarySubmits;
            check((await coordinator.FlushSecondaryAsync(actor)).Deferred && secondarySubmits == calls, "secondary timeout immediately enters bounded backoff");
            check(!timeoutTokenWasCancelled, "timeout fixtures use an uncancelled effective request token");

            mode = "count";
            var countStore = Store("count"); Seed(countStore, 120);
            // Owner-scoped totals must exclude a foreign-row fixture even if the sidecar is damaged.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "count", "social.sqlite3"), Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO social_batches SELECT $batch,$other,study_date,revision,payload_hash,payload,0 FROM social_batches LIMIT 1";
                command.Parameters.AddWithValue("$batch", Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("$other", Guid.NewGuid().ToString("D")); command.ExecuteNonQuery();
            }
            coordinator = new(countStore, client, () => now, () => TimeSpan.Zero);
            check(countStore.LoadPending(actor).Count == 50, "send page limit remains 50");
            check(countStore.CountPending(actor) == 120, "dedicated scalar count reports full owner queue without reading payloads");
            var count = await coordinator.FlushAsync(actor);
            check(count is { Accepted: 50, Pending: 70 }, "Pending reports owner total 70 after sending 50 of 120");
            count = await coordinator.FlushAsync(actor);
            check(count is { Accepted: 50, Pending: 20 }, "Pending reports remaining owner total on next page");
            count = await coordinator.FlushAsync(actor);
            check(count is { Accepted: 20, Pending: 0 }, "zero owner pending excludes foreign-row fixture");
            check(countStore.CountPending(actor) == 0, "dedicated owner pending count reaches zero");
            try { countStore.CountPending(Guid.NewGuid()); check(false, "dedicated count must reject foreign owner"); }
            catch (InvalidOperationException) { check(true, "dedicated count rejects foreign owner"); }
            try { countStore.CountPending(Guid.Empty); check(false, "dedicated count must reject empty actor"); }
            catch (ArgumentException) { check(true, "dedicated count rejects empty actor"); }
            try { await coordinator.FlushAsync(Guid.NewGuid()); check(false, "foreign owner pending access must be rejected"); }
            catch (InvalidOperationException) { check(true, "foreign owner pending access is rejected"); }

            mode = "caller";
            var callerStore = Store("caller"); Seed(callerStore, 1);
            coordinator = new(callerStore, client, () => now, () => TimeSpan.Zero);
            using var cancel = new CancellationTokenSource();
            var pending = coordinator.FlushAsync(actor, cancel.Token); await callerEntered.Task; cancel.Cancel();
            try { await pending; check(false, "active caller cancellation must propagate"); }
            catch (OperationCanceledException) { check(callerStore.LoadPending(actor).Count == 1, "active caller cancellation propagates and preserves queue"); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class MemorySecrets : ISecureSecretStore { private string? value; public string? Get() => value; public void Set(string s) => value = s; public void Delete() => value = null; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation); }
}
