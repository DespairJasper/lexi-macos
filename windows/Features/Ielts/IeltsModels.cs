using System;
using System.Collections.Generic;
using System.Linq;

namespace Lexi.Features.Ielts;

public enum IeltsFilterType
{
    All,
    Practiced,
    Errors,
    SelectedOnly
}

public sealed class IeltsPracticeSettings
{
    public int Count { get; set; } = 20;
    public bool AllWords { get; set; } = true;
    public bool RandomOrder { get; set; }
}

/// <summary>
/// 跨章节选择仓库：
/// 保持词汇唯一身份（不按拼写合并，使用原始条目ID），
/// 切章时不被清空，支持跨章节统计与明确训练来源。
/// </summary>
public sealed class IeltsSelectionStore
{
    private readonly Dictionary<string, (string SectionId, LearningWord Word)> _selected = new(StringComparer.Ordinal);

    public int Count => _selected.Count;

    public bool Contains(string wordId) => _selected.ContainsKey(wordId);

    public void Toggle(LearningWord word, string sectionId, bool isSelected)
    {
        if (isSelected)
            _selected[word.Id] = (sectionId, word);
        else
            _selected.Remove(word.Id);
    }

    public void Clear() => _selected.Clear();

    public int DistinctChapterCount => _selected.Values.Select(v => v.SectionId).Distinct(StringComparer.Ordinal).Count();

    public List<LearningWord> GetSelectedWords() => _selected.Values.Select(v => v.Word).ToList();

    public List<LearningWord> GetSelectedWordsForSection(string sectionId) =>
        _selected.Values.Where(v => v.SectionId == sectionId).Select(v => v.Word).ToList();

    /// <summary>
    /// 获取诚实明确的训练来源描述。
    /// </summary>
    public string GetExplicitTrainingSource(LearningSection currentSection)
    {
        if (_selected.Count == 0)
            return $"IELTS · {currentSection.Title}";

        var chapterCount = DistinctChapterCount;
        if (chapterCount > 1)
            return $"IELTS · 跨章节已选 {_selected.Count} 词";

        return $"IELTS · {currentSection.Title}（已选 {_selected.Count} 词）";
    }
}
