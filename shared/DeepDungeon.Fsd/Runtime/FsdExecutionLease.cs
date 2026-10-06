namespace DeepDungeon.Fsd.Runtime;

public sealed class FsdExecutionLease : IDisposable
{
    public static string LeaseName { get; } = $@"Local\DeepDungeon.Fsd.Execution.v2.{Environment.ProcessId}";

    private readonly Semaphore _semaphore;
    private readonly string _ownerIdentity;
    private bool _held;
    private bool _disposed;

    public FsdExecutionLease(string ownerIdentity)
    {
        if (string.IsNullOrWhiteSpace(ownerIdentity))
            throw new ArgumentException("Lease owner identity is required.", nameof(ownerIdentity));
        _ownerIdentity = ownerIdentity;
        _semaphore = new Semaphore(1, 1, LeaseName);
    }

    public bool IsHeld => _held;
    public string OwnerIdentity => _ownerIdentity;

    public void Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_held)
            return;

        // Independently loaded plugins share the framework thread. A mutex is
        // reentrant there; the semaphore gives each host instance one claim.
        if (!_semaphore.WaitOne(0))
            throw new InvalidOperationException($"FSD execution is already owned by another plugin. Requested owner: {_ownerIdentity}.");

        _held = true;
    }

    public void Release()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_held)
            return;
        _semaphore.Release();
        _held = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_held)
        {
            _semaphore.Release();
            _held = false;
        }
        _semaphore.Dispose();
        _disposed = true;
    }
}
