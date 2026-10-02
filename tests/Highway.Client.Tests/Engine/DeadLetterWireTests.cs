using System.Text;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Wire;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Client.Tests.Engine;

public class DeadLetterWireTests
{
    public sealed class SampleMessage
    {
        public string? Text { get; set; }
        public int Number { get; set; }
    }

    [Fact]
    public void DecodeDeadLetter_ValidRecord_DecodesAllFields()
    {
        var msg = new SampleMessage { Text = "hello", Number = 42 };
        var envelope = HighwayJson.EncodeEnvelope("node-1", msg);
        var rpcEntry = Envelope.EncodeRpcEntry("msg-100"u8, envelope, attempts: 3);

        var failBlock = Envelope.EncodeFailureBlock(
            "System.TimeoutException"u8,
            "System.InvalidOperationException"u8,
            "Timeout connecting to database"u8);
        var entryWithFailure = Envelope.WithFailureBlock(rpcEntry, failBlock);
        var entryWithLineage = Envelope.WithLineage(entryWithFailure, requeues: 2);

        var deadTicks = DateTime.UtcNow.Ticks;
        var deadRecord = DeadLetter.Encode(deadTicks, 3, "MAX_ATTEMPTS", entryWithLineage);

        var result = DeadLetterWire.DecodeDeadLetter(
            typeof(SampleMessage),
            deadRecord,
            "000000010000000000000005",
            DeadLetterTargetKind.Queue,
            "orders",
            group: null);

        var letter = result as DeadLetter<SampleMessage>;
        letter.Should().NotBeNull();
        letter!.Id.Should().Be("msg-100");
        letter.DeadLetterId.Should().Be("000000010000000000000005");
        letter.Target.Kind.Should().Be(DeadLetterTargetKind.Queue);
        letter.Target.Name.Should().Be("orders");
        letter.Attempts.Should().Be(3);
        letter.Requeues.Should().Be(2);
        letter.Reason.Should().Be("MAX_ATTEMPTS");
        letter.DeadLetteredAt.Ticks.Should().Be(deadTicks);

        letter.LastFailure.Should().NotBeNull();
        letter.LastFailure!.Type.Should().Be("System.TimeoutException");
        letter.LastFailure.Detail.Should().Be("Timeout connecting to database");

        letter.FirstFailure.Should().NotBeNull();
        letter.FirstFailure!.Type.Should().Be("System.InvalidOperationException");
        letter.FirstFailure.Detail.Should().Be("Timeout connecting to database");

        letter.Message.Should().NotBeNull();
        letter.Message!.Text.Should().Be("hello");
        letter.Message.Number.Should().Be(42);
        letter.RawPayload.ToArray().Should().BeEquivalentTo(envelope);
    }

    [Fact]
    public void DecodeDeadLetter_PoisonJsonPayload_LeavesMessageNullAndRawPayloadIntact()
    {
        var corruptedPayload = "NOT_A_VALID_JSON_ENVELOPE"u8.ToArray();
        var rpcEntry = Envelope.EncodeRpcEntry("msg-bad"u8, corruptedPayload, attempts: 5);
        var deadRecord = DeadLetter.Encode(DateTime.UtcNow.Ticks, 5, "MAX_ATTEMPTS", rpcEntry);

        var result = DeadLetterWire.DecodeDeadLetter(
            typeof(SampleMessage),
            deadRecord,
            "000000010000000000000009",
            DeadLetterTargetKind.Queue,
            "poison-queue",
            group: null);

        var letter = result as DeadLetter<SampleMessage>;
        letter.Should().NotBeNull();
        letter!.Message.Should().BeNull();
        letter.RawPayload.ToArray().Should().BeEquivalentTo(corruptedPayload);
        letter.Id.Should().Be("msg-bad");
        letter.DeadLetterId.Should().Be("000000010000000000000009");
    }

    [Fact]
    public void DecodeDeadLetter_NoLineageOrFailure_DefaultsGracefully()
    {
        var msg = new SampleMessage { Text = "simple", Number = 1 };
        var envelope = HighwayJson.EncodeEnvelope("node-1", msg);
        var rpcEntry = Envelope.EncodeRpcEntry("msg-1"u8, envelope, attempts: 1);
        var deadRecord = DeadLetter.Encode(DateTime.UtcNow.Ticks, 1, "MAX_ATTEMPTS", rpcEntry);

        var result = DeadLetterWire.DecodeDeadLetter(
            typeof(SampleMessage),
            deadRecord,
            "000000010000000000000002",
            DeadLetterTargetKind.Group,
            "events",
            group: "workers");

        var letter = result as DeadLetter<SampleMessage>;
        letter.Should().NotBeNull();
        letter!.Requeues.Should().Be(0);
        letter.LastFailure.Should().BeNull();
        letter.FirstFailure.Should().BeNull();
        letter.Target.Kind.Should().Be(DeadLetterTargetKind.Group);
        letter.Target.Group.Should().Be("workers");
        letter.Target.ToString().Should().Be("Q:events@workers");
    }
}
