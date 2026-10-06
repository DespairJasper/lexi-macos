using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lexi;

public static class FoundationCredentialTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lexi_CredentialRegression_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "vocab.sqlite3");
            using var service = new VocabularyService(path);
            const string secret = "fixture-only-credential-728935";
            service.SaveSettings(new AppSettings { ApiKey = secret, RememberKey = true });
            Check(!ReadJson(path).Contains(secret), "remembered key was written as SQLite plaintext");

            Lexi.Core.ISecureSecretStore store = OperatingSystem.IsWindows()
                ? new WindowsSecretStore(root)
                : new MacKeychainSecretStore(root);

            Check(store.Get() == secret && service.LoadSettings().ApiKey == secret, "remembered key was not loaded from secure store");
            service.SaveSettings(new AppSettings { ApiKey = "ephemeral-fixture", RememberKey = false });
            Check(store.Get() == null && service.LoadSettings().ApiKey == "" && !ReadJson(path).Contains("ephemeral-fixture"), "non-remembered key persisted");
            WriteJson(path, JsonSerializer.Serialize(new AppSettings { ApiKey = secret, RememberKey = true, Model = "migration-model" }));
            Check(service.LoadSettings().ApiKey == secret && !ReadJson(path).Contains(secret)
                && store.Get() == secret, "legacy key migration was not verified and cleared");
            Check(service.LoadSettings().Model == "migration-model", "migration lost settings");

            if (OperatingSystem.IsWindows())
            {
                var blob = Directory.GetFiles(Path.Combine(root, "credentials"), "*.dpapi").Single();
                File.WriteAllBytes(blob, [1, 2, 3]);
            }
            else if (OperatingSystem.IsMacOS())
            {
                ((MacKeychainSecretStore)store).SetRawForTesting([0xFF, 0xFE, 0xFD]);
            }

            Check(service.LoadSettings().ApiKey == "", "corrupt credential store returned a key");
            Check(!string.IsNullOrEmpty(service.CredentialWarning), "credential failure has no visible warning");
            service.AddWord("still-operational", "", "正常", "");
            Check(service.GetAllWords().Count == 1, "credential error blocked vocabulary");
            service.SaveSettings(new AppSettings { ApiKey = secret, RememberKey = true });
            using (var c = Open(path))
            {
                using var command = c.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_settings BEFORE UPDATE ON app_settings BEGIN SELECT RAISE(ABORT,'fixture failure'); END;";
                command.ExecuteNonQuery();
            }
            try { service.SaveSettings(new AppSettings { RememberKey = false }); throw new Exception("failed settings write unexpectedly succeeded"); }
            catch (SqliteException) { }
            Check(store.Get() == secret, "failed save deleted existing credential");
            using (var c = Open(path)) { using var command = c.CreateCommand(); command.CommandText = "DROP TRIGGER fail_settings"; command.ExecuteNonQuery(); }
            WriteJson(path, JsonSerializer.Serialize(new AppSettings { ApiKey = secret, RememberKey = true }));
            using (var blocked = new VocabularyService(path, new RejectingSecretStore()))
            {
                Check(blocked.LoadSettings().ApiKey == "" && blocked.CredentialWarning != "", "failed migration did not report credential warning");
                Check(ReadJson(path).Contains(secret), "failed DPAPI migration erased recoverable legacy key");
            }
            Console.WriteLine("PASS: credential integration (DPAPI/Keychain, legacy migration, SQLite redaction, non-remembered and corrupt credentials)");
        }
        finally
        {
            if (OperatingSystem.IsMacOS())
            {
                try { new MacKeychainSecretStore(root).Delete(); } catch { }
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string ReadJson(string path)
    {
        using var c = Open(path); using var command = c.CreateCommand();
        command.CommandText = "SELECT settings_json FROM app_settings WHERE id=1";
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    private static void WriteJson(string path, string json)
    {
        using var c = Open(path); using var command = c.CreateCommand();
        command.CommandText = "UPDATE app_settings SET settings_json=$json WHERE id=1";
        command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open(); return c;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Credential regression failed: " + message);
    }

    private sealed class RejectingSecretStore : Lexi.Core.ISecureSecretStore
    {
        public string? Get() => throw new System.Security.Cryptography.CryptographicException();
        public void Set(string secret) => throw new System.Security.Cryptography.CryptographicException();
        public void Delete() => throw new System.Security.Cryptography.CryptographicException();
    }
}
