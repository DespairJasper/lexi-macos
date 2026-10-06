using System.Text.Json;

namespace Lexi;

public sealed class DailyStudyPlanStore
{
    private readonly string _path;

    public DailyStudyPlanStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空。", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public List<DailyStudyPlan> Load()
    {
        if (!File.Exists(_path)) return [];
        List<DailyStudyPlan> plans;
        try
        {
            plans = JsonSerializer.Deserialize<List<DailyStudyPlan>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("计划文件为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("计划 JSON 无效。", ex);
        }
        Validate(plans);
        return plans;
    }

    public void Save(IReadOnlyList<DailyStudyPlan> plans)
    {
        if (plans is null) throw new ArgumentNullException(nameof(plans));
        Validate(plans);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(plans));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(IReadOnlyList<DailyStudyPlan> plans)
    {
        if (plans.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) ||
            string.IsNullOrWhiteSpace(p.Name) || p.DailyWordCount <= 0 ||
            !Enum.IsDefined(p.Source) || !Enum.IsDefined(p.Status) ||
            p.CreatedAt == default || p.Words is null || p.Words.Count == 0 ||
            p.CompletedWordIds is null || p.CurrentBatchWordIds is null || p.ForgotWordIds is null || p.OriginalWordIds is null ||
            p.CurrentBatchWordIds.Distinct(StringComparer.Ordinal).Count() != p.CurrentBatchWordIds.Count ||
            p.CurrentBatchWordIds.Concat(p.ForgotWordIds).Any(id => !p.Words.Any(w => w.Id == id)) ||
            p.Words.Any(w => w is null || string.IsNullOrWhiteSpace(w.Id) || string.IsNullOrWhiteSpace(w.Word)) ||
            p.Words.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != p.Words.Count ||
            p.CompletedWordIds.Any(id => !p.Words.Any(w => w.Id == id)) ||
            (p.OriginalWordIds.Count > 0 && (p.OriginalWordIds.Count != p.Words.Count ||
                !new HashSet<string>(p.OriginalWordIds, StringComparer.Ordinal).SetEquals(p.Words.Select(w => w.Id)))) ||
            (p.Status == DailyStudyPlanStatus.Completed) != (p.CompletedWordIds.Count == p.Words.Count) ||
            (p.LastBatchCompletedDate is not null && p.CompletedWordIds.Count == 0)) ||
            plans.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != plans.Count)
            throw new InvalidDataException("计划结构无效。");
    }
}
