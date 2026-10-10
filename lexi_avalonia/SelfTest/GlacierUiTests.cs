using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;

namespace Lexi;

public static class GlacierUiTests
{
    public static async Task RunAsync(MainWindow window)
    {
        var folder = Environment.GetEnvironmentVariable("LEXI_DATA_DIR") ?? throw new InvalidOperationException("Isolated data required");
        var report = new List<string>(); var exit = 0;
        void Check(bool valid, string label) { report.Add((valid ? "PASS " : "FAIL ") + label); File.WriteAllLines(Path.Combine(folder,"glacier-result.txt"),report); if(!valid)throw new Exception(label); }
        T C<T>(string name) where T:Control => window.FindControl<T>(name) ?? window.GetLogicalDescendants().OfType<T>().First(c=>c.Name==name);
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
        void Set(string name,object value)=>typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,value);
        object? Call(string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,args);
        void Click(string name)=>C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Snapshot(string name) { await Task.Delay(50); using var b=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));b.Render(window);b.Save(Path.Combine(folder,name+".png")); }
        try
        {
            window.Width=1280;window.Height=940;
            Click("NavQuotes");
            window.SetUiLanguage("en");
            Check(C<Button>("NavQuotes").Content?.ToString()=="Quotes" && C<TextBlock>("QuotesTitle").Text=="Quotes", "existing quote navigation and title switch immediately to English");
            Check(C<TextBox>("QuoteSearchInput").Watermark=="Search sentences, translations, sources or notes" && C<Button>("NewQuoteBtn").Content?.ToString()=="Add quote" && C<Button>("ExportQuotesBtn").Content?.ToString()=="Export all quotes" && C<Button>("QuotesPreviousBtn").Content?.ToString()=="Previous" && C<Button>("QuotesNextBtn").Content?.ToString()=="Next", "quote search, add, export and pagination switch to English");
            Check(C<TextBlock>("QuotesSummary").Text!.StartsWith("No quotes yet."), "quote empty state is English");
            await Snapshot("v321-quotes-en-empty");
            var quoteArchive=(IQuoteArchive)Field<IVocabularyArchive>("_vocabService");
            var quote=quoteArchive.SaveQuote(null,"原句保持中文", "译文保持原样", "来源保持原样", "备注保持原样");
            C<TextBox>("QuoteSearchInput").Text="原句";
            window.SetUiLanguage("zh-CN"); window.SetUiLanguage("en");
            var quoteList=C<StackPanel>("QuotesList");
            Check(C<TextBox>("QuoteSearchInput").Text=="原句" && quoteArchive.GetQuotes("原句",1,0).Single().Original==quote.Original && quoteList.GetLogicalDescendants().OfType<TextBlock>().Any(t=>t.Text=="备注保持原样"), "language toggles preserve quote contents and search text");
            Check(quoteList.GetLogicalDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Edit") && quoteList.GetLogicalDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Delete…"), "quote row edit and delete actions are English");
            quoteList.GetLogicalDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Delete…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(quoteList.GetLogicalDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Confirm delete") && quoteList.GetLogicalDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Cancel"), "quote deletion confirmation is English");
            quoteList.GetLogicalDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Snapshot("v321-quotes-en-filled");
            window.SetUiLanguage("zh-CN");
            Check(C<Button>("NavQuotes").Content?.ToString()=="金句本" && C<TextBlock>("QuotesTitle").Text=="金句本" && C<Button>("QuotesPreviousBtn").Content?.ToString()=="上一页", "quote labels return to Chinese");
            Click("NavStatistics");
            Check(Field<Grid>("_statisticsPage").IsVisible && !C<Grid>("LookupPageHost").IsVisible && !C<Grid>("PageStudyPlan").IsVisible,"statistics opens as an exclusive native page");
            Check(Field<ScrollViewer>("_statisticsScroll").VerticalScrollBarVisibility==ScrollBarVisibility.Hidden, "statistics hides vertical scrollbar");
            var heat=Field<StatisticsHeatmap>("_statisticsHeatmap");var curve=Field<StatisticsCurve>("_statisticsCurve");
            double Progress(object chart)=>(double)chart.GetType().GetProperty("RevealProgress")!.GetValue(chart)!;
            Check(Progress(heat)<.2 && Progress(curve)<.2,"entry starts heatmap and curve left-to-right reveals");
            await Task.Delay(330);var middle=Progress(heat);Check(middle>.1&&middle<1,"heatmap progresses smoothly between endpoints");
            await Task.Delay(650);Check(Progress(heat)==1&&Progress(curve)==1,"chart reveals reach their final state");
            foreach(var period in new[]{StatisticsPeriod.Day,StatisticsPeriod.Week,StatisticsPeriod.Month,StatisticsPeriod.Cumulative})
            {
                Click("StatisticsPeriod"+period);heat=Field<StatisticsHeatmap>("_statisticsHeatmap");
                Check(heat.Period==period && Progress(heat)<.2,"period "+period+" replays reveal");
                await Task.Delay(900);Check(Progress(heat)==1,"period "+period+" completes reveal");
            }
            Click("NavLookup");Click("NavStatistics");Check(Progress(Field<StatisticsCurve>("_statisticsCurve"))<.2,"statistics re-entry redraws curve");
            await Task.Delay(950);
            window.SetUiLanguage("en");
            Check(Field<Grid>("_statisticsPage").GetLogicalDescendants().OfType<TextBlock>().Any(t=>t.Text=="Forgetting curve"),"statistics explanations switch to English");
            await Task.Delay(950);await Snapshot("glacier-en-statistics-empty");
            window.SetUiLanguage("zh-CN");
            var archive=Field<IVocabularyArchive>("_vocabService");archive.AddWord("glacierfixture","","冰川测试","Fixture definition");Call("RefreshWords");
            var word=archive.GetAllWords().Single(w=>w.Word=="glacierfixture");
            var plan=DailyStudyPlanRules.Create("冰川蓝测试计划",DailyStudyPlanSource.Archive,"我的词汇档案",[new(){Id=word.Id.ToString(),Word=word.Word,Meaning=word.Translation}],1,false,42);
            plan.CurrentBatchWordIds=[word.Id.ToString()];plan.CurrentBatchDate=DateOnly.FromDateTime(DateTime.Now);plan.CurrentBatchRandomOrder=false;plan.CompletedWordIds.Add(word.Id.ToString());plan.LastBatchCompletedDate=plan.CurrentBatchDate;plan.LearningDates.Add(plan.CurrentBatchDate.Value);plan.Status=DailyStudyPlanStatus.Completed;
            Field<List<DailyStudyPlan>>("_studyPlans").Add(plan);Call("SaveStudyPlans");Call("RenderStudyPlanLists");Call("OpenDailyPlanDetail",plan);
            Check(!C<Button>("DailyPlanFirstBtn").IsEnabled&&C<Button>("DailyPlanReviewBtn").IsEnabled&&C<Button>("DailyPlanSpellBtn").IsEnabled,"only completed first-learning entry disables");
            var dailyScroll=Field<Grid>("_dailyPlanPage").GetLogicalDescendants().OfType<ScrollViewer>().First();
            Check(dailyScroll.VerticalScrollBarVisibility==ScrollBarVisibility.Hidden, "daily plan hides vertical scrollbar");
            await Task.Delay(350);await Snapshot("glacier-zh-daily-plan");
            Check(((ISolidColorBrush)C<ProgressBar>("DailyPlanFirstProgress").Foreground!).Color==((ISolidColorBrush)window.FindResource(window.ActualThemeVariant,"PrimaryGreen")!).Color,"progress bars use glacier blue instead of system accent");
            var completed=plan.CompletedWordIds.Count;
            for(var i=0;i<2;i++)
            {
                Click("DailyPlanReviewBtn");Click("PlanSpellingStartBtn");
                Check(Field<StudyRound<string>>("_focusRound").Mode==StudyMode.Review&&Field<StudyRound<string>>("_focusRound").CurrentStep==StudyStep.Recall,"repeat review enters recall directly #"+i);
                Check(!C<TextBlock>("ResultTranslationText").IsEffectivelyVisible,"review does not expose the first-learning answer");
                Call("ExitWordFocus");Call("ShowPage","dailyplan");
                Check(plan.CompletedWordIds.Count==completed,"repeat review preserves first-learning progress #"+i);
                Click("DailyPlanSpellBtn");Click("PlanSpellingStartBtn");Check(Field<bool>("_typingPlaying"),"spelling starts repeatedly #"+i);Call("ShowPage","dailyplan");
            }
            window.SetUiLanguage("en");Check(C<Button>("DailyPlanReviewBtn").Content?.ToString()=="Review words","daily plan switches to English synchronously");await Snapshot("glacier-en-daily-plan");
            window.SetUiLanguage("zh-CN");Click("DailyPlanBackBtn");await Task.Delay(350);await Snapshot("glacier-zh-plans");
            Check(C<Grid>("PageStudyPlan").GetLogicalDescendants().OfType<ScrollViewer>().First().VerticalScrollBarVisibility==ScrollBarVisibility.Hidden, "study plans hide vertical scrollbar");
            // Deterministic visual fixtures exist only inside this isolated test process.
            Click("NavStatistics");var today=DateOnly.FromDateTime(DateTime.Today);var snapshot=new StudyStatisticsSnapshot{Today=today,LearnedWords=3840,LongestStreak=32,CurrentStreak=12};
            var rng=new Random(42);for(var day=new DateOnly(today.Year,1,1);day<=today;day=day.AddDays(1))if(rng.Next(4)>0)snapshot.Days[day]=new(){FirstLearnSeconds=rng.Next(100,1200),ReviewSeconds=rng.Next(60,1800),SpellingSeconds=rng.Next(0,800),Words=rng.Next(5,35),NewWords=rng.Next(0,20)};
            Set("_statisticsSnapshot",snapshot);Set("_statisticsPrediction",Enumerable.Range(0,121).Select(i=>(i/4d,Math.Exp(-i/120d))).ToArray());Set("_statisticsPredictionCount",128);Call("RenderStatisticsPage");Call("ReplayStatistics");await Task.Delay(950);await Snapshot("glacier-zh-statistics-fixture");
            foreach(var period in new[]{StatisticsPeriod.Week,StatisticsPeriod.Month,StatisticsPeriod.Cumulative}){Click("StatisticsPeriod"+period);await Task.Delay(950);await Snapshot("glacier-zh-heatmap-"+period);}
            C<CheckBox>("ReduceMotionBox").IsChecked=true;Click("StatisticsPeriodDay");Check(Progress(Field<StatisticsHeatmap>("_statisticsHeatmap"))==1&&Progress(Field<StatisticsCurve>("_statisticsCurve"))==1,"reduced motion displays final charts immediately");
            foreach(var width in new[]{840,1000,1280}) {
                window.Width=width;window.Height=780;await Task.Delay(250);
                var metrics=C<UniformGrid>("StatisticsMetrics");var cards=metrics.Children.OfType<Border>().ToArray();
                Check(metrics.Columns==4&&cards.Length==4&&cards.All(c=>Math.Abs(c.Bounds.Y-cards[0].Bounds.Y)<1),"four statistics cards stay on one row at width "+width);
                var scroll=Field<ScrollViewer>("_statisticsScroll");
                scroll.Offset=new Vector(0,80); await Task.Delay(30);
                Check(scroll.Extent.Height>scroll.Viewport.Height && scroll.Offset.Y>0, "statistics still scrolls with hidden scrollbar at width "+width);
                scroll.Offset=Vector.Zero;
                await Snapshot("glacier-statistics-width-"+width);
            }
            Click("NavLookup");await Task.Delay(350);
            Check(!window.GetLogicalDescendants().OfType<TextBlock>().Any(t=>t.Text=="lexi"),"top-left brand text is removed");await Snapshot("glacier-home");
            report.Add("PASS glacier acceptance completed");
        }
        catch(Exception ex){exit=1;report.Add("FAIL "+ex);}
        finally{File.WriteAllLines(Path.Combine(folder,"glacier-result.txt"),report);window.PrepareForApplicationShutdown();(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);}
    }
}
