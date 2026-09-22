using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using RocksDbSharp;
using StackExchange.Redis;

namespace Highway.Server.Storage.Rocks;

internal enum ReplicaRole { Primary, Replica, Fenced, Demoted }
internal enum SlotState { Active, Lagging, Dropped }

/// <summary>
/// The primary's pull cursor no longer reaches the retained WAL — the replica must
/// re-sync via <c>HW.REPL.SNAPSHOT</c> (042 G4). Served over the wire as
/// <c>ERR HW_REPL_GAP</c>; never a silently gapped page.
/// </summary>
internal sealed class ReplicationGapException(ulong requestedSeq, ulong firstAvailableSeq)
    : Exception($"WAL no longer reaches back to {requestedSeq}; first available batch is {firstAvailableSeq}")
{
    public ulong RequestedSeq { get; } = requestedSeq;
    public ulong FirstAvailableSeq { get; } = firstAvailableSeq;
}

/// <summary>
/// Node-side replication runtime (042 T1–T7, refined by 042-1): WAL feeder, slots+cap,
/// snapshot capture, epoch/role, the fence backstop, and the willingness answer.
///
/// <para><b>Epoch durability (G2).</b> The epoch is node-local state that must survive a
/// restart but must NOT ride the replicated WAL (a shipped write would clobber the
/// replica's own epoch), so it lives in a small file beside the store
/// (<c>repl-epoch.txt</c>), rewritten atomically on every change. Changes are rare —
/// promote, demote, adopt — so the cost is nil.</para>
///
/// <para><b>One contact clock (042-1a).</b> <see cref="LastPeerContact"/> is the last
/// successful peer exchange — on a primary, inbound HELLO/PULL/ACK; on a standby, its own
/// pull-loop success against the master. It feeds the fence backstop
/// (<see cref="TickDeadman"/> — the timer never promotes; promotion is herd-driven) and
/// the willingness answer (<see cref="IsWillingForHerd"/>), the herd contract's no-split
/// rule. The 042 witness apparatus is deleted (042-1a reconciliation map).</para>
/// </summary>
internal sealed class ReplicationFeeder : IDisposable
{
    private const string EpochFileName = "repl-epoch.txt";

    private readonly ReplicationSource _source;
    private readonly ConcurrentDictionary<string, ReplicaSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SnapshotSessionState> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _dropEvents = new();
    private readonly ConcurrentQueue<string> _transitions = new();
    private DateTimeOffset? _leadershipSince;   // when this node's current epoch/leadership was established (050 T5)
    private readonly string _dataDir;
    private readonly Lock _roleLock = new();
    private readonly Timer? _deadman;

    private string? _knownPrimaryEndpoint;

    /// <summary>
    /// Optional log sink (feature 047). Set by the host after the store opens; when present, every
    /// role/topology transition and slot change is written here as well as to the in-memory tail
    /// <c>HW.REPL.STATUS</c> serves, so an operator sees replication in <c>logs/</c> without tooling.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>Records a transition to the in-memory tail AND the log (047). Transitions are the
    /// only enqueue sites and none is on the per-pull path, so this stays milestone-level.</summary>
    private void Note(string transition, LogLevel level = LogLevel.Information)
    {
        _transitions.Enqueue(transition);
        while (_transitions.Count > 256) _transitions.TryDequeue(out _);   // bounded event tail (050 T5)
        Logger?.Log(level, "[replication] {Transition}", transition);
    }

