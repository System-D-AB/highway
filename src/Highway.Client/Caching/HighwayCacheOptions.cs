namespace Highway.Client.Caching;

/// <summary>
/// Options for the Highway <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
/// adapter (feature 044). The TTL policy — default and maximum — is a <b>server</b> setting
/// (<c>server.cache.*</c>), so nothing here duplicates it; the only client-side knob is a key
/// prefix for namespacing an application's entries within the shared broker-local cache.
/// </summary>
public sealed class HighwayCacheOptions
{
    /// <summary>
    /// An optional application prefix applied <i>after</i> the mandatory <c>hw:cache:</c> routing
    /// prefix, e.g. <c>"orders:"</c> → keys land at <c>hw:cache:orders:{key}</c>. Empty by default.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;
}
