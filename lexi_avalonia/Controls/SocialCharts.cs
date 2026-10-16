using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Lexi.Social;

namespace Lexi;

/// <summary>Predicted-recall curves for the current user and every friend still authorized to
/// share one. Series are shown or hidden independently and carry their own colour and legend
/// entry; the layout never sorts or scores them. The reveal animation is the same 720 ms
/// left-to-right clip the existing statistics curve uses, and is skipped entirely when the
/// user asks for reduced motion.</summary>
public sealed class SocialCurveOverlay : Control
{
    public sealed record SocialCurveSeries(string Label, string Color, IReadOnlyList<double> Points, int SampleCount, bool IsSelf)
    {
        public bool Visible { get; set; } = true;
    }

    public IReadOnlyList<SocialCurveSeries> Series { get; set; } = [];
    public Func<string, string, string> Text { get; set; } = (zh, en) => zh;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private double _reveal = 1;
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#728492"));

    public SocialCurveOverlay()
    {
        Height = 260; MinWidth = 320;
        _timer.Tick += (_, _) =>
        {
            _reveal = Math.Min(1, _clock.Elapsed.TotalMilliseconds / 720);
            InvalidateVisual();
            if (_reveal >= 1) _timer.Stop();
        };
    }

    public double RevealProgress => _reveal;
    public new bool IsAnimating => _timer.IsEnabled;

    public void Replay(bool reduced)
    {
        _timer.Stop(); _reveal = reduced ? 1 : 0; _clock.Restart();
        if (!reduced) _timer.Start();
        InvalidateVisual();
    }

    /// <summary>Called whenever the page is hidden or the session ends, so no timer outlives it.</summary>
    public void StopAnimation()
    {
        _timer.Stop(); _reveal = 1; InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private FormattedText Label(string text, double size = 11) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), size,
            (IBrush?)this.FindResource(ActualThemeVariant, "MutedBrush") ?? Ink);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Series.Count == 0) return;
        var plot = new Rect(46, 22, Math.Max(1, Bounds.Width - 70), Bounds.Height - 88);
        Point Map(double days, double rate) => new(plot.X + days / SocialCurve.MaximumDays * plot.Width,
            plot.Bottom - Math.Clamp(rate, 0, 1) * plot.Height);
        var grid = new Pen((IBrush?)this.FindResource(ActualThemeVariant, "LineBrush") ?? new SolidColorBrush(Color.Parse("#E7EEF2")), 1);
        for (var percent = 0; percent <= 100; percent += 25)
        {
            var y = Map(0, percent / 100d).Y;
            context.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));
            context.DrawText(Label(percent + "%"), new Point(1, y - 7));
        }
        foreach (var day in new[] { 0, 7, 14, 21, 30 })
            context.DrawText(Label(day.ToString(CultureInfo.InvariantCulture)), new Point(Map(day, 0).X - 4, plot.Bottom + 8));
        context.DrawText(Label(Text("距上次学习的天数", "Days since previous study")), new Point(plot.X, plot.Bottom + 26));

        using (context.PushClip(new Rect(plot.X - 3, plot.Y - 3, plot.Width * _reveal + 6, plot.Height + 6)))
        {
            foreach (var series in Series)
            {
                if (!series.Visible || series.Points.Count == 0) continue;
                var pen = new Pen(new SolidColorBrush(Color.Parse(series.Color)), series.IsSelf ? 2.5 : 2);
                if (!series.IsSelf) pen.DashStyle = new DashStyle([4, 3], 0);
                for (var index = 1; index < series.Points.Count; index++)
                    context.DrawLine(pen, Map((index - 1) * SocialCurve.MaximumDays / (series.Points.Count - 1), series.Points[index - 1]),
                        Map(index * SocialCurve.MaximumDays / (series.Points.Count - 1), series.Points[index]));
            }
        }

        // Legend: every entry is independently hideable and states its own sample count.
        var legendX = plot.X; var legendY = Bounds.Height - 34;
        foreach (var series in Series)
        {
            var swatch = new Rect(legendX, legendY + 3, 16, 3);
            context.DrawRectangle(new SolidColorBrush(Color.Parse(series.Color)), null, swatch);
            var label = Label(series.Label + " · " + series.SampleCount + (series.Visible ? "" : " " + Text("（已隐藏）", "(hidden)")), 10.5);
            context.DrawText(label, new Point(legendX + 21, legendY - 2));
            legendX += 21 + label.Width + 16;
            if (legendX > plot.Right - 60) { legendX = plot.X; legendY += 16; }
        }
        if (Series.All(s => !s.Visible))
            context.DrawText(Label(Text("全部曲线已隐藏，点击图例可重新显示。", "All curves are hidden. Use the legend to show one again.")),
                new Point(plot.X + 12, plot.Y + 68));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Series.Count == 0 || Bounds.Width <= 0) return;
        var plotWidth = Math.Max(1, Bounds.Width - 70);
        var days = Math.Clamp((e.GetPosition(this).X - 46) / plotWidth * SocialCurve.MaximumDays, 0, SocialCurve.MaximumDays);
        var tip = Text($"间隔约 {days:0.#} 天", $"About {days:0.#} days after study");
        foreach (var series in Series)
        {
            if (!series.Visible || series.Points.Count == 0) continue;
            var index = (int)Math.Round(days / SocialCurve.MaximumDays * (series.Points.Count - 1));
            tip += "\n" + series.Label + " " + series.Points[Math.Clamp(index, 0, series.Points.Count - 1)].ToString("P0", CultureInfo.CurrentCulture);
        }
        tip += "\n" + Text("模型预测，不做记忆能力排名。", "Model prediction only; this is not a ranking of memory ability.");
        ToolTip.SetTip(this, tip);
    }
}

