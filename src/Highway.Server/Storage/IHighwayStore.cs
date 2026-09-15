namespace Highway.Server.Storage;

/// <summary>
/// The storage seam for feature 037 — the one interface between the ported
/// <c>HW.*</c> commands and whatever engine lives underneath (RocksDB in
/// production, an in-memory map in tests).
///
/// <para><b>Gate G1 (037 R3.2):</b> <c>Highway.Server</c> compiles against this
/// interface with <b>no engine type on it</b>. There is no <c>RocksDb</c>,
/// <c>WriteBatch</c>, <c>ColumnFamilyHandle</c>, <c>Snapshot</c>, Garnet or
/// Tsavorite type anywhere in this file or on any member. If one appears, the
/// seam is wrong and the port must not start.</para>
///
/// <para><b>Derived, not designed (037 R3.1, T2.1).</b> Every member here comes
/// from a real call site in the current Garnet commands — see the per-family
/// notes and the inventory in <c>design.md</c> §1. Nothing speculative is added.
/// The four families and their operations are the complete set the 23 commands
/// use, and only that set.</para>
///
/// <list type="bullet">
///   <item><b>KV</b> — reply slots, the channel sequence, registration records,
///         mirror lists, byte counters. <see cref="Get"/> <see cref="Set"/>
///         <see cref="SetEx"/> <see cref="Delete"/> <see cref="Increment"/></item>
///   <item><b>List</b> — queues, processing lists, dead-letter lists.
///         <see cref="ListRightPush"/> <see cref="ListLeftPush"/>
///         <see cref="ListLeftPop"/> <see cref="ListDrain"/> <see cref="ListLength"/></item>
///   <item><b>Ordered set</b> — the delayed set, job schedules.
///         <see cref="SortedSetAdd"/> <see cref="SortedSetRemove"/>
///         <see cref="SortedSetRangeByScore"/> <see cref="SortedSetLength"/></item>
///   <item><b>Membership set</b> — service nodes, queue nodes, channel groups.
///         <see cref="SetAdd"/> <see cref="SetRemove"/> <see cref="SetMembers"/></item>
///   <item><b>Range delete</b> — group retirement and node decommission, and the
///         mechanical answer to C4.6. <see cref="DeleteRange"/></item>
/// </list>
///
/// <para><b>The mirror lists collapse (037 §2, T3.3).</b> Under Garnet the
/// commands keep a Main-store "mirror" (a newline-delimited string) beside every
/// object-store Set, because <c>Prepare</c> could not read a Set without
/// registering a watch that the later exclusive lock would fail (the 004.1 rule).
/// On this seam there is no <c>Prepare</c> and no watch, so a membership set is
/// just a set: reads use <see cref="SetMembers"/>, and the mirror keys
/// (<c>nodelist</c>, <c>grplist</c>, <c>job:index</c>, <c>grp:members</c>,
/// <c>node:subs</c>, <c>node:channels</c>) cease to exist. That <see cref="SetMembers"/>
/// exists here — where Garnet's commands never called <c>SetMembers</c> — is the
/// single largest structural simplification of the port, and the place T3.3 says
/// to prove each mirror's old reader gets the same answer from the one surviving copy.</para>
///
/// <para><b>The write model (037 R4, R5).</b> A command does not call these
/// mutators one at a time against live storage. It:
/// <list type="number">
///   <item>reads the wall clock <b>once</b>, before anything (037 R5.1 — no clock
///         inside a transaction);</item>
///   <item>takes the per-key lock (<c>reference/stow-engine/Engine/StripedLock.cs</c>);</item>
///   <item>opens a snapshot and reads what it needs against it (<see cref="Snapshot"/>);</item>
///   <item>stages every mutation into one <see cref="IStoreBatch"/>;</item>
///   <item>commits it exactly once (<see cref="IStoreBatch.Commit"/> — 037 R4.1,
///         "exactly one place a batch is committed").</item>
/// </list>
/// A command that fails part-way commits nothing (037 R4.2). The clock value read
/// in step 1 is the absolute value written into the batch, so WAL replay after an
/// ungraceful kill reproduces byte-identical state (037 R5.3) — the property
/// Garnet's stored-procedure replay could not offer.</para>
///
/// <para><b>Values and members are opaque bytes.</b> Queue entries carry their own
/// framing (attempt counts, claim timestamps, failure blocks) inside the payload;
/// the store must preserve those bytes exactly and never interpret them. Ordering
/// is the store's job (list position, score order); content is the command's.</para>
///
/// <para><b>Reference implementation.</b> The RocksDB shape of every method here
/// exists already in the sibling <c>stow-rocksdb</c> project, copied under
/// <c>docs/features/037-rocksdb-engine/reference/stow-engine/</c>. Each member below
/// points at the specific file. Read <c>reference/README.md</c> first — it names the
/// two hazards (the global-sequence trap and the clock-in-transaction trap) that
/// project already paid for.</para>
/// </summary>
public interface IHighwayStore : IDisposable
{
    // =========================================================================
    // Snapshot + batch — the transactional spine (037 R4)
    // =========================================================================

