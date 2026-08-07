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
}
