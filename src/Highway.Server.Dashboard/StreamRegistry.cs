namespace Highway.Server.Dashboard;

/// <summary>
/// Tracks active SSE streams, enforces the concurrency cap, and cancels all on disposal.
/// </summary>
internal sealed class StreamRegistry : IDisposable
{
    private readonly int _maxConcurrent;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _activeCount;

    public StreamRegistry(int maxConcurrent) => _maxConcurrent = maxConcurrent;

    public CancellationToken ShutdownToken => _shutdownCts.Token;
    public int ActiveCount => _activeCount;

    public bool TryAcquire()
    {
        var current = Interlocked.Increment(ref _activeCount);
        if (current > _maxConcurrent)
        {
            Interlocked.Decrement(ref _activeCount);
            return false;
        }
        return true;
    }

    public void Release() => Interlocked.Decrement(ref _activeCount);

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }
}
