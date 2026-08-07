using System.Reflection;

namespace Highway.Client;

/// <summary>
/// Configuration options for the Highway client.
/// Options are snapshotted by the engine at start; mutation afterwards has no effect.
/// </summary>
public sealed class HighwayOptions
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
    /// Default timeout for RPC calls.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of service executions running concurrently per service
    /// on this node. Default: 8.
    /// </summary>
    public int WorkerConcurrency { get; set; } = 8;

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
    /// Additional assemblies to scan beyond those auto-discovered via AppDomain.
    /// Use this when assemblies containing services haven't been loaded yet.
    /// </summary>
    public List<Assembly> AdditionalAssemblies { get; } = [];

    /// <summary>
    /// Predicates to exclude assemblies from scanning.
    /// If any predicate returns true for an assembly, it is skipped.
    /// </summary>
    public List<Func<Assembly, bool>> ExcludedAssemblies { get; } = [];

    private static string DefaultNodeName()
    {
        var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "highway-node";
        return $"{appName}-{Environment.MachineName}";
    }
}
