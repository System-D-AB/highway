using FluentAssertions;
using Highway.Client.Engine;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Highway.Client.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 005 Task 8 — consumer loop drain semantics and the RACK-only-after-
/// dispatch invariant that makes a crash mid-dispatch cause redelivery, not loss.
/// </summary>
public class ChannelConsumerLoopTests : IDisposable
{
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly ServiceProvider _provider;
    private readonly ServiceExecutor _executor;
    private readonly ChannelDescriptor _descriptor;

    public ChannelConsumerLoopTests()
    {
        var compiler = new ExpressionDelegateCompiler();
        _descriptor = new ChannelDescriptor
        {
            Name = "test.events",
            MessageType = typeof(TestEvent),
            Subscribers =
            [
                new SubscriberDescriptor
                {
                    ImplementationType = typeof(LoopSubscriberA),
                    Lifetime = Highway.Abstractions.HighwayServiceLifetime.Scoped,
                    InvokeDelegate = compiler.CompileSubscriberDelegate(typeof(LoopSubscriberA), typeof(TestEvent)),
                },
                new SubscriberDescriptor
                {
                    ImplementationType = typeof(LoopSubscriberB),
                    Lifetime = Highway.Abstractions.HighwayServiceLifetime.Scoped,
                    InvokeDelegate = compiler.CompileSubscriberDelegate(typeof(LoopSubscriberB), typeof(TestEvent)),
                },
            ],
        };

        var catalog = new ImmutableCatalog([], [_descriptor]);
        var services = new ServiceCollection();
        services.AddScoped<LoopSubscriberA>();
        services.AddScoped<LoopSubscriberB>();
        _provider = services.BuildServiceProvider();
        _executor = new ServiceExecutor(catalog, _provider.GetRequiredService<IServiceScopeFactory>());

        LoopSubscriberA.Reset();
        LoopSubscriberB.Reset();
    }

    public void Dispose() => _provider.Dispose();

    private ChannelConsumerLoop CreateLoop(LoopWake wake, int batchSize = 10)
        => new(_descriptor, _connection, _executor, "test-node", batchSize, wake, NullLogger.Instance);

    private static byte[] MessageEnvelope(string data)
        => HighwayJson.EncodeEnvelope("publisher", new TestEvent { Data = data });

    private static IReadOnlyList<(long, byte[])> Batch(params (long, byte[])[] items) => items;

    private async Task RunOnePassAsync(ChannelConsumerLoop loop, LoopWake wake)
    {
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(50), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(250);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Drain_DispatchesToEveryLocalSubscriber()
    {
        _connection.ReceiveAsync("test.events", "test-node", 10, Arg.Any<CancellationToken>())
            .Returns(Batch((1L, MessageEnvelope("hello"))), Batch());

        var wake = new LoopWake();
        await RunOnePassAsync(CreateLoop(wake), wake);

        LoopSubscriberA.CallCount.Should().Be(1);
        LoopSubscriberB.CallCount.Should().Be(1, "every local subscriber gets the message");
        LoopSubscriberA.LastData.Should().Be("hello");
    }

    /// <summary>
    /// The invariant: RACK happens only after dispatch completes, so a crash
    /// mid-dispatch causes redelivery rather than silent loss.
    /// </summary>
    [Fact]
    public async Task Dispatch_RacksOnlyAfterSubscribersRun()
    {
        var order = new List<string>();
        _connection.ReceiveAsync("test.events", "test-node", 10, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                lock (order) if (!order.Contains("RECEIVE")) order.Add("RECEIVE");
                return Task.FromResult(LoopSubscriberA.CallCount == 0 ? Batch((1L, MessageEnvelope("x"))) : Batch());
            });
        _connection.RackAsync("test.events", "test-node", 1L, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                lock (order) order.Add($"RACK(subscribersRan={LoopSubscriberA.CallCount})");
                return Task.CompletedTask;
            });

        var wake = new LoopWake();
        await RunOnePassAsync(CreateLoop(wake), wake);

