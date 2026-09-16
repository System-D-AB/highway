namespace Highway.Server.Storage.Cache;

/// <summary>
/// The broker-local cache store (feature 044). A best-effort K/V with per-entry absolute
/// expiry — deliberately <b>not</b> the replicated <see cref="IHighwayStore"/>: it lives in
/// its own database (or in memory), never ships on the WAL, and is wiped on every epoch
/// change (mastership move). A miss is normal; callers refetch.
///
/// <para>The store never reads a wall clock (037 R5.1, kept for consistency) — <c>now</c> is
/// always a parameter. Values are framed with <see cref="ExpiryFraming"/> so read-side expiry
/// is identical to the reply-slot / idempotency path.</para>
/// </summary>
internal interface IHighwayCacheStore : IDisposable
{
    /// <summary>The live value for <paramref name="key"/>, or null when absent or expired at <paramref name="nowTicks"/>.</summary>
    byte[]? Get(string key, long nowTicks);

    /// <summary>Stores <paramref name="value"/> with an absolute expiry (<paramref name="expiresAtTicks"/>).</summary>
    void Set(string key, byte[] value, long expiresAtTicks);

    /// <summary>Removes <paramref name="key"/>.</summary>
    void Remove(string key);

    /// <summary>
    /// The absolute expiry ticks of a live entry, or null when absent/expired. Serves the
    /// wire TTL read; every cache entry has an expiry, so a live entry always returns a value.
    /// </summary>
    long? GetExpiryTicks(string key, long nowTicks);

    /// <summary>Drops every entry — the epoch-change invalidation and the over-cap backstop (044 R6/R7).</summary>
    void Clear();

    /// <summary>Removes entries that have expired at <paramref name="nowTicks"/> (best-effort; may be a no-op on stores that expire lazily on read).</summary>
    void SweepExpired(long nowTicks);

    /// <summary>Approximate live size in bytes — the sweeper's size-cap input.</summary>
    long ApproximateSizeBytes();
}
