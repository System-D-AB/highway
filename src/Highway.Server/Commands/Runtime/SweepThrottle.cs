using System.Collections.Concurrent;

namespace Highway.Server.Commands.Runtime;

/// <summary>
/// Rate-limits the lease sweep (061 A2). <c>HW.DEQUEUE</c> and <c>HW.QCLAIM</c> used to sweep every
/// worker's processing list on <b>every</b> poll — and an idle worker polls several times a second per
/// loop — while a lease is measured in minutes. Sweeping a queue at most once per
/// <c>min(1 s, Lease / 10)</c> keeps expiry detection prompt relative to the lease and removes almost
/// all of the sweep's reads from the poll path.
///
/// <para>Process-local, per server, never persisted: it is a throttle, not state. Losing it (a restart,
/// a promotion) only means the next poll sweeps. Absent on a <see cref="CommandContext"/> (in-process
/// command tests) means "sweep every time", which is the previous behaviour.</para>
/// </summary>
internal sealed class SweepThrottle
{
    private static readonly long MaxIntervalTicks = TimeSpan.FromSeconds(1).Ticks;

    private readonly ConcurrentDictionary<string, long> _lastSweep = new(StringComparer.Ordinal);

    /// <summary>True when <paramref name="scope"/> (a queue or service) is due a sweep at <paramref name="nowTicks"/>; records it.</summary>
    public bool ShouldSweep(string scope, long nowTicks, TimeSpan lease)
    {
        var interval = Math.Min(MaxIntervalTicks, lease.Ticks / 10);
        while (true)
        {
            if (!_lastSweep.TryGetValue(scope, out var last))
            {
                if (_lastSweep.TryAdd(scope, nowTicks)) return true;
                continue;
            }

            if (nowTicks - last < interval) return false;
            if (_lastSweep.TryUpdate(scope, nowTicks, last)) return true;
        }
    }
}
