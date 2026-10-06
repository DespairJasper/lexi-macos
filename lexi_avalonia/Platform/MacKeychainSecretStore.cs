using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Lexi.Core;

namespace Lexi;

/// <summary>Native macOS Keychain storage. Credentials are bound to the data directory identifier.</summary>
public sealed class MacKeychainSecretStore : ISecureSecretStore
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int MaxSecretBytes = 64 * 1024;
    private const int errSecSuccess = 0;
    private const int errSecItemNotFound = -25300;
    private const int errSecDuplicateItem = -25299;

    private readonly string _serviceName;
    private readonly string _accountName;
    private readonly byte[] _serviceBytes;
    private readonly byte[] _accountBytes;
    private readonly object _gate = new();

    public string ServiceName => _serviceName;
    public string AccountName => _accountName;

    public MacKeychainSecretStore(string dataDirectory, string serviceName = "Lexi")
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS Keychain is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var normalized = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _serviceName = serviceName;
        _accountName = "api-key:" + normalized;
        _serviceBytes = Encoding.UTF8.GetBytes(_serviceName);
        _accountBytes = Encoding.UTF8.GetBytes(_accountName);
    }

    public string? Get()
    {
        lock (_gate)
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)_serviceBytes.Length,
                _serviceBytes,
                (uint)_accountBytes.Length,
                _accountBytes,
                out var passwordLength,
                out var passwordData,
                out var itemRef);

            try
            {
                if (status == errSecItemNotFound) return null;
                if (status != errSecSuccess)
                    throw new CryptographicException($"macOS Keychain query failed with OSStatus {status}.");
                if (passwordLength > MaxSecretBytes + 4096)
                {
                    throw new CryptographicException("Protected credential in Keychain is invalid (exceeds size limit).");
                }

                var buffer = new byte[passwordLength];
                Marshal.Copy(passwordData, buffer, 0, (int)passwordLength);
                try
                {
                    return new UTF8Encoding(false, true).GetString(buffer);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new CryptographicException("Protected credential in Keychain is not valid UTF-8.", ex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }
            finally
            {
                if (passwordData != IntPtr.Zero)
                {
                    SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                }
                if (itemRef != IntPtr.Zero)
                {
                    CFRelease(itemRef);
                }
            }
        }
    }

    public void Set(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (Encoding.UTF8.GetByteCount(secret) > MaxSecretBytes)
            throw new ArgumentException("Credential is too long.", nameof(secret));

        lock (_gate)
        {
            var plain = Encoding.UTF8.GetBytes(secret);
            try
            {
                var status = SecKeychainFindGenericPassword(
                    IntPtr.Zero,
                    (uint)_serviceBytes.Length,
                    _serviceBytes,
                    (uint)_accountBytes.Length,
                    _accountBytes,
                    out var oldPasswordLength,
                    out var oldPasswordData,
                    out var itemRef);

                try
                {
                    if (status == errSecSuccess && itemRef != IntPtr.Zero)
                    {
                        var modStatus = SecKeychainItemModifyContent(
                            itemRef,
                            IntPtr.Zero,
                            (uint)plain.Length,
                            plain);

                        if (modStatus != errSecSuccess)
                        {
                            throw new CryptographicException($"macOS Keychain update failed with OSStatus {modStatus}.");
                        }
                    }
                    else if (status == errSecItemNotFound)
                    {
                        IntPtr newItemRef = IntPtr.Zero;
                        try
                        {
                            var addStatus = SecKeychainAddGenericPassword(
                                IntPtr.Zero, (uint)_serviceBytes.Length, _serviceBytes,
                                (uint)_accountBytes.Length, _accountBytes,
                                (uint)plain.Length, plain, out newItemRef);
                            if (addStatus == errSecDuplicateItem)
                            {
                                // Another instance created it after our lookup. Update in place;
                                // deleting here could destroy its key if the following operation fails.
                                UpdateExistingAfterDuplicate(plain);
                            }
                            else if (addStatus != errSecSuccess)
                            {
                                throw new CryptographicException($"macOS Keychain add failed with OSStatus {addStatus}.");
                            }
                        }
                        finally
                        {
                            if (newItemRef != IntPtr.Zero) CFRelease(newItemRef);
                        }
                    }
                    else
                    {
                        throw new CryptographicException($"macOS Keychain find failed with OSStatus {status}.");
                    }
                }
                finally
                {
                    if (oldPasswordData != IntPtr.Zero)
                    {
                        SecKeychainItemFreeContent(IntPtr.Zero, oldPasswordData);
                    }
                    if (itemRef != IntPtr.Zero)
                    {
                        CFRelease(itemRef);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    private void UpdateExistingAfterDuplicate(byte[] plain)
    {
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero, (uint)_serviceBytes.Length, _serviceBytes,
            (uint)_accountBytes.Length, _accountBytes,
            out _, out var passwordData, out var itemRef);
        try
        {
            if (status != errSecSuccess || itemRef == IntPtr.Zero)
                throw new CryptographicException($"macOS Keychain concurrent lookup failed with OSStatus {status}.");
            var modified = SecKeychainItemModifyContent(itemRef, IntPtr.Zero, (uint)plain.Length, plain);
            if (modified != errSecSuccess)
                throw new CryptographicException($"macOS Keychain concurrent update failed with OSStatus {modified}.");
        }
        finally
        {
            if (passwordData != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            if (itemRef != IntPtr.Zero) CFRelease(itemRef);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)_serviceBytes.Length,
                _serviceBytes,
                (uint)_accountBytes.Length,
                _accountBytes,
                out var passwordLength,
                out var passwordData,
                out var itemRef);

            try
            {
                if (status == errSecItemNotFound)
                {
                    return;
                }

                if (status != errSecSuccess)
                {
                    throw new CryptographicException($"macOS Keychain find for delete failed with OSStatus {status}.");
                }

                if (itemRef != IntPtr.Zero)
                {
                    var delStatus = SecKeychainItemDelete(itemRef);
                    if (delStatus != errSecSuccess && delStatus != errSecItemNotFound)
                    {
                        throw new CryptographicException($"macOS Keychain delete failed with OSStatus {delStatus}.");
                    }
                }
            }
            finally
            {
                if (passwordData != IntPtr.Zero)
                {
                    SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                }
                if (itemRef != IntPtr.Zero)
                {
                    CFRelease(itemRef);
                }
            }
        }
    }

    internal void SetRawForTesting(byte[] rawData)
    {
        ArgumentNullException.ThrowIfNull(rawData);
        lock (_gate)
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)_serviceBytes.Length,
                _serviceBytes,
                (uint)_accountBytes.Length,
                _accountBytes,
                out var oldLen,
                out var oldData,
                out var itemRef);
            try
            {
                if (status == errSecSuccess && itemRef != IntPtr.Zero)
                {
                    var modStatus = SecKeychainItemModifyContent(itemRef, IntPtr.Zero, (uint)rawData.Length, rawData);
                    if (modStatus != errSecSuccess)
                    {
                        throw new CryptographicException($"SetRawForTesting modify failed with OSStatus {modStatus}.");
                    }
                }
                else if (status == errSecItemNotFound)
                {
                    IntPtr newItem = IntPtr.Zero;
                    try
                    {
                        var addStatus = SecKeychainAddGenericPassword(
                            IntPtr.Zero, (uint)_serviceBytes.Length, _serviceBytes,
                            (uint)_accountBytes.Length, _accountBytes,
                            (uint)rawData.Length, rawData, out newItem);
                        if (addStatus != errSecSuccess)
                            throw new CryptographicException($"SetRawForTesting add failed with OSStatus {addStatus}.");
                    }
                    finally
                    {
                        if (newItem != IntPtr.Zero) CFRelease(newItem);
                    }
                }
                else
                {
                    throw new CryptographicException($"SetRawForTesting find failed with OSStatus {status}.");
                }
            }
            finally
            {
                if (oldData != IntPtr.Zero)
                {
                    SecKeychainItemFreeContent(IntPtr.Zero, oldData);
                }
                if (itemRef != IntPtr.Zero)
                {
                    CFRelease(itemRef);
                }
            }
        }
    }

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyContent(
        IntPtr itemRef,
        IntPtr tags,
        uint length,
        byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr cf);
}
