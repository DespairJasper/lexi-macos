using Microsoft.Data.Sqlite;

namespace Lexi;

public sealed class ArchiveBackups : IArchiveBackups
{
    private static SqliteConnection OpenReadOnly(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }
    public long Inspect(string path)
    {
        using var db = OpenReadOnly(path);
        DatabaseSafety.Validate(db); DatabaseSafety.ValidateApplicationSchema(db);
        using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM words";
        return Convert.ToInt64(command.ExecuteScalar());
    }
    public string Create(string databasePath)
    {
        using var db = OpenReadOnly(databasePath);
        return DatabaseSafety.CreateBackup(db, databasePath);
    }
    public string Stage(string selectedPath, string dataDirectory) => DatabaseSafety.StageRestoreSelection(selectedPath, dataDirectory);
    public string Restore(string stagedPath, string targetPath) => DatabaseSafety.RestoreBackup(stagedPath, targetPath);
    public void ReleaseStage(string stagedPath) => DatabaseSafety.DeleteRestoreSelection(stagedPath);
}
