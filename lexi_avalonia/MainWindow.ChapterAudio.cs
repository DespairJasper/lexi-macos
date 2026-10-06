using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Lexi;

public partial class MainWindow
{
    private StackPanel? _chapterAudioControls;
    private Slider _chapterTimeline = null!;
    private TextBlock _chapterTime = null!;
    private Button _chapterPause = null!;
    private bool _chapterUpdating;
    private readonly DispatcherTimer _chapterAudioTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private void ConfigureChapterAudio(StackPanel host)
    {
        _chapterAudioControls = new StackPanel { Name = "ChapterAudioControls", Spacing = 8, IsVisible = false };
        var buttons = new WrapPanel();
        _chapterPause = LearningButton("暂停 / 续播", "ChapterPauseBtn"); _chapterPause.Click += (_, _) => { _wordAudio.TogglePause(); UpdateChapterTimeline(); };
        var back = LearningButton("后退 5 秒", "ChapterBackBtn"); back.Click += (_, _) => _wordAudio.Seek(_wordAudio.Position - 5);
        var forward = LearningButton("前进 5 秒", "ChapterForwardBtn"); forward.Click += (_, _) => _wordAudio.Seek(_wordAudio.Position + 5);
        foreach (var button in new[] { _chapterPause, back, forward }) { button.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(button); }
        _chapterTime = ContentText("", 12); _chapterTime.VerticalAlignment = VerticalAlignment.Center; buttons.Children.Add(_chapterTime);
        _chapterTimeline = new Slider { Name = "ChapterTimeline", Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        _chapterTimeline.PropertyChanged += (_, e) =>
        { if (!_chapterUpdating && e.Property == Slider.ValueProperty && _wordAudio.CanSeek) _wordAudio.Seek(_chapterTimeline.Value); };
        _chapterAudioControls.Children.Add(buttons); _chapterAudioControls.Children.Add(_chapterTimeline); host.Children.Add(_chapterAudioControls);
        _chapterAudioTimer.Tick += (_, _) => UpdateChapterTimeline();
        _ieltsPage!.KeyDown += (_, e) =>
        {
            if (e.Source is TextBox || !_chapterAudioControls.IsVisible || !_wordAudio.CanSeek || e.KeyModifiers != KeyModifiers.None) return;
            if (e.Key == Key.Space) _wordAudio.TogglePause();
            else if (e.Key == Key.Left) _wordAudio.Seek(_wordAudio.Position - 5);
            else if (e.Key == Key.Right) _wordAudio.Seek(_wordAudio.Position + 5);
            else return;
            UpdateChapterTimeline(); e.Handled = true;
        };
        Closed += (_, _) => _chapterAudioTimer.Stop();
    }
    private void ShowChapterTimeline()
    {
        if (_chapterAudioControls == null) return;
        _chapterAudioControls.IsVisible = _wordAudio.CanSeek;
        UpdateChapterTimeline();
        if (_wordAudio.CanSeek) _chapterAudioTimer.Start();
    }
    private void UpdateChapterTimeline()
    {
        if (_chapterAudioControls == null || !_wordAudio.CanSeek) return;
        _chapterUpdating = true;
        _chapterTimeline.Maximum = Math.Max(1, _wordAudio.Duration); _chapterTimeline.Value = _wordAudio.Position;
        _chapterTime.Text = $"{TimeSpan.FromSeconds(_wordAudio.Position):mm\\:ss} / {TimeSpan.FromSeconds(_wordAudio.Duration):mm\\:ss}";
        _chapterUpdating = false;
    }
}
