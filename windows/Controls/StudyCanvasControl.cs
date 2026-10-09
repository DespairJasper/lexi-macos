using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lexi.Controls;

/// <summary>统一学习内容模型：单词、音标、释义、细节与示例，供 StudyCanvasControl 渲染。</summary>
public sealed class StudyCanvasModel
{
    public string Word { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string Details { get; set; } = "";
    public string Example { get; set; } = "";
    public string Placeholder { get; set; } = "";
    public bool MeaningVisible { get; set; } = true;
    public Action? Speak { get; set; }
}

/// <summary>
/// 1.2.3 统一背词内容组件：focus / 计划 / 今日复习共享。
/// 最大阅读宽 680，水平居中；正文全部使用 SelectableTextBlock 支持选中复制。
/// 词头 40 左右，音标旁紧凑放置 InlineAudioButton 小扬声器。
/// 揭晓释义后形成限宽、细分隔和留白的阅读边界，长正文左对齐但整体阅读区居中。
/// 不使用白色卡片，不让例句铺满窗口，共用纯色背景与当前主题字色资源。
/// </summary>
public sealed class StudyCanvasControl : UserControl
{
    public Action? Speak { get; set; }

    public StudyCanvasControl() => Render(new StudyCanvasModel());

    public void Render(StudyCanvasModel model)
    {
        var content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 16)
        };

        var word = new SelectableTextBlock
        {
            Text = model.Word,
            FontSize = 40,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        word.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        content.Children.Add(word);

        // 音标与小扬声器
        if (!string.IsNullOrWhiteSpace(model.Phonetic) || !string.IsNullOrWhiteSpace(model.Word))
        {
            var phoneticRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8
            };

            if (!string.IsNullOrWhiteSpace(model.Phonetic))
            {
                var phonetic = new SelectableTextBlock
                {
                    Text = model.Phonetic,
                    FontSize = 17,
                    Opacity = 0.82,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                phonetic.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
                phoneticRow.Children.Add(phonetic);
            }

            var audioBtn = new InlineAudioButton(UiText.Bilingual("朗读单词","Listen to word"), () => (model.Speak ?? Speak)?.Invoke());
            phoneticRow.Children.Add(audioBtn);

            content.Children.Add(phoneticRow);
        }

        if (model.MeaningVisible)
        {
            bool hasRevealedContent = !string.IsNullOrWhiteSpace(model.Meaning)
                || !string.IsNullOrWhiteSpace(model.Details)
                || !string.IsNullOrWhiteSpace(model.Example);

            if (hasRevealedContent)
            {
                // 细分隔线与留白边界
                var divider = new Border
                {
                    Height = 1,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Opacity = 0.45,
                    Margin = new Thickness(0, 8, 0, 10)
                };
                divider.Bind(Border.BackgroundProperty, this.GetResourceObservable("LineBrush"));
                content.Children.Add(divider);
            }

            if (!string.IsNullOrWhiteSpace(model.Meaning))
            {
                var meaning = new SelectableTextBlock
                {
                    Text = model.Meaning,
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    LineHeight = 28,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 2, 0, 4)
                };
                meaning.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
                content.Children.Add(meaning);
            }

            if (!string.IsNullOrWhiteSpace(model.Details))
            {
                var details = new SelectableTextBlock
                {
                    Text = model.Details,
                    FontSize = 16,
                    LineHeight = 26,
                    Opacity = 0.9,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                details.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
                content.Children.Add(details);
            }

            if (!string.IsNullOrWhiteSpace(model.Example))
            {
                var example = new SelectableTextBlock
                {
                    Text = model.Example,
                    FontSize = 16,
                    LineHeight = 26,
                    Opacity = 0.9,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                example.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
                content.Children.Add(example);
            }
        }
        else if (!string.IsNullOrWhiteSpace(model.Placeholder))
        {
            var placeholder = new SelectableTextBlock
            {
                Text = model.Placeholder,
                FontSize = 15,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 12, 0, 0)
            };
            placeholder.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
            content.Children.Add(placeholder);
        }

        Content = content;
    }
}
