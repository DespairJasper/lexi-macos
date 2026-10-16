using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Lexi.Core;
using Lexi.Social;

internal static class SocialCoordinatorTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"lexi-coordinator-test-"+Guid.NewGuid().ToString("N"));
        var actor=Guid.NewGuid();var now=DateTimeOffset.Parse("2026-10-11T04:00:00Z");var elapsed=TimeSpan.Zero;var day=new DateOnly(2026,10,11);
        var loginActor=actor;
        var options=new SocialCloudOptions(new Uri("https://abcdefghijklmnopqrst.supabase.co"),"sb_publishable_test");
        Guid? remoteBinding=null;var failBind=true;var failSubmit=false;var wrongHash=false;var receiptStatus="accepted";var submitCalls=0;var secondaryCalls=0;string? sentPayload=null;
        TaskCompletionSource<HttpResponseMessage>? delayed=null;TaskCompletionSource<HttpResponseMessage>? delayedSecondary=null;CancellationToken secondaryToken=default;var secondaryEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets=new MemorySecrets();
        string Session() => JsonSerializer.Serialize(new{access_token="e30."+Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{sub=loginActor}))).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic",refresh_token="synthetic-refresh",expires_at=now.AddDays(1).ToUnixTimeSeconds(),user=new{id=loginActor}});
        object Binding() => new{binding_id=remoteBinding,bound_at="2026-10-11T04:00:00Z",first_day="2026-10-11"};
        HttpResponseMessage Response(object value) => new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
        using var http=new HttpClient(new Handler(async(request,cancel)=>
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path=="/auth/v1/token")return new(HttpStatusCode.OK){Content=new StringContent(Session())};
            if(path.EndsWith("lexi_status"))return Response(new{binding=remoteBinding is null?null:Binding(),server_now=now});
            if(path.EndsWith("lexi_bind"))
            {
                using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel));remoteBinding=json.RootElement.GetProperty("p_binding_id").GetGuid();
                if(failBind){failBind=false;throw new HttpRequestException("synthetic response lost after server commit");}
                return Response(Binding());
            }
            if(path.EndsWith("lexi_submit"))
            {
                submitCalls++;using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel));
                sentPayload=json.RootElement.GetProperty("p_payload").GetString()!;
                if(failSubmit)throw new HttpRequestException("synthetic network failure");
                var receipt=new{batch_id=json.RootElement.GetProperty("p_batch_id").GetGuid(),hash=wrongHash?new string('0',64):Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sentPayload))).ToLowerInvariant(),status=receiptStatus,received_at=now};
                if(delayed is not null){entered.TrySetResult();return await delayed.Task;}
                return Response(receipt);
            }
            if(path.EndsWith("lexi_submit_duration") || path.EndsWith("lexi_submit_curve"))
            {
                secondaryCalls++;
                if(delayedSecondary is not null){secondaryToken=cancel;secondaryEntered.TrySetResult();return await delayedSecondary.Task;}
                return Response("accepted");
            }
            return Response(new{});
        }));
        using var client=new SupabaseSocialClient(options,secrets,http,()=>now);
        try
        {
            var flow=new SocialOAuthFlow();var start=flow.Begin(options.ProjectUrl,now);
            var query=start.AuthorizationUri.Query.TrimStart('?').Split('&').Select(v=>v.Split('=',2)).ToDictionary(v=>v[0],v=>Uri.UnescapeDataString(v[1]));
            var flowId=new Uri(query["redirect_to"]).Query.TrimStart('?').Split('=')[1];
            flow.TryConsumeCallback(new Uri(SocialOAuthFlow.CallbackBase+"?flow_id="+flowId+"&code=synthetic-code"),now,out var exchange,out _);
            await client.ExchangeAsync(exchange!);
            var store=new SocialStore(root);var coordinator=new SocialSyncCoordinator(store,client,()=>now,()=>elapsed);
            var exclusions=new Dictionary<string,IReadOnlySet<string>>{{"p",new HashSet<string>{"never-upload-word"}}};
            try{await coordinator.RequestBindingAsync(actor,"test",exclusions,true);check(false,"lost bind reply must not activate");}catch(HttpRequestException){check(store.GetBinding(actor)!.BoundAt is null,"lost bind response leaves durable unactivated intent");}
            var persisted=store.GetBinding(actor)!;
            store=new SocialStore(root);coordinator=new SocialSyncCoordinator(store,client,()=>now,()=>elapsed);
            var active=await coordinator.ResumeBindingAsync(actor);
            check(store.LoadClockDiagnostic(actor)?.ServerNow==now,"last server/monotonic anchor persists for diagnostics only");
            check(active.BindingId==persisted.BindingId && active.BoundAt==now,"status reconciliation recovers same binding after cloud commit/crash");
            remoteBinding=Guid.NewGuid();
            try{await coordinator.ResumeBindingAsync(actor);check(false,"foreign cloud binding must not activate");}catch(InvalidOperationException){check(store.GetBinding(actor)!.BindingId==persisted.BindingId,"foreign cloud binding leaves authorized dataset unchanged");}
            remoteBinding=persisted.BindingId;
            check(store.GetBindingExclusions(actor,"p",day).Contains("never-upload-word"),"cloud recovery cannot erase original exclusions");
            store.ConfirmBindingBaseline(actor,active.BindingId,active.BoundAt!.Value,exclusions);
            now=now.AddMinutes(1);elapsed=elapsed.Add(TimeSpan.FromMinutes(1));
            var facts=new SocialDayObservation(day,[new("p",true,1,1,now)],1,0,30,now,now,QualificationCause.LearningComplete);
            var projection=coordinator.ObserveLocal(actor,facts);
            check(projection.Summary.TimeTrusted,"status server anchor grants same-process monotonic time trust");
            var queued=store.LoadPending(actor).Single();wrongHash=true;
            var mismatch=await coordinator.FlushAsync(actor);
            check(mismatch.Error is not null && store.LoadPending(actor).Count==1,"wrong cloud hash cannot acknowledge local payload");
            check(sentPayload==queued.RawPayload,"RPC sends the exact stored string used for SHA256");
            var deferred=await coordinator.FlushAsync(actor);
            check(deferred.Deferred && submitCalls==1,"failed receipt validation activates bounded backoff");
            wrongHash=false;now=now.AddMinutes(6);elapsed=elapsed.Add(TimeSpan.FromMinutes(6));
            var accepted=await coordinator.FlushAsync(actor);
            check(accepted.Accepted==1 && accepted.Pending==0,"matching accepted receipt acknowledges exactly own batch");
            check((await coordinator.FlushAsync(actor)).Accepted==0,"duplicate flush does not fabricate another accepted event");
            coordinator.ObserveLocal(actor,facts with{ReviewWords=1});receiptStatus="late";
            var late=await coordinator.FlushAsync(actor);
            check(late.Late==1 && late.Accepted==0 && late.Pending==0,"late receipt clears transport queue but never claims accepted success");
            coordinator.ObserveLocal(actor,facts with{ReviewWords=2});failSubmit=true;
            var failed=await coordinator.FlushAsync(actor);
            check(failed.Error is not null && new SocialStore(root).LoadPending(actor).Count==1,"offline failure cannot erase durable observation");
            failSubmit=false;now=now.AddMinutes(6);elapsed=elapsed.Add(TimeSpan.FromMinutes(6));receiptStatus="stale";
            check((await coordinator.FlushAsync(actor)).Stale==1,"stale receipt is visible separately from accepted");
            coordinator=new SocialSyncCoordinator(store,client,()=>now,()=>elapsed);
            check(!coordinator.RecoverLocal(actor,facts with{ReviewWords=3}).Summary.TimeTrusted,"coordinator restart does not restore prior trusted clock");
            coordinator.QueueDuration(actor,new(day,31,"partial"));
            coordinator.QueueCurve(actor,new(now,5,Enumerable.Repeat(0.7,121).ToArray()));
            var off=await coordinator.FlushSecondaryAsync(actor);
            check(off.Accepted==0 && secondaryCalls==0 && off.Pending==2,"all-off aggregate sharing stops secondary network while preserving local facts");
            coordinator.SetSecondarySharing(actor,true,true);
            var secondary=await coordinator.FlushSecondaryAsync(actor);
            check(secondary.Accepted==2 && secondary.Pending==0,"eligible duration and curve use typed RPCs and durably acknowledge");
            check((await coordinator.FlushSecondaryAsync(actor)).Accepted==0 && secondaryCalls==2,"accepted unchanged secondary snapshots do not upload again");
            coordinator.SetSecondarySharing(actor,false,false);
            check((await coordinator.FlushSecondaryAsync(actor)).Accepted==0 && secondaryCalls==2,"turning aggregate sharing off blocks retained data immediately");
            coordinator.SetSecondarySharing(actor,true,true);
            check((await coordinator.FlushSecondaryAsync(actor)).Accepted==2,"reenabling sharing republishes retained eligible secondary snapshots");
            coordinator.QueueDuration(actor,new(day,32,"partial"));
            delayedSecondary=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingSecondary=coordinator.FlushSecondaryAsync(actor);await secondaryEntered.Task;
            coordinator.SetSecondarySharing(actor,false,false);
            check(secondaryToken.IsCancellationRequested,"revoking aggregate sharing cancels in-flight secondary request token");
            delayedSecondary.SetResult(Response("accepted"));
            try{await pendingSecondary;check(false,"revoked secondary reply must not acknowledge");}catch(OperationCanceledException){check(store.LoadSecondaryPending(actor,now).Count>0,"late secondary reply after revocation preserves local snapshot");}
            delayedSecondary=null;
            delayed=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=coordinator.FlushAsync(actor);await entered.Task;
            check((await coordinator.FlushAsync(actor)).Deferred,"concurrent flush returns without a second transmission");
            await client.LogoutAsync();delayed.SetResult(Response(new{batch_id=store.LoadPending(actor).Single().BatchId,hash=store.LoadPending(actor).Single().Hash,status="accepted",received_at=now}));
            try{await pending;check(false,"logout must cancel account upload");}catch(OperationCanceledException){check(store.LoadPending(actor).Count==1,"late A receipt after logout cannot acknowledge");}
            loginActor=Guid.NewGuid();await client.ExchangeAsync(exchange!);
            var callsBeforeOther=submitCalls;
            try{await coordinator.FlushAsync(loginActor);check(false,"B must not flush A local dataset");}catch(InvalidOperationException){check(submitCalls==callsBeforeOther && store.LoadPending(actor).Count==1,"B session cannot send or remove A queue");}

        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class MemorySecrets:ISecureSecretStore {private string? value;public string? Get()=>value;public void Set(string s)=>value=s;public void Delete()=>value=null;}
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c)=>send(r,c);}
}
