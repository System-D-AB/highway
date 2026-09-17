using System.Globalization;
using Microsoft.Extensions.Logging;
using RocksDbSharp;
using StackExchange.Redis;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// Replica-side client of <c>HW.REPL.*</c> (042 T3/T8): HELLO → PULL → apply → ACK,
/// with snapshot bootstrap when the data directory is blank. Its pull-success clock
/// (<see cref="ReplicationFeeder.NotePeerContact"/>) doubles as the standby's master-link
/// clock, which the willingness answer reads (042-1a). The 042 witness loop is deleted
/// (042-1a reconciliation map).
///
/// <para><b>Gap handling (G4).</b> A pull refused with <c>HW_REPL_GAP</c> means the
/// primary's retained WAL no longer reaches this replica's watermark. The puller writes
/// a <c>resync-required</c> marker into the data directory, logs a transition, and backs
/// off — it never applies a gapped stream. On the next process start,
/// <see cref="RocksDbStore.Open"/> sees the marker, wipes the directory, and re-runs the
/// identical snapshot-bootstrap path (R2.2's one code path).</para>
/// </summary>
internal sealed class ReplicaPuller : IAsyncDisposable
{
    internal const string ResyncMarkerFileName = "resync-required";

    /// <summary>
    /// Marker written by a demoting ex-primary (050 T2): names the new primary it learned at
    /// runtime and the epoch it demoted to. <see cref="RocksDbStore.Open"/> honours it — wipe,
    /// re-bootstrap from that primary, come up as its replica — then consumes it. Distinct from
    /// the resync marker, which serves a *configured* replica that hit a WAL gap; this one carries
    /// the endpoint because a demoted ex-primary has no static <c>PrimaryServer</c>.
    /// </summary>
    internal const string RejoinMarkerFileName = "rejoin-required";

    /// <summary>Writes the rejoin marker: the learned primary endpoint and the observed epoch.</summary>
    internal static void WriteRejoinMarker(string dataDir, string primaryEndpoint, ulong observedEpoch)
        => File.WriteAllText(
            Path.Combine(dataDir, RejoinMarkerFileName),
            $"endpoint={primaryEndpoint}\nepoch={observedEpoch.ToString(CultureInfo.InvariantCulture)}\n");

