using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lexi.Controls;

namespace Lexi;

public partial class MainWindow
{
    private Border? _globalStudySurface;
    private StudyCanvasControl? _globalStudyCanvas;
    private TextBlock? _globalStudyProgress;
    private StackPanel? _globalStudyToolbar;
    private WrapPanel? _globalStudyActions;
    private Border? _globalStudyContent;
    private bool _globalStudyRefreshQueued;

    private void ConfigureGlobalStudySurface()
    {
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(24,16,24,20), RowSpacing = 16 };
        _globalStudyToolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _globalStudyProgress = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        _globalStudyProgress.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 20 };
        header.Children.Add(_globalStudyToolbar); Grid.SetColumn(_globalStudyProgress,1); header.Children.Add(_globalStudyProgress); layout.Children.Add(header);
        _globalStudyCanvas = new StudyCanvasControl();
        _globalStudyActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin=new Thickness(0,24,0,0) };
        var reading=new Grid {RowDefinitions=new RowDefinitions("*,Auto"),VerticalAlignment=VerticalAlignment.Center};
        var textScroll=new ScrollViewer {Content=_globalStudyCanvas,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};
        reading.Children.Add(textScroll);Grid.SetRow(_globalStudyActions,1);reading.Children.Add(_globalStudyActions);
        _globalStudyContent = new Border { Child = reading, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(_globalStudyContent,1);layout.Children.Add(_globalStudyContent);
        _globalStudySurface = new Border { Name = "UnifiedFocusSurface", Child = layout, IsVisible = false };
        _globalStudySurface.Bind(Border.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        Grid.SetRow(_globalStudySurface,1); _globalStudySurface.SetValue(Panel.ZIndexProperty,150);
        ((Grid)((Grid)RootWindowBorder.Child!).Children[0]).Children.Add(_globalStudySurface);
        _globalStudySurface.SizeChanged += (_,_) => ResizeGlobalStudyContent();
        _studyWorkspaceHost.Children.CollectionChanged += (_,_) => QueueGlobalStudyRefresh();
        ReviewWordText.PropertyChanged += (_,e) => { if(e.Property==TextBlock.TextProperty)QueueGlobalStudyRefresh(); };
        ReviewAnswer.PropertyChanged += (_,e) => { if(e.Property==IsVisibleProperty)QueueGlobalStudyRefresh(); };
        ReviewRatingBar.PropertyChanged += (_,e) => { if(e.Property==IsEnabledProperty)QueueGlobalStudyRefresh(); };
        if(_focusCanvas!=null) _focusCanvas.PropertyChanged += (_,e) => { if(e.Property==ContentControl.ContentProperty)QueueGlobalStudyRefresh(); };
    }

    private void QueueGlobalStudyRefresh()
    {
        if(_globalStudyRefreshQueued)return;
        _globalStudyRefreshQueued=true;
        Dispatcher.UIThread.Post(()=>{ _globalStudyRefreshQueued=false; RefreshGlobalStudySurface(); });
    }

    private void ResizeGlobalStudyContent()
    {
        if(_globalStudySurface==null || _globalStudyContent==null || _globalStudyCanvas==null)return;
        _globalStudyContent.Width=_globalStudyContent.MaxWidth=Math.Min(680,Math.Max(260,_globalStudySurface.Bounds.Width-64));
        _globalStudyContent.MaxHeight=Math.Max(180,_globalStudySurface.Bounds.Height-100);
        var word=_globalStudyCanvas.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();
        if(word!=null)word.FontSize=Math.Clamp(48-word.Text?.Length*.4??40,36,44);
    }

    private void RefreshGlobalStudySurface()
    {
        if(_globalStudySurface==null || _globalStudyProgress==null || _globalStudyActions==null || _globalStudyToolbar==null || _globalStudyCanvas==null)return;
        var active = !_settingsDrawerOpen && _typingHost?.IsVisible!=true
            && (_focusActive || _globalFocusActive && (_currentPage=="review" || _currentPage=="learning" && _studyWorkspaceHost.IsVisible));
        _globalStudySurface.IsVisible=active;
        if(!active)return;
        _globalStudyActions!.Children.Clear();_globalStudyToolbar!.Children.Clear();
        Button Button(string label, Action action)
        {
            var button=new Button { Content=label, Classes={"secondary"}, Padding=new Thickness(14,9), Margin=new Thickness(4), MinHeight=38 };
            button.Click+=(_,_)=>{action();QueueGlobalStudyRefresh();}; return button;
        }
        _globalStudyToolbar.Children.Add(Button(UiText.Bilingual("返回","Back"),ToggleGlobalFocus));
        _globalStudyToolbar.Children.Add(Button(UiText.Bilingual("词源 ▾","Source ▾"),ShowFocusSourceChooser));
        var model=new StudyCanvasModel { MeaningVisible=false };
        var phase=DetermineCurrentStudyPhase(); var hasWord=DetermineHasCurrentWord();
        if(_focusActive && _focusRound!=null)
        {
            var key=_focusRated ? _focusRatedWord??"" : _focusRound.HasCurrent?_focusRound.Current:"";
            var source=_focusIeltsWords.GetValueOrDefault(key);
            var archive=source==null?_allWords.FirstOrDefault(w=>w.Word.Equals(key,StringComparison.OrdinalIgnoreCase)):null;
            model.Word=FocusDisplayWord(key); model.Phonetic=source?.Phonetic??archive?.Phonetic??ResultPhoneticText.Text??"";
            model.Meaning=source?.Meaning??archive?.Translation??_focusOriginalTranslation;
            model.Details=archive?.Definition??(source==null?ResultDefinitionText.Text:source.Extra)??"";
            model.Example=source?.Example??string.Join("\n",archive?.AiExamples.Select(e=>e.English+"\n"+e.Chinese)??[]);
            model.MeaningVisible=_focusAnswerVisible;
            _globalStudyProgress.Text=UiText.Bilingual($"专注学习 · {_focusRound.Completed} / {_focusRound.Total}",$"Focus · {_focusRound.Completed} / {_focusRound.Total}");
        }
        else if(_currentPage=="learning" && _dailyLearningSession!=null && _activeLearningPlan!=null)
        {
            var round=_dailyLearningSession.Round;
            var word=round.HasCurrent?_activeLearningPlan.Words.Single(w=>w.Id==round.Current):null;
            model.Word=word?.Word??"";model.Phonetic=word?.Phonetic??"";model.Meaning=word?.Meaning??"";
            model.Details=word?.Definition??"";model.Example=word?.Example??"";
            model.MeaningVisible=round.CurrentStep==StudyStep.Learn||_planAnswerVisible;
            _globalStudyProgress.Text=$"{_activeLearningPlan.Name} · {round.Completed} / {round.Total}";
        }
        else
        {
            model.Word=_reviewWord?.Word??"";model.Phonetic=_reviewWord?.Phonetic??"";
            model.Meaning=_reviewWord?.Translation??"";model.Details=_reviewWord?.Definition??"";
            model.Example=string.Join("\n",_reviewWord?.AiExamples.Select(e=>e.English+"\n"+e.Chinese)??[]);
            model.MeaningVisible=_reviewRevealed;
            _globalStudyProgress.Text=UiText.Bilingual($"今日重逢 · {_reviewRound.Completed} / {_reviewRound.Total}",$"Today's review · {_reviewRound.Completed} / {_reviewRound.Total}");
        }
        if(!hasWord){model.Word=UiText.Bilingual("本轮完成","Session complete");model.MeaningVisible=false;}
        model.Placeholder=hasWord&&!model.MeaningVisible?UiText.Bilingual("先回忆，再揭晓释义。","Recall first, then reveal the meaning."):"";
        _globalStudyCanvas!.Speak=ExecuteSpeakCurrent;_globalStudyCanvas.Render(model);ResizeGlobalStudyContent();
        if(hasWord)
        {
            _globalStudyCanvas.Speak=ExecuteSpeakCurrent;
            if(phase==StudyPhase.Learn)_globalStudyActions.Children.Add(Button(UiText.Bilingual("开始回忆","Start recall"),ExecuteAdvanceLearn));
            else if(phase==StudyPhase.RecallHidden)_globalStudyActions.Children.Add(Button(UiText.Bilingual("揭晓释义","Reveal meaning"),ExecuteRevealHidden));
            else if(phase==StudyPhase.RecallRated)_globalStudyActions.Children.Add(Button(UiText.Bilingual("下一词","Next word"),ExecuteNextWord));
            else
                foreach(var (label,rating,shortcut) in new[]{(UiText.Bilingual("忘记","Forgot"),StudyRating.Forgot,ShortcutAction.Forgot),(UiText.Bilingual("模糊","Unsure"),StudyRating.Unsure,ShortcutAction.Unsure),(UiText.Bilingual("认识","Known"),StudyRating.Known,ShortcutAction.Known)})
                {
                    var button=Button(label+" ("+ShortcutHint(shortcut)+")",async()=>{await ExecuteRatingAsync(rating);QueueGlobalStudyRefresh();});
                    button.IsEnabled=!_reviewBusy;_globalStudyActions.Children.Add(button);
                }
        }
        if(DetermineCanUndo())_globalStudyActions.Children.Add(Button(UiText.Bilingual("撤销","Undo"),ExecuteUndoCurrent));
    }
}
