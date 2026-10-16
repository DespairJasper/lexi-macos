using Lexi.Social;
using Lexi;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;

internal static class SocialProfileLauncherTests
{
    public static void Run(Action<bool, string> check)
    {
        var type = typeof(SocialCloudOptions).Assembly.GetType("Lexi.Social.SocialProfileLauncher");
        check(type is not null, "wrong-owner independent profile launcher must be executable");
        if (type is null) return;
        var root = Path.Combine(Path.GetTempPath(), "lexi-profile-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var configuration = Path.Combine(root, "launcher", "profile.json");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "original.json"), "preserve A learning data");
        dynamic launcher = Activator.CreateInstance(type, configuration)!;
        var options = new SocialCloudOptions(new Uri("https://profilefixture.supabase.co"), "sb_publishable_testonly");
        var previousData = Environment.GetEnvironmentVariable("LEXI_DATA_DIR");
        var previousKey = Environment.GetEnvironmentVariable("LEXI_AUTH_JWT");
        try
        {
            var actor = Guid.NewGuid(); var binding = Guid.NewGuid(); var a = new SocialStore(source);
            a.BindOwner(actor, binding);
            a.Enqueue(actor, Guid.NewGuid(), new(binding, new DateOnly(2026, 10, 10), 1, 1, 0, 0, 0, null, QualificationCause.Correction, false));
            string profile = launcher.CreateIndependentProfile(source, options);
            check(Path.GetFullPath(profile) != Path.GetFullPath(source) && !profile.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal), "fresh profile is separate from source learning directory");
            check(File.ReadAllText(Path.Combine(source, "original.json")) == "preserve A learning data" && a.LoadPending(actor).Count == 1, "profile creation preserves A data and pending queue");
            check(!File.Exists(Path.Combine(profile, "vocab.sqlite3")) && !File.Exists(Path.Combine(profile, "social.sqlite3")), "fresh profile never copies old database or owner queue");
            check(!File.Exists(configuration), "creation alone does not switch active profile before ready restart");
            check(SocialCloudOptions.FromConfiguration(profile, false)?.ProjectUrl.Host == "profilefixture.supabase.co", "new profile receives only public connection configuration");
            var b = new SocialStore(profile); var other = Guid.NewGuid(); b.BindOwner(other, Guid.NewGuid());
            check(b.LoadPending(other).Count == 0 && a.LoadPending(actor).Count == 1, "new account owns an independent empty social store");
            launcher.SelectProfile(profile, source);
            Environment.SetEnvironmentVariable("LEXI_DATA_DIR", null);
            launcher.ApplyStartupSelection(Array.Empty<string>(), false);
            check(Environment.GetEnvironmentVariable("LEXI_DATA_DIR") == profile, "next normal process restores selected independent profile");
            Environment.SetEnvironmentVariable("LEXI_DATA_DIR", source);
            launcher.ApplyStartupSelection(new[] { "--profile-dir", profile }, false);
            check(Environment.GetEnvironmentVariable("LEXI_DATA_DIR") == profile, "explicit profile argument replaces inherited A directory before startup");
            Environment.SetEnvironmentVariable("LEXI_DATA_DIR", null);
            launcher.ApplyStartupSelection(Array.Empty<string>(), true);
            check(Environment.GetEnvironmentVariable("LEXI_DATA_DIR") is null, "automated startup never silently selects real saved profile");
            Environment.SetEnvironmentVariable("LEXI_AUTH_JWT", "synthetic-private-marker");
            ProcessStartInfo start = launcher.CreateStartInfo("/usr/bin/true", null, profile);
            check(!start.ArgumentList.Any(v => v.Contains("synthetic-private-marker")) && !start.Environment.Values.Any(v => v?.Contains("synthetic-private-marker") == true), "profile process forwards no inherited JWT or secret");
            check(start.Environment["LEXI_DATA_DIR"] == profile && !start.Environment.ContainsKey("LEXI_SOCIAL_CONFIG"), "child uses own profile public config, not A override");
            if (!OperatingSystem.IsWindows()) check(File.GetUnixFileMode(configuration) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "launcher config outside learning data has mode600");
            if (!OperatingSystem.IsWindows())
            {
                var link = Path.Combine(root, "outside-looking-link"); Directory.CreateSymbolicLink(link, source);
                var linkedLauncher = new SocialProfileLauncher(Path.Combine(link, "profile.json"));
                try { linkedLauncher.CreateIndependentProfile(source, null); check(false, "symlink launcher cannot embed fresh profile inside A learning directory"); }
                catch (InvalidOperationException) { check(!Directory.Exists(Path.Combine(source, "profiles")), "symlink containment rejects before creating files inside A"); }
            }
            var caseAlias = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(source).ToUpperInvariant());
            if (caseAlias != source && Directory.Exists(caseAlias))
            {
                var caseLauncher = new SocialProfileLauncher(Path.Combine(caseAlias, "nested", "profile.json"));
                try { caseLauncher.CreateIndependentProfile(source, null); check(false, "filesystem case alias cannot create an independent profile inside A"); }
                catch (InvalidOperationException) { check(!Directory.Exists(Path.Combine(source, "nested")), "case containment rejects before touching A"); }
            }
            File.WriteAllText(configuration, "unreadable original launcher config");
            try { launcher.SelectProfile(profile, source); check(false, "invalid launcher config prevents overwrite"); }
            catch (InvalidDataException) { check(File.ReadAllText(configuration) == "unreadable original launcher config", "configuration error preserves existing launcher file"); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEXI_DATA_DIR", previousData); Environment.SetEnvironmentVariable("LEXI_AUTH_JWT", previousKey);
            Directory.Delete(root, true);
            Console.WriteLine("Profile configuration fixture cleanup: " + root + " removed=" + !Directory.Exists(root));
        }
    }
}
