using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>Which family a failed message belongs to — the grammar <c>HW.DLQ</c> already parses.</summary>
internal enum FailureFamily
{
    /// <summary>An RPC request in a service's processing list.</summary>
    Service,

    /// <summary>A queued message in a queue's processing list (feature 014).</summary>
    Queue,

    /// <summary>A pub/sub message in a channel group's processing list.</summary>
    Channel,
}

/// <summary>
/// Identifies the message that failed: the family, the service/queue/channel name, and the
/// scope within it — a node for services and queues, a group for channels.
/// </summary>
internal readonly record struct FailureTarget(FailureFamily Family, string Name, string Scope)
{
    /// <summary>The noun used in log messages: "service", "queue" or "channel".</summary>
    public string Kind => Family switch
    {
        FailureFamily.Service => "service",
        FailureFamily.Queue => "queue",
        _ => "channel",
    };
}

/// <summary>
/// The one place a handler exception is reported from. All three loops route through it —
/// <see cref="SingleMessageWorkerLoop"/> for services and queues, <see cref="ChannelConsumerLoop"/>
/// for pub/sub — because failure reporting is the concern they genuinely have in common, even
/// though the batch loop is otherwise a different shape.
///
/// <para><b>Currently inert.</b> It logs and nothing more. The server command that carries the
/// exception to the dead letter (<c>HW.FAIL</c>, T3) does not exist yet, so this task is purely
/// structural: one seam to change when it does, instead of three.</para>
///
/// <para><b>The rule it will have to keep.</b> A diagnostic write must never delay, block or
/// break the recovery of a message — the same rule feature 002 states for the flight recorder.
/// When <c>HW.FAIL</c> lands here, its failure is swallowed and logged with the original
/// exception attached, never surfaced in place of it.</para>
/// </summary>
internal sealed class FailureReporter(ILogger logger)
{
    /// <summary>
    /// Reports that a handler threw. <paramref name="disposition"/> states what happens to the
    /// message as a result — the three loops answer that differently, and the difference is the
    /// part an operator reading the log actually needs.
    /// </summary>
    public Task ReportAsync(
        FailureTarget target,
        string messageId,
        Exception exception,
        string disposition,
        CancellationToken cancellationToken = default)
    {
        logger.LogError(exception,
            "Handler failed for message '{MessageId}' on {Kind} '{Target}'; {Disposition}",
            messageId, target.Kind, target.Name, disposition);

        // T3 will send HW.FAIL from here. Until then there is nothing to await, and
        // pretending otherwise would add a state machine to the hot failure path.
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