    public ReplicationFeeder(RocksDb db, string dataDir, HighwayReplicationOptions options)
    {
        Engine = db;
        _dataDir = dataDir;
        Options = options;
        _source = new ReplicationSource(db);
        Epoch = LoadPersistedEpoch();
        Role = options.StartAsReplica ? ReplicaRole.Replica : ReplicaRole.Primary;
        LastPeerContact = options.Clock.GetUtcNow();
        SelfEndpoint = options.AdvertiseEndpoint ?? "127.0.0.1:6500";
        if (options.AutoFailover && ReferenceEquals(options.Clock, TimeProvider.System))
            _deadman = new Timer(_ => TickDeadman(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
    }

    public HighwayReplicationOptions Options { get; }
    public ulong Epoch { get; private set; }
    public ReplicaRole Role { get; private set; }

    /// <summary>This node's own advertised address (config; falls back to the bind endpoint).</summary>
    public string SelfEndpoint { get; set; }

    public DateTimeOffset LastPeerContact { get; private set; }
    public DateTimeOffset? LastPromotion { get; private set; }
    public string? LastPromotionReason { get; private set; }
    public string? LastReconciliationPath { get; private set; }
    internal RocksDb Engine { get; }
    internal IReadOnlyCollection<string> DropEvents => _dropEvents.ToArray();
    internal IReadOnlyCollection<string> Transitions => _transitions.ToArray();
    internal IReadOnlyDictionary<string, ReplicaSlot> Slots => _slots;

    public bool IsWritable => Role == ReplicaRole.Primary;

    /// <summary>
    /// The endpoint a <c>-NOTPRIMARY</c> refusal advertises (G5): a primary names itself;
    /// everyone else names the primary it knows — the endpoint a higher-epoch HELLO
    /// announced, else the configured <c>PrimaryServer</c> host, else (last resort) self.
    /// </summary>
    public string RedirectEndpoint()
    {
        if (Role == ReplicaRole.Primary) return SelfEndpoint;
        return _knownPrimaryEndpoint
            ?? HostOf(Options.PrimaryServer)
            ?? SelfEndpoint;
    }

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
            HasSeenMaster = true;   // the willingness clock now measures LOSS, not absence
    }

    /// <summary>True once this standby's puller has completed a real exchange with its master.</summary>
    public bool HasSeenMaster { get; private set; }

    // ---- herd state (042-1c C-T1) --------------------------------------------

    private int _connectedClients;

    /// <summary>
    /// Authenticated client sessions currently connected (peer/replica connections
    /// excluded). This is the thing that actually defines mastership in 042-1 — the herd —
    /// and the early-exit condition for a GOODBYE drain.
    /// </summary>
    public int ConnectedClients => Volatile.Read(ref _connectedClients);

    public void ClientSessionOpened() => Interlocked.Increment(ref _connectedClients);

    public void ClientSessionClosed() => Interlocked.Decrement(ref _connectedClients);

    /// <summary>
    /// The narration publisher (042-1c C-T3): rings <c>hw:door:topology</c> on the herd's
    /// subscribed connections. Set by the RESP server at wiring time; null in socketless
    /// harnesses — narration is advisory, so absence breaks nothing.
    /// </summary>
    public Action<string>? Narrator { get; set; }

    /// <summary>
    /// Reads the current roster's peer endpoints (042-1d): set by the owning store after
    /// construction, so a promotion can announce its new epoch to every known peer, not
    /// just the configured primary. Null → only the configured primary is told.
    /// </summary>
    public Func<IReadOnlyList<string>>? RosterPeers { get; set; }

    /// <summary>
    /// Registers this node into the replicated roster (050 T1 / F1). Set by the owning store,
    /// which holds the <c>IHighwayStore</c> the feeder itself does not; fired by
    /// <see cref="TryPromote"/> after the promotion announce so a node that <b>promotes</b> (not
    /// only one that <b>starts</b> primary) appears in the roster — the successor priority map and
    /// the dashboard succession view stay complete after a failover instead of going blank exactly
    /// when the cluster needs its own membership. Idempotent: the upsert is keyed on the node id.
    /// Null in socketless harnesses — nothing else depends on it.
    /// </summary>
    public Action? RegisterSelfInRoster { get; set; }

    /// <summary>
    /// Fired when a demotion has scheduled an auto-rejoin (050 T2/T3): a rejoin marker is now on
    /// disk naming the new primary, and a restart will re-sync this node as its replica. The host
    /// wires this to a graceful stop so an external supervisor restarts the process at once; if it
    /// is left null the marker is still honoured on the next restart from any cause, so rejoin is
    /// never lost, only delayed. Fired outside the role lock. No-op when <c>AutoRejoin</c> is off.
    /// </summary>
    public Action? RejoinRequested { get; set; }

    /// <summary>
    /// Raised after the node's epoch changes (promotion, or adopting a higher epoch). The
    /// broker-local cache subscribes to wipe itself (044 R6): an epoch change is exactly
    /// the condition "another node may have written the underlying data since this node
    /// last owned it," so any cached copy is suspect. Fired outside the role lock.
    /// </summary>
    public event Action? OnEpochChanged;

    /// <summary>
    /// Role-transition notifications for metrics (feature 051): fired once per promotion, demotion,
    /// and self-fence. The host subscribes <c>HighwayMetrics</c> counters here — the feeder stays
    /// oblivious to metrics, exactly as it does to logging (an optional <see cref="Logger"/>).
    /// Fired outside the role lock.
    /// </summary>
    public event Action? OnPromoted;
    public event Action? OnDemoted;
    public event Action? OnFenced;

    /// <summary>The master promised to leave (GOODBYE, 042-1) — a standby is immediately willing.</summary>
    public bool GoodbyeSeen { get; private set; }

    /// <summary>Records an observed GOODBYE from the master (narration or peer notice, 042-1c wires the sources).</summary>
    public void NoteGoodbye() => GoodbyeSeen = true;

    /// <summary>A JOIN refused for a held priority (parent R13.3) — loud, in the transition log, never fatal.</summary>
    public void NoteJoinRefused(string detail)
        => Note($"join-refused at={Options.Clock.GetUtcNow():o} detail={detail}", LogLevel.Warning);

    /// <summary>
    /// The herd contract's willingness answer (042-1a D1): may this node accept an arriving
    /// herd? Only a Replica can be willing — a Fenced/Demoted ex-master redirects instead —
    /// and only when its own master-link is dead past the threshold (or GOODBYE was seen).
    /// </summary>
    public bool IsWillingForHerd()
        => Role == ReplicaRole.Replica
           && Willingness.Decide(
               Options.Priority,
               Options.Clock.GetUtcNow() - LastPeerContact,
               GoodbyeSeen,
               Options.WillingnessThreshold,
               HasSeenMaster,
               Options.FenceTimeout);

    /// <summary>True once a WAL gap forced this node into resync-required state (G4).</summary>
    public bool ResyncRequired { get; private set; }

    /// <summary>Records the loud half of G4: this replica cannot catch up and must re-bootstrap.</summary>
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

    public (ulong Epoch, ulong MinSeq) Hello(string replicaId, ulong lastAppliedSeq, ulong replicaEpoch, string? callerEndpoint = null)
    {
        NotePeerContact();
        // >= not >: an equal-epoch HELLO carrying an endpoint is the promoted node's
        // announce arriving after a plain HELLO already spread the epoch — the endpoint
        // refresh must still land (ObserveHigherEpoch's own guards make ties a no-op
        // beyond that refresh).
        if (replicaEpoch >= Epoch)
            ObserveHigherEpoch(replicaEpoch, "HELLO carried a higher epoch", callerEndpoint);

        // 047: log a first attach (or re-attach after a drop). HELLO recurs on every pull, so this
        // is guarded to only fire when the slot is genuinely new/absent — not per HELLO.
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
            Logger?.LogInformation("[replication] replica {ReplicaId} attached (watermark={Watermark}, epoch={Epoch})",
                replicaId, lastAppliedSeq, replicaEpoch);
        EnforceCap();
        return (Epoch, MinAckedWatermark());
    }

    public bool Ack(string replicaId, ulong appliedSeq)
    {
        NotePeerContact();
        if (!_slots.TryGetValue(replicaId, out var slot) || slot.State == SlotState.Dropped)
            return false;
        // Any ack is a contact — refresh liveness even when it does not advance the watermark (050 T4).
        _slots[replicaId] = slot with
        {
            AckedSeq = Math.Max(slot.AckedSeq, appliedSeq),
            State = SlotState.Active,
            LastContact = Options.Clock.GetUtcNow(),
        };
        EnforceCap();
        return true;
    }

    /// <summary>
    /// A page of WAL batches from <paramref name="fromSeq"/>. Refuses with
    /// <see cref="ReplicationGapException"/> when the retained WAL no longer reaches back
    /// that far (G4) — a gapped stream must never be served as if contiguous.
    /// </summary>
    public ReplicationPage Pull(ulong fromSeq, int maxBytes)
    {
        NotePeerContact();
        EnforceCap();
        if (maxBytes < 1) maxBytes = 1;

        var batches = new List<(ulong Seq, byte[] Data)>();
        ulong pageBytes = 0;
        ulong? nextSeq = null;
        var latestAtStart = Engine.GetLatestSequenceNumber();

        foreach (var pooled in _source.GetPooledWalUpdates(fromSeq))
        {
            try
            {
                // 058 R2: GetUpdatesSince starts at the batch that CONTAINS fromSeq, so the first
                // batch is always one the replica already has. Skip any batch whose last sequence
                // is at or below the watermark — otherwise a caught-up replica is served its own
                // last batch on every pull and busy-loops (058 bug 1). The pooled buffer is still
                // returned by the finally below.
                var lastSeq = pooled.SequenceNumber + WriteBatchCount(pooled.PooledData, pooled.Length) - 1;
                if (lastSeq <= fromSeq)
                    continue;

                // The first batch KEPT must actually follow the cursor. GetUpdatesSince on a
                // trimmed WAL starts at what still exists — detected here, refused loudly.
                if (batches.Count == 0 && pooled.SequenceNumber > fromSeq + 1)
                    throw new ReplicationGapException(fromSeq, pooled.SequenceNumber);

                var length = pooled.Length;
                if (batches.Count > 0 && pageBytes + (ulong)length > (ulong)maxBytes)
                {
                    nextSeq = pooled.SequenceNumber;
                    break;
                }

                var owned = pooled.PooledData.AsSpan(0, length).ToArray();
                batches.Add((pooled.SequenceNumber, owned));
                pageBytes += (ulong)length;
            }
            finally
            {
                if (pooled.PooledData is not null)
                    ArrayPool<byte>.Shared.Return(pooled.PooledData);
            }
        }

        // The other gap shape: GetUpdatesSince reads the LIVE WAL only, so after this
        // node recovered from an ungraceful stop the prefix lives in SSTs and the
        // iterator is empty while the DB sequence is ahead. An empty page to a replica
        // that is behind would read as "caught up" — the silent-divergence lie G4 exists
        // to prevent — so it is refused the same way.
        if (batches.Count == 0 && latestAtStart > fromSeq)
            throw new ReplicationGapException(fromSeq, latestAtStart + 1);

        return new ReplicationPage(Epoch, batches, nextSeq);
    }

    /// <summary>
    /// The number of entries in a RocksDB <c>WriteBatch</c>, read from its 12-byte header
    /// (<c>[8B LE sequence][4B LE count]</c>, the format <see cref="WalTailSummary"/> decodes). A
    /// batch's sequence range is <c>[SequenceNumber, SequenceNumber + Count - 1]</c>. A short or
    /// absent buffer counts as 1, so a malformed batch is never skipped in error (058 R2).
    /// </summary>
    private static uint WriteBatchCount(byte[]? data, int length)
    {
        if (data is null || length < 12) return 1;
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        return count == 0 ? 1u : count;
    }

    public SnapshotBeginResult BeginSnapshot()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hw-snap-" + Guid.NewGuid().ToString("N"));
        Engine.DisableFileDeletions();
        ReplicationSession session;
        try
        {
            session = _source.GetInitialState(temp);
        }
        finally
        {
            Engine.EnableFileDeletions();
        }

        var id = Guid.NewGuid().ToString("N");
        var manifest = session.GetManifest();
        var state = new SnapshotSessionState(id, session, manifest, Engine.GetLatestSequenceNumber());
        _snapshots[id] = state;
        return new SnapshotBeginResult(Epoch, state.Sequence, id, manifest);
    }

