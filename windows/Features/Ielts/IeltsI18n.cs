using System;
using System.Collections.Generic;

namespace Lexi.Features.Ielts;

/// <summary>
/// IELTS 专区动态多语言翻译支持与集中词条。
/// 支持随 UiText.Language 在中英文之间即时响应。
/// </summary>
public static class IeltsI18n
{
    private static readonly Dictionary<string, string> EnTranslations = new(StringComparer.Ordinal)
    {
        ["IELTS 专题"] = "IELTS",
        ["IELTS 学习资料"] = "IELTS Learning Materials",
        ["词汇"] = "Vocabulary",
        ["听力资料"] = "Listening Resources",
        ["阅读同义替换"] = "Reading Synonyms",
        ["写作练习"] = "100 Sentences Writing",
        ["章节目录"] = "Chapters",
        ["搜索章节..."] = "Search chapters...",
        ["全部章节"] = "All Chapters",
        ["仅词汇"] = "Vocab Only",
        ["仅听力"] = "Listening Only",
        ["搜索当前章节词汇或释义..."] = "Search words or meanings...",
        ["全部"] = "All",
        ["已练习"] = "Practiced",
        ["错误词"] = "Errors",
        ["仅看已选"] = "Selected Only",
        ["查看全部"] = "Show All",
        ["开始练习"] = "Start Practice",
        ["开始学习"] = "Start Learning",
        ["提示拼写"] = "Guided Spelling",
        ["拼写练习"] = "Spelling practice",
        ["无提示默写"] = "Dictation",
        ["创建每日计划"] = "Create Daily Plan",
        ["同义替换听写"] = "Synonyms Dictation",
        ["练习设置"] = "Practice Settings",
        ["练习数量"] = "Word Count",
        ["全部词"] = "All Words",
        ["随机练习"] = "Random Order",
        ["清空选择"] = "Clear Selection",
        ["清空"] = "Clear",
        ["学习已选词"] = "Practice Selected",
        ["章节录音"] = "Chapter Audio",
        ["播放"] = "Play",
        ["暂停"] = "Pause",
        ["停止"] = "Stop",
        ["当前章节暂无独立音频录音。"] = "No standalone audio file for this chapter.",
        ["无法播放录音，改用系统朗读。"] = "Unable to play recording; falling back to speech synthesis.",
        ["该条目暂无录音。"] = "No recording for this entry.",
        ["同义替换训练已完成。"] = "Synonyms training complete.",
        ["检查全部答案"] = "Check All Answers",
        ["重播读音"] = "Replay Audio",
        ["下一词"] = "Next Word",
        ["全部正确，可以进入下一词。"] = "All correct! Press Next to proceed.",
        ["请正确输入考点词及全部同义替换后继续。"] = "Please enter the target word and all synonyms correctly before continuing.",
        ["返回 IELTS 目录"] = "Back to IELTS Catalog",
        ["显示 / 隐藏答案"] = "Show / Hide Answer",
        ["朗读参考译文"] = "Read Reference Answer",
        ["书中答案："] = "Book Answer: ",
        ["备用译文："] = "Alternate Answer: ",
        ["当前范围没有同义替换练习。"] = "No synonym exercises in the current scope.",
        ["听力与语法学习资料"] = "Listening & Grammar Resources",
        ["课程视频"] = "Course Video",
        ["语法讲义 PDF"] = "Grammar Lecture PDF",
        ["语法思维导图"] = "Grammar Mind Map",
        ["资料来源"] = "Source",
        ["打开完整听力笔记"] = "Open Complete Listening Notes",
        ["100 句翻译练习"] = "100 Sentences Translation Practice",
        ["输入你的英文翻译，自动保存"] = "Type your English translation; auto-saved",
        ["全选当前筛选"] = "Select All Filtered",
        ["反选可见"] = "Invert Visible Selection",
        ["收藏到档案"] = "Add to Archive",
        ["加入词汇档案"] = "Add to Archive",
        ["查看档案"] = "View Archive",
        ["已在档案中 · 查看档案"] = "In Archive · View",
        ["查看全部同义替换"] = "Show All Synonyms",
        ["当前章节暂无同义替换考点"] = "No synonyms in current chapter",
        ["雅思同义替换主要收录于高频考点词章节（如听力 179 考点词等）。"] = "IELTS synonyms are mainly cataloged in key listening/reading chapters.",
        ["未找到匹配的同义替换考点"] = "No matching synonyms found",
        ["清除搜索"] = "Clear Search",
        ["搜索考点词、同义表达或释义..."] = "Search target word, synonyms or meaning...",
        ["听力笔记与章节录音"] = "Listening Notes & Audio",
        ["语法讲义与思维导图"] = "Grammar Notes & Mind Map",
        ["语法资料"] = "Grammar Resources",
        ["来源许可"] = "License & Source",
        ["打开语法讲义 PDF"] = "Open Grammar PDF",
        ["打开语法思维导图 SVG"] = "Open Grammar SVG",
        ["打开思维导图图片 PNG"] = "Open Mind Map PNG",
        ["展开完整听力笔记"] = "Expand Listening Notes",
        ["收起听力笔记"] = "Collapse Notes",
        ["在外部程序中打开"] = "Open in External App",
        ["访问资料开源主页"] = "Visit Source Repository",
        ["本地文件就绪"] = "Local files ready",
        ["本地资源完整"] = "Local assets complete",
        ["未选择要收藏的词汇。"] = "No words selected to archive.",
        ["同义替换"] = "Synonyms",
        ["包含雅思听力高频考点词汇、听力笔记及章节录音音频。"] = "Contains IELTS listening high-frequency vocabulary, notes, and audio.",
        ["雅思基础语法核心思维导图与完整配套讲义。"] = "Core IELTS grammar mind map and complete lecture notes.",
        ["资料来自 my-ielts；原作者禁止商业用途。口语和大小作文尚无完整内容。"] = "Source: my-ielts. Non-commercial use only.",
        ["批量收藏完成：新增 {0} 词，已存在 {1} 词，失败 {2} 词。"] = "Batch archive complete: {0} added, {1} existing, {2} failed.",
        ["已将 '{0}' 加入词汇档案。"] = "Added '{0}' to vocabulary archive.",
        ["'{0}' 已在词汇档案中，未覆盖已有笔记译文。"] = "'{0}' already in archive; existing notes and translation preserved."
    };

    public static string T(string chinese)
    {
        if (EnTranslations.TryGetValue(chinese, out var en))
            return Lexi.UiText.Bilingual(chinese, en);
        return chinese;
    }

    public static string Format(FormattableString value)
    {
        var chinese = value.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var fmt = T(value.Format);
        var english = string.Format(System.Globalization.CultureInfo.CurrentCulture, fmt, value.GetArguments());
        return Lexi.UiText.Language == "en" ? english : chinese;
    }

    public static string SelectedCountFormat(int count, int chapterCount)
    {
        var isEn = Lexi.UiText.Language == "en";
        if (chapterCount > 1)
        {
            return isEn
                ? $"{count} words selected (across {chapterCount} chapters)"
                : $"已选 {count} 词（跨 {chapterCount} 个章节）";
        }
        return isEn ? $"{count} words selected" : $"已选 {count} 词";
    }

    public static string WordCountFormat(int count)
    {
        return Lexi.UiText.Language == "en" ? $"{count} words" : $"{count} 词";
    }
}
