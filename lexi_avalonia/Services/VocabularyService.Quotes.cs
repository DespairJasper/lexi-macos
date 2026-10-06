using Microsoft.Data.Sqlite;

namespace Lexi;

public sealed partial class VocabularyService : IQuoteArchive
{
    private void MigrateQuoteSchema()
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS quotes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                original TEXT NOT NULL UNIQUE CHECK(length(original) BETWEEN 1 AND 4096),
                translation TEXT NOT NULL DEFAULT '' CHECK(length(translation)<=8192),
                source TEXT NOT NULL DEFAULT '' CHECK(length(source)<=1000),
                notes TEXT NOT NULL DEFAULT '' CHECK(length(notes)<=4096),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_quotes_updated ON quotes(updated_at_utc DESC,id DESC);
            """;
        cmd.ExecuteNonQuery(); tx.Commit();
        ValidateQuoteSchema(_connection);
    }

    public static void ValidateQuoteSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='quotes'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) return;
        cmd.CommandText = "SELECT id,original,translation,source,notes,created_at_utc,updated_at_utc FROM quotes";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.GetInt64(0) < 1) throw new InvalidDataException("金句标识无效。");
            CheckQuoteText(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
            for (var i=5;i<=6;i++)
                if (!DateTimeOffset.TryParse(r.GetString(i), out var date) || date.Offset != TimeSpan.Zero)
                    throw new InvalidDataException("金句时间格式无效，已停止写入。");
        }
    }

    private static void CheckQuoteText(string original,string translation,string source,string notes)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Length > 4096)
            throw new ArgumentException("原句不能为空，最多 4096 字符。");
        if (translation.Length > 8192 || source.Length > 1000 || notes.Length > 4096)
            throw new ArgumentException("译文、来源或备注超过长度限制。");
    }

    private static QuoteItem ReadQuote(SqliteDataReader r) => new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6));

    public List<QuoteItem> GetQuotes(string search = "",int limit = 200,int offset = 0)
    {
        if (limit is <1 or >500 || offset<0) throw new ArgumentOutOfRangeException(nameof(limit));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,original,translation,source,notes,created_at_utc,updated_at_utc FROM quotes
            WHERE $search='' OR instr(lower(original),lower($search))>0 OR instr(lower(translation),lower($search))>0
                OR instr(lower(source),lower($search))>0 OR instr(lower(notes),lower($search))>0
            ORDER BY updated_at_utc DESC,id DESC LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$search",search.Trim()); cmd.Parameters.AddWithValue("$limit",limit);cmd.Parameters.AddWithValue("$offset",offset);
        using var r = cmd.ExecuteReader();var items=new List<QuoteItem>();while(r.Read()) items.Add(ReadQuote(r));return items;
    }

    public QuoteItem SaveQuote(long? id,string original,string translation,string source,string notes)
    {
        original=original.Trim();translation=translation.Trim();source=source.Trim();notes=notes.Trim();
        CheckQuoteText(original,translation,source,notes);
        using var tx = _connection.BeginTransaction();
        using var duplicate=_connection.CreateCommand();duplicate.Transaction=tx;
        duplicate.CommandText="SELECT id,original,translation,source,notes,created_at_utc,updated_at_utc FROM quotes WHERE original=$original";
        duplicate.Parameters.AddWithValue("$original",original);
        QuoteItem? existing;using(var r=duplicate.ExecuteReader()) existing=r.Read()?ReadQuote(r):null;
        if(existing!=null && existing.Id!=id)
        {
            if(id!=null) throw new InvalidOperationException("已有相同原句，未覆盖其他金句。");
            tx.Commit();return existing with {AlreadyExists=true};
        }
        var now=DateTime.UtcNow.ToString("o");
        using var cmd = _connection.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText = id==null
            ? "INSERT INTO quotes(original,translation,source,notes,created_at_utc,updated_at_utc) VALUES($original,$translation,$source,$notes,$now,$now) RETURNING id"
            : "UPDATE quotes SET original=$original,translation=$translation,source=$source,notes=$notes,updated_at_utc=$now WHERE id=$id RETURNING id";
        cmd.Parameters.AddWithValue("$original",original);cmd.Parameters.AddWithValue("$translation",translation);
        cmd.Parameters.AddWithValue("$source",source);cmd.Parameters.AddWithValue("$notes",notes);cmd.Parameters.AddWithValue("$now",now);
        if(id!=null)cmd.Parameters.AddWithValue("$id",id.Value);
        var result=cmd.ExecuteScalar() ?? throw new InvalidOperationException("金句已不存在，未保存。");
        var savedId=Convert.ToInt64(result);tx.Commit();BackupCommittedState();
        using var query = _connection.CreateCommand();query.CommandText="SELECT id,original,translation,source,notes,created_at_utc,updated_at_utc FROM quotes WHERE id=$id";
        query.Parameters.AddWithValue("$id",savedId);using var reader=query.ExecuteReader();reader.Read();return ReadQuote(reader);
    }

    public void DeleteQuote(long id)
    {
        using var tx=_connection.BeginTransaction();using var cmd=_connection.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="DELETE FROM quotes WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("金句已不存在。");
        tx.Commit();BackupCommittedState();
    }
}