    public SnapshotChunkResult ReadSnapshotChunk(string sessionId, string fileName, ulong offset, int maxBytes)
    {
        if (!_snapshots.TryGetValue(sessionId, out var state))
            throw new InvalidOperationException("unknown snapshot session");
        if (maxBytes < 1) maxBytes = 1;

        using var file = state.Session.OpenFile(fileName);
        var stream = file.FileStream;
        stream.Seek((long)offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(maxBytes, 64 * 1024)];
        var read = stream.Read(buffer, 0, buffer.Length);
        var chunk = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
        var next = offset + (ulong)read;
        var length = stream.CanSeek ? stream.Length : 0;
        var done = read == 0 || (length > 0 && (long)next >= length);
        return new SnapshotChunkResult(fileName, offset, chunk, done ? null : next);
    }

    public void EndSnapshot(string sessionId)
    {
        if (_snapshots.TryRemove(sessionId, out var state))
            state.Dispose();
    }

    public void EnforceCap()
    {
        var latest = Engine.GetLatestSequenceNumber();
        var cap = Options.SlotLagCapSequences;
        var now = Options.Clock.GetUtcNow();
        var staleAfter = Options.SlotStaleAfter;

        foreach (var (id, slot) in _slots)
        {
            if (slot.State == SlotState.Dropped) continue;

            // 050 T4 (F3): time-based staleness. A slot that has made no contact within the bound is
            // dropped even when it is under the lag cap — a replica that silently stopped acking must
            // not keep reading as Active on a live primary. Independent of the lag cap (which may be 0).
            if (staleAfter > TimeSpan.Zero && slot.LastContact != default && now - slot.LastContact > staleAfter)
            {
                _slots[id] = slot with { State = SlotState.Dropped };
                var silentMs = (now - slot.LastContact).TotalMilliseconds;
                _dropEvents.Enqueue($"slot-dropped-stale replica={id} silentMs={silentMs:F0} bound={staleAfter}");
                Logger?.LogWarning("[replication] replica {ReplicaId} slot dropped: silent {SilentMs:F0} ms exceeds {Bound}; it must re-bootstrap",
                    id, silentMs, staleAfter);
                continue;
            }

            if (cap == 0) continue;   // lag cap disabled — the staleness rule above still applies
            var lag = latest > slot.AckedSeq ? latest - slot.AckedSeq : 0;
            if (lag > cap)
            {
                _slots[id] = slot with { State = SlotState.Dropped };
                _dropEvents.Enqueue($"slot-dropped replica={id} lag={lag} cap={cap}");
                Logger?.LogWarning("[replication] replica {ReplicaId} slot dropped: lag {Lag} exceeds cap {Cap}; it must re-bootstrap",
                    id, lag, cap);
            }
            else if (lag > cap / 4)
            {
                _slots[id] = slot with { State = SlotState.Lagging };
            }
        }
    }

