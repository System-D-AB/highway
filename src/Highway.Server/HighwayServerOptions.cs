using System.Net;

namespace Highway.Server;

/// <summary>
/// Configuration model for a Highway server instance.
/// All options have production-ready defaults.
///
/// <para>
/// Public because the embedded test server exposes a configuration delegate
/// over it (feature 004.1) and <c>product.md</c>'s hosting model anticipates
/// <c>ConfigureHighwayServer(o => ...)</c>. Additive fields are non-breaking.
/// </para>
/// </summary>
public sealed class HighwayServerOptions
{
    /// <summary>
    /// TCP port Garnet listens on. Default: 6500.
    /// </summary>
    public int Port { get; set; } = 6500;

    /// <summary>
    /// Network interface the server listens on. Default: <see cref="IPAddress.Loopback"/>
    /// (secure by default — exposing the broker to other machines is an explicit
    /// operator decision, e.g. <see cref="IPAddress.Any"/>).
    /// </summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    /// <summary>
    /// Directory for Garnet data (AOF + checkpoints). When <c>null</c> (default)
    /// the server runs in memory-only mode — no disk writes, no durability.
    /// </summary>
    public string? DataDir { get; set; }

    /// <summary>
    /// Lease duration for RPC processing entries. After this period, a pending
    /// entry is considered abandoned and returned to the queue by the next
    /// <c>HW.DEQUEUE</c> call. <see cref="TimeSpan.Zero"/> disables lazy requeue.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// TTL applied to reply slots (<c>hw:rep:{requestId}</c>).
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan ReplySlotTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum allowed payload size in bytes for a single RPC or pub/sub message.
    /// Requests exceeding this limit are rejected with a RESP error.
    /// Default: 1 MiB.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>
    /// Maximum allowed length in bytes for an identifier (service, channel,
    /// group, node, request, or message ID). Longer identifiers are rejected so
    /// a pathological identifier cannot produce an unbounded key.
    /// Default: 256.
    /// </summary>
    public int MaxIdentifierBytes { get; set; } = 256;

    /// <summary>
    /// How long a node's registration stays valid without a heartbeat. A node
    /// whose last beat is older than this is stale: excluded from
    /// <c>HW.DISCOVER</c> results and eligible for pruning.
    ///
    /// <para>What matters is the <b>ratio</b> to the client's
    /// <c>HeartbeatInterval</c>, not the absolute value. The defaults give 6×
    /// (30s expiry against a 5s beat), so several consecutive beats can be lost
    /// before a healthy node is declared dead. Narrowing that margin below about
    /// 3× makes false staleness likely under ordinary GC pauses or load spikes.</para>
    ///
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan NodeExpiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When <c>true</c> (default), a stale node is pruned by the next
    /// <c>HW.DEQUEUE</c> on a service it hosted: its unacknowledged RPC requests
    /// are requeued, and it is dropped from the node sets and the registry.
    ///
    /// <para>When <c>false</c>, stale nodes are still excluded from discovery
    /// but their state is never reclaimed — <c>HW.DEQUEUE</c> keeps locking and
    /// sweeping dead nodes' processing lists, and their unacknowledged requests
    /// are recovered only by the slower per-entry <see cref="Lease"/> sweep.</para>
    ///
    /// <para>Pruning never deletes subscriber groups; those outlive the process
    /// by contract so a restarting node resumes its pending messages.</para>
    /// </summary>
    public bool PruningEnabled { get; set; } = true;

    /// <summary>
    /// Maximum size in bytes of the catalog JSON accepted by the
    /// <c>HW.HEARTBEAT</c> registration form. Larger catalogs are rejected with
    /// <c>HW_PAYLOAD_TOO_LARGE</c>.
    ///
    /// <para>The catalog crosses the wire once per node lifetime, not once per
    /// beat, so this cap can be generous without affecting steady-state cost.</para>
    ///
    /// Default: 256 KiB.
    /// </summary>
    public int MaxCatalogBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// How long a published message is kept in the backlog for late subscribers.
    /// Default: 1 day.
    /// </summary>
    public TimeSpan BacklogRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Maximum number of entries in the per-channel backlog. When the cap is
    /// reached the oldest entry is dropped and a warning is logged.
    /// Default: 10,000.
    /// </summary>
    public int MaxBacklogEntries { get; set; } = 10_000;

    /// <summary>
    /// Default number of messages returned by <c>HW.RECEIVE</c> when no COUNT
    /// argument is supplied. Default: 10.
    /// </summary>
    public int ReceiveDefaultCount { get; set; } = 10;

    /// <summary>
    /// Maximum COUNT value accepted by <c>HW.RECEIVE</c>. Requests above this
    /// limit are rejected with a RESP error. Default: 500.
    /// </summary>
    public int ReceiveMaxCount { get; set; } = 500;

    /// <summary>
    /// When <c>true</c>, the server waits for each AOF commit before sending
    /// the response (strict durability, higher latency). Only effective when
    /// <see cref="DataDir"/> is set and AOF is enabled. Default: <c>false</c>.
    /// </summary>
    public bool WaitForCommit { get; set; } = false;
}
