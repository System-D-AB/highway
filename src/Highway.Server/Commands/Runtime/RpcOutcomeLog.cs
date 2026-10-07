using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Commands.Runtime;

/// <summary>
/// The broker log for RPC requests that ended without an answer (074): one Warning line per service per
/// <c>RpcLogInterval</c> — "N expired, M failed after their last attempt (last: …)" — never one line per request,
/// because an outage expires a whole backlog at once. Also warns once per service when a requested timeout is
/// clamped. One per broker (the dispatcher holds it); process-local, never persisted.
/// </summary>
internal sealed class RpcOutcomeLog
{
    private sealed class Tally
    {
        public long Queue, Redelivery, Failed, Cancelled;
        public long TotalExpired, TotalFailed;   // since this broker process started (HW.STATS)
        public string? LastFailure;
        public long LastLoggedTicks = long.MinValue;
    }

    private readonly ConcurrentDictionary<string, Tally> _tallies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _clampWarned = new(StringComparer.Ordinal);
    private ILogger? _logger;
    private TimeSpan _interval;
    private Timer? _timer;

    /// <summary>
    /// Logs what accumulated since the last line once its interval has passed, even if no further request ends —
    /// the tail of an outage would otherwise wait for an event that may never come. Started by the first event.
    /// </summary>
    private void EnsureTimer(ILogger? logger, TimeSpan interval)
    {
        if (logger is null || _timer is not null) return;
        _logger = logger;
        _interval = interval;
        var timer = new Timer(_ => FlushAll(), null, interval, interval);
        if (Interlocked.CompareExchange(ref _timer, timer, null) is not null) timer.Dispose();
    }

    private void FlushAll()
    {
        try
        {
            var now = DateTime.UtcNow.Ticks;
            foreach (var (service, t) in _tallies)
                lock (t)
                    if (t.Queue + t.Redelivery + t.Failed > 0) FlushIfDue(_logger, service, t, now, _interval);
        }
        catch
        {
            // A broken log sink must not fault a timer thread (C7.1).
        }
    }

    /// <summary>Warns once per service (per broker process) that a requested timeout was clamped.</summary>
    public void Clamped(ILogger? logger, string service, TimeSpan requested, TimeSpan max)
    {
        if (logger is null || !_clampWarned.TryAdd(service, 0)) return;
        logger.LogWarning(
            "[rpc] '{Service}' asked for a {Requested} timeout; clamped to MaxRpcTimeout {Max}. Logged once per service.",
            service, requested, max);
    }

    /// <summary>Counts requests that expired (stage queue or redelivery) and logs the summary when it is due.</summary>
    public void Expired(ILogger? logger, string service, string stage, long count, long nowTicks, TimeSpan interval)
    {
        if (count <= 0) return;
        EnsureTimer(logger, interval);
        var t = _tallies.GetOrAdd(service, _ => new Tally());
        lock (t)
        {
            if (stage == "redelivery") t.Redelivery += count; else t.Queue += count;
            t.TotalExpired += count;
            FlushIfDue(logger, service, t, nowTicks, interval);
        }
    }

    /// <summary>Counts a request that failed on its last attempt, keeping the last failure for the summary.</summary>
    public void FailedFinal(ILogger? logger, string service, string? failure, long nowTicks, TimeSpan interval)
    {
        EnsureTimer(logger, interval);
        var t = _tallies.GetOrAdd(service, _ => new Tally());
        lock (t)
        {
            t.Failed++;
            t.TotalFailed++;
            if (failure is not null) t.LastFailure = failure;
            FlushIfDue(logger, service, t, nowTicks, interval);
        }
    }

    /// <summary>Counts a request withdrawn by its caller (<c>HW.CANCEL</c>).</summary>
    public void Cancelled(string service)
    {
        var t = _tallies.GetOrAdd(service, _ => new Tally());
        lock (t) t.Cancelled++;
    }

    /// <summary>Requests of <paramref name="service"/> that ended unanswered since this broker process started.</summary>
    public (long Expired, long FailedFinal, long Cancelled) Totals(string service)
    {
        if (!_tallies.TryGetValue(service, out var t)) return default;
        lock (t) return (t.TotalExpired, t.TotalFailed, t.Cancelled);
    }

    private static void FlushIfDue(ILogger? logger, string service, Tally t, long nowTicks, TimeSpan interval)
    {
        // The first event logs at once (an outage is visible as it starts); later ones accumulate until an
        // interval has passed since the last line, and are logged with the next event after that.
        if (t.LastLoggedTicks != long.MinValue && nowTicks - t.LastLoggedTicks < interval.Ticks) return;
        if (logger is not null)
        {
            logger.LogWarning(
                "[rpc] '{Service}': {Expired} request(s) expired unanswered (in queue {Queue}, on redelivery {Redelivery}), " +
                "{Failed} failed after their last attempt{Last}. Expired requests never run; nothing is dead-lettered.",
                service, t.Queue + t.Redelivery, t.Queue, t.Redelivery, t.Failed,
                t.LastFailure is null ? "" : $" (last: {t.LastFailure})");
        }
        t.Queue = t.Redelivery = t.Failed = 0;
        t.LastFailure = null;
        t.LastLoggedTicks = nowTicks;
    }
}
