using System.Reflection;

namespace Highway.Client;

/// <summary>
/// Configuration options for the Highway client.
/// Options are snapshotted by the engine at start; mutation afterwards has no effect.
/// </summary>
public sealed class HighwayOptions : IHighwayConnectionSettings
{
    /// <summary>
    /// Unique name for this node in the cluster.
    ///
    /// Defaults to a stable value — <c>{entry-assembly-name}-{machine-name}</c> —
    /// so a restarted process resumes its subscriber group and processing identity
    /// instead of orphaning them.
    ///
    /// <para>
    /// MUST be unique per live process instance: two live processes sharing a name
    /// share one pub/sub group and one processing identity (they compete with each
    /// other). Must also satisfy the server's identifier rules — non-empty, at most
    /// 256 bytes, no character below U+0020, no U+007F — otherwise the server rejects
    /// every command carrying it (feature 004.1, Requirement 3).
    /// </para>
    /// </summary>
    public string NodeName { get; set; } = DefaultNodeName();

    /// <summary>
    /// Connection string for the Highway server (Garnet/Redis endpoint).
    /// Required — Highway always communicates through a server.
    /// </summary>
    public string? Server { get; set; }

    /// <summary>
    /// Username presented to the server (feature 012). Optional.
    ///
    /// <para>A Highway server secured with <c>WithPassword</c> has exactly one user,
    /// Garnet's <c>default</c>, so this is normally left unset — the password alone is
    /// enough. Set it only against a server configured with named users through the
    /// <c>WithAuthentication(IAuthenticationSettings)</c> escape hatch.</para>
    ///
    /// <para><b>Precedence:</b> the connection string is parsed first, this overwrites
    /// what it set, and <see cref="ConfigureConnection"/> runs last and can override
    /// anything.</para>
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password presented to the server (feature 012). Optional; required against a
    /// server configured with <c>WithPassword</c>.
    ///
    /// <para><b>Sent in clear text unless <see cref="Tls"/> is enabled.</b> RESP <c>AUTH</c>
    /// carries it as an ordinary bulk string.</para>
    ///
    /// <para><b>Precedence:</b> as <see cref="Username"/> — connection string, then this,
    /// then <see cref="ConfigureConnection"/>.</para>
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Transport security (feature 012). Off by default.
    /// </summary>
    public HighwayTlsOptions? Tls { get; set; }

    /// <summary>
    /// Escape hatch over the underlying StackExchange.Redis configuration, applied
    /// <b>last</b> — after the connection string and after every property above, so it can
    /// override any of them.
    ///
    /// <para>This is how client certificates and private certificate authorities are
    /// reached without Highway modelling every knob:</para>
    ///
    /// <code>
    /// options.ConfigureConnection = c =>
    /// {
    ///     c.CertificateSelection += (_, _, _, _, _) => clientCertificate;
    ///     c.CertificateValidation += (_, cert, chain, errors) => ValidateAgainstPrivateCa(cert, chain, errors);
    /// };
    /// </code>
    /// </summary>
    public Action<StackExchange.Redis.ConfigurationOptions>? ConfigureConnection { get; set; }

    /// <summary>
    /// Default timeout for RPC calls.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of service executions running concurrently per service
    /// on this node. Default: 8.
    /// </summary>
    public int WorkerConcurrency { get; set; } = 8;

    /// <summary>
    /// How often a running handler's lease is renewed. Default: 1 minute. Must be positive.
    ///
    /// <para>Against the server's 5-minute default <c>Lease</c> that is 5× headroom — the same
    /// ratio the heartbeat keeps against <c>NodeExpiry</c>. <b>The client cannot read the
    /// server's lease</b>, so the relationship is documented rather than validated: lowering
    /// the server's <c>Lease</c> below roughly 3× this interval makes renewal unreliable.</para>
    /// </summary>
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a single message may have its lease renewed for. Default: 15 minutes.
    /// <see cref="TimeSpan.Zero"/> disables renewal entirely, restoring pre-019 behaviour.
    ///
    /// <para><b>The cap is the feature, not a limitation of it.</b> Unbounded renewal would
    /// delete lease recovery: a handler stuck in a deadlock or an infinite loop would hold its
    /// message forever — never redelivered, never dead-lettered, never visible as a problem.
    /// Past the cap the message returns to exactly the behaviour it has today.</para>
    ///
    /// <para><b>For work measured in hours, chunk instead.</b> Claim, process one slice,
    /// checkpoint to your own database, enqueue the next slice, acknowledge. Each message lives
    /// seconds while the job lives hours — and it survives deploys, parallelises for free, and
    /// dead-letters one bad slice without killing the job.</para>
    /// </summary>
    public TimeSpan MaxProcessingTime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Number of messages requested per <c>HW.RECEIVE</c> batch in consumer loops.
    /// Must be within the server's bounds (1..500). Default: 10.
    /// </summary>
    public int ReceiveBatchSize { get; set; } = 10;

    /// <summary>
    /// Interval of the backstop sweep that drives progress when doorbells are
    /// missed. Must be at least 50ms. Default: 500ms.
    /// </summary>
    public TimeSpan BackstopInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long graceful shutdown waits for in-flight work to finish before
    /// abandoning it to server lease recovery. Default: 10s.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// When <c>false</c>, the engine skips all doorbell subscriptions and relies
    /// purely on the backstop sweep. Test seam proving doorbells are only a
    /// latency optimization. Default: <c>true</c>.
    /// </summary>
    public bool DoorbellsEnabled { get; set; } = true;

