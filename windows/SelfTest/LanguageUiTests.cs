using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using System.Reflection;
using Avalonia.VisualTree;

namespace Lexi;

public static class LanguageUiTests
{
    public static async Task RunAsync(MainWindow main)
    {
        var report = new List<string>(); var exit = 0;
        void Check(bool value, string name) { report.Add((value ? "PASS " : "FAIL ") + name); if (!value) throw new Exception(name); }
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR")!;
        try
        {
            Check(main.FindControl<ComboBox>("SettingsLanguageCombo") != null, "settings provides a language selector");
            var method = typeof(MainWindow).GetMethod("SetUiLanguage");
            Check(method != null, "language switching API is available");
            main.FindControl<TextBox>("LookupInput")!.Text = "resilient";
            main.FindControl<TextBox>("ArchiveNotesInput")!.Text = "用户备注：学习中";
            method!.Invoke(main, ["en"]);
            await Task.Delay(100);
            Check(main.FindControl<Button>("LookupBtn")!.Content?.ToString() == "Look up ↵", "English updates existing lookup controls");
            Check(main.FindControl<Button>("BackupNowBtn")!.Content?.ToString() == "Back up now", "English updates backup controls");
            var quickNav = main.FindControl<StackPanel>("LearningNavHost")!.Children.OfType<Button>().ToList();
            Check(quickNav.Single(b => b.Name == "OpenTranslateBtn").Content?.ToString() == "Translate sentence", "English translates sentence navigation");
            Check(quickNav.Single(b => b.Name == "NavQuotes").Content?.ToString() == "Quotes", "English translates quotes navigation");
            await main.HandleQuickActionAsync(QuickAction.Translate, 0);
            var card = (QuickCardWindow)typeof(MainWindow).GetField("_quickCard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            card.OriginalInput.Text = "Keep my sentence"; card.TranslationInput.Text = "保留我的译文";
            method.Invoke(main, ["zh-CN"]); method.Invoke(main, ["en"]);
            Check(card.OriginalInput.Text == "Keep my sentence" && card.TranslationInput.Text == "保留我的译文", "language change preserves open translation draft");
            Check(card.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "QuickRunBtn").Content?.ToString() == "Translate", "open translation card updates its action language");
            card.Close();
            await main.HandleQuickActionAsync(QuickAction.SaveQuote, 0);
            card = (QuickCardWindow)typeof(MainWindow).GetField("_quickCard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            card.OriginalInput.Text="A quote worth keeping";card.TranslationInput.Text="保留金句草稿";
            method.Invoke(main,["zh-CN"]);method.Invoke(main,["en"]);
            Check(card.OriginalInput.Text=="A quote worth keeping"&&card.TranslationInput.Text=="保留金句草稿","language change preserves open quote draft");
            Check(!System.Text.RegularExpressions.Regex.IsMatch(card.Title??"","[\\u4e00-\\u9fff]"),"open quote card title follows English mode");
            card.Close();
            Check(main.FindControl<TextBox>("LookupInput")!.Text == "resilient" && main.FindControl<TextBox>("ArchiveNotesInput")!.Text == "用户备注：学习中", "language switching preserves user text");
            using var store = new VocabularyService();
            Check(store.LoadSettings().GetType().GetProperty("UiLanguage")!.GetValue(store.LoadSettings())?.ToString() == "en", "switching persists English");
            main.FindControl<Button>("SettingsSaveBtn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.LoadSettings().GetType().GetProperty("UiLanguage")!.GetValue(store.LoadSettings())?.ToString() == "en", "saving AI settings preserves English");
            var combo = main.FindControl<ComboBox>("SettingsLanguageCombo")!;
            combo.SelectedIndex = 0;
            Check(main.FindControl<Button>("LookupBtn")!.Content?.ToString() == "查询 ↵", "actual selector returns to Chinese");
            var reviewCaption=main.FindControl<Button>("NavReview")!.GetVisualDescendants().OfType<TextBlock>().First().Text;
            Check(reviewCaption=="今日重逢","Chinese review navigation keeps its own label after language round trips");
        }
        catch (Exception ex) { report.Add(ex.ToString()); exit = 1; }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(folder, "language-test-result.txt"), report);
            foreach (var line in report) Console.WriteLine(line);
            main.ForceClose(); (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(exit);
        }
    }
}
