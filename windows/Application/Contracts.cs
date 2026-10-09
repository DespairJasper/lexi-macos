using Lexi.Core;

namespace Lexi;

/// <summary>Application ports; mobile hosts supply infrastructure and platform adapters.</summary>
public interface IDictionaryLookup : IDisposable
{
    Task<LookupResult> LookupAsync(string word);
}

public interface IAiExpansion
{
    Task<string> TranslateAsync(string text, AppSettings config, CancellationToken cancellationToken = default);
    Task<LookupResult> LookupWordAsync(string word, AppSettings config, CancellationToken cancellationToken = default);
    Task<LlmResult> GenerateExpansionAsync(string word, IReadOnlyList<string> modules,
        AppSettings config, CancellationToken cancellationToken = default, string? sourceExcerpt = null);
}

public interface IVocabularyArchive : IDisposable
{
    string DatabasePath { get; }
    string CredentialWarning { get; }
    string BackupWarning { get; }
    string? LastBackupPath { get; }
    string MigrationMessage { get; }
    List<WordItem> GetAllWords();
    (int Added, int Skipped) ImportArchive(IReadOnlyList<WordItem> entries);
    void AddWord(string word, string phonetic, string translation, string definition);
    void SaveArchive(long id, string translation, string notes, ArchiveMetadata metadata, LlmResult? ai);
    void SaveExpansion(long id, LlmResult result);
    void RecordEncounter(long id);
    void MarkUnfamiliar(long id);
    void MarkUnsure(long id);
    void MarkForgot(long id);
    void ExecuteBatch(IEnumerable<long> ids, string action, int? targetStage = null);
    bool UndoLastReview(long wordId);
    bool UndoLastLearningAction(long wordId);
    bool UndoMostRecentReview();
    void EditWord(long id, string translation);
    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
    string CreateManualBackup();
}

public interface ISelectionCapture
{
    Task<string?> CaptureAsync(nint source);
}

public interface IExternalDocumentLauncher
{
    void Open(string path);
}

public interface IArchiveBackups
{
    long Inspect(string path);
    string Create(string databasePath);
    string Stage(string selectedPath, string dataDirectory);
    string Restore(string stagedPath, string targetPath);
    void ReleaseStage(string stagedPath);
}

public interface IQuoteArchive
{
    List<QuoteItem> GetQuotes(string search = "", int limit = 200, int offset = 0);
    QuoteItem SaveQuote(long? id, string original, string translation, string source, string notes);
    void DeleteQuote(long id);
}

