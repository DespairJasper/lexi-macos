using Lexi.Core;

namespace Lexi;

/// <summary>Composition boundary. Hosts consume application ports, not database or HTTP implementations.</summary>
public static class ServiceFactory
{
    public static IVocabularyArchive OpenArchive(string? databasePath = null, ISecureSecretStore? secrets = null)
        => new VocabularyService(databasePath, secrets);
    public static IDictionaryLookup OpenDictionary(string? dictionaryPath = null)
        => new DictionaryService(dictionaryPath);
    public static IAiExpansion CreateAi() => new AiService();
    public static ILearningMemoryStore OpenMemory(IVocabularyArchive archive) => (ILearningMemoryStore)archive;
    public static IArchiveBackups CreateBackups() => new ArchiveBackups();
}
