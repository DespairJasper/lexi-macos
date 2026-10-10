using System.Text.Json;
namespace Lexi;
public sealed record StudyActivityReadResult(IReadOnlyList<StudyActivity> Activities, bool Writable, bool Recovered, string? Error);
public sealed record StudyActivityWriteResult(bool Success,string? Error);
/// <summary>Independent, versioned sidecar. Reads fail closed; a corrupt primary is never overwritten.</summary>
public sealed class StudyActivityStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private sealed record Document(int Version, List<StudyActivity> Activities);
    public StudyActivityStore(string path) { ArgumentException.ThrowIfNullOrWhiteSpace(path); _path=path; }
    public StudyActivityReadResult Read() { lock(_gate) return ReadCore(); }
    private StudyActivityReadResult ReadCore()
    {
        if (!File.Exists(_path) && !Directory.Exists(_path)) return new([],true,false,null);
        try { return new(ReadFile(_path),true,false,null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) {
            try { return new(ReadFile(_path+".backup"),false,true,ex.Message); }
            catch { return new([],false,false,ex.Message); }
        }
    }
    private static List<StudyActivity> ReadFile(string path)
    {
        using var stream=File.OpenRead(path); var document=JsonSerializer.Deserialize<Document>(stream);
        if(document is null || document.Version!=1 || document.Activities is null || document.Activities.Any(a=>a is null || !StudyStatistics.IsValid(a))) throw new InvalidDataException("Invalid study activity document.");
        return document.Activities.GroupBy(a=>a.ActivityId).Select(g=>g.First()).ToList();
    }
    public StudyActivityWriteResult Append(IEnumerable<StudyActivity> activities)
    {
        lock(_gate) {
            var incoming=activities.ToArray(); if(incoming.Any(a=>!StudyStatistics.IsValid(a))) return new(false,"Invalid study activity.");
            var existing=ReadCore(); if(!existing.Writable) return new(false,existing.Error);
            var merged=existing.Activities.Concat(incoming).GroupBy(a=>a.ActivityId).Select(g=>g.First()).OrderBy(a=>a.StartUtc).ToList();
            var temporary=_path+".pending-"+Guid.NewGuid().ToString("N"); var backupTemporary=temporary+".backup";
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { JsonSerializer.Serialize(stream,new Document(1,merged)); stream.Flush(true); }
                File.Copy(temporary,backupTemporary);
                // Both files contain the latest complete snapshot; a crash between replacements retains at least one complete document.
                File.Move(backupTemporary,_path+".backup",true); File.Move(temporary,_path,true);
                return new(true,null);
            } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) { return new(false,ex.Message); }
            finally { try { if(File.Exists(temporary))File.Delete(temporary);if(File.Exists(backupTemporary))File.Delete(backupTemporary); } catch(IOException) { } }
        }
    }
}
/// <summary>Conservative clock: both interval endpoints must be eligible; long gaps are dropped, never inferred.</summary>
public sealed class StudyActivityTracker
{
    private DateTime? _last; private StudyActivityKind? _kind; private bool _eligible;
    private string? _session; private readonly List<StudyActivity> _pending=[];
    public void Advance(DateTime nowUtc,StudyActivityKind? kind,bool activeVisible,bool recentlyInteracted)
    {
        var now=StudyStatistics.Utc(nowUtc); var eligible=activeVisible&&recentlyInteracted&&kind.HasValue;
        if(_last is { } last && eligible&&_eligible&&_kind==kind && now>last && now-last<=TimeSpan.FromSeconds(15))
        {
            var seconds=(now-last).TotalSeconds;
            if(_pending.Count>0 && _pending[^1].SessionId==_session && _pending[^1].Kind==kind && _pending[^1].EndUtc==last)
                _pending[^1]=_pending[^1] with { EndUtc=now, Seconds=_pending[^1].Seconds+seconds };
            else _pending.Add(new(_session!,kind!.Value,last,now,seconds));
        }
        if(!eligible || _kind!=kind || (_last is {} before && now-before>TimeSpan.FromSeconds(15))) _session=null;
        if(eligible) _session??=Guid.NewGuid().ToString("N");
        _last=now;_kind=kind;_eligible=eligible;
    }
    public IReadOnlyList<StudyActivity> Drain() { var result=_pending.ToArray();_pending.Clear();return result; }
}
