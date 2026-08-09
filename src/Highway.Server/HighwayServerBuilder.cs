using System.Globalization;
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
    ///
    /// <para>When omitted, Highway picks one beside the executable (feature 016) — the server
    /// is <b>durable by default</b>. Use <see cref="Ephemeral"/> to opt out deliberately.</para>
    /// </summary>
    public HighwayServerBuilder WithDataDir(string dataDir)
    {
        _opts.DataDir = dataDir;
        _opts.Ephemeral = false;
        return this;
    }

    /// <summary>
    /// Runs the broker in memory: no data directory, no AOF, nothing survives the process.
    ///
    /// <para><b>The deliberate opt-out from durability (016 R1.4).</b> Durability as a default
    /// only works if declining it is one call — otherwise a test suite fights the default and
    /// somebody eventually flips the default back rather than the tests.</para>
    ///
    /// <para>Correct for tests and genuinely disposable brokers. Wrong for anything asked to
    /// remember a message: every queue, group queue and dead letter is lost on exit.</para>
    /// </summary>
    public HighwayServerBuilder Ephemeral()
    {
        _opts.Ephemeral = true;
        _opts.DataDir = null;
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

        ResolveDataDirectory(_opts);

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
    /// <summary>
    /// Chooses the data directory when the caller did not (016 R1, decision 4).
    ///
    /// <para><b>Durable by default.</b> Until this feature <c>new HighwayServerBuilder().Build()</c>
    /// was memory-only, which made every queue and pub/sub guarantee false in the configuration
    /// a newcomer meets first. The location is beside the executable: predictable, and findable
    /// without reading source.</para>
    ///
    /// <para><b>An unusable location throws here rather than degrading.</b> A broker that
    /// quietly becomes non-durable is the exact defect this feature removes — and it would be
    /// worse after this change than before, because the guarantee is now documented as true.
    /// The message names the path and both ways out.</para>
    /// </summary>
    private static void ResolveDataDirectory(HighwayServerOptions opts)
    {
        if (opts.Ephemeral)
        {
            opts.DataDir = null;   // asked for by name
            return;
        }

        opts.DataDir ??= DefaultDataDirectory(opts.Port);

        var dir = Path.GetFullPath(opts.DataDir);

        try
        {
            Directory.CreateDirectory(dir);

            // Creatable is not the same as writable — a directory can exist and refuse writes.
            // Prove it now, at Build(), rather than on the first AOF commit.
            var probe = Path.Combine(dir, ".highway-write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Highway could not use its data directory '{dir}': {ex.Message} " +
                "A broker that cannot write cannot be durable, and silently running in memory " +
                "would lose every message it was asked to keep. Either point it somewhere " +
                "writable with WithDataDir(path), or ask for a memory-only broker by name with " +
                "Ephemeral().", ex);
        }

        VerifyStorageFormat(dir);

        opts.DataDir = dir;
    }

    /// <summary>
    /// The storage format a data directory was written by. Bumped whenever the <b>command set</b>
    /// or an <b>entry framing</b> changes, because both are encoded in the AOF.
    ///
    /// <para>1 = pre-013. 2 = 013's versioned entry framings. 3 = 018, which removed
    /// <c>HW.RECEIVE</c> and <c>HW.RACK</c> and thereby shifted every stored-procedure id.</para>
    /// </summary>
    private const int StorageFormatVersion = 3;

    private const string StorageFormatFile = "highway.format";

    /// <summary>
    /// Refuses a data directory written by an incompatible build, before Garnet tries to
    /// recover from it (feature 016 follow-up).
    ///
    /// <para><b>Why this is needed, and why the 018 check was not enough.</b> Garnet's AOF
    /// stores a stored-procedure <i>id</i> per record, and those ids are positional. Feature 018
    /// removed two commands, so every id after them shifted, and replaying an older AOF fails
    /// with "Transaction procedure N not found". Recovery then aborts and the broker carries on
    /// with an <b>empty store</b> — healthy-looking, and missing every message it was asked to
    /// keep. The 018 check scanned for leftover <c>hw:ch:*:grp:*</c> keys, which can only be
    /// found if recovery <i>succeeded</i>: it looked for a symptom that is absent in the worst
    /// case.</para>
    ///
    /// <para>Feature 016 made this everyone's problem by turning durability on by default, so
    /// the next command-set change would silently empty every existing broker.</para>
    /// </summary>
    private static void VerifyStorageFormat(string dir)
    {
        var stampPath = Path.Combine(dir, StorageFormatFile);
        var hasData = Directory.Exists(Path.Combine(dir, "checkpoints"))
                   || Directory.Exists(Path.Combine(dir, "log"));

        if (File.Exists(stampPath))
        {
            var text = File.ReadAllText(stampPath).Trim();

            if (int.TryParse(text, out var found) && found == StorageFormatVersion)
                return;

            throw new InvalidOperationException(
                $"Highway's data directory '{dir}' was written in storage format '{text}', but this " +
                $"build reads format {StorageFormatVersion}. Recovering it would fail part-way and " +
                "leave the broker running with an empty store, which looks healthy and is not. " +
                "Drain it with the previous version, or delete the directory to start fresh. " +
                "Use Ephemeral() if this broker does not need to keep anything.");
        }

        if (hasData)
        {
            throw new InvalidOperationException(
                $"Highway's data directory '{dir}' holds data written before storage formats were " +
                $"stamped, and cannot be read by this build (format {StorageFormatVersion}). " +
                "Drain it with the previous version, or delete the directory to start fresh. " +
                "Use Ephemeral() if this broker does not need to keep anything.");
        }

        File.WriteAllText(stampPath, StorageFormatVersion.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Beside the executable, port-suffixed when the port is not the default so two brokers on
    /// one machine do not share a directory and recover each other's data.
    /// </summary>
    private static string DefaultDataDirectory(int port)
        => Path.Combine(
            AppContext.BaseDirectory,
            port == HighwayServerOptions.DefaultPort ? "highway-data" : $"highway-data-{port}");

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
            // Durable mode: AOF + storage tier + recovery.
            // Since 016 this is the DEFAULT path — see ResolveDataDirectory.
            var dir = Path.GetFullPath(opts.DataDir);

            garnet.EnableStorageTier = true;
            garnet.LogDir            = Path.Combine(dir, "log");
            garnet.CheckpointDir     = Path.Combine(dir, "checkpoints");
            garnet.EnableAOF         = true;
            garnet.CommitFrequencyMs = 0;   // commit per op
            garnet.Recover           = true;

            // Bound the log (016 R6). Without this Garnet never checkpoints on size, so the
            // AOF grows without limit and a long-lived broker replays its whole history on
            // start. Truncation is the broker's own housekeeping — it refuses nothing.
            if (opts.AofSizeLimitBytes > 0)
                garnet.AofSizeLimit = opts.AofSizeLimitBytes.ToString(CultureInfo.InvariantCulture);

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
