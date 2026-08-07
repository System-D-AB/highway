using Garnet.server;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// Dead-letter helpers shared by the two commands that can dead-letter (feature 013):
/// <c>HW.DEQUEUE</c>'s RPC lease sweep and <c>HW.RECEIVE</c>'s group lease sweep.
///
/// <para>Both sweeps had the same defect and both need the same bound, so the cap lives
/// here rather than being written twice with two chances to drift.</para>
/// </summary>
internal abstract partial class HighwayCommandBase
{
    /// <summary>
    /// Entries this invocation discarded from a dead-letter list to stay inside
    /// <see cref="HighwayServerOptions.MaxDeadLetterEntries"/>.
    /// </summary>
    protected int DeadLettersDropped { get; private set; }

    /// <summary>Resets the drop counter. Called from the sweep's owning command.</summary>
    protected void ResetDeadLetterCounters() => DeadLettersDropped = 0;

    /// <summary>
    /// Enforces <paramref name="maxEntries"/> on a dead-letter list by dropping the
    /// oldest entries.
    ///
    /// <para>Bounded because an unattended dead-letter list would otherwise exhaust the
    /// server it exists to protect — a denial of service with good intentions. Dropping
    /// the <i>oldest</i> keeps the most recent failures, which are the ones an operator
    /// is most likely to be investigating.</para>
    /// </summary>
    protected void TrimDeadLetters<TGarnetApi>(TGarnetApi api, PinnedSpanByte dlqKey, int maxEntries)
        where TGarnetApi : IGarnetApi
    {
        if (maxEntries <= 0) return;

        api.ListLength(dlqKey, out var length);
        while (length > maxEntries)
        {
            if (api.ListLeftPop(dlqKey, out _) != GarnetStatus.OK) break;
            DeadLettersDropped++;
            length--;
        }
    }
}
