using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace Lexi.Features.Ielts;

/// <summary>
/// 独立紧凑章节音频播放器：
/// 支持播放、暂停、停止、前进/后退 10 秒、拖动进度 Seek 与时间指示。
/// 无资源时诚实解释原因，单一播放所有者。
/// 1.2.3：按钮统一为线条图标（Path），降低控件高度，保留现有播放/停止/Seek/时间 API。
/// </summary>
public sealed class IeltsAudioBar : Border, IDisposable
{
    private const string PlayIcon = "M 5,3 L 13,8 L 5,13 Z";
    private const string PauseIcon = "M 4,3 L 7,3 L 7,13 L 4,13 Z M 9,3 L 12,3 L 12,13 L 9,13 Z";
    private const string StopIcon = "M 4,4 L 12,4 L 12,12 L 4,12 Z";
    private const string RewindIcon = "M 9,3 L 9,13 L 4,8 Z M 14,3 L 14,13 L 9,8 Z";
    private const string ForwardIcon = "M 2,3 L 2,13 L 7,8 Z M 7,3 L 7,13 L 12,8 Z";

    private readonly Func<IWordAudioPlayer> _playerProvider;
    private readonly Action<string> _setStatus;
    private readonly DispatcherTimer _pollTimer;

    private readonly TextBlock _titleBlock;
    private readonly Button _rewindBtn;
    private readonly Button _playPauseBtn;
    private readonly Button _forwardBtn;
    private readonly Button _stopBtn;
    private readonly Path _playPauseIcon;
    private readonly TextBlock _currentTimeBlock;
    private readonly Slider _seekSlider;
    private readonly TextBlock _durationBlock;
    private readonly TextBlock _noAudioNotice;
    private readonly Grid _controlsGrid;

    private string? _currentAudioFile;
    private string _currentSectionTitle = "";
    private bool _isDraggingSlider;
    private bool _hasAudio;
    private bool _isPlayingVisual;

    public IeltsAudioBar(Func<IWordAudioPlayer> playerProvider, Action<string> setStatus)
    {
        _playerProvider = playerProvider;
        _setStatus = setStatus;

        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(20, 6);
        MinHeight = 40;
        Background = Brushes.Transparent;
        this.Bind(BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var mainLayout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };

        // 左侧：录音章节名称
        _titleBlock = new TextBlock
        {
            FontWeight = FontWeight.Medium,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0)
        };
        _titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        Grid.SetColumn(_titleBlock, 0);
        mainLayout.Children.Add(_titleBlock);

