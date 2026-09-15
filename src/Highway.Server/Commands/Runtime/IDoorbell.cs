namespace Highway.Server.Commands.Runtime;

/// <summary>
/// The doorbell seam (039 T1): a lossy fan-out to connected subscribers, rung post-commit
/// to wake waiters. Extracted so commands never see the Garnet-coupled
/// <c>DoorbellBridge</c> (which pins bytes and calls <c>SubscribeBroker.PublishNow</c>).
///
/// <para>Correctness never depends on this — <c>BackstopSweeper</c> is the delivery path
/// (037 R7). A dropped ring costs latency only. 040 provides the RESP-backed implementation;
/// 039's tests use a recording fake.</para>
/// </summary>
internal interface IDoorbell
{
    /// <summary>Rings <paramref name="channel"/> with <paramref name="payload"/>; returns subscribers notified.</summary>
    int Ring(string channel, ReadOnlySpan<byte> payload);
}
