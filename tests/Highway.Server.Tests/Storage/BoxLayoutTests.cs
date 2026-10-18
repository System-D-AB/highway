using Highway.Server.Storage.Crystal;
using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Replication;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 065 — the box layout. Keys order and isolate correctly; the catalog allocates, finds, lists
/// and retires; and a work box behaves exactly like a plain reference model of a queue under long random
/// operation sequences, on both stores — with every count a stored counter that must equal the model.
/// </summary>
public class BoxLayoutTests
{
    public static TheoryData<string> Stores => new() { "memory", "rocks" };

    private static (IHighwayStore Store, Action Cleanup) Open(string kind)
    {
        if (kind == "memory") return (new InMemoryStore(), () => { });
        var dir = TempDirs.NewTempDir();
        var store = CrystalHighwayStore.Open(dir);
        return (store, () => { store.Dispose(); TempDirs.TryDelete(dir); });
    }

    private static byte[] Entry(string id, ushort attempts = 0, ushort requeues = 0)
    {
        var b = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes(id), Encoding.UTF8.GetBytes("p-" + id), attempts);
        return requeues > 0 ? Envelope.WithLineage(b, requeues) : b;
    }

    // ---------------------------------------------------------------- keys

    [Fact]
    public void Keys_OrderNumerically_AndStayInsideTheirBoxAndSpace()
    {
        var keys = new[]
        {
            BoxKeys.Seq(16, BoxKeys.Ready, 2), BoxKeys.Seq(16, BoxKeys.Ready, 10), BoxKeys.Seq(16, BoxKeys.Ready, 256),
            BoxKeys.TicksSeq(16, BoxKeys.Lease, 5, 1), BoxKeys.TicksSeq(16, BoxKeys.Lease, 5, 9), BoxKeys.TicksSeq(16, BoxKeys.Lease, 70000, 0),
            BoxKeys.Seq(17, BoxKeys.Ready, 0),
        };
        keys.Should().BeInAscendingOrder(ByteArrayComparer.Instance);

        foreach (var k in keys.Take(3))
            k.AsSpan().StartsWith(BoxKeys.Space(16, BoxKeys.Ready)).Should().BeTrue();
        BoxKeys.Seq(17, BoxKeys.Ready, 0).AsSpan().StartsWith(BoxKeys.Box(16)).Should().BeFalse("another box never shares a prefix");
        HighwayKeyspace.Kv("x").AsSpan().StartsWith(BoxKeys.Box(BoxKeys.SystemBox)).Should().BeTrue("family keys live in the system box");

        BoxKeys.TrailingSeq(BoxKeys.TicksSeq(16, BoxKeys.Lease, 5, 9)).Should().Be(9);
        BoxKeys.TicksAt(BoxKeys.TicksSeq(16, BoxKeys.Lease, 70000, 0)).Should().Be(70000);
        BoxKeys.NameAfterTicks(BoxKeys.TicksName(16, BoxKeys.ScheduleIndex, 3, "nightly\0x")).Should().Be("nightly\0x");
    }

    // ---------------------------------------------------------------- catalog

    [Theory]
    [MemberData(nameof(Stores))]
    public void Catalog_Allocates_Finds_Lists_Retires_AndNeverReusesAnId(string kind)
    {
        var (store, cleanup) = Open(kind);
        try
        {
            var rt = new BoxRuntime();
            using var locks = new StripedLock();
            var a = Catalog.Ensure(store, rt, locks, BoxKeys.Kind.Queue, "orders", 1);
            var b = Catalog.Ensure(store, rt, locks, BoxKeys.Kind.Service, "orders", 1);   // same name, other kind
            var again = Catalog.Ensure(store, new BoxRuntime(), locks, BoxKeys.Kind.Queue, "orders", 2);

            a.Should().BeGreaterThanOrEqualTo(BoxKeys.FirstWorkBox);
            b.Should().NotBe(a);
            again.Should().Be(a, "an existing name resolves to its box, even through a cold cache");

            using (var batch = store.NewBatch())
            {
                Catalog.List(store, batch).Select(x => (x.Kind, x.Name, x.Box))
                    .Should().BeEquivalentTo([(BoxKeys.Kind.Service, "orders", b), (BoxKeys.Kind.Queue, "orders", a)]);
                Catalog.Version(store, batch).Should().Be(2);
            }

            using (var wb = new WorkBox(store, rt, a, tracksBytes: true))
            {
                wb.Enqueue(Entry("m1"));
                wb.Commit();
            }
            using (var batch = store.NewBatch())
            {
                Catalog.Retire(store, batch, rt, BoxKeys.Kind.Queue, "orders", 3).Should().Be(a);
                batch.Commit();
            }
            rt.Forget(BoxKeys.Kind.Queue, "orders", a);

            using (var batch = store.NewBatch())
            {
                Catalog.Find(store, batch, rt, BoxKeys.Kind.Queue, "orders").Should().BeNull();
                store.Scan(batch, BoxKeys.Box(a), BoxKeys.Box(a), 10).Should().BeEmpty("retiring deletes the box's whole range");
                Catalog.ReadMeta(store, batch, a)!.State.Should().Be("retired");
            }
            Catalog.Ensure(store, rt, locks, BoxKeys.Kind.Queue, "orders", 4).Should().NotBe(a, "a retired id is never reused");
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- reference model

    private sealed class Model
    {
        public readonly List<(ulong Seq, string Id, ushort Attempts, ushort Requeues)> Ready = [];
        public readonly Dictionary<ulong, (string Id, ushort Attempts, long Claim, string Node, ushort Requeues)> InFlight = [];
        public readonly List<(long Due, string Id)> Delayed = [];
        public readonly List<(string Id, ushort Attempts, ushort Requeues)> Dead = [];

        public Model Clone()
        {
            var c = new Model();
            c.Ready.AddRange(Ready);
            foreach (var kv in InFlight) c.InFlight[kv.Key] = kv.Value;
            c.Delayed.AddRange(Delayed);
            c.Dead.AddRange(Dead);
            return c;
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void WorkBox_MatchesAReferenceModel_UnderRandomOperations(string kind)
    {
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var (store, cleanup) = Open(kind);
            try { RunModel(store, seed); }
            finally { cleanup(); }
        }
    }

    private static void RunModel(IHighwayStore store, int seed)
    {
        const int maxAttempts = 3, maxDead = 5;
        const long lease = 100;
        var rng = new Random(seed);
        var rt = new BoxRuntime();
        using var locks = new StripedLock();
        var box = Catalog.Ensure(store, rt, locks, BoxKeys.Kind.Queue, "model", 0);
        var m = new Model();
        long now = 1_000;
        var nextId = 0;
        var held = new List<ulong>();

        for (var step = 0; step < 1500; step++)
        {
            now += rng.Next(0, 20);
            if (rng.Next(40) == 0) now -= rng.Next(0, 150);   // the clock steps back (failover, NTP): floors must cope
            var because = $"seed {seed}, step {step}";
            // Occasionally the command fails and its batch is discarded: nothing may change — not the
            // data, not the counters, not the floors (only raised after a commit).
            var discard = rng.Next(25) == 0;
            var committed = m;
            if (discard) m = m.Clone();
            using var wb = new WorkBox(store, rt, box, tracksBytes: true);
            switch (rng.Next(10))
            {
                case 0 or 1:
                {
                    var id = "m" + nextId++;
                    wb.Enqueue(Entry(id));
                    m.Ready.Add((0, id, 0, 0));
                    break;
                }
                case 2:
                {
                    var id = "d" + nextId++;
                    var due = now + rng.Next(1, 200);
                    wb.EnqueueDelayed(Entry(id), due);
                    m.Delayed.Add((due, id));
                    break;
                }
                case 3:
                {
                    var promoted = wb.PromoteDue(now);
                    var due = m.Delayed.Where(d => d.Due <= now).OrderBy(d => d.Due).ToList();
                    promoted.Should().Be(due.Count, because);
                    foreach (var d in due) { m.Delayed.Remove(d); m.Ready.Add((0, d.Id, 0, 0)); }
                    break;
                }
                case 4 or 5:
                {
                    var n = rng.Next(1, 4);
                    var node = "n" + rng.Next(3);
                    var claimed = wb.Claim(node, now, n, "model");
                    var expected = m.Ready.Take(n).ToList();
                    claimed.Select(c => Encoding.UTF8.GetString(c.Id)).Should().Equal(expected.Select(e => e.Id), because + " (FIFO)");
                    for (var i = 0; i < claimed.Count; i++)
                    {
                        m.Ready.RemoveAt(0);
                        m.InFlight[claimed[i].Token] = (expected[i].Id, expected[i].Attempts, now, node, expected[i].Requeues);
                        claimed[i].Attempts.Should().Be(expected[i].Attempts, because);
                        held.Add(claimed[i].Token);
                    }
                    break;
                }
                case 6:
                {
                    if (held.Count == 0) break;
                    var token = held[rng.Next(held.Count)];
                    var expected = m.InFlight.Remove(token);
                    wb.Ack(token).Should().Be(expected, because + " (ack of a " + (expected ? "held" : "stale") + " token)");
                    break;
                }
                case 7:
                {
                    if (held.Count == 0) break;
                    var token = held[rng.Next(held.Count)];
                    var expected = m.InFlight.TryGetValue(token, out var f);
                    wb.Touch(token, now).Should().Be(expected, because);
                    if (expected) m.InFlight[token] = f with { Claim = now };
                    break;
                }
                case 8:
                {
                    var expiry = now - lease;
                    var dead = wb.SweepLeases(expiry, now, maxAttempts, maxDead);
                    var expired = m.InFlight.Where(kv => kv.Value.Claim < expiry)
                        .OrderBy(kv => kv.Value.Claim).ThenBy(kv => kv.Key).ToList();
                    var expectDead = new List<string>();
                    foreach (var (token, f) in expired)
                    {
                        m.InFlight.Remove(token);
                        var next = (ushort)(f.Attempts + 1);
                        if (next > maxAttempts) { m.Dead.Add((f.Id, next, f.Requeues)); expectDead.Add(f.Id); }
                        else m.Ready.Add((0, f.Id, next, f.Requeues));
                    }
                    while (m.Dead.Count > maxDead) m.Dead.RemoveAt(0);
                    dead.Select(d => d.Id).Should().Equal(expectDead, because);
                    break;
                }
                default:
                {
                    var node = "n" + rng.Next(3);
                    var mine = m.InFlight.Where(kv => kv.Value.Node == node).OrderBy(kv => kv.Key).ToList();
                    wb.RequeueNode(node, 0).Should().Be(mine.Count, because);
                    foreach (var (token, f) in mine) { m.InFlight.Remove(token); m.Ready.Add((0, f.Id, (ushort)(f.Attempts + 1), f.Requeues)); }
                    if (rng.Next(4) == 0)
                    {
                        var n = rng.Next(1, 3);
                        var take = m.Dead.Take(n).ToList();
                        wb.RequeueDead(n).Should().Be(take.Count, because);
                        foreach (var d in take) { m.Dead.RemoveAt(0); m.Ready.Add((0, d.Id, 0, (ushort)(d.Requeues + 1))); }
                    }
                    break;
                }
            }

            if (discard) { m = committed; continue; }   // wb disposed uncommitted
            wb.Commit();

            using var check = new WorkBox(store, rt, box, tracksBytes: true);
            check.Ready.Should().Be(m.Ready.Count, because);
            check.InFlight.Should().Be(m.InFlight.Count, because);
            check.Delayed.Should().Be(m.Delayed.Count, because);
            check.Dead.Should().Be(m.Dead.Count, because);
            check.Bytes.Should().Be(m.Ready.Sum(r => (long)Entry(r.Id, r.Attempts, r.Requeues).Length) + m.Delayed.Sum(d => (long)Entry(d.Id).Length), because);
            check.PeekDead(100).Select(d => { DeadLetter.Decode(d.Value, out _, out var a, out _, out _); return a; })
                .Should().Equal(m.Dead.Select(d => d.Attempts), because);
        }

        // Everything the model holds is still claimable, in order, once all delays and leases elapse.
        using var drain = new WorkBox(store, rt, box, tracksBytes: true);
        now += 1_000_000;
        drain.PromoteDue(now);
        drain.Claim("final", now, 100_000, "model").Count
            .Should().Be(m.Ready.Count + m.Delayed.Count, "nothing ready or delayed is ever lost (seed " + seed + ")");
    }

    // ---- the id cache cannot be refilled with a retired id (072 review, finding 2) --------------

    [Fact]
    public void ALookupStartedBeforeARetirement_DoesNotCacheTheRetiredId()
    {
        // Catalog.Find reads the name index without the box lock and then caches what it read. Interleaving:
        // the lookup notes the generation and reads box 7; the retirer commits and forgets 7; the lookup then
        // caches. Before the generation it cached 7 again, and every path that trusts the cache under its
        // locks (retire, leave, join) would act on a deleted box.
        var rt = new BoxRuntime();
        var generation = rt.Generation;                          // Find: before its read
        rt.Forget(BoxKeys.Kind.Queue, "news@billing", 7);         // the retirement, committed, forgets
        rt.RememberIf(BoxKeys.Kind.Queue, "news@billing", 7, generation);   // Find: caches what it read

        rt.TryGetId(BoxKeys.Kind.Queue, "news@billing", out _).Should().BeFalse("the retirement happened after the read");

        rt.RememberIf(BoxKeys.Kind.Queue, "news@billing", 9, rt.Generation);
        rt.TryGetId(BoxKeys.Kind.Queue, "news@billing", out var live).Should().BeTrue();
        live.Should().Be(9u, "a lookup that started after the retirement caches normally");
    }

    [Fact]
    public void AnEpochReset_InvalidatesLookupsInFlight()
    {
        var rt = new BoxRuntime();
        var generation = rt.Generation;
        rt.Reset();
        rt.RememberIf(BoxKeys.Kind.Service, "orders", 3, generation);

        rt.TryGetId(BoxKeys.Kind.Service, "orders", out _).Should().BeFalse();
    }
}
