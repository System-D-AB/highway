using System.Collections.Concurrent;

namespace Highway.Client.Engine;

/// <summary>
/// What a fast-fail check learned about a service (feature 006).
/// </summary>
internal enum DiscoveryOutcome
{
    /// <summary>
    /// Discovery succeeded and returned at least one live host, or its result is
    /// not trustworthy enough to act on (cache miss with caching disabled, a
    /// failed lookup, an expired entry). Either way: enqueue normally.
    /// </summary>
    Proceed,

    /// <summary>
    /// A fresh, successful lookup found zero live hosts. This is the only
    /// outcome that may fast-fail.
    /// </summary>
    NoLiveHosts,
}

/// <summary>
/// Short-TTL cache over <c>HW.DISCOVER</c> so fast-fail does not add a round
/// trip to every call in a hot loop.
///
/// <para><b>The safety rule.</b> Only a fresh, successful, empty result yields
/// <see cref="DiscoveryOutcome.NoLiveHosts"/>. A lookup that failed, or one
/// whose entry has expired, yields <see cref="DiscoveryOutcome.Proceed"/> and
/// the call is enqueued as normal. The cache can therefore delay a fast-fail but
/// can never cause a request to be dropped that would otherwise have been
/// served — it is an optimization, never an authority.</para>
/// </summary>
internal sealed class ServiceDiscoveryCache
{
    private readonly IHighwayConnection _connection;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ServiceDiscoveryCache(IHighwayConnection connection, TimeSpan ttl)
    {
        _connection = connection;
        _ttl = ttl;
    }

    /// <summary>Entries currently cached (diagnostics and tests).</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Decides whether a call to <paramref name="service"/> may fast-fail.
    /// Never throws: a discovery failure is reported as
    /// <see cref="DiscoveryOutcome.Proceed"/>.
    /// </summary>
    public async Task<DiscoveryOutcome> CheckAsync(string service, CancellationToken ct = default)
    {
        if (_ttl > TimeSpan.Zero
            && _entries.TryGetValue(service, out var cached)
            && DateTime.UtcNow - cached.FetchedAtUtc < _ttl)
        {
            return cached.HostCount > 0 ? DiscoveryOutcome.Proceed : DiscoveryOutcome.NoLiveHosts;
        }

        int hostCount;
        try
        {
            var hosts = await _connection.DiscoverAsync(service, ct).ConfigureAwait(false);
            hostCount = hosts.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Discovery is an optimization; its failure must never turn into a
            // 404 for a service that may well be running.
            return DiscoveryOutcome.Proceed;
        }

        if (_ttl > TimeSpan.Zero)
            _entries[service] = new Entry(DateTime.UtcNow, hostCount);

        return hostCount > 0 ? DiscoveryOutcome.Proceed : DiscoveryOutcome.NoLiveHosts;
    }

    private readonly record struct Entry(DateTime FetchedAtUtc, int HostCount);
}
