namespace DeepDungeon.Fsd.Runtime;

public sealed class FsdFacadeLifecycle : IDisposable
{
    private bool _disposed;

    public bool IsDisposed => _disposed;

    public void EnsureActive() => ObjectDisposedException.ThrowIf(_disposed, this);

    public bool TryDispose()
    {
        if (_disposed)
            return false;
        _disposed = true;
        return true;
    }

    public void Dispose() => TryDispose();
}
