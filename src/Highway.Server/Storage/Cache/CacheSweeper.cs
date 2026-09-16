namespace Highway.Server.Storage.Cache;

/// <summary>
/// Periodic maintenance for the cache (044 R5/R7): drop expired entries, and enforce the
/// size cap. Size is primarily bounded by TTL turnover; when the store exceeds
/// <c>maxSizeBytes</c> the sweeper deletes expired entries and, if still over,
/// <see cref="IHighwayCacheStore.Clear"/>s the cache — safe, since every entry is
/// regenerable, and self-healing as the cache repopulates on demand.
/// </summary>
internal sealed class CacheSweeper : IDisposable
{
    private readonly IHighwayCacheStore _store;
    private readonly long _maxSizeBytes;
    private readonly TimeProvider _clock;
    private readonly Timer _timer;
    private readonly Action<string>? _onEvent;
    private long _clearedOversizeCount;

    public CacheSweeper(IHighwayCacheStore store, long maxSizeBytes, TimeSpan interval, TimeProvider? clock = null, Action<string>? onEvent = null)
    {
        _store = store;
        _maxSizeBytes = maxSizeBytes;
        _clock = clock ?? TimeProvider.System;
        _onEvent = onEvent;
        _timer = new Timer(_ => Tick(), null, interval, interval);
    }

    /// <summary>Cache clears forced by the size cap since start (observability/tests).</summary>
    public long ClearedOversizeCount => Interlocked.Read(ref _clearedOversizeCount);

    /// <summary>One maintenance pass — exposed so a test can drive it on a fake clock.</summary>
    public void Tick()
    {
        var now = _clock.GetUtcNow().UtcTicks;
        try
        {
            _store.SweepExpired(now);
            if (_maxSizeBytes > 0 && _store.ApproximateSizeBytes() > _maxSizeBytes)
            {
                _store.Clear();
                Interlocked.Increment(ref _clearedOversizeCount);
                _onEvent?.Invoke($"cache-cleared-oversize at={now}");
            }
        }
        catch
        {
            // A sweep failure must never take down the broker; the next tick retries.
        }
    }

    public void Dispose() => _timer.Dispose();
}
