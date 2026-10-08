namespace Highway.Integration.Tests;

/// <summary>
/// Starves the thread pool for a test's duration: <c>HW_TEST_POOL_HOG=&lt;n&gt;</c> keeps n pool threads blocked
/// (069 R4 — the herd must not split when probes are slow because the pool is busy). Inactive when unset.
/// </summary>
internal sealed class PoolHog : IDisposable
{
    private volatile bool _stop;

    private PoolHog(int threads)
    {
        for (var i = 0; i < threads; i++)
            _ = Task.Run(() => { while (!_stop) Thread.Sleep(50); });
    }

    public static PoolHog? FromEnvironment()
        => int.TryParse(Environment.GetEnvironmentVariable("HW_TEST_POOL_HOG"), out var n) && n > 0 ? new PoolHog(n) : null;

    public void Dispose() => _stop = true;
}
