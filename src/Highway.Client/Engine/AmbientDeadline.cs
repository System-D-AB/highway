using System.Diagnostics;

namespace Highway.Client.Engine;

/// <summary>
/// The deadline of the RPC request whose handler is running on this async flow (074, D6). A call a handler makes
/// to another service inherits it: its timeout becomes <c>min(its own, the time left here)</c>, so an inner call
/// never outlives the caller waiting on the outer one.
///
/// <para>Kept as a local monotonic timestamp (<see cref="Stopwatch"/>), never wall-clock time: the remaining time the
/// broker sent is relative, and so is everything derived from it here. No clock is compared with another
/// machine's.</para>
///
/// <para>The flow holds a <b>holder</b>, cleared when the handler ends: work the handler started that outlives it (a
/// fire-and-forget task, a timer, a background loop first resolved inside it) captured the holder, not the value,
/// and so stops seeing a deadline that no longer belongs to anyone.</para>
/// </summary>
internal static class AmbientDeadline
{
    private sealed class Holder(long deadline, Holder? outer)
    {
        public long Deadline = deadline;   // Stopwatch timestamp; 0 once its handler has ended
        public readonly Holder? Outer = outer;
    }

    private static readonly AsyncLocal<Holder?> Current = new();

    /// <summary>The time left before the enclosing handler's deadline, or null outside a (still running) handler.</summary>
    public static TimeSpan? Remaining
    {
        get
        {
            for (var h = Current.Value; h is not null; h = h.Outer)
            {
                var deadline = Volatile.Read(ref h.Deadline);
                if (deadline == 0) continue;   // that handler has ended
                var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
            return null;
        }
    }

    /// <summary>Sets the deadline for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable Enter(TimeSpan remaining)
    {
        var outer = Current.Value;
        var deadline = Stopwatch.GetTimestamp() + (long)(remaining.TotalSeconds * Stopwatch.Frequency);
        // An inner scope never extends an outer one that is still live.
        for (var h = outer; h is not null; h = h.Outer)
        {
            var d = Volatile.Read(ref h.Deadline);
            if (d != 0) { if (d < deadline) deadline = d; break; }
        }
        var holder = new Holder(deadline, outer);
        Current.Value = holder;
        return new Scope(holder, outer);
    }

    private sealed class Scope(Holder holder, Holder? outer) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref holder.Deadline, 0);   // every captured copy now sees "no deadline"
            Current.Value = outer;
        }
    }
}
