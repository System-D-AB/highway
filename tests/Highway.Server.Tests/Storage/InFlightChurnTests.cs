using Highway.Server.Storage.Crystal;
using System.Diagnostics.Metrics;
using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Observability;
using Highway.Server.Resp;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Replication;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 061 — in-flight work without churn. A broker at modest load spent a whole core because every
/// ack / fail / touch / lease sweep deleted and re-wrote every in-flight entry, and every later scan
/// stepped over the tombstones. These pin the new contract: change only what changed, write nothing when
/// nothing changed, and never lose a message to the head hint.
/// </summary>
public class InFlightChurnTests
{
    /// <summary>The pre-065 list push (a seq allocator + an ordered entry) — the list family still backs the registry.</summary>
    private static void PushTail(IHighwayStore store, IStoreBatch batch, string listName, byte[] value)
    {
        var seq = store.Increment(batch, HighwayKeyspace.Counter(HighwayNames.ListSequence(listName)), 1) - 1;
        store.ListRightPush(batch, HighwayKeyspace.ListEntry(listName, seq), value);
    }

    /// <summary>The pre-065 head push (a descending low-water seq) — exercised by the head-hint model.</summary>
    private static void PushHead(IHighwayStore store, IStoreBatch batch, string listName, byte[] value)
    {
        var seq = store.Increment(batch, HighwayKeyspace.Counter(HighwayNames.ListSequence(listName) + ":low"), -1);
        store.ListLeftPush(batch, HighwayKeyspace.ListEntry(listName, seq), value);
    }

    private const long T0 = 638_000_000_000_000_000L;

    private static (CrystalHighwayStore Store, Highway.Server.Tests.Commands.CommandHarness H, string Dir) Broker(HighwayServerOptions? options = null)
    {
        var dir = TempDirs.NewTempDir();
        var store = CrystalHighwayStore.Open(dir);
        return (store, new Highway.Server.Tests.Commands.CommandHarness(store, options ?? new HighwayServerOptions()), dir);
    }

    private static List<(byte[] Key, byte[] Value)> InFlightRows(CrystalHighwayStore store, uint box)
    {
        using var batch = store.NewBatch();
        var space = Highway.Server.Storage.Boxes.BoxKeys.Space(box, Highway.Server.Storage.Boxes.BoxKeys.InFlight);
        return [.. store.Scan(batch, space, space, 1000)];
    }