    /// <summary>
    /// Opens a point-in-time read view. Everything a command decides is decided
    /// against one snapshot, so a concurrent write cannot change the answer
    /// mid-command. Dispose it before the command returns.
    ///
    /// <para><b>Reference:</b> <c>reference/stow-engine/Engine/ReadPath.cs</c> shows
    /// the snapshot-scoped read discipline (create, read under
    /// <c>ReadOptions.SetSnapshot</c>, dispose); <c>SequenceSource.cs</c> shows how
    /// its exact sequence is obtained if a caller needs it.</para>
    /// </summary>
    IStoreSnapshot Snapshot();

    /// <summary>
    /// Opens a new write batch. Nothing staged into it is visible until
    /// <see cref="IStoreBatch.Commit"/>. This is the <b>only</b> way to mutate the
    /// store — there are no un-batched writes (037 R4.1).
    ///
    /// <para><b>Reference:</b> <c>reference/stow-engine/Engine/WritePath.cs</c> — the
    /// single transactional method; every mutation is a <c>WriteBatch</c> committed
    /// once by <c>db.Write(batch)</c>.</para>
    /// </summary>
    IStoreBatch NewBatch();

    // =========================================================================
    // FAMILY 1 — KV
    //   reply slot (SETEX), channel seq (Increment), registration record,
    //   mirror lists (now plain sets — see the class remarks), byte counters.
    //   Garnet call sites: HwReply, HwPublish, HwHeartbeat, HighwayCommandBase.Registry
    // =========================================================================

    /// <summary>
    /// Reads a key against <paramref name="snapshot"/>. Null when absent.
    /// Concept: registration record read, byte-counter read, reply-slot read.
    /// </summary>
    byte[]? Get(IStoreSnapshot snapshot, byte[] key);

    /// <summary>
    /// Stages a set. Last write wins. Concept: registration record write
    /// (<c>hw:reg:node:{nodeId}</c>), the channel sequence's string form.
    /// </summary>
    void Set(IStoreBatch batch, byte[] key, byte[] value);

    /// <summary>
    /// Stages a set with an absolute expiry. The only expiring key in the system is
    /// the RPC reply slot (<c>hw:rep:{requestId}</c>, <c>HwReplyCommand</c>).
    ///
    /// <para><paramref name="expiresAtTicks"/> is an <b>absolute</b> .NET UTC tick
    /// count computed by the caller from the single pre-batch clock read plus the
    /// TTL — never a relative duration the store turns into a time itself, which
    /// would read a clock inside the write (037 R5.1).</para>
    ///
    /// <para><b>OD5</b> (design open decision): whether expiry is an expiry field
    /// filtered on read or a RocksDB compaction filter is the implementer's choice,
    /// recorded in T2.3. The seam only promises the key is gone once
    /// <paramref name="expiresAtTicks"/> has passed.</para>
    /// </summary>
    void SetEx(IStoreBatch batch, byte[] key, byte[] value, long expiresAtTicks);

    /// <summary>Stages a delete. Idempotent — deleting an absent key is not an error.</summary>
    void Delete(IStoreBatch batch, byte[] key);

    /// <summary>
    /// Reads a key written by <see cref="SetEx"/>, honouring its expiry. Returns the stored
    /// value if <paramref name="nowTicks"/> is before its expiry, or <c>null</c> if the key
    /// is absent <b>or expired</b> — an expired reply slot reads as gone (037 R5.1: the clock
    /// value is passed in, never read inside the store).
    ///
    /// <para>The only expiring key is the RPC reply slot (<c>HwReplyCommand</c> writes it,
    /// <c>HwCall</c>/<c>HwReplay</c> read it), so this is its read path. A plain
    /// <see cref="Get"/> on a <see cref="SetEx"/> key returns the raw framed bytes and is not
    /// what a caller wants — use this. OD5, decided in 038 T5.</para>
    /// </summary>
    byte[]? GetLive(IStoreSnapshot snapshot, byte[] key, long nowTicks);