/// <summary>One metric of the weekly detail: seven finished Beijing days, smoothed only inside
/// each run of consecutive known values. A day the server did not report leaves a gap, and the
/// line breaks rather than dropping to zero.</summary>
public sealed class SocialWeeklyChart : Control
{
    public IReadOnlyList<SocialWeekDay> Days { get; set; } = [];
    public Func<SocialWeekDay, double?> Value { get; set; } = _ => null;
    public Func<SocialWeekDay, string> Tooltip { get; set; } = _ => "";
    public string Unit { get; set; } = "";
    public string Title { get; set; } = "";
    public Func<string, string, string> Text { get; set; } = (zh, en) => zh;
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#728492"));

    public SocialWeeklyChart() { Height = 200; MinWidth = 300; }

    private FormattedText Label(string text, double size = 10.5) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), size,
            (IBrush?)this.FindResource(ActualThemeVariant, "MutedBrush") ?? Ink);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Days.Count == 0) return;
        var plot = new Rect(46, 20, Math.Max(1, Bounds.Width - 66), Bounds.Height - 62);
        var values = Days.Select(Value).ToArray();
        // A day with no value is absent data, not a zero, so it neither sets the axis nor joins a line.
        var known = values.Where(v => v is not null).Select(v => v!.Value).ToArray();
        var maximum = known.Length == 0 ? 0 : known.Max();
        if (maximum <= 0) maximum = 1;
        context.DrawText(Label(Title, 11), new Point(plot.X, 2));
        context.DrawText(Label(Unit), new Point(0, 6));

        var grid = new Pen((IBrush?)this.FindResource(ActualThemeVariant, "LineBrush") ?? new SolidColorBrush(Color.Parse("#E7EEF2")), 1);
        for (var step = 0; step <= 4; step++)
        {
            var y = plot.Bottom - plot.Height * step / 4;
            context.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));
            if (step % 2 == 0) context.DrawText(Label((maximum * step / 4).ToString("0.#", CultureInfo.CurrentCulture)), new Point(2, y - 7));
        }
        double X(int index) => plot.X + plot.Width * index / Math.Max(1, Days.Count - 1);
        double Y(double value) => plot.Bottom - Math.Clamp(value / maximum, 0, 1) * plot.Height;

        // Only consecutive known days are joined, and each run is smoothed on its own so the
        // interpolation can never span a gap or invent a value inside one.
        var run = new List<(double X, double Y)>();
        void Flush()
        {
            if (run.Count == 1) context.DrawEllipse(new SolidColorBrush(Color.Parse("#357B9B")), new Pen(Brushes.White, 1.5), new Point(run[0].X, run[0].Y), 3, 3);
            else if (run.Count > 1)
            {
                var smooth = MonotoneCurve.Smooth(run, 8);
                var pen = new Pen(new SolidColorBrush(Color.Parse("#357B9B")), 2.2);
                for (var index = 1; index < smooth.Count; index++)
                    context.DrawLine(pen, new Point(smooth[index - 1].X, smooth[index - 1].Y), new Point(smooth[index].X, smooth[index].Y));
                foreach (var node in run)
                    context.DrawEllipse(new SolidColorBrush(Color.Parse("#357B9B")), new Pen(Brushes.White, 1.5), new Point(node.X, node.Y), 3, 3);
            }
            run.Clear();
        }
        for (var index = 0; index < Days.Count; index++)
        {
            if (values[index] is { } value) run.Add((X(index), Y(value)));
            else Flush();
        }
        Flush();

        for (var index = 0; index < Days.Count; index++)
            context.DrawText(Label(Days[index].Date.ToString("MM-dd", CultureInfo.InvariantCulture)),
                new Point(X(index) - 15, plot.Bottom + 8));
        if (known.Length == 0)
            context.DrawText(Label(Text("这段时间没有已授权的数据。", "No authorized data in this window.")), new Point(plot.X + 12, plot.Y + 60));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Days.Count == 0 || Bounds.Width <= 0) return;
        var plotWidth = Math.Max(1, Bounds.Width - 66);
        var index = (int)Math.Round((e.GetPosition(this).X - 46) / plotWidth * (Days.Count - 1));
        if (index < 0 || index >= Days.Count) return;
        ToolTip.SetTip(this, Tooltip(Days[index]));
    }
}
