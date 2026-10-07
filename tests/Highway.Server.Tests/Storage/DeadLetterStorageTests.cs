using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

public class DeadLetterStorageTests
{
    private static byte[] SampleEntry(string id, ushort attempts = 0)
        => Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes(id), Encoding.UTF8.GetBytes("payload-" + id), attempts);

    [Fact]
    public void EnvelopeLineage_NoTrailer_DecodesAsZeroRequeues()
    {
        var entry = SampleEntry("msg-1", 1);
        Envelope.TryGetLineage(entry, out var requeues, out var withoutLineage).Should().BeFalse();
        requeues.Should().Be(0);
        withoutLineage.SequenceEqual(entry).Should().BeTrue();

        Envelope.DecodeRpcEntry(entry, out var id, out var payload, out var attempts);
        Encoding.UTF8.GetString(id).Should().Be("msg-1");
        Encoding.UTF8.GetString(payload).Should().Be("payload-msg-1");
        attempts.Should().Be(1);
    }

    [Fact]
    public void EnvelopeLineage_WithTrailer_DecodesRequeuesAndStripsLineage()
    {
        var bare = SampleEntry("msg-2", 0);
        var withLineage = Envelope.WithLineage(bare, 3);

        withLineage.Length.Should().Be(bare.Length + 6, "[u16 requeues][u32 magic]");
        Envelope.TryGetLineage(withLineage, out var requeues, out var withoutLineage).Should().BeTrue();
        requeues.Should().Be(3);
        withoutLineage.SequenceEqual(bare).Should().BeTrue();

        Envelope.DecodeRpcEntry(withLineage, out var id, out _, out _);
        Encoding.UTF8.GetString(id).Should().Be("msg-2");
    }

    [Fact]
    public void EnvelopeLineage_WithFailureBlockAndTrailer_BothDecodedAndCarried()
    {
        var bare = SampleEntry("msg-3", 2);
        var failureBlock = Envelope.EncodeFailureBlock(
            Encoding.UTF8.GetBytes("System.Exception"),
            Encoding.UTF8.GetBytes(""),
            Encoding.UTF8.GetBytes("boom"));

        var withFailure = Envelope.WithFailureBlock(bare, failureBlock);
        var withBoth = Envelope.WithLineage(withFailure, 2);

        // Verify lineage extraction
        Envelope.TryGetLineage(withBoth, out var requeues, out var withoutLineage).Should().BeTrue();
        requeues.Should().Be(2);

        // Verify failure block extraction
        Envelope.TryGetFailureBlock(withBoth, out var extractedBlock, out var withoutBoth).Should().BeTrue();
        Envelope.DecodeFailureBlock(extractedBlock, out var type, out _, out var detail);
        Encoding.UTF8.GetString(type).Should().Be("System.Exception");
        Encoding.UTF8.GetString(detail).Should().Be("boom");
        withoutBoth.SequenceEqual(bare).Should().BeTrue();

        // CarryFailureBlock onto a fresh rebuilt entry copies both
        var rebuilt = SampleEntry("msg-3", 3);
        var carried = Envelope.CarryFailureBlock(withBoth, rebuilt);

        Envelope.TryGetLineage(carried, out var carriedReq, out _).Should().BeTrue();
        carriedReq.Should().Be(2);
        Envelope.TryGetFailureBlock(carried, out var carriedFb, out _).Should().BeTrue();
        Envelope.DecodeFailureBlock(carriedFb, out var carriedType, out _, out _);
        Encoding.UTF8.GetString(carriedType).Should().Be("System.Exception");
    }

    [Fact]
    public void DeadLetter_FormatAndParseDeadId_RoundTrips()
    {
        uint box = 18;
        ulong seq = 42;
        var deadId = DeadLetter.FormatDeadId(box, seq);

        deadId.Length.Should().Be(24);
        deadId.Should().Be("00000012000000000000002a");

        DeadLetter.TryParseDeadId(deadId.AsSpan(), out var parsedBox, out var parsedSeq).Should().BeTrue();
        parsedBox.Should().Be(box);
        parsedSeq.Should().Be(seq);

        DeadLetter.TryParseDeadId(Encoding.ASCII.GetBytes(deadId), out var parsedBoxBytes, out var parsedSeqBytes).Should().BeTrue();
        parsedBoxBytes.Should().Be(box);
        parsedSeqBytes.Should().Be(seq);
    }

    [Fact]
    public void DeadLetter_TryParseDeadId_RejectsMalformedAndReserved()
    {
        // Reserved system box (< 16)
        var systemBoxDeadId = DeadLetter.FormatDeadId(0, 1);
        DeadLetter.TryParseDeadId(systemBoxDeadId.AsSpan(), out _, out _).Should().BeFalse();

        // Wrong length
        DeadLetter.TryParseDeadId("00000012000000000000002".AsSpan(), out _, out _).Should().BeFalse();
        DeadLetter.TryParseDeadId("00000012000000000000002aa".AsSpan(), out _, out _).Should().BeFalse();

        // Invalid hex
        DeadLetter.TryParseDeadId("0000001g000000000000002a".AsSpan(), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void WorkBox_PointPurgeAndRequeue_BySeq_WorksExact()
    {
        var store = new InMemoryStore();
        var rt = new BoxRuntime();
        const uint boxId = 16;

        var entry1 = SampleEntry("d1", 5);
        var entry2 = SampleEntry("d2", 5);

        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.Enqueue(entry1);
            wb.Enqueue(entry2);
            wb.Commit();
        }

        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.Claim("w1", 100, 2, "test");
            wb.Commit();
        }

        // Sweep with lease expiry to move to Dead
        ulong seq1, seq2;
        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            var dead = wb.SweepLeases(200, 200, maxAttempts: 1, maxDeadEntries: 10);
            dead.Should().HaveCount(2);
            wb.Dead.Should().Be(2);

            var peeked = wb.PeekDead(10);
            peeked.Should().HaveCount(2);
            seq1 = BoxKeys.TrailingSeq(peeked[0].Key);
            seq2 = BoxKeys.TrailingSeq(peeked[1].Key);

            // Point read
            wb.ReadDead(seq1).Should().NotBeNull();
            wb.ReadDead(seq2).Should().NotBeNull();
            wb.ReadDead(9999).Should().BeNull();

            // Point purge seq1
            wb.PurgeDead(seq1).Should().BeTrue();
            wb.Dead.Should().Be(1);
            wb.ReadDead(seq1).Should().BeNull();
            wb.PurgeDead(seq1).Should().BeFalse("already purged");

            // Point requeue seq2
            wb.RequeueDead(seq2).Should().BeTrue();
            wb.Dead.Should().Be(0);
            wb.Ready.Should().Be(1);
            wb.Commit();
        }

        // Verify requeued item has attempts = 0 and requeues = 1
        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            var claimed = wb.Claim("w2", 300, 1, "test");
            claimed.Should().HaveCount(1);
            claimed[0].Attempts.Should().Be(0);
            Encoding.UTF8.GetString(claimed[0].Id).Should().Be("d2");

            var readyEntry = store.Read(wb.Batch, BoxKeys.Seq(boxId, BoxKeys.InFlight, claimed[0].Token))!;
            var inFlightRec = InFlightRecord.Decode(readyEntry);
            Envelope.TryGetLineage(inFlightRec.Entry, out var reqs, out _).Should().BeTrue();
            reqs.Should().Be(1);
        }
    }

    [Fact]
    public void WorkBox_PointPurge_LeavesHole_OldestScanSkipsHole_CountersExact()
    {
        var store = new InMemoryStore();
        var rt = new BoxRuntime();
        const uint boxId = 16;

        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            for (var i = 1; i <= 3; i++)
                wb.Enqueue(SampleEntry($"m{i}", 1));
            wb.Commit();
        }

        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.Claim("w1", 100, 3, "test");
            wb.Commit();
        }

        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.SweepLeases(200, 200, maxAttempts: 1, maxDeadEntries: 10);
            wb.Dead.Should().Be(3);

            var peeked = wb.PeekDead(10);
            peeked.Should().HaveCount(3);
            var seq2 = BoxKeys.TrailingSeq(peeked[1].Key); // middle entry

            // Purge the middle entry (seq2), creating a hole between seq1 and seq3
            wb.PurgeDead(seq2).Should().BeTrue();
            wb.Dead.Should().Be(2);

            // Oldest scan (TakeDead / RequeueDead) should skip hole and process seq1 and seq3 in order
            var moved = wb.RequeueDead(10);
            moved.Should().Be(2);
            wb.Dead.Should().Be(0);
            wb.Commit();
        }

        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            var claims = wb.Claim("w2", 300, 10, "test");
            claims.Select(c => Encoding.UTF8.GetString(c.Id)).Should().Equal(["m1", "m3"]);
        }
    }

    [Fact]
    public void WorkBox_PointRequeue_WithAfterDelay_EnqueuesDelayed()
    {
        var store = new InMemoryStore();
        var rt = new BoxRuntime();
        const uint boxId = 16;

        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.Enqueue(SampleEntry("delayed-test", 1));
            wb.Commit();
        }

        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.Claim("w1", 100, 1, "test");
            wb.Commit();
        }

        var delay = TimeSpan.FromSeconds(5);
        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.SweepLeases(200, 200, maxAttempts: 1, maxDeadEntries: 10);
            var seq = BoxKeys.TrailingSeq(wb.PeekDead(1)[0].Key);

            // Requeue with 5-second delay
            wb.RequeueDead(seq, delay, nowTicks: 1_000_000).Should().BeTrue();
            wb.Dead.Should().Be(0);
            wb.Delayed.Should().Be(1);
            wb.Ready.Should().Be(0);
            wb.Commit();
        }

        // Not claimable before due
        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.PromoteDue(1_000_000);
            wb.Ready.Should().Be(0);
        }

        // Due after delay
        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            wb.PromoteDue(1_000_000 + delay.Ticks);
            wb.Ready.Should().Be(1);
        }
    }
}