    /// <summary>Reads the rejoin marker, or null when absent or malformed (no endpoint).</summary>
    internal static (string Endpoint, ulong Epoch)? ReadRejoinMarker(string dataDir)
    {
        var path = Path.Combine(dataDir, RejoinMarkerFileName);
        if (!File.Exists(path)) return null;

        string? endpoint = null;
        ulong epoch = 0;
        foreach (var line in File.ReadAllLines(path))
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var key = line[..i];
            var value = line[(i + 1)..];
            if (key == "endpoint") endpoint = value;
            else if (key == "epoch") _ = ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch);
        }
        return string.IsNullOrWhiteSpace(endpoint) ? null : (endpoint!, epoch);
    }

    private readonly RocksDbStore _store;
    private readonly HighwayReplicationOptions _options;
    private readonly BatchApplier _applier;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly string _dataDir;
    private readonly ILogger? _logger;   // 047: optional log sink (null in tests)

    public ReplicaPuller(RocksDbStore store, HighwayReplicationOptions options, ILogger? logger = null)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _dataDir = store.DataDir;
        _applier = new BatchApplier(store.Replication.Engine, store.Replication.Epoch);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>True once a WAL gap forced this replica into resync-required state (G4).</summary>
    public bool ResyncRequired { get; private set; }

    /// <summary>
    /// Streams a primary checkpoint into <paramref name="destDir"/> over RESP.
    /// Used before opening the replica RocksDB on a blank data directory (T3).
    /// </summary>
    public static TimeSpan DownloadSnapshot(string primaryServer, string destDir)
    {
        var started = TimeProvider.System.GetUtcNow();
        using var mux = ConnectionMultiplexer.Connect(primaryServer);
        var db = mux.GetDatabase();
        var began = (RedisResult[])db.Execute("HW.REPL.SNAPSHOT", "BEGIN")!;
        var seq = (long)began[1];
        var session = (string)began[2]!;
        var manifest = (RedisResult[])began[3]!;

        Directory.CreateDirectory(destDir);
        foreach (var row in manifest)
        {
            var pair = (RedisResult[])row!;
            var fileName = (string)pair[0]!;
            ulong offset = 0;
            while (true)
            {
                var chunk = (RedisResult[])db.Execute(
                    "HW.REPL.SNAPSHOT", "GET", session, fileName,
                    offset.ToString(CultureInfo.InvariantCulture), "65536")!;
                var data = (byte[])chunk[2]!;
                var dest = Path.Combine(destDir, fileName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var fs = new FileStream(dest, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                {
                    fs.Seek((long)offset, SeekOrigin.Begin);
                    fs.Write(data, 0, data.Length);
                }

                if (chunk[3].IsNull)
                    break;
                offset = (ulong)(long)chunk[3];
            }
        }

        db.Execute("HW.REPL.SNAPSHOT", "END", session);
        File.WriteAllText(Path.Combine(destDir, "snapshot-bootstrap.log"),
            $"seq={seq} durationMs={(TimeProvider.System.GetUtcNow() - started).TotalMilliseconds:F0}\n");
        return TimeProvider.System.GetUtcNow() - started;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.PrimaryServer))
            return;

        // 047: log the peer as host:port only — the connection string carries the password.
        var primaryHost = ReplicationFeeder.HostOf(_options.PrimaryServer) ?? "(primary)";
        var id = string.IsNullOrWhiteSpace(_options.ReplicaId) ? "replica" : _options.ReplicaId;
        _logger?.LogInformation("[replication] replica active: following {Primary} as {ReplicaId} (priority {Priority})",
            primaryHost, id, _options.Priority);
        var connected = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_store.Replication.IsWritable || ResyncRequired)
                {
                    // A promoted node stops pulling; a gapped one must not pull — it waits
                    // for the restart-time re-bootstrap (the marker is already on disk).
                    await Task.Delay(ResyncRequired ? 5_000 : 200, ct).ConfigureAwait(false);
                    continue;
                }

                using var mux = await ConnectionMultiplexer.ConnectAsync(_options.PrimaryServer).ConfigureAwait(false);

                // 042-1c C-T3: hear the master's narration. GOODBYE makes this standby
                // immediately willing (the herd contract); everything else is advisory.
                await mux.GetSubscriber().SubscribeAsync(
                    RedisChannel.Literal("hw:door:topology"),
                    (_, message) =>
                    {
                        if (((string?)message)?.StartsWith("GOODBYE", StringComparison.Ordinal) == true)
                            _store.Replication.NoteGoodbye();
                    }).ConfigureAwait(false);

                var db = mux.GetDatabase();
                await AnnounceJoinAsync(db).ConfigureAwait(false);
                if (!connected)
                {
                    _logger?.LogInformation("[replication] replica connected to primary {Primary}, streaming from seq {Seq}",
                        primaryHost, _applier.Watermark);
                    connected = true;
                }
                await PumpAsync(db, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (connected)
                {
                    _logger?.LogWarning("[replication] replica lost the primary connection ({Error}); retrying", ex.Message);
                    connected = false;
                }
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }
    }

    /// <summary>
    /// 042-1c C-T2 / parent R13.1: announce this node's own-config priority to the master.
    /// A refusal (<c>HW_PRIORITY_TAKEN</c>) is loud and non-fatal — the node still pulls
    /// and stays warm, it is just not in the succession until the operator fixes the
    /// config. Idempotent per connection; a reconnect re-announces (an upsert).
    /// </summary>
    private async Task AnnounceJoinAsync(IDatabase db)
    {
        var id = string.IsNullOrWhiteSpace(_options.ReplicaId) ? "replica" : _options.ReplicaId;
        try
        {
            await db.ExecuteAsync("HW.REPL.JOIN", id,
                _options.Priority.ToString(CultureInfo.InvariantCulture),
                _store.Replication.SelfEndpoint).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("HW_PRIORITY_TAKEN", StringComparison.Ordinal))
        {
            _store.Replication.NoteJoinRefused(ex.Message);
        }
        catch (RedisServerException)
        {
            // A NOTPRIMARY (the master moved) or transient refusal: the next reconnect
            // re-announces against whoever answers then.
        }
    }

    private async Task PumpAsync(IDatabase db, CancellationToken ct)
    {
        var feeder = _store.Replication;
        var id = string.IsNullOrWhiteSpace(_options.ReplicaId) ? "replica" : _options.ReplicaId;

        while (!ct.IsCancellationRequested && !feeder.IsWritable && !ResyncRequired)
        {
            var hello = (RedisResult[])(await db.ExecuteAsync("HW.REPL.HELLO", id,
                _applier.Watermark.ToString(CultureInfo.InvariantCulture),
                feeder.Epoch.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false))!;
            var primaryEpoch = (ulong)(long)hello[0];
            if (primaryEpoch > feeder.Epoch)
            {
                // Normal operation for a replica: the primary has been promoted at some
                // point. Adopt the epoch (G3 fix — never mark a replica Demoted).
                feeder.ObserveHigherEpoch(primaryEpoch, "HELLO saw a higher primary epoch",
                    ReplicationFeeder.HostOf(_options.PrimaryServer));
            }

            var from = _applier.Watermark;
            RedisResult[] pull;
            try
            {
                pull = (RedisResult[])(await db.ExecuteAsync("HW.REPL.PULL",
                    from.ToString(CultureInfo.InvariantCulture), "1048576").ConfigureAwait(false))!;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("HW_REPL_GAP", StringComparison.Ordinal))
            {
                MarkResyncRequired(from, ex.Message);
                return;
            }

            var pageEpoch = (ulong)(long)pull[0];
            var batches = ParsePage((RedisResult[])pull[1]!);
            var applied = _applier.ApplyPage(pageEpoch, batches);
            if (applied.Status == ApplyStatus.EpochRefused)
            {
                // The pulled page carries an epoch below what this replica has seen: the
                // node we are pulling from is a stale primary. Refuse and stop pumping it.
                return;
            }
            if (pageEpoch > feeder.Epoch)
                feeder.ObserveHigherEpoch(pageEpoch, "PULL page carried a higher epoch",
                    ReplicationFeeder.HostOf(_options.PrimaryServer));

            if (applied.Applied + applied.Skipped > 0 || batches.Count > 0)
            {
                await db.ExecuteAsync("HW.REPL.ACK", id,
                    _applier.Watermark.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }

            feeder.NotePeerContact();
            if (batches.Count == 0)
                await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private void MarkResyncRequired(ulong watermark, string detail)
    {
        ResyncRequired = true;
        _logger?.LogWarning("[replication] replica WAL gap at seq {Watermark}; re-bootstrap required — restart this replica to re-snapshot", watermark);
        try
        {
            File.WriteAllText(Path.Combine(_dataDir, ResyncMarkerFileName),
                $"watermark={watermark}\n{detail}\nrestart this replica to re-bootstrap via HW.REPL.SNAPSHOT\n");
        }
        catch (IOException) { /* the in-memory flag still stops the pump */ }
        _store.Replication.NoteResyncRequired(watermark, detail);
    }

    private static List<(ulong Seq, byte[] Data)> ParsePage(RedisResult[] rows)
    {
        var list = new List<(ulong, byte[])>(rows.Length);
        foreach (var row in rows)
        {
            var pair = (RedisResult[])row!;
            list.Add(((ulong)(long)pair[0], (byte[])pair[1]!));
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch { /* cancelled */ }
        _cts.Dispose();
    }
}