    [Fact]
    public void Ack_RemovesOnlyTheAckedClaim_AndLeavesTheOthersUntouched()
    {
        var (store, h, dir) = Broker();
        try
        {
            var tokens = new List<string>();
            for (var i = 1; i <= 5; i++)
            {
                h.Run(new HwCallCommand(), T0, "svc.echo", "r" + i, "body");
                tokens.Add(h.ClaimOne("SVC", "svc.echo", "worker-1", T0)!.Value.Token);
            }
            var box = uint.Parse(tokens[0].Split('.')[0]);
            var before = InFlightRows(store, box);
            before.Should().HaveCount(5);

            Highway.Server.Tests.Commands.CommandHarness.AsText(h.Run(new HwAckCommand(), T0, tokens[2])).Should().Be(":1\r\n");

            var after = InFlightRows(store, box);
            after.Select(r => Convert.ToHexString(r.Key) + Convert.ToHexString(r.Value))
                .Should().Equal(before.Where((_, i) => i != 2).Select(r => Convert.ToHexString(r.Key) + Convert.ToHexString(r.Value)),
                    "the four survivors keep their keys and bytes — an ack deletes one claim by key (061 A1, 065)");
        }
        finally { h.Dispose(); TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void IdlePoll_WithCallsInFlight_WritesNothing()
    {
        var (store, h, dir) = Broker();
        try
        {
            for (var i = 1; i <= 8; i++)
            {
                h.Run(new HwCallCommand(), T0, "svc.echo", "r" + i, "body");
                h.ClaimOne("SVC", "svc.echo", "worker-1", T0);
            }
            var sequence = store.LatestPosition;

            for (var i = 0; i < 20; i++)
                h.ClaimOne("SVC", "svc.echo", "worker-1", T0 + i).Should().BeNull();   // empty queue, 8 in flight

            store.LatestPosition.Should().Be(sequence,
                "an idle claim whose lease sweep finds nothing expired stages nothing, so nothing is written (061 A2)");
        }
        finally { h.Dispose(); TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void DlqPeek_WritesNothing()
    {
        var (store, h, dir) = Broker(new HighwayServerOptions { Lease = TimeSpan.FromSeconds(10), MaxDeliveryAttempts = 1 });
        try
        {
            var t = T0;
            for (var i = 0; i < 5; i++) h.Run(new HwSendCommand(), t, "orders", "m" + i, "p");
            for (var i = 0; i < 5; i++) h.ClaimOne("Q", "orders", "w", t);
            h.ClaimOne("Q", "orders", "w", t += TimeSpan.FromSeconds(11).Ticks);   // attempt 1: redelivered
            for (var i = 0; i < 5; i++) h.ClaimOne("Q", "orders", "w", t);
            h.ClaimOne("Q", "orders", "w", t += TimeSpan.FromSeconds(11).Ticks).Should().BeNull("all five dead-lettered");
            var sequence = store.LatestPosition;

            Highway.Server.Tests.Commands.CommandHarness.AsText(h.Run(new HwDlqCommand(), t, "PEEK", "Q", "orders")).Should().StartWith("*5");

            store.LatestPosition.Should().Be(sequence, "PEEK is read-only (061 A1)");
        }
        finally { h.Dispose(); TempDirs.TryDelete(dir); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HeadHint_NeverSkipsALiveEntry_AgainstAReferenceList(int seed)
    {
        // Randomised pushes, head-pushes, pops, out-of-order removals (acks), range deletes, discarded
        // batches and epoch changes over a few lists; every pop and every scan must match a plain
        // in-memory reference. A hint that ever rose above a live key would lose a message here.
        var dir = TempDirs.NewTempDir();
        using var store = CrystalHighwayStore.Open(dir);
        try
        {
            var random = new Random(seed);
            string[] lists = ["q.a", "q.b", "q.c"];
            var reference = lists.ToDictionary(l => l, _ => new List<string>());
            var counter = 0;

            for (var step = 0; step < 6_000; step++)
            {
                var list = lists[random.Next(lists.Length)];
                var prefix = HighwayKeyspace.ListPrefix(list);
                var model = reference[list];
                var roll = random.Next(100);
                var discard = random.Next(20) == 0;   // 5 %: build the batch, then throw it away

                using var batch = store.NewBatch();
                if (roll < 40)
                {
                    var value = "v" + counter++;
                    PushTail(store, batch, list, Encoding.UTF8.GetBytes(value));
                    if (!discard) model.Add(value);
                }
                else if (roll < 50)
                {
                    var value = "h" + counter++;
                    PushHead(store, batch, list, Encoding.UTF8.GetBytes(value));
                    if (!discard) model.Insert(0, value);
                }
                else if (roll < 75)
                {
                    var popped = store.ListLeftPop(batch, prefix);
                    var expected = model.Count > 0 ? model[0] : null;
                    (popped is null ? null : Encoding.UTF8.GetString(popped)).Should().Be(expected, $"step {step}, pop from {list}");
                    if (!discard && model.Count > 0) model.RemoveAt(0);
                }
                else if (roll < 95)
                {
                    var entries = store.ListEntries(batch, prefix);
                    entries.Select(e => Encoding.UTF8.GetString(e.Value)).Should().Equal(model, $"step {step}, scan of {list}");
                    if (entries.Count > 0)
                    {
                        var victim = random.Next(entries.Count);   // an ack of any in-flight entry
                        store.Delete(batch, entries[victim].Key);
                        if (!discard) model.RemoveAt(victim);
                    }
                }
                else if (roll < 98)
                {
                    store.DeleteRange(batch, prefix);
                    store.DeleteRange(batch, HighwayKeyspace.Counter(HighwayNames.ListSequence(list)));   // sequence restarts at 0
                    if (!discard) model.Clear();
                }
                else
                {
                    store.Replication.ObserveHigherEpoch(store.Replication.Epoch, "test");   // no-op epoch path
                }

                if (!discard) batch.Commit();
            }

            foreach (var list in lists)
            {
                using var batch = store.NewBatch();
                store.ListEntries(batch, HighwayKeyspace.ListPrefix(list))
                    .Select(e => Encoding.UTF8.GetString(e.Value)).Should().Equal(reference[list], $"final contents of {list}");
            }
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void SweepThrottle_SweepsAtMostOncePerInterval_PerScope()
    {
        var throttle = new SweepThrottle();
        var lease = TimeSpan.FromMinutes(5);   // interval = min(1 s, lease/10) = 1 s
        var t0 = DateTime.UtcNow.Ticks;

        throttle.ShouldSweep("svc:a", t0, lease).Should().BeTrue("first sweep");
        throttle.ShouldSweep("svc:a", t0 + TimeSpan.FromMilliseconds(500).Ticks, lease).Should().BeFalse("within the interval");
        throttle.ShouldSweep("svc:b", t0 + TimeSpan.FromMilliseconds(500).Ticks, lease).Should().BeTrue("another scope");
        throttle.ShouldSweep("svc:a", t0 + TimeSpan.FromSeconds(1).Ticks, lease).Should().BeTrue("interval elapsed");

        var shortLease = TimeSpan.FromSeconds(2);   // interval = 200 ms
        throttle.ShouldSweep("q:x", t0, shortLease).Should().BeTrue();
        throttle.ShouldSweep("q:x", t0 + TimeSpan.FromMilliseconds(250).Ticks, shortLease).Should().BeTrue(
            "a short lease sweeps proportionally often, so expiry detection stays prompt");
    }

    [Fact]
    public void Compact_DropsTombstones_AndKeepsTheData()
    {
        // 061 T5, on Crystal's offline compaction since Crystal 1.0.0-rc.3: a stopped broker's store is compacted and
        // reads back exactly what it held.
        var dir = TempDirs.NewTempDir();
        try
        {
            using (var store = CrystalHighwayStore.Open(dir))
            {
                using var fill = store.NewBatch();
                for (var i = 0; i < 200; i++)
                    PushTail(store, fill, "churn", Encoding.UTF8.GetBytes($"m{i}"));
                PushTail(store, fill, "kept", Encoding.UTF8.GetBytes("survivor"));
                fill.Commit();
                using var drain = store.NewBatch();
                store.ListDrain(drain, HighwayKeyspace.ListPrefix("churn"));   // 200 tombstones
                drain.Commit();
            }

            var result = HighwayStorageMaintenance.Compact(dir);
            result.BytesBefore.Should().BeGreaterThan(0);

            using var reopened = CrystalHighwayStore.Open(dir);
            using var read = reopened.NewBatch();
            reopened.ListEntries(read, HighwayKeyspace.ListPrefix("kept"))
                .Select(e => Encoding.UTF8.GetString(e.Value)).Should().Equal("survivor");
            reopened.ListEntries(read, HighwayKeyspace.ListPrefix("churn")).Should().BeEmpty();
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void Compact_RefusesADirectoryThatIsNotABrokerDatabase_AndALiveBroker()
    {
        var empty = TempDirs.NewTempDir();
        var live = TempDirs.NewTempDir();
        try
        {
            var notAStore = () => HighwayStorageMaintenance.Compact(empty);
            notAStore.Should().Throw<InvalidOperationException>().WithMessage("*not a Highway data directory*");

            using var store = CrystalHighwayStore.Open(live);
            var whileOpen = () => HighwayStorageMaintenance.Compact(live);
            whileOpen.Should().Throw<SystemD.Crystal.StoreLockedException>("a running broker holds the store");
        }
        finally { TempDirs.TryDelete(empty); TempDirs.TryDelete(live); }
    }

    [Fact]
    public void StorageGauges_ReportMemtableDeletes()
    {
        var dir = TempDirs.NewTempDir();
        using var store = CrystalHighwayStore.Open(dir);
        try
        {
            using (var batch = store.NewBatch())
            {
                for (var i = 0; i < 100; i++) PushTail(store, batch, "g", new byte[8]);
                batch.Commit();
            }
            using (var drain = store.NewBatch())
            {
                store.ListDrain(drain, HighwayKeyspace.ListPrefix("g"));
                drain.Commit();
            }

            var name = "Highway.Server.Test." + Guid.NewGuid().ToString("N");
            using var metrics = new HighwayMetrics(null, () => [], new FlightRecorder(new HighwayServerOptions().Observability), name, store.StorageProperty);
            var values = new Dictionary<string, long>();
            using var listener = new MeterListener
            {
                InstrumentPublished = (inst, l) => { if (inst.Meter.Name == name) l.EnableMeasurementEvents(inst); },
            };
            listener.SetMeasurementEventCallback<long>((inst, m, _, _) => values[inst.Name] = m);
            listener.Start();
            listener.RecordObservableInstruments();

            values.Should().ContainKey("highway.storage.estimated_keys");
            values["highway.storage.memtable_deletes"].Should().BeGreaterThanOrEqualTo(100, "the drain deleted 100 entries still in the memtable");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void ScanOverManyMemtableTombstones_FlushesTheMemtable()
    {
        // The post-061-a production shape: pops leave tombstones in a slowly-filling memtable, and a
        // read that starts at the list's first key (a queue-depth count) steps over every one of them.
        // The scan flush trigger (T9) must turn that into a flush instead of a cost that grows with uptime.
        var dir = TempDirs.NewTempDir();
        using var store = CrystalHighwayStore.Open(dir);
        try
        {
            var list = HighwayKeyspace.ListPrefix("depth");
            const int popped = 5_000;
            using (var batch = store.NewBatch())
            {
                for (var i = 0; i < popped + 3; i++) PushTail(store, batch, "depth", new byte[8]);
                batch.Commit();
            }
            for (var i = 0; i < popped; i++)
            {
                using var pop = store.NewBatch();
                store.ListLeftPop(pop, list).Should().NotBeNull();
                pop.Commit();
            }
            store.StorageProperty("rocksdb.num-deletes-active-mem-table").Should().BeGreaterThanOrEqualTo(popped);

            using (var snap = store.Snapshot())
                store.ListLength(snap, list).Should().Be(3, "the count is unaffected by the tombstones it steps over");

            // RocksDB schedules a marked memtable's flush on the next write.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (store.StorageProperty("rocksdb.num-deletes-active-mem-table") >= popped && DateTime.UtcNow < deadline)
            {
                using (var poke = store.NewBatch())
                {
                    PushTail(store, poke, "poke", new byte[8]);
                    poke.Commit();
                }
                Thread.Sleep(50);
            }

            store.StorageProperty("rocksdb.num-deletes-active-mem-table").Should().BeLessThan(popped,
                "a scan over thousands of memtable tombstones marks the memtable for flush");
            using (var snap = store.Snapshot())
                store.ListLength(snap, list).Should().Be(3);
        }
        finally { TempDirs.TryDelete(dir); }
    }
}
