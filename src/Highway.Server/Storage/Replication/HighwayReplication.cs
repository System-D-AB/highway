using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Highway.Server.Storage.Crystal;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using SystemD.Crystal;
using CrystalRole = SystemD.Crystal.ReplicationRole;

namespace Highway.Server.Storage.Replication;

internal enum ReplicaRole { Primary, Replica, Fenced, Demoted }
internal enum SlotState { Active, Lagging, Dropped }

/// <summary>
/// Highway's replication <b>policy</b> on SystemD.Crystal (076 R5). Crystal moves the data — its own port and protocol,
/// epochs, snapshot bootstrap, gaps, divergence, reconciliation reports, rejoin — and decides nothing. This class keeps
/// what Highway decides, unchanged from the RocksDB feeder it replaces: the role and the write gate, herd willingness,
/// the higher-peer check, the roster, the deadman fence, herd-arrival promotion, GOODBYE, <c>-NOTPRIMARY</c>.
///
/// <para><b>Roles.</b> <see cref="ReplicaRole.Primary"/> is Crystal's primary; <see cref="ReplicaRole.Replica"/> and
/// <see cref="ReplicaRole.Demoted"/> are Crystal standbys, following or not. <see cref="ReplicaRole.Fenced"/> is
/// Highway's own gate on a Crystal primary: Highway's fence lifts when contact returns, at the same epoch, which
/// Crystal's fence (lifted only by a new epoch) cannot express.</para>
///
/// <para><b>Epoch.</b> Crystal's <c>node.epoch</c>, or a higher one Highway has heard of (a promotion announce) that
/// Crystal adopts once it follows the new primary.</para>
///
/// <para><b>Contact (042-1a).</b> <see cref="LastPeerContact"/> is the last <c>HW.REPL.HELLO</c> exchange: on a primary,
/// a standby's hello arriving; on a standby, its own hello answered (<see cref="StandbyAgent"/>, every 200 ms). Crystal's
/// pull is a long poll that moves nothing while idle, so it cannot say whether the master is alive.</para>
/// </summary>
internal sealed class HighwayReplication : IDisposable
{
    private readonly CrystalHighwayStore _store;
    private readonly CrystalReplication _crystal;
    private readonly ConcurrentDictionary<string, ReplicaSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _standbyNames = new(StringComparer.Ordinal);   // Crystal node id → replica id
    private readonly ConcurrentQueue<string> _dropEvents = new();
    private readonly ConcurrentQueue<string> _transitions = new();
    private DateTimeOffset? _leadershipSince;
    private readonly Lock _roleLock = new();
    private readonly Timer? _deadman;
    private readonly CancellationTokenSource _peerWatchCts = new();
    private readonly ConcurrentDictionary<string, PeerObservation> _peerObservations = new(StringComparer.OrdinalIgnoreCase);
    private StandbyAgent? _agent;
    private bool _rejoinScheduled;
    private ulong _observedEpoch;
    private int _disposed;

    private string? _knownPrimaryEndpoint;
    private string? _knownPrimaryReplicationEndpoint;

    public ILogger? Logger { get; set; }

    private void Note(string transition, LogLevel level = LogLevel.Information)
    {
        _transitions.Enqueue(transition);
        while (_transitions.Count > 256) _transitions.TryDequeue(out _);
        Logger?.Log(level, "[replication] {Transition}", transition);
    }

    /// <summary>The Crystal replication options for <paramref name="options"/> (076 R5.5): listen, secret, slot policy, WAL retention.</summary>
    internal static ReplicationOptions CrystalOptionsFor(HighwayReplicationOptions options) => new()
    {
        ListenEndpoint = options.ReplicationListen ?? options.DefaultReplicationListen,
        Secret = options.ReplicationSecret is { Length: > 0 } secret ? secret : UnauthenticatedSecret,
        LagCap = options.SlotLagCapSequences > long.MaxValue ? long.MaxValue : (long)options.SlotLagCapSequences,
        SlotStaleAfter = options.SlotStaleAfter > TimeSpan.Zero ? options.SlotStaleAfter : TimeSpan.FromDays(3650),
        WalTtl = TimeSpan.FromSeconds(options.WalTtlSeconds),
        WalBytesPerDatabase = options.MaxTotalWalSizeBytes,
        // TLS as on RESP (076): a broker that serves TLS replicates over TLS with the same certificate. A replica set
        // shares it, so a self-signed one is trusted as itself; a CA-issued one chains to the operating system's trust.
        Certificate = options.TlsCertificate,
        RequireClientCertificate = options.TlsCertificate is not null && options.TlsRequireClientCertificate,
        TrustedCertificates = options.TlsCertificate is { } cert && cert.Subject == cert.Issuer ? [cert] : null,
    };

    /// <summary>The secret of a replica set with no password and no replication secret: open, as its RESP port is.</summary>
    internal const string UnauthenticatedSecret = "highway-replication-unauthenticated";

