namespace Highway.Server.Observability;

/// <summary>
/// Drives the flight recorder's periodic reclamation (feature 002).
///
/// <para><b>An explicit timer, not a hosted service.</b> <c>HighwayServer</c> is
/// a plain wrapper around <c>GarnetServer</c> with no generic host, so there is
/// no <c>IHostedService</c> to hang this on. The timer is owned by
/// <see cref="FlightRecorder"/> and disposed with it, so it cannot outlive the
/// server that created it.</para>
///
/// <para>Sweeping is memory reclamation only. Retention is already enforced at
/// read, so nothing about correctness depends on this having run recently.</para>
/// </summary>
internal sealed class RecorderSweeper : IDisposable
{
    private readonly FlightRecorder _recorder;
    private readonly Timer _timer;
    private int _running;

    public RecorderSweeper(FlightRecorder recorder, TimeSpan interval)
    {
        _recorder = recorder;
        _timer = new Timer(OnTick, state: null, interval, interval);
    }

    private void OnTick(object? _)
    {
        // Skip rather than queue if a sweep is still going: overlapping sweeps
        // would contend for the same buffer locks to no benefit.
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;

        try
        {
            _recorder.Sweep();
        }
        catch (Exception)
        {
            // A failed sweep must not stop the timer. Memory reclamation is
            // best-effort; the next tick tries again.
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
