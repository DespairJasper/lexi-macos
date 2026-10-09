using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Lexi;
public partial class MainWindow
{
    private Dictionary<string,LearningWord> _focusIeltsWords=[];
    private string FocusSurfaceScope()=>"focus:"+(_focusIeltsWords.Count>0?"ielts":"archive-or-form")+":"+_focusRound!.Mode+":"+
        string.Join("|",System.Text.Json.JsonSerializer.Deserialize<StudyRound<string>.PersistedRound>(_focusRound.CaptureJson(w=>w))!.States.Select(w=>w.WordId).OrderBy(w=>w,StringComparer.Ordinal));
    private string FocusDisplayWord(string identity)=>_focusIeltsWords.TryGetValue(identity,out var word)?word.Word:identity;
    private void ShowIeltsFocusChooser()
    {
        _ieltsCatalog??=IeltsCatalog.Load();
        var selected=_ieltsCatalog.AllWords.Where(w=>_ieltsSelected.Contains(w.Id)).ToList();
        var section=_ieltsCatalog.Sections.FirstOrDefault(s=>s.Id==_ieltsProgress.SelectedSection);
        var words=selected.Count>0?selected:section?.Entries??[];
        var title=selected.Count>0?UiText.Bilingual("学习已选教材词","Study selected IELTS words"):section?.Title??UiText.Bilingual("当前章节","Current chapter");
        var body=new StackPanel {Spacing=12,MaxWidth=380};
        body.Children.Add(LearningText(title,20));
        body.Children.Add(LearningText(UiText.Bilingual($"本轮 {words.Count} 词，保留教材独立记忆进度。",$"{words.Count} words. IELTS memory progress stays separate."),13));
        var dialog=new Flyout {Content=body};
        TrackStudyFlyout(dialog);
        var start=LearningButton(UiText.Bilingual("开始专注","Start focus"),()=>{dialog.Hide();EnterIeltsFocus(words);},true);
        start.IsEnabled=words.Count>0;body.Children.Add(start);
        body.Children.Add(LearningButton("取消",()=>dialog.Hide())); dialog.ShowAt(GlobalFocusButton);
    }
    private void EnterIeltsFocus(List<LearningWord> words)
    {
        if(_focusActive||words.Count==0||_restoring||!_databaseAvailable)return;
        _focusIeltsWords=words.DistinctBy(w=>w.Id).ToDictionary(w=>w.Id);
        _focusRound=new StudyRound<string>();_focusRound.Reset(_focusIeltsWords.Keys,StudyMode.FirstLearn,shuffle:false);
        _focusPreviousPage=_currentPage;_focusActive=true;_focusAnswerVisible=false;_focusRated=false;_focusUndo=null;_focusUndoArchiveId=null;_focusUndoRevision=null;
        BeginFocusMemory(_focusRound.Mode,_focusRound.Total);
        if(_ieltsPage!=null)_ieltsPage.IsVisible=false;
        _focusHost!.IsVisible=true;SetGlobalFocusChrome(true);RenderFocus();_focusHost.Focus();
    }
}
