using System.Globalization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Highway.Server.Storage.Replication;

/// <summary>
/// A standby's link to its primary over RESP (076 R5.6; the replica puller's loop, without the data path). Crystal moves
/// the data over its own port; this agent keeps what Highway's policy needs:
/// <list type="bullet">
/// <item><c>HW.REPL.JOIN</c> on every connect — this node's priority, into the roster (042-1c C-T2);</item>
/// <item>the master's narration — GOODBYE makes this standby willing at once;</item>
/// <item><c>HW.REPL.HELLO</c> every 200 ms — the master-link clock willingness and the deadman read, the epoch both
/// ways, and the primary's replication endpoint, which this standby follows.</item>
/// </list>
/// </summary>
internal sealed class StandbyAgent : IAsyncDisposable
{
    /// <summary>The hello cadence: well inside the willingness threshold (3 s by default), so an idle standby never reads its master as lost.</summary>
    internal const int HelloIntervalMs = 200;

    private readonly HighwayReplication _replication;
    private readonly HighwayReplicationOptions _options;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private bool _formatRefused;

    public StandbyAgent(HighwayReplication replication, HighwayReplicationOptions options, ILogger? logger = null)
    {
        _replication = replication;
        _options = options;
        _logger = logger;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private string Id => string.IsNullOrWhiteSpace(_options.ReplicaId) ? "replica" : _options.ReplicaId;

    private async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.PrimaryServer))
            return;

        var primaryHost = HighwayReplication.HostOf(_options.PrimaryServer) ?? "(primary)";
        _logger?.LogInformation("[replication] replica active: following {Primary} as {ReplicaId} (priority {Priority})",
            primaryHost, Id, _options.Priority);
        var connected = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_replication.IsWritable)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);   // a promoted node says hello to nobody
                    continue;
                }

                using var mux = await ConnectionMultiplexer.ConnectAsync(_options.PrimaryServer).ConfigureAwait(false);

                await mux.GetSubscriber().SubscribeAsync(
                    RedisChannel.Literal("hw:door:topology"),
                    (_, message) =>
                    {
                        if (((string?)message)?.StartsWith("GOODBYE", StringComparison.Ordinal) == true)
                            _replication.NoteGoodbye();
                    }).ConfigureAwait(false);

                var db = mux.GetDatabase();
                await AnnounceJoinAsync(db).ConfigureAwait(false);
                if (!connected)
                {
                    _logger?.LogInformation("[replication] replica connected to primary {Primary}", primaryHost);
                    connected = true;
                }
                await HelloLoopAsync(db, ct).ConfigureAwait(false);
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

    private async Task AnnounceJoinAsync(IDatabase db)
    {
        try
        {
            await db.ExecuteAsync("HW.REPL.JOIN", Id,
                _options.Priority.ToString(CultureInfo.InvariantCulture),
                _replication.SelfEndpoint).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("HW_PRIORITY_TAKEN", StringComparison.Ordinal))
        {
            _replication.NoteJoinRefused(ex.Message);
        }
        catch (RedisServerException)
        {
            // A NOTPRIMARY (the master moved) or a transient refusal: the next reconnect re-announces.
        }
    }

    /// <summary>
    /// 065: the primary's storage format as its <c>HW.REPL.HELLO</c> reply states it, when it is not this build's — or
    /// null when they match. A 2.x primary answers <c>[epoch, minSeq]</c> (no format); a primary that predates 076
    /// answers three elements and names no replication endpoint, so it cannot be followed either.
    /// </summary>
    internal static string? PrimaryFormatMismatch(RedisResult[] hello)
    {
        if (hello.Length < 3)
            return "3 (Highway 2.x)";
        var theirs = (long)hello[2];
        if (theirs != HighwayServerBuilder.StorageFormatVersion)
            return theirs.ToString(CultureInfo.InvariantCulture);
        return hello.Length < 4 ? $"{theirs} without SystemD.Crystal replication (a 3.0 pre-release)" : null;
    }

    /// <summary>
    /// The replication endpoint to dial (H1): as the primary names it, unless it names an unspecified host (it binds to
    /// 0.0.0.0 or :: and advertises nothing better) — then the host this standby already reaches it at, from
    /// <c>PrimaryServer</c>, with the port it named.
    /// </summary>
    internal static string? Reachable(string? named, string? primaryServer)
    {
        if (string.IsNullOrEmpty(named)) return named;
        var colon = named.LastIndexOf(':');
        if (colon <= 0) return named;
        var host = named[..colon].Trim('[', ']');
        if (host is not ("0.0.0.0" or "::" or "*" or "+")) return named;
        if (HighwayReplication.HostOf(primaryServer) is not { } primary) return named;
        var primaryColon = primary.LastIndexOf(':');
        var primaryHost = primaryColon > 0 ? primary[..primaryColon] : primary;
        return primaryHost + named[colon..];
    }

    private async Task HelloLoopAsync(IDatabase db, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_replication.IsWritable)
        {
            var hello = (RedisResult[])(await db.ExecuteAsync("HW.REPL.HELLO",
                _replication.HelloArgs(Id, _replication.FollowedPosition(), _replication.Epoch, endpoint: null)).ConfigureAwait(false))!;

            // 065: a primary of another storage format (or one that predates 076 and names no replication endpoint) is
            // not followed: refuse loudly, once, and stay a warm, empty-handed standby until it is upgraded.
            if (PrimaryFormatMismatch(hello) is { } theirs)
            {
                if (!_formatRefused)
                {
                    _formatRefused = true;
                    var detail = $"primary {HighwayReplication.HostOf(_options.PrimaryServer)} stores format {theirs}; " +
                                 $"this node stores format {HighwayServerBuilder.StorageFormatVersion}. Not replicating: " +
                                 "a replica must run the same major version as its primary.";
                    _logger?.LogError("[replication] {Detail}", detail);
                    _replication.NoteJoinRefused(detail);
                }
                await Task.Delay(5_000, ct).ConfigureAwait(false);
                return;
            }
            _formatRefused = false;

            var primaryEpoch = (ulong)(long)hello[0];
            var replicationEndpoint = Reachable((string?)hello[3], _options.PrimaryServer);
            if (primaryEpoch > _replication.Epoch)
                _replication.ObserveHigherEpoch(primaryEpoch, "HELLO saw a higher primary epoch",
                    HighwayReplication.HostOf(_options.PrimaryServer), replicationEndpoint);

            if (!string.IsNullOrEmpty(replicationEndpoint))
                _replication.EnsureFollowing(replicationEndpoint);

            // H1: master contact is a hello answered while this standby's Crystal follower is connected to the primary the
            // hello named — what a successful pull was on v3.0. A standby that reaches RESP but replicates nothing does not
            // count as in touch. (The reply's dataLive, the primary's view of the same, stays on the wire.)
            if (_replication.ReplicatingFrom(replicationEndpoint))
            {
                _replication.NotePeerContact();
                if (_replication.HoldsReplicatedData())
                    _replication.NotePullsConfirmed();   // syncing → standby once caught up (see ReadyAsStandby)
            }
            // Settle syncing → standby now, while the primary answers: checked only on demand, a standby that caught up
            // but was never asked before its primary died could no longer prove it (Crystal drops the positions then).
            _replication.RefreshStandbyReadiness();
            _replication.ClearResyncIfCaughtUp();

            await Task.Delay(HelloIntervalMs, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        // A connect in progress does not observe the token; never hold a shutdown for its timeout. The loop exits on its
        // own once the connect returns, seeing the cancellation.
        try { await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false); } catch { /* cancelled */ }
    }
}
