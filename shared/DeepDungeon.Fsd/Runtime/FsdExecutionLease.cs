namespace DeepDungeon.Fsd.Runtime;

public sealed class FsdExecutionLease : IDisposable
{
    public const string LeaseName = @"Local\DeepDungeon.Fsd.Execution.v1";

    private readonly Mutex _mutex;
    private readonly string _ownerIdentity;
    private bool _held;
    private bool _disposed;

    public FsdExecutionLease(string ownerIdentity)
    {
        if (string.IsNullOrWhiteSpace(ownerIdentity))
            throw new ArgumentException("Lease owner identity is required.", nameof(ownerIdentity));
        _ownerIdentity = ownerIdentity;
        _mutex = new Mutex(false, LeaseName);
    }

    public bool IsHeld => _held;
    public string OwnerIdentity => _ownerIdentity;

    public void Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_held)
            return;

        try
        {
            if (!_mutex.WaitOne(0))
                throw new InvalidOperationException($"FSD execution is already owned by another plugin. Requested owner: {_ownerIdentity}.");
        }
        catch (AbandonedMutexException)
        {
            // The abandoned mutex is acquired by this call; this is an explicit ownership transfer.
        }

        _held = true;
    }

    public void Release()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_held)
            return;
        _mutex.ReleaseMutex();
        _held = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_held)
        {
            _mutex.ReleaseMutex();
            _held = false;
        }
        _mutex.Dispose();
        _disposed = true;
    }
}
