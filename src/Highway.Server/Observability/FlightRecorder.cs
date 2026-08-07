using System.Collections.Concurrent;
using Highway.Abstractions.Observability;

namespace Highway.Server.Observability;

/// <summary>
/// The flight recorder: a bounded, in-process, <b>volatile</b> record of recent
/// operations, queried with <c>HW.REPLAY</c> and measured with
/// <c>HW.STATS RECORDER</c> (feature 002).
///
/// <para><b>Volatile by design.</b> Contents are lost when the server stops.
/// Storing events in the Garnet keyspace instead would put them in the AOF,
/// where recovery would replay them with replay-time timestamps and fabricate
/// history on every restart — and would make the recorder compete with the
/// actual queues for the same store. Anyone needing durable retention wants the
/// OpenTelemetry path, exported to a system built for it.</para>
///
/// <para><b>Recording never fails an operation.</b> <see cref="Record"/> catches
/// everything, counts the failure, and returns. A flight recorder that can break
/// the system it observes is worse than none.</para>
/// </summary>
internal sealed class FlightRecorder : IDisposable
{
    private readonly ObservabilityOptions _options;
    private readonly ConcurrentDictionary<string, NameBuffer?> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly RecorderSweeper? _sweeper;

    private long _failures;
    private long _droppedBudget;

    public FlightRecorder(ObservabilityOptions options)
    {
        _options = options;

        if (options.RecorderEnabled)
            _sweeper = new RecorderSweeper(this, options.SweepInterval);
    }

    public bool Enabled => _options.RecorderEnabled;
    public ObservabilityOptions Options => _options;

    /// <summary>Recording failures swallowed. Cumulative since start.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Events reclaimed to stay inside the global byte budget. Cumulative.</summary>
    public long DroppedBudget => Interlocked.Read(ref _droppedBudget);

    /// <summary>
    /// Records one operation. Called from a command's <c>Finalize</c> — after
    /// the transaction commits, before the reply is written.
    ///
    /// <para>The whole write path is: resolve the buffer, allocate one event,
    /// append. No serialization (that happens only when <c>HW.REPLAY</c> reads),
    /// and no payload copy — under <see cref="PayloadCapture.Full"/> the
    /// recorder holds a reference to the array the command already owns.</para>
    /// </summary>
    public void Record(
        HighwayEventType eventType,
        string name,
        string? nodeId = null,
        string? requestId = null,
        long? messageId = null,
        byte[]? payload = null,
        string? errorCode = null,
        int? count = null)
    {
        if (!_options.RecorderEnabled)
            return;

        try
        {
            var buffer = ResolveBuffer(name);
            if (buffer is null)
                return; // name disabled — a dictionary miss and nothing more

            var capturedPayload = buffer.Capture == PayloadCapture.Full ? payload : null;

            buffer.Append(new HighwayEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = eventType,
                Name = name,
                NodeId = nodeId,
                RequestId = requestId,
                MessageId = messageId,
                Payload = capturedPayload,
                PayloadSize = payload?.Length ?? 0,
                ErrorCode = errorCode,
                Count = count,
            });
        }
        catch (Exception)
        {
            // An operation must never fail because recording did.
            Interlocked.Increment(ref _failures);
        }
    }

    /// <summary>Reads one name's events. Returns empty for an unknown or disabled name — never an error.</summary>
    public IReadOnlyList<HighwayEvent> Read(
        string name, DateTimeOffset from, DateTimeOffset to, string? nodeId, int limit)
    {
        if (!_options.RecorderEnabled)
            return [];

        return _buffers.TryGetValue(name, out var buffer) && buffer is not null
            ? buffer.Read(from, to, nodeId, limit, DateTimeOffset.UtcNow)
            : [];
    }

    /// <summary>Current recorder state, for <c>HW.STATS RECORDER</c>.</summary>
    public RecorderSnapshot Snapshot()
    {
        long events = 0, bytes = 0, droppedCapacity = 0;
        var names = 0;

        foreach (var buffer in _buffers.Values)
        {
            if (buffer is null) continue;
            names++;
            events += buffer.Count;
            bytes += buffer.Bytes;
            droppedCapacity += buffer.DroppedCapacity;
        }

        return new RecorderSnapshot(
            Enabled: _options.RecorderEnabled,
            Names: names,
            Events: events,
            Bytes: bytes,
            DroppedCapacity: droppedCapacity,
            DroppedBudget: DroppedBudget,
            Failures: Failures);
    }

    /// <summary>
    /// Reclaims expired events, then enforces the global budget by trimming the
    /// largest buffers first. Driven by <see cref="RecorderSweeper"/>.
    /// </summary>
    internal void Sweep()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var buffer in _buffers.Values)
            buffer?.SweepExpired(now);

        var total = 0L;
        foreach (var buffer in _buffers.Values)
            total += buffer?.Bytes ?? 0;

        if (total <= _options.MaxBytes)
            return;

        // Over budget: trim the largest first, so one runaway name is reduced
        // before quiet ones lose anything.
        var ordered = _buffers.Values
            .Where(b => b is not null)
            .Select(b => b!)
            .OrderByDescending(b => b.Bytes)
            .ToList();

        foreach (var buffer in ordered)
        {
            if (total <= _options.MaxBytes) break;

            var target = Math.Max(0, buffer.Bytes / 2);
            var reclaimed = buffer.TrimTo(target);
            total -= reclaimed;

            if (reclaimed > 0)
                Interlocked.Add(ref _droppedBudget, 1);
        }
    }

    /// <summary>
    /// Resolves (and caches) the buffer for a name. Returns null when the name
    /// is configured off, so a disabled name never allocates.
    /// </summary>
    private NameBuffer? ResolveBuffer(string name)
        => _buffers.GetOrAdd(name, static (key, opts) =>
        {
            opts.Overrides.TryGetValue(key, out var over);

            var capture = over?.Capture ?? opts.DefaultCapture;
            var capacity = over?.Capacity ?? opts.DefaultCapacity;
            var retention = over?.Retention ?? opts.DefaultRetention;

            if (capture == PayloadCapture.Off || capacity <= 0 || retention <= TimeSpan.Zero)
                return null;   // disabled: no buffer, no allocation

            return new NameBuffer(key, capacity, retention, capture);
        }, _options);

    public void Dispose() => _sweeper?.Dispose();
}

/// <summary>Point-in-time recorder state.</summary>
internal readonly record struct RecorderSnapshot(
    bool Enabled,
    int Names,
    long Events,
    long Bytes,
    long DroppedCapacity,
    long DroppedBudget,
    long Failures);
