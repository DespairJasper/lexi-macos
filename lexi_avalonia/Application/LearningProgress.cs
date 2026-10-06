using System.Text.Json;

namespace Lexi;

public sealed class LearningProgress
{
    public string SelectedSection { get; set; } = "01_自然地理";
    public int LastShuffleSeed { get; set; }
    public bool TypingHints { get; set; } = true;
    public int RoundSize { get; set; } = 20;
    public bool RoundAll { get; set; }
    public bool RoundRandom { get; set; }
    public HashSet<string> Typed { get; set; } = [];
    public HashSet<string> Errors { get; set; } = [];
    public Dictionary<int, string> WritingDrafts { get; set; } = [];
    public static LearningProgress Load(string path)
    {
        if (!File.Exists(path)) return new();
        var progress = JsonSerializer.Deserialize<LearningProgress>(File.ReadAllText(path)) ?? throw new InvalidDataException("学习记录为空");
        if (progress.Typed == null || progress.Errors == null || progress.WritingDrafts == null)
            throw new InvalidDataException("学习记录结构无效");
        return progress;
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}

public interface IWordAudioPlayer : IDisposable
{
    void Play(string text, string? localRecording = null);
    void Stop();
    bool CanSeek => false;
    bool IsPlaying => false;
    double Position => 0;
    double Duration => 0;
    void TogglePause() { }
    void Seek(double seconds) { }
}
