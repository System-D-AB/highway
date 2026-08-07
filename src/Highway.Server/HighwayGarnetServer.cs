using Garnet;
using Garnet.server;
using Microsoft.Extensions.Logging;

namespace Highway.Server;

/// <summary>
/// Subclass of <see cref="GarnetServer"/> that exposes the internal
/// <see cref="Garnet.server.SubscribeBroker"/> for doorbell notifications.
///
/// <para>
/// The broker is reached via the <c>protected storeWrapper</c> field that
/// <see cref="GarnetServer"/> exposes, which holds a <c>public readonly</c>
/// <see cref="Garnet.server.SubscribeBroker"/> field.  No reflection required.
/// </para>
/// </summary>
internal sealed class HighwayGarnetServer : GarnetServer
{
    /// <inheritdoc cref="GarnetServer(GarnetServerOptions, ILoggerFactory, Garnet.server.IGarnetServer[], bool)"/>
    public HighwayGarnetServer(
        GarnetServerOptions opts,
        ILoggerFactory? loggerFactory = null)
        : base(opts, loggerFactory)
    {
    }

    /// <summary>
    /// The pub/sub subscribe broker, reachable after construction.
    /// Returns <c>null</c> only when <c>DisablePubSub = true</c> was set on the
    /// options (Highway.Server never sets this flag, so in practice it is always
    /// non-null after the first SUBSCRIBE or <see cref="Garnet.server.SubscribeBroker.Subscribe"/> call).
    /// <see cref="Garnet.server.SubscribeBroker.PublishNow"/> is safe to call even before
    /// any subscriber has registered — it returns 0 instead of throwing.
    /// </summary>
    public SubscribeBroker? SubscribeBroker => storeWrapper?.subscribeBroker;
}
