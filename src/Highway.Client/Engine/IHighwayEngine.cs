namespace Highway.Client.Engine;

/// <summary>Lifecycle states of the Highway engine (Requirement 11 AC6).</summary>
public enum EngineState
{
    NotStarted,
    Running,
    Draining,
    Stopped,
}

/// <summary>
/// The client-side runtime: connection, doorbell subscriptions, worker and
/// consumer loops, and the backstop sweep. Registered as a singleton by
/// <c>AddHighway</c> and started/stopped with the host via the
/// <c>IHostedService</c> wrapper.
/// </summary>
public interface IHighwayEngine
{
    /// <summary>Current lifecycle state (diagnostics and call gating).</summary>
    EngineState State { get; }

    /// <summary>
    /// Connects to the server (fail fast), subscribes doorbells, registers all
    /// catalog channels, starts loops and the sweeper. Throws when already started.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops taking new work, awaits in-flight work up to the drain timeout,
    /// then stops the sweeper and disposes the connection. Never sends
    /// HW.UNSUBSCRIBE — the node's groups and pending messages persist.
    /// Safe to call once; subsequent calls are no-ops.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Engine internals consumed by <c>HighwayClient</c> — kept off the public
/// <see cref="IHighwayEngine"/> surface because both types are internal.
/// </summary>
internal interface IHighwayEngineInternals
{
    /// <summary>Wire access once the engine is running; null before.</summary>
    IHighwayConnection? Connection { get; }

    /// <summary>Call correlation registry once running; null before.</summary>
    PendingCallRegistry? PendingCalls { get; }

    /// <summary>Discovery cache backing fast-fail once running; null before.</summary>
    ServiceDiscoveryCache? Discovery { get; }
}
