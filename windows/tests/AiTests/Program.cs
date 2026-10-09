using System.Net;
using System.Text;
using System.Text.Json;
using Lexi;
static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
Check(!AiService.Presets.ContainsKey("responses"), "Responses is a protocol, not a provider preset");
Check(!AiService.ParseExpansion("{}", ["phrases"]).HasContent, "phrases omitted is valid");
Check(!AiService.ParseExpansion("{\"phrases\":[]}", ["phrases"]).HasContent, "empty phrases valid");
var json = """{"phrases":[{"en":"take off","zh":"起飞"},{"en":"take care of","zh":"照顾"}]}""";
var result = AiService.ParseExpansion(json, ["phrases"]);
Check(result.Phrases.Count == 2 && result.HasContent && result.Phrases[0].Chinese == "起飞", "bilingual phrases parsed");
Check(!AiService.ParseExpansion(json, []).HasContent, "unchecked phrases ignored");
foreach (var bad in new[] { """{"phrases":[{"en":"take off","zh":""},{"en":"take care","zh":"保重"}]}""", """{"phrases":[{"en":"take off","zh":"起飞"}]}""", """{"phrases":[{"en":"take off","zh":"起飞"},{"en":"TAKE OFF","zh":"起飞"}]}""", """{"phrases":"invalid"}""" })
{
    var rejected = false;
    try { AiService.ParseExpansion(bad, ["phrases"]); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "malformed phrases rejected");
}

using (var listener = new System.Net.HttpListener())
{
    var portFinder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    portFinder.Start(); var port = ((System.Net.IPEndPoint)portFinder.LocalEndpoint).Port; portFinder.Stop();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
    var request = new AiService().GenerateExpansionAsync("take", ["phrases"], new AppSettings { BaseUrl = $"http://127.0.0.1:{port}", ApiKey = "test-only", Model = "test-only" });
    var context = await listener.GetContextAsync();
    using var reader = new StreamReader(context.Request.InputStream);
    using var body = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
    var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
    Check(prompt.Contains("切勿生硬编造拼凑") && prompt.Contains("省略") && prompt.Contains("phrases:"), "outgoing prompt enforces optional idiomatic phrases");
    Check(!prompt.Contains("examples:") && !body.RootElement.TryGetProperty("max_tokens", out _), "outgoing request only includes selected schema without token cap");
    var response = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { choices = new[] { new { message = new { content = "{}" } } } });
    await context.Response.OutputStream.WriteAsync(response); context.Response.Close();
    Check(!(await request).HasContent, "omitted phrases accepted end to end over HTTP");
}

static string Endpoint(string url, string protocol) => AiService.NormalizeEndpoint(url, protocol);
static AppSettings Config(string protocol = "responses")
{
    var config = new AppSettings { BaseUrl = "https://relay.example.org/agent", ApiKey = "test-secret", Model = "deepseek-v4-flash" };
    config.AiProtocol = protocol;
    return config;
}
static AiService Service(HttpClient client) => new(client);
static Task<string> Translate(AiService service, AppSettings config, CancellationToken ct = default) =>
    service.TranslateAsync("Hello", config, ct);
static HttpResponseMessage Reply(string body, string type = "application/json", HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
static async Task Reject(Func<Task> run, string expected, string name)
{
    try { await run(); throw new Exception("accepted invalid response: " + name); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains(expected) && !ex.Message.Contains("test-secret"), name); }
}

using (var client = new HttpClient(new TestHandler(async (req, ct) =>
{
    using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
    Check(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("phonetic"), "AI word lookup prompt requests the dictionary schema");
    return Reply("{\"choices\":[{\"message\":{\"content\":\"{\\\"phonetic\\\":\\\"rɪˈzɪliənt\\\",\\\"pos\\\":\\\"adj.\\\",\\\"translation\\\":\\\"adj. 有韧性的\\\",\\\"definition\\\":\\\"able to recover quickly\\\"}\"}}]}");
})))
{
    var aiWord = await new AiService(client).LookupWordAsync("resilient", Config("chat"));
    Check(aiWord.Found && aiWord.Phonetic == "rɪˈzɪliənt" && aiWord.Pos == "adj." && aiWord.Translation.Contains("有韧性") && aiWord.Definition.Contains("recover"), "AI word lookup returns a full offline-equivalent entry");
}
static HttpResponseMessage Chat(string content) => Reply(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Chat("```json\n{\"translation\":\"\"}\n```")))))
    await Reject(() => new AiService(client).LookupWordAsync("x", Config("chat")), "中文释义", "AI word lookup rejects an empty meaning");
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Chat("not a dictionary")))))
    await Reject(() => new AiService(client).LookupWordAsync("x", Config("chat")), "词典 JSON", "AI word lookup rejects a non-JSON reply");