        order.Should().Contain("RACK(subscribersRan=1)");
    }

    [Fact]
    public async Task Dispatch_PoisonMessage_IsAckedWithoutDispatch()
    {
        _connection.ReceiveAsync("test.events", "test-node", 10, Arg.Any<CancellationToken>())
            .Returns(Batch((7L, "not-json"u8.ToArray())), Batch());

        var wake = new LoopWake();
        await RunOnePassAsync(CreateLoop(wake), wake);

        LoopSubscriberA.CallCount.Should().Be(0);
        await _connection.Received(1).RackAsync("test.events", "test-node", 7L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drain_ShortBatch_EndsThePass()
    {
        _connection.ReceiveAsync("test.events", "test-node", 10, Arg.Any<CancellationToken>())
            .Returns(Batch((1L, MessageEnvelope("a"))));

        var wake = new LoopWake();
        var loop = CreateLoop(wake);
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(10_000), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(250);
        await stop.CancelAsync();
        await run;

        // A batch shorter than the batch size means the queue is drained: exactly
        // one RECEIVE per wake, not a spin.
        await _connection.Received(1).ReceiveAsync("test.events", "test-node", 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drain_FullBatch_ContinuesUntilShortBatch()
    {
        // NSubstitute repeats its last configured value, so drive the sequence
        // explicitly and return empty forever afterwards.
        var call = 0;
        _connection.ReceiveAsync("test.events", "test-node", 2, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++call switch
            {
                1 => Batch((1L, MessageEnvelope("a")), (2L, MessageEnvelope("b"))),  // full → keep going
                2 => Batch((3L, MessageEnvelope("c"))),                              // short → pass ends
                _ => Batch(),
            }));

        var wake = new LoopWake();
        await RunOnePassAsync(CreateLoop(wake, batchSize: 2), wake);

        LoopSubscriberA.CallCount.Should().Be(3, "a full batch means more may be waiting");
    }

    [Fact]
    public async Task Drain_TransientAbort_DoesNotKillTheLoop()
    {
        var calls = 0;
        _connection.ReceiveAsync("test.events", "test-node", 10, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1) throw new HighwayTransientException(HighwayConnection.TransientAbortMessage);
                return Task.FromResult((IReadOnlyList<(long, byte[])>)Batch());
            });

        var wake = new LoopWake();
        var loop = CreateLoop(wake);
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(50), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(400);
        await stop.CancelAsync();
        await run;

        calls.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Dispatch_SubscriberThrows_DoesNotBlockSiblingsOrTheAck()
    {
        var compiler = new ExpressionDelegateCompiler();
        var descriptor = new ChannelDescriptor
        {
            Name = "test.events",
            MessageType = typeof(TestEvent),
            Subscribers =
            [
                new SubscriberDescriptor
                {
                    ImplementationType = typeof(ThrowingSubscriber),
                    Lifetime = Highway.Abstractions.HighwayServiceLifetime.Scoped,
                    InvokeDelegate = compiler.CompileSubscriberDelegate(typeof(ThrowingSubscriber), typeof(TestEvent)),
                },
                new SubscriberDescriptor
                {
                    ImplementationType = typeof(LoopSubscriberA),
                    Lifetime = Highway.Abstractions.HighwayServiceLifetime.Scoped,
                    InvokeDelegate = compiler.CompileSubscriberDelegate(typeof(LoopSubscriberA), typeof(TestEvent)),
                },
            ],
        };

        var catalog = new ImmutableCatalog([], [descriptor]);
        var services = new ServiceCollection();
        services.AddScoped<ThrowingSubscriber>();
        services.AddScoped<LoopSubscriberA>();
        using var provider = services.BuildServiceProvider();
        var executor = new ServiceExecutor(catalog, provider.GetRequiredService<IServiceScopeFactory>());

        _connection.ReceiveAsync("test.events", "test-node", 10, Arg.Any<CancellationToken>())
            .Returns(Batch((1L, MessageEnvelope("x"))), Batch());

        var wake = new LoopWake();
        var loop = new ChannelConsumerLoop(descriptor, _connection, executor, "test-node", 10, wake, NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(50), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(250);
        await stop.CancelAsync();
        await run;

        LoopSubscriberA.CallCount.Should().Be(1, "a failing subscriber must not block its siblings");
        await _connection.Received(1).RackAsync("test.events", "test-node", 1L, Arg.Any<CancellationToken>());
    }
}
