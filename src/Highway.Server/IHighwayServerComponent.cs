using Highway.Server.Observability;
using Microsoft.Extensions.Logging;

namespace Highway.Server;

/// <summary>
/// An optional in-process component hosted alongside the broker.
/// Internal: first-party seam, not an extension point.
/// </summary>
internal interface IHighwayServerComponent : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Starts the component. Must not throw — a component that cannot start
    /// logs and returns; the broker carries on without it.
    /// </summary>
    void Start();
}

internal sealed record HighwayComponentContext(
    HighwayServerOptions Options,
    FlightRecorder Recorder,
    ILoggerFactory LoggerFactory,
    string Endpoint,

    /// <summary>
    /// Read-only broker state, for components that need to show what is true now rather than
    /// what has happened (020). The recorder answers the second question; this answers the
    /// first.
    /// </summary>
    IBrokerState BrokerState);
