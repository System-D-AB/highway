using System.Globalization;
using System.Net;
using Highway.Server.Internal;
using Microsoft.Extensions.Logging;

namespace Highway.Server;

/// <summary>
/// Fluent builder for configuring and constructing a <see cref="IHighwayServer"/>
/// (the RESP + RocksDB Highway broker with all HW.* commands registered).
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
    /// Configures the AOF segment file size (e.g. "32m", "64m", "1g").
    /// </summary>
    public HighwayServerBuilder WithAofSegmentSize(string segmentSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentSize);
        _opts.AofSegmentSize = segmentSize;
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
    /// <para>This promises a password rather than a username directory. Clients may send the
    /// password alone or pair it with the username <c>default</c>; both work, and anything else
    /// is refused. For named users, populate <see cref="Security.AuthenticationOptions.Users"/>
    /// through <see cref="WithOptions"/>.</para>
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
    /// and certificate refresh. The certificate is loaded and served by the RESP server's
    /// Kestrel endpoint (see <see cref="Security.TlsOptions.LoadServerCertificate"/>).
    /// </summary>
    public HighwayServerBuilder WithTls(Action<Security.TlsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_opts.Tls);
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
        _opts.Replication.Validate();
        _opts.Cache.Validate();
        ValidateDeliveryOptions(_opts);

        ResolveDataDirectory(_opts);

        var logger = _loggerFactory?.CreateLogger<HighwayServerBuilder>();

        // Applied after the bind address is resolved above, because the whole rule is
        // a function of it: free on loopback, required off it (feature 012).
        Security.SecurityPolicy.Enforce(_opts, logger);

        logger?.LogInformation(
            "Building Highway server: bind={BindAddress}, port={Port}, dataDir={DataDir}, lease={Lease}",
            _opts.BindAddress, _opts.Port, _opts.DataDir ?? "(memory-only)", _opts.Lease);

        // The broker runs the 040 RESP server over the 038 RocksDB store (feature 041 — Garnet
        // deleted). The certificate is loaded here so a missing file or wrong password is a
        // startup error naming the file, not an opaque handshake failure later.
        var certificate = _opts.Tls.LoadServerCertificate();
        return new RespHighwayServer(_opts, _loggerFactory, certificate, _componentFactories);
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

        // 057: message size is a chosen, bounded envelope. Positive, and at most the 15 MiB ceiling —
        // a message is buffered whole in memory at several hops, so it is raised-but-bounded, not
        // unlimited (chunk-and-stream for larger payloads).
        if (opts.MaxPayloadBytes <= 0)
            throw new InvalidOperationException(
                $"HighwayServerOptions.MaxPayloadBytes must be positive, but was {opts.MaxPayloadBytes}.");

        if (opts.MaxPayloadBytes > Abstractions.HighwayLimits.MaxPayloadCeilingBytes)
            throw new InvalidOperationException(
                $"HighwayServerOptions.MaxPayloadBytes ({opts.MaxPayloadBytes}) exceeds the 15 MiB ceiling " +
                $"({Abstractions.HighwayLimits.MaxPayloadCeilingBytes} bytes). A message is buffered whole in " +
                "memory at several hops (client, RESP frame, store, WAL, replication); for larger payloads " +
                "chunk-and-stream instead of one message.");
    }

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
    /// <c>HW.RECEIVE</c> and <c>HW.RACK</c> and thereby shifted every stored-procedure id.
    /// 4 = 065 (Highway 3.0): every key lives in a box — the per-queue layout. No in-place migration.</para>
    ///
    /// <para>Public so the highways host's <c>--version</c> can print it (feature 031): an
    /// operator upgrading in place can compare formats before touching a data directory.</para>
    /// </summary>
    public static readonly int StorageFormatVersion = 4;

    /// <summary>
    /// Refuses a data directory written by an earlier storage engine (076 R3.2), at Build() rather than at start.
    /// Since 076 the directory is a SystemD.Crystal store, owned wholly by Crystal: Highway writes no stamp of its own
    /// (its storage format is the key layout, <see cref="StorageFormatVersion"/>), and Crystal refuses any directory
    /// it did not stamp. A directory Highway's RocksDB engine wrote (a <c>highway.format</c> stamp or a <c>CURRENT</c>
    /// file), or Garnet before it (<c>checkpoints/</c>, <c>log/</c>), is named here with its way forward, because
    /// handing it to a new engine would leave a broker that looks healthy and holds nothing it was asked to keep.
    /// </summary>
    private static void VerifyStorageFormat(string dir)
    {
        if (File.Exists(Path.Combine(dir, "crystal.format"))) return;
        if (File.Exists(Path.Combine(dir, "highway.format")) || File.Exists(Path.Combine(dir, "CURRENT"))
            || Directory.Exists(Path.Combine(dir, "checkpoints")) || Directory.Exists(Path.Combine(dir, "log")))
            throw new InvalidOperationException(Storage.Crystal.CrystalHighwayStore.StorageLayoutMessage(dir));
    }

    /// <summary>
    /// Beside the executable, port-suffixed when the port is not the default so two brokers on
    /// one machine do not share a directory and recover each other's data.
    /// </summary>
    private static string DefaultDataDirectory(int port)
        => Path.Combine(
            AppContext.BaseDirectory,
            port == HighwayServerOptions.DefaultPort ? "highway-data" : $"highway-data-{port}");
}
