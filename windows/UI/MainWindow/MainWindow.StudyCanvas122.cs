using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Lexi.Controls;

namespace Lexi;

/// <summary>
/// 1.2.2 统一背词画布主题接入：专注 / 计划 / 今日复习的背景与字色
/// 全部跟随当前 Avalonia 主题资源（PaperBrush / InkBrush / MutedBrush / LineBrush / PrimaryGreen）。
/// 不再维护独立的暖纸 / 雾灰 / 夜墨配色与独立偏好文件；浅 / 深主题切换由
/// Avalonia 资源绑定自动即时刷新，不重开 session、不重置评分 / 揭晓。
/// </summary>
public partial class MainWindow
{
    private bool _studySurfaceThemed;
    private bool _reviewSurfaceThemed;

    private StudyCanvasControl CreateStudyCanvas() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };

    /// <summary>
    /// 供主控在 ToggleGlobalFocus 中调用的统一刷新入口：读取 _globalFocusActive / _focusActive，
    /// 确保表面主题资源已接入，并按当前激活页面重绘内容（不重开 session、不重置评分 / 揭晓）。
    /// </summary>
    internal void RefreshUnifiedStudyCanvas()
    {
        EnsureStudySurfaceTheming();
        if (_focusActive) { RenderFocus(); return; }
        if (_studyWorkspaceHost is { IsVisible: true }) { RenderDailyLearning(); return; }
        if (_globalFocusActive || _currentPage == "review") ApplyReviewCanvasTheme();
    }

    /// <summary>一次性把专注 / 计划 / 今日复习表面背景与字色接到当前主题资源上。</summary>
    private void EnsureStudySurfaceTheming()
    {
        if (_studySurfaceThemed) return;
        _studySurfaceThemed = true;
        if (_focusHost != null) _focusHost.Bind(Border.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        if (_studyWorkspaceHost != null) _studyWorkspaceHost.Bind(Panel.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        ApplyReviewCanvasTheme();
    }

    /// <summary>今日复习复用同一套主题资源：纯色背景、舒适字色，命名控件保持不动。</summary>
    private void ApplyReviewCanvasTheme()
    {
        if (_reviewSurfaceThemed) return;
        _reviewSurfaceThemed = true;
        PageReview.Bind(Panel.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        ReviewCard.Bind(Border.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        ReviewCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        ReviewCard.BoxShadow = default;
        ReviewBackOne.Bind(Border.BackgroundProperty, this.GetResourceObservable("LineBrush"));
        ReviewBackTwo.Bind(Border.BackgroundProperty, this.GetResourceObservable("TintBrush"));
        ReviewBackOne.Opacity=0.65; ReviewBackTwo.Opacity=0.6;
        ReviewBackOne.Margin=new Thickness(4,4,-4,-4);ReviewBackTwo.Margin=new Thickness(8,8,-8,-8);
        ReviewWordText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        ReviewPhoneticText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        ReviewMeaningText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        ReviewDefinitionText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
    }
}
