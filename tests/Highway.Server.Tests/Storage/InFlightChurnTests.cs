using System.Diagnostics.Metrics;
using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Observability;
using Highway.Server.Resp;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Rocks;
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
    private static byte[][] F(params string[] t) => [.. t.Select(Encoding.UTF8.GetBytes)];

    private sealed class NullBell : IDoorbell { public int Ring(string channel, ReadOnlySpan<byte> payload) => 0; }

    private static (RocksDbStore Store, CommandDispatcher Dispatcher, string Dir) Broker()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        var store = RocksDbStore.Open(dir);
        var options = new HighwayServerOptions();
        var dispatcher = new CommandDispatcher(
            store, new StripedLock(), new NullBell(), new FlightRecorder(options.Observability), options, store.Replication);
        return (store, dispatcher, dir);
    }

    private static List<byte[]> KeysUnder(RocksDbStore store, byte[] prefix)
    {
        using var batch = store.NewBatch();
        return [.. store.ListEntries(batch, prefix).Select(e => e.Key)];
    }

    [Fact]
    public void Ack_RemovesOnlyTheAckedEntry_AndLeavesTheOthersUnderTheirOwnKeys()
    {
        var (store, d, dir) = Broker();
        try
        {
            for (var i = 1; i <= 5; i++)
            {
                d.Dispatch(F("HW.CALL", "svc.echo", "r" + i, "body"), new RespWriter());
                d.Dispatch(F("HW.DEQUEUE", "svc.echo", "worker-1"), new RespWriter());
            }
            var proc = HighwayKeyspace.ListPrefix(HighwayNames.ServiceProcessing("svc.echo", "worker-1"));
            var before = KeysUnder(store, proc);
            before.Should().HaveCount(5);

            d.Dispatch(F("HW.ACK", "svc.echo", "worker-1", "r3"), new RespWriter());

            var after = KeysUnder(store, proc);
            after.Should().HaveCount(4);
            after.Should().BeEquivalentTo(before.Where((_, i) => i != 2), o => o.WithStrictOrdering(),
                "the four survivors keep their keys and order — nothing is re-written (061 A1)");
        }
        finally { store.Dispose(); ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void IdlePoll_WithCallsInFlight_WritesNothing()
    {
        var (store, d, dir) = Broker();
        try
        {
            for (var i = 1; i <= 8; i++)
            {
                d.Dispatch(F("HW.CALL", "svc.echo", "r" + i, "body"), new RespWriter());
                d.Dispatch(F("HW.DEQUEUE", "svc.echo", "worker-1"), new RespWriter());
            }
            var sequence = store.Replication.Engine.GetLatestSequenceNumber();

            for (var i = 0; i < 20; i++)
                d.Dispatch(F("HW.DEQUEUE", "svc.echo", "worker-1"), new RespWriter());   // empty queue, 8 in flight

            store.Replication.Engine.GetLatestSequenceNumber().Should().Be(sequence,
                "an idle poll whose sweep finds nothing expired stages nothing, so nothing is written (061 A2)");
        }
        finally { store.Dispose(); ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void DlqPeek_WritesNothing()
    {
        var (store, d, dir) = Broker();
        try
        {
            using (var batch = store.NewBatch())
            {
                for (var i = 0; i < 5; i++)
                    HwQSendCommand.PushTail(store, batch, HighwayNames.QueueDeadLetter("orders"), Encoding.UTF8.GetBytes("dead" + i));
                batch.Commit();
            }
            var sequence = store.Replication.Engine.GetLatestSequenceNumber();

            d.Dispatch(F("HW.DLQ", "PEEK", "Q", "orders"), new RespWriter());

            store.Replication.Engine.GetLatestSequenceNumber().Should().Be(sequence, "PEEK is read-only (061 A1)");
        }
        finally { store.Dispose(); ReplicationToolkitSurfaceTests.TryDelete(dir); }
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
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        using var store = RocksDbStore.Open(dir);
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
                    HwQSendCommand.PushTail(store, batch, list, Encoding.UTF8.GetBytes(value));
                    if (!discard) model.Add(value);
                }
                else if (roll < 50)
                {
                    var value = "h" + counter++;
                    ConsumerSupport.PushHead(store, batch, list, Encoding.UTF8.GetBytes(value));
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
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
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
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using (var store = RocksDbStore.Open(dir))
            {
                for (var round = 0; round < 20; round++)
                {
                    using var batch = store.NewBatch();
                    for (var i = 0; i < 500; i++)
                        HwQSendCommand.PushTail(store, batch, "churn", new byte[256]);
                    batch.Commit();
                    using var drain = store.NewBatch();
                    store.ListDrain(drain, HighwayKeyspace.ListPrefix("churn"));
                    drain.Commit();
                }
                using var keep = store.NewBatch();
                HwQSendCommand.PushTail(store, keep, "kept", Encoding.UTF8.GetBytes("survivor"));
                keep.Commit();
            }

            var result = HighwayStorageMaintenance.Compact(dir);
            result.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);

            using var reopened = RocksDbStore.Open(dir);
            using var read = reopened.NewBatch();
            reopened.ListEntries(read, HighwayKeyspace.ListPrefix("kept"))
                .Select(e => Encoding.UTF8.GetString(e.Value)).Should().Equal("survivor");
            reopened.ListEntries(read, HighwayKeyspace.ListPrefix("churn")).Should().BeEmpty();
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Compact_RefusesADirectoryThatIsNotABrokerDatabase()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var act = () => HighwayStorageMaintenance.Compact(dir);
            act.Should().Throw<InvalidOperationException>().WithMessage("*not a Highway data directory*");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void StorageGauges_ReportMemtableDeletes()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        using var store = RocksDbStore.Open(dir);
        try
        {
            using (var batch = store.NewBatch())
            {
                for (var i = 0; i < 100; i++) HwQSendCommand.PushTail(store, batch, "g", new byte[8]);
                batch.Commit();
            }
            using (var drain = store.NewBatch())
            {
                store.ListDrain(drain, HighwayKeyspace.ListPrefix("g"));
                drain.Commit();
            }

            var name = "Highway.Server.Test." + Guid.NewGuid().ToString("N");
            using var metrics = new HighwayMetrics(null, () => [], new FlightRecorder(new HighwayServerOptions().Observability), name, store.SumProperty);
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
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void ScanOverManyMemtableTombstones_FlushesTheMemtable()
    {
        // The post-061-a production shape: pops leave tombstones in a slowly-filling memtable, and a
        // read that starts at the list's first key (a queue-depth count) steps over every one of them.
        // The scan flush trigger (T9) must turn that into a flush instead of a cost that grows with uptime.
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        using var store = RocksDbStore.Open(dir);
        try
        {
            var list = HighwayKeyspace.ListPrefix("depth");
            const int popped = 5_000;
            using (var batch = store.NewBatch())
            {
                for (var i = 0; i < popped + 3; i++) HwQSendCommand.PushTail(store, batch, "depth", new byte[8]);
                batch.Commit();
            }
            for (var i = 0; i < popped; i++)
            {
                using var pop = store.NewBatch();
                store.ListLeftPop(pop, list).Should().NotBeNull();
                pop.Commit();
            }
            store.SumProperty("rocksdb.num-deletes-active-mem-table").Should().BeGreaterThanOrEqualTo(popped);

            using (var snap = store.Snapshot())
                store.ListLength(snap, list).Should().Be(3, "the count is unaffected by the tombstones it steps over");

            // RocksDB schedules a marked memtable's flush on the next write.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (store.SumProperty("rocksdb.num-deletes-active-mem-table") >= popped && DateTime.UtcNow < deadline)
            {
                using (var poke = store.NewBatch())
                {
                    HwQSendCommand.PushTail(store, poke, "poke", new byte[8]);
                    poke.Commit();
                }
                Thread.Sleep(50);
            }

            store.SumProperty("rocksdb.num-deletes-active-mem-table").Should().BeLessThan(popped,
                "a scan over thousands of memtable tombstones marks the memtable for flush");
            using (var snap = store.Snapshot())
                store.ListLength(snap, list).Should().Be(3);
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }
}
