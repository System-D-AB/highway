using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Client.Engine;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests;

[Queue("orders.queue")]
public record UnitDlqQueueMessage : ISend;

[Service("orders.service")]
public record UnitDlqServiceRequest : IReturn<UnitDlqServiceResponse>;

public class UnitDlqServiceResponse : Output;

[Channel("orders.events")]
public record UnitDlqChannelMessage : IPublish;

public record UnannotatedMessage;

public class DeadLetterOperatorApiUnitTests
{
    private readonly ICatalog _catalog = Substitute.For<ICatalog>();
    private readonly IHighwayEngine _engine = Substitute.For<IHighwayEngine>();
    private readonly IHighwayEngineInternals _engineInternals = Substitute.For<IHighwayEngineInternals>();
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly HighwayOptions _options = new() { Server = "127.0.0.1:6379", SubscriptionGroup = "default-group" };

    public DeadLetterOperatorApiUnitTests()
    {
        _engine.State.Returns(EngineState.Running);
        _engineInternals.Connection.Returns(_connection);
    }

    [Fact]
    public async Task PeekAsync_ResolvesQueueTarget_Correctly()
    {
        _connection.DlqPeekAsync("Q", "orders.queue", 20, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RawDeadLetterPeekEntry>>(Array.Empty<RawDeadLetterPeekEntry>()));

        var api = new HighwayDeadLetters(_catalog, _options, _engine, _engineInternals);

        var result = await api.PeekAsync<UnitDlqQueueMessage>();

        result.Should().BeEmpty();
        await _connection.Received(1).DlqPeekAsync("Q", "orders.queue", 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AServiceContract_HasNoDeadLetters_AndSaysSo()
    {
        // 074 (D2): RPC has no dead letters; the operator API refuses a service contract instead of asking the broker.
        var api = new HighwayDeadLetters(_catalog, _options, _engine, _engineInternals);

        var act = () => api.PeekAsync<UnitDlqServiceRequest>(10);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*orders.service*RPC has no dead letters*");
        await _connection.DidNotReceiveWithAnyArgs().DlqPeekAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task PeekAsync_ResolvesChannelTarget_WithDefaultAndCustomGroup()
    {
        _connection.DlqPeekAsync("Q", "orders.events@default-group", 20, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RawDeadLetterPeekEntry>>(Array.Empty<RawDeadLetterPeekEntry>()));
        _connection.DlqPeekAsync("Q", "orders.events@custom-group", 5, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RawDeadLetterPeekEntry>>(Array.Empty<RawDeadLetterPeekEntry>()));

        var api = new HighwayDeadLetters(_catalog, _options, _engine, _engineInternals);

        // Default group from options
        await api.PeekAsync<UnitDlqChannelMessage>();
        await _connection.Received(1).DlqPeekAsync("Q", "orders.events@default-group", 20, Arg.Any<CancellationToken>());

        // Custom group override
        await api.PeekAsync<UnitDlqChannelMessage>(5, group: "custom-group");
        await _connection.Received(1).DlqPeekAsync("Q", "orders.events@custom-group", 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PeekAsync_ThrowsOnUnannotatedType()
    {
        var api = new HighwayDeadLetters(_catalog, _options, _engine, _engineInternals);

        var act = () => api.PeekAsync<UnannotatedMessage>();

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not a recognized Highway contract*");
    }

    [Fact]
    public async Task PeekAsync_ThrowsWhenEngineNotRunning()
    {
        _engine.State.Returns(EngineState.Stopped);
        var api = new HighwayDeadLetters(_catalog, _options, _engine, _engineInternals);

        var act = () => api.PeekAsync<UnitDlqQueueMessage>();

        await act.Should().ThrowAsync<HighwayTransportException>()
            .WithMessage("*engine is not running*");
    }

    [Fact]
    public async Task RequeueAsync_ThrowsOnNullOrEmptyId()
    {
        var api = new HighwayDeadLetters(_catalog, _options, _engine, _engineInternals);

        var act1 = () => api.RequeueAsync<UnitDlqQueueMessage>("");
        var act2 = () => api.RequeueAsync<UnitDlqQueueMessage>("   ");

        await act1.Should().ThrowAsync<ArgumentException>();
        await act2.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task InvalidCount_ThrowsArgumentOutOfRangeException()
    {
        var api = new HighwayDeadLetters(_catalog, _options, _engine, _engineInternals);

        var peekAct = () => api.PeekAsync<UnitDlqQueueMessage>(0);
        var requeueAllAct = () => api.RequeueAllAsync<UnitDlqQueueMessage>(-1);
        var purgeAllAct = () => api.PurgeAllAsync<UnitDlqQueueMessage>(0);

        await peekAct.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await requeueAllAct.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await purgeAllAct.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