foreach (var bad in new[] {
    ("{\"examples\":[{\"en\":12,\"zh\":\"中文\"}]}", "examples", "例句"),
    ("{\"examples\":[{\"en\":\"Hello\",\"zh\":true}]}", "examples", "例句"),
    ("{\"synonyms\":[1,2,3]}", "synonyms", "同义词"),
    ("{\"antonyms\":[\"a\",false]}", "antonyms", "反义词") })
    await Reject(() => Task.FromResult(AiService.ParseExpansion(bad.Item1, [bad.Item2])), bad.Item3, "invalid expansion types retain Chinese validation");
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply("{\"choices\":[null]}")))))
    await Reject(() => Translate(Service(client), Config("chat")), "文本", "malformed Chat choice yields Chinese validation");
const string completed = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
Check(Endpoint("https://relay.example.org/agent/", "responses") == "https://relay.example.org/agent/responses", "Responses preserves provider prefix");
Check(Endpoint("https://relay.example.org/agent/responses", "responses") == "https://relay.example.org/agent/responses", "Responses accepts complete endpoint");
Check(Endpoint("https://example.org/v1/chat/completions", "chat") == "https://example.org/v1/chat/completions", "Chat complete endpoint preserved");
foreach (var bad in new[] { "http://example.org/v1", "https://u:p@example.org/v1", "https://example.org/v1?secret=x", "https://example.org/v1#x" })
{
    try { Endpoint(bad, "responses"); throw new Exception("accepted unsafe URL"); }
    catch (ArgumentException) { Check(true, "unsafe endpoint rejected"); }
}
using (var client = new HttpClient(new TestHandler(async (req, ct) =>
{
    Check(req.RequestUri!.AbsoluteUri == "https://relay.example.org/agent/responses", "translation calls Responses endpoint");
    Check(req.Headers.UserAgent.ToString().Contains("Lexi/1.1.4"), "request supplies relay compatible User-Agent");
    Check(req.Headers.Authorization?.Scheme == "Bearer" && req.Headers.Authorization.Parameter == "test-secret", "authorization on request");
    using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
    var root = body.RootElement;
    Check(root.GetProperty("stream").GetBoolean() && root.GetProperty("input")[1].GetProperty("content").GetString() == "Hello" && !root.TryGetProperty("messages", out _), "Responses sends streaming input schema");
    return Reply("data: {\"type\":\"response.reasoning_text.delta\",\"delta\":\"private reasoning\"}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"你好\"}\n\n" + completed, "text/event-stream");
})))
    Check(await Translate(Service(client), Config()) == "你好", "Responses translation excludes reasoning");
const string responseJson = "{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"summary\":[]},{\"type\":\"message\",\"content\":[{\"type\":\"reasoning_text\",\"text\":\"private\"},{\"type\":\"output_text\",\"text\":\"你好\"}]}]}";
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply(responseJson)))))
    Check(await Translate(Service(client), Config()) == "你好", "Responses accepts non-stream JSON final text");
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply("event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":" + responseJson + "}\n\n", "text/event-stream")))))
    Check(await Translate(Service(client), Config()) == "你好", "Responses completed-only SSE supplies final text");
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply(
    ": keepalive\r\ndata: {\"type\":\"response.output_text.delta\",\r\ndata: \"delta\":\"你好\"}\r\n\r\n" +
    "event: response.completed\ndata: {\"response\":" + responseJson + "}\n\n", "text/event-stream")))))
    Check(await Translate(Service(client), Config()) == "你好", "SSE multiline framing and final output do not duplicate text");
using (var client = new HttpClient(new TestHandler(async (req, ct) =>
{
    using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
    Check(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == "Hello" && !body.RootElement.TryGetProperty("stream", out _), "legacy translation uses Chat schema");
    return Reply("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}");
})))
    Check(await Translate(Service(client), Config("chat")) == "你好", "legacy translation extracts content");
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply("data: {\"type\":\"response.output_text.delta\",\"delta\":\"{}\"}\n\n" + completed, "text/event-stream")))))
    Check(!(await Service(client).GenerateExpansionAsync("take", ["phrases"], Config())).HasContent, "Responses expansion shares parsing contract");
foreach (var pair in new[] { (HttpStatusCode.Unauthorized, "401"), (HttpStatusCode.Forbidden, "403"), (HttpStatusCode.TooManyRequests, "429"), (HttpStatusCode.BadGateway, "502") })
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply("test-secret", status: pair.Item1)))))
    await Reject(() => Translate(Service(client), Config()), pair.Item2, "HTTP error is useful and redacted");
foreach (var pair in new[] {
    ("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n", "完整"),
    ("data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\"}}\n\n", "完整"),
    ("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"test-secret\"}}}\n\n", "失败"),
    ("data: {\"type\":\"error\",\"message\":\"test-secret\"}\n\n", "失败"),
    ("data: not-json\n\n", "JSON"),
    (completed, "文本") })
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply(pair.Item1, "text/event-stream")))))
    await Reject(() => Translate(Service(client), Config()), pair.Item2, "invalid SSE is rejected without partial translation");
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply("{\"status\":\"incomplete\",\"output_text\":\"partial\"}")))))
    await Reject(() => Translate(Service(client), Config()), "完整", "incomplete JSON is rejected");
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply(new string('x', 1_000_001))))))
    await Reject(() => Translate(Service(client), Config()), "过大", "response size is bounded");
