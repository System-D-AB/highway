using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Highway.Server.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

public class DeadLetterWorkerLoopTests : IDisposable
{
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly ServiceProvider _provider;
    private readonly ServiceExecutor _executor;
    private readonly DeadLetterDescriptor _descriptor;

    [Queue("orders.process")]
    public sealed class OrderProcessMessage : ISend
    {
        public string? OrderId { get; set; }
    }

    public sealed class OrderDeadLetterHandler : IDeadLetter<OrderProcessMessage>
    {
        public static Func<DeadLetter<OrderProcessMessage>, DeadLetterAction>? Behavior { get; set; }

        public Task<DeadLetterAction> HandleAsync(DeadLetter<OrderProcessMessage> letter, CancellationToken ct = default)
        {
            if (Behavior is not null)
                return Task.FromResult(Behavior(letter));
            return Task.FromResult(DeadLetterAction.Keep);
        }
    }

    public DeadLetterWorkerLoopTests()
    {
        var compiler = new ExpressionDelegateCompiler();
        _descriptor = new DeadLetterDescriptor
        {
            TargetKind = DeadLetterTargetKind.Queue,
            TargetName = "orders.process",
            MessageType = typeof(OrderProcessMessage),
            HandlerType = typeof(OrderDeadLetterHandler),
            Lifetime = HighwayServiceLifetime.Scoped,
            InvokeDelegate = compiler.CompileDeadLetterDelegate(typeof(OrderDeadLetterHandler), typeof(OrderProcessMessage))
        };

        var catalog = new ImmutableCatalog(
            services: [],
            channels: [],
            deadLetters: [_descriptor]);

        var services = new ServiceCollection();
        services.AddScoped<OrderDeadLetterHandler>();
        _provider = services.BuildServiceProvider();
        _executor = new ServiceExecutor(catalog, _provider.GetRequiredService<IServiceScopeFactory>());

        OrderDeadLetterHandler.Behavior = null;
    }

    public void Dispose()
    {
        OrderDeadLetterHandler.Behavior = null;
        _provider.Dispose();
    }

    private DeadLetterWorkerLoop CreateLoop(LoopWake wake)
        => new(_descriptor, _connection, _executor, "test-node", group: null, wake, NullLogger.Instance);

    private static ClaimedWork CreateClaimedWork(string msgId, string deadLetterId, OrderProcessMessage msg)
    {
        var envelope = HighwayJson.EncodeEnvelope("node-1", msg);
        var rpcEntry = Envelope.EncodeRpcEntry(System.Text.Encoding.UTF8.GetBytes(msgId), envelope, attempts: 3);
        var deadRecord = DeadLetter.Encode(DateTime.UtcNow.Ticks, 3, "MAX_ATTEMPTS", rpcEntry);
        return new ClaimedWork("token-" + deadLetterId, deadLetterId, deadRecord);
    }

    private async Task RunOnePassAsync(DeadLetterWorkerLoop loop, LoopWake wake)
    {
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(50), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(250);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Drain_HandlerReturnsDiscard_ResolvesDiscard()
    {
        var wake = new LoopWake();
        var loop = CreateLoop(wake);
        var work = CreateClaimedWork("ord-1", "000000010000000000000001", new OrderProcessMessage { OrderId = "101" });

        OrderDeadLetterHandler.Behavior = letter =>
        {
            letter.Message.Should().NotBeNull();
            letter.Message!.OrderId.Should().Be("101");
            return DeadLetterAction.Discard;
        };

        _connection.ClaimDeadAsync(WorkKind.Queue, "orders.process", "test-node", Arg.Any<CancellationToken>())
            .Returns(work, (ClaimedWork?)null);

        _connection.ResolveDeadLetterAsync(Arg.Any<string>(), Arg.Any<DeadLetterAction>(), Arg.Any<CancellationToken>())
            .Returns(new DeadLetterResolutionResult(true, "done", "discarded"));

        await RunOnePassAsync(loop, wake);

        await _connection.Received(1).ResolveDeadLetterAsync(
            work.Token,
            Arg.Is<DeadLetterAction>(a => a.Type == DeadLetterAction.ActionType.Discard),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drain_HandlerReturnsRequeueWithAfter_ResolvesRequeueWithAfter()
    {
        var wake = new LoopWake();
        var loop = CreateLoop(wake);
        var work = CreateClaimedWork("ord-2", "000000010000000000000002", new OrderProcessMessage { OrderId = "102" });

        OrderDeadLetterHandler.Behavior = _ => DeadLetterAction.Requeue(TimeSpan.FromSeconds(15));

        _connection.ClaimDeadAsync(WorkKind.Queue, "orders.process", "test-node", Arg.Any<CancellationToken>())
            .Returns(work, (ClaimedWork?)null);

        _connection.ResolveDeadLetterAsync(Arg.Any<string>(), Arg.Any<DeadLetterAction>(), Arg.Any<CancellationToken>())
            .Returns(new DeadLetterResolutionResult(true, "done", "requeued"));

        await RunOnePassAsync(loop, wake);

        await _connection.Received(1).ResolveDeadLetterAsync(
            work.Token,
            Arg.Is<DeadLetterAction>(a => a.Type == DeadLetterAction.ActionType.Requeue && a.After == TimeSpan.FromSeconds(15)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drain_HandlerThrows_ResolvesKeepSafely()
    {
        var wake = new LoopWake();
        var loop = CreateLoop(wake);
        var work = CreateClaimedWork("ord-3", "000000010000000000000003", new OrderProcessMessage { OrderId = "103" });

        OrderDeadLetterHandler.Behavior = _ => throw new InvalidOperationException("Handler failed catastrophically!");

        _connection.ClaimDeadAsync(WorkKind.Queue, "orders.process", "test-node", Arg.Any<CancellationToken>())
            .Returns(work, (ClaimedWork?)null);

        _connection.ResolveDeadLetterAsync(Arg.Any<string>(), Arg.Any<DeadLetterAction>(), Arg.Any<CancellationToken>())
            .Returns(new DeadLetterResolutionResult(true, "done", "kept"));

        await RunOnePassAsync(loop, wake);

        // Handler threw, so worker loop must preserve evidence by resolving KEEP
        await _connection.Received(1).ResolveDeadLetterAsync(
            work.Token,
            Arg.Is<DeadLetterAction>(a => a.Type == DeadLetterAction.ActionType.Keep),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drain_CorruptPayload_ResolvesKeepSafely()
    {
        var wake = new LoopWake();
        var loop = CreateLoop(wake);
        var corruptedWork = new ClaimedWork("token-corrupt", "000000010000000000000004", new byte[] { 0, 1, 2, 3 });

        _connection.ClaimDeadAsync(WorkKind.Queue, "orders.process", "test-node", Arg.Any<CancellationToken>())
            .Returns(corruptedWork, (ClaimedWork?)null);

        _connection.ResolveDeadLetterAsync(Arg.Any<string>(), Arg.Any<DeadLetterAction>(), Arg.Any<CancellationToken>())
            .Returns(new DeadLetterResolutionResult(true, "done", "kept"));

        await RunOnePassAsync(loop, wake);

        await _connection.Received(1).ResolveDeadLetterAsync(
            corruptedWork.Token,
            Arg.Is<DeadLetterAction>(a => a.Type == DeadLetterAction.ActionType.Keep),
            Arg.Any<CancellationToken>());
    }
}
