using Lexi.Core;

namespace Lexi;

/// <summary>Platform secret store factory.</summary>
public static class SecretStoreFactory
{
    public static ISecureSecretStore Create(string dataDirectory)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainSecretStore(dataDirectory);
        }
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSecretStore(dataDirectory);
        }
        throw new PlatformNotSupportedException("Unsupported operating system for secure secret store.");
    }
}
