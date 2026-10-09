using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Lexi;

public static class AiSettingsUiTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var report = new List<string>(); var exit = 1;
        T C<T>(string name) where T : Control => window.FindControl<T>(name)!;
        Task Call(string name) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
        void Check(bool ok, string name) { report.Add((ok ? "PASS " : "FAIL ") + name); if (!ok) throw new Exception(name); }
        async Task<HttpListenerContext> Request(HttpListener listener) => await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        async Task Reply(HttpListenerContext context, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            try { await context.Response.OutputStream.WriteAsync(bytes); } catch (HttpListenerException) { }
            finally { try { context.Response.Close(); } catch (HttpListenerException) { } }
        }
        var portFinder = new TcpListener(IPAddress.Loopback, 0); portFinder.Start(); var port = ((IPEndPoint)portFinder.LocalEndpoint).Port; portFinder.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        try
        {
            C<ComboBox>("SettingsProviderCombo").SelectedIndex = 3;
            C<TextBox>("SettingsBaseUrlInput").Text = $"http://127.0.0.1:{port}/v1";
            C<TextBox>("SettingsModelInput").Text = "ui-test-model";
            C<TextBox>("SettingsApiKeyInput").Text = "ui-test-key";
            C<ComboBox>("SettingsProtocolCombo").SelectedIndex = 1;
            var run = Call("TestAiConnectionAsync"); var request = await Request(listener);
            using (var reader = new StreamReader(request.Request.InputStream))
            using (var body = JsonDocument.Parse(await reader.ReadToEndAsync()))
                Check(request.Request.Url!.AbsolutePath == "/v1/responses" && body.RootElement.GetProperty("model").GetString() == "ui-test-model" && request.Request.Headers["Authorization"] == "Bearer ui-test-key", "connection test reads editable endpoint model key and protocol");
            Check(!C<Button>("SettingsTestConnectionBtn").IsEnabled && C<Button>("SettingsCancelConnectionBtn").IsVisible, "connection test exposes cancellation while pending");
            await Reply(request, "{\"status\":\"completed\",\"output_text\":\"你好\"}"); await run;
            Check(C<TextBlock>("GlobalStatusText").Text!.Contains("连接成功") && C<Button>("SettingsTestConnectionBtn").IsEnabled, "connection result updates status and restores controls");
            run = Call("TestAiConnectionAsync"); request = await Request(listener);
            ((CancellationTokenSource)typeof(MainWindow).GetField("_connectionTestCts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5)); await Reply(request, "{}");
            Check(C<TextBlock>("GlobalStatusText").Text!.Contains("已取消") && C<Button>("SettingsTestConnectionBtn").IsEnabled, "cancelled connection test restores controls");

            typeof(MainWindow).GetMethod("OnSaveSettingsClicked", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            C<TextBox>("LookupInput").Text = "resilient";
            await Call("PerformLookupAsync");
            Check(C<TextBlock>("GlobalStatusText").Text!.Contains("离线词库"), "known dictionary word uses offline source with AI configured");
            C<TextBox>("LookupInput").Text = "lexifallbackfixture";
            run = Call("PerformLookupAsync"); request = await Request(listener);
            var dictionary = JsonSerializer.Serialize(new { phonetic = "fɪkstʃə", pos = "n.", translation = "AI 测试词", definition = "test definition" });
            await Reply(request, JsonSerializer.Serialize(new { status = "completed", output_text = dictionary })); await run;
            Check(C<TextBlock>("ResultTranslationText").Text == "AI 测试词" && C<TextBlock>("GlobalStatusText").Text!.Contains("AI 补全"), "missing offline word receives full AI dictionary entry");
            C<TextBox>("LookupInput").Text = "lexidelayedfixture";
            run = Call("PerformLookupAsync"); request = await Request(listener);
            C<TextBox>("LookupInput").Text = "resilient"; await Call("PerformLookupAsync");
            await Reply(request, JsonSerializer.Serialize(new { status = "completed", output_text = dictionary })); await run;
            Check(C<TextBlock>("ResultWordText").Text == "resilient" && C<Button>("LookupBtn").IsEnabled, "editing query cancels old AI fallback without replacing latest offline result");
            exit = 0;
        }
        catch (Exception ex) { report.Add("FAIL " + ex); }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!, "ai-settings-ui-test.txt"), report);
            foreach (var line in report) Console.WriteLine(line);
            window.ForceClose(); (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(exit);
        }
    }
}