    public bool TryPromote(string reason, out string? error)
    {
        error = null;
        string? announceTo = null;
        ulong announceEpoch = 0;

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

            Epoch++;
            PersistEpoch();
            Role = ReplicaRole.Primary;
            _knownPrimaryEndpoint = null; // the primary is now this node
            LastPromotion = Options.Clock.GetUtcNow();
            LastPromotionReason = reason;
            LastPeerContact = LastPromotion.Value;
            _leadershipSince = LastPromotion;   // 050 T5: this node's leadership epoch starts now
            Note($"promote epoch={Epoch} reason={reason} at={LastPromotion:o}");
            announceEpoch = Epoch;
        }

        // Announce the new, higher epoch to every node this node knows — the configured
        // primary AND every roster peer (042-1d). A HELLO carrying the higher epoch and
        // this node's endpoint makes each recipient adopt it: a stale ex-primary demotes,
        // and a lower-epoch standby re-points at the new master rather than lingering on a
        // dead one. (Two standbys promoting at the *same* epoch is prevented upstream by
        // the willingness stagger, which lets only the highest-priority successor turn
        // willing first; this broadcast is the heal channel for the lower-epoch cases.)
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HostOf(Options.PrimaryServer) is { } p) targets.Add(p);
        foreach (var endpoint in RosterPeers?.Invoke() ?? [])
        {
            if (!string.Equals(endpoint, SelfEndpoint, StringComparison.OrdinalIgnoreCase))
                targets.Add(endpoint);
        }
        _ = announceTo;   // superseded by the roster broadcast
        if (targets.Count > 0)
            AnnouncePromotionAsync(targets, announceEpoch);

        // 050 T1 (F1): a node that PROMOTES at runtime must self-register in the roster too —
        // the same record the startup path writes for a node that STARTS writable. Without it the
        // set loses its own membership map at the moment of failover (blank succession view, lost
        // successor priorities). The owning store performs the upsert (it holds the store handle);
        // it is idempotent on the node id and, written through the store, WAL-ships to every standby.
        RegisterSelfInRoster?.Invoke();

        OnEpochChanged?.Invoke();   // 044: promotion bumped the epoch → wipe the local cache
        OnPromoted?.Invoke();       // 051: count the promotion
        return true;
    }

    private void AnnouncePromotionAsync(IReadOnlyCollection<string> endpoints, ulong epoch)
        => _ = Task.Run(async () =>
        {
            var server = Options.PrimaryServer;   // carries the password/TLS tail, if any
            var tail = server is not null && server.Contains(',') ? server[server.IndexOf(',')..] : "";

            var live = new HashSet<string>(endpoints, StringComparer.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 5 && live.Count > 0; attempt++)
            {
                foreach (var endpoint in live.ToArray())
                {
                    try
                    {
                        using var mux = await ConnectionMultiplexer.ConnectAsync(endpoint + tail).ConfigureAwait(false);
                        await mux.GetDatabase().ExecuteAsync("HW.REPL.HELLO",
                            Options.ReplicaId, "0", epoch.ToString(CultureInfo.InvariantCulture), SelfEndpoint)
                            .ConfigureAwait(false);
                        live.Remove(endpoint);   // told successfully
                    }
                    catch
                    {
                        // Unreachable (dead, or mid-partition) — retried next pass.
                    }
                }
                if (live.Count > 0)
                    await Task.Delay(1_000).ConfigureAwait(false);
            }
        });

    public void Promote(string reason) => TryPromote(reason, out _);

    public void Fence(string reason)
    {
        lock (_roleLock)
        {
            if (Role != ReplicaRole.Primary) return;
            Role = ReplicaRole.Fenced;
            Note($"fence epoch={Epoch} reason={reason} at={Options.Clock.GetUtcNow():o}", LogLevel.Warning);
        }
        OnFenced?.Invoke();   // 051: count the self-fence
    }

    public void UnfenceIfContact()
    {
        lock (_roleLock)
        {
            if (Role != ReplicaRole.Fenced) return;
            Role = ReplicaRole.Primary;
            Note($"unfence epoch={Epoch} at={Options.Clock.GetUtcNow():o}");
        }
    }

    /// <summary>
    /// A higher epoch was observed on any channel (RD7). A node that believed itself
    /// primary (or fenced-primary) <b>demotes</b> — refuses writes, preserves its tail as
    /// a reconciliation report. A replica simply <b>adopts</b> the epoch: seeing its
    /// primary promoted is normal operation, not a demotion (fixes the pre-review bug
    /// where a replica was marked Demoted and lost deadman eligibility).
    /// </summary>
    public void ObserveHigherEpoch(ulong observedEpoch, string reason, string? primaryEndpoint = null)
    {
        var epochChanged = false;
        var rejoinRequested = false;
        var demoted = false;
        lock (_roleLock)
        {
            if (observedEpoch < Epoch) return;

            // Record the endpoint even when the epoch is already known: the promoting
            // node's announce may arrive after a 3-arg HELLO already carried the epoch,
            // and the redirect must still learn where the new primary lives (G5). A
            // node that is (still) Primary records it only when the epoch supersedes it
            // — i.e., exactly when this call is about to demote it.
            if (primaryEndpoint is not null && (Role != ReplicaRole.Primary || observedEpoch > Epoch))
            {
                _knownPrimaryEndpoint = primaryEndpoint;
                // A node already Demoted that only now learns where the new primary is (the
                // announce arrived after a bare-epoch demotion) can schedule its rejoin here.
                if (Role == ReplicaRole.Demoted)
                    ScheduleRejoinIfConfigured(Math.Max(Epoch, observedEpoch), ref rejoinRequested);
            }

            // Equal epoch: no role change (this call may only have just learned the endpoint,
            // above) — fall through so the rejoin hook still fires. Only a strictly higher epoch
            // demotes or is adopted.
            if (observedEpoch > Epoch)
            {
                if (Role is ReplicaRole.Primary or ReplicaRole.Fenced)
                {
                    var from = Role;
                    Role = ReplicaRole.Demoted;
                    demoted = true;
                    _slots.Clear();   // 050 T4 (F3): a demoted node holds no replicas — drop phantom slots now
                    LastReconciliationPath = WriteReconciliationReport();
                    Note($"demote from={from} observedEpoch={observedEpoch} reason={reason} file={LastReconciliationPath}", LogLevel.Warning);
                    // 050 T2/T3 (F2): schedule the demoted ex-primary to rejoin the new primary as a
                    // replica. Needs the endpoint — set just above when the announce carried it; if it
                    // did not, the schedule fires when a later redirect/announce supplies it (above).
                    ScheduleRejoinIfConfigured(observedEpoch, ref rejoinRequested);
                }
                else
                {
                    Note($"adopt-epoch from={Epoch} to={observedEpoch} reason={reason}");
                }

                Epoch = observedEpoch;
                PersistEpoch();
                _leadershipSince = Options.Clock.GetUtcNow();   // 050 T5: adopted a new leadership epoch
                epochChanged = true;
            }
        }

        if (epochChanged)
            OnEpochChanged?.Invoke();   // 044: adopted a higher epoch → wipe the local cache
        if (demoted)
            OnDemoted?.Invoke();        // 051: count the demotion
        if (rejoinRequested)
            RejoinRequested?.Invoke();  // 050: ask the host to restart so Open re-syncs us as a replica
    }

    /// <summary>
    /// Writes the rejoin marker (050 T2) if auto-rejoin is on, this node is demoted, and it knows
    /// where the new primary is — at most once (a marker already on disk is left as-is). Called
    /// under <c>_roleLock</c>; sets <paramref name="rejoinRequested"/> so the caller fires the
    /// <see cref="RejoinRequested"/> hook outside the lock. Bounded by construction: only a demotion
    /// (a strictly higher epoch) reaches here, and a rejoined node is a Replica that never re-demotes
    /// at the same epoch — so it cannot loop.
    /// </summary>
    private void ScheduleRejoinIfConfigured(ulong primaryEpoch, ref bool rejoinRequested)
    {
        if (!Options.AutoRejoin) return;
        if (Role != ReplicaRole.Demoted) return;
        if (_knownPrimaryEndpoint is not { } target) return;
        if (File.Exists(Path.Combine(_dataDir, ReplicaPuller.RejoinMarkerFileName))) return;

        ReplicaPuller.WriteRejoinMarker(_dataDir, target, primaryEpoch);
        Note($"rejoin-scheduled primary={HostOf(target) ?? target} epoch={primaryEpoch}", LogLevel.Warning);
        rejoinRequested = true;
    }

    /// <summary>Kept for the explicit paths that must demote regardless of current role semantics.</summary>
    public void Demote(ulong observedEpoch, string reason) => ObserveHigherEpoch(observedEpoch, reason);

    /// <summary>
    /// The deadman backstop (042-1a D6): only the <b>fence</b> half survives. A former
    /// master with no peer contact past <c>T_fence</c> moves to read-only; contact lifts
    /// it. Nothing here ever promotes — promotion is herd-driven (042-1c wires the
    /// herd-arrival trigger onto <see cref="TryPromote"/>).
    /// </summary>
    public void TickDeadman()
    {
        if (!Options.AutoFailover) return;
        var now = Options.Clock.GetUtcNow();

        if (Role == ReplicaRole.Primary && !IsDraining)
        {
            // Herd-aware (042-1d partition matrix, parent R11.2a): a master isolated
            // from its PEERS but still serving CLIENTS keeps its herd — mastership is
            // where the clients are. Only a node with neither fences.
            if (ConnectedClients == 0 && now - LastPeerContact > Options.FenceTimeout)
                Fence("deadman: no peer and no client contact");
        }
        else if (Role == ReplicaRole.Fenced)
        {
            if (now - LastPeerContact <= Options.FenceTimeout)
                UnfenceIfContact();
        }
    }

    // ---- GOODBYE: graceful drain (042-1c C-T5 / parent R12) ------------------

    private DateTimeOffset? _drainDeadline;
    private Timer? _drainTimer;

    /// <summary>True while the master is quiescing: completion verbs run, new work is refused.</summary>
    public bool IsDraining => _drainDeadline is not null;

    /// <summary>
    /// Begin a graceful departure (parent R12): narrate GOODBYE (timing only — the
    /// successor is common knowledge), stop accepting new master-only work (the dispatcher
    /// gate reads <see cref="IsDraining"/>), and let in-flight complete. Bounded by
    /// <c>GoodbyeDrainTimeout</c>; completes early when the herd is gone. Idempotent; a
    /// no-op on a non-primary (it has nothing to drain).
    /// </summary>
    public void BeginGoodbye(string reason)
    {
        lock (_roleLock)
        {
            if (Role != ReplicaRole.Primary || IsDraining) return;
            _drainDeadline = Options.Clock.GetUtcNow() + Options.GoodbyeDrainTimeout;
            Note($"goodbye-begin epoch={Epoch} reason={reason} deadline={_drainDeadline:o}");
        }

        Narrator?.Invoke($"GOODBYE {Epoch.ToString(CultureInfo.InvariantCulture)}");

        // Drive completion on the wall clock even if no command ever ticks us again
        // (fake-clock tests call TickGoodbye directly).
        if (ReferenceEquals(Options.Clock, TimeProvider.System))
            _drainTimer = new Timer(_ => TickGoodbye(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Completes the drain at the deadline or as soon as the herd has left.</summary>
    public void TickGoodbye()
    {
        lock (_roleLock)
        {
            if (!IsDraining) return;
            var now = Options.Clock.GetUtcNow();
            if (now < _drainDeadline && ConnectedClients > 0) return;

            var cause = ConnectedClients == 0 ? "herd-left" : "drain-deadline";
            _drainDeadline = null;
            Role = ReplicaRole.Demoted;   // the clean stood-down state (parent R12.4)
            _slots.Clear();               // 050 T4 (F3): stood down → hold no replicas
            if (Engine.GetLatestSequenceNumber() > MinAckedWatermark())
                LastReconciliationPath = WriteReconciliationReport();
            Note($"goodbye-complete epoch={Epoch} cause={cause} at={now:o}");
        }

        _drainTimer?.Dispose();
        _drainTimer = null;
    }

    /// <summary>One tick for everything clock-driven — the dispatcher calls this per command.</summary>
    public void Tick()
    {
        TickDeadman();
        TickGoodbye();
    }

    /// <summary>
    /// Whether this node should receive client traffic now (052 R2): a writable, un-fenced,
    /// non-draining, fully-bootstrapped primary is ready; everything else names why it is not. The
    /// health endpoint's <c>/ready</c> serves this, and it also rides <see cref="StatsFields"/> so
    /// readiness is visible on every replication channel (the "loud on every channel" doctrine).
    /// </summary>
    public (bool Ready, string Reason) Readiness()
    {
        if (Role == ReplicaRole.Fenced) return (false, "fenced");
        if (IsDraining) return (false, "draining");
        if (ResyncRequired) return (false, "bootstrapping");
        if (Role == ReplicaRole.Demoted) return (false, "demoted");
        if (Role == ReplicaRole.Replica) return (false, "replica");
        if (Role == ReplicaRole.Primary) return (true, "primary");
        return (false, "unknown");
    }

    public IReadOnlyList<(string Name, string Value)> StatsFields()
    {
        EnforceCap();   // 050 T4: refresh slot staleness so a read never shows a phantom Active replica
        var (ready, readyReason) = Readiness();
        var fields = new List<(string, string)>
        {
            ("repl.role", Role.ToString()),
            ("repl.epoch", Epoch.ToString(CultureInfo.InvariantCulture)),
            ("repl.endpoint", SelfEndpoint),
            ("repl.redirect", RedirectEndpoint()),
            ("repl.priority", Options.Priority.ToString(CultureInfo.InvariantCulture)),
            ("repl.fenced", (Role == ReplicaRole.Fenced).ToString()),
            ("repl.slots", _slots.Count.ToString(CultureInfo.InvariantCulture)),
            ("repl.minAcked", MinAckedWatermark().ToString(CultureInfo.InvariantCulture)),
            ("repl.latestSeq", Engine.GetLatestSequenceNumber().ToString(CultureInfo.InvariantCulture)),
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

        // 050 T5: a short, bounded timeline of recent role/topology transitions for the dashboard.
        var events = _transitions.ToArray();
        var eventFrom = Math.Max(0, events.Length - 10);
        for (var e = eventFrom; e < events.Length; e++)
            fields.Add(($"repl.event.{e - eventFrom}", events[e]));

        var i = 0;
        foreach (var slot in _slots.Values)
        {
            var lag = Engine.GetLatestSequenceNumber() > slot.AckedSeq
                ? Engine.GetLatestSequenceNumber() - slot.AckedSeq : 0;
            fields.Add(($"repl.slot.{i}.id", slot.ReplicaId));
            fields.Add(($"repl.slot.{i}.acked", slot.AckedSeq.ToString(CultureInfo.InvariantCulture)));
            fields.Add(($"repl.slot.{i}.state", slot.State.ToString()));
            fields.Add(($"repl.slot.{i}.lag", lag.ToString(CultureInfo.InvariantCulture)));
            i++;
        }

        return fields;
    }

    /// <summary>
    /// The cluster's redundancy state (050 T5 / F6), for the dashboard banner, clients and (later)
    /// metrics/health. The two degraded states are <c>no-standby</c> (a primary the roster expects
    /// standbys for, with none attached) and <c>demoted</c> (a superseded node not yet rejoined);
    /// <c>single</c> is an intentional lone node (no standby expected), never an alarm.
    /// </summary>
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

    /// <summary>
    /// The unreplicated tail, by entity (G9 / R4.2): what the acked-but-local writes
    /// touched, decoded from the WAL, so an operator can replay deliberately. The raw
    /// data directory always retains the full tail; this file is the legible index of it.
    /// </summary>
    private string WriteReconciliationReport()
    {
        Directory.CreateDirectory(_dataDir);
        var path = Path.Combine(_dataDir, $"reconciliation-e{Epoch}-{Options.Clock.GetUtcNow().UtcTicks}.txt");
        var latest = Engine.GetLatestSequenceNumber();
        var acked = MinAckedWatermark();
        var sb = new StringBuilder();
        sb.AppendLine($"epoch={Epoch}");
        sb.AppendLine($"lastReplicatedSeq={acked}");
        sb.AppendLine($"lastLocalSeq={latest}");
        sb.AppendLine();
        if (latest > acked)
        {
            sb.AppendLine($"unreplicated tail ({acked + 1}..{latest}), by entity:");
            foreach (var line in WalTailSummary.Describe(_source, acked))
                sb.AppendLine("  " + line);
        }
        else
        {
            sb.AppendLine("no unreplicated tail: every local write had been acked by a replica.");
        }
        sb.AppendLine();
        sb.AppendLine("tail is preserved; operator replay only — the broker never merges.");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    // ---- epoch persistence (G2) ----------------------------------------------

    private ulong LoadPersistedEpoch()
    {
        try
        {
            var path = Path.Combine(_dataDir, EpochFileName);
            if (File.Exists(path) && ulong.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch >= 1)
                return epoch;
        }
        catch (IOException) { /* unreadable → fresh */ }
        return 1;
    }

    private void PersistEpoch()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var path = Path.Combine(_dataDir, EpochFileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, Epoch.ToString(CultureInfo.InvariantCulture));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // A failed persist is survivable until restart; the transition log records the
            // in-memory truth. Do not fail a promotion over a disk hiccup on this file.
            Note($"epoch-persist-failed epoch={Epoch}", LogLevel.Error);
        }
    }

    public void Dispose()
    {
        _deadman?.Dispose();
        _drainTimer?.Dispose();
        foreach (var s in _snapshots.Values)
            s.Dispose();
        _snapshots.Clear();
    }

    private sealed class SnapshotSessionState(string id, ReplicationSession session, List<ReplicationFileInfo> manifest, ulong sequence) : IDisposable
    {
        public string Id { get; } = id;
        public ReplicationSession Session { get; } = session;
        public List<ReplicationFileInfo> Manifest { get; } = manifest;
        public ulong Sequence { get; } = sequence;
        public void Dispose()
        {
            try { Session.Dispose(); } catch (IOException) { /* Windows live-primary hardlinks */ }
        }
    }
}

internal readonly record struct ReplicaSlot(string ReplicaId, ulong AckedSeq, ulong ReplicaEpoch, SlotState State = SlotState.Active, DateTimeOffset LastContact = default);

internal readonly record struct ReplicationPage(
    ulong Epoch,
    IReadOnlyList<(ulong Seq, byte[] Data)> Batches,
    ulong? NextSeq);

internal readonly record struct SnapshotBeginResult(
    ulong Epoch,
    ulong Sequence,
    string SessionId,
    IReadOnlyList<ReplicationFileInfo> Manifest);

internal readonly record struct SnapshotChunkResult(
    string FileName,
    ulong Offset,
    byte[] Data,
    ulong? NextOffset);
