namespace Highway.Server.Storage.Rocks;

/// <summary>
/// The herd contract's no-split hinge (042-1a D1): when may a standby answer a client's
/// handshake "willing"?
///
/// <para>Willing iff priority ≠ 0 <b>and</b> the standby has itself lost the master — no
/// successful exchange for at least the willingness threshold <c>W</c> — or the master
/// promised to leave (GOODBYE). A standby whose master-link is healthy answers unwilling,
/// whatever any client believes.</para>
///
/// <para>Promotion therefore requires two independent observers of the loss — the client
/// (its own trigger) and the standby (its dead link). This is 042's witness role absorbed
/// into the topology with no third process, and the reason the witness apparatus was
/// deleted (042-1a reconciliation map).</para>
/// </summary>
internal static class Willingness
{
    /// <summary>Per-priority-unit stagger on the willingness threshold (042-1d): the higher-priority successor turns willing first, so the herd converges on ONE node.</summary>
    public static readonly TimeSpan StaggerPerPriority = TimeSpan.FromMilliseconds(400);

    /// <summary>Priority beyond which the stagger stops growing — keeps a large priority number from an absurd wait.</summary>
    private const int StaggerCap = 20;

    /// <summary>
    /// Pure decision — the truth table A-T4 pins. <paramref name="hasSeenMaster"/>
    /// (amendment 2026-09-16): a standby that has NEVER reached its master is not one
    /// that <i>lost</i> it — "not found yet" at startup must not read as "dead", or a
    /// slow-starting standby promotes under a living master. Such a standby waits the
    /// larger <paramref name="fenceTimeout"/> window instead of <paramref name="threshold"/> —
    /// a genuinely dead-master bootstrap still becomes willing, just on the outer bound.
    ///
    /// <para><b>Priority stagger (042-1d).</b> When several standbys lose the master at
    /// once, they must not all turn willing in the same instant, or clients walking the
    /// roster can land on different successors (a split). Each node adds
    /// <c>priority × StaggerPerPriority</c> to its threshold, so the highest-priority
    /// (lowest number) successor becomes willing first and the whole herd converges on
    /// it; a lower-priority node turns willing only if the higher one never does (it, too,
    /// is dead). This is the promotion-ordering role of 042's deleted stagger, moved to
    /// its correct home — the willingness gate, not a promote timer.</para>
    /// </summary>
    public static bool Decide(
        int priority, TimeSpan masterSilence, bool goodbyeSeen, TimeSpan threshold,
        bool hasSeenMaster, TimeSpan fenceTimeout)
    {
        if (priority == 0) return false;
        if (goodbyeSeen) return true;
        var baseWindow = hasSeenMaster ? threshold : fenceTimeout;
        var effective = baseWindow + StaggerPerPriority * Math.Min(priority, StaggerCap);
        return masterSilence >= effective;
    }
}