var cancellationRequests = 0;
using (var client = new HttpClient(new TestHandler(async (req, ct) => { cancellationRequests++; await Task.Delay(Timeout.Infinite, ct); return Reply(""); })))
using (var cancel = new CancellationTokenSource(50))
{
    await Reject(() => Translate(Service(client), Config(), cancel.Token), "已取消", "caller cancellation is preserved");
    Check(cancellationRequests == 1, "cancel does not retry billable request");
}
using (var client = new HttpClient(new TestHandler(async (req, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply(""); })))
{
    var config = Config(); config.Timeout = 1;
    await Reject(() => Translate(Service(client), config), "超时", "timeout explains potential consumption");
}
using (var client = new HttpClient(new TestHandler((req, ct) => throw new HttpRequestException("test-secret https://secret.example"))))
    await Reject(() => Translate(Service(client), Config()), "网络", "network exception does not leak credentials");

using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply(responseJson)))))
{
    var config = Config(); config.ApiKey = "test-secret\r\nInjected: yes";
    await Reject(() => Translate(Service(client), config), "API Key", "invalid header credentials are safely rejected");
}
using (var client = new HttpClient(new TestHandler((req, ct) => Task.FromResult(Reply("{\"status\":\"completed\",\"output\":[{\"type\":42,\"content\":[]}]}")))))
    await Reject(() => Translate(Service(client), Config()), "文本", "malformed content type does not escape parser errors");

using (var client = new HttpClient(new TestHandler(async (req, ct) =>
{
    using var request = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
    using var data = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
    Check(data.RootElement.GetProperty("context").GetString()!.Length == 500 && data.RootElement.GetProperty("source").GetString()!.Length == 2000,
        "expansion bounds context and source excerpt");
    return Chat("{}");
})))
{
    var config = Config("chat");
    config.AiContext = new string('c', 501);
    config.IncludeSourceInAi = true;
    await Service(client).GenerateExpansionAsync("take", ["phrases"], config, sourceExcerpt: new string('s', 2001));
}
using (var client = new HttpClient(new TestHandler(async (req, ct) =>
{
    using var request = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
    using var data = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
    Check(data.RootElement.GetProperty("source").ValueKind == JsonValueKind.Null, "source excerpt stays local until opted in");
    return Chat("{}");
})))
    await Service(client).GenerateExpansionAsync("take", ["phrases"], Config("chat"), sourceExcerpt: "private excerpt");

var settingsDirectory = Path.Combine(Path.GetTempPath(), "LexiAiSettings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(settingsDirectory);
try
{
    var databasePath = Path.Combine(settingsDirectory, "vocab.sqlite3");
    using (var archive = new VocabularyService(databasePath))
    {
        var config = Config();
        archive.SaveSettings(config);
        Check(archive.LoadSettings().AiProtocol == "responses", "Responses protocol persisted without remembering credential");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT settings_json FROM app_settings WHERE id=1";
        Check(!command.ExecuteScalar()!.ToString()!.Contains("test-secret"), "protocol persistence never writes plaintext key");
        command.CommandText = "UPDATE app_settings SET settings_json='{\"Provider\":\"custom\",\"Model\":\"legacy-model\"}' WHERE id=1";
        command.ExecuteNonQuery();
        Check(archive.LoadSettings().AiProtocol == "chat", "legacy settings retain Chat compatibility");
    }
    using (var archive = new VocabularyService(databasePath))
    {
        archive.SaveSettings(Config());
    }
    using (var archive = new VocabularyService(databasePath))
        Check(archive.LoadSettings().AiProtocol == "responses", "Responses protocol survives reopening the database");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(settingsDirectory, true);
}

if (args.Contains("--live-local-config"))
{
    using var archive = new VocabularyService();
    var config = archive.LoadSettings();
    Check(config.Provider == "custom" && config.AiProtocol == "responses" && config.RememberKey && !string.IsNullOrEmpty(config.ApiKey), "LIVE custom local configuration restores a protected credential");
    var service = new AiService();
    var translation = await service.TranslateAsync("Hello, how are you today?", config);
    Check(translation.Any(c => c >= '\u4e00' && c <= '\u9fff') && !translation.Contains(config.ApiKey), "LIVE .NET Responses returns Chinese translation without credentials");
    var expansion = await service.GenerateExpansionAsync("take", ["phrases"], config);
    Check(expansion.Phrases.Count is 0 or >= 2 and <= 4, "LIVE .NET Responses expansion validates phrases schema");
    config.ApiKey = "";
}

sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

