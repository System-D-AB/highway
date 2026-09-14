namespace Highway.Server.Storage.Layout;

/// <summary>
/// Builds the physical keys for Highway's four structure families (see
/// <c>physical-layout.md</c>). A key is a one-byte <b>family tag</b>, then the logical
/// name encoded order-preservingly, then a family-specific ordered suffix.
///
/// <para>The tag replaces stow's 4-byte collection code: Highway has a closed set of
/// structure <i>kinds</i>, not an open set of collections, so a fixed tag needs no
/// catalog and no allocator (physical-layout.md §2). The logical <c>name</c> is the
/// Highway name straight off the wire (<c>invoices</c>, <c>orders@billing</c>, a request
/// id); it is string-encoded so it cannot collide with the suffix that follows it.</para>
///
/// <para>These builders return the <c>byte[]</c> keys and prefixes that
/// <see cref="IHighwayStore"/> consumes. They are pure functions of their arguments — no
/// clock, no state — so a command builds every key it needs before opening a batch.</para>
/// </summary>
internal static class HighwayKeyspace
{
    // Family tags — one byte each, distinct, never reused.
    private const byte TagKv = (byte)'k';     // point-get values
    private const byte TagList = (byte)'q';   // FIFO lists
    private const byte TagZset = (byte)'z';   // ordered sets (by score)
    private const byte TagSet = (byte)'s';    // membership sets
    private const byte TagCounter = (byte)'n'; // i64 counters (seq allocators, byte counters)

    // A generous stack budget; KeyWriter grows to a pooled array only if a name is huge.
    private const int StackBudget = 256;

    // -------------------------------------------------------------------------
    // KV (family k) — point get. Reply slot, registration record.
    // -------------------------------------------------------------------------

    /// <summary>Builds a KV key <c>k &lt;name&gt;</c>.</summary>
    public static byte[] Kv(string name) => Simple(TagKv, name);

    // -------------------------------------------------------------------------
    // Counter (family n) — i64 via the merge operator. Per-list seq allocator,
    // per-channel message seq, byte counters.
    // -------------------------------------------------------------------------

    /// <summary>Builds a counter key <c>n &lt;name&gt;</c>.</summary>
    public static byte[] Counter(string name) => Simple(TagCounter, name);

    // -------------------------------------------------------------------------
    // List (family q) — FIFO. Full key = q <name> <seq:8>. The prefix (tag+name,
    // no suffix) is what a pop seeks and what a DeleteRange bounds.
    // -------------------------------------------------------------------------

    /// <summary>Builds the list prefix <c>q &lt;name&gt;</c> — the seek target for a pop and the range bound for a teardown.</summary>
    public static byte[] ListPrefix(string name) => Simple(TagList, name);

    /// <summary>Builds a full list entry key <c>q &lt;name&gt; &lt;seq:8&gt;</c>.</summary>
    public static byte[] ListEntry(string name, long seq)
    {
        Span<byte> buffer = stackalloc byte[StackBudget];
        var writer = new KeyWriter(buffer);
        writer.WriteByte(TagList);
        KeyEncoding.WriteString(name, ref writer);
        KeyEncoding.WriteSequence(seq, ref writer);
        return writer.ToArray();
    }

    // -------------------------------------------------------------------------
    // Ordered set (family z) — by score. Full key = z <name> <score:8> <member>.
    // -------------------------------------------------------------------------

    /// <summary>Builds the ordered-set prefix <c>z &lt;name&gt;</c> — the range base for a by-score scan and a teardown.</summary>
    public static byte[] SortedSetPrefix(string name) => Simple(TagZset, name);

    /// <summary>
    /// Builds the lower bound for a by-score scan: <c>z &lt;name&gt; &lt;minScore&gt;</c>.
    /// Seek here and iterate while the score is ≤ the upper bound.
    /// </summary>
    public static byte[] SortedSetScoreBound(string name, long score)
    {
        Span<byte> buffer = stackalloc byte[StackBudget];
        var writer = new KeyWriter(buffer);
        writer.WriteByte(TagZset);
        KeyEncoding.WriteString(name, ref writer);
        KeyEncoding.WriteScore(score, ref writer);
        return writer.ToArray();
    }

    /// <summary>Builds a full ordered-set member key <c>z &lt;name&gt; &lt;score:8&gt; &lt;member&gt;</c>.</summary>
    public static byte[] SortedSetMember(string name, long score, ReadOnlySpan<byte> member)
    {
        Span<byte> buffer = stackalloc byte[StackBudget];
        var writer = new KeyWriter(buffer);
        writer.WriteByte(TagZset);
        KeyEncoding.WriteString(name, ref writer);
        KeyEncoding.WriteScore(score, ref writer);
        writer.WriteBytes(member);
        return writer.ToArray();
    }

    // -------------------------------------------------------------------------
    // Membership set (family s) — full key = s <name> <member>, empty value.
    // -------------------------------------------------------------------------

    /// <summary>Builds the set prefix <c>s &lt;name&gt;</c> — the scan base for enumeration and the range bound for a teardown.</summary>
    public static byte[] SetPrefix(string name) => Simple(TagSet, name);

    /// <summary>Builds a full membership key <c>s &lt;name&gt; &lt;member&gt;</c> (value is empty; membership is the key's presence).</summary>
    public static byte[] SetMember(string name, ReadOnlySpan<byte> member)
    {
        Span<byte> buffer = stackalloc byte[StackBudget];
        var writer = new KeyWriter(buffer);
        writer.WriteByte(TagSet);
        KeyEncoding.WriteString(name, ref writer);
        writer.WriteBytes(member);
        return writer.ToArray();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Tag + encoded name, no suffix — the shape of a KV/counter key and of every family prefix.</summary>
    private static byte[] Simple(byte tag, string name)
    {
        Span<byte> buffer = stackalloc byte[StackBudget];
        var writer = new KeyWriter(buffer);
        writer.WriteByte(tag);
        KeyEncoding.WriteString(name, ref writer);
        return writer.ToArray();
    }
}