        // 中部：播放器控件栏（统一线条图标）
        _controlsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,6,Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };

        _rewindBtn = CreateIconButton(RewindIcon, () => SeekRelative(-10));
        Grid.SetColumn(_rewindBtn, 0);
        _controlsGrid.Children.Add(_rewindBtn);

        _playPauseIcon = new Path
        {
            Data = Geometry.Parse(PlayIcon),
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform
        };
        _playPauseIcon.Bind(Shape.StrokeProperty, this.GetResourceObservable("MutedBrush"));
        _playPauseBtn = new Button
        {
            Classes = { "row-btn" },
            Padding = new Thickness(6, 4),
            Margin = new Thickness(0, 0, 3, 0),
            Content = _playPauseIcon
        };
        _playPauseBtn.Click += (_, _) => TogglePlayPause();
        Grid.SetColumn(_playPauseBtn, 1);
        _controlsGrid.Children.Add(_playPauseBtn);

        _forwardBtn = CreateIconButton(ForwardIcon, () => SeekRelative(10));
        Grid.SetColumn(_forwardBtn, 2);
        _controlsGrid.Children.Add(_forwardBtn);

        _stopBtn = CreateIconButton(StopIcon, Stop);
        Grid.SetColumn(_stopBtn, 3);
        _controlsGrid.Children.Add(_stopBtn);

        _currentTimeBlock = new TextBlock
        {
            Text = "00:00",
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(_currentTimeBlock, 5);
        _controlsGrid.Children.Add(_currentTimeBlock);

        _seekSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0)
        };
        _seekSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) => _isDraggingSlider = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _seekSlider.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            _isDraggingSlider = false;
            var player = _playerProvider();
            player.Seek(_seekSlider.Value);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _seekSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && _isDraggingSlider)
            {
                _currentTimeBlock.Text = FormatTime(_seekSlider.Value);
            }
        };
        Grid.SetColumn(_seekSlider, 6);
        _controlsGrid.Children.Add(_seekSlider);

        _durationBlock = new TextBlock
        {
            Text = "00:00",
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(_durationBlock, 7);
        _controlsGrid.Children.Add(_durationBlock);

        Grid.SetColumn(_controlsGrid, 1);
        mainLayout.Children.Add(_controlsGrid);

        // 无音频录音时的诚实提示
        _noAudioNotice = new TextBlock
        {
            Text = IeltsI18n.T("当前章节暂无独立音频录音。"),
            Classes = { "muted" },
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        Grid.SetColumn(_noAudioNotice, 1);
        mainLayout.Children.Add(_noAudioNotice);

        Child = mainLayout;
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 700;
            _titleBlock.IsVisible = !compact;
            mainLayout.ColumnDefinitions[0].Width = compact ? new GridLength(0) : GridLength.Auto;
        };

        ApplyTransportLabels();
        SetPlayPause(false);

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    public void LoadSection(LearningSection section)
    {
        _currentSectionTitle = section.Title;
        _currentAudioFile = IeltsCatalog.ResolveAsset(section.AudioPath);
        _hasAudio = !string.IsNullOrEmpty(_currentAudioFile);

        _titleBlock.Text = IeltsI18n.T("章节录音") + " · " + section.Title;
        _controlsGrid.IsVisible = _hasAudio;
        _noAudioNotice.IsVisible = !_hasAudio;

        // 切章时停止之前章节的录音播放
        Stop();
    }

    private void TogglePlayPause()
    {
        if (!_hasAudio || string.IsNullOrEmpty(_currentAudioFile))
        {
            _setStatus(IeltsI18n.T("当前章节暂无独立音频录音。"));
            return;
        }

        var player = _playerProvider();
        if (player.IsPlaying)
        {
            player.TogglePause();
            SetPlayPause(false);
        }
        else
        {
            if (player.Position > 0 && player.Position < player.Duration - 1)
            {
                player.TogglePause();
            }
            else
            {
                player.Play("", _currentAudioFile);
            }
            SetPlayPause(true);
        }
    }

    private void SeekRelative(double seconds)
    {
        if (!_hasAudio) return;
        var player = _playerProvider();
        player.Seek(player.Position + seconds);
    }

    public void Stop()
    {
        var player = _playerProvider();
        player.Stop();
        SetPlayPause(false);
        _seekSlider.Value = 0;
        _currentTimeBlock.Text = "00:00";
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (!_hasAudio) return;
        var player = _playerProvider();
        var isPlaying = player.IsPlaying;

        if (isPlaying)
        {
            SetPlayPause(true);
            var pos = player.Position;
            var dur = player.Duration;

            if (dur > 0 && _seekSlider.Maximum != dur)
                _seekSlider.Maximum = dur;

            if (!_isDraggingSlider)
            {
                _seekSlider.Value = pos;
                _currentTimeBlock.Text = FormatTime(pos);
            }
            _durationBlock.Text = FormatTime(dur);

            // 播放完成回位
            if (dur > 0 && pos >= dur - 0.3)
            {
                SetPlayPause(false);
            }
        }
        else if (_isPlayingVisual)
        {
            SetPlayPause(false);
        }
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>创建线条图标按钮：统一 Path 图标 + row-btn 样式，前景绑定 MutedBrush。</summary>
    private Button CreateIconButton(string data, Action onClick)
    {
        var icon = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform
        };
        icon.Bind(Shape.StrokeProperty, this.GetResourceObservable("MutedBrush"));
        var btn = new Button
        {
            Classes = { "row-btn" },
            Padding = new Thickness(6, 4),
            Margin = new Thickness(0, 0, 3, 0),
            Content = icon
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>切换播放/暂停图标与可访问名称。</summary>
    private void SetPlayPause(bool playing)
    {
        _isPlayingVisual = playing;
        _playPauseIcon.Data = Geometry.Parse(playing ? PauseIcon : PlayIcon);
        var name = IeltsI18n.T(playing ? "暂停" : "播放");
        ToolTip.SetTip(_playPauseBtn, name);
        AutomationProperties.SetName(_playPauseBtn, name);
    }

    /// <summary>刷新后退/前进/停止按钮的可访问名称（双语）。</summary>
    private void ApplyTransportLabels()
    {
        var stop = IeltsI18n.T("停止");
        ToolTip.SetTip(_stopBtn, stop);
        AutomationProperties.SetName(_stopBtn, stop);

        var rewind = Lexi.UiText.Bilingual("后退 10 秒", "Rewind 10 seconds");
        ToolTip.SetTip(_rewindBtn, rewind);
        AutomationProperties.SetName(_rewindBtn, rewind);

        var forward = Lexi.UiText.Bilingual("前进 10 秒", "Forward 10 seconds");
        ToolTip.SetTip(_forwardBtn, forward);
        AutomationProperties.SetName(_forwardBtn, forward);
    }

    public void RefreshLanguage()
    {
        _titleBlock.Text = IeltsI18n.T("章节录音") + (_currentSectionTitle.Length > 0 ? " · " + _currentSectionTitle : "");
        _noAudioNotice.Text = IeltsI18n.T("当前章节暂无独立音频录音。");
        ApplyTransportLabels();
        SetPlayPause(_playerProvider().IsPlaying);
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        Stop();
    }
}