    /// <summary>
    /// Stages the physical deletion of every <see cref="SetEx"/> key whose expiry is at or
    /// before <paramref name="nowTicks"/>. The reclamation half of OD5: <see cref="GetLive"/>
    /// hides an expired slot immediately; this makes it physically gone. Called by the
    /// reply-slot maintenance path; <paramref name="nowTicks"/> is passed in (037 R5.1).
    /// </summary>
    void SweepExpired(IStoreBatch batch, long nowTicks);

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to an i64 counter and returns the
    /// new value. Two concepts ride this: the per-channel message-ID sequence
    /// (<c>HwPublishCommand</c>, the only Garnet <c>Increment</c>) and, in the port,
    /// the per-queue message sequence that list keys need (design §1) and byte
    /// accounting (which Garnet did with GET/SET, races and all).
    ///
    /// <para><b>Must be inside the batch, from a counter key</b> — never predicted
    /// from the engine's global sequence. This is the <b>B1 defect</b> documented in
    /// <c>reference/stow-engine/Engine/SequenceSource.cs</c>: a global sequence
    /// advances on any write and cannot be reserved under a per-key lock.</para>
    ///
    /// <para><b>Reference:</b> <c>reference/stow-engine/Engine/CounterMergeOperator.cs</c>
    /// — a race-free i64-add RocksDB merge operator, exactly this primitive.</para>
    /// </summary>
    long Increment(IStoreBatch batch, byte[] key, long delta);

    // =========================================================================
    // FAMILY 2 — List (FIFO)
    //   queues (hw:q:{q}:q, hw:svc:{s}:q, derived hw:q:{ch}@{grp}:q),
    //   processing lists (…:proc:{node}), dead-letter lists (…:dlq).
    //   Producers push tail, consumers pop head; redelivery-to-head pushes head.
    //   Garnet call sites: HwCall, HwQSend, HwPublish, HwDequeue, HwQClaim,
    //     HwDlq, HwAck, HwQAck, HwFail, HwTouch, LeaseSweep, Decommission
    // =========================================================================

    /// <summary>
    /// Appends <paramref name="value"/> to the tail. The enqueue and requeue-to-tail
    /// path (RPC returns to tail; a promoted delayed message; a swept survivor).
    ///
    /// <para><b>On ordered keys a list is a key range</b> <c>q|&lt;list&gt;|&lt;seq&gt;</c>
    /// with a monotonic per-list <paramref name="value"/> seq; tail-push writes the
    /// next seq. <b>The seq is allocated in the same batch</b> (see
    /// <see cref="Increment"/> and design §1) or two concurrent pushes collide.</para>
    ///
    /// <para><b>Reference:</b> the key shape is <c>reference/stow-engine/Layout/DocKey.cs</c>
    /// (prefix + ordered suffix); the write is <c>WritePath.cs</c>.</para>
    /// </summary>
    void ListRightPush(IStoreBatch batch, byte[] key, byte[] value);

    /// <summary>
    /// Prepends <paramref name="value"/> to the head. Redelivery-to-head only: the
    /// pub/sub lease sweep returns a survivor to the head so a redelivery keeps its
    /// place (<c>LeaseSweep.SweepExpiredEntries</c> with <c>returnToHead</c>), and
    /// <c>HwDlqCommand</c> restores a peeked entry. On ordered keys this writes a seq
    /// <b>below</b> the current head — a descending counter, or a signed seq the head
    /// grows downward into.
    /// </summary>
    void ListLeftPush(IStoreBatch batch, byte[] key, byte[] value);

    /// <summary>
    /// Removes and returns the head, or null when empty. The claim/dequeue path
    /// (<c>HwDequeue</c>, <c>HwQClaim</c>).
    ///
    /// <para><b>On ordered keys this is seek-first-on-prefix</b>: seek the list
    /// prefix, take the first key, stage its delete in <paramref name="batch"/>. The
    /// read is against the batch's own snapshot so a pop then push in one batch is
    /// consistent.</para>
    ///
    /// <para><b>Reference:</b> the seek-first loop is <c>reference/stow-engine/Engine/ReadPath.cs</c>
    /// <c>ScanDocuments</c> (<c>iter.Seek(prefix)</c> → <c>StartsWith</c> → take).</para>
    /// </summary>
    byte[]? ListLeftPop(IStoreBatch batch, byte[] key);

