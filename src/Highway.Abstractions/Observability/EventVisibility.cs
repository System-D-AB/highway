namespace Highway.Abstractions.Observability;

/// <summary>
/// Whether an event is something the developer's code caused, or something the broker did to
/// move it (feature 023).
/// </summary>
public enum EventVisibility
{
    /// <summary>
    /// The developer's code caused it, or must act on it: an RPC was started, a response came
    /// back, a message was published or sent, a handler failed, a message dead-lettered, a send
    /// was refused.
    /// </summary>
    Public,

    /// <summary>
    /// The broker recognising its own work: claims, acknowledgements, doorbells, sweeps,
    /// requeues, topology. Real and useful — but not what a developer asked the system to do.
    /// </summary>
    Internal,
}

/// <summary>
/// Which side of that line each event type falls on.
///
/// <para><b>Decided here, on the server, and nowhere else.</b> A view that classified events by
/// inspecting their names would be a second implementation of this table — the mistake this
/// project has now declined three times: the browser must not learn the key layout (020), must
/// not parse a name (022), must not decide what acknowledged means (023).</para>
///
/// <para><b>This is not merely a filter.</b> Highway has no "the handler finished" event — an
/// acknowledgement <i>is</i> that evidence. So the fact an operator needs (<i>processed at
/// 8:35:47 on order-service-1</i>) is public, while the only record of it is classified
/// <see cref="EventVisibility.Internal"/>. The projection derives public <b>facts</b>; this
/// table decides which raw <b>steps</b> are shown. Conflating the two produces a message list
/// that says "acknowledged", which is the word the whole feature exists to stop showing.</para>
/// </summary>
public static class EventVisibilityMap
{
    /// <summary>Classifies one event type.</summary>
    public static EventVisibility Of(HighwayEventType type) => type switch
    {
        // The developer called ExecuteAsync / PublishAsync / SendAsync.
        HighwayEventType.RpcEnqueued => EventVisibility.Public,
        HighwayEventType.Published => EventVisibility.Public,
        HighwayEventType.QueueSent => EventVisibility.Public,

        // A handler produced an answer.
        HighwayEventType.RpcReplied => EventVisibility.Public,

        // Something went wrong that the developer owns.
        HighwayEventType.DeliveryFailed => EventVisibility.Public,
        HighwayEventType.RpcDeadLettered => EventVisibility.Public,
        HighwayEventType.QueueDeadLettered => EventVisibility.Public,
        HighwayEventType.MessageDeadLettered => EventVisibility.Public,
        HighwayEventType.SendRefused => EventVisibility.Public,

        // Everything else is the broker moving work around: claims, acknowledgements,
        // topology, lease renewals, retirement. Default rather than enumerated, because a
        // new event type is far more likely to be machinery than a developer-facing fact —
        // and the enumeration test forces the question either way.
        _ => EventVisibility.Internal,
    };
}
