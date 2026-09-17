namespace Highway.Server;

/// <summary>
/// Represents a running Highway server instance.
/// </summary>
public interface IHighwayServer : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The endpoint the server is listening on, in the form <c>host:port</c>.
    /// Valid after <see cref="Start"/> or <see cref="RunAsync"/> is called.
    /// </summary>
    string Endpoint { get; }

    /// <summary>
    /// Starts the server listeners. Idempotent if already started.
    /// </summary>
    void Start();

    /// <summary>
    /// Starts the server (if not already started) and waits until
    /// <paramref name="ct"/> is cancelled, then disposes the server.
    /// </summary>
    Task RunAsync(CancellationToken ct = default);

    /// <summary>
    /// Raised when replication has scheduled an auto-rejoin (050 T3): a demoted ex-primary has
    /// written its rejoin marker and needs a restart so <see cref="Start"/>/<c>Open</c> re-syncs it
    /// as the new primary's replica. The packaged host handles this by stopping the application, so
    /// the service supervisor relaunches it. Never fires for an in-memory server (no store to rejoin).
    /// </summary>
    event Action? RejoinRequested;
}
