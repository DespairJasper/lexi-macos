using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Lexi.Core;

namespace Lexi;

/// <summary>DPAPI CurrentUser storage. The protected subdirectory grants access only to its owner.</summary>
public sealed class WindowsSecretStore : ISecureSecretStore
{
    private const int UiForbidden = 1;
    private const int MaxSecretBytes = 64 * 1024;
    private readonly string _directory;
    private readonly string _path;
    private readonly object _gate = new();

    public WindowsSecretStore(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "credentials");
        _path = Path.Combine(_directory, "api-key.dpapi");
    }

    public string? Get()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            if (new FileInfo(_path).Length > MaxSecretBytes + 4096) throw new CryptographicException("Protected credential is invalid.");
            var encrypted = File.ReadAllBytes(_path);
            var plain = Transform(encrypted, protect: false);
            try { return new UTF8Encoding(false, true).GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }

    public void Set(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (Encoding.UTF8.GetByteCount(secret) > MaxSecretBytes) throw new ArgumentException("Credential is too long.", nameof(secret));
        lock (_gate)
        {
            EnsurePrivateDirectory();
            var plain = Encoding.UTF8.GetBytes(secret);
            byte[] encrypted;
            try { encrypted = Transform(plain, protect: true); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var temporary = Path.Combine(_directory, ".credential-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    file.Write(encrypted);
                    file.Flush(flushToDisk: true);
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public void Delete()
    {
        lock (_gate) { if (File.Exists(_path)) File.Delete(_path); }
    }

    private void EnsurePrivateDirectory()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = Directory.CreateDirectory(_directory);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Credential directory cannot be a link.");
        var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("Windows user identity is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!success) throw new CryptographicException("Windows could not protect or read this credential.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Length < 0 || output.Length > MaxSecretBytes + 4096) throw new CryptographicException("Protected credential is invalid.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            ZeroNative(input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                ZeroNative(output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }

    private static void ZeroNative(IntPtr pointer, int length)
    {
        for (var offset = 0; offset < length; offset++) Marshal.WriteByte(pointer, offset, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
