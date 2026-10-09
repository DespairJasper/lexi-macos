using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Interactivity;

namespace Lexi;

public partial class MainWindow
{
    /// <summary>
    /// 通用例句小朗读/收藏操作按钮栏（供查词/档案等模块调用）
    /// </summary>
    public StackPanel CreateExampleActions(string english, string? translation = null, string source = "例句收藏")
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        var speakBtn = new Lexi.Controls.InlineAudioButton(UiText.Bilingual("朗读例句","Listen to example"),()=>{}) {Tag=english};
        ToolTip.SetTip(speakBtn, UiText.Bilingual("朗读例句", "Listen to example"));
        Avalonia.Automation.AutomationProperties.SetName(speakBtn, UiText.Bilingual("朗读例句", "Listen to example"));
        speakBtn.Click += async (_, _) => await SpeakWordAsync(english);

        var quoteBtn = new Button
        {
            Content = "＋",
            Classes = { "row-btn", "ghost" },
            Padding = new Thickness(6, 2)
        };
        ToolTip.SetTip(quoteBtn, UiText.Bilingual("收藏到金句本", "Save to quotes"));
        Avalonia.Automation.AutomationProperties.SetName(quoteBtn, UiText.Bilingual("收藏例句到金句本", "Save example to quotes"));
        quoteBtn.Click += (_, _) => SaveExampleToQuotes(english, translation, source);

        actions.Children.Add(speakBtn);
        actions.Children.Add(quoteBtn);
        return actions;
    }

    /// <summary>
    /// 异步朗读文本（供主控与各交互控件调用）
    /// </summary>
    public async Task SpeakWordAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var player = GetLearningAudio();
            player.Play(text.Trim());
        }
        catch (Exception ex)
        {
            SetStatus(UiText.Bilingual("朗读失败：", "Audio failed: ") + ex.Message);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 将例句安全存入金句本：收藏真实英文及已有译文，不把词义当句译文，去重不覆盖已存在金句。
    /// </summary>
    public QuoteItem? SaveExampleToQuotes(string english, string? translation = null, string source = "例句收藏")
    {
        if (_restoring || !_databaseAvailable || _isForceClose)
        {
            SetStatus(UiText.Bilingual("词库暂不可用，请稍后收藏。", "Archive unavailable; please try saving later."));
            return null;
        }
        var cleanEnglish = (english ?? "").Trim();
        if (string.IsNullOrEmpty(cleanEnglish)) return null;

        // 严格保存真实句子译文；不把单词词义（如 "n. 证据"）强行作为句子译文
        var cleanTranslation = (translation ?? "").Trim();

        try
        {
            var archive = (IQuoteArchive)_vocabService;
            var saved = archive.SaveQuote(null, cleanEnglish, cleanTranslation, source, "");
            if (saved.AlreadyExists)
            {
                SetStatus(UiText.Bilingual("该例句已在金句本中，未覆盖已有金句。", "Example already in quotes; existing record preserved."));
            }
            else
            {
                SetStatus(UiText.Bilingual("例句已收藏到金句本。", "Example saved to quotes notebook."));
                if (_currentPage == "quotes")
                {
                    RenderQuotes();
                }
            }
            return saved;
        }
        catch (Exception ex)
        {
            SetStatus(UiText.Bilingual("保存金句失败：", "Failed to save quote: ") + ex.Message);
            return null;
        }
    }
}

