using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentAssertions;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// The seam contract (038 R2.1) — one suite that defines <see cref="IHighwayStore"/>'s
/// semantics and must pass identically against every implementation. It is written
/// against <see cref="InMemoryStore"/> first (T2); <c>RocksDbStore</c> inherits and runs
/// the same cases (T3). The contract comes from what the commands need, not from what any
/// engine happens to do.
///
/// <para>Subclass and override <see cref="CreateStore"/> to run the whole suite against a
/// different implementation.</para>
/// </summary>
public abstract class StoreContractTests
{
    protected abstract IHighwayStore CreateStore();

    // -- helpers ---------------------------------------------------------------

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(byte[] b) => Encoding.UTF8.GetString(b);

    /// <summary>Runs an action inside one batch and commits it.</summary>
    private static void InBatch(IHighwayStore store, Action<IStoreBatch> act)
    {
        using var batch = store.NewBatch();
        act(batch);
        batch.Commit();
    }

    /// <summary>Reads inside a fresh snapshot.</summary>
    private static T Read<T>(IHighwayStore store, Func<IStoreSnapshot, T> read)
    {
        using var snap = store.Snapshot();
        return read(snap);
    }

    // =========================================================================
    // KV
    // =========================================================================

    [Fact]
    public void Kv_SetThenGet()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Kv("rep:req-1");

        InBatch(store, b => store.Set(b, key, B("payload")));

