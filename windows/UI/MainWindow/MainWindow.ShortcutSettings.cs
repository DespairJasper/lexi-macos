using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Lexi.Features.Settings;

namespace Lexi;
public partial class MainWindow
{
    private readonly Dictionary<ShortcutAction,TextBox> _shortcutEditors=[];
    private CheckBox? _numberShortcutOption;
    private TextBlock? _shortcutEditStatus;

    private void AddStudyShortcutSettings(StackPanel target)
    {
        ConfigureStudyShortcuts();
        var body=new StackPanel {Spacing=12};
        body.Children.Add(LearningText(UiText.Bilingual("学习快捷键","Study shortcuts"),18));
        body.Children.Add(LearningText(UiText.Bilingual("三种学习共用同一套按键。多个按键用逗号分隔，例如 Space, Enter。输入文字时不会触发学习评分。","All study views share these keys. Separate aliases with commas, e.g. Space, Enter. Typing never rates a word."),12));
        var labels=new Dictionary<ShortcutAction,(string,string)>
        {
            [ShortcutAction.Primary]=("揭晓 / 继续","Reveal / continue"),[ShortcutAction.Forgot]=("忘记","Forgot"),
            [ShortcutAction.Unsure]=("模糊","Unsure"),[ShortcutAction.Known]=("认识","Known"),[ShortcutAction.Speak]=("朗读","Speak"),
            [ShortcutAction.Undo]=("撤销","Undo"),[ShortcutAction.Favorite]=("收藏","Save word"),[ShortcutAction.ToggleGlobalFocus]=("专注模式","Focus mode")
        };
        foreach(var (action,label) in labels)
        {
            var row=new Grid {ColumnDefinitions=new ColumnDefinitions("120,*")};
            row.Children.Add(new TextBlock {Text=UiText.Bilingual(label.Item1,label.Item2),VerticalAlignment=VerticalAlignment.Center});
            var editor=new TextBox {Name="StudyShortcut"+action,Text=string.Join(", ",ShortcutConfigManager.GetGestures(action)),Watermark="Ctrl+K"};
            _shortcutEditors[action]=editor; Grid.SetColumn(editor,1); row.Children.Add(editor); body.Children.Add(row);
        }
        _numberShortcutOption=new CheckBox {Content=UiText.Bilingual("启用 1 / 2 / 3 评分备用键","Enable 1 / 2 / 3 rating aliases"),IsChecked=ShortcutConfigManager.CurrentConfig.EnableNumberRatings};body.Children.Add(_numberShortcutOption);
        body.Children.Add(LearningText(UiText.Bilingual("Esc 返回，Tab 切换控件；Alt+Space、Alt+F4 等 Windows 系统按键保留。","Esc returns; Tab navigates controls. Windows shortcuts such as Alt+Space and Alt+F4 stay available."),12));
        _shortcutEditStatus=new TextBlock {Name="StudyShortcutStatus",TextWrapping=Avalonia.Media.TextWrapping.Wrap};body.Children.Add(_shortcutEditStatus);
        body.Children.Add(LearningButton(UiText.Bilingual("保存学习快捷键","Save study shortcuts"),SaveStudyShortcutEditors,true));
        body.Children.Add(LearningButton(UiText.Bilingual("恢复默认快捷键","Restore default shortcuts"),()=>
        {
            try { var result=SaveShortcutConfiguration(ShortcutConfiguration.CreateDefault()); if(!result.IsValid)throw new InvalidOperationException(string.Join("\n",result.Errors)); RefreshShortcutEditors(); _shortcutEditStatus.Text=UiText.Bilingual("默认快捷键已保存。","Default shortcuts saved."); }
            catch(Exception ex) {_shortcutEditStatus.Text=ex.Message;}
        }));
        var card=new Border {Classes={"card"},Child=body};target.Children.Insert(0,card);_settingsRoots.Add(card);
    }
    private void SaveStudyShortcutEditors()
    {
        var config=ShortcutConfigManager.CurrentConfig.Clone();
        foreach(var (action,input) in _shortcutEditors)config.Bindings[action]=(input.Text??"").Split([',','，'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();
        config.EnableNumberRatings=_numberShortcutOption!.IsChecked==true;
        try
        {
            var result=SaveShortcutConfiguration(config);
            _shortcutEditStatus!.Text=result.IsValid?UiText.Bilingual("学习快捷键已保存。","Study shortcuts saved."):string.Join("\n",result.Errors);
        }
        catch(Exception ex){_shortcutEditStatus!.Text=UiText.Bilingual("保存失败：","Could not save: ")+ex.Message;}
    }
    private void RefreshShortcutEditors()
    {
        foreach(var (action,input) in _shortcutEditors)input.Text=string.Join(", ",ShortcutConfigManager.GetGestures(action));
        if(_numberShortcutOption!=null)_numberShortcutOption.IsChecked=ShortcutConfigManager.CurrentConfig.EnableNumberRatings;
    }
    private string ShortcutHint(ShortcutAction action)
    {
        var gestures=ShortcutConfigManager.GetGestures(action).Select(g=>g.Replace("Left","←").Replace("Right","→").Replace("Up","↑").Replace("Down","↓"));
        var text=string.Join(" / ",gestures);
        if(ShortcutConfigManager.CurrentConfig.EnableNumberRatings && action is ShortcutAction.Forgot or ShortcutAction.Unsure or ShortcutAction.Known)
            text+=" / "+(action==ShortcutAction.Forgot?"1":action==ShortcutAction.Unsure?"2":"3");
        return text;
    }
    private void RefreshStudyShortcutHints()
    {
        if(_shortcutConfigManager==null)return;
        ToolTip.SetTip(ReviewUnfamiliarBtn,UiText.Text("忘记")+" · "+ShortcutHint(ShortcutAction.Forgot));
        ToolTip.SetTip(ReviewUnsureBtn,UiText.Text("模糊")+" · "+ShortcutHint(ShortcutAction.Unsure));
        ToolTip.SetTip(ReviewRememberBtn,UiText.Text("认识")+" · "+ShortcutHint(ShortcutAction.Known));
        ToolTip.SetTip(ReviewRevealBtn,UiText.Text("揭晓释义")+" · "+ShortcutHint(ShortcutAction.Primary));
        ToolTip.SetTip(ReviewRoundUndoBtn,UiText.Text("撤销")+" · "+ShortcutHint(ShortcutAction.Undo));
        ToolTip.SetTip(GlobalFocusButton,UiText.Text("专注")+" · "+ShortcutHint(ShortcutAction.ToggleGlobalFocus));
        if(_focusActive)RenderFocus();
        if(_currentPage=="learning"&&_studyWorkspaceHost.IsVisible&&_dailyLearningSession!=null)RenderDailyLearning();
    }
}
