using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
namespace Lexi;

/// <summary>Full 53×7 square grid. Daily colour heatmap; aggregated periods fill columns bottom-up.</summary>
public sealed class StatisticsHeatmap : Control
{
    public StudyStatisticsSnapshot Snapshot { get; set; } = new();
    public StatisticsPeriod Period { get; set; }
    public StatisticsActivityFilter Filter { get; set; }
    public int Year { get; set; } = DateTime.Today.Year;
    public Func<string,string,string> Text { get; set; } = (zh,en)=>zh;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer = new() { Interval=TimeSpan.FromMilliseconds(16) };
    public double RevealProgress => _reveal;
    public new bool IsAnimating => _timer.IsEnabled;
    private bool _animate;
    private double _reveal=1;
    private static readonly Color Blue=Color.Parse("#69B6D2");
    private static readonly IBrush Empty = new SolidColorBrush(Color.Parse("#EDF1F4"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#728492"));
    public StatisticsHeatmap() { Height=152; MinWidth=350; _timer.Tick+=(_,_)=> { _reveal=Math.Min(1,_clock.Elapsed.TotalMilliseconds/800); InvalidateVisual(); if(_reveal>=1)_timer.Stop(); }; }
    public void Replay(bool reduceMotion) { _timer.Stop();_animate=!reduceMotion;_reveal=reduceMotion?1:0;_clock.Restart();if(!reduceMotion)_timer.Start();InvalidateVisual(); }
    public void StopAnimation() { _timer.Stop();_reveal=1;InvalidateVisual(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _timer.Stop();base.OnDetachedFromVisualTree(e); }
    private (double Size,double Pitch,double Left) Layout() { var pitch=Math.Max(5,(Bounds.Width-42)/StudyStatistics.BuildGrid(Snapshot,Year,Period,Filter).Count);return(Math.Min(12,pitch-3),pitch,36); }
    private static FormattedText Label(string text,IBrush brush) => new(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Inter"),10,brush);
    public override void Render(DrawingContext context)
    {
        base.Render(context); var (size,pitch,left)=Layout();var top=24d;
        var ink=(IBrush?)this.FindResource(ActualThemeVariant,"MutedBrush")??Ink;
        var empty=(IBrush?)this.FindResource(ActualThemeVariant,"InsetBrush")??Empty;
        var columns=StudyStatistics.BuildGrid(Snapshot,Year,Period,Filter);
        var max=Period==StatisticsPeriod.Day ? Snapshot.Days.Where(p=>p.Key.Year==Year).Select(p=>p.Value.Seconds(Filter)).DefaultIfEmpty().Max() : columns.Max(c=>c.Seconds);
        var first=new DateOnly(Year,1,1);var start=first.AddDays(-(((int)first.DayOfWeek+6)%7));
        for(var col=0;col<columns.Count;col++)
        {
            var alpha=_animate?Math.Clamp((_reveal*1.18-col/(double)columns.Count)/0.18,0,1):1;
            using(context.PushOpacity(alpha))
            for(var row=0;row<7;row++)
            {
                var brush=empty;var column=columns[col];
                if(Period==StatisticsPeriod.Day) {
                    var date=start.AddDays(col*7+row);
                    if(date.Year==Year&&date<=Snapshot.Today&&Snapshot.Days.TryGetValue(date,out var day)&&day.Seconds(Filter)>0)
                    {
                        var strength=max>0?day.Seconds(Filter)/max:0;
                        var color=strength<.25?"#DFEDF3":strength<.5?"#A7D3E3":strength<.75?"#69B6D2":"#357B9B";
                        brush=new SolidColorBrush(Color.Parse(color));
                    }
                } else { var cells=column.Seconds<=0?0:Math.Max(1,(int)Math.Ceiling(column.Seconds/max*7));if(!column.Future&&row>=7-cells)brush=new SolidColorBrush(Blue); }
                context.DrawRectangle(brush,null,new Rect(left+col*pitch,top+row*(size+3),size,size),2,2);
            }
        }
        var rowNames=Period==StatisticsPeriod.Day ? new[] {Text("一","M"),"",Text("三","W"),"",Text("五","F"),"",""} : new[] {max>0?$"{max/60:0.#}":"0","","","","","","0"};
        for(var i=0;i<7;i++) if(rowNames[i].Length>0)context.DrawText(Label(rowNames[i],ink),new Point(0,top+i*(size+3)));
        var lastMonth=0;
        for(var i=0;i<columns.Count;i++) { var date=start.AddDays(i*7+3);if(date.Year!=Year||date.Month==lastMonth)continue;lastMonth=date.Month;var label=Text(date.Month+"月",CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(date.Month));context.DrawText(Label(label,ink),new Point(left+i*pitch,2)); }
        var units=Period==StatisticsPeriod.Day?Text("色深 · 有效学习分钟","Colour · active minutes"):Text("柱高 · 有效学习分钟","Column height · active minutes");
        context.DrawText(Label(units,ink),new Point(left,top+7*(size+3)+8));
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);var(size,pitch,left)=Layout();var p=e.GetPosition(this);var col=(int)Math.Floor((p.X-left)/pitch);var row=(int)Math.Floor((p.Y-24)/(size+3));
        if(col<0||col>=StudyStatistics.BuildGrid(Snapshot,Year,Period,Filter).Count||row<0||row>=7){ToolTip.SetTip(this,null);return;}
        if(Period==StatisticsPeriod.Day) {
            var first=new DateOnly(Year,1,1);var start=first.AddDays(-(((int)first.DayOfWeek+6)%7));var date=start.AddDays(col*7+row);Snapshot.Days.TryGetValue(date,out var day);day??=new();
            ToolTip.SetTip(this,$"{date:yyyy-MM-dd}\n"+Text($"初学 {day.FirstLearnSeconds/60:0.#} · 复习 {day.ReviewSeconds/60:0.#} · 拼写 {day.SpellingSeconds/60:0.#} 分钟\n新学 {day.NewWords} 个词",$"First learn {day.FirstLearnSeconds/60:0.#} · Review {day.ReviewSeconds/60:0.#} · Spelling {day.SpellingSeconds/60:0.#} min\n{day.NewWords} newly learned words"));
        } else {
            var column=StudyStatistics.BuildGrid(Snapshot,Year,Period,Filter)[col];
            ToolTip.SetTip(this,$"{column.Start:yyyy-MM-dd} — {column.End:yyyy-MM-dd}\n"+ (column.Future?Text("未来日期 · 暂无记录","Future date · no record"):Text($"初学 {column.FirstLearnSeconds/60:0.#} · 复习 {column.ReviewSeconds/60:0.#} · 拼写 {column.SpellingSeconds/60:0.#} 分钟\n新学 {column.NewWords} 个词",$"First learn {column.FirstLearnSeconds/60:0.#} · Review {column.ReviewSeconds/60:0.#} · Spelling {column.SpellingSeconds/60:0.#} min\n{column.NewWords} newly learned words")));
        }
    }
}
public sealed class StatisticsCurve : Control
{
    public IReadOnlyList<(double Days,double Rate)> Prediction { get; set; } = [];
    public IReadOnlyList<RecallObservation> Observations { get; set; } = [];
    public Func<string,string,string> Text { get; set; } = (zh,en)=>zh;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(16)};
    public double RevealProgress => _reveal;
    public new bool IsAnimating => _timer.IsEnabled;
    private readonly Stopwatch _clock=new();private double _reveal=1;
    private static readonly IBrush Ink=new SolidColorBrush(Color.Parse("#728492"));
    public StatisticsCurve() { Height=230;MinWidth=320;_timer.Tick+=(_,_)=>{_reveal=Math.Min(1,_clock.Elapsed.TotalMilliseconds/720);InvalidateVisual();if(_reveal>=1)_timer.Stop();}; }
    public void Replay(bool reduced) { _timer.Stop();_reveal=reduced?1:0;_clock.Restart();if(!reduced)_timer.Start();InvalidateVisual(); }
    public void StopAnimation() { _timer.Stop();_reveal=1;InvalidateVisual(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e){_timer.Stop();base.OnDetachedFromVisualTree(e);}
    private FormattedText Label(string text)=>new(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Inter"),11,(IBrush?)this.FindResource(ActualThemeVariant,"MutedBrush")??Ink);
    public override void Render(DrawingContext context)
    {
        base.Render(context);var plot=new Rect(42,20,Math.Max(1,Bounds.Width-62),165);
        Point Map(double day,double rate)=>new(plot.X+day/30*plot.Width,plot.Bottom-Math.Clamp(rate,0,1)*plot.Height);
        var grid=new Pen((IBrush?)this.FindResource(ActualThemeVariant,"LineBrush")??new SolidColorBrush(Color.Parse("#E7EEF2")),1);
        for(var pct=0;pct<=100;pct+=25) { var y=Map(0,pct/100d).Y;context.DrawLine(grid,new Point(plot.X,y),new Point(plot.Right,y));context.DrawText(Label(pct+"%"),new Point(1,y-7)); }
        foreach(var day in new[]{0,7,14,21,30})context.DrawText(Label(day.ToString()),new Point(Map(day,0).X-4,plot.Bottom+8));
        context.DrawText(Label(Text("距上次学习的天数","Days since previous study")),new Point(plot.X,plot.Bottom+28));
        using(context.PushClip(new Rect(plot.X-3,plot.Y-3,plot.Width*_reveal+6,plot.Height+6)))
        {
            DrawLine(context,Prediction.Where(p=>p.Days<=30).Select(p=>Map(p.Days,p.Rate)).ToArray(),new Pen(new SolidColorBrush(Color.Parse("#357B9B")),2.5));
            var observed=Observations.Where(p=>p.Days<=30).ToArray();
            DrawLine(context,observed.Select(p=>Map(p.Days,p.Rate)).ToArray(),new Pen(new SolidColorBrush(Color.Parse("#79BDD4")),2));
            foreach(var p in observed)context.DrawEllipse(new SolidColorBrush(Color.Parse("#79BDD4")),new Pen(Brushes.White,1.5),Map(p.Days,p.Rate),4,4);
        }
        if(Prediction.Count==0&&Observations.Count==0)context.DrawText(Label(Text("完成学习后显示真实记录与模型预测","Study to see your observations and model prediction")),new Point(plot.X+12,plot.Y+68));
    }
    private static void DrawLine(DrawingContext context,Point[] points,Pen pen) { for(var i=1;i<points.Length;i++)context.DrawLine(pen,points[i-1],points[i]); }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);var plotWidth=Math.Max(1,Bounds.Width-62);var day=Math.Clamp((e.GetPosition(this).X-42)/plotWidth*30,0,30);
        var prediction=Prediction.OrderBy(p=>Math.Abs(p.Days-day)).FirstOrDefault();var observation=Observations.Where(p=>p.Days<=30).OrderBy(p=>Math.Abs(p.Days-day)).FirstOrDefault();
        var tip=Text($"间隔约 {day:0.#} 天",$"About {day:0.#} days after study");
        if(Prediction.Count>0)tip+="\n"+Text($"模型预计可回忆 {prediction.Rate:P0}",$"Predicted recall {prediction.Rate:P0}");
        if(observation!=null)tip+="\n"+Text($"最近的观察组：{observation.Rate:P0}（{observation.Samples} 次有效复习）",$"Nearest observed group: {observation.Rate:P0} ({observation.Samples} valid reviews)");
        ToolTip.SetTip(this,tip);
    }
}
public sealed class StatisticsTimeTrend : Control
{
    public StudyStatisticsSnapshot Snapshot { get; set; }=new();
    public Func<string,string,string> Text { get; set; }=(zh,en)=>zh;
    public StatisticsTimeTrend(){Height=170;MinWidth=300;}
    public override void Render(DrawingContext context)
    {
        base.Render(context);var ink=(IBrush?)this.FindResource(ActualThemeVariant,"MutedBrush")??new SolidColorBrush(Color.Parse("#728492"));var recent=Enumerable.Range(0,14).Select(i=>Snapshot.Today.AddDays(i-13)).ToArray();
        var max=recent.Select(d=>Snapshot.Days.TryGetValue(d,out var a)?a.Seconds(StatisticsActivityFilter.All):0).DefaultIfEmpty().Max();
        var width=Math.Max(1,Bounds.Width-50);var pitch=width/14;var bottom=135d;
        var colors=new[]{"#DFEDF3","#357B9B","#82B8CC"};
        for(var i=0;i<recent.Length;i++) { Snapshot.Days.TryGetValue(recent[i],out var d);d??=new();var values=new[]{d.FirstLearnSeconds,d.ReviewSeconds,d.SpellingSeconds};var y=bottom;
            for(var kind=0;kind<3;kind++){var h=max>0?values[kind]/max*105:0;y-=h;context.DrawRectangle(new SolidColorBrush(Color.Parse(colors[kind])),null,new Rect(35+i*pitch,y,Math.Max(3,pitch-6),h),2,2);}
            if(i%3==0||i==13)context.DrawText(new FormattedText(recent[i].ToString("MM/dd"),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Inter"),10,ink),new Point(35+i*pitch,bottom+7));
        }
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#E2EAF0")),1),new Point(35,bottom),new Point(Bounds.Width-10,bottom));
        context.DrawText(new FormattedText(Text($"{max/60:0.#} 分",$"{max/60:0.#} min"),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Inter"),10,ink),new Point(0,17));
    }
}
