using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
namespace Lexi;

public partial class MainWindow
{
    private Grid? _statisticsPage;
    private ScrollViewer? _statisticsScroll;
    private StudyStatisticsSnapshot _statisticsSnapshot = new();
    private StatisticsHeatmap? _statisticsHeatmap;
    private StatisticsCurve? _statisticsCurve;
    private readonly StudyActivityTracker _statisticsTracker = new();
    private readonly List<StudyActivity> _statisticsPending = [];
    private StudyActivityStore? _statisticsActivityStore;
    private DispatcherTimer? _statisticsTimer;
    private readonly Stopwatch _statisticsInteractionClock = Stopwatch.StartNew();
    private TimeSpan _statisticsLastInteraction;
    private StatisticsPeriod _statisticsPeriod;
    private StatisticsActivityFilter _statisticsFilter;
    private int _statisticsYear = DateTime.Today.Year;
    private int _statisticsTicks;
    private bool _statisticsConfiguring, _statisticsFlushing, _statisticsStopped, _statisticsExpandedWords, _statisticsLifetimeRanking, _statisticsExplanation;
    private bool _statisticsHistoryFailed, _statisticsActivityFailed, _statisticsRecovered;
    private IReadOnlyList<(double Days,double Rate)> _statisticsPrediction = [];
    private int _statisticsPredictionCount;
    private string? _statisticsStorePath;
    private static string StatText(string chinese,string english) => UiText.Language == "en" ? english : chinese;
    private static IBrush StatBrush(string color) => new SolidColorBrush(Color.Parse(color));
    private static TextBlock StatLabel(string text,double size=13,string color="#617987")
    {
        var label=new TextBlock { Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,LineHeight=size*1.35 };
        var key=color is "#243F50" or "#3F6579"?"InkBrush":color is "#235D79" or "#357B9B"?"PrimaryGreen":"MutedBrush";
        label.Bind(TextBlock.ForegroundProperty,new DynamicResourceExtension(key));return label;
    }
    private static string StatDuration(double seconds) => seconds<=0 ? StatText("暂无记录","No record") : seconds>=3600 ? StatText($"{seconds/3600:0.#} 小时",$"{seconds/3600:0.#} h") : StatText($"{seconds/60:0.#} 分钟",$"{seconds/60:0.#} min");
    private static Border StatCard(Control child)
    {
        var card=new Border { Child=child,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),Padding=new Thickness(24) };
        card.Bind(Border.BackgroundProperty,new DynamicResourceExtension("CardBrush"));card.Bind(Border.BorderBrushProperty,new DynamicResourceExtension("LineBrush"));return card;
    }
    private static Button StatButton(string text) => new() { Content=text,Classes={"secondary"},Padding=new Thickness(12,7),FontSize=12 };

    private void ConfigureStatistics()
    {
        if(_statisticsPage!=null)return;
        this.FindControl<Button>("NavStatistics")!.Content=StatText("学习统计","Statistics");
        this.FindControl<Button>("NavStatistics")!.Click+=(_,_)=> { if(FocusCanNavigate)ShowPage("statistics"); };
        _statisticsPage=new Grid { Name="StatisticsPage",IsVisible=false,Margin=new Thickness(30,24,30,20) };
        ((Panel)LookupPageHost.Parent!).Children.Add(_statisticsPage);
        AddHandler(KeyDownEvent,(_,_)=>StatisticsInteraction(),RoutingStrategies.Tunnel,true);
        AddHandler(PointerPressedEvent,(_,_)=>StatisticsInteraction(),RoutingStrategies.Tunnel,true);
        AddHandler(PointerWheelChangedEvent,(_,_)=>StatisticsInteraction(),RoutingStrategies.Tunnel,true);
        // Pauses immediately on focus loss, hiding and minimization; no background time is inferred.
        PropertyChanged+=(_,e)=> { if(e.Property==IsActiveProperty||e.Property==IsVisibleProperty||e.Property==WindowStateProperty) StatisticsTick(); };
        _statisticsTimer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1) };
        _statisticsTimer.Tick+=(_,_)=> { StatisticsTick();if(++_statisticsTicks%15==0)_=FlushStatisticsActivityAsync(); };
        _statisticsTimer.Start();
        RenderStatisticsPage();
    }
    private void StatisticsInteraction() { _statisticsLastInteraction=_statisticsInteractionClock.Elapsed;StatisticsTick(); }
    private StudyActivityKind? StatisticsCurrentActivity()
    {
        if(_typingPlaying&&_typingSession.Current!=null&&_typingSession.Outcome!=TypingOutcome.Correct&&_currentPage=="typing")return StudyActivityKind.Spelling;
        if(_wordFocusActive&&_focusRound.HasCurrent&&_currentPage=="lookup")
            return (_planCardActive?_planLearningSession?.Mode:MemoryFocusCommitMode(_focusRound.Current))==StudyMode.FirstLearn?StudyActivityKind.FirstLearn:StudyActivityKind.Review;
        if(_currentPage=="review"&&_reviewRound.HasCurrent)return StudyActivityKind.Review;
        return null;
    }
    private void StatisticsTick()
    {
        if(_statisticsStopped)return;
        var kind=StatisticsCurrentActivity();
        _statisticsTracker.Advance(DateTime.UtcNow,kind,IsActive&&IsVisible&&WindowState!=WindowState.Minimized&&_databaseAvailable,
            _statisticsInteractionClock.Elapsed-_statisticsLastInteraction<TimeSpan.FromSeconds(60));
        if(_currentPage!="statistics") { _statisticsHeatmap?.StopAnimation();_statisticsCurve?.StopAnimation(); }
    }
    private void EnsureStatisticsActivityStore()
    {
        var path=Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!,"study-activity.json");
        if(_statisticsStorePath==path)return;
        _statisticsStorePath=path;_statisticsActivityStore=new(path);
    }
    private async Task FlushStatisticsActivityAsync()
    {
        if(_statisticsFlushing||_statisticsStopped)return;
        _statisticsPending.AddRange(_statisticsTracker.Drain());
        if(_statisticsPending.Count==0)return;
        EnsureStatisticsActivityStore(); var pending=_statisticsPending.ToArray();var store=_statisticsActivityStore!;
        _statisticsFlushing=true;
        try { var result=await Task.Run(()=>store.Append(pending));
            _statisticsActivityFailed=!result.Success;
            if(result.Success) { var ids=pending.Select(a=>a.ActivityId).ToHashSet();_statisticsPending.RemoveAll(a=>ids.Contains(a.ActivityId)); }
        } finally { _statisticsFlushing=false; }
    }
    private void StatisticsStopTracking()
    {
        StatisticsTick();_statisticsPending.AddRange(_statisticsTracker.Drain());_statisticsStopped=true;_statisticsTimer?.Stop();_statisticsHeatmap?.StopAnimation();_statisticsCurve?.StopAnimation();
        if(_statisticsPending.Count==0)return;
        EnsureStatisticsActivityStore();var result=_statisticsActivityStore!.Append(_statisticsPending);
        _statisticsActivityFailed=!result.Success;if(result.Success)_statisticsPending.Clear();
    }
    private void RefreshStatisticsLanguage() { if(_statisticsPage!=null) { this.FindControl<Button>("NavStatistics")!.Content=StatText("学习统计","Statistics"); RenderStatisticsPage();if(_currentPage=="statistics")ReplayStatistics(); } }
    private void StatisticsEnterPage() { RefreshStatistics();ReplayStatistics(); }
    private void ReplayStatistics() { var reduced=ReduceMotionBox.IsChecked.GetValueOrDefault();_statisticsHeatmap?.Replay(reduced);_statisticsCurve?.Replay(reduced); }
    private void RefreshStatistics()
    {
        if(_statisticsPage==null)return;
        StatisticsTick();_statisticsPending.AddRange(_statisticsTracker.Drain());EnsureStatisticsActivityStore();
        var stored=_statisticsActivityStore!.Read();_statisticsActivityFailed=!stored.Writable;_statisticsRecovered=stored.Recovered;
        IReadOnlyList<CanonicalHistoryRow> rows=[];_statisticsHistoryFailed=false;
        try { if(_memoryStore!=null)rows=_memoryStore.LoadCanonicalHistory(null,DateTime.UtcNow.AddTicks(1),null);else _statisticsHistoryFailed=true; }
        catch(Exception ex) { _statisticsHistoryFailed=true;Debug.WriteLine("[statistics] history unavailable: "+ex.GetType().Name); }
        _statisticsSnapshot=StudyStatistics.Build(rows,stored.Activities.Concat(_statisticsPending),DateTime.UtcNow,TimeZoneInfo.Local);
        _statisticsPrediction=[];_statisticsPredictionCount=0;
        if(!_statisticsHistoryFailed&&_memoryStore!=null)
        {
            try {
                var cards=rows.Select(r=>r.WordKey).Distinct(StringComparer.Ordinal).Select(k=>_memoryStore.GetCard(k)).Where(c=>c!=null&&double.IsFinite(c.Stability)&&c.Stability>0).ToArray();
                var scheduler=_memoryPersonalization!=null?new Fsrs6Scheduler(_memoryPersonalization.Weights):new Fsrs6Scheduler();
                _statisticsPredictionCount=cards.Length;
                if(cards.Length>0)_statisticsPrediction=Enumerable.Range(0,121).Select(i=>(Days:i/4d,Rate:cards.Average(c=>scheduler.Retrievability(c!.Stability,i/4d)))).ToArray();
            } catch(Exception ex) { Debug.WriteLine("[statistics] prediction unavailable: "+ex.GetType().Name); }
        }
        RenderStatisticsPage();
    }
    private void RenderStatisticsPage()
    {
        if(_statisticsPage==null)return;
        _statisticsConfiguring=true;
        var offset=_statisticsScroll?.Offset??default;
        _statisticsHeatmap?.StopAnimation();_statisticsCurve?.StopAnimation();
        var body=new StackPanel { Spacing=14,MaxWidth=1120,HorizontalAlignment=HorizontalAlignment.Stretch };
        body.Children.Add(StatLabel(StatText("学习统计","Learning statistics"),24,"#243F50"));
        body.Children.Add(StatLabel(StatText("基于有效学习记录 · 当前本地时区 · 时长从此版本开始记录","Valid learning records · Current local time zone · Duration recorded from this version")));
        if(_statisticsHistoryFailed||_statisticsActivityFailed)body.Children.Add(StatCard(StatLabel(
            _statisticsHistoryFailed?StatText("学习历史暂时无法读取；恢复后可重新进入本页。这里没有把读取失败当作零次学习。","Learning history is unavailable. Reopen this page after recovery; missing data is not treated as zero study."):
            _statisticsRecovered?StatText("活动记录损坏，已读取最近完整备份。原文件保留，暂停写入；当前未保存时长仍保留在内存中。","Activity records were recovered from the latest complete backup. The original file is preserved and writing is paused; unsaved time remains in memory."):
            StatText("学习时长暂时无法保存或读取；原记录已保留，未保存的时长仍保留在内存中。","Study duration cannot currently be saved or read. Existing records are preserved and unsaved time remains in memory."),13,"#8E6A3A")));
        var metrics=new UniformGrid { Name="StatisticsMetrics",Columns=4,Rows=1,Margin=new Thickness(-6,0,-6,0) };
        metrics.Children.Add(StatMetric(StatText("学习天数","Study days"),_statisticsHistoryFailed?"—":_statisticsSnapshot.LearningDays.ToString(),StatText("有完成学习或有效练习的日期","Days with completed learning or active practice")));
        metrics.Children.Add(StatMetric(StatText("去重已学词汇","Unique learned words"),_statisticsHistoryFailed?"—":_statisticsSnapshot.LearnedWords.ToString(),StatText("初学与复习共享稳定词身份","Stable word identity across learning and review")));
        metrics.Children.Add(StatMetric(StatText("单词卡时长","Flashcard time"),StatDuration(_statisticsSnapshot.CardSeconds),StatText($"初学 {StatDuration(_statisticsSnapshot.FirstLearnSeconds)} · 复习 {StatDuration(_statisticsSnapshot.ReviewSeconds)}",$"First learn {StatDuration(_statisticsSnapshot.FirstLearnSeconds)} · Review {StatDuration(_statisticsSnapshot.ReviewSeconds)}")));
        metrics.Children.Add(StatMetric(StatText("拼写时长","Spelling time"),StatDuration(_statisticsSnapshot.SpellingSeconds),StatText("专注练习时间 · 离开或闲置即暂停","Active practice · Paused when hidden or idle")));
        body.Children.Add(metrics);
        var peaks=new WrapPanel { Orientation=Orientation.Horizontal,ItemSpacing=28,LineSpacing=12 };
        foreach(var item in new[] { (StatText("单日词汇峰值","Peak words / day"),_statisticsSnapshot.PeakWords.ToString()),(StatText("单日时长峰值","Peak time / day"),StatDuration(_statisticsSnapshot.PeakSeconds)),(StatText("最长学习会话","Longest session"),StatDuration(_statisticsSnapshot.LongestSessionSeconds)),(StatText("最长连续","Longest streak"),StatText($"{_statisticsSnapshot.LongestStreak} 天",$"{_statisticsSnapshot.LongestStreak} days")),(StatText("当前连续","Current streak"),StatText($"{_statisticsSnapshot.CurrentStreak} 天",$"{_statisticsSnapshot.CurrentStreak} days")) }) {
            var p=new StackPanel{Spacing=4};p.Children.Add(StatLabel(item.Item1,11));p.Children.Add(StatLabel(item.Item2,15,"#243F50"));peaks.Children.Add(p);
        }
        var peaksCard=StatCard(peaks);peaksCard.Padding=new Thickness(18);body.Children.Add(peaksCard);
        body.Children.Add(BuildStatisticsHeatmapCard());
        var comparison=new Grid { ColumnDefinitions=new ColumnDefinitions("*"),RowDefinitions=new RowDefinitions("Auto,Auto") };
        var curveCard=BuildStatisticsCurveCard();var wordsCard=BuildStatisticsWordsCard();
        comparison.Children.Add(curveCard);comparison.Children.Add(wordsCard);
        void LayoutComparison() {
            var wide=Bounds.Width>=1100;
            comparison.ColumnDefinitions=new ColumnDefinitions(wide?"*,*":"*");
            Grid.SetRow(wordsCard,wide?0:1);Grid.SetColumn(wordsCard,wide?1:0);
            curveCard.Margin=new Thickness(0,0,wide?10:0,wide?0:20);wordsCard.Margin=new Thickness(wide?10:0,0,0,0);
        }
        LayoutComparison();comparison.SizeChanged+=(_,_)=>LayoutComparison();body.Children.Add(comparison);
        var trend=new StackPanel{Spacing=10};trend.Children.Add(StatLabel(StatText("学习时长趋势","Study duration trend"),16,"#243F50"));
        trend.Children.Add(StatLabel(StatText("近14天 · 初学（浅蓝）／复习（深蓝）／拼写（中蓝）","Last 14 days · First learn (light) / Review (dark) / Spelling (medium)"),12));
        trend.Children.Add(new StatisticsTimeTrend { Snapshot=_statisticsSnapshot,Text=StatText });body.Children.Add(StatCard(trend));
        var facts=new StackPanel{Spacing=7};facts.Children.Add(StatLabel(StatText("数据口径","What these numbers mean"),16,"#243F50"));
        facts.Children.Add(StatLabel(StatText($"总有效复习 {_statisticsSnapshot.Reviews} 次。新词初学聚合不计为长期复习；改判使用最终有效结果，撤销记录不纳入。词数按既有稳定词身份去重，不按词形跨词库合并。",$"{_statisticsSnapshot.Reviews} valid reviews. First learning is excluded from long-term review counts; revisions use the final valid result and undone records are excluded. Words use the existing stable identity, without merging matching spellings across sources."),12));
        facts.Children.Add(StatLabel(StatText("时长只记录正在显示且处于活动状态窗口中的卡片或拼写练习。60秒无按键、点击或滚动即暂停；休眠间隔不计时。旧版未记录时长，显示暂无记录。拼写正确率与到期完成率缺少完整历史分母，暂不展示数值。","Time counts flashcard or spelling practice in a visible, active window. It pauses after 60 seconds without a key, click or scroll; sleep gaps are excluded. Older duration records are unavailable. Historical denominators for spelling accuracy and due-review completion are unavailable."),12));
        body.Children.Add(StatCard(facts));
        _statisticsScroll=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        _statisticsPage.Children.Clear();_statisticsPage.Children.Add(_statisticsScroll);
        var currentScroll=_statisticsScroll;Dispatcher.UIThread.Post(()=>{if(_statisticsScroll==currentScroll)currentScroll.Offset=offset;},DispatcherPriority.Loaded);
        _statisticsConfiguring=false;
    }
    private Border StatMetric(string title,string value,string detail)
    {
        var p=new StackPanel { Spacing=5 };
        var heading=StatLabel(title,12);
        var number=StatLabel(value,24,"#235D79");number.FontWeight=FontWeight.Medium;number.TextWrapping=TextWrapping.NoWrap;number.TextTrimming=TextTrimming.CharacterEllipsis;
        var note=StatLabel(detail,11);note.MaxHeight=32;ToolTip.SetTip(note,detail);
        p.Children.Add(heading);p.Children.Add(number);p.Children.Add(note);
        var card=StatCard(p);card.Margin=new Thickness(6);card.Padding=new Thickness(18);card.MinHeight=112;
        card.SizeChanged+=(_,_)=> { var narrow=card.Bounds.Width<170;card.Padding=new Thickness(narrow?10:18);number.FontSize=narrow?17:card.Bounds.Width<220?21:24;number.LineHeight=number.FontSize*1.2; };
        return card;
    }
    private Border BuildStatisticsHeatmapCard()
    {
        var panel=new StackPanel{Spacing=10};var header=new WrapPanel{ItemSpacing=18,LineSpacing=8};header.Children.Add(StatLabel(StatText("学习活动","Learning activity"),16,"#243F50"));
        var periods=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};
        foreach(var period in Enum.GetValues<StatisticsPeriod>()) {
            var title=period switch {StatisticsPeriod.Day=>StatText("每日","Daily"),StatisticsPeriod.Week=>StatText("每周","Weekly"),StatisticsPeriod.Month=>StatText("每月","Monthly"),_=>StatText("累计","Cumulative")};
            var button=StatButton(title);button.Name="StatisticsPeriod"+period;
            if(period==_statisticsPeriod)button.Bind(Button.BackgroundProperty,new DynamicResourceExtension("TintBrush"));else button.Background=Brushes.Transparent;button.Bind(Button.ForegroundProperty,new DynamicResourceExtension("PrimaryGreen"));
            button.Click+=(_,_)=>{_statisticsPeriod=period;RenderStatisticsPage();ReplayStatistics();};periods.Children.Add(button);
        }
        header.Children.Add(periods);var years=_statisticsSnapshot.Days.Keys.Select(d=>d.Year).Append(DateTime.Today.Year).Distinct().OrderDescending().ToArray();
        var year=new ComboBox { Name="StatisticsYear", ItemsSource=years,SelectedItem=_statisticsYear,MinWidth=92 };year.SelectionChanged+=(_,_)=>{if(!_statisticsConfiguring&&year.SelectedItem is int value){_statisticsYear=value;RenderStatisticsPage();ReplayStatistics();}};header.Children.Add(year);
        var filter=new ComboBox{Name="StatisticsFilter",ItemsSource=new[]{StatText("全部练习","All practice"),StatText("单词卡","Flashcards"),StatText("拼写","Spelling")},SelectedIndex=(int)_statisticsFilter,MinWidth=126};
        filter.SelectionChanged+=(_,_)=>{if(!_statisticsConfiguring){_statisticsFilter=(StatisticsActivityFilter)Math.Max(0,filter.SelectedIndex);RenderStatisticsPage();ReplayStatistics();}};header.Children.Add(filter);panel.Children.Add(header);
        _statisticsHeatmap=new StatisticsHeatmap{Name="StatisticsHeatmap",Snapshot=_statisticsSnapshot,Year=_statisticsYear,Period=_statisticsPeriod,Filter=_statisticsFilter,Text=StatText};panel.Children.Add(_statisticsHeatmap);
        panel.Children.Add(StatLabel(_statisticsPeriod switch {
            StatisticsPeriod.Day=>StatText("每格对应一天；颜色越深，有效学习分钟越多。悬停可查看初学、复习、拼写与新学词数。","One square per day; darker colour means more active minutes. Hover for first learning, review, spelling and new words."),
            StatisticsPeriod.Week=>StatText("完整年度 × 7行网格；每列是一周，亮蓝方格从底部填高，表示该周学习分钟。最高柱为7格。","Full-year seven-row grid. Each column is a week; blue squares fill from the bottom by active minutes, up to seven squares."),
            StatisticsPeriod.Month=>StatText("完整年度 × 7行网格；同一月份的列共享当月分钟与柱高，重复列只为呈现月份宽度，不重复计入总时长。","Full-year seven-row grid. Columns in the same month share that month’s minutes and height; repeated columns do not add study time."),
            _=>StatText("每列是一周，截至该周的年内累计分钟逐周增加；未来列保持空白。","Each column is a week. Year-to-date minutes accumulate through that week; future columns remain empty.")},12));
        var card=StatCard(panel);card.Padding=new Thickness(20);return card;
    }
    private Border BuildStatisticsCurveCard()
    {
        var panel=new StackPanel{Spacing=10};panel.Children.Add(StatLabel(StatText("遗忘曲线","Forgetting curve"),16,"#243F50"));
        panel.Children.Add(StatLabel(StatText("深蓝：当前模型预测 · 浅蓝圆点与线：真实复习观察","Dark blue: current model prediction · Light blue dots and line: observed reviews"),12));
        _statisticsCurve=new StatisticsCurve{Name="StatisticsCurve",Prediction=_statisticsPrediction,Observations=_statisticsSnapshot.Observations,Text=StatText};panel.Children.Add(_statisticsCurve);
        panel.Children.Add(StatLabel(StatText("横轴是距离上次学习多久；纵轴是还能想起单词的可能性。例如80%表示100个相似词预计约80个能想起，是模型估计，不是考试分数。","The horizontal axis shows time since study. The vertical axis is the chance of remembering a word. At 80%, roughly 80 of 100 similar words may be recalled. This is a model estimate, not an exam score."),13,"#3F6579"));
        panel.Children.Add(StatLabel(_statisticsPredictionCount>0?StatText($"预测取 {_statisticsPredictionCount} 张当前词卡的平均值，假设这段时间不再学习。观察按真实复习间隔分组，认识与模糊计为成功回忆，忘记计为失败；仅展示0–30天，每组样本数见悬停。",$"Prediction averages {_statisticsPredictionCount} current word cards, assuming no further study. Observations group actual review gaps; Known and Unsure count as recall, Forgot as failure. The plot shows 0–30 days; hover for group sizes."):
            StatText("当前没有可用模型词卡；完成学习后才会生成预测。","No model cards are available yet. Predictions appear after completed learning."),12));
        if(_statisticsSnapshot.Observations.Count==0)panel.Children.Add(StatLabel(StatText("暂无观察数据：同一词至少需要一次先前学习和一次有效复习，才能知道真实间隔。","No observations yet: a word needs prior learning and a valid later review to establish its actual gap."),12));
        var explain=StatButton(_statisticsExplanation?StatText("收起参数解释","Hide parameter explanation"):StatText("这条线怎么看 · 参数解释","How to read this · Parameters"));
        explain.Name="StatisticsCurveExplanation";explain.Click+=(_,_)=>{_statisticsExplanation=!_statisticsExplanation;RenderStatisticsPage();ReplayStatistics();};panel.Children.Add(explain);
        if(_statisticsExplanation) {
            panel.Children.Add(StatLabel(StatText("稳定性：记忆保持得多久。数值越大，曲线通常下降得越慢；它不是每天固定下降的百分点。","Stability: how long a memory lasts. Higher stability generally means a slower decline, not a fixed daily percentage loss."),13));
            panel.Children.Add(StatLabel(StatText("难度：模型认为这个词对你有多难，范围1–10。它会影响下一次复习后的记忆变化，不等于这个词客观上有多难。","Difficulty: how hard the model considers this word for you, from 1 to 10. It affects memory changes after review; it is not an objective word difficulty score."),13));
            panel.Children.Add(StatLabel(StatText("目标记住率：目前为90%。模型据此安排复习，意思是希望复习时约100个相似词中有90个还想得起，不承诺每次都达到90%。","Target retention is currently 90%. It guides scheduling: ideally around 90 of 100 similar words are still remembered when reviewed. It does not guarantee 90% at every review."),13));
            panel.Children.Add(StatLabel(StatText("两条线可比较趋势，但不能把差异直接解释为进步或模型失准：不同间隔的词与样本量可能不同，少量观察波动很大。","Compare the trends carefully: a gap between the lines alone does not prove progress or poor model accuracy. Words and sample sizes differ across intervals, and small samples fluctuate."),13));
        }
        return StatCard(panel);
    }
    private Border BuildStatisticsWordsCard()
    {
        var panel=new StackPanel{Spacing=12};panel.Children.Add(StatLabel(StatText("易忘词 TOP 50","Most forgotten · Top 50"),16,"#243F50"));
        var switcher=StatButton(_statisticsLifetimeRanking?StatText("切换到近30天遗忘率","Show 30-day forgetting rate"):StatText("切换到累计遗忘次数","Show lifetime forgotten count"));
        switcher.Name="StatisticsRanking";switcher.Click+=(_,_)=>{_statisticsLifetimeRanking=!_statisticsLifetimeRanking;RenderStatisticsPage();};panel.Children.Add(switcher);
        panel.Children.Add(StatLabel(StatText("近30天遗忘率 = 忘记次数 ÷ 有效复习次数；至少3次才进入排名。同率优先显示样本更多的词。","30-day forgetting rate = Forgot responses ÷ valid reviews. Ranking requires at least three reviews; ties favour larger samples."),12));
        var ranked=_statisticsLifetimeRanking?_statisticsSnapshot.LifetimeForgettable.ToArray():_statisticsSnapshot.Forgettable.ToArray();
        if(ranked.Length==0)panel.Children.Add(StatLabel(StatText("暂无足够记录；完成复习后会显示你的真实词汇。","No sufficient records yet. Your actual words appear after reviews."),13));
        foreach(var word in ranked.Take(_statisticsExpandedWords?50:10))panel.Children.Add(StatisticsWordRow(word));
        if(ranked.Length>10){var more=StatButton(_statisticsExpandedWords?StatText("收起列表","Show fewer"):StatText($"展开全部 {ranked.Length} 个词",$"Show all {ranked.Length} words"));more.Name="StatisticsExpandWords";more.Click+=(_,_)=>{_statisticsExpandedWords=!_statisticsExpandedWords;RenderStatisticsPage();};panel.Children.Add(more);}
        if(!_statisticsLifetimeRanking&&_statisticsSnapshot.InsufficientWords.Count>0) {
            panel.Children.Add(StatLabel(StatText($"记录不足（{_statisticsSnapshot.InsufficientWords.Count} 个词）· 少于3次有效复习",$"Insufficient records ({_statisticsSnapshot.InsufficientWords.Count} words) · Fewer than three valid reviews"),14,"#657B88"));
            foreach(var word in _statisticsSnapshot.InsufficientWords.Take(_statisticsExpandedWords?50:5))panel.Children.Add(StatisticsWordRow(word));
        }
        return StatCard(panel);
    }
    private Control StatisticsWordRow(ForgettableWord word)
    {
        var archive=_allWords.FirstOrDefault(w=>!string.IsNullOrWhiteSpace(w.Archive.Uuid)&&WordKeyResolver.FromArchive(w).Key==word.WordKey);
        var parsed=WordKey.Parse(word.WordKey);
        var label=archive?.Word ?? (parsed.Source==WordSource.Ielts?_ieltsCatalog?.AllWords.FirstOrDefault(w=>w.Id==parsed.SourceId)?.Word:null) ?? parsed.SourceId;
        var grid=new Grid { ColumnDefinitions=new ColumnDefinitions("*,Auto"),Margin=new Thickness(0,3) };
        var button=StatButton(label);button.HorizontalAlignment=HorizontalAlignment.Left;button.IsEnabled=archive!=null;
        if(archive!=null)button.Click+=async(_,_)=>await OpenArchivedWordFocusAsync(archive);
        ToolTip.SetTip(button,archive!=null?StatText("打开词汇卡片","Open word card"):StatText("此词来自其他学习来源","This word belongs to another learning source"));grid.Children.Add(button);
        var count=StatLabel(_statisticsLifetimeRanking?StatText($"累计忘记 {word.AllForgotten} 次",$"Forgotten {word.AllForgotten} times total"):
            StatText($"{word.Rate:P0} · 忘记 {word.Forgotten} / 复习 {word.Samples}",$"{word.Rate:P0} · Forgot {word.Forgotten} / {word.Samples} reviews"),12);count.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(count,1);grid.Children.Add(count);return grid;
    }
}
