namespace Highway.Client.Engine;

/// <summary>
/// Decides whether a convergence re-sends pending calls (074, T8): only after a real master change, and once per
/// change. The connection counts changes — its source raises <c>MasterChanged</c> only when the endpoint differs — and
/// a convergence on the same master (a blip that recovered in place, or a second failing command finding the
/// connection already restored) re-sends nothing. Re-sending on every reconnect turned connection blips into
/// duplicate work (as 2.5 clients did).
/// </summary>
internal sealed class ReplayGate
{
    private long _replayed;

    /// <summary>True exactly once for each new value of <paramref name="masterChangeCount"/>.</summary>
    public bool ShouldReplay(long masterChangeCount)
    {
        while (true)
        {
            var seen = Interlocked.Read(ref _replayed);
            if (masterChangeCount <= seen) return false;
            if (Interlocked.CompareExchange(ref _replayed, masterChangeCount, seen) == seen) return true;
        }
    }
}
