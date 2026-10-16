using System.Diagnostics;
using Lexi.Social;

namespace Lexi;

internal static class SocialProfileProcessTests
{
    private static async Task CheckCancelledChild(SocialProfileLauncher launcher, string source, string configuration, Action<bool, string> check)
    {
        var directory = Path.GetDirectoryName(configuration)!; Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "owned-cancel-fixture.sh"); var pidFile = Path.Combine(directory, "owned-child.pid");
        File.WriteAllText(script, "echo $$ > \"$LEXI_TEST_PID\"\nsleep 1\necho $$ > \"$LEXI_PROFILE_READY_FILE\"\nsleep 10\n");
        var start = launcher.CreateStartInfo("/bin/sh", null, source); start.ArgumentList.Clear(); start.ArgumentList.Add(script);
        start.Environment["LEXI_TEST_PID"] = pidFile;
        using var cancellation = new CancellationTokenSource();
        var launching = launcher.StartAndWaitForReadyAsync(start, cancellation.Token);
        for (var i = 0; i < 100 && !File.Exists(pidFile); i++) await Task.Delay(20);
        check(File.Exists(pidFile), "cancel fixture starts a bounded owned child");
        var pid = int.Parse(File.ReadAllText(pidFile));
        using var child = Process.GetProcessById(pid);
        try
        {
            cancellation.Cancel();
            try { using var unexpected = await launching; check(false, "cancelled handshake cannot succeed"); }
            catch (OperationCanceledException) { }
            await Task.Delay(1300); child.Refresh();
            check(child.HasExited && !File.Exists(start.Environment["LEXI_PROFILE_READY_FILE"]), "cancelled handshake stops only owned child before late readiness and removes marker");
            check(!File.Exists(configuration), "failed handshake leaves A selected and never writes launcher selection");
        }
        finally { child.Refresh(); if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var source = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        // The isolated UI wrapper sets LEXI_PROFILE_CONFIG, but the suite has to run without it too
        // (scripts/run_tests.sh sets only LEXI_DATA_DIR). The fallback must live *beside* the data
        // directory, never inside it: the launcher deliberately refuses to create a profile or a
        // configuration within the original learning directory, and that guard is correct.
        // Unlike DefaultConfigurationPath this also never touches the real launcher configuration.
        var configuration = Environment.GetEnvironmentVariable("LEXI_PROFILE_CONFIG") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "lexi-launcher-profile", "profile.json");
        var launcher = new SocialProfileLauncher(configuration);
        if (!OperatingSystem.IsWindows()) await CheckCancelledChild(launcher, source, configuration, check);
        var profile = launcher.CreateIndependentProfile(source, new(new Uri("https://profiletest.supabase.co"), "sb_publishable_testonly"));
        using (var vocabulary = new VocabularyService(Path.Combine(profile, "vocab.sqlite3"), new AutomatedRunSecrets()))
            vocabulary.AddWord("independentfixture", "", "fixture", "fixture");
        var executable = Environment.ProcessPath!;
        var assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? typeof(Program).Assembly.Location : null;
        async Task<int> RunCheck(string target, string inherited)
        {
            var start = launcher.CreateStartInfo(executable, assembly, target);
            start.ArgumentList.Add("--database-check"); start.Environment["LEXI_DATA_DIR"] = inherited;
            start.RedirectStandardOutput = start.RedirectStandardError = true;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); await output; await errors;
            return process.ExitCode;
        }
        check(await RunCheck(source, source) == 3, "same-profile maintenance process is blocked by running A mutex");
        if (!OperatingSystem.IsWindows())
        {
            var alias = Path.Combine(Path.GetDirectoryName(configuration)!, "A-alias"); Directory.CreateSymbolicLink(alias, source);
            check(await RunCheck(alias, alias) == 3, "physical A database through symlink alias shares the running A mutex");
        }
        var caseAlias = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(source).ToUpperInvariant());
        if (caseAlias != source && Directory.Exists(caseAlias))
            check(await RunCheck(caseAlias, caseAlias) == 3, "physical A database through filesystem case alias shares running A mutex");
        check(await RunCheck(profile, source) == 0, "profile argument selects B before mutex and database check despite inherited A directory");
        check(!File.Exists(configuration), "process database check does not switch profile selection");
        var restartProfile = launcher.CreateIndependentProfile(source, null);
        var restart = launcher.CreateStartInfo(executable, assembly, restartProfile);
        restart.ArgumentList.Add("--ui-smoke");
        Process? started = null;
        try { started = await launcher.StartAndWaitForReadyAsync(restart).WaitAsync(TimeSpan.FromSeconds(35)); }
        catch (InvalidOperationException) { }
        check(started is not null, "independent window signals readiness before profile selection");
        using var child = started!;
        check(File.Exists(Path.Combine(restartProfile, "vocab.sqlite3")), "restart handshake comes from an initialized independent learning window");
        launcher.SelectProfile(restartProfile, source);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100));
        var smokeLines = File.ReadAllLines(Path.Combine(restartProfile, "ui-smoke-result.txt"));
        Console.WriteLine("Independent profile UI smoke: " + smokeLines.Count(line => line.StartsWith("PASS ", StringComparison.Ordinal)) + " checks passed");
        var smokeReport = smokeLines.Where(line => line.StartsWith("FAIL", StringComparison.Ordinal)).ToArray();
        if (smokeReport.Length > 0) Console.Error.WriteLine("Profile child fixture failure: " + string.Join("; ", smokeReport));
        check(child.ExitCode == 0, "independent process executes actual isolated Avalonia smoke test");
        check(!File.Exists(restart.Environment["LEXI_PROFILE_READY_FILE"]), "ready marker is removed after verified child handshake");
        // Source data is still present; caller fixture finally owns both exact profiles.
        check(File.Exists(Path.Combine(source, "vocab.sqlite3")), "A learning database remains present after independent restart");
    }
}
