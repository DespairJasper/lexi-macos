using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Lexi;

public partial class MainWindow
{
    internal bool TryFlushWritingDraft()
    {
        if (_ieltsSubContent == null) return true;
        foreach (var workspace in _ieltsSubContent.Children.OfType<Lexi.Features.Ielts.IeltsWritingWorkspace>())
            if (!workspace.FlushPendingSave()) return false;
        return true;
    }

    private void OnContentSpeakClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: string text } && !string.IsNullOrWhiteSpace(text))
            GetLearningAudio().Play(text);
    }

    private void OnExampleQuoteClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.DataContext is not ExampleItem example) return;
        var word=button.GetVisualAncestors().OfType<Control>().Select(c=>c.DataContext).OfType<WordItem>().FirstOrDefault();
        SaveExampleToQuotes(example.English, example.Chinese,word==null?"例句收藏":"词汇档案 · "+word.Word);
    }
}
