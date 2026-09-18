namespace Highway.Abstractions;

/// <summary>
/// Message-size bounds shared by the client and server (feature 057), so the default and the ceiling
/// are stated once rather than mirrored as drifting constants.
///
/// <para>The maximum message size is a chosen bound, not a structural one: RocksDB stores multi-MB
/// values without trouble. What it protects is <b>memory</b> — a message is buffered whole at several
/// hops (client serialize, RESP frame, store value, WAL record, replication ship), so peak memory
/// scales with message size × concurrency. Hence a raised-but-bounded envelope, not "unlimited": above
/// the ceiling the right tool is chunk-and-stream, not one giant message.</para>
/// </summary>
public static class HighwayLimits
{
    /// <summary>Default maximum message (envelope) size: 5 MiB.</summary>
    public const int DefaultMaxPayloadBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Enforced ceiling on a configured maximum: 15 MiB. A configured value above this is refused at
    /// build/compose time (never silently clamped); for larger payloads, chunk-and-stream.
    /// </summary>
    public const int MaxPayloadCeilingBytes = 15 * 1024 * 1024;
}
