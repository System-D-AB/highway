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
    /// Observability: the flight recorder and activity emission (feature 002).
    /// Defaults produce a useful recorder with no configuration.
    /// </summary>
    public Observability.ObservabilityOptions Observability { get; set; } = new();

    /// <summary>
    /// How many times a request or message may be delivered before it is moved to the
    /// dead-letter list instead of being requeued (feature 013). Default: 5.
    ///
    /// <para>The count increments when an entry is requeued after a <see cref="Lease"/>
    /// expiry, not when it is first enqueued: a message delivered once and acknowledged
    /// has one attempt, not two.</para>
    ///
    /// <para>Five because the failure this bounds is usually either transient — one retry
    /// fixes it — or permanent, where no number of retries fixes it. Five is comfortably
    /// past the first and cheaply short of infinity.</para>
    ///
    /// <para>Set to <c>0</c> for unlimited retries, which is how Highway behaved before
    /// this option existed. That restores the defect it was added to fix: a permanently
    /// failing message is redelivered forever and, because the queue is FIFO, is retried
    /// ahead of everything behind it.</para>
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>
    /// Maximum entries retained in any one dead-letter list. When full, the oldest is
    /// dropped and the drop is counted and logged. Default: 10,000.
    ///
    /// <para>Bounded because an unattended dead-letter list would otherwise exhaust the
    /// server it exists to protect — a denial of service with good intentions.</para>
    /// </summary>
    public int MaxDeadLetterEntries { get; set; } = 10_000;

    /// <summary>
    /// When <c>true</c>, a pub/sub message returned to its group queue after a lease
    /// expiry becomes claimable only after a delay that grows with the attempt count
    /// (feature 013). <b>Default: false.</b>
    ///
    /// <para><b>Backoff and ordering are mutually exclusive, and ordering wins by
    /// default.</b> Highway redelivers an unacknowledged message at the <i>head</i> of its
    /// group queue precisely so a redelivery keeps its place: a consumer that receives
    /// m1, m2, m3 and fails to acknowledge m1 sees m1, m2, m3 again. Holding m1 for a
    /// backoff delay serves m2 and m3 ahead of it, and that ordering guarantee is a
    /// documented part of the protocol. It is not something to trade away silently.</para>
    ///
    /// <para>Enable it when pacing matters more than order — a subscriber failing for a
    /// structural reason is otherwise retried as fast as it can be polled. The trade is
    /// explicit either way; there is no setting that gives both.</para>
    /// </summary>
    public bool PubSubBackoffEnabled { get; set; }

    /// <summary>
    /// When <c>true</c>, an RPC request returned to the queue after a lease expiry is held
    /// for a backoff delay before becoming claimable. <b>Default: false</b>, and the
    /// default is the interesting part.
    ///
    /// <para>An RPC caller is waiting against <c>CallTimeout</c> — 30 seconds by default —
    /// while <see cref="Lease"/> defaults to 5 minutes. By the time a lease expires and the
    /// first retry is possible, the caller has long since timed out. Adding a backoff
    /// therefore changes nothing for the caller and only delays the eventual dead-letter.
    /// It is worth enabling only where <see cref="Lease"/> has been tuned well below the
    /// client's call timeout, so a retry can still land while somebody is listening.</para>
    /// </summary>
    public bool RpcBackoffEnabled { get; set; }

    /// <summary>
    /// Upper bound on the retry backoff delay. Default: 1 minute.
    ///
    /// <para>The cap matters more than the curve. An uncapped exponential reaches hours by
    /// the twelfth attempt, at which point the message is functionally dead but is still
    /// occupying a live queue and still counting toward nothing.</para>
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Authentication (feature 012). Off by default, which is correct on
    /// <see cref="BindAddress"/>'s loopback default and refused on any other address —
    /// see <see cref="HighwayServerBuilder.WithPassword"/>.
    /// </summary>
    public Security.AuthenticationOptions Authentication { get; set; } = new();

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
