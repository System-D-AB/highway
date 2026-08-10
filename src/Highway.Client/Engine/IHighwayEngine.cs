using StackExchange.Redis;

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
    /// What this process provides and can use (feature 024). Available from construction —
    /// the manifest describes the scan, not the connection — and logged at
    /// <see cref="StartAsync"/>.
    /// </summary>
    TopologyManifest Topology { get; }

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

    /// <summary>
    /// Retires this node permanently (feature 017): stops the loops, drains in-flight work,
    /// then purges its registration and destroys its subscriber queues on the server.
    ///
    /// <para><b>This destroys data on purpose.</b> It is not <c>StopAsync</c> with tidying —
    /// messages addressed to this node's subscriber groups are deleted, because the node has
    /// declared it will never exist to process them. Use <c>StopAsync</c> for a restart.</para>
    ///
    /// <para><b>The loops stop first, and that is a correctness requirement.</b> The heartbeat
    /// loop re-registers the node, so a purge issued while it still runs is undone moments
    /// later and the node reappears with an empty catalog — which looks exactly like a purge
    /// that worked and then silently did not.</para>
    /// </summary>
    /// <returns>What was destroyed, so an irreversible operation leaves a record.</returns>
    Task<(int Groups, long Messages, long Bytes)> CleanAndByeForeverAsync(CancellationToken ct = default);
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

    /// <summary>
    /// The shared multiplexer once the engine is running; null before start.
    /// Used by <c>HighwayCache</c> to share the engine's connection (feature 026).
    /// </summary>
    IConnectionMultiplexer? Multiplexer { get; }
}
