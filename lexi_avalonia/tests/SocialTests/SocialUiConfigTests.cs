using System.Reflection;
using System.Text.Json;
using Lexi.Social;
using Lexi;

internal static class SocialUiConfigTests
{
    public static void Run(Action<bool, string> check)
    {
        var projection = typeof(SocialStatistics).Assembly.GetType("Lexi.Social.SocialFactProjection");
        check(projection is not null, "durable local social fact projection must exist");
        if (projection is not null)
        {
            var cause = projection.GetMethod("SavedCause");
            check(cause is not null, "durable write cause classifier must exist");
            if (cause is not null)
            {
                check((QualificationCause)cause.Invoke(null, [false, false])! == QualificationCause.Correction, "unrelated durable save is correction");
                check((QualificationCause)cause.Invoke(null, [true, false])! == QualificationCause.LearningComplete, "new durable first learn completion is learning cause");
                check((QualificationCause)cause.Invoke(null, [false, true])! == QualificationCause.PlanChange, "intentional plan write is plan-change cause");
            }
            var bound = DateTimeOffset.Parse("2026-10-10T04:00:00Z");
            var date = new DateOnly(2026, 10, 10);
            var origin = Enum.GetValues<CanonicalOrigin>()[0];
            var rows = new CanonicalHistoryRow[] {
                new("old", "a", origin, StudyRating.Known, bound.AddDays(-1).UtcDateTime, bound.AddDays(-1).UtcDateTime),
                new("old", "b", origin, StudyRating.Known, bound.UtcDateTime, bound.UtcDateTime),
                new("new", "c", origin, StudyRating.Known, bound.AddMinutes(1).UtcDateTime, bound.AddMinutes(1).UtcDateTime),
                new("new", "d", origin, StudyRating.Known, bound.AddMinutes(2).UtcDateTime, bound.AddMinutes(2).UtcDateTime) };
            var result = projection.GetMethod("Counts")!.Invoke(null, [rows, date, bound]);
            check((int)result!.GetType().GetProperty("NewWords")!.GetValue(result)! == 1, "new words use lifetime first seen and deduplicate");
            check((int)result.GetType().GetProperty("ReviewWords")!.GetValue(result)! == 1, "review words exclude same-day new identities");
            var activity = new StudyActivity("a", StudyActivityKind.FirstLearn, bound.AddMinutes(-1).UtcDateTime, bound.AddMinutes(1).UtcDateTime, 120);
            var seconds = (double)projection.GetMethod("Seconds")!.Invoke(null, [new[] { activity }, date, bound])!;
            check(seconds == 60, "duration clips at actual bound_at");
        }
        var load = typeof(SocialCloudOptions).GetMethod("FromConfiguration", [typeof(string), typeof(bool)]);
        var save = typeof(SocialCloudOptions).GetMethod("SaveConfiguration", [typeof(string)]);
        check(load is not null && save is not null, "safe local config load/save API must exist");
        if (load is null || save is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "lexi-social-ui-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "public.json");
        var names = new[] { "LEXI_SUPABASE_URL", "LEXI_SUPABASE_PUBLISHABLE_KEY", "LEXI_SOCIAL_CONFIG" };
        var prior = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            File.WriteAllText(path, JsonSerializer.Serialize(new { project_url = "https://localproject.supabase.co", public_key = "sb_publishable_testonly" }));
            Environment.SetEnvironmentVariable("LEXI_SOCIAL_CONFIG", path);
            var options = (SocialCloudOptions?)load.Invoke(null, [folder, false]);
            check(options?.ProjectUrl.Host == "localproject.supabase.co", "local fallback honors explicit file override");
            Environment.SetEnvironmentVariable("LEXI_SUPABASE_URL", "https://envproject.supabase.co");
            Environment.SetEnvironmentVariable("LEXI_SUPABASE_PUBLISHABLE_KEY", "sb_publishable_envtestonly");
            options = (SocialCloudOptions?)load.Invoke(null, [folder, false]);
            check(options?.ProjectUrl.Host == "envproject.supabase.co", "environment takes priority over local config");
            check(load.Invoke(null, [folder, true]) is null, "automated mode ignores environment and real config");
            foreach (var name in names.Take(2)) Environment.SetEnvironmentVariable(name, null);
            File.WriteAllText(path, "unreadable original");
            try { load.Invoke(null, [folder, false]); } catch (TargetInvocationException) { }
            check(File.ReadAllText(path) == "unreadable original", "invalid local configuration is preserved");
            var output = Path.Combine(folder, "saved.json");
            save.Invoke(new SocialCloudOptions(new Uri("https://validproject.supabase.co"), "sb_publishable_testonly"), [output]);
            check(File.Exists(output), "valid public configuration is saved");
            if (!OperatingSystem.IsWindows()) check(File.GetUnixFileMode(output) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "configuration permissions are 600");
            File.WriteAllText(output, "broken");
            try { save.Invoke(new SocialCloudOptions(new Uri("https://validproject.supabase.co"), "sb_publishable_testonly"), [output]); check(false, "unreadable configuration must block overwrite"); }
            catch (TargetInvocationException) { check(File.ReadAllText(output) == "broken", "unreadable target survives save"); }
            var malformedUri = JsonSerializer.Serialize(new { project_url = "not-a-url", public_key = "sb_publishable_testonly" });
            File.WriteAllText(output, malformedUri);
            var repairOptions = new SocialCloudOptions(new Uri("https://validproject.supabase.co"), "sb_publishable_testonly");
            try { repairOptions.SaveConfiguration(output); check(false, "invalid URL configuration blocks normal overwrite"); }
            catch (FormatException) { check(File.ReadAllText(output) == malformedUri, "normal save preserves valid JSON containing malformed project URL"); }
            var repairedUri = false;
            try { repairOptions.SaveConfigurationPreservingOriginal(output); repairedUri = true; } catch (FormatException) { }
            check(repairedUri && Directory.GetFiles(folder, "saved.json.preserved-*").Any(p => File.ReadAllText(p) == malformedUri) && File.ReadAllText(output).Contains("https://validproject.supabase.co"), "explicit repair preserves malformed URL original and replaces single active public configuration");
        }
        finally { for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], prior[i]); Directory.Delete(folder, true); }
        SocialProfileLauncherTests.Run(check);
    }
}