    /// <summary>
    /// Removes and returns <b>every</b> entry, in FIFO order. This is the
    /// <c>ListLeftPop(int.MaxValue)</c> idiom the ack/fail/touch/sweep paths use:
    /// drain a processing list, filter it, push the survivors back
    /// (<c>HwAck</c>, <c>HwQAck</c>, <c>HwFail</c>, <c>HwTouch</c>,
    /// <c>LeaseSweep.SweepExpiredEntries</c>, <c>RequeueNodeWork</c>). Kept as one
    /// operation because the commands treat "take all, decide, restore some" as a
    /// unit; splitting it into N pops would multiply the iteration.
    /// </summary>
    IReadOnlyList<byte[]> ListDrain(IStoreBatch batch, byte[] key);

    /// <summary>
    /// Number of entries. Read for depth/in-flight/dead-letter counts
    /// (<c>HwStatsCommand</c>) and to count a group's backlog before retiring it
    /// (<c>Decommission.RetireGroup</c>). Reads against a <see cref="IStoreSnapshot"/>.
    ///
    /// <para><b>On ordered keys this is a range count over the list prefix.</b> If
    /// that is measured to be too slow on the stats path (OD1/tuning), the fallback
    /// is a maintained length counter via <see cref="Increment"/> — but a counter
    /// that can drift is a liability, so it is a measured second step, not a default.</para>
    /// </summary>
    long ListLength(IStoreSnapshot snapshot, byte[] key);

    // =========================================================================
    // FAMILY 3 — Ordered set (by score)
    //   the delayed set (hw:q:{q}:delayed, score = absolute delivery ticks),
    //   job schedules (hw:job:{q}:schedules, score = nextFireTicks).
    //   Garnet call sites: HwQSend, HwPublish (AT), HwQClaim (promote + fire jobs),
    //     HwJob (set/del/list), HwStats
    // =========================================================================

    /// <summary>
    /// Adds or replaces <paramref name="member"/> at <paramref name="score"/>.
    /// The delayed-send path (<c>HwQSend</c>/<c>HwPublish</c> <c>AT</c>) and job
    /// fire-and-re-arm (<c>HwQClaim.FireDueJobs</c>, <c>HwJob.RunSet</c>).
    ///
    /// <para><paramref name="score"/> is a signed .NET tick count. <b>This is the
    /// structure that replaces Garnet's <c>SortedSet</c> object</b>, and the win is
    /// the whole feature: on ordered keys it is <c>z|&lt;set&gt;|&lt;order-preserving
    /// score&gt;|&lt;member&gt;</c>, so a range scan by score is a prefix iterate and
    /// space reclaims by compaction. Garnet's culture-formatted double score (which
    /// returned a tick count as <c>6,39E+17</c> on a European machine — see
    /// <c>HwQClaim.PromoteDueMessages</c>) cannot happen: the score is bytes.</para>
    ///
    /// <para><b>Reference:</b> <c>reference/stow-engine/Encoding/Int64Encoder.cs</c> —
    /// sign-flipped big-endian so lexicographic byte order equals numeric order;
    /// <c>Layout/IndexKey.cs</c> — the <c>&lt;value&gt;&lt;id&gt;</c> suffix layout the
    /// score/member key mirrors.</para>
    /// </summary>
    void SortedSetAdd(IStoreBatch batch, byte[] key, long score, byte[] member);

    /// <summary>Removes <paramref name="member"/> regardless of score. Promotion and re-arm remove the old member before re-adding.</summary>
    void SortedSetRemove(IStoreBatch batch, byte[] key, byte[] member);

    /// <summary>
    /// Returns members with score in <c>[minScore, maxScore]</c>, ascending,
    /// up to <paramref name="limit"/>. The "what is due now?" query:
    /// <c>SortedSetRange("-inf", now, byScore, limit)</c> in <c>PromoteDueMessages</c>
    /// and <c>FireDueJobs</c>; the full-range enumerate (<c>"-inf","+inf"</c>) in
    /// <c>HwJob</c> LIST/find. Members are returned <b>without scores</b> — the job
    /// record carries its own nextFire because Garnet's members-only range did too.
    ///
    /// <para>Range-read then remove, never pop-and-restore: a gap between a pop and a
    /// write-back loses anything not yet due (design note in <c>PromoteDueMessages</c>).</para>
    ///
    /// <para><b>Reference:</b> <c>reference/stow-engine/Engine/ReadPath.cs</c>
    /// <c>ScanDocuments</c> — the same bounded prefix iterate with a take limit.</para>
    /// </summary>
    IReadOnlyList<byte[]> SortedSetRangeByScore(
        IStoreSnapshot snapshot, byte[] key, long minScore, long maxScore, int limit);

