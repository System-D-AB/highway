using Highway.Abstractions.Observability;

namespace Highway.Server.Observability;

/// <summary>
/// The bounded record of one name's recent operations (feature 002).
///
/// <para>A fixed-capacity circular buffer with its own lock. Per-name buffers
/// rather than one shared structure is what makes per-name retention possible
/// at all — and it buys isolation: a chatty health check cannot evict the
/// history of the service you are actually debugging.</para>
///
/// <para><b>Retention is enforced at read, not only by the sweep.</b> A stale
/// event is never returned merely because the sweeper has not run yet. The
/// sweep exists to reclaim memory; correctness does not depend on its timing.</para>
/// </summary>
internal sealed class NameBuffer
{
    private readonly Lock _gate = new();
    private readonly HighwayEvent?[] _ring;

    private int _next;          // where the next append goes
    private int _count;         // events currently held
    private long _bytes;        // approximate retained bytes

    public NameBuffer(string name, int capacity, TimeSpan retention, PayloadCapture capture)
    {
        Name = name;
        Capacity = capacity;
        Retention = retention;
        Capture = capture;
        _ring = new HighwayEvent?[capacity];
    }

    public string Name { get; }
    public int Capacity { get; }
    public TimeSpan Retention { get; }
    public PayloadCapture Capture { get; }

    /// <summary>Events currently held, including any past retention not yet swept.</summary>
    public int Count { get { lock (_gate) return _count; } }

    /// <summary>Approximate retained bytes.</summary>
    public long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Events dropped because the buffer was full. Cumulative.</summary>
    public long DroppedCapacity { get; private set; }

    /// <summary>
    /// Appends an event, dropping the oldest when full. Never rejects, never
    /// blocks on anything but this buffer's own lock.
    /// </summary>
    public void Append(HighwayEvent evt)
    {
        lock (_gate)
        {
            var displaced = _ring[_next];
            if (displaced is not null)
            {
                _bytes -= displaced.ApproximateBytes;
                DroppedCapacity++;
            }

            _ring[_next] = evt;
            _bytes += evt.ApproximateBytes;
            _next = (_next + 1) % Capacity;

            if (_count < Capacity)
                _count++;
        }
    }

    /// <summary>
    /// Reads events in chronological order, filtered by time window, node, and
    /// this buffer's retention. Retention is applied here so an unswept stale
    /// event is still invisible.
    /// </summary>
    public IReadOnlyList<HighwayEvent> Read(
        DateTimeOffset from,
        DateTimeOffset to,
        string? nodeId,
        int limit,
        DateTimeOffset now)
    {
        var cutoff = Retention > TimeSpan.Zero ? now - Retention : DateTimeOffset.MinValue;
        var results = new List<HighwayEvent>(Math.Min(limit, 64));

        lock (_gate)
        {
            // Oldest first: when full the oldest sits at _next, otherwise at 0.
            var start = _count == Capacity ? _next : 0;

            for (var i = 0; i < _count; i++)
            {
                var evt = _ring[(start + i) % Capacity];
                if (evt is null) continue;
                if (evt.Timestamp < cutoff) continue;      // past retention
                if (evt.Timestamp < from || evt.Timestamp > to) continue;
                if (nodeId is not null && !string.Equals(evt.NodeId, nodeId, StringComparison.Ordinal)) continue;

                results.Add(evt);
                if (results.Count >= limit) break;
            }
        }

        return results;
    }

    /// <summary>
    /// Drops events past retention and reports the bytes reclaimed. Called by
    /// the sweeper; correctness never depends on it having run.
    /// </summary>
    public long SweepExpired(DateTimeOffset now)
    {
        if (Retention <= TimeSpan.Zero)
            return 0;

        var cutoff = now - Retention;
        long reclaimed = 0;

        lock (_gate)
        {
            for (var i = 0; i < Capacity; i++)
            {
                var evt = _ring[i];
                if (evt is null || evt.Timestamp >= cutoff) continue;

                reclaimed += evt.ApproximateBytes;
                _bytes -= evt.ApproximateBytes;
                _ring[i] = null;
                _count--;
            }
        }

        return reclaimed;
    }

    /// <summary>
    /// Drops the oldest events until at most <paramref name="targetBytes"/>
    /// remain. Used by the global budget sweep, which trims the largest buffers
    /// first. Returns bytes reclaimed.
    /// </summary>
    public long TrimTo(long targetBytes)
    {
        long reclaimed = 0;

        lock (_gate)
        {
            var start = _count == Capacity ? _next : 0;

            for (var i = 0; i < Capacity && _bytes > targetBytes; i++)
            {
                var index = (start + i) % Capacity;
                var evt = _ring[index];
                if (evt is null) continue;

                reclaimed += evt.ApproximateBytes;
                _bytes -= evt.ApproximateBytes;
                _ring[index] = null;
                _count--;
            }
        }

        return reclaimed;
    }
}