        Read(store, s => S(store.Get(s, key)!)).Should().Be("payload");
    }

    [Fact]
    public void Kv_GetAbsent_ReturnsNull()
    {
        using var store = CreateStore();
        Read(store, s => store.Get(s, HighwayKeyspace.Kv("missing"))).Should().BeNull();
    }

    [Fact]
    public void Kv_Delete_IsIdempotent()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Kv("k");

        // Deleting an absent key is not an error.
        InBatch(store, b => store.Delete(b, key));
        InBatch(store, b => store.Set(b, key, B("v")));
        InBatch(store, b => store.Delete(b, key));
        InBatch(store, b => store.Delete(b, key)); // again — still fine

        Read(store, s => store.Get(s, key)).Should().BeNull();
    }

    [Fact]
    public void Kv_LastWriteWins()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Kv("k");
        InBatch(store, b => store.Set(b, key, B("first")));
        InBatch(store, b => store.Set(b, key, B("second")));
        Read(store, s => S(store.Get(s, key)!)).Should().Be("second");
    }

    // =========================================================================
    // Increment — visibility within its batch, monotonicity
    // =========================================================================

    [Fact]
    public void Increment_FromAbsent_StartsAtDelta()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Counter("seq:q:invoices");
        long result = 0;
        InBatch(store, b => result = store.Increment(b, key, 1));
        result.Should().Be(1);
    }

    [Fact]
    public void Increment_IsVisibleWithinSameBatch()
    {
        // The read-your-own-writes property (R0.4 / WBWI): two increments in one batch
        // must see each other, or an in-batch seq allocation collides (the B1 trap).
        using var store = CreateStore();
        var key = HighwayKeyspace.Counter("seq:q:invoices");

        var values = new List<long>();
        InBatch(store, b =>
        {
            values.Add(store.Increment(b, key, 1)); // 1
            values.Add(store.Increment(b, key, 1)); // 2
            values.Add(store.Increment(b, key, 1)); // 3
        });

        values.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Increment_PersistsAcrossBatches()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Counter("c");
        long a = 0, b2 = 0;
        InBatch(store, b => a = store.Increment(b, key, 5));
        InBatch(store, b => b2 = store.Increment(b, key, 3));
        a.Should().Be(5);
        b2.Should().Be(8);
    }

    // =========================================================================
    // List — FIFO, pop-after-push, multi-pop distinctness, drain
    // =========================================================================

    [Fact]
    public void List_FifoOrder()
    {
        using var store = CreateStore();
        var name = HighwayNames.Queue("invoices");
        var prefix = HighwayKeyspace.ListPrefix(name);

        // Push three, each at the next seq (allocated from the list's counter).
        PushTail(store, name, "a");
        PushTail(store, name, "b");
        PushTail(store, name, "c");

        Pop(store, prefix).Should().Be("a");
        Pop(store, prefix).Should().Be("b");
        Pop(store, prefix).Should().Be("c");
        Read(store, s => store.ListLength(s, prefix)).Should().Be(0);
    }

    [Fact]
    public void List_PopAfterPush_InSameBatch()
    {
        // A pop must see a push staged earlier in the same batch (read-your-own-writes).
        using var store = CreateStore();
        var name = HighwayNames.Queue("q");
        var prefix = HighwayKeyspace.ListPrefix(name);
        var counter = HighwayKeyspace.Counter(HighwayNames.ListSequence(name));

        byte[]? popped = null;
        InBatch(store, b =>
        {
            var seq = store.Increment(b, counter, 1) - 1; // reserve seq 0
            store.ListRightPush(b, HighwayKeyspace.ListEntry(name, seq), B("only"));
            popped = store.ListLeftPop(b, prefix);
        });

        popped.Should().NotBeNull();
        S(popped!).Should().Be("only");
    }

    [Fact]
    public void List_MultiPop_InOneBatch_ReturnsDistinctEntries()
    {
        // R0.4's mechanism asserted: popping N times in one batch yields N distinct
        // entries, not the same head N times. This is what 039's claim loop relies on.
        using var store = CreateStore();
        var name = HighwayNames.Queue("q");
        var prefix = HighwayKeyspace.ListPrefix(name);

        PushTail(store, name, "a");
        PushTail(store, name, "b");
        PushTail(store, name, "c");

        var popped = new List<string>();
        InBatch(store, b =>
        {
            for (var i = 0; i < 3; i++)
            {
                var v = store.ListLeftPop(b, prefix);
                if (v is not null) popped.Add(S(v));
            }
        });

        popped.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void List_LeftPop_Empty_ReturnsNull()
    {
        using var store = CreateStore();
        var prefix = HighwayKeyspace.ListPrefix(HighwayNames.Queue("empty"));
        InBatch(store, b => store.ListLeftPop(b, prefix).Should().BeNull());
    }

    [Fact]
    public void List_HeadPush_PopsBeforeTail()
    {
        // Redeliver-to-head: a left-push (negative seq) must pop before existing tail entries.
        using var store = CreateStore();
        var name = HighwayNames.Queue("q");
        var prefix = HighwayKeyspace.ListPrefix(name);

        PushTail(store, name, "tail-0"); // seq 0
        PushTail(store, name, "tail-1"); // seq 1
        InBatch(store, b => store.ListLeftPush(b, HighwayKeyspace.ListEntry(name, -1), B("head")));

        Pop(store, prefix).Should().Be("head");
        Pop(store, prefix).Should().Be("tail-0");
        Pop(store, prefix).Should().Be("tail-1");
    }

    [Fact]
    public void List_Drain_ReturnsAllInOrder_AndEmpties()
    {
        using var store = CreateStore();
        var name = HighwayNames.Queue("q");
        var prefix = HighwayKeyspace.ListPrefix(name);
        PushTail(store, name, "a");
        PushTail(store, name, "b");

        List<byte[]> drained = [];
        InBatch(store, b => drained = [.. store.ListDrain(b, prefix)]);

        drained.Select(S).Should().Equal("a", "b");
        Read(store, s => store.ListLength(s, prefix)).Should().Be(0);
    }

    [Fact]
    public void List_DrainFilterPushBack_InOneBatch()
    {
        // The ack/fail/touch/sweep idiom: drain, keep some, push survivors back — atomically.
        using var store = CreateStore();
        var name = HighwayNames.QueueProcessing("q", "node-A");
        var prefix = HighwayKeyspace.ListPrefix(name);
        PushTail(store, name, "keep-1");
        PushTail(store, name, "drop");
        PushTail(store, name, "keep-2");

        InBatch(store, b =>
        {
            var all = store.ListDrain(b, prefix);
            long seq = 0;
            foreach (var entry in all)
            {
                if (S(entry) == "drop") continue;
                store.ListRightPush(b, HighwayKeyspace.ListEntry(name, seq++), entry);
            }
        });

        var remaining = new List<string>();
        InBatch(store, b =>
        {
            byte[]? v;
            while ((v = store.ListLeftPop(b, prefix)) is not null) remaining.Add(S(v));
        });
        remaining.Should().Equal("keep-1", "keep-2");
    }

    // =========================================================================
    // Ordered set — range bounds, member order, removal
    // =========================================================================

    [Fact]
    public void SortedSet_RangeByScore_InclusiveBounds()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed("q"));

        InBatch(store, b =>
        {
            store.SortedSetAdd(b, key, 100, B("m100"));
            store.SortedSetAdd(b, key, 200, B("m200"));
            store.SortedSetAdd(b, key, 300, B("m300"));
        });

        // [100, 200] inclusive → m100, m200.
        var due = Read(store, s => store.SortedSetRangeByScore(s, key, long.MinValue, 200, 100));
        due.Select(S).Should().Equal("m100", "m200");
    }

    [Fact]
    public void SortedSet_Range_AscendingByScore()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.SortedSetPrefix(HighwayNames.JobSchedules("q"));
        InBatch(store, b =>
        {
            store.SortedSetAdd(b, key, 300, B("c"));
            store.SortedSetAdd(b, key, 100, B("a"));
            store.SortedSetAdd(b, key, 200, B("b"));
        });

        var all = Read(store, s => store.SortedSetRangeByScore(s, key, long.MinValue, long.MaxValue, 100));
        all.Select(S).Should().Equal("a", "b", "c"); // score order, not insertion order
    }

    [Fact]
    public void SortedSet_Range_RespectsLimit()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed("q"));
        InBatch(store, b =>
        {
            for (var i = 0; i < 10; i++)
                store.SortedSetAdd(b, key, i, B($"m{i}"));
        });

        var limited = Read(store, s => store.SortedSetRangeByScore(s, key, long.MinValue, long.MaxValue, 3));
        limited.Should().HaveCount(3);
        limited.Select(S).Should().Equal("m0", "m1", "m2");
    }

    [Fact]
    public void SortedSet_Remove()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed("q"));
        InBatch(store, b =>
        {
            store.SortedSetAdd(b, key, 100, B("m1"));
            store.SortedSetAdd(b, key, 200, B("m2"));
        });
        InBatch(store, b => store.SortedSetRemove(b, key, B("m1")));

        Read(store, s => store.SortedSetLength(s, key)).Should().Be(1);
        Read(store, s => store.SortedSetRangeByScore(s, key, long.MinValue, long.MaxValue, 10))
            .Select(S).Should().Equal("m2");
    }

    [Fact]
    public void SortedSet_NegativeAndPositiveScores_Order()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.SortedSetPrefix("z");
        InBatch(store, b =>
        {
            store.SortedSetAdd(b, key, 5, B("pos"));
            store.SortedSetAdd(b, key, -5, B("neg"));
            store.SortedSetAdd(b, key, 0, B("zero"));
        });

        Read(store, s => store.SortedSetRangeByScore(s, key, long.MinValue, long.MaxValue, 10))
            .Select(S).Should().Equal("neg", "zero", "pos");
    }

    // =========================================================================
    // Membership set — add/existed, members, remove
    // =========================================================================

    [Fact]
    public void Set_Add_ReturnsTrueOnFirst_FalseOnRepeat()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.SetPrefix(HighwayNames.QueueNodes("q"));

        bool first = false, second = false;
        InBatch(store, b => first = store.SetAdd(b, key, B("node-A")));
        InBatch(store, b => second = store.SetAdd(b, key, B("node-A")));

        first.Should().BeTrue("newly added");
        second.Should().BeFalse("already present");
    }

    [Fact]
    public void Set_Members_EnumeratesAll()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.SetPrefix(HighwayNames.ChannelGroups("orders"));
        InBatch(store, b =>
        {
            store.SetAdd(b, key, B("billing"));
            store.SetAdd(b, key, B("shipping"));
        });

        Read(store, s => store.SetMembers(s, key)).Select(S)
            .Should().BeEquivalentTo("billing", "shipping");
    }

    [Fact]
    public void Set_Remove()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.SetPrefix(HighwayNames.QueueNodes("q"));
        InBatch(store, b =>
        {
            store.SetAdd(b, key, B("node-A"));
            store.SetAdd(b, key, B("node-B"));
        });
        InBatch(store, b => store.SetRemove(b, key, B("node-A")));

        Read(store, s => store.SetMembers(s, key)).Select(S).Should().BeEquivalentTo("node-B");
    }

    // =========================================================================
    // DeleteRange — prefix bounds, no sibling over-delete
    // =========================================================================

    [Fact]
    public void DeleteRange_RemovesWholePrefix()
    {
        using var store = CreateStore();
        var name = HighwayNames.Queue("invoices");
        var prefix = HighwayKeyspace.ListPrefix(name);
        PushTail(store, name, "a");
        PushTail(store, name, "b");

        InBatch(store, b => store.DeleteRange(b, prefix));

        Read(store, s => store.ListLength(s, prefix)).Should().Be(0);
    }

    [Fact]
    public void DeleteRange_DoesNotTouchSiblingName()
    {
        // The self-delimiting encoding's payoff: deleting "invoices" must not touch
        // "invoices2" — 'invoices' is not a byte-prefix of 'invoices2' after encoding.
        using var store = CreateStore();
        var victim = HighwayNames.Queue("invoices");
        var sibling = HighwayNames.Queue("invoices2");
        PushTail(store, victim, "victim");
        PushTail(store, sibling, "survivor");

        InBatch(store, b => store.DeleteRange(b, HighwayKeyspace.ListPrefix(victim)));

        Read(store, s => store.ListLength(s, HighwayKeyspace.ListPrefix(victim))).Should().Be(0);
        Read(store, s => store.ListLength(s, HighwayKeyspace.ListPrefix(sibling))).Should().Be(1,
            "the sibling queue must be untouched");
    }

    // =========================================================================
    // T4 — seq allocation + head-push scheme
    // =========================================================================

    [Fact]
    public void Seq_MultipleHeadPushes_PopInReverseInsertionOrder()
    {
        // Scheme 1 (signed seq): each redeliver-to-head goes below the current low-water,
        // so the most-recently-head-pushed entry pops first — and all head entries pop
        // before any tail entry. This is FIFO-after-head-push.
        using var store = CreateStore();
        var name = HighwayNames.Queue("q");
        var prefix = HighwayKeyspace.ListPrefix(name);
        var lowWater = HighwayKeyspace.Counter(HighwayNames.ListSequence(name) + ":low");

        PushTail(store, name, "tail-0");
        PushTail(store, name, "tail-1");

        // Two head-pushes, each reserving the next-lower seq from a low-water counter.
        HeadPush(store, name, lowWater, "head-A"); // seq -1
        HeadPush(store, name, lowWater, "head-B"); // seq -2  (pops before -1)

        Pop(store, prefix).Should().Be("head-B");
        Pop(store, prefix).Should().Be("head-A");
        Pop(store, prefix).Should().Be("tail-0");
        Pop(store, prefix).Should().Be("tail-1");
    }

    [Fact]
    public async Task Seq_ConcurrentTailPush_TwoLists_EachMonotonicAndComplete()
    {
        // The realistic claim topology (tasks.md T4): two threads, two lists, per-key locks.
        // Distinct lists map to (almost certainly) distinct stripes, so they proceed in
        // parallel; each list's seq stays monotonic and no entry is lost.
        using var store = CreateStore();
        using var locks = new StripedLock();

        var listA = HighwayNames.Queue("A");
        var listB = HighwayNames.Queue("B");
        const int perList = 200;

        void PushMany(string list, string tag)
        {
            for (var i = 0; i < perList; i++)
            {
                using (locks.Lock(list)) // per-list lock, as a command would take
                    PushTail(store, list, $"{tag}-{i}");
            }
        }

        await Task.WhenAll(Task.Run(() => PushMany(listA, "A")), Task.Run(() => PushMany(listB, "B")));

        // Both lists complete and in order.
        DrainAll(store, HighwayKeyspace.ListPrefix(listA)).Should()
            .Equal(Enumerable.Range(0, perList).Select(i => $"A-{i}"));
        DrainAll(store, HighwayKeyspace.ListPrefix(listB)).Should()
            .Equal(Enumerable.Range(0, perList).Select(i => $"B-{i}"));
    }

    [Fact]
    public async Task Seq_ConcurrentTailPush_SameList_UnderLock_NoCollision()
    {
        // Two threads pushing to the SAME list, each holding the per-list lock, must
        // produce a complete monotonic sequence with no seq collision (the B1 trap the
        // per-key lock exists to prevent). Without the lock, two batches could reserve the
        // same seq and one push would overwrite the other.
        using var store = CreateStore();
        using var locks = new StripedLock();
        var name = HighwayNames.Queue("shared");
        const int perThread = 200;

        void PushMany(string tag)
        {
            for (var i = 0; i < perThread; i++)
            {
                using (locks.Lock(name))
                    PushTail(store, name, $"{tag}-{i}");
            }
        }

        await Task.WhenAll(Task.Run(() => PushMany("x")), Task.Run(() => PushMany("y")));

        var all = DrainAll(store, HighwayKeyspace.ListPrefix(name));
        all.Should().HaveCount(perThread * 2, "no push may be lost to a seq collision");
        all.Distinct().Should().HaveCount(perThread * 2, "every entry is present exactly once");
    }

    // =========================================================================
    // T5 — reply-slot expiry (OD5)
    // =========================================================================

    private const long Now = 1_000_000L;

    [Fact]
    public void Expiry_LiveSlot_IsReadable()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Kv(HighwayNames.ReplySlot("req-1"));

        InBatch(store, b => store.SetEx(b, key, B("reply"), expiresAtTicks: Now + 100));

        Read(store, s => store.GetLive(s, key, Now)).Should().NotBeNull();
        Read(store, s => S(store.GetLive(s, key, Now)!)).Should().Be("reply");
    }

    [Fact]
    public void Expiry_ExpiredSlot_ReadsAsAbsent_Immediately()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Kv(HighwayNames.ReplySlot("req-1"));

        InBatch(store, b => store.SetEx(b, key, B("reply"), expiresAtTicks: Now));

        // At exactly `Now` the slot has expired (>= is expired).
        Read(store, s => store.GetLive(s, key, Now)).Should().BeNull("an expired slot reads as gone");
        Read(store, s => store.GetLive(s, key, Now + 1)).Should().BeNull();
    }

    [Fact]
    public void Expiry_Sweep_PhysicallyRemovesExpired()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Kv(HighwayNames.ReplySlot("req-1"));

        InBatch(store, b => store.SetEx(b, key, B("reply"), expiresAtTicks: Now));

        // Before sweep: hidden by GetLive but the raw key is still physically present.
        Read(store, s => store.Get(s, key)).Should().NotBeNull("not yet physically removed");

        InBatch(store, b => store.SweepExpired(b, Now + 1));

        Read(store, s => store.Get(s, key)).Should().BeNull("sweep physically removed the expired slot");
    }

    [Fact]
    public void Expiry_Sweep_LeavesLiveSlots_AndPlainKeys()
    {
        using var store = CreateStore();
        var expiring = HighwayKeyspace.Kv(HighwayNames.ReplySlot("expiring"));
        var live = HighwayKeyspace.Kv(HighwayNames.ReplySlot("live"));
        var plain = HighwayKeyspace.Kv(HighwayNames.RegistrationNode("node-A")); // a plain Set on the k family

        InBatch(store, b =>
        {
            store.SetEx(b, expiring, B("gone"), expiresAtTicks: Now);
            store.SetEx(b, live, B("stay"), expiresAtTicks: Now + 1000);
            store.Set(b, plain, B("registration")); // NOT an expiring value
        });

        InBatch(store, b => store.SweepExpired(b, Now + 1));

        Read(store, s => store.Get(s, expiring)).Should().BeNull("expired → swept");
        Read(store, s => store.GetLive(s, live, Now + 1)).Should().NotBeNull("still live → kept");
        Read(store, s => store.Get(s, plain)).Should().NotBeNull(
            "a plain Set on the KV family must never be mistaken for an expiring value");
        Read(store, s => S(store.Get(s, plain)!)).Should().Be("registration");
    }

    // =========================================================================
    // Atomicity + fault injection (T6 / R4.2) — no partial state
    // =========================================================================

    [Fact]
    public void UncommittedBatch_ChangesNothing()
    {
        using var store = CreateStore();
        var key = HighwayKeyspace.Kv("k");

        using (var batch = store.NewBatch())
        {
            store.Set(batch, key, B("staged"));
            // no Commit
        }

        Read(store, s => store.Get(s, key)).Should().BeNull("an uncommitted batch must not mutate the store");
    }

    [Fact]
    public void FaultBetweenTwoWrites_LeavesNoPartialState()
    {
        // Two writes that must land together — a queue push and its byte-counter bump, the
        // shape of every real command. A fault after the first stage, before Commit, must
        // leave BOTH absent: the single commit point means staged-but-uncommitted is nothing.
        using var store = CreateStore();
        var entryKey = HighwayKeyspace.ListEntry(HighwayNames.Queue("q"), 0);
        var counterKey = HighwayKeyspace.Counter(HighwayNames.QueueBytes("q"));

        var thrown = false;
        try
        {
            using var batch = store.NewBatch();
            store.ListRightPush(batch, entryKey, B("msg"));
            store.Increment(batch, counterKey, 42);
            throw new InvalidOperationException("injected fault before commit");
#pragma warning disable CS0162 // unreachable — the throw models a command failing mid-work
            batch.Commit();
#pragma warning restore CS0162
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }

        thrown.Should().BeTrue();
        Read(store, s => store.Get(s, entryKey)).Should().BeNull("the push must not survive a pre-commit fault");
        Read(store, s => store.Get(s, counterKey)).Should().BeNull("the counter must not survive either");
    }

    [Fact]
    public void SecondBatch_AfterAFaultedFirst_CommitsCleanly()
    {
        // A faulted batch leaves no residue that would corrupt a subsequent one — the store
        // is exactly as it was before the faulted batch opened.
        using var store = CreateStore();
        var key = HighwayKeyspace.Kv("k");

        try
        {
            using var bad = store.NewBatch();
            store.Set(bad, key, B("doomed"));
            throw new InvalidOperationException("fault");
        }
        catch (InvalidOperationException) { /* discarded */ }

        InBatch(store, b => store.Set(b, key, B("good")));
        Read(store, s => S(store.Get(s, key)!)).Should().Be("good");
    }

    // =========================================================================
    // shared push helpers (use the real seq allocator, as a command would)
    // =========================================================================

    private static void PushTail(IHighwayStore store, string listName, string value)
    {
        var counter = HighwayKeyspace.Counter(HighwayNames.ListSequence(listName));
        InBatch(store, b =>
        {
            var seq = store.Increment(b, counter, 1) - 1; // 0-based tail seq
            store.ListRightPush(b, HighwayKeyspace.ListEntry(listName, seq), B(value));
        });
    }

    private static string Pop(IHighwayStore store, byte[] listPrefix)
    {
        string? result = null;
        InBatch(store, b =>
        {
            var v = store.ListLeftPop(b, listPrefix);
            if (v is not null) result = S(v);
        });
        return result!;
    }

    /// <summary>
    /// Redeliver-to-head using scheme 1 (signed seq): reserve the next seq *below* the
    /// current low-water from a dedicated counter that decrements, so each head-push sorts
    /// below the previous one and below every tail entry (seq &gt;= 0).
    /// </summary>
    private static void HeadPush(IHighwayStore store, string listName, byte[] lowWaterCounter, string value)
    {
        InBatch(store, b =>
        {
            var seq = store.Increment(b, lowWaterCounter, -1); // -1, -2, -3, …
            store.ListLeftPush(b, HighwayKeyspace.ListEntry(listName, seq), B(value));
        });
    }

    private static List<string> DrainAll(IHighwayStore store, byte[] listPrefix)
    {
        var result = new List<string>();
        InBatch(store, b =>
        {
            foreach (var entry in store.ListDrain(b, listPrefix))
                result.Add(S(entry));
        });
        return result;
    }
}

/// <summary>The contract suite bound to <see cref="InMemoryStore"/> (T2).</summary>
public sealed class InMemoryStoreContractTests : StoreContractTests
{
    protected override IHighwayStore CreateStore() => new InMemoryStore();
}

/// <summary>
/// The identical contract suite bound to <see cref="Highway.Server.Storage.Rocks.RocksDbStore"/>
/// (T3) — proving both implementations satisfy one contract. Each store opens a fresh temp
/// directory it owns and deletes on dispose.
/// </summary>
public sealed class RocksDbStoreContractTests : StoreContractTests
{
    protected override IHighwayStore CreateStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-store-" + Guid.NewGuid().ToString("N"));
        return Highway.Server.Storage.Rocks.RocksDbStore.Open(dir, ownsDirectory: true);
    }
}