    public HighwayReplication(CrystalHighwayStore store, HighwayReplicationOptions options, ILogger? logger)
    {
        _store = store;
        _crystal = store.Crystal.Replication;
        Options = options;
        Logger = logger;
        LastPeerContact = options.Clock.GetUtcNow();
        SelfEndpoint = options.AdvertiseEndpoint ?? "127.0.0.1:6500";
        _crystal.Events += OnCrystalEvent;

        if (options.AutoFailover && ReferenceEquals(options.Clock, TimeProvider.System))
            _deadman = new Timer(_ => TickDeadman(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

        // 069 R1: herd-arrival promotion does not depend on AutoFailover, so neither does the check that guards it.
        if (ReferenceEquals(options.Clock, TimeProvider.System) && options.Priority > 0)
        {
            PeerProbe = HigherPeerWatch.NetworkProbe(this);
            _ = Task.Run(() => PeerWatchLoopAsync(_peerWatchCts.Token));
        }
    }

    /// <summary>
    /// Settles the starting role and starts replication (076 R5.6): listen, then — as a standby — say hello to the
    /// primary and follow it. A rejoin scheduled before this start (a demoted ex-primary) makes this node a standby of
    /// the primary it names; Crystal already re-bootstraps from it.
    /// </summary>
    internal void Start()
    {
        // L2: an epoch heard of before Crystal adopted it by following survives a restart, as v3.0's epoch file did.
        Interlocked.Exchange(ref _observedEpoch, NodeRecord.ReadObservedEpoch(_store.Crystal));

        // A node whose data is already on disk was a complete standby before it stopped: it is one again from the moment
        // it starts, as on v3.0 — it must be, or a standby restarting while its primary is dead could never take over.
        if (HoldsReplicatedData())
            Interlocked.Exchange(ref _readySinceTicks, LastPeerContact.UtcTicks);

        var rejoinRecord = NodeRecord.ReadRejoin(_store.Crystal);
        if (rejoinRecord is not null && !Options.AutoRejoin)
        {
            NodeRecord.ClearRejoin(_store.Crystal);   // auto-rejoin was turned off: the operator's config decides
            rejoinRecord = null;
        }

        if (rejoinRecord is { } rejoin)
        {
            Options.StartAsReplica = true;
            Options.PrimaryServer = rejoin + (Options.AuthTail ?? "");
            Logger?.LogWarning("[replication] rejoining as a replica of {Primary}", HostOf(rejoin) ?? rejoin);
        }

        if (Options.StartAsReplica)
        {
            if (_crystal.Role == CrystalRole.Primary)
                _crystal.Demote();   // a fresh store is a Crystal primary at epoch 1; a configured standby never writes
            _role = ReplicaRole.Replica;
        }
        else
        {
            if (_crystal.Role == CrystalRole.Primary)
                _role = ReplicaRole.Primary;
            else if (_crystal.Status().FollowedPrimary is not null)
                _role = ReplicaRole.Replica;
            else
            {
                // A node configured as primary comes back primary, as on v3.0, even if it last stood down (a GOODBYE, a
                // config flipped from standby, a wipe). Crystal persists a stood-down node as a standby, so it is
                // promoted here, past any epoch this node has heard of. A demoted node that should rejoin has a rejoin
                // record and took the standby branch above.
                PromoteCrystal("configured primary at start");
                _role = ReplicaRole.Primary;
                Note($"start-promote epoch={Epoch} reason=configured-primary");
            }
        }

        if (Options.ReplicationListen is not null)
        {
            _crystal.Listen();   // configured: listen now, and a failure to bind stops the node
            WarnIfUnauthenticated();
        }

        if (Options.StartAsReplica && !string.IsNullOrWhiteSpace(Options.PrimaryServer))
            _agent = new StandbyAgent(this, Options, Logger);
    }

    public HighwayReplicationOptions Options { get; }

    /// <summary>This node's epoch: Crystal's, or a higher one heard of and not yet adopted by following.</summary>
    public ulong Epoch => Math.Max((ulong)_crystal.Epoch, Interlocked.Read(ref _observedEpoch));

    public ReplicaRole Role
    {
        get => _role;
        private set => _role = value;
    }
    private volatile ReplicaRole _role;

    public string SelfEndpoint { get; set; }

    /// <summary>The Crystal endpoint peers follow this node at (076): advertised, else this host with the bound port.</summary>
    public string ReplicationEndpoint
    {
        get
        {
            if (Options.ReplicationAdvertiseEndpoint is { Length: > 0 } advertised) return advertised;
            if (_crystal.ListenerEndpoint is not { } bound) return "";
            var port = bound[(bound.LastIndexOf(':') + 1)..];
            var self = HostOf(SelfEndpoint) ?? SelfEndpoint;
            var host = self.Contains(':') ? self[..self.LastIndexOf(':')] : self;
            return $"{host}:{port}";
        }
    }

    /// <summary>The Crystal node id of this store (the id standbys are known by in Crystal's slots).</summary>
    public string CrystalNodeId => _store.Crystal.NodeId;

    public DateTimeOffset LastPeerContact { get; private set; }
    public DateTimeOffset? LastPromotion { get; private set; }
    public string? LastPromotionReason { get; private set; }
    public string? LastReconciliationPath { get; private set; }
    internal IReadOnlyCollection<string> DropEvents => _dropEvents.ToArray();
    internal IReadOnlyCollection<string> Transitions => _transitions.ToArray();
    internal IReadOnlyDictionary<string, ReplicaSlot> Slots => _slots;

    public bool IsWritable => Role == ReplicaRole.Primary;

    /// <summary>The endpoint a <c>-NOTPRIMARY</c> refusal advertises (G5): self as primary, else the primary it knows.</summary>
    public string RedirectEndpoint()
    {
        if (Role == ReplicaRole.Primary) return SelfEndpoint;
        if (OutrankedByHigherPeer(out var peer) && peer is not null) return peer;
        return _knownPrimaryEndpoint ?? HostOf(Options.PrimaryServer) ?? SelfEndpoint;
    }

    internal static string ConnectionTail(string? server)
        => server is not null && server.Contains(',') ? server[server.IndexOf(',')..] : "";

    internal static string? HostOf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var first = connectionString.Split(',')[0].Trim();
        return first.Contains('=') || first.Length == 0 ? null : first;
    }

    public void NotePeerContact()
    {
        LastPeerContact = Options.Clock.GetUtcNow();
        if (Role == ReplicaRole.Replica)
            HasSeenMaster = true;
    }

    public bool HasSeenMaster { get; private set; }

    // ---- herd state (042-1c) ---------------------------------------------------------------------------------------

    private int _connectedClients;

    public int ConnectedClients => Volatile.Read(ref _connectedClients);
    public void ClientSessionOpened() => Interlocked.Increment(ref _connectedClients);
    public void ClientSessionClosed() => Interlocked.Decrement(ref _connectedClients);

    public Action<string>? Narrator { get; set; }
    public Func<IReadOnlyList<string>>? RosterPeers { get; set; }
    public Func<IReadOnlyList<RosterMember>>? RosterMembers { get; set; }
    internal Func<string, CancellationToken, Task<PeerObservation>>? PeerProbe { get; set; }
    public Action? RegisterSelfInRoster { get; set; }
    public Action? RejoinRequested { get; set; }

    public event Action? OnEpochChanged;
    public event Action? OnPromoted;
    public event Action? OnDemoted;
    public event Action? OnFenced;

    public bool GoodbyeSeen { get; private set; }
    public void NoteGoodbye() => GoodbyeSeen = true;

    public void NoteJoinRefused(string detail)
        => Note($"join-refused at={Options.Clock.GetUtcNow():o} detail={detail}", LogLevel.Warning);

    public bool IsWillingForHerd()
        => Role == ReplicaRole.Replica
           && ReadyAsStandby(out var masterSilence)
           && Willingness.Decide(
               Options.Priority,
               masterSilence,
               GoodbyeSeen,
               Options.WillingnessThreshold,
               HasSeenMaster,
               Options.FenceTimeout)
           && !OutrankedByHigherPeer(out _);

    private long _readySinceTicks;   // 0 = syncing: not a standby yet (no data, or still catching up after a (re-)bootstrap)

    /// <summary>
    /// Whether this node is a <b>standby</b> yet, and how long its primary has been silent since it became one. A node
    /// that is copying the primary's data, or has the snapshot but has not caught up, is <b>syncing</b>: it is never
    /// willing, has lost no primary, and reads <c>bootstrapping</c> on <c>/ready</c>. It becomes a standby the first time
    /// it holds the data and has caught up (lag 0) — the silence clock starts there, not at process start, so a slow
    /// start or a slow copy can never read as a dead primary. A re-bootstrap makes it syncing again. Noted when first
    /// observed, which is never earlier than it happened: the safe direction.
    /// </summary>
    private bool ReadyAsStandby(out TimeSpan masterSilence)
    {
        masterSilence = TimeSpan.Zero;
        if (!HoldsReplicatedData())
        {
            Interlocked.Exchange(ref _readySinceTicks, 0);
            Volatile.Write(ref _pullsConfirmed, false);
            return false;
        }

        var now = Options.Clock.GetUtcNow();
        if (Interlocked.Read(ref _readySinceTicks) == 0)
        {
            if (!CaughtUp()) return false;
            Interlocked.CompareExchange(ref _readySinceTicks, now.UtcTicks, 0);
            Note("standby-ready");
        }
        var readySince = new DateTimeOffset(Interlocked.Read(ref _readySinceTicks), TimeSpan.Zero);
        var lastHeard = LastPeerContact > readySince ? LastPeerContact : readySince;
        masterSilence = now - lastHeard;
        return true;
    }

    /// <summary>
    /// Whether this standby holds its primary's data at all (076): a standby still bootstrapping is up and serving RESP
    /// (on v3.0 it was still inside <c>Open</c>), and must never take the herd with nothing.
    /// </summary>
    internal bool HoldsReplicatedData()
    {
        try { return _store.View() is not null; }
        catch (Exception ex) when (ex is ObjectDisposedException or DatabaseNotFoundException) { return false; }
    }

    /// <summary>
    /// Whether the standby known to Crystal as <paramref name="crystalNodeId"/> is receiving this node's data (H1): its slot
    /// is not dropped and it pulled within <see cref="DataLiveWindow"/> (a healthy follower pulls at least every long
    /// poll, 5 s). Without it a hello proves only that RESP works, not that anything replicates.
    /// </summary>
    internal bool IsDataLive(string? crystalNodeId)
    {
        if (crystalNodeId is null || Role is not (ReplicaRole.Primary or ReplicaRole.Fenced)) return false;
        try
        {
            var now = TimeProvider.System.GetUtcNow();
            return _crystal.Status().Slots.Any(s => s.Standby == crystalNodeId
                                                     && s.State != ReplicationSlotState.Dropped
                                                     && now - s.LastContact <= DataLiveWindow);
        }
        catch (ObjectDisposedException) { return false; }
    }

    internal static readonly TimeSpan DataLiveWindow = TimeSpan.FromSeconds(10);

    private bool _pullsConfirmed;

    /// <summary>The primary answered a hello saying it receives this standby's pulls (<c>dataLive</c>) — since it holds the data.</summary>
    internal void NotePullsConfirmed() => Volatile.Write(ref _pullsConfirmed, true);

    /// <summary>
    /// Whether this node's copy of database <c>highway</c> has caught up with its primary: the primary confirms it is
    /// receiving this standby's pulls, and the last pull reply left nothing behind. Crystal publishes a standby's
    /// position only on a pull reply, and a caught-up standby on an idle primary sits in a long poll (up to 5 s) with
    /// nothing to fetch — so no published position, with pulls confirmed, is caught up. While it works through a
    /// backlog every page returns at once with its lag, and it stays syncing until a page leaves none.
    /// </summary>
    private bool CaughtUp()
    {
        if (!Volatile.Read(ref _pullsConfirmed)) return false;
        try
        {
            return _crystal.Status().Followed.FirstOrDefault(f => f.Name == CrystalHighwayStore.DatabaseName) is not { Lag: > 0 };
        }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>The inputs of <see cref="ReadyAsStandby"/>, for a test that timed out.</summary>
    internal string ReadinessDebug()
    {
        string followed;
        try { followed = string.Join(",", _crystal.Status().Followed.Select(f => $"{f.Name}:pos={f.Position}/latest={f.PrimaryLatest}/lag={f.Lag}")); }
        catch (ObjectDisposedException) { followed = "(closed)"; }
        return $"role={Role} holds={HoldsReplicatedData()} pullsConfirmed={Volatile.Read(ref _pullsConfirmed)} " +
               $"readySince={Interlocked.Read(ref _readySinceTicks)} hasSeenMaster={HasSeenMaster} followed=[{followed}]";
    }

    /// <summary>Settles syncing → standby as soon as it is true (called on every answered hello).</summary>
    internal void RefreshStandbyReadiness()
    {
        if (Role == ReplicaRole.Replica) ReadyAsStandby(out _);
    }

    /// <summary>Whether this node is a standby yet (holds the data and caught up once), or still syncing.</summary>
    internal bool IsReadyStandby => Role == ReplicaRole.Replica && ReadyAsStandby(out _);

    /// <summary>L3: a standby that re-bootstrapped and caught up is no longer "bootstrapping".</summary>
    internal void ClearResyncIfCaughtUp()
    {
        if (!ResyncRequired) return;
        try
        {
            if (_crystal.Status().Followed.FirstOrDefault(f => f.Name == CrystalHighwayStore.DatabaseName) is { Lag: 0, Position: > 0 })
            {
                ResyncRequired = false;
                Note("resync-complete");
            }
        }
        catch (ObjectDisposedException) { }
    }

    private bool MasterLost()
    {
        if (Role != ReplicaRole.Replica || Options.Priority == 0) return false;
        if (!ReadyAsStandby(out var masterSilence)) return false;   // a standby not yet ready has lost nothing
        if (GoodbyeSeen) return true;
        var window = HasSeenMaster ? Options.WillingnessThreshold : Options.FenceTimeout;
        return masterSilence >= window;
    }

    private List<RosterMember> HigherPeers()
    {
        var own = Options.Priority;
        return (RosterMembers?.Invoke() ?? [])
            .Where(m => m.Priority > 0 && m.Priority < own
                        && !string.Equals(m.Endpoint, SelfEndpoint, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    internal bool OutrankedByHigherPeer(out string? redirect)
    {
        redirect = null;
        if (PeerProbe is null || !MasterLost()) return false;
        return HigherPeerWatch.Outranked(Options.Clock.GetUtcNow(), HigherPeers(), _peerObservations, SelfEndpoint, out redirect);
    }

    internal async Task ProbeHigherPeersAsync(CancellationToken ct = default)
    {
        if (PeerProbe is not { } probe || !MasterLost())
        {
            _peerObservations.Clear();
            return;
        }

        var peers = HigherPeers();
        var results = await Task.WhenAll(peers.Select(m => probe(m.Endpoint, ct))).ConfigureAwait(false);
        for (var i = 0; i < peers.Count; i++)
        {
            var endpoint = peers[i].Endpoint;
            _peerObservations[endpoint] = results[i].After(
                _peerObservations.TryGetValue(endpoint, out var previous) ? previous : null);
        }
    }

    private async Task PeerWatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HigherPeerWatch.Interval, ct).ConfigureAwait(false);
                await ProbeHigherPeersAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger?.LogDebug(ex, "[replication] higher-peer probe round failed");
            }
        }
    }

    /// <summary>True while this standby's data is being re-bootstrapped (a gap or divergence; Crystal re-syncs it).</summary>
    public bool ResyncRequired { get; private set; }

    /// <summary>Records the loud half of G4: this standby cannot catch up and is re-bootstrapping.</summary>
    public void NoteResyncRequired(ulong watermark, string detail)
    {
        ResyncRequired = true;
        Note($"resync-required watermark={watermark} at={Options.Clock.GetUtcNow():o} detail={detail}", LogLevel.Warning);
    }

    public ulong MinAckedWatermark()
    {
        ulong? min = null;
        foreach (var slot in _slots.Values)
        {
            if (slot.State == SlotState.Dropped) continue;
            min = min is null ? slot.AckedSeq : Math.Min(min.Value, slot.AckedSeq);
        }
        return min ?? 0;
    }

    // ---- the replica hello (contact, epoch, slots) ---------------------------------------------------------------

    /// <summary>
    /// A standby's hello (its 200 ms heartbeat, or a promoted node's announce). Contact for the deadman; a higher epoch
    /// it carries demotes or is adopted (RD7); an equal-epoch announce from another primary settles the tie (069 R2).
    /// </summary>
    public (ulong Epoch, ulong MinSeq) Hello(string replicaId, ulong lastAppliedSeq, ulong replicaEpoch, string? callerEndpoint = null,
        string? callerReplicationEndpoint = null, string? crystalNodeId = null)
    {
        NotePeerContact();
        EnsureListening();   // a standby says hello before it follows: the listener is up by the time it is needed
        if (crystalNodeId is not null)
            _standbyNames[crystalNodeId] = replicaId;
        if (replicaEpoch >= Epoch)
            ObserveHigherEpoch(replicaEpoch, "HELLO carried a higher epoch", callerEndpoint, callerReplicationEndpoint);

        var isNewSlot = !_slots.ContainsKey(replicaId);
        var contactNow = Options.Clock.GetUtcNow();
        _slots.AddOrUpdate(
            replicaId,
            _ => new ReplicaSlot(replicaId, lastAppliedSeq, replicaEpoch, SlotState.Active, contactNow),
            (_, existing) => existing with
            {
                AckedSeq = Math.Max(existing.AckedSeq, lastAppliedSeq),
                ReplicaEpoch = replicaEpoch,
                State = SlotState.Active,
                LastContact = contactNow,
            });
        if (isNewSlot)
            Logger?.LogInformation("[replication] replica {ReplicaId} attached (position={Position}, epoch={Epoch})",
                replicaId, lastAppliedSeq, replicaEpoch);
        EnforceCap();

        if (callerEndpoint is not null && replicaEpoch == Epoch && Role is ReplicaRole.Primary or ReplicaRole.Fenced)
            YieldIfOutranked(callerEndpoint, callerReplicationEndpoint);

        return (Epoch, MinAckedWatermark());
    }

    private void YieldIfOutranked(string peerEndpoint, string? peerReplicationEndpoint)
    {
        var peer = (RosterMembers?.Invoke() ?? [])
            .FirstOrDefault(m => string.Equals(m.Endpoint, peerEndpoint, StringComparison.OrdinalIgnoreCase));
        if (peer.Endpoint is null || peer.Priority <= 0) return;
        if (Options.Priority > 0 && peer.Priority >= Options.Priority) return;

        var rejoinRequested = false;
        lock (_roleLock)
        {
            if (Role is not (ReplicaRole.Primary or ReplicaRole.Fenced)) return;
            var from = Role;
            StandDown();
            _knownPrimaryEndpoint = peerEndpoint;
            if (peerReplicationEndpoint is not null) _knownPrimaryReplicationEndpoint = peerReplicationEndpoint;
            Note($"demote from={from} epoch={Epoch} reason=equal-epoch-tie-break peer={peer.NodeId} peerPriority={peer.Priority} " +
                 $"file={LastReconciliationPath}", LogLevel.Warning);
            ScheduleRejoinIfConfigured(Epoch, ref rejoinRequested);
        }

        OnDemoted?.Invoke();
        if (rejoinRequested)
            RejoinRequested?.Invoke();
    }

    /// <summary>Stands this node down to a Crystal standby: refuses writes and records its unreplicated tail. Under the role lock.</summary>
    private void StandDown(bool reportEvenIfEmpty = true)
    {
        Role = ReplicaRole.Demoted;
        _slots.Clear();
        try
        {
            var before = _crystal.LastReconciliationReport;
            // Crystal demotes only a primary: a node Highway fenced is unfenced first, under the role lock, so its demote
            // writes the reconciliation report. Nothing writes in between — Highway's own gate still refuses commands —
            // and the unfence cannot be refused here: a fenced Crystal node follows nobody, so its epoch has not moved.
            if (_crystal.Role == CrystalRole.Fenced)
            {
                try { _crystal.Unfence("standing down"); }
                catch (InvalidOperationException ex) { Note($"unfence-before-demote-refused reason={ex.Message}", LogLevel.Warning); }
            }
            _crystal.Demote();
            var crystalReport = _crystal.LastReconciliationReport is { } after && after != before ? after : null;
            if (crystalReport is not null || reportEvenIfEmpty)
                LastReconciliationPath = HighwayReconciliation.Write(_store, Epoch, crystalReport, Options.Clock.GetUtcNow());
        }
        catch (ObjectDisposedException) { /* closing */ }
        catch (IOException ex)
        {
            Note($"reconciliation-report-failed error={ex.Message}", LogLevel.Error);   // stood down regardless
        }
    }

    /// <summary>
    /// Refreshes the slots from Crystal's (positions, lag state) and drops a slot silent past <c>SlotStaleAfter</c>
    /// (050 T4) — what Highway shows; Crystal enforces its own cap on the data path.
    /// </summary>
    public void EnforceCap()
    {
        var now = Options.Clock.GetUtcNow();
        var staleAfter = Options.SlotStaleAfter;
        Dictionary<string, SystemD.Crystal.ReplicationSlot> crystalSlots = [];
        try
        {
            foreach (var s in _crystal.Status().Slots)
                if (_standbyNames.TryGetValue(s.Standby, out var name)) crystalSlots[name] = s;
        }
        catch (ObjectDisposedException) { return; }

        foreach (var (id, slot) in _slots)
        {
            if (slot.State == SlotState.Dropped) continue;

            if (staleAfter > TimeSpan.Zero && slot.LastContact != default && now - slot.LastContact > staleAfter)
            {
                _slots[id] = slot with { State = SlotState.Dropped };
                var silentMs = (now - slot.LastContact).TotalMilliseconds;
                _dropEvents.Enqueue($"slot-dropped-stale replica={id} silentMs={silentMs:F0} bound={staleAfter}");
                Logger?.LogWarning("[replication] replica {ReplicaId} slot dropped: silent {SilentMs:F0} ms exceeds {Bound}",
                    id, silentMs, staleAfter);
                continue;
            }

            if (!crystalSlots.TryGetValue(id, out var cs))
            {
                // H1: a standby that says hello but pulls nothing (wrong endpoint, firewall, secret) is not redundancy.
                if (slot.State == SlotState.Active) _slots[id] = slot with { State = SlotState.Lagging };
                continue;
            }
            var state = cs.State switch
            {
                ReplicationSlotState.Dropped => SlotState.Dropped,
                ReplicationSlotState.Lagging => SlotState.Lagging,
                _ => SlotState.Active,
            };
            if (state == SlotState.Dropped && slot.State != SlotState.Dropped)
                _dropEvents.Enqueue($"slot-dropped replica={id} lag={cs.LagSequences} cap={Options.SlotLagCapSequences}");
            _slots[id] = slot with { AckedSeq = (ulong)Math.Max(0, cs.Acknowledged), State = state, Lag = (ulong)Math.Max(0, cs.LagSequences) };
        }
    }

    // ---- promote, fence, epochs ----------------------------------------------------------------------------------

    public bool TryPromote(string reason, out string? error)
    {
        error = null;
        ulong announceEpoch;

        lock (_roleLock)
        {
            if (Role == ReplicaRole.Primary)
                return true;
            if (Options.Priority == 0)
            {
                error = "priority 0 never promotes";
                Note($"promote-refused reason=priority-0 at={Options.Clock.GetUtcNow():o}", LogLevel.Warning);
                return false;
            }

            try
            {
                PromoteCrystal(reason);
            }
            catch (IOException ex)
            {
                error = $"promotion failed: {ex.Message}";
                Note($"promote-failed reason={reason} error={ex.Message}", LogLevel.Error);
                return false;
            }

            Role = ReplicaRole.Primary;
            EnsureListening();   // the announce names this node's replication endpoint
            ResyncRequired = false;
            _rejoinScheduled = false;
            try { NodeRecord.SetObservedEpoch(_store.Crystal, 0); }   // Crystal's epoch is now the highest known
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { NodeRecord.ClearRejoin(_store.Crystal); }   // a rejoined replica that is promoted no longer rejoins at start
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            _knownPrimaryEndpoint = null;
            _knownPrimaryReplicationEndpoint = null;
            LastPromotion = Options.Clock.GetUtcNow();
            LastPromotionReason = reason;
            LastPeerContact = LastPromotion.Value;
            _leadershipSince = LastPromotion;
            Note($"promote epoch={Epoch} reason={reason} at={LastPromotion:o}");
            announceEpoch = Epoch;
        }

        // Announce the new epoch to the configured primary and every roster peer (042-1d): a stale ex-primary demotes, and
        // a lower-epoch standby re-points at this node. The announce names this node's replication endpoint too (076).
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HostOf(Options.PrimaryServer) is { } p) targets.Add(p);
        foreach (var endpoint in RosterPeers?.Invoke() ?? [])
        {
            if (!string.Equals(endpoint, SelfEndpoint, StringComparison.OrdinalIgnoreCase))
                targets.Add(endpoint);
        }
        if (targets.Count > 0)
            AnnouncePromotionAsync(targets, announceEpoch);

        RegisterSelfInRoster?.Invoke();
        OnEpochChanged?.Invoke();
        OnPromoted?.Invoke();
        return true;
    }

