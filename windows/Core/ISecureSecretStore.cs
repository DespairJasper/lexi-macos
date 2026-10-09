namespace Lexi.Core;

/// <summary>Platform credential storage; no desktop framework dependency.</summary>
public interface ISecureSecretStore
{
    string? Get();
    void Set(string secret);
    void Delete();
}
