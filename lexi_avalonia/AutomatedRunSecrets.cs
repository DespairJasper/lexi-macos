using Lexi.Core;

namespace Lexi;

// Automated app modes use a process-local credential store; native secure stores stay untouched.
internal sealed class AutomatedRunSecrets : ISecureSecretStore
{
    private string? _value;
    public string? Get() => _value;
    public void Set(string value) => _value = value;
    public void Delete() => _value = null;
}
