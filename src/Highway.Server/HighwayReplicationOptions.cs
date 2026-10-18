namespace Highway.Server;

/// <summary>
/// Replication knobs (042). Off-path defaults keep a single node writable with no
/// auto-failover — the v1 default is explicit <c>HW.REPL.PROMOTE</c>.
/// </summary>
public sealed class HighwayReplicationOptions
{
    /// <summary>When true this node starts as a replica and will not accept client writes.</summary>
    public bool StartAsReplica { get; set; }

    /// <summary>Slot identity this replica presents in <c>HW.REPL.HELLO</c>.</summary>
    public string ReplicaId { get; set; } = Environment.MachineName;

    /// <summary>Redis-style replica priority. Lowest non-zero promotes first; 0 = never promote.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Endpoint advertised in <c>-NOTPRIMARY</c> replies (<c>host:port</c>).</summary>
    public string? AdvertiseEndpoint { get; set; }

    /// <summary>
    /// The primary's RESP connection string (SE.Redis form). A standby says hello and joins over it; its data then flows
    /// over the primary's replication endpoint, which the primary names in its hello reply (076).
    /// </summary>
    public string? PrimaryServer { get; set; }

    /// <summary>
    /// Where this node's replication listener binds (<c>host:port</c>), separate from the RESP port (076 R5.5). Set, the
    /// node listens from start and a failure to bind stops it. Null: the RESP bind address and the RESP port plus one,
    /// bound only once replication is used — a standby's first hello, or a promotion — so a broker that is not in a
    /// replica set opens no second port.
    /// </summary>
    public string? ReplicationListen { get; set; }

    /// <summary>The listener a node binds when <see cref="ReplicationListen"/> is unset: set by the server from its RESP endpoint.</summary>
    internal string? DefaultReplicationListen { get; set; }

    /// <summary>
    /// The broker's TLS certificate, set by the server when RESP serves TLS (076): replication then runs over TLS too,
    /// as it did when it rode the RESP connection. Null: plain TCP, as RESP is.
    /// </summary>
    internal System.Security.Cryptography.X509Certificates.X509Certificate2? TlsCertificate { get; set; }

    /// <summary>Whether RESP requires client certificates (mutual TLS): replication then requires one from a standby.</summary>
    internal bool TlsRequireClientCertificate { get; set; }

    /// <summary>
    /// The replication endpoint peers dial (<c>host:port</c>). Null: this node's advertised host with the listener's
    /// bound port.
    /// </summary>
    public string? ReplicationAdvertiseEndpoint { get; set; }

    /// <summary>
    /// The replica set's shared replication secret (076 R5.5): both ends prove they know it before any data moves.
    /// Null: the broker password. A node with neither replicates unauthenticated, exactly as its RESP port is open.
    /// </summary>
    public string? ReplicationSecret { get; set; }

    /// <summary>
    /// The SE.Redis connection-string tail (e.g. <c>,password=…</c>) used to authenticate an
    /// auto-rejoin's snapshot/pull to the learned primary (050 T3). Set by the server from its own
    /// authentication at startup — a replica set shares one secret — because the rejoin endpoint is
    /// learned at runtime and carries no credentials of its own. Internal: derived, never user-set,
    /// so it is not part of the configuration schema.
    /// </summary>
    internal string? AuthTail { get; set; }

    /// <summary>Drop a replica slot whose acked watermark lags the primary by more than this many sequences.</summary>
    public ulong SlotLagCapSequences { get; set; } = 100_000;

    /// <summary>
    /// Drop a replica slot that has made no contact (HELLO / PULL / ACK) for this long, even when it
    /// is under the lag cap (050 T4 / F3). Stops a silently-gone replica from reading as <c>Active</c>
    /// on a live primary and inflating the redundancy the dashboard shows. Default 60s;
    /// <see cref="TimeSpan.Zero"/> disables the time-based rule (lag-cap dropping still applies).
    /// </summary>
    public TimeSpan SlotStaleAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Opt-in automatic failover (the two-timeout deadman). Off by default.</summary>
    public bool AutoFailover { get; set; }

