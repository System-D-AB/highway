using FluentAssertions;
using Highway.Abstractions;
using Highway.Abstractions.Exceptions;
using Highway.Client.Scanning;
using Xunit;

namespace Highway.Client.Tests.Scanning;

/// <summary>
/// Feature 018, Task 0 — the client-side half of the <c>@</c> reservation.
/// The scanner throws at startup when <c>[Queue]</c> or <c>[Channel]</c> names
/// contain the reserved character, so the collision is caught before any traffic.
/// </summary>
public class AtSignReservationScannerTests
{
    private readonly DefaultTypeScanner _scanner = new();

    // --- Queue contracts ---

    [Queue("orders@billing")]
    private sealed record BadQueueContract : ISend;

    [Fact]
    public void QueueContract_WithAtSign_ThrowsReservedCharacterException()
    {
        var act = () => _scanner.ScanTypes([typeof(BadQueueContract)]);
        act.Should().Throw<ReservedCharacterException>()
            .WithMessage("*Queue*orders@billing*@*");
    }

    [Queue("orders.billing")]
    private sealed record GoodQueueContract : ISend;

    [Fact]
    public void QueueContract_WithoutAtSign_Succeeds()
    {
        var result = _scanner.ScanTypes([typeof(GoodQueueContract)]);
        result.QueueContracts.Should().ContainKey(typeof(GoodQueueContract));
    }

    // --- Queue processors ---

    [Queue("invoices@bad")]
    private sealed record BadProcessorMessage : ISend;

    private sealed class BadProcessor : IProcess<BadProcessorMessage>
    {
        public Task ProcessAsync(BadProcessorMessage message, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void QueueProcessor_WithAtSignInName_ThrowsReservedCharacterException()
    {
        var act = () => _scanner.ScanTypes([typeof(BadProcessorMessage), typeof(BadProcessor)]);
        act.Should().Throw<ReservedCharacterException>()
            .WithMessage("*@*");
    }

    // --- Channel contracts ---

    [Channel("events@topic")]
    private sealed record BadChannelMessage : IPublish;

    [Fact]
    public void ChannelContract_WithAtSign_ThrowsReservedCharacterException()
    {
        var act = () => _scanner.ScanTypes([typeof(BadChannelMessage)]);
        act.Should().Throw<ReservedCharacterException>()
            .WithMessage("*Channel*events@topic*@*");
    }

    [Channel("events.topic")]
    private sealed record GoodChannelMessage : IPublish;

    [Fact]
    public void ChannelContract_WithoutAtSign_Succeeds()
    {
        var result = _scanner.ScanTypes([typeof(GoodChannelMessage)]);
        result.MessageContracts.Should().ContainKey(typeof(GoodChannelMessage));
    }

    // --- Channel subscribers ---

    [Channel("orders@placed")]
    private sealed record BadSubscriberMessage : IPublish;

    private sealed class BadSubscriber : ISubscribe<BadSubscriberMessage>
    {
        public Task SubscribeAsync(BadSubscriberMessage message, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void ChannelSubscriber_WithAtSignInChannelName_ThrowsReservedCharacterException()
    {
        var act = () => _scanner.ScanTypes([typeof(BadSubscriberMessage), typeof(BadSubscriber)]);
        act.Should().Throw<ReservedCharacterException>()
            .WithMessage("*@*");
    }

    // --- NodeName validation ---

    [Fact]
    public void NodeName_WithAtSign_Throws()
    {
        var act = () => HighwayOptionsValidator.ValidateNodeName("node@1");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*@*reserved*");
    }

    [Fact]
    public void NodeName_WithoutAtSign_Passes()
    {
        var act = () => HighwayOptionsValidator.ValidateNodeName("node-1");
        act.Should().NotThrow();
    }
}