    /// <summary>Number of members. <c>HwStats</c> (deferred count) and <c>HwJob.RunDel</c> (remove from index when the last schedule goes).</summary>
    long SortedSetLength(IStoreSnapshot snapshot, byte[] key);

    // =========================================================================
    // FAMILY 4 — Membership set
    //   service nodes (hw:svc:{s}:nodes), queue nodes (hw:q:{q}:nodes),
    //   channel groups (hw:ch:{ch}:groups).
    //   Garnet call sites: HwDequeue, HwQClaim, HwSubscribe, Decommission
    //
    //   NOTE: SetMembers is NEW on this seam. Garnet's commands never called it —
    //   they kept a Main-store mirror string instead, because reading a Set in
    //   Prepare registered a watch that the exclusive lock failed (004.1). With no
    //   Prepare and no watch, a set is just a set and the mirrors collapse (T3.3).
    // =========================================================================

    /// <summary>
    /// Adds <paramref name="member"/>; returns true if it was newly added. The
    /// "register this worker / this group" step (<c>HwDequeue</c>, <c>HwQClaim</c>,
    /// <c>HwSubscribe</c>). The added/existed answer is the membership set's
    /// authority that the old mirror could only approximate.
    ///
    /// <para><b>On ordered keys a set is a prefix</b> <c>s|&lt;set&gt;|&lt;member&gt;</c>
    /// with an empty value; membership is a point get, add is a put, iterate is a
    /// prefix scan. <b>Reference:</b> <c>reference/stow-engine/Encoding/StringEncoder.cs</c>
    /// for encoding the member into a self-delimiting compound key.</para>
    /// </summary>
    bool SetAdd(IStoreBatch batch, byte[] key, byte[] member);

    /// <summary>Removes <paramref name="member"/>. Node decommission and group retirement (<c>Decommission</c>).</summary>
    void SetRemove(IStoreBatch batch, byte[] key, byte[] member);

    // =========================================================================
    // Range delete — retirement and decommission (037 §2, and C4.6)
    // =========================================================================

    /// <summary>
    /// Stages the deletion of every key whose bytes begin with
    /// <paramref name="keyPrefix"/>. The whole-structure teardown path: retiring a
    /// subscriber group destroys its queue, delayed set, DLQ, processing list and
    /// membership in one operation (<c>Decommission.RetireGroup</c>), and
    /// decommissioning a node removes its every key range.
    ///
    /// <para><b>Why it is here and not a delete loop.</b> Under Garnet the commands
    /// enumerate and <c>DELETE</c> each key because there was no range primitive.
    /// On an ordered keyspace a prefix is a contiguous range, so this is one
    /// <c>DeleteRange</c> over <c>[keyPrefix, keyPrefix++]</c> — measured elsewhere at
    /// ~1 ms for 50 000 keys — instead of N staged deletes.</para>
    ///
    /// <para><b>This is the mechanical answer to C4.6.</b> A drained queue's range is
    /// <b>physically reclaimed</b> at the next compaction, not merely logically
    /// truncated the way Garnet's <c>TruncateUntil</c> left retired segments on disk.
    /// Reclamation is compaction, which is what an LSM does for a living.</para>
    ///
    /// <para><b>Reference:</b> <c>reference/stow-tech/keyspace.md</c> §7 (the
    /// <c>DeleteRange</c> bounds and cost) and <c>storage-model.md</c> §7 (tombstone →
    /// compaction reclaim).</para>
    /// </summary>
    void DeleteRange(IStoreBatch batch, byte[] keyPrefix);

    /// <summary>
    /// Returns every member. <b>New on this seam</b> (see the family note): it
    /// replaces every Garnet Main-store mirror read — the node lists the claim/dequeue
    /// path enumerated, the group list publish fanned out to, the job index, group
    /// membership, node subscriptions. Reads against a <see cref="IStoreSnapshot"/>.
    ///
    /// <para><b>Reference:</b> <c>reference/stow-engine/Engine/ReadPath.cs</c>
    /// <c>ScanDocuments</c> — a prefix iterate is exactly a set enumeration.</para>
    /// </summary>
    IReadOnlyList<byte[]> SetMembers(IStoreSnapshot snapshot, byte[] key);
}
