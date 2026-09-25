using System.Buffers.Binary;
using System.Diagnostics;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 062 — replica pulls served from the primary's in-memory tail. A caught-up pull used to call
/// GetUpdatesSince, which re-reads the live WAL file from its start: 1.5 ms at 2k writes, 54 ms at 120k,
/// every 50 ms per replica. These pin: same pages as the WAL, never a gap, and a cost that does not grow.
/// </summary>
public class ReplicationTailTests
{
    private static byte[] Batch(ulong seq, uint count, int size = 32)
    {
        var data = new byte[Math.Max(size, 12)];
        BinaryPrimitives.WriteUInt64LittleEndian(data, seq);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), count);
        return data;
    }

    private static ReplicationTail Capturing(long maxBytes = ReplicationTail.DefaultMaxBytes)
    {
        var tail = new ReplicationTail(maxBytes);
        tail.Enable();
        return tail;
    }

    [Fact]
    public void Reads_TheContiguousRun_AfterTheCursor()
    {
        var tail = Capturing();
        tail.Add(Batch(1, 2));   // 1..2
        tail.Add(Batch(3, 1));   // 3
        tail.Add(Batch(4, 3));   // 4..6

        var page = new List<(ulong Seq, byte[] Data)>();
        tail.TryRead(2, 1 << 20, page, out var more).Should().BeTrue();
        page.Select(b => b.Seq).Should().Equal(3UL, 4UL);
        more.Should().BeNull();

        page.Clear();
        tail.TryRead(4, 1 << 20, page, out _).Should().BeFalse(
            "seq 5 is inside the batch 4..6: served whole, the replica would skip it — the WAL path trims instead");
    }

    [Fact]
    public void StopsAtAHole_AndHealsWhenTheLateBatchArrives()
    {
        var tail = Capturing();
        tail.Add(Batch(1, 1));
        tail.Add(Batch(3, 1));   // 2 is written but its committer has not added it yet

        var page = new List<(ulong Seq, byte[] Data)>();
        tail.TryRead(0, 1 << 20, page, out _).Should().BeTrue();
        page.Select(b => b.Seq).Should().Equal(1UL);

        page.Clear();
        tail.TryRead(1, 1 << 20, page, out _).Should().BeFalse("the batch holding seq 2 is not held — the WAL must answer");

        tail.Add(Batch(2, 1));
        page.Clear();
        tail.TryRead(0, 1 << 20, page, out _).Should().BeTrue();
        page.Select(b => b.Seq).Should().Equal(1UL, 2UL, 3UL);
    }

    [Fact]
    public void ByteCap_SplitsThePage_AndReportsWhereToContinue()
    {
        var tail = Capturing();
        for (ulong s = 1; s <= 5; s++) tail.Add(Batch(s, 1, size: 100));

        var page = new List<(ulong Seq, byte[] Data)>();
        tail.TryRead(0, 250, page, out var more).Should().BeTrue();
        page.Select(b => b.Seq).Should().Equal(1UL, 2UL);
        more.Should().Be(3UL);

        page.Clear();
        tail.TryRead(0, 1, page, out more).Should().BeTrue();
        page.Should().ContainSingle("the first batch always fits, as on the WAL path");
        more.Should().Be(2UL);
    }

    [Fact]
    public void Eviction_TurnsOldCursorsIntoMisses()
    {
        var tail = Capturing(maxBytes: 300);
        for (ulong s = 1; s <= 10; s++) tail.Add(Batch(s, 1, size: 100));

        tail.Bytes.Should().BeLessThanOrEqualTo(300);
        var page = new List<(ulong Seq, byte[] Data)>();
        tail.TryRead(0, 1 << 20, page, out _).Should().BeFalse("seq 1 was evicted");
        tail.TryRead(8, 1 << 20, page, out _).Should().BeTrue();
        page.Select(b => b.Seq).Should().Equal(9UL, 10UL);
    }

    [Fact]
    public void NothingIsCaptured_UntilAReplicaPulls_AndClearEmptiesIt()
    {
        var tail = new ReplicationTail();
        tail.Add(Batch(1, 1));
        tail.Count.Should().Be(0, "a broker with no replica pays nothing");

        tail.Enable();
        tail.Add(Batch(2, 1));
        tail.Count.Should().Be(1);

        tail.Clear();
        tail.Count.Should().Be(0);
        tail.Bytes.Should().Be(0);
    }

    /// <summary>Follow the primary from the replica's watermark until caught up; every page applied.</summary>
    private static void CatchUp(ReplicationFeeder primary, RocksDbStore replica, int maxBytes, ulong until, Func<ulong, bool>? beforePull = null)
    {
        var db = replica.Replication.Engine;
        for (var guard = 0; ReplicationApply.Watermark(db) < until; guard++)
        {
            guard.Should().BeLessThan(100_000, "a replica must make progress on every pull");
            var w = ReplicationApply.Watermark(db);
            if (beforePull is not null && !beforePull(w)) return;
            var page = primary.Pull(w, maxBytes);
            page.Batches.Should().NotBeEmpty($"the replica at {w} is behind {until}");
            page.Batches[0].Seq.Should().Be(w + 1, "a page starts exactly after the replica's watermark");
            foreach (var (seq, data) in page.Batches)
                ReplicationApply.TryIngest(db, seq, data).Should().BeTrue($"batch {seq} is new to the replica");
        }
    }

    [Fact]
    public void Replicas_FedFromTheTail_TheWal_OrBoth_EndIdentical_UnderConcurrentCommits()
    {
        var dirs = Enumerable.Range(0, 4).Select(_ => ReplicationToolkitSurfaceTests.NewTempDir()).ToArray();
        try
        {
            using var store = RocksDbStore.Open(dirs[0]);
            using var fromTail = RocksDbStore.Open(dirs[1]);
            using var fromWal = RocksDbStore.Open(dirs[2]);
            using var switched = RocksDbStore.Open(dirs[3]);
            var feeder = store.Replication;
            var start = feeder.Engine.GetLatestSequenceNumber();
            foreach (var r in new[] { fromTail, fromWal, switched })
                ReplicationApply.Watermark(r.Replication.Engine).Should().Be(start, "replicas start aligned with the primary");
            feeder.Pull(start, 1 << 20);                 // a replica attaches: capture starts

            // Concurrent committers: RocksDB's group commit merges them into shared WAL records.
            Parallel.For(0, 4, worker =>
            {
                var rng = new Random(worker);
                for (var i = 0; i < 400; i++)
                {
                    using var b = store.NewBatch();
                    for (var n = rng.Next(1, 4); n > 0; n--)
                        store.Set(b, Highway.Server.Storage.Layout.HighwayKeyspace.Kv($"w{worker}:{i}:{n}"), new byte[rng.Next(1, 200)]);
                    if (i % 5 == 0) store.Delete(b, Highway.Server.Storage.Layout.HighwayKeyspace.Kv($"w{worker}:{i - 1}:1"));
                    b.Commit();
                }
            });
            var latest = feeder.Engine.GetLatestSequenceNumber();

            // Where the tail's batch boundaries fall inside a merged WAL record.
            var tailStarts = new List<(ulong Seq, byte[] Data)>();
            feeder.Tail.TryRead(start, int.MaxValue, tailStarts, out _).Should().BeTrue("everything since the attach was captured");
            tailStarts.Sum(b => b.Data.Length).Should().BeGreaterThan(0);

            // 1. Tail only: every pull answered from memory.
            CatchUp(feeder, fromTail, 700, latest, w =>
            {
                var probe = new List<(ulong, byte[])>();
                feeder.Tail.TryRead(w, 700, probe, out _).Should().BeTrue($"the tail answers the cursor {w}");
                return true;
            });

            // 3a. Switched: half way via the tail, stopping at a tail boundary...
            var half = tailStarts[tailStarts.Count / 2].Seq - 1;
            CatchUp(feeder, switched, 700, latest, w => w < half);

            // 2. WAL only, and 3b. the rest of the switched replica: the tail is gone (a primary restart).
            feeder.Tail.Clear();
            var walRecords = new List<ulong>();
            CatchUp(feeder, fromWal, 1, latest, w => { walRecords.Add(w + 1); return true; });
            CatchUp(feeder, switched, 700, latest);

            var expected = store.DumpData();
            foreach (var (name, r) in new[] { ("tail", fromTail), ("wal", fromWal), ("switched", switched) })
            {
                ReplicationApply.Watermark(r.Replication.Engine).Should().Be(latest, name);
                var actual = r.DumpData();
                actual.Select(e => Convert.ToHexString(e.Key) + "=" + Convert.ToHexString(e.Value))
                    .Should().Equal(expected.Select(e => Convert.ToHexString(e.Key) + "=" + Convert.ToHexString(e.Value)), name);
            }
        }
        finally { foreach (var d in dirs) ReplicationToolkitSurfaceTests.TryDelete(d); }
    }

    [Fact]
    public void Trim_StartsAMergedRecordMidway_AndRefusesWhatItCannotParse()
    {
        using var wb = new RocksDbSharp.WriteBatch();
        wb.Put(new byte[] { 1 }, new byte[] { 10 });
        wb.Delete(new byte[] { 2 });
        wb.PutLogData(new byte[] { 0xAA }, 1);
        wb.DeleteRange(new byte[] { 3 }, 1, new byte[] { 4 }, 1);
        wb.Put(new byte[] { 5 }, new byte[] { 50, 51 });
        var data = wb.ToBytes();
        BinaryPrimitives.WriteUInt64LittleEndian(data, 100);   // as if written at 100..103

        WriteBatchTrim.TryStartAt(data, 102, out var rest).Should().BeTrue();
        BinaryPrimitives.ReadUInt64LittleEndian(rest).Should().Be(102);
        BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(8)).Should().Be(2u, "DeleteRange and the last Put remain");
        using (var parsed = new RocksDbSharp.WriteBatch(rest))
            parsed.Count().Should().Be(2);

        WriteBatchTrim.TryStartAt(data, 100, out var whole).Should().BeTrue();
        whole.Should().Equal(data);
        WriteBatchTrim.TryStartAt(data, 104, out _).Should().BeFalse("104 is past the batch");
        WriteBatchTrim.TryStartAt(data, 99, out _).Should().BeFalse("99 is before it");

        var unknown = (byte[])data.Clone();
        unknown[12] = 0x11;                                    // BlobIndex: not something Highway writes
        WriteBatchTrim.TryStartAt(unknown, 101, out _).Should().BeFalse("an unknown record is refused, never guessed");
    }

    [Fact]
    public void PullCost_DoesNotGrowWithTheWal()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        using var store = RocksDbStore.Open(dir);
        try
        {
            var feeder = store.Replication;
            feeder.Pull(feeder.Engine.GetLatestSequenceNumber(), 1 << 20);
            var payload = new byte[300];
            var written = 0;
            double Measure(int target)
            {
                ulong behind = 0;
                for (; written < target; written++)
                {
                    behind = feeder.Engine.GetLatestSequenceNumber();   // a replica's cursor sits on a batch boundary
                    using var b = store.NewBatch();
                    store.Set(b, Highway.Server.Storage.Layout.HighwayKeyspace.Kv("q" + written), payload);
                    b.Commit();
                }
                var latest = feeder.Engine.GetLatestSequenceNumber();
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 200; i++)
                {
                    feeder.Pull(behind, 1 << 20).Batches.Should().ContainSingle();       // one batch behind
                    feeder.Pull(latest, 1 << 20).Batches.Should().BeEmpty();             // caught up
                }
                return sw.Elapsed.TotalMilliseconds / 400;
            }

            var small = Measure(2_000);
            var large = Measure(40_000);   // the WAL path took ~18 ms per pull here

            large.Should().BeLessThan(1.0, $"a pull must not re-read the WAL (2k writes: {small:F3} ms, 40k: {large:F3} ms)");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }
}
