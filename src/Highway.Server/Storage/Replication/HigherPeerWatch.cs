using System.Globalization;
using StackExchange.Redis;

namespace Highway.Server.Storage.Replication;

/// <summary>
/// What a roster peer answered when probed (069 R1). <see cref="Unreachable"/> is a quick, definitive
/// failure (the connection was refused: nothing listens there); <see cref="Slow"/> is a probe that ran
/// out of time, which under load says nothing about whether the peer is alive.
/// </summary>
internal enum PeerAnswer { Unreachable, Slow, Master, Willing, Standby }

/// <summary>
/// One probe result: when it completed, what the peer said, (for a standby) where it redirects, and —
/// while the peer is not answering — since when it has been silent.
/// </summary>
internal readonly record struct PeerObservation(DateTimeOffset At, PeerAnswer Answer, string? Redirect, DateTimeOffset? SilentSince = null)
{
    public bool Answered => Answer is PeerAnswer.Master or PeerAnswer.Willing or PeerAnswer.Standby;

    /// <summary>This result, carrying forward how long the peer has been silent from the previous one.</summary>
    public PeerObservation After(PeerObservation? previous)
        => Answered ? this with { SilentSince = null }
                    : this with { SilentSince = previous is { Answered: false, SilentSince: { } since } ? since : At };
}

/// <summary>
/// 069 R1 — willingness is a checked fact, not only a timer. The priority stagger
/// (<see cref="Willingness"/>) makes the highest-priority successor turn willing first, but it only
/// prevents a split if every client reaches that successor within the stagger. Under load a client's
/// probe of the live successor can be slow enough to be taken for dead; it then walks on to a
/// lower-priority standby that, by its timer alone, would answer willing and promote — two masters at
/// one epoch.
///
/// <para>So a standby that has lost its master probes the higher-priority roster peers itself
/// (<c>HW.REPL.HELLO PEER</c> — a peer session: it is neither counted as a herd client nor taken as
/// master contact by the probed node) and is willing only when none of them could take the herd. A
/// higher-priority peer that answers <c>master</c> or <c>willing</c>, or is a standby still following
/// someone, outranks it: this standby answers <c>standby</c> and redirects the client there. A peer
/// that refuses the connection, or that redirects to itself (a draining master) or to this node, does
/// not. Slow is not dead — that confusion is the bug 069 fixes — so a peer whose probes time out, or
/// that has no recent result at all, still counts as outranking until it has been silent for
/// <see cref="SilentPeerDeadAfter"/>: under load the standby waits rather than guesses, and a peer
/// whose host is truly gone (which also times out) is given up on a few seconds later.</para>
/// </summary>
internal static class HigherPeerWatch
{
    /// <summary>How often a standby that has lost its master probes its higher-priority peers.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    /// <summary>A probe result older than this is no evidence either way.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(3);

    /// <summary>A peer whose probes have all failed for this long is taken to be gone, even if they only timed out.</summary>
    public static readonly TimeSpan SilentPeerDeadAfter = TimeSpan.FromSeconds(5);

    /// <summary>The bound on one probe (connect, then the handshake).</summary>
    private const int ProbeTimeoutMs = 1_000;

    /// <summary>A failure faster than this was a refusal, not a timeout.</summary>
    private const int QuickFailureMs = ProbeTimeoutMs / 2;

    /// <summary>
    /// Pure verdict: does a higher-priority peer outrank this standby? <paramref name="redirect"/> names
    /// the peer to send the client to, or null when the outranking peer is merely unknown.
    /// </summary>
    public static bool Outranked(
        DateTimeOffset now,
        IEnumerable<RosterMember> higherPeers,
        IReadOnlyDictionary<string, PeerObservation> seen,
        string self,
        out string? redirect)
    {
        foreach (var peer in higherPeers.OrderBy(m => m.Priority))
        {
            if (!seen.TryGetValue(peer.Endpoint, out var o) || now - o.At > Freshness)
            {
                redirect = null;
                return true;
            }

            if (o.Answer == PeerAnswer.Slow && now - (o.SilentSince ?? o.At) < SilentPeerDeadAfter)
            {
                redirect = null;   // maybe alive, just slow: wait
                return true;
            }

            var outranks = o.Answer switch
            {
                PeerAnswer.Master or PeerAnswer.Willing => true,
                PeerAnswer.Standby => !SameEndpoint(o.Redirect, peer.Endpoint) && !SameEndpoint(o.Redirect, self),
                _ => false,
            };
            if (outranks)
            {
                redirect = peer.Endpoint;
                return true;
            }
        }

        redirect = null;
        return false;
    }

    /// <summary>The production probe: a short-lived connection and one <c>HW.REPL.HELLO PEER</c>.</summary>
    public static Func<string, CancellationToken, Task<PeerObservation>> NetworkProbe(HighwayReplication feeder)
        => async (endpoint, ct) =>
        {
            var started = Environment.TickCount64;
            try
            {
                var config = ConfigurationOptions.Parse(endpoint + HighwayReplication.ConnectionTail(feeder.Options.PrimaryServer));
                config.ConnectTimeout = ProbeTimeoutMs;
                config.AsyncTimeout = ProbeTimeoutMs;
                config.SyncTimeout = ProbeTimeoutMs;
                config.ConnectRetry = 1;
                config.AbortOnConnectFail = true;
                config.BacklogPolicy = BacklogPolicy.FailFast;

                using var mux = await ConnectionMultiplexer.ConnectAsync(config).ConfigureAwait(false);
                var id = string.IsNullOrWhiteSpace(feeder.Options.ReplicaId) ? "replica" : feeder.Options.ReplicaId;
                var reply = (RedisResult[])(await mux.GetDatabase().ExecuteAsync("HW.REPL.HELLO", "PEER", id,
                    feeder.Epoch.ToString(CultureInfo.InvariantCulture)).WaitAsync(ct).ConfigureAwait(false))!;
                var answer = (string?)reply[0] switch
                {
                    "master" => PeerAnswer.Master,
                    "willing" => PeerAnswer.Willing,
                    _ => PeerAnswer.Standby,
                };
                return new PeerObservation(feeder.Options.Clock.GetUtcNow(), answer,
                    answer == PeerAnswer.Standby ? (string?)reply[1] : endpoint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var quick = Environment.TickCount64 - started < QuickFailureMs;
                return new PeerObservation(feeder.Options.Clock.GetUtcNow(), quick ? PeerAnswer.Unreachable : PeerAnswer.Slow, null);
            }
        };

    private static bool SameEndpoint(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
