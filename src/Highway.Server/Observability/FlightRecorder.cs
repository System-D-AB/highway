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

    // --- Observer infrastructure (feature 011, T2) ---
    private volatile IRecorderObserver[] _observers = [];
    private long _observerFailures;

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
    /// Subscriber groups retired and messages discarded with them (feature 017). Cumulative.
    ///
    /// <para>Counted here rather than logged from the command, because a command runs inside a
    /// Garnet transaction and has no logger. Retirement is the largest single loss Highway can
    /// inflict, so it must be countable even when nobody was watching the replay.</para>
    /// </summary>
    public long GroupsRetired => Interlocked.Read(ref _groupsRetired);

    /// <inheritdoc cref="GroupsRetired"/>
    public long MessagesDiscarded => Interlocked.Read(ref _messagesDiscarded);

    /// <summary>
    /// Sends and publishes refused at a queue's byte limit (feature 016 R4.6). Cumulative.
    /// </summary>
    public long SendsRefused => Interlocked.Read(ref _sendsRefused);

    private long _groupsRetired;
    private long _messagesDiscarded;
    private long _sendsRefused;

    /// <summary>Observer failure count, surfaced in snapshots.</summary>
    internal long ObserverFailures => Interlocked.Read(ref _observerFailures);

    // --- T2: Observer contract ---

    /// <summary>Non-blocking notification interface for live event streaming.</summary>
    internal interface IRecorderObserver
    {
        /// <summary>
        /// Called on the recording path. MUST NOT block, throw, or do work proportional to the event.
        /// </summary>
        void OnRecorded(in HighwayEvent evt);
    }

    internal void Subscribe(IRecorderObserver observer)
    {
        lock (_buffers) // reuse existing lock-free-for-reads structure; subscription is rare
        {
            _observers = [.. _observers, observer];
        }
    }

    internal void Unsubscribe(IRecorderObserver observer)
    {
        lock (_buffers)
        {
            _observers = _observers.Where(o => o != observer).ToArray();
        }
    }

    // --- T1: Name enumeration ---

    /// <summary>Snapshot of one recorded name's state.</summary>
    internal readonly record struct RecorderName(
        string Name, int Count, long Bytes, PayloadCapture Capture, long DroppedCapacity);

    /// <summary>
    /// Enumerates names the recorder holds events for.
    /// Returns a weakly-consistent snapshot — acceptable for a diagnostic view.
    /// </summary>
    internal IReadOnlyList<RecorderName> Names()
    {
        var result = new List<RecorderName>();
        foreach (var (name, buffer) in _buffers)
        {
            if (buffer is not null)
                result.Add(new RecorderName(name, buffer.Count, buffer.Bytes, buffer.Capture, buffer.DroppedCapacity));
        }
        return result;
    }

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
        // Counted BEFORE the enabled check: a retirement that happened must be countable even
        // on a broker whose recorder is switched off. The count is not diagnostics, it is the
        // receipt for a destructive act.
        if (eventType == HighwayEventType.SendRefused)
            Interlocked.Increment(ref _sendsRefused);

        if (eventType == HighwayEventType.GroupRetired)
        {
            Interlocked.Increment(ref _groupsRetired);
            if (count is { } discarded)
                Interlocked.Add(ref _messagesDiscarded, discarded);
        }

        if (!_options.RecorderEnabled)
            return;

        try
        {
            var buffer = ResolveBuffer(name);
            if (buffer is null)
                return; // name disabled — a dictionary miss and nothing more

            var capturedPayload = buffer.Capture == PayloadCapture.Full ? payload : null;

            var evt = new HighwayEvent
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
            };

            buffer.Append(evt);

            // Notify observers (T2). With zero observers this is one volatile
            // read and a length check — no allocation, no lock.
            var observers = _observers;
            if (observers.Length != 0)
                NotifyObservers(observers, in evt);
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
            Failures: Failures,
            ObserverFailures: ObserverFailures);
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
    /// Notifies observers, isolating each so one bad observer cannot prevent
    /// the rest from being notified. Separate non-inlined method so the common
    /// (no-observer) path stays small.
    /// </summary>
    private void NotifyObservers(IRecorderObserver[] observers, in HighwayEvent evt)
    {
        foreach (var observer in observers)
        {
            try { observer.OnRecorded(in evt); }
            catch (Exception) { Interlocked.Increment(ref _observerFailures); }
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

    /// <summary>
    /// The effective capture mode for <paramref name="name"/> — the per-name override if there
    /// is one, otherwise the default.
    ///
    /// <para>Exposed so <c>HW.FAIL</c> can honour the <b>same</b> switch (015 R3.5). An
    /// exception message routinely contains application data, so a name whose payloads are
    /// withheld must not have that data arrive through the failure path instead. A second
    /// setting would be a second thing to get wrong.</para>
    /// </summary>
    public PayloadCapture CaptureFor(string name)
    {
        _options.Overrides.TryGetValue(name, out var over);
        return over?.Capture ?? _options.DefaultCapture;
    }

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
    long Failures,
    long ObserverFailures);
