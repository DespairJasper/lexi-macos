using Lexi;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
Check(typeof(AppSettings).GetProperty("UiLanguage") != null, "language preference is available");
var path = Path.Combine(Path.GetTempPath(), "LexiLanguage-" + Guid.NewGuid().ToString("N"), "vocab.sqlite3");
try
{
    using (var store = new VocabularyService(path))
    {
        dynamic settings = store.LoadSettings();
        Check(settings.UiLanguage == "zh-CN", "existing settings default to Chinese");
        settings.UiLanguage = "en";
        store.SaveSettings(settings);
    }
    using (var store = new VocabularyService(path))
    {
        dynamic settings = store.LoadSettings();
        Check(settings.UiLanguage == "en", "English preference persists across restart");
        settings.Timeout = 60; store.SaveSettings(settings);
        Check(((dynamic)store.LoadSettings()).UiLanguage == "en", "saving AI preferences keeps current language");
    }
}
finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
