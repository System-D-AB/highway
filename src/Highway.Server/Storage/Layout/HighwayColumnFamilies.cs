namespace Highway.Server.Storage.Layout;

/// <summary>
/// The RocksDB column families and their fixed numeric order (see
/// <c>physical-layout.md</c> §6). Batches address a family by handle; a mismatched
/// order writes to the wrong family <b>silently</b>, so the order is asserted on open.
///
/// <para>The split is <b>operational, not hot/cold</b>. Highway always reads a whole
/// entry, so — unlike stow's <c>meta</c>/<c>body</c> split — there is no metadata to
/// separate. The families exist so a growing dead-letter backlog cannot evict live
/// queue keys from the block cache.</para>
///
/// <para>Discipline ported from <c>reference/stow-engine/Layout/ColumnFamilies.cs</c>:
/// the order is significant, new families are <b>appended</b> and existing families are
/// <b>never reordered</b>, and the store implementation asserts on open that the opened
/// database's families match <see cref="OrderedNames"/> — refusing a mismatch rather
/// than writing to the wrong family silently.</para>
/// </summary>
internal static class HighwayColumnFamilies
{
    /// <summary>Index 0 — RocksDB requires a "default" family; Highway does not use it.</summary>
    public const int Default = 0;

    /// <summary>
    /// Index 1 — the live working set: families <c>q</c> (lists), <c>z</c> (ordered
    /// sets), <c>s</c> (membership sets), <c>k</c> (KV), <c>n</c> (counters). The hot
    /// path; churns and compacts constantly.
    /// </summary>
    public const int Data = 1;

    /// <summary>
    /// Index 2 — dead-letter lists. Cold, bounded, rarely read; kept out of
    /// <see cref="Data"/> so a poison-message backlog does not pollute the live block
    /// cache.
    /// </summary>
    public const int Dlq = 2;

    /// <summary>
    /// The family names in fixed numeric order. Never reorder; only append.
    /// </summary>
    public static readonly IReadOnlyList<string> OrderedNames =
    [
        "default", // 0 — unused, RocksDB requires it
        "data",    // 1 — live working set
        "dlq",     // 2 — dead-letter lists
    ];
}
