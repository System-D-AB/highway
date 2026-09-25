using System.Collections.Concurrent;

namespace Highway.Server.Storage.Boxes;

/// <summary>
/// Process-local state for the box layout (065): the catalog cache (name → box id) and the time floors of
/// the time-ordered spaces (lease, delayed, schedule index).
///
/// <para><b>Floors.</b> A floor is a lower bound on every live key in its space, so a scan starts there
/// instead of at the space's first key and never walks the deleted history below it. It is kept here,
/// not in the store: time-ordered keys are written with the broker's clock, and a durable floor would lose
/// entries if the clock ever moved back (another node after a failover, an NTP step). The discipline is
/// 061's head hint — <see cref="Lower"/> on every insert below the floor, <see cref="Raise"/> only after a
/// committed scan — and <see cref="Reset"/> on any epoch change. Every mutation of a box's floors happens
/// under that box's command lock, so plain dictionary writes suffice.</para>
/// </summary>
internal sealed class BoxRuntime
{
    private readonly ConcurrentDictionary<(BoxKeys.Kind Kind, string Name), uint> _ids = new();
    private readonly ConcurrentDictionary<(uint Box, byte Space), long> _floors = new();
    private readonly ConcurrentDictionary<uint, (BoxKeys.Kind Kind, string Name)> _meta = new();

    public bool TryGetMeta(uint box, out (BoxKeys.Kind Kind, string Name) meta) => _meta.TryGetValue(box, out meta);

    public void RememberMeta(uint box, BoxKeys.Kind kind, string name) => _meta[box] = (kind, name);

    public bool TryGetId(BoxKeys.Kind kind, string name, out uint box) => _ids.TryGetValue((kind, name), out box);

    public void Remember(BoxKeys.Kind kind, string name, uint box) => _ids[(kind, name)] = box;

    public void Forget(BoxKeys.Kind kind, string name, uint box)
    {
        _ids.TryRemove((kind, name), out _);
        _meta.TryRemove(box, out _);
        foreach (var key in _floors.Keys)
            if (key.Box == box) _floors.TryRemove(key, out _);
    }

    public long Floor(uint box, byte space) => _floors.TryGetValue((box, space), out var f) ? f : 0;

    /// <summary>An insert at <paramref name="ticks"/>: the floor may only go down, immediately.</summary>
    public void Lower(uint box, byte space, long ticks)
    {
        if (_floors.TryGetValue((box, space), out var f) && ticks < f)
            _floors[(box, space)] = Math.Max(0, ticks);
    }

    /// <summary>After a committed scan found nothing live below <paramref name="ticks"/>.</summary>
    public void Raise(uint box, byte space, long ticks)
    {
        var key = (box, space);
        if (!_floors.TryGetValue(key, out var f) || ticks > f)
            _floors[key] = ticks;
    }

    /// <summary>Epoch change or store replacement: every cached id and floor is suspect.</summary>
    public void Reset()
    {
        _ids.Clear();
        _floors.Clear();
        _meta.Clear();
    }
}
