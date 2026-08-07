using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// The single periodic sweep that makes doorbells a pure latency optimization:
/// (a) GETs reply slots of pending calls older than the grace window, and
/// (b) signals every worker/consumer loop to run a drain pass.
///
/// <para>Cheap when idle: with no pending calls the registry sweep performs
/// zero network I/O; loop signals are in-memory. The sweeper never dies —
/// internal errors are logged and the next iteration proceeds.</para>
/// </summary>
internal sealed class BackstopSweeper
{
    private readonly PendingCallRegistry _registry;
    private readonly TimeSpan _interval;
    private readonly IReadOnlyList<LoopWake> _loopWakes;
    private readonly ILogger<BackstopSweeper> _logger;

    public BackstopSweeper(
        PendingCallRegistry registry,
        TimeSpan interval,
        IReadOnlyList<LoopWake> loopWakes,
        ILogger<BackstopSweeper> logger)
    {
        _registry = registry;
        _interval = interval;
        _loopWakes = loopWakes;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Backstop sweeper started (interval {Interval})", _interval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                // A doorbell may have been dropped any time since registration;
                // use the sweep interval as the grace window.
                await _registry.SweepAsync(_interval, ct).ConfigureAwait(false);

                foreach (var wake in _loopWakes)
                    wake.Signal();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backstop sweep iteration failed; continuing on next tick");
            }
        }

        _logger.LogInformation("Backstop sweeper stopped");
    }
}
