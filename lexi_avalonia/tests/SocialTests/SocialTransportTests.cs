using System.Net;
using System.Text;
using System.Text.Json;
using Lexi.Core;
using Lexi.Social;

internal static class SocialTransportTests
{
    public static async Task Run(Action<bool,string> check)
    {
        await RunRegressions(check);
        var now=DateTimeOffset.Parse("2026-10-11T04:00:00Z");
        var actor=Guid.NewGuid();
        var key="sb_publishable_test";
        var options=new SocialCloudOptions(new Uri("https://abcdefghijklmnopqrst.supabase.co"),key);
        foreach(var bad in new[]{"sb_secret_test", "service_role", ""})
        {
            try { _=new SocialCloudOptions(options.ProjectUrl,bad); check(false,"non-public cloud keys rejected"); }
            catch(ArgumentException) { check(true,"non-public cloud keys rejected"); }
        }
        var secrets=new MemorySecrets();
        var calls=new List<(string Path,string Body,string? Authorization)>();
        string Token(string suffix) => Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))+"."+
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{sub=actor,exp=now.AddHours(1).ToUnixTimeSeconds()}))).TrimEnd('=').Replace('+','-').Replace('/','_')+"."+suffix;
        string Session(string suffix) => JsonSerializer.Serialize(new{access_token=Token(suffix),refresh_token="refresh-"+suffix,expires_at=now.AddHours(1).ToUnixTimeSeconds(),provider_token="github-provider-secret",provider_refresh_token="github-refresh-secret",user=new{id=actor,email="private@example.invalid",user_metadata=new{user_name="test-person"}}});
        var handler=new FakeHandler(async (request,cancel)=>
        {
            calls.Add((request.RequestUri!.PathAndQuery,request.Content==null?"":await request.Content.ReadAsStringAsync(cancel),request.Headers.Authorization?.Parameter));
            var body=request.RequestUri.AbsolutePath.EndsWith("/token")?Session(calls.Count.ToString()):"{\"ok\":true}";
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
        });
        using var http=new HttpClient(handler);
        using var client=new SupabaseSocialClient(options,secrets,http,()=>now);
        check(client.UserId is null && calls.Count==0,"constructing social client does not log in or upload");
        var flow=new SocialOAuthFlow();var start=flow.Begin(options.ProjectUrl,now);
        var values=start.AuthorizationUri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1]));
        var redirect=new Uri(values["redirect_to"]);
        var nonce=redirect.Query.TrimStart('?').Split('=')[1];
        check(flow.TryConsumeCallback(new Uri(SocialOAuthFlow.CallbackBase+"?flow_id="+nonce+"&code=test-code"),now,out var exchange,out _),"test exchange available");
        await client.ExchangeAsync(exchange!);
        check(client.UserId==actor && client.Nickname=="test-person","token exchange authenticates intended user");
        check(calls[0].Path=="/auth/v1/token?grant_type=pkce","exchange uses official PKCE endpoint");
        using(var body=JsonDocument.Parse(calls[0].Body))
        {
            check(body.RootElement.GetProperty("auth_code").GetString()=="test-code" && body.RootElement.GetProperty("code_verifier").GetString()==exchange!.Verifier,"PKCE verifier sent only to token endpoint");
            check(body.RootElement.EnumerateObject().Count()==2,"exchange does not send local learning records");
        }
        check(secrets.Value!=null && secrets.Value.Contains("refresh-1"),"session persisted through supplied secure store");
        check(!secrets.Value!.Contains("github-provider-secret") && !secrets.Value.Contains("github-refresh-secret") && !secrets.Value.Contains("private@example.invalid"),"provider credentials and contact metadata are never persisted");
        check(!client.ToString()!.Contains("refresh-1"),"client diagnostics redact session");
        using var restored=new SupabaseSocialClient(options,secrets,http,()=>now);
        check(restored.UserId==actor,"session restores for the same project only");
        using(var other=new SupabaseSocialClient(new SocialCloudOptions(new Uri("https://different.supabase.co"),key),secrets,http,()=>now))
            check(other.UserId is null,"another project cannot reuse session envelope");
        var result=await client.RpcAsync(actor,"lexi_friends",new{});
        check(result.GetProperty("ok").GetBoolean() && calls[^1].Authorization==Token("1"),"RPC carries current actor bearer");
        try {await client.RpcAsync(Guid.NewGuid(),"lexi_friends",new{});check(false,"foreign actor blocked");}
        catch(InvalidOperationException) {check(true,"foreign actor blocked before network");}
        try {await client.RpcAsync(actor,"arbitrary_function",new{});check(false,"unknown RPC blocked");}
        catch(ArgumentException) {check(true,"RPC endpoints allowlist");}
        now=now.AddHours(1);
        await client.RpcAsync(actor,"lexi_friends",new{});
        check(calls[^2].Path=="/auth/v1/token?grant_type=refresh_token" && calls[^2].Body.Contains("refresh-1"),"expired token refreshed once before RPC");
        check(calls[^1].Authorization!=Token("1"),"refresh uses rotated token");
        var beforeInvalidAvatar=calls.Count;
        try {await client.UploadAvatarAsync(actor,"../foreign.webp",new byte[200001]);check(false,"oversized avatar rejected");}
        catch(ArgumentException){check(calls.Count==beforeInvalidAvatar,"invalid avatar never transmitted");}
        await client.LogoutAsync();
        check(client.UserId is null && secrets.Value is null,"logout removes only social session");
        var count=calls.Count;
        try { await client.RpcAsync(actor,"lexi_friends",new{}); check(false,"logged-out rpc blocked"); }
        catch(InvalidOperationException) {check(calls.Count==count,"logged-out client never uploads");}
        check(calls[^1].Path=="/auth/v1/logout?scope=local","logout revokes this session only");

        var waiting=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var raceHttp=new HttpClient(new FakeHandler((_,__)=>waiting.Task));
        using var race=new SupabaseSocialClient(options,new MemorySecrets(),raceHttp,()=>now);
        var pending=race.ExchangeAsync(exchange!);
        await race.LogoutAsync();
        waiting.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Session("race"))});
        try {await pending;check(false,"cancelled exchange rejected");}
        catch(OperationCanceledException){check(race.UserId is null,"late exchange cannot restore a logged-out session");}
    }
    private static async Task RunRegressions(Action<bool,string> check)
    {
        var failures=new List<string>();
        var assertions=0;
        void Verify(bool value,string message) { assertions++; if(!value) failures.Add(message); }
        var now=DateTimeOffset.Parse("2026-10-11T04:00:00Z");
        var actorA=Guid.Parse("11111111-1111-1111-1111-111111111111");
        var actorB=Guid.Parse("22222222-2222-2222-2222-222222222222");
        var options=new SocialCloudOptions(new Uri("https://abcdefghijklmnopqrst.supabase.co"),"sb_publishable_test");
        var exchange=Exchange(options,now);
        string Session(Guid actor,string suffix,int seconds=3600) => JsonSerializer.Serialize(new {
            access_token="e30."+Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{sub=actor}))).TrimEnd('=').Replace('+','-').Replace('/','_')+"."+suffix,
            refresh_token="synthetic-refresh-"+suffix, expires_at=now.AddSeconds(seconds).ToUnixTimeSeconds(),user=new{id=actor}
        });
        HttpResponseMessage Response(string json) => new(HttpStatusCode.OK){Content=new StringContent(json)};
        TaskCompletionSource<HttpResponseMessage> Waiting() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Rejected(Task task)
        {
            try { await task; return false; }
            catch(InvalidOperationException) { return true; }
        }
        async Task<bool> Cancelled(Task task)
        {
            try { await task; return false; }
            catch(OperationCanceledException) { return true; }
        }

        // Removing the logged-in exchange guard permits B's HTTP call and late A refresh overwrite.
        var delayedRefresh=Waiting();var pkceCalls=0;var rpcCalls=0;
        using(var http=new HttpClient(new FakeHandler((request,_)=>
        {
            if(request.RequestUri!.Query.Contains("refresh_token")) return delayedRefresh.Task;
            if(request.RequestUri.Query.Contains("pkce")) return Task.FromResult(Response(++pkceCalls==1?Session(actorA,"A",30):Session(actorB,"B")));
            rpcCalls++;return Task.FromResult(Response("{}"));
        })))
        using(var client=new SupabaseSocialClient(options,new MemorySecrets(),http,()=>now))
        {
            await client.ExchangeAsync(exchange);
            var pending=client.RpcAsync(actorA,"lexi_friends",new{});
            var rejected=await Rejected(client.ExchangeAsync(exchange));
            delayedRefresh.SetResult(Response(Session(actorA,"A-rotated")));
            await pending;
            Verify(rejected && pkceCalls==1 && rpcCalls==1 && client.UserId==actorA,"logged-in exchange rejected before HTTP while A refresh waits");
        }

        // Logout and a fresh B exchange invalidate both A's refresh publication and following RPC.
        delayedRefresh=Waiting();pkceCalls=0;rpcCalls=0;var switchedSecrets=new MemorySecrets();
        using(var http=new HttpClient(new FakeHandler((request,_)=>
        {
            if(request.RequestUri!.Query.Contains("refresh_token")) return delayedRefresh.Task;
            if(request.RequestUri.Query.Contains("pkce")) return Task.FromResult(Response(++pkceCalls==1?Session(actorA,"A",30):Session(actorB,"B")));
            if(request.RequestUri.AbsolutePath.Contains("/rpc/")) rpcCalls++;
            return Task.FromResult(Response("{}"));
        })))
        using(var client=new SupabaseSocialClient(options,switchedSecrets,http,()=>now))
        {
            await client.ExchangeAsync(exchange);
            var pending=client.RpcAsync(actorA,"lexi_friends",new{});
            await client.LogoutAsync();await client.ExchangeAsync(exchange);
            delayedRefresh.SetResult(Response(Session(actorA,"A-late")));
            Verify(await Cancelled(pending) && client.UserId==actorB && rpcCalls==0 && switchedSecrets.Value!.Contains("synthetic-refresh-B") && !switchedSecrets.Value.Contains("A-late"),"late A refresh cannot replace B or send A RPC after logout");
        }

        // A second exchange must be refused while the first waits, rather than sharing its generation.
        var first=Waiting();pkceCalls=0;
        using(var http=new HttpClient(new FakeHandler((_,__)=>++pkceCalls==1?first.Task:Task.FromResult(Response(Session(actorB,"B"))))))
        using(var client=new SupabaseSocialClient(options,new MemorySecrets(),http,()=>now))
        {
            var pending=client.ExchangeAsync(exchange);
            var rejected=await Rejected(client.ExchangeAsync(exchange));
            first.SetResult(Response(Session(actorA,"A")));await pending;
            Verify(rejected && pkceCalls==1 && client.UserId==actorA,"concurrent exchange rejected before HTTP and cannot overwrite pending login");
        }

        // A's finally after logout must not clear B's pending marker, allowing C to start.
        first=Waiting();var second=Waiting();pkceCalls=0;
        using(var http=new HttpClient(new FakeHandler((_,__)=>(++pkceCalls) switch {1=>first.Task,2=>second.Task,_=>Task.FromResult(Response(Session(actorA,"C")))})))
        using(var client=new SupabaseSocialClient(options,new MemorySecrets(),http,()=>now))
        {
            var old=client.ExchangeAsync(exchange);await client.LogoutAsync();
            var current=client.ExchangeAsync(exchange);
            first.SetResult(Response(Session(actorA,"A")));
            var oldCancelled=await Cancelled(old);
            var rejected=await Rejected(client.ExchangeAsync(exchange));
            second.SetResult(Response(Session(actorB,"B")));await current;
            Verify(oldCancelled && rejected && pkceCalls==2 && client.UserId==actorB,"old exchange finally preserves new pending exchange marker");
        }

        // An HTTP failure must release the marker so a fresh login succeeds.
        pkceCalls=0;
        using(var http=new HttpClient(new FakeHandler((_,__)=>Task.FromResult(++pkceCalls==1?new HttpResponseMessage(HttpStatusCode.BadRequest):Response(Session(actorB,"B"))))))
        using(var client=new SupabaseSocialClient(options,new MemorySecrets(),http,()=>now))
        {
            try {await client.ExchangeAsync(exchange);}catch(SocialCloudException){}
            await client.ExchangeAsync(exchange);
            Verify(pkceCalls==2 && client.UserId==actorB,"failed exchange releases its pending marker for retry");
        }

        // Refresh rejection clears A; beginning B must cancel tasks still carrying A's stamp.
        var oldRpc=Waiting();var oldToken=CancellationToken.None;pkceCalls=0;
        using(var http=new HttpClient(new FakeHandler((request,cancel)=>
        {
            if(request.RequestUri!.Query.Contains("pkce")) return Task.FromResult(Response(++pkceCalls==1?Session(actorA,"A",120):Session(actorB,"B")));
            if(request.RequestUri.Query.Contains("refresh_token")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            oldToken=cancel;return oldRpc.Task;
        })))
        using(var client=new SupabaseSocialClient(options,new MemorySecrets(),http,()=>now))
        {
            await client.ExchangeAsync(exchange);var pending=client.RpcAsync(actorA,"lexi_friends",new{});
            now=now.AddMinutes(2);
            Verify(await Rejected(client.RpcAsync(actorA,"lexi_friends",new{})),"refresh rejection clears expired A session");
            await client.ExchangeAsync(exchange);
            Verify(oldToken.IsCancellationRequested,"new exchange after rejected refresh cancels old account token");
            oldRpc.SetResult(Response("{}"));
            Verify(await Cancelled(pending) && client.UserId==actorB,"old account RPC cannot complete after new login");
        }

        // Public API must not permit HttpClient injection that can silently follow cross-origin POST redirects.
        var constructors=typeof(SupabaseSocialClient).GetConstructors();
        Verify(constructors.Length==1 && constructors[0].GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(SocialCloudOptions),typeof(ISecureSecretStore)}),"production constructor does not expose injectable HTTP client or clock");
        using(var production=new SupabaseSocialClient(options,new MemorySecrets()))
        {
            var http=(HttpClient)typeof(SupabaseSocialClient).GetField("_http",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(production)!;
            var handler=typeof(HttpMessageInvoker).GetField("_handler",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(http);
            Verify(handler is HttpClientHandler transport && !transport.AllowAutoRedirect,"production transport disables automatic redirects before any send");
        }
        foreach(var status in new[]{HttpStatusCode.TemporaryRedirect,HttpStatusCode.PermanentRedirect})
        {
            var requests=new List<Uri>();var secrets=new MemorySecrets();
            using var http=new HttpClient(new FakeHandler((request,_)=>
            {
                requests.Add(request.RequestUri!);
                var response=new HttpResponseMessage(status){Content=new StringContent("synthetic-secret-response-body")};
                response.Headers.Location=new Uri("https://second-origin.invalid/collect");return Task.FromResult(response);
            }));
            using var client=new SupabaseSocialClient(options,secrets,http,()=>now);
            try { await client.ExchangeAsync(exchange);Verify(false,$"HTTP {(int)status} must fail safely"); }
            catch(SocialCloudException error)
            {
                Verify(error.Status==status && !error.ToString().Contains("synthetic-secret-response-body") && !error.ToString().Contains(exchange.Verifier),$"HTTP {(int)status} error redacts credentials and response body");
            }
            Verify(requests.Count==1 && requests[0].Host==options.ProjectUrl.Host && client.UserId is null && secrets.Value is null,$"HTTP {(int)status} never requests second origin or stores session");
        }
        check(failures.Count==0,$"Auth transport regressions ({assertions} assertions): "+string.Join("; ",failures));
    }
    private static SocialOAuthExchange Exchange(SocialCloudOptions options,DateTimeOffset now)
    {
        var flow=new SocialOAuthFlow();var start=flow.Begin(options.ProjectUrl,now);
        var values=start.AuthorizationUri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1]));
        var flowId=new Uri(values["redirect_to"]).Query.TrimStart('?').Split('=')[1];
        if(!flow.TryConsumeCallback(new Uri(SocialOAuthFlow.CallbackBase+"?flow_id="+flowId+"&code=synthetic-code"),now,out var exchange,out _)) throw new InvalidOperationException("Synthetic OAuth callback was rejected.");
        return exchange!;
    }
    private sealed class MemorySecrets:ISecureSecretStore
    {
        public string? Value; public string? Get()=>Value; public void Set(string secret)=>Value=secret; public void Delete()=>Value=null;
    }
    private sealed class FakeHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request,cancellationToken);
    }
}
