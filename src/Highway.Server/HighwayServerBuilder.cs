using System.Net;
using Garnet.server;
using Garnet.server.Auth.Settings;
using Garnet.server.TLS;
using Highway.Server.Internal;
using Microsoft.Extensions.Logging;

namespace Highway.Server;

/// <summary>
/// Fluent builder for configuring and constructing a <see cref="IHighwayServer"/>
/// (a Highway Garnet server with all HW.* commands registered).
/// </summary>
public sealed class HighwayServerBuilder
{
    private readonly HighwayServerOptions _opts = new();
    private readonly List<Func<HighwayComponentContext, IHighwayServerComponent>> _componentFactories = [];
    private ILoggerFactory? _loggerFactory;
    private string? _bindAddressText;

    /// <summary>Sets the TCP port to listen on. Default: 6500.</summary>
    public HighwayServerBuilder WithPort(int port)
    {
        _opts.Port = port;
        return this;
    }

    /// <summary>
    /// Sets the network interface to listen on. Default: loopback (secure by
    /// default — exposing the broker is an explicit operator decision).
    /// Use <see cref="IPAddress.Any"/> to listen on all interfaces.
    /// </summary>
    public HighwayServerBuilder WithBindAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        _opts.BindAddress = address;
        _bindAddressText  = null;
        return this;
    }

    /// <summary>
    /// Sets the network interface to listen on from a dotted-quad (or hostname)
    /// string. Parsed at <see cref="Build"/>; an invalid value is rejected there
    /// with a descriptive exception naming the offending value.
    /// </summary>
    public HighwayServerBuilder WithBindAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        _bindAddressText = address;
        return this;
    }

    /// <summary>
    /// Applies an arbitrary configuration delegate to the underlying options —
    /// the escape hatch for options without a dedicated <c>With*</c> method.
    /// </summary>
    public HighwayServerBuilder WithOptions(Action<HighwayServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_opts);
        return this;
    }

    /// <summary>
    /// Sets the data directory for AOF and checkpoints.
    /// When omitted, the server runs in memory-only mode (no durability).
    /// </summary>
    public HighwayServerBuilder WithDataDir(string dataDir)
    {
        _opts.DataDir = dataDir;
        return this;
    }

    /// <summary>
    /// Sets the lease duration for RPC processing entries.
    /// Use <see cref="TimeSpan.Zero"/> to disable the lazy requeue sweep.
    /// Default: 5 minutes.
    /// </summary>
    public HighwayServerBuilder WithLease(TimeSpan lease)
    {
        _opts.Lease = lease;
        return this;
    }

    /// <summary>
    /// Sets the TTL applied to reply slots. Default: 5 minutes.
    /// </summary>
    public HighwayServerBuilder WithReplySlotTtl(TimeSpan ttl)
    {
        _opts.ReplySlotTtl = ttl;
        return this;
    }

    /// <summary>
    /// Sets the maximum allowed payload size in bytes. Default: 1 MiB.
    /// </summary>
    public HighwayServerBuilder WithMaxPayloadBytes(int maxBytes)
    {
        _opts.MaxPayloadBytes = maxBytes;
        return this;
    }

    /// <summary>
    /// Sets the backlog retention window and per-channel entry cap.
    /// Default: 1 day, 10,000 entries.
    /// </summary>
    public HighwayServerBuilder WithBacklogRetention(TimeSpan retention, int maxEntries)
    {
        _opts.BacklogRetention  = retention;
        _opts.MaxBacklogEntries = maxEntries;
        return this;
    }

    /// <summary>
    /// Sets the default and maximum count for HW.RECEIVE.
    /// Default: count = 10, maxCount = 500.
    /// </summary>
    public HighwayServerBuilder WithReceiveDefaults(int count, int maxCount)
    {
        _opts.ReceiveDefaultCount = count;
        _opts.ReceiveMaxCount     = maxCount;
        return this;
    }

    /// <summary>
    /// When <see langword="true"/>, the server waits for each AOF commit before
    /// sending the response (strict durability, higher latency).
    /// Only effective when a data directory is configured. Default: false.
    /// </summary>
    public HighwayServerBuilder WithWaitForCommit(bool waitForCommit)
    {
        _opts.WaitForCommit = waitForCommit;
        return this;
    }

    /// <summary>
    /// Configures the flight recorder and activity emission (feature 002).
    ///
    /// <para>Defaults are useful with no configuration. The one setting worth a
    /// deliberate decision is payload capture: it defaults to
    /// <c>PayloadCapture.Full</c>, which means payload content sits in server
    /// memory readable by anyone who can issue <c>HW.REPLAY</c> — and Highway
    /// has no authentication. Use <c>HeadersOnly</c> for sensitive names, or set
    /// <c>ReplayEnabled = false</c> to keep the metrics without serving bodies.</para>
    /// </summary>
    public HighwayServerBuilder WithObservability(Action<Observability.ObservabilityOptions> configure)
    {
        configure(_opts.Observability);
        return this;
    }

    /// <summary>
    /// Requires every client to present <paramref name="password"/> (feature 012).
    ///
    /// <para>This is the whole of the common case: an administrator sets a password on
    /// the broker and gives it to the team, who set it on their clients. There is no
    /// configuration file, no user directory, and nothing to generate.</para>
    ///
    /// <para><b>The username is Garnet's <c>default</c>.</b> Without an ACL configuration
    /// file Garnet supports exactly one user, so this method promises a password rather
    /// than a username directory. Clients may send the password alone or pair it with the
    /// username <c>default</c>; both work, and anything else is refused. Use
    /// <see cref="WithAuthentication(IAuthenticationSettings)"/> if you need named users.</para>
    ///
    /// <para><b>Not required on loopback.</b> A server left on the default bind address
    /// runs happily without this — see <see cref="WithBindAddress(IPAddress)"/> for the
    /// rule. Binding anywhere else requires either this or
    /// <see cref="WithoutAuthentication"/>.</para>
    ///
    /// <para><b>The password crosses the wire in clear text unless TLS is enabled.</b>
    /// RESP <c>AUTH</c> sends it as an ordinary bulk string. On an untrusted network add
    /// <see cref="WithTls(string, string)"/>. TLS is never required — Highway cannot
    /// invent a certificate — but a password on a network without it is a password on
    /// the wire.</para>
    /// </summary>
    public HighwayServerBuilder WithPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        _opts.Authentication.Password = password;
        return this;
    }

    /// <summary>
    /// Escape hatch: uses <paramref name="settings"/> verbatim instead of anything
    /// Highway would construct. ACL configuration files, named users, per-command rules
    /// and Entra ID are all reachable this way.
    ///
    /// <para>Read <see cref="Security.AuthenticationOptions.Settings"/> before using this
    /// — it documents two measured traps (<c>nopass</c> silently disabling
    /// authentication entirely, and Highway's commands living in Garnet's
    /// <c>@dangerous</c> category) that are easy to walk into and hard to notice.</para>
    /// </summary>
    public HighwayServerBuilder WithAuthentication(IAuthenticationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _opts.Authentication.Settings = settings;
        return this;
    }

    /// <summary>
    /// Runs the server with no authentication on an address that would otherwise require
    /// it. Deliberate, supported, and logged as a warning on every start.
    ///
    /// <para>Not needed on loopback, where running open is already the default. This
    /// exists for a broker on a network you trust — a private subnet, or behind an
    /// authenticating proxy — where the alternative would be working around the rule in
    /// a worse way.</para>
    /// </summary>
    public HighwayServerBuilder WithoutAuthentication()
    {
        _opts.Authentication.ExplicitlyDisabled = true;
        return this;
    }

    /// <summary>
    /// Serves TLS using a PFX certificate file (feature 012).
    ///
    /// <para><b>Never required, and strongly recommended wherever a password crosses a
    /// network.</b> RESP <c>AUTH</c> sends the password as an ordinary bulk string, so
    /// without TLS it is on the wire in clear text. Highway makes authentication mandatory
    /// off loopback because it can demand a password; it cannot demand a certificate, so
    /// TLS stays opt-in.</para>
    ///
    /// <para>The certificate is loaded at <see cref="Build"/> so a missing file or wrong
    /// password is a startup error naming the file, rather than an opaque handshake failure
    /// minutes later.</para>
    /// </summary>
    public HighwayServerBuilder WithTls(string certFileName, string? certPassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certFileName);
        _opts.Tls.CertFileName = certFileName;
        _opts.Tls.CertPassword = certPassword;
        return this;
    }

    /// <summary>
    /// Configures TLS in full — certificate store subject names, mTLS, revocation checking
    /// and certificate refresh.
    ///
    /// <para>Read <see cref="Security.TlsOptions.Settings"/> before relying on this in
    /// production: it quotes Garnet's own warning that the TLS class Highway wraps is
    /// sample code not intended for production without review, and offers the escape
    /// hatch.</para>
    /// </summary>
    public HighwayServerBuilder WithTls(Action<Security.TlsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_opts.Tls);
        return this;
    }

    /// <summary>
    /// Escape hatch: uses <paramref name="settings"/> verbatim instead of the wrapper over
    /// Garnet's sample TLS implementation.
    /// </summary>
    public HighwayServerBuilder WithTls(IGarnetTlsOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _opts.Tls.Settings = settings;
        return this;
    }

    /// <summary>Supplies a logger factory for structured logging from the server.</summary>
    public HighwayServerBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// Registers a component factory. Components are created after command
    /// registration, started in <see cref="IHighwayServer.Start"/>, and disposed
    /// before the recorder.
    /// </summary>
    internal HighwayServerBuilder AddComponent(Func<HighwayComponentContext, IHighwayServerComponent> factory)
    {
        _componentFactories.Add(factory);
        return this;
    }

    /// <summary>
    /// Builds the server, registers all HW.* commands, and returns the
    /// <see cref="IHighwayServer"/> ready to be started via <see cref="IHighwayServer.Start"/>
    /// or <see cref="IHighwayServer.RunAsync"/>.
    /// </summary>
    public IHighwayServer Build()
    {
        // Resolve a deferred bind-address string (WithBindAddress(string)) so an
        // invalid value is rejected here, at Build(), naming the offending value.
        if (_bindAddressText is not null)
        {
            if (!IPAddress.TryParse(_bindAddressText, out var parsed))
                throw new ArgumentException(
                    $"'{_bindAddressText}' is not a valid bind address (expected a dotted-quad IP address).",
                    nameof(_bindAddressText));
            _opts.BindAddress = parsed;
            _bindAddressText  = null;
        }

        _opts.Observability.Validate();
        _opts.Authentication.Validate();
        _opts.Tls.Validate();
        ValidateDeliveryOptions(_opts);

        var garnetOpts = BuildGarnetOptions(_opts);
        var logger = _loggerFactory?.CreateLogger<HighwayServerBuilder>();

        // Applied after the bind address is resolved above, because the whole rule is
        // a function of it: free on loopback, required off it (feature 012).
        Security.SecurityPolicy.Enforce(_opts, logger);

        logger?.LogInformation(
            "Building Highway server: bind={BindAddress}, port={Port}, dataDir={DataDir}, lease={Lease}",
            _opts.BindAddress, _opts.Port, _opts.DataDir ?? "(memory-only)", _opts.Lease);

        var garnet = new HighwayGarnetServer(garnetOpts, _loggerFactory);
        return new HighwayServer(garnet, _opts, _loggerFactory, _componentFactories);
    }

    /// <summary>
    /// Validates feature 013's delivery limits, naming the offending value.
    /// </summary>
    internal static void ValidateDeliveryOptions(HighwayServerOptions opts)
    {
        if (opts.MaxDeliveryAttempts < 0)
            throw new InvalidOperationException(
                $"HighwayServerOptions.MaxDeliveryAttempts cannot be negative, but was {opts.MaxDeliveryAttempts}. " +
                "Use 0 for unlimited retries.");

        if (opts.MaxDeliveryAttempts > Internal.Envelope.MaxAttempts)
            throw new InvalidOperationException(
                $"HighwayServerOptions.MaxDeliveryAttempts ({opts.MaxDeliveryAttempts}) exceeds the " +
                $"{Internal.Envelope.MaxAttempts} an entry can record. The count saturates there, so a " +
                "higher limit would never be reached and dead-lettering would never happen.");

        if (opts.MaxDeadLetterEntries < 0)
            throw new InvalidOperationException(
                $"HighwayServerOptions.MaxDeadLetterEntries cannot be negative, but was {opts.MaxDeadLetterEntries}.");

        // The 0xFF entry version byte is only unambiguous against a legacy RPC entry
        // while an identifier length's high byte cannot reach 0xFF.
        if (opts.MaxIdentifierBytes > Internal.Envelope.MaxUnambiguousIdentifierBytes)
            throw new InvalidOperationException(
                $"HighwayServerOptions.MaxIdentifierBytes ({opts.MaxIdentifierBytes}) exceeds " +
                $"{Internal.Envelope.MaxUnambiguousIdentifierBytes}, above which a pre-013 entry could be " +
                "mistaken for a current one and delivered as a corrupt payload.");
    }

    /// <summary>
    /// Maps <see cref="HighwayServerOptions"/> to <see cref="GarnetServerOptions"/>
    /// per the design table.
    /// </summary>
    internal static GarnetServerOptions BuildGarnetOptions(HighwayServerOptions opts)
    {
        var garnet = new GarnetServerOptions
        {
            // Endpoint — configured bind address (default loopback) on the configured port
            EndPoints = [new IPEndPoint(opts.BindAddress, opts.Port)],

            // PubSub must stay enabled for doorbells
            // DisablePubSub is not a field on GarnetServerOptions; it stays at its default (false)

            // Suppress cluster in v1
            EnableCluster = false,

            // Authentication (feature 012). Null when this server runs open, which is
            // Garnet's own default and means every connection is accepted.
            AuthSettings = opts.Authentication.CreateSettings(),

            // Transport security (feature 012). Null when TLS is not configured, which is
            // the default and is Garnet's own default too.
            TlsOptions = opts.Tls.CreateTlsOptions(null),
        };

        if (opts.DataDir is not null)
        {
            // Durable mode: AOF + storage tier + recovery
            var dir = Path.GetFullPath(opts.DataDir);

            garnet.EnableStorageTier = true;
            garnet.LogDir            = Path.Combine(dir, "log");
            garnet.CheckpointDir     = Path.Combine(dir, "checkpoints");
            garnet.EnableAOF         = true;
            garnet.CommitFrequencyMs = 0;   // commit per op
            garnet.Recover           = true;

            if (opts.WaitForCommit)
                garnet.WaitForCommit = true;
        }
        else
        {
            // Memory-only mode: no AOF, no storage tier
            garnet.EnableStorageTier = false;
            garnet.EnableAOF         = false;
        }

        return garnet;
    }
}