    /// <summary>
    /// The most an epoch heard from a peer or client may lead this node's own (M2): epochs grow by one per promotion, so
    /// a larger lead is not a replica set's history. Ignoring it keeps one bad handshake from forcing a promotion to burn
    /// through epochs, and from leaving a node unable to promote at all.
    /// </summary>
    internal const ulong MaxEpochLead = 1_000;

    /// <summary>
    /// Promotes Crystal to an epoch above any Highway has heard of, in one persisted step (Crystal 1.0.0-rc.3
    /// <c>Promote(reason, minimumEpoch)</c>). The floor is bounded on the way in by <see cref="MaxEpochLead"/>.
    /// </summary>
    private long PromoteCrystal(string reason)
        => _crystal.Promote(reason, (long)Interlocked.Read(ref _observedEpoch) + 1);

    private void AnnouncePromotionAsync(IReadOnlyCollection<string> endpoints, ulong epoch)
        => _ = Task.Run(async () =>
        {
            var tail = ConnectionTail(Options.PrimaryServer) is { Length: > 0 } t ? t : Options.AuthTail ?? "";

            async Task<bool> TellAsync(string endpoint)
            {
                try
                {
                    var config = ConfigurationOptions.Parse(endpoint + tail);
                    config.ConnectTimeout = 1_000;
                    config.AsyncTimeout = 2_000;
                    config.ConnectRetry = 1;
                    config.AbortOnConnectFail = true;
                    using var mux = await ConnectionMultiplexer.ConnectAsync(config).ConfigureAwait(false);
                    await mux.GetDatabase().ExecuteAsync("HW.REPL.HELLO", HelloArgs(Options.ReplicaId, 0, epoch, SelfEndpoint)).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            var live = endpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (var attempt = 0; attempt < 5 && live.Count > 0; attempt++)
            {
                var told = await Task.WhenAll(live.Select(TellAsync)).ConfigureAwait(false);
                live = live.Where((_, i) => !told[i]).ToList();
                if (live.Count > 0)
                    await Task.Delay(1_000).ConfigureAwait(false);
            }
        });

    /// <summary>
    /// The replica hello's arguments (076): <c>&lt;id&gt; &lt;position&gt; &lt;epoch&gt; [endpoint] FORMAT &lt;n&gt;
    /// REPL &lt;replicationEndpoint&gt; NODE &lt;crystalNodeId&gt;</c>.
    /// </summary>
    internal object[] HelloArgs(string replicaId, long position, ulong epoch, string? endpoint)
    {
        var args = new List<object> { replicaId, position.ToString(CultureInfo.InvariantCulture), epoch.ToString(CultureInfo.InvariantCulture) };
        if (endpoint is not null) args.Add(endpoint);
        args.Add("FORMAT");
        args.Add(HighwayServerBuilder.StorageFormatVersion.ToString(CultureInfo.InvariantCulture));
        if (ReplicationEndpoint is { Length: > 0 } repl)
        {
            args.Add("REPL");
            args.Add(repl);
        }
        args.Add("NODE");
        args.Add(CrystalNodeId);
        return [.. args];
    }

    public void Promote(string reason) => TryPromote(reason, out _);

    public void Fence(string reason)
    {
        lock (_roleLock)
        {
            if (Role != ReplicaRole.Primary) return;
            Role = ReplicaRole.Fenced;
            // The storage refuses writes too (Crystal's fence): Highway's write gate is no longer the only guard.
            try { _crystal.Fence(reason); }
            catch (ObjectDisposedException) { /* closing */ }
            Note($"fence epoch={Epoch} reason={reason} at={Options.Clock.GetUtcNow():o}", LogLevel.Warning);
        }
        OnFenced?.Invoke();
    }

    /// <summary>
    /// The fence backstop lifts at the same epoch when contact returns (Crystal 1.0.0-rc.3 <c>Unfence</c>). Crystal
    /// refuses if another node has been primary since; the node then stays fenced until that higher epoch demotes it.
    /// </summary>
    public void UnfenceIfContact()
    {
        lock (_roleLock)
        {
            if (Role != ReplicaRole.Fenced) return;
            try { _crystal.Unfence("contact returned"); }
            catch (ObjectDisposedException) { return; }   // closing (it derives from InvalidOperationException)
            catch (InvalidOperationException ex)
            {
                Note($"unfence-refused epoch={Epoch} reason={ex.Message}", LogLevel.Warning);
                return;
            }
            Role = ReplicaRole.Primary;
            Note($"unfence epoch={Epoch} at={Options.Clock.GetUtcNow():o}");
        }
    }

    /// <summary>
    /// A higher epoch was observed on any channel (RD7). A node that believed itself primary (or fenced) demotes — its
    /// unreplicated tail goes to Crystal's reconciliation report — and, with auto-rejoin, schedules its rejoin as the new
    /// primary's standby. A replica adopts the epoch; Crystal adopts it as it follows.
    /// </summary>
    public void ObserveHigherEpoch(ulong observedEpoch, string reason, string? primaryEndpoint = null, string? primaryReplicationEndpoint = null)
    {
        var epochChanged = false;
        var rejoinRequested = false;
        var demoted = false;
        if (observedEpoch > Epoch + MaxEpochLead)
        {
            Note($"epoch-ignored observed={observedEpoch} own={Epoch} reason={reason}: more than {MaxEpochLead} ahead", LogLevel.Warning);
            return;
        }

        lock (_roleLock)
        {
            if (observedEpoch < Epoch) return;

            if (primaryEndpoint is not null && (Role != ReplicaRole.Primary || observedEpoch > Epoch))
            {
                _knownPrimaryEndpoint = primaryEndpoint;
                if (primaryReplicationEndpoint is not null) _knownPrimaryReplicationEndpoint = primaryReplicationEndpoint;
                if (Role == ReplicaRole.Demoted)
                    ScheduleRejoinIfConfigured(Math.Max(Epoch, observedEpoch), ref rejoinRequested);
            }

            if (observedEpoch > Epoch)
            {
                if (Role is ReplicaRole.Primary or ReplicaRole.Fenced)
                {
                    var from = Role;
                    StandDown();
                    demoted = true;
                    Note($"demote from={from} observedEpoch={observedEpoch} reason={reason} file={LastReconciliationPath}", LogLevel.Warning);
                    ScheduleRejoinIfConfigured(observedEpoch, ref rejoinRequested);
                }
                else
                {
                    Note($"adopt-epoch from={Epoch} to={observedEpoch} reason={reason}");
                }

                AdoptObserved(observedEpoch);
                _leadershipSince = Options.Clock.GetUtcNow();
                epochChanged = true;
            }
        }

        if (epochChanged)
            OnEpochChanged?.Invoke();
        if (demoted)
            OnDemoted?.Invoke();
        if (rejoinRequested)
            RejoinRequested?.Invoke();
    }

    private void AdoptObserved(ulong epoch)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _observedEpoch);
            if (epoch <= current) return;
            if (Interlocked.CompareExchange(ref _observedEpoch, epoch, current) != current) continue;
            try { NodeRecord.SetObservedEpoch(_store.Crystal, epoch); }   // L2: persisted, as v3.0's epoch file was
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotPrimaryException)
            {
                Note($"epoch-persist-failed epoch={epoch} error={ex.Message}", LogLevel.Error);
            }
            return;
        }
    }

    /// <summary>
    /// Schedules this demoted node's rejoin (050 T2): the new primary's RESP endpoint is kept node-locally, so the next
    /// start is a standby of it — its first hello names the primary's replication endpoint, and Crystal, following it,
    /// finds this node's diverged history and re-bootstraps. When that endpoint is already known, Crystal's own rejoin
    /// is scheduled too, so the re-bootstrap starts at open. At most once; needs the RESP endpoint, as before 076.
    /// </summary>
    private void ScheduleRejoinIfConfigured(ulong primaryEpoch, ref bool rejoinRequested)
    {
        if (!Options.AutoRejoin) return;
        if (Role != ReplicaRole.Demoted) return;
        if (_knownPrimaryEndpoint is not { } target) return;
        if (_rejoinScheduled) return;

        try
        {
            NodeRecord.SetRejoin(_store.Crystal, target);
            if (_knownPrimaryReplicationEndpoint is { } replicationTarget)
                _crystal.ScheduleRejoin(replicationTarget);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or ObjectDisposedException)
        {
            Note($"rejoin-not-scheduled primary={HostOf(target) ?? target} error={ex.Message}", LogLevel.Warning);
            return;
        }
        _rejoinScheduled = true;
        Note($"rejoin-scheduled primary={HostOf(target) ?? target} epoch={primaryEpoch}", LogLevel.Warning);
        rejoinRequested = true;
    }

    public void Demote(ulong observedEpoch, string reason) => ObserveHigherEpoch(observedEpoch, reason);

    /// <summary>The deadman backstop (042-1a D6): only the fence half. Nothing here promotes.</summary>
    public void TickDeadman()
    {
        if (!Options.AutoFailover) return;
        var now = Options.Clock.GetUtcNow();

        if (Role == ReplicaRole.Primary && !IsDraining)
        {
            if (ConnectedClients == 0 && now - LastPeerContact > Options.FenceTimeout)
                Fence("deadman: no peer and no client contact");
        }
        else if (Role == ReplicaRole.Fenced)
        {
            if (now - LastPeerContact <= Options.FenceTimeout)
                UnfenceIfContact();
        }
    }

    // ---- Crystal's events -------------------------------------------------------------------------------------------

    /// <summary>
    /// Crystal reports, Highway decides (076 R5.3). Promotions, demotions and fences Highway asked for are already
    /// handled where it asked; what Crystal does on its own — adopting a primary's epoch, re-bootstrapping after a gap or
    /// divergence, slot changes — resets what Highway holds in memory about the data and is noted.
    /// </summary>
    private void OnCrystalEvent(object? sender, ReplicationEvent e)
    {
        switch (e.Kind)
        {
            case ReplicationEventKind.EpochChanged:
                Note($"crystal-epoch epoch={e.Epoch} {e.Detail}");
                OnEpochChanged?.Invoke();
                break;
            case ReplicationEventKind.Gap:
            case ReplicationEventKind.Divergence:
                if (Role != ReplicaRole.Primary)
                    NoteResyncRequired((ulong)Math.Max(0, FollowedPosition()), $"{e.Kind}: {e.Detail}");
                _store.ForgetView();
                OnEpochChanged?.Invoke();   // the data under every in-memory cache is being replaced
                break;
            case ReplicationEventKind.Demoted:
                OnCrystalDemoted(e);
                break;
            case ReplicationEventKind.SlotDropped:
                _dropEvents.Enqueue($"slot-dropped {e.Detail}");
                break;
            case ReplicationEventKind.FormatRefused:
                NoteJoinRefused(e.Detail);
                break;
            case ReplicationEventKind.Conflict:
            case ReplicationEventKind.PromotedWithPendingResync:
                Note($"{e.Kind} epoch={e.Epoch} {e.Detail}", LogLevel.Warning);
                break;
        }
    }

    /// <summary>
    /// Starts the replication listener if it is not up (076): on a standby's first hello and on promotion. Idempotent.
    /// A failure to bind is loud — logged and in the transition log — and never stops the broker: clients are
    /// served, and the standby, which finds no endpoint in the hello reply, waits and says hello again.
    /// </summary>
    internal void EnsureListening()
    {
        if (_crystal.ListenerEndpoint is not null || Options.ReplicationListen is null && Options.DefaultReplicationListen is null) return;
        try
        {
            _crystal.Listen();
            WarnIfUnauthenticated();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            if (Interlocked.Exchange(ref _listenFailedNoted, 1) == 0)
                Note($"replication-listen-failed endpoint={Options.ReplicationListen ?? Options.DefaultReplicationListen} error={ex.Message}",
                    LogLevel.Error);
        }
    }

    private int _listenFailedNoted;
    private int _unauthenticatedNoted;

    /// <summary>A replica set with no password and no replication secret replicates as openly as its RESP port is open: say so, once.</summary>
    private void WarnIfUnauthenticated()
    {
        if (string.IsNullOrEmpty(Options.ReplicationSecret) && Interlocked.Exchange(ref _unauthenticatedNoted, 1) == 0)
            Note("replication-unauthenticated: no replicationSecret and no broker password — any host that reaches the " +
                 "replication port can follow this node's data. Set a password or server.replication.replicationSecret.",
                LogLevel.Warning);
    }

    /// <summary>
    /// L1: Crystal demoted this primary on its own — a follower showed it a higher epoch on the data path. Highway stands
    /// down too, as it would on hearing that epoch in a hello. A demotion Highway asked for has already changed its role,
    /// so it is not handled twice.
    /// </summary>
    private void OnCrystalDemoted(ReplicationEvent e)
    {
        lock (_roleLock)
        {
            if (Role is not (ReplicaRole.Primary or ReplicaRole.Fenced)) return;
            try { if (_crystal.Role == CrystalRole.Primary) return; }
            catch (ObjectDisposedException) { return; }
            var from = Role;
            Role = ReplicaRole.Demoted;
            _slots.Clear();
            try
            {
                LastReconciliationPath = HighwayReconciliation.Write(_store, Epoch, _crystal.LastReconciliationReport, Options.Clock.GetUtcNow());
            }
            catch (IOException ex) { Note($"reconciliation-report-failed error={ex.Message}", LogLevel.Error); }
            _leadershipSince = Options.Clock.GetUtcNow();
            Note($"demote from={from} epoch={Epoch} reason=storage-saw-higher-epoch detail={e.Detail} file={LastReconciliationPath}", LogLevel.Warning);
        }
        OnEpochChanged?.Invoke();
        OnDemoted?.Invoke();
    }

    /// <summary>Follows <paramref name="replicationEndpoint"/> if this standby is not following it already (the agent's hello told it where).</summary>
    internal void EnsureFollowing(string replicationEndpoint)
    {
        lock (_roleLock)
        {
            if (Role != ReplicaRole.Replica) return;
            if (string.Equals(_crystal.Status().FollowedPrimary, replicationEndpoint, StringComparison.OrdinalIgnoreCase)) return;
            _crystal.Follow(replicationEndpoint);
            WarnIfUnauthenticated();
            _knownPrimaryReplicationEndpoint = replicationEndpoint;
            Note($"follow primary={replicationEndpoint}");
        }
    }

    /// <summary>
    /// Whether Crystal's follower holds an authenticated replication connection to <paramref name="replicationEndpoint"/>
    /// (Crystal 1.0.0-rc.3 <c>ConnectedToPrimary</c>): this standby's own evidence that replication runs (H1). A wrong
    /// advertised host, a closed port or a mismatched secret leaves it false, whatever RESP says.
    /// </summary>
    internal bool ReplicatingFrom(string? replicationEndpoint)
    {
        if (string.IsNullOrEmpty(replicationEndpoint)) return false;
        try
        {
            var status = _crystal.Status();
            return status.ConnectedToPrimary
                   && string.Equals(status.FollowedPrimary, replicationEndpoint, StringComparison.OrdinalIgnoreCase);
        }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>The position this standby has applied (its hello's watermark).</summary>
    internal long FollowedPosition()
    {
        try { return _crystal.Status().Followed.FirstOrDefault(f => f.Name == CrystalHighwayStore.DatabaseName)?.Position ?? 0; }
        catch (ObjectDisposedException) { return 0; }
    }

    // ---- GOODBYE (042-1c / parent R12) -----------------------------------------------------------------------------

    private DateTimeOffset? _drainDeadline;
    private Timer? _drainTimer;

    public bool IsDraining => _drainDeadline is not null;

    public void BeginGoodbye(string reason)
    {
        lock (_roleLock)
        {
            if (Role != ReplicaRole.Primary || IsDraining) return;
            _drainDeadline = Options.Clock.GetUtcNow() + Options.GoodbyeDrainTimeout;
            Note($"goodbye-begin epoch={Epoch} reason={reason} deadline={_drainDeadline:o}");
        }

        Narrator?.Invoke($"GOODBYE {Epoch.ToString(CultureInfo.InvariantCulture)}");

        if (ReferenceEquals(Options.Clock, TimeProvider.System))
            _drainTimer = new Timer(_ => TickGoodbye(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    public void TickGoodbye()
    {
        if (!IsDraining) return;   // every command ticks: never take the role lock unless a drain is running (M3)
        lock (_roleLock)
        {
            if (!IsDraining) return;
            var now = Options.Clock.GetUtcNow();
            if (now < _drainDeadline && ConnectedClients > 0) return;

            var cause = ConnectedClients == 0 ? "herd-left" : "drain-deadline";
            _drainDeadline = null;
            StandDown(reportEvenIfEmpty: false);   // a report only when a tail went unacknowledged
            Note($"goodbye-complete epoch={Epoch} cause={cause} at={now:o}");
        }

        _drainTimer?.Dispose();
        _drainTimer = null;
    }

    public void Tick()
    {
        TickDeadman();
        TickGoodbye();
    }

    public (bool Ready, string Reason) Readiness()
    {
        if (Role == ReplicaRole.Fenced) return (false, "fenced");
        if (IsDraining) return (false, "draining");
        if (ResyncRequired) return (false, "bootstrapping");
        if (Role == ReplicaRole.Replica && !ReadyAsStandby(out _)) return (false, "bootstrapping");   // syncing: not a standby yet
        if (Role == ReplicaRole.Demoted) return (false, "demoted");
        if (Role == ReplicaRole.Replica) return (false, "replica");
        if (Role == ReplicaRole.Primary) return (true, "primary");
        return (false, "unknown");
    }

    public IReadOnlyList<(string Name, string Value)> StatsFields()
    {
        EnforceCap();
        var (ready, readyReason) = Readiness();
        var latest = LatestPosition();
        var fields = new List<(string, string)>
        {
            ("repl.role", Role.ToString()),
            ("repl.epoch", Epoch.ToString(CultureInfo.InvariantCulture)),
            ("repl.endpoint", SelfEndpoint),
            ("repl.replicationEndpoint", ReplicationEndpoint),
            ("repl.redirect", RedirectEndpoint()),
            ("repl.priority", Options.Priority.ToString(CultureInfo.InvariantCulture)),
            ("repl.fenced", (Role == ReplicaRole.Fenced).ToString()),
            ("repl.slots", _slots.Count.ToString(CultureInfo.InvariantCulture)),
            ("repl.minAcked", MinAckedWatermark().ToString(CultureInfo.InvariantCulture)),
            ("repl.latestSeq", latest.ToString(CultureInfo.InvariantCulture)),
            ("repl.lastPromotion", LastPromotion?.ToString("o") ?? ""),
            ("repl.lastPromotionReason", LastPromotionReason ?? ""),
            ("repl.reconciliation", LastReconciliationPath ?? ""),
            ("repl.drops", _dropEvents.Count.ToString(CultureInfo.InvariantCulture)),
            ("repl.resyncRequired", ResyncRequired.ToString()),
            ("repl.clients", ConnectedClients.ToString(CultureInfo.InvariantCulture)),
            ("repl.draining", IsDraining.ToString()),
            ("repl.leadershipSince", _leadershipSince?.ToString("o") ?? ""),
            ("repl.redundancy", ComputeRedundancy()),
            ("repl.ready", ready.ToString()),
            ("repl.readyReason", readyReason),
        };

        var events = _transitions.ToArray();
        var eventFrom = Math.Max(0, events.Length - 10);
        for (var e = eventFrom; e < events.Length; e++)
            fields.Add(($"repl.event.{e - eventFrom}", events[e]));

        var i = 0;
        foreach (var slot in _slots.Values)
        {
            fields.Add(($"repl.slot.{i}.id", slot.ReplicaId));
            fields.Add(($"repl.slot.{i}.acked", slot.AckedSeq.ToString(CultureInfo.InvariantCulture)));
            fields.Add(($"repl.slot.{i}.state", slot.State.ToString()));
            fields.Add(($"repl.slot.{i}.lag", slot.Lag.ToString(CultureInfo.InvariantCulture)));
            i++;
        }

        return fields;
    }

    /// <summary>The latest position of database <c>highway</c> as this node knows it: a primary's from its slots, a standby's applied position.</summary>
    private long LatestPosition()
    {
        try
        {
            var status = _crystal.Status();
            if (status.Followed.FirstOrDefault(f => f.Name == CrystalHighwayStore.DatabaseName) is { } followed)
                return followed.Position;
            return status.Slots.Count == 0 ? 0 : status.Slots.Max(s => s.Acknowledged + s.LagSequences);
        }
        catch (ObjectDisposedException) { return 0; }
    }

    private string ComputeRedundancy()
    {
        var rosterCount = RosterPeers?.Invoke().Count ?? 0;
        var activeStandbys = _slots.Values.Count(s => s.State == SlotState.Active);
        return Role switch
        {
            ReplicaRole.Demoted => "demoted",
            ReplicaRole.Fenced => "fenced",
            ReplicaRole.Replica => "replica",
            ReplicaRole.Primary when rosterCount <= 1 => "single",
            ReplicaRole.Primary when activeStandbys == 0 => "no-standby",
            _ => "healthy",
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _crystal.Events -= OnCrystalEvent;
        _peerWatchCts.Cancel();
        try { _agent?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* shutdown */ }
        _deadman?.Dispose();
        _drainTimer?.Dispose();
    }
}

internal readonly record struct ReplicaSlot(string ReplicaId, ulong AckedSeq, ulong ReplicaEpoch, SlotState State = SlotState.Active,
    DateTimeOffset LastContact = default, ulong Lag = 0);

/// <summary>
/// Node-local Highway state that must survive a restart and must never replicate (076): the RESP endpoint of the
/// primary a demoted node rejoins. Database <c>highway.node</c> (not replicated) in the broker's Crystal store, because
/// Crystal owns the whole data directory.
/// </summary>
internal static class NodeRecord
{
    private const string DatabaseName = "highway.node";
    private const string BoxName = "node";
    private static readonly byte[] RejoinKey = "rejoin"u8.ToArray();

    private static (CrystalDatabase Db, SystemD.Crystal.Box Box) Open(CrystalStore store)
    {
        CrystalDatabase db;
        try { db = store.OpenDatabase(DatabaseName); }
        catch (DatabaseNotFoundException) { db = store.CreateDatabase(DatabaseName, replicated: false); }
        return (db, db.Boxes.Find(BoxName) ?? db.Boxes.Create(BoxName));
    }

    private static readonly byte[] ObservedEpochKey = "observed-epoch"u8.ToArray();

    /// <summary>The highest epoch this node heard of that its storage has not adopted yet (L2), or 0.</summary>
    public static ulong ReadObservedEpoch(CrystalStore store)
    {
        var (db, box) = Open(store);
        using var snapshot = db.Snapshot();
        return snapshot.Get(box, ObservedEpochKey) is { Length: 8 } raw ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(raw) : 0;
    }

    public static void SetObservedEpoch(CrystalStore store, ulong epoch)
    {
        var (db, box) = Open(store);
        var raw = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(raw, epoch);
        using var batch = db.NewBatch();
        batch.Put(box, ObservedEpochKey, raw);
        batch.Commit(DurabilityMode.Strict);
    }

    public static void SetRejoin(CrystalStore store, string primaryEndpoint)
    {
        var (db, box) = Open(store);
        using var batch = db.NewBatch();
        batch.Put(box, RejoinKey, Encoding.UTF8.GetBytes(primaryEndpoint));
        batch.Commit(DurabilityMode.Strict);
    }

    /// <summary>
    /// The rejoin record, or null when none. It stays while the node is the rejoined primary's standby — a restart
    /// mid-bootstrap must still know whom to say hello to — and is cleared when the node is promoted.
    /// </summary>
    public static string? ReadRejoin(CrystalStore store)
    {
        var (db, box) = Open(store);
        using var snapshot = db.Snapshot();
        return snapshot.Get(box, RejoinKey) is { } raw ? Encoding.UTF8.GetString(raw) : null;
    }

    public static void ClearRejoin(CrystalStore store)
    {
        var (db, box) = Open(store);
        using var batch = db.NewBatch();
        batch.Delete(box, RejoinKey);
        batch.Commit(DurabilityMode.Strict);
    }
}
