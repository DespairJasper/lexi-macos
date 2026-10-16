using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Lexi;

public sealed class SingleInstanceService : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    public SingleInstanceService()
    {
        var path = StoragePathIdentity.Resolve(VocabularyService.GetDefaultDatabasePath());
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.UserDomainName + "|" + Environment.UserName + "|" +
            System.Diagnostics.Process.GetCurrentProcess().SessionId + "|" + path)))[..24];
        _mutexName = @"Local\LexiNative_" + scope; _pipeName = "LexiNative_" + scope;
    }
    private Mutex? _mutex;
    private readonly CancellationTokenSource _stop = new();
    private bool _owns;
    private Action? _wakeup;
    private bool _pending;
    private readonly object _gate = new();
    public bool IsPrimary => _owns;
    public event Action WakeupRequested
    {
        add { bool pending; lock (_gate) { _wakeup += value; pending = _pending; _pending = false; } if (pending) value(); }
        remove { lock (_gate) _wakeup -= value; }
    }
    public bool CheckAndAcquire(bool wakeExisting = true)
    {
        _mutex = new Mutex(false, _mutexName);
        try { _owns = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _owns = true; }
        if (!_owns)
        {
            if (!wakeExisting) return false;
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(5000);
            client.Write("SHOW"u8);
            client.Flush();
            return false;
        }
        _ = ListenAsync(_stop.Token);
        return true;
    }
    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(2000);
                var buffer = new byte[4];
                await server.ReadExactlyAsync(buffer, timeout.Token);
                if (Encoding.UTF8.GetString(buffer) != "SHOW") continue;
                Action? action;
                lock (_gate) { action = _wakeup; if (action == null) _pending = true; }
                action?.Invoke();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception) { try { await Task.Delay(150, token); } catch (OperationCanceledException) { break; } }
        }
    }
    public void Dispose()
    {
        _stop.Cancel();
        if (_owns) { _mutex?.ReleaseMutex(); _owns = false; }
        _mutex?.Dispose();
        _mutex = null;
    }
}
