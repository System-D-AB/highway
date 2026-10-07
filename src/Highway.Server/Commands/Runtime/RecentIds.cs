namespace Highway.Server.Commands.Runtime;

/// <summary>
/// Remembers IDs recently admitted by this broker, each until its own expiry, bounded by capacity. Keyed by
/// (scope, id), with an associated value (e.g., the group count for a publish).
///
/// <para>Process-local, per server process, never persisted or replicated. An id is remembered for
/// <see cref="Window"/>, or until the expiry it was admitted with (074: a call is remembered until its own
/// deadline). Past the capacity the soonest-expiring ids are forgotten first (may let a duplicate through, never
/// drops a new message).</para>
/// </summary>
internal sealed class RecentIds<TValue>
{
    public const int DefaultCapacity = 200_000;

    private readonly object _gate = new();
    private readonly Dictionary<(string Scope, string Id), (long Version, long Expires, TValue Value)> _seen = [];
    private readonly PriorityQueue<(string Scope, string Id, long Version), long> _order = new();
    private readonly int _capacity;
    private long _version;
    private long _duplicates;

    public RecentIds(TimeSpan window, int capacity = DefaultCapacity)
    {
        Window = window;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>How long an id is remembered by default; zero or negative disables the filter.</summary>
    public TimeSpan Window { get; }

    /// <summary>Total duplicates suppressed by this instance (process counter for HW.STATS).</summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);

    /// <summary>Entries in the eviction order (diagnostics and tests): bounded at twice the capacity, even when ids are forgotten.</summary>
    internal int OrderLength
    {
        get { lock (_gate) return _order.Count; }
    }

    /// <summary>Explicitly records a duplicate (e.g. for TryGet hits).</summary>
    public void RecordDuplicate() => Interlocked.Increment(ref _duplicates);

    /// <summary>
    /// Checks whether (scope, id) is currently remembered, returning its stored value if so.
    /// </summary>
    public bool TryGet(string scope, string id, long nowTicks, out TValue value)
    {
        if (Window <= TimeSpan.Zero)
        {
            value = default!;
            return false;
        }

        lock (_gate)
        {
            Expire(nowTicks);
            if (_seen.TryGetValue((scope, id), out var entry) && nowTicks < entry.Expires)
            {
                value = entry.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Remembers (scope, id) with its value for <see cref="Window"/>. Called only <b>after</b> the write it stands
    /// for has committed, so a refused or failed write is never remembered and its retry is stored (074). There is
    /// deliberately no check-and-admit in one call: that would remember an id before its commit.
    /// </summary>
    public void Admit(string scope, string id, long nowTicks, TValue value)
        => Admit(scope, id, nowTicks, value, nowTicks + Window.Ticks);

    /// <summary>Remembers (scope, id) until <paramref name="expiresTicks"/> (074: a call, until its deadline).</summary>
    public void Admit(string scope, string id, long nowTicks, TValue value, long expiresTicks)
    {
        if (Window <= TimeSpan.Zero || expiresTicks <= nowTicks) return;
        lock (_gate)
        {
            Expire(nowTicks);
            var version = ++_version;
            _seen[(scope, id)] = (version, expiresTicks, value);
            _order.Enqueue((scope, id, version), expiresTicks);
            while ((_seen.Count > _capacity || _order.Count > 2 * _capacity) && _order.TryDequeue(out var oldest, out _))
            {
                if (_seen.TryGetValue((oldest.Scope, oldest.Id), out var e) && e.Version == oldest.Version)
                    _seen.Remove((oldest.Scope, oldest.Id));
            }
        }
    }

    /// <summary>Forgets one id — a call that left the broker (dead-lettered, expired, cancelled).</summary>
    public void Forget(string scope, string id)
    {
        lock (_gate) _seen.Remove((scope, id));
    }

    /// <summary>Forgets every id — on an epoch change, when what this broker stored may not have survived.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _seen.Clear();
            _order.Clear();
        }
    }

    /// <summary>Ids currently remembered (diagnostics and tests).</summary>
    public int Count
    {
        get { lock (_gate) return _seen.Count; }
    }

    private void Expire(long nowTicks)
    {
        while (_order.TryPeek(out var head, out var expires) && expires <= nowTicks)
        {
            _order.Dequeue();
            if (_seen.TryGetValue((head.Scope, head.Id), out var e) && e.Version == head.Version)
                _seen.Remove((head.Scope, head.Id));
        }
    }
}