    /// <summary>
    /// Automatic rejoin of a demoted ex-primary (050 F2). When a node that believed itself
    /// primary observes a higher epoch and demotes, it records where the new primary is and, on
    /// its next restart, wipes and re-syncs as that primary's replica — restoring redundancy with
    /// no operator step. Its diverged tail is preserved in a reconciliation report first (herd-wins
    /// RPO, C9.2a), never merged. On by default: the alternative (a demoted node stranded forever)
    /// is strictly worse. Set false to keep a demoted node in place for manual recovery.
    /// </summary>
    public bool AutoRejoin { get; set; } = true;

    /// <summary>Primary fences itself after this long without replica or witness contact. Default 5s (OD1).</summary>
    public TimeSpan FenceTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Replica may self-promote after this long of primary silence. Must be &gt; FenceTimeout + Margin.</summary>
    public TimeSpan PromoteTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Clock-rate / scheduling slack. Default 1s (OD1).</summary>
    public TimeSpan Margin { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The willingness threshold <c>W</c> (042-1a): a standby answers a client handshake
    /// "willing" only after its own link to the master has been silent this long (or after
    /// GOODBYE). Contract ordering: client health timeout <c>x</c> (3s default) ≤ W &lt;
    /// <see cref="FenceTimeout"/>. Default 3s.
    /// </summary>
    public TimeSpan WillingnessThreshold { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// GOODBYE drain bound (042-1c / parent R12.3, OD7): how long a departing master lets
    /// in-flight work complete before it stands down regardless. Anything undrained falls
    /// back to the clients' ordinary replay path — a maintenance departure is never held
    /// hostage by one stuck message. Default 5s.
    /// </summary>
    public TimeSpan GoodbyeDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>WAL retention time. A replica silent longer than this re-bootstraps (it is never fed a gapped stream). Default 24h.</summary>
    public long WalTtlSeconds { get; set; } = 86_400;

    /// <summary>WAL retention size cap — a dead replica can never fill the disk. Default 1 GiB.</summary>
    public long MaxTotalWalSizeBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Clock used by the deadman (inject a fake clock in tests).</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Refuses to start auto-failover unless <c>T_promote &gt; T_fence + margin</c>; refuses non-positive WAL retention.</summary>
    public void Validate()
    {
        if (WalTtlSeconds <= 0)
            throw new InvalidOperationException($"Replication WalTtlSeconds must be positive, but was {WalTtlSeconds}.");
        if (MaxTotalWalSizeBytes <= 0)
            throw new InvalidOperationException($"Replication MaxTotalWalSizeBytes must be positive, but was {MaxTotalWalSizeBytes}.");

        if (GoodbyeDrainTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException($"Replication GoodbyeDrainTimeout must be positive, but was {GoodbyeDrainTimeout}.");

        // 042-1a A-R2.3: a standby must become willing before the fence backstop is the only
        // thing standing — 0 < W < T_fence, config-checked, not hoped.
        if (WillingnessThreshold <= TimeSpan.Zero || WillingnessThreshold >= FenceTimeout)
        {
            throw new InvalidOperationException(
                $"Replication WillingnessThreshold ({WillingnessThreshold}) must be positive and less than " +
                $"FenceTimeout ({FenceTimeout}) — the herd contract's x ≤ W < T_fence ordering.");
        }

        if (!AutoFailover) return;
        if (PromoteTimeout <= FenceTimeout + Margin)
        {
            throw new InvalidOperationException(
                $"Replication auto-failover invalid: PromoteTimeout ({PromoteTimeout}) must be greater than " +
                $"FenceTimeout ({FenceTimeout}) + Margin ({Margin}).");
        }
    }
}
