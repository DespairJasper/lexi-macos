using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Lexi;

public static class ScrollingRegressionTests
{
    public static async Task RunAsync(MainWindow window, List<string> report, string folder)
    {
        var list = window.FindControl<ListBox>("VocabListBox")!;
        var rows = Enumerable.Range(0, 60).Select(i => new WordItem
        {
            Id = 1000 + i, Word = "word" + i, Translation = "测试释义，保持清晰易读。",
            Definition = "A practical word used in everyday life.", IsExpanded = i is 0 or 4 or 18
        }).ToList();
        typeof(MainWindow).GetField("_allWords", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, rows);
        typeof(MainWindow).GetMethod("ApplyVocabFilters", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        await Task.Delay(150);
        // A real click leaves keyboard focus inside the generated row. Virtualization
        // keeps this container alive even when it scrolls outside the viewport.
        list.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == rows[0] && b.Content?.ToString() == "✦ 生成").Focus();
        var expansion = new LlmResult
        {
            Examples = [new("The office furniture is modern and functional.", "办公家具既现代又实用。"), new("Please move the furniture away from the wall.", "请把家具从墙边移开。"), new("We chose simple furniture for the room.", "我们为房间选了简约的家具。")],
            Synonyms = ["furnishings", "household goods", "movables", "chattels"],
            Antonyms = ["fixtures", "built-ins"],
            Phrases = [new("a piece of furniture", "一件家具"), new("office furniture", "办公家具")]
        };
        foreach (var row in rows.Where(r => r.IsExpanded)) row.AiResult = expansion;
        await Task.Delay(200);
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        foreach (var y in new double[] { 0, 150, 320, 520, 760, 1050, 1600, 2400, 1000, 300, 0 })
        {
            scroll.Offset = new Vector(0, y);
            await Task.Delay(100);
            var containers = list.GetVisualDescendants().OfType<ListBoxItem>().Where(c => c.IsVisible && c.Bounds.Height > 0).ToList();
            var ordered = containers.OrderBy(c => ((WordItem)c.DataContext!).Id).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var previousBottom = ordered[i - 1].TranslatePoint(new Point(0, ordered[i - 1].Bounds.Height), list)!.Value.Y;
                var currentTop = ordered[i].TranslatePoint(new Point(), list)!.Value.Y;
                if (previousBottom > currentTop + 2)
                {
                    File.WriteAllLines(Path.Combine(folder, "overlap-diagnostics.txt"), ordered.Select(c => $"id={((WordItem)c.DataContext!).Id} bounds={c.Bounds} desired={c.DesiredSize} effectiveVisible={c.IsEffectivelyVisible} measure={c.IsMeasureValid} arrange={c.IsArrangeValid}"));
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    bitmap.Render(window); bitmap.Save(Path.Combine(folder, "scroll-overlap.png"));
                    throw new Exception($"Recycled rows overlap at scroll={y}: prev bottom={previousBottom}, next top={currentTop}");
                }
            }
            foreach (var c in containers)
            {
                var content = c.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("drawer-slot") && b.IsVisible);
                if (content == null) continue;
                var bottom = content.TranslatePoint(new Point(0, content.Bounds.Height), c)!.Value.Y;
                if (bottom > c.Bounds.Height + 2)
                {
                    using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    bmp.Render(window); bmp.Save(Path.Combine(folder, "scroll-overlap.png"));
                    throw new Exception($"Expanded drawer overflows recycled row at scroll={y}: drawer bottom={bottom:F1}, row height={c.Bounds.Height:F1}");
                }
            }
        }
        report.Add("PASS generated variable-height rows remain within their containers through down/up scrolling");
        scroll.ScrollToEnd(); await Task.Delay(150);
        var last = list.GetVisualDescendants().OfType<ListBoxItem>().Last();
        var end = last.TranslatePoint(new Point(0, last.Bounds.Height), scroll)!.Value.Y;
        if (end > scroll.Bounds.Height + 2 || end < 0) throw new Exception("Last row cannot be reached by scrolling");
        report.Add("PASS entire expanded page remains reachable at scroll bottom");
        rows[0].Selected = true;
        window.FindControl<Button>("VocabNextBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(150);
        if (list.ItemCount != 10 || !rows[0].Selected || !window.FindControl<TextBlock>("SelectedCountText")!.Text!.Contains("1"))
            throw new Exception("Pagination lost cross-page selection");
        window.FindControl<Button>("VocabPreviousBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(150);
        if (list.ItemCount != 50 || !rows[0].IsExpanded || rows[0].AiResult != expansion) throw new Exception("Pagination lost expansion result");
        report.Add("PASS bounded pages preserve selection, expansion and generated content across navigation");
        scroll.Offset = new Vector(0, 320); await Task.Delay(150);
        using var screenshot = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        screenshot.Render(window); screenshot.Save(Path.Combine(folder, "scroll-expanded-fixed.png"));
    }
}
