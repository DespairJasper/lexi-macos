using Lexi;
using Lexi.Core;
using Microsoft.Data.Sqlite;
var dir = Path.Combine(Path.GetTempPath(), "lexi-quotes-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir); var path = Path.Combine(dir,"vocab.sqlite3");
void Check(bool value,string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
using (var store = new VocabularyService(path,new Secrets()))
{
    Check(store.GetType().GetMethod("SaveQuote") != null,"quote saving API exists");
    dynamic archive = store;
    store.AddWord("evidence","","证据","");
    dynamic item = archive.SaveQuote(null,"Evidence matters.","证据很重要。","TestEdit","");
    long id = item.Id;
    Check(id > 0,"quote saved with identity");
    dynamic duplicate = archive.SaveQuote(null," Evidence matters. ","不能覆盖","","");
    Check(duplicate.Id == id && duplicate.Translation == "证据很重要。" && duplicate.AlreadyExists,"duplicate does not overwrite translation");
    archive.SaveQuote(id,"Evidence matters.","修改后的译文","Paper title","备注");
    Check(archive.GetQuotes("修改",200,0).Count == 1,"search includes edited translation");
    Check(store.GetAllWords().Count == 1 && store.GetAllWords()[0].Translation == "证据","quote write preserves words");
    var backup = store.CreateManualBackup();
    using var c = new SqliteConnection("Data Source="+backup+";Mode=ReadOnly"); c.Open();
    using var cmd = c.CreateCommand(); cmd.CommandText="SELECT translation FROM quotes WHERE id="+id;
    Check((string?)cmd.ExecuteScalar()=="修改后的译文","SQLite backup contains edited quote");
}
using (var store = new VocabularyService(path,new Secrets()))
{
    dynamic archive=store;
    Check(archive.GetQuotes("",200,0).Count == 1,"quote survives restart");
    dynamic saved=archive.GetQuotes("",200,0)[0];
    Check(saved.Translation=="修改后的译文" && saved.Notes=="备注","edited translation and notes survive restart");
    var rejected=false;
    try { archive.SaveQuote(null," ","","",""); } catch(ArgumentException) { rejected=true; }
    Check(rejected,"blank quote rejected");
    rejected=false;try { archive.SaveQuote(null,new string('x',4097),"","",""); }catch(ArgumentException){rejected=true;}
    Check(rejected,"oversized quote rejected without truncation");
    archive.DeleteQuote(saved.Id);Check(archive.GetQuotes("",200,0).Count==0,"explicit delete removes quote");
}
using (var store = new VocabularyService(path,new Secrets()))
{
    using(var seed=new SqliteConnection("Data Source="+path))
    {
        seed.Open();using var tx=seed.BeginTransaction();using var cmd=seed.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO quotes(original,translation,source,notes,created_at_utc,updated_at_utc) VALUES($original,'before','','',$time,$time)";
        cmd.Parameters.AddWithValue("$original","");cmd.Parameters.AddWithValue("$time",DateTime.UtcNow.ToString("o"));
        for(var i=0;i<501;i++){cmd.Parameters["$original"].Value="Export fixture "+i;cmd.ExecuteNonQuery();}tx.Commit();
    }
    var type=typeof(VocabularyService).Assembly.GetType("Lexi.QuoteExportService");Check(type!=null,"consistent quote export API exists");
    var original=store.GetQuotes("Export fixture 0",500,0).Single(q=>q.Original=="Export fixture 0");
    using var output=new MutatingStream(()=>store.SaveQuote(original.Id,original.Original,"after","",""));
    var export=type!.GetMethod("ExportJsonAsync")!;
    await (Task)export.Invoke(null,new object[]{output,store,CancellationToken.None})!;
    using var doc=System.Text.Json.JsonDocument.Parse(output.ToArray());var rows=doc.RootElement.GetProperty("quotes").EnumerateArray().ToArray();
    Check(rows.Length==501&&rows.Select(q=>q.GetProperty("Id").GetInt64()).Distinct().Count()==501,"export covers all rows without duplicates while editing");
    Check(rows.All(q=>q.GetProperty("Translation").GetString()=="before") && store.GetQuotes("after",50,0).Count==1,"export uses one snapshot while source edit succeeds");
}
Console.WriteLine("PASS quote storage isolated data="+dir);
sealed class Secrets : ISecureSecretStore { string? key; public string? Get()=>key;public void Set(string s)=>key=s;public void Delete()=>key=null; }

sealed class MutatingStream(Action mutate) : MemoryStream
{
    bool changed;
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)
    {
        if(!changed){changed=true;mutate();}return base.WriteAsync(buffer,token);
    }
}
