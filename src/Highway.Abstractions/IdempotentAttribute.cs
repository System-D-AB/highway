namespace Highway.Abstractions;

/// <summary>
/// Marks a request or message contract whose handler must run <b>at most once</b> per
/// delivery, within a window (feature 013).
///
/// <code>
/// [Service("orders.create")]
/// [Idempotent]
/// public sealed class CreateOrder : IReturn&lt;OrderCreated&gt; { }
/// </code>
///
/// <para><b>What this deduplicates, exactly.</b> Highway's delivery is at-least-once by
/// design: if a consumer claims a request, runs the handler, and then dies before its
/// acknowledgement lands, the lease expires and the <i>same</i> request is delivered
/// again. This attribute makes that second delivery skip the handler and return the
/// original response.</para>
///
/// <para><b>What it does not deduplicate.</b> A caller that issues the same logical
/// request twice — two clicks, a retry loop above Highway, a replayed integration event —
/// produces two requests with two different IDs. Highway cannot know they are related, and
/// this attribute will not suppress either of them. If you need that, you need a key drawn
/// from your own domain, and Highway does not offer one yet.</para>
///
/// <para>Stating this precisely matters more than stating it briefly. "Idempotent" reads
/// like a promise about business operations; the mechanism can only make a promise about
/// <i>redeliveries</i>.</para>
///
/// <para><b>A crash mid-handler blocks rather than re-runs.</b> The consumer claims a
/// marker before invoking the handler and overwrites it with the response afterwards. If
/// the process dies between the two, the marker says "in progress" until the window
/// elapses, and a redelivery in that period is <i>not</i> run and <i>not</i> replied to.
/// That is the correct reading of the attribute: by applying it you have said running
/// twice is worse than running late. The window is therefore also how long a crashed
/// in-flight request stays blocked, not merely how long duplicates are remembered.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IdempotentAttribute : Attribute
{
    /// <summary>
    /// How long a completed delivery is remembered, in seconds. Zero uses the server's
    /// configured default (5 minutes, matching the reply-slot TTL).
    ///
    /// <para>Expressed in seconds rather than a <c>TimeSpan</c> because attribute
    /// arguments must be compile-time constants.</para>
    /// </summary>
    public int WindowSeconds { get; init; }

    /// <summary>The configured window, or <see langword="null"/> to use the default.</summary>
    public TimeSpan? Window => WindowSeconds > 0 ? TimeSpan.FromSeconds(WindowSeconds) : null;
}
