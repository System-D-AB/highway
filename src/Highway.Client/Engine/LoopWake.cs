namespace Highway.Client.Engine;

/// <summary>
/// Wake signal for worker/consumer loops. Doorbells and the backstop sweeper
/// signal it; the loop awaits it between drain passes. Signalling is lossy by
/// design — one pending signal is enough, the drain pass always runs to nil.
/// </summary>
internal sealed class LoopWake
{
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>Idempotent wake: at most one pending signal is kept.</summary>
    public void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Loop already shut down.
        }
    }

    /// <summary>
    /// Waits for a wake. The timeout is self-healing insurance: even if every
    /// doorbell AND the sweeper were lost, the loop re-drains periodically.
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _signal.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }
}
