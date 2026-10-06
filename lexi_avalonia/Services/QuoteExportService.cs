using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Lexi;

/// <summary>Stream a consistent snapshot while the main archive stays editable.</summary>
public static class QuoteExportService
{
    public static async Task<int> ExportJsonAsync(Stream output,IVocabularyArchive archive,CancellationToken cancellationToken=default)
    {
        var dataDir=Path.GetDirectoryName(archive.DatabasePath)!;
        var backup=archive.CreateManualBackup();
        var snapshot=DatabaseSafety.StageRestoreSelection(backup,dataDir);
        try
        {
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=snapshot,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();
            using var command=connection.CreateCommand();command.CommandText="SELECT id,original,translation,source,notes,created_at_utc,updated_at_utc FROM quotes ORDER BY updated_at_utc DESC,id DESC";
            using var rows=command.ExecuteReader();using var writer=new Utf8JsonWriter(output,new JsonWriterOptions {Indented=true});
            writer.WriteStartObject();writer.WriteString("schema","lexi-quotes-1");writer.WritePropertyName("quotes");writer.WriteStartArray();var count=0;
            while(rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                JsonSerializer.Serialize(writer,new QuoteItem(rows.GetInt64(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetString(5),rows.GetString(6)));
                if(++count%500==0)await writer.FlushAsync(cancellationToken);
            }
            writer.WriteEndArray();writer.WriteEndObject();await writer.FlushAsync(cancellationToken);return count;
        }
        finally{DatabaseSafety.DeleteRestoreSelection(snapshot);}
    }
}
