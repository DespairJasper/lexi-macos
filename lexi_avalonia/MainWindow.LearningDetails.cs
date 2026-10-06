using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi;

public partial class MainWindow
{
    private readonly IWordAudioPlayer _wordAudio;

    private void SpeakLearningText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var entry = _ieltsCatalog?.Find(text);
        _wordAudio.Play(text, IeltsCatalog.ResolveAsset(entry?.AudioPath));
    }

    private void StopLearningSpeech()
    {
        _wordAudio.Stop(); _chapterAudioTimer.Stop();
        if (_chapterAudioControls != null) _chapterAudioControls.IsVisible = false;
    }

    private void AddLearningDetails(StackPanel host, LlmResult? content)
    {
        if (content == null) return;
        var dark = _settings.Theme == "Dark";
        Border GlassCard(Control body, double minimum = 0) => new()
        {
            Classes = { "card" }, Child = body, Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(12),
            BorderThickness = default, BoxShadow = default, MinHeight = minimum,
            Background = new SolidColorBrush(Color.Parse(dark ? "#382D526A" : "#66FFFFFF"))
        };
        foreach (var example in content.Examples)
        {
            var body = new StackPanel { Spacing = 8 };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 5 };
            var play = new Button { Classes = { "ghost" }, Content = "♪", Padding = new Thickness(0, 2, 5, 2), FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
            ToolTip.SetTip(play, T("朗读例句") + "  S");
            play.Click += (_, _) => SpeakLearningText(example.English);
            row.Children.Add(play);
            var english = new TextBlock { Text = example.English, FontSize = 16, LineHeight = 26, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(english, 1); row.Children.Add(english); body.Children.Add(row);
            body.Children.Add(new TextBlock { Text = example.Chinese, FontSize = 15, LineHeight = 24, TextWrapping = TextWrapping.Wrap });
            host.Children.Add(GlassCard(body));
        }
        if (content.Phrases.Count > 0)
        {
            var phrases = new StackPanel { Spacing = 7 };
            foreach (var phrase in content.Phrases)
                phrases.Children.Add(new TextBlock { Text = phrase.English + "  " + phrase.Chinese, FontSize = 16, LineHeight = 26, TextWrapping = TextWrapping.Wrap });
            host.Children.Add(GlassCard(phrases, 190 * _cardHeightRatio));
        }
    }
}