    /// <summary>
    /// When <c>true</c> (default), the engine registers this node with the server
    /// at start and then proves liveness on <see cref="HeartbeatInterval"/>.
    ///
    /// <para>Turning it off keeps the node out of the registry entirely: it is
    /// never returned by <c>HW.DISCOVER</c>, never appears in <c>HW.STATS</c>
    /// node counts, and — because the server only prunes nodes that hold a
    /// registration record — is never pruned. RPC and pub/sub are unaffected.</para>
    /// </summary>
    public bool HeartbeatEnabled { get; set; } = true;

    /// <summary>
    /// How often the node proves liveness. The catalog is not resent — a beat is
    /// a few bytes regardless of how many services the node hosts.
    ///
    /// <para>What matters is the <b>ratio</b> to the server's <c>NodeExpiry</c>,
    /// not the absolute value. The defaults give 6× (a 5s beat against a 30s
    /// expiry), so several consecutive beats can be lost before a healthy node is
    /// declared dead. Below roughly 3×, ordinary GC pauses start causing false
    /// staleness.</para>
    ///
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When <c>true</c>, <c>ExecuteAsync</c> consults discovery before enqueuing
    /// and returns 404 immediately when no live node hosts the service, instead
    /// of waiting out <see cref="CallTimeout"/>.
    ///
    /// <para><b>Off by default.</b> It trades a round trip on a cold cache for a
    /// faster failure, and that trade belongs to the application. A stale or
    /// failed discovery never causes a 404 — only a fresh, successful, empty
    /// result does — so enabling it cannot drop a request that would otherwise
    /// have been served.</para>
    /// </summary>
    public bool FastFailEnabled { get; set; }

    /// <summary>
    /// How long a discovery result is reused before being refetched, so fast-fail
    /// does not add a round trip to every call in a hot loop.
    /// <see cref="TimeSpan.Zero"/> disables caching. Default: 1 second.
    /// </summary>
    public TimeSpan DiscoveryCacheTtl { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether the client emits <see cref="System.Diagnostics.Activity"/> spans
    /// for calls and publishes (feature 002).
    ///
    /// <para>Highway takes no OpenTelemetry dependency — it emits activities and
    /// the application subscribes. With no listener attached, emission costs
    /// essentially nothing, which is why this defaults on.</para>
    /// </summary>
    public bool ActivitiesEnabled { get; set; } = true;

    /// <summary>
    /// Additional assemblies to scan beyond those auto-discovered via AppDomain.
    /// Use this when assemblies containing services haven't been loaded yet.
    /// </summary>
    public List<Assembly> AdditionalAssemblies { get; } = [];

    /// <summary>
    /// Predicates to exclude assemblies from scanning.
    /// If any predicate returns true for an assembly, it is skipped.
    /// </summary>
    public List<Func<Assembly, bool>> ExcludedAssemblies { get; } = [];

    /// <summary>
    /// The logical consumer identity for pub/sub (feature 025). Replicas that share a
    /// subscription group <b>compete</b> for one copy of each published message; nodes with
    /// distinct groups each receive <b>their own copy</b>.
    ///
    /// <para>Default <c>null</c> → the group is <see cref="NodeName"/>, which reproduces the
    /// original behavior exactly: every node is its own group, every node gets a copy. Set it
    /// to one stable name per logical application ("billing") and scale replicas freely —
    /// they will share the group's queue through the ordinary claim machinery.</para>
    ///
    /// <para>Validated by the same identifier rules as <see cref="NodeName"/>, including the
    /// <c>@</c> rejection — the group is embedded in the derived queue name
    /// <c>{channel}@{group}</c>.</para>
    /// </summary>
    public string? SubscriptionGroup { get; set; }

    /// <summary>The group pub/sub actually uses: <see cref="SubscriptionGroup"/> or <see cref="NodeName"/>.</summary>
    internal string EffectiveSubscriptionGroup => SubscriptionGroup ?? NodeName;

    /// <summary>Recurring-job declarations (feature 028). See <see cref="JobsOptions"/>.</summary>
    public JobsOptions Jobs { get; } = new();

    /// <summary>
    /// Which assemblies may contribute <b>handlers</b> to this process (feature 024).
    /// Contract discovery ignores this setting — see <see cref="Client.HostingMode"/>.
    /// Default: <see cref="Client.HostingMode.Implicit"/>, the original behavior.
    /// </summary>
    public HostingMode HostingMode { get; set; } = HostingMode.Implicit;

    /// <summary>
    /// Assemblies the composition root consents to host handlers from, in addition to any
    /// carrying <c>[assembly: HighwayHostModule]</c>. Only consulted by
    /// <see cref="Client.HostingMode.Declared"/> and <see cref="Client.HostingMode.ExplicitOnly"/>.
    ///
    /// <para>Distinct from <see cref="AdditionalAssemblies"/>, which adds assemblies to the
    /// <i>scan</i> (contracts and, mode permitting, handlers); this list grants <i>hosting
    /// consent</i> to assemblies already scanned.</para>
    /// </summary>
    public List<Assembly> HostAssemblies { get; } = [];

    /// <summary>Fluent form of <see cref="HostAssemblies"/>. Idempotent.</summary>
    public HighwayOptions HostAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (!HostAssemblies.Contains(assembly))
            HostAssemblies.Add(assembly);
        return this;
    }

    private static string DefaultNodeName()
    {
        var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "highway-node";
        return $"{appName}-{Environment.MachineName}";
    }
}
