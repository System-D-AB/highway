using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Highway.Server.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// 074 Part B, ported to 3.0: a handler is cancelled at MaxProcessingTime; one that stops is a failure (queue,
/// subscriber), a 504 (RPC) or a KEEP (dead letter); one that ignores the cancellation is abandoned after
/// AbandonGrace — slot freed, lease no longer renewed — and the loop keeps working, bounded.
/// </summary>
public class HungHandlerTests : IDisposable
{
    public sealed class TestMessage : ISend { public string Content { get; set; } = ""; }
    public sealed class TestEvent : IPublish { public string EventName { get; set; } = ""; }
    public sealed class TestRequest : IReturn<TestResponse> { public string Text { get; set; } = ""; }
    public sealed class TestResponse : Output { public string Echo { get; set; } = ""; }

    public sealed class HangableProcessor : IProcess<TestMessage>
    {
        public static Func<TestMessage, CancellationToken, Task>? Handler { get; set; }
        public Task ProcessAsync(TestMessage message, CancellationToken ct = default)
            => Handler is not null ? Handler(message, ct) : Task.CompletedTask;
    }

    public sealed class HangableSubscriber : ISubscribe<TestEvent>
    {
        public static Func<TestEvent, CancellationToken, Task>? Handler { get; set; }
        public Task SubscribeAsync(TestEvent message, CancellationToken ct = default)
            => Handler is not null ? Handler(message, ct) : Task.CompletedTask;
    }

    public sealed class HangableService : AsyncService<TestRequest, TestResponse>
    {
        public static Func<TestRequest, CancellationToken, Task<TestResponse>>? Handler { get; set; }
        public override Task<TestResponse> ExecuteAsync(TestRequest request, CancellationToken ct = default)
            => Handler is not null ? Handler(request, ct) : Task.FromResult(new TestResponse { Echo = request.Text });
    }

    [Idempotent(WindowSeconds = 3600)]
    public sealed class IdemMessage : ISend { public string Content { get; set; } = ""; }

    [Idempotent(WindowSeconds = 3600)]
    public sealed class IdemRequest : IReturn<TestResponse> { public string Text { get; set; } = ""; }

    public sealed class HangableIdemProcessor : IProcess<IdemMessage>
    {
        public static Func<CancellationToken, Task>? Handler { get; set; }
        public Task ProcessAsync(IdemMessage message, CancellationToken ct = default)
            => Handler is not null ? Handler(ct) : Task.CompletedTask;
    }

    public sealed class HangableIdemService : AsyncService<IdemRequest, TestResponse>
    {
        public static Func<CancellationToken, Task<TestResponse>>? Handler { get; set; }
        public override Task<TestResponse> ExecuteAsync(IdemRequest request, CancellationToken ct = default)
            => Handler is not null ? Handler(ct) : Task.FromResult(new TestResponse { Echo = request.Text });
    }

    public sealed class HangableDeadLetterHandler : IDeadLetter<TestMessage>
    {
        public static Func<CancellationToken, Task<DeadLetterAction>>? Handler { get; set; }
        public Task<DeadLetterAction> HandleAsync(DeadLetter<TestMessage> letter, CancellationToken ct = default)
            => Handler is not null ? Handler(ct) : Task.FromResult(DeadLetterAction.Discard);
    }

    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly ServiceProvider _provider;
    private readonly ServiceExecutor _executor;
    private readonly QueueDescriptor _queue;
    private readonly ChannelDescriptor _channel;
    private readonly Highway.Client.Scanning.ServiceDescriptor _service;
    private readonly DeadLetterDescriptor _deadLetter;
    private readonly QueueDescriptor _idemQueue;
    private readonly Highway.Client.Scanning.ServiceDescriptor _idemService;

    public HungHandlerTests()
    {
        HangableProcessor.Handler = null;
        HangableSubscriber.Handler = null;
        HangableService.Handler = null;
        HangableDeadLetterHandler.Handler = null;
        HangableIdemProcessor.Handler = null;
        HangableIdemService.Handler = null;

        var services = new ServiceCollection();
        services.AddScoped<HangableProcessor>();
        services.AddScoped<HangableSubscriber>();
        services.AddScoped<HangableService>();
        services.AddScoped<HangableDeadLetterHandler>();
        services.AddScoped<HangableIdemProcessor>();
        services.AddScoped<HangableIdemService>();
        _provider = services.BuildServiceProvider();

        _queue = new QueueDescriptor
        {
            Name = "test.queue",
            MessageType = typeof(TestMessage),
            ProcessorType = typeof(HangableProcessor),
            Lifetime = HighwayServiceLifetime.Scoped,
            InvokeDelegate = (p, m, ct) => ((HangableProcessor)p).ProcessAsync((TestMessage)m, ct),
        };
        _channel = new ChannelDescriptor
        {
            Name = "test.channel",
            MessageType = typeof(TestEvent),
            Subscribers =
            [
                new SubscriberDescriptor
                {
                    ImplementationType = typeof(HangableSubscriber),
                    Lifetime = HighwayServiceLifetime.Scoped,
                    InvokeDelegate = (s, m, ct) => ((HangableSubscriber)s).SubscribeAsync((TestEvent)m, ct),
                },
            ],
        };
        _service = new Highway.Client.Scanning.ServiceDescriptor
        {
            Name = "test.service",
            RequestType = typeof(TestRequest),
            ResponseType = typeof(TestResponse),
            ImplementationType = typeof(HangableService),
            Lifetime = HighwayServiceLifetime.Scoped,
            InvokeDelegate = async (s, r, ct) => await ((HangableService)s).ExecuteAsync((TestRequest)r, ct),
        };
        _deadLetter = new DeadLetterDescriptor
        {
            TargetKind = DeadLetterTargetKind.Queue,
            TargetName = "test.queue",
            MessageType = typeof(TestMessage),
            HandlerType = typeof(HangableDeadLetterHandler),
            Lifetime = HighwayServiceLifetime.Scoped,
            InvokeDelegate = new ExpressionDelegateCompiler().CompileDeadLetterDelegate(typeof(HangableDeadLetterHandler), typeof(TestMessage)),
        };

        _idemQueue = new QueueDescriptor
        {
            Name = "idem.queue",
            MessageType = typeof(IdemMessage),
            ProcessorType = typeof(HangableIdemProcessor),
            Lifetime = HighwayServiceLifetime.Scoped,
            InvokeDelegate = (p, m, ct) => ((HangableIdemProcessor)p).ProcessAsync((IdemMessage)m, ct),
        };
        _idemService = new Highway.Client.Scanning.ServiceDescriptor
        {
            Name = "idem.service",
            RequestType = typeof(IdemRequest),
            ResponseType = typeof(TestResponse),
            ImplementationType = typeof(HangableIdemService),
            Lifetime = HighwayServiceLifetime.Scoped,
            InvokeDelegate = async (s, r, ct) => await ((HangableIdemService)s).ExecuteAsync((IdemRequest)r, ct),
        };

        var catalog = new ImmutableCatalog([_service, _idemService], [_channel], queues: [_queue, _idemQueue], deadLetters: [_deadLetter]);
        _executor = new ServiceExecutor(catalog, _provider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        _provider.Dispose();
        HangableProcessor.Handler = null;
        HangableSubscriber.Handler = null;
        HangableService.Handler = null;
        HangableDeadLetterHandler.Handler = null;
        HangableIdemProcessor.Handler = null;
        HangableIdemService.Handler = null;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static ClaimedWork Msg(string id, string content)
        => new("tok-" + id, id, HighwayJson.EncodeEnvelope("sender", new TestMessage { Content = content }));

    private static ClaimedWork Evt(string id)
        => new("tok-" + id, id, HighwayJson.EncodeEnvelope("sender", new TestEvent { EventName = id }));

    private static ClaimedWork Req(string id, string text)
        => new("tok-" + id, id, HighwayJson.EncodeEnvelope("caller", new TestRequest { Text = text }));

    private static ClaimedWork? None => null;

    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private int Count(string method) => _connection.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);

    private bool Acked(string id) => _connection.ReceivedCalls().Any(c =>
        c.GetMethodInfo().Name == nameof(IHighwayConnection.AckAsync) && (string)c.GetArguments()[0]! == "tok-" + id);

    private QueueWorkerLoop QueueLoop(LoopWake wake, ILogger? logger = null, int concurrency = 1, TimeSpan? renewal = null,
        TimeSpan? cap = null, TimeSpan? grace = null, int maxAbandoned = 0)
        => new(_queue, _connection, _executor, "test-node", concurrency, wake, logger ?? NullLogger.Instance,
            renewal ?? TimeSpan.FromMilliseconds(20), cap ?? TimeSpan.FromMilliseconds(60), grace ?? TimeSpan.FromMilliseconds(100), maxAbandoned);

    private void Claims(params ClaimedWork?[] items)
    {
        var first = items[0];
        var rest = items.Skip(1).ToArray();
        _connection.ClaimAsync(WorkKind.Queue, "test.queue", "test-node", Arg.Any<CancellationToken>()).Returns(first, rest);
    }

    private sealed class CapturingLogger : ILogger
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
            => Entries.Enqueue((level, f(state, ex)));
    }

    // ---- honouring cancellation --------------------------------------------------------------------

    [Fact]
    public async Task QueueHandler_StoppedAtTheCap_IsReportedFailed_NotAcked_AndFreesItsSlot()
    {
        HangableProcessor.Handler = async (m, ct) => { if (m.Content == "hang") await Task.Delay(TimeSpan.FromSeconds(30), ct); };
        Claims(Msg("m-1", "hang"), Msg("m-2", "ok"), None);
        var wake = new LoopWake();
        var loop = QueueLoop(wake, grace: TimeSpan.FromSeconds(5));

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        (await Eventually(() => Acked("m-2"), TimeSpan.FromSeconds(5))).Should().BeTrue("the freed slot processes m-2");
        await stop.CancelAsync();
        await run;

        await _connection.Received(1).FailAsync("tok-m-1", "ProcessingTimeExceeded", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        Acked("m-1").Should().BeFalse("a handler stopped at the cap failed; its message is redelivered");
    }

    [Fact]
    public async Task SubscriptionHandler_StoppedAtTheCap_IsReportedFailed()
    {
        HangableSubscriber.Handler = async (e, ct) => await Task.Delay(TimeSpan.FromSeconds(30), ct);
        _connection.ClaimAsync(WorkKind.Queue, "test.channel@grp", "grp", Arg.Any<CancellationToken>()).Returns(Evt("e-1"), None);
        var wake = new LoopWake();
        var loop = new SubscriptionWorkerLoop(_channel, _connection, _executor, "grp", 1, wake, NullLogger.Instance,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(5));

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        (await Eventually(() => Count(nameof(IHighwayConnection.FailAsync)) >= 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
        await stop.CancelAsync();
        await run;

        await _connection.Received(1).FailAsync("tok-e-1", "ProcessingTimeExceeded", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        Acked("e-1").Should().BeFalse();
    }

    [Fact]
    public async Task RpcHandler_StoppedAtTheCap_Replies504_AndSettles()
    {
        HangableService.Handler = async (r, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new TestResponse(); };
        byte[]? reply = null;
        _connection.ClaimAsync(WorkKind.Service, "test.service", "test-node", Arg.Any<CancellationToken>()).Returns(Req("r-1", "x"), None);
        _connection.ReplyAsync("r-1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(ci => { reply = ci.Arg<byte[]>(); return Task.CompletedTask; });
        var wake = new LoopWake();
        var loop = new RpcWorkerLoop(_service, _connection, _executor, "test-node", 1, wake, NullLogger.Instance,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(5));

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        (await Eventually(() => Acked("r-1"), TimeSpan.FromSeconds(5))).Should().BeTrue();
        await stop.CancelAsync();
        await run;

        var output = (GenericOutput)HighwayJson.DeserializeBody(HighwayJson.DecodeEnvelope(reply!), typeof(GenericOutput))!;
        output.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        output.Error!.Code.Should().Be("PROCESSING_TIME_EXCEEDED");
    }

    [Fact]
    public async Task RpcHandler_ReturningDuringTheGrace_GetsItsRealResult()
    {
        HangableService.Handler = async (r, ct) =>
        {
            await Task.Delay(200, CancellationToken.None);   // past the 60 ms cap, ignoring ct
            return new TestResponse { Echo = "real:" + r.Text, StatusCode = StatusCodes.Status200OK };
        };
        byte[]? reply = null;
        _connection.ClaimAsync(WorkKind.Service, "test.service", "test-node", Arg.Any<CancellationToken>()).Returns(Req("r-1", "x"), None);
        _connection.ReplyAsync("r-1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(ci => { reply = ci.Arg<byte[]>(); return Task.CompletedTask; });
        var wake = new LoopWake();
        var loop = new RpcWorkerLoop(_service, _connection, _executor, "test-node", 1, wake, NullLogger.Instance,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(5));

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        (await Eventually(() => reply is not null, TimeSpan.FromSeconds(5))).Should().BeTrue();
        await stop.CancelAsync();
        await run;

        var response = (TestResponse)HighwayJson.DeserializeBody(HighwayJson.DecodeEnvelope(reply!), typeof(TestResponse))!;
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Echo.Should().Be("real:x");
    }

    [Fact]
    public async Task DeadLetterHandler_StoppedAtTheCap_KeepsTheLetter()
    {
        HangableDeadLetterHandler.Handler = async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return DeadLetterAction.Discard; };
        var envelope = HighwayJson.EncodeEnvelope("node-1", new TestMessage { Content = "x" });
        var record = DeadLetter.Encode(DateTime.UtcNow.Ticks, 3, "MAX_ATTEMPTS",
            Envelope.EncodeRpcEntry(System.Text.Encoding.UTF8.GetBytes("m-1"), envelope, attempts: 3));
        var work = new ClaimedWork("tok-d-1", "000000010000000000000001", record);
        _connection.ClaimDeadAsync(WorkKind.Queue, "test.queue", "test-node", Arg.Any<CancellationToken>()).Returns(work, None);
        _connection.ResolveDeadLetterAsync(Arg.Any<string>(), Arg.Any<DeadLetterAction>(), Arg.Any<CancellationToken>())
            .Returns(new DeadLetterResolutionResult(true, "done", ""));
        var wake = new LoopWake();
        var loop = new DeadLetterWorkerLoop(_deadLetter, _connection, _executor, "test-node", null, wake, NullLogger.Instance,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(5));

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        (await Eventually(() => Count(nameof(IHighwayConnection.ResolveDeadLetterAsync)) >= 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
        await stop.CancelAsync();
        await run;

        await _connection.Received(1).ResolveDeadLetterAsync("tok-d-1", DeadLetterAction.Keep, Arg.Any<CancellationToken>());
    }

    // ---- ignoring cancellation: abandonment -----------------------------------------------------------

    [Fact]
    public async Task AHandlerIgnoringTheCap_IsAbandoned_SlotFreed_RenewalStopped_LateCompletionNotAcked()
    {
        var hold = new TaskCompletionSource();
        HangableProcessor.Handler = async (m, ct) => { if (m.Content == "hang") await hold.Task; };
        Claims(Msg("m-hung", "hang"), Msg("m-next", "ok"), None);
        var logger = new CapturingLogger();
        var wake = new LoopWake();
        var loop = QueueLoop(wake, logger, maxAbandoned: 2);

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        try
        {
            (await Eventually(() => Acked("m-next"), TimeSpan.FromSeconds(5))).Should().BeTrue("m-hung was abandoned and freed its slot");
            var touches = Count(nameof(IHighwayConnection.TouchAsync));
            await Task.Delay(150);
            Count(nameof(IHighwayConnection.TouchAsync)).Should().Be(touches, "an abandoned message is no longer renewed");
            logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Text.Contains("abandoned"));

            hold.TrySetResult();
            (await Eventually(() => loop.AbandonedCount == 0, TimeSpan.FromSeconds(5))).Should().BeTrue();
            Acked("m-hung").Should().BeFalse("an abandoned message is never acknowledged when its handler finally returns");
        }
        finally
        {
            hold.TrySetResult();
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ASynchronouslyBlockingHandler_IsAbandoned_AndItsSlotReused()
    {
        using var release = new ManualResetEventSlim(false);
        HangableProcessor.Handler = (m, ct) => { if (m.Content == "block") release.Wait(); return Task.CompletedTask; };
        Claims(Msg("m-block", "block"), Msg("m-next", "ok"), None);
        var wake = new LoopWake();
        var loop = QueueLoop(wake, maxAbandoned: 2);

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        try
        {
            (await Eventually(() => loop.AbandonedCount == 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await Eventually(() => Acked("m-next"), TimeSpan.FromSeconds(5))).Should().BeTrue();
            var touches = Count(nameof(IHighwayConnection.TouchAsync));
            await Task.Delay(150);
            Count(nameof(IHighwayConnection.TouchAsync)).Should().Be(touches);
        }
        finally
        {
            release.Set();
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ARedeliveryOfAnAbandonedMessage_IsAcknowledged()
    {
        var hold = new TaskCompletionSource();
        HangableProcessor.Handler = async (m, ct) => { if (m.Content == "hang") await hold.Task; };
        var first = true;
        var redeliver = false;
        _connection.ClaimAsync(WorkKind.Queue, "test.queue", "test-node", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (first) { first = false; return Task.FromResult<ClaimedWork?>(Msg("m-1", "hang")); }
            if (redeliver) { redeliver = false; return Task.FromResult<ClaimedWork?>(new ClaimedWork("tok-m-1b", "m-1", Msg("m-1", "ok").Payload)); }
            return Task.FromResult<ClaimedWork?>(null);
        });
        var wake = new LoopWake();
        var loop = QueueLoop(wake, concurrency: 2, maxAbandoned: 2);

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        try
        {
            (await Eventually(() => loop.AbandonedCount == 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
            redeliver = true;   // lease recovery hands m-1 out again while the first copy still hangs
            wake.Signal();
            (await Eventually(() => _connection.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IHighwayConnection.AckAsync)
                && (string)c.GetArguments()[0]! == "tok-m-1b"), TimeSpan.FromSeconds(5))).Should().BeTrue("the healthy redelivery is acknowledged");
        }
        finally
        {
            hold.TrySetResult();
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AnAbandonedIdempotentHandler_ThatFinishesLate_RecordsCompletion_ButDoesNotAck()
    {
        // Without the "done" marker its redelivery would find the in-progress marker, never run nor ack, and be
        // dead-lettered although the work succeeded.
        var hold = new TaskCompletionSource();
        HangableIdemProcessor.Handler = async ct => await hold.Task;
        var work = new ClaimedWork("tok-i-1", "i-1", HighwayJson.EncodeEnvelope("sender", new IdemMessage { Content = "x" }));
        _connection.ClaimAsync(WorkKind.Queue, "idem.queue", "test-node", Arg.Any<CancellationToken>()).Returns(work, None);
        var wake = new LoopWake();
        var loop = new QueueWorkerLoop(_idemQueue, _connection, _executor, "test-node", 1, wake, NullLogger.Instance,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(100), 2);

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        try
        {
            (await Eventually(() => loop.AbandonedCount == 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
            hold.TrySetResult();
            (await Eventually(() => loop.AbandonedCount == 0, TimeSpan.FromSeconds(5))).Should().BeTrue();

            await _connection.Received(1).CompleteIdempotencyAsync("idem.queue", "i-1", Arg.Any<byte[]>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            Acked("i-1").Should().BeFalse("the abandoned copy's claim is no longer its own; the redelivery acks");
        }
        finally
        {
            hold.TrySetResult();
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AnAbandonedIdempotentService_ThatAnswersLate_RecordsItsResponse_ButDoesNotReply()
    {
        var hold = new TaskCompletionSource();
        HangableIdemService.Handler = async ct => { await hold.Task; return new TestResponse { Echo = "late", StatusCode = StatusCodes.Status200OK }; };
        var work = new ClaimedWork("tok-ir-1", "ir-1", HighwayJson.EncodeEnvelope("caller", new IdemRequest { Text = "x" }));
        _connection.ClaimAsync(WorkKind.Service, "idem.service", "test-node", Arg.Any<CancellationToken>()).Returns(work, None);
        byte[]? stored = null;
        _connection.CompleteIdempotencyAsync("idem.service", "ir-1", Arg.Any<byte[]>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => { stored = ci.ArgAt<byte[]>(2); return Task.CompletedTask; });
        var wake = new LoopWake();
        var loop = new RpcWorkerLoop(_idemService, _connection, _executor, "test-node", 1, wake, NullLogger.Instance,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(100), 2);

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        try
        {
            (await Eventually(() => loop.AbandonedCount == 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
            hold.TrySetResult();
            (await Eventually(() => loop.AbandonedCount == 0, TimeSpan.FromSeconds(5))).Should().BeTrue();

            stored.Should().NotBeNull("the redelivery replies with this answer instead of waiting out an in-progress marker");
            ((TestResponse)HighwayJson.DeserializeBody(HighwayJson.DecodeEnvelope(stored!), typeof(TestResponse))!).Echo.Should().Be("late");
            Count(nameof(IHighwayConnection.ReplyAsync)).Should().Be(0);
            Acked("ir-1").Should().BeFalse();
        }
        finally
        {
            hold.TrySetResult();
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AtTheBound_TheLoopStopsClaiming_AndResumesAsSoonAsAnAbandonedHandlerEnds()
    {
        var hold = new TaskCompletionSource();
        HangableProcessor.Handler = async (m, ct) => { if (m.Content == "hang") await hold.Task; };
        Claims(Msg("m-hang", "hang"), Msg("m-next", "ok"), None);
        var wake = new LoopWake();
        var loop = QueueLoop(wake, cap: TimeSpan.FromMilliseconds(40), grace: TimeSpan.FromMilliseconds(60), maxAbandoned: 1);

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromSeconds(30), stop.Token, CancellationToken.None);   // only the wake can resume it
        wake.Signal();
        try
        {
            (await Eventually(() => loop.AbandonedCount == 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
            await Task.Delay(150);
            Count(nameof(IHighwayConnection.ClaimAsync)).Should().Be(1, "at the bound the loop claims nothing more");

            hold.TrySetResult();
            (await Eventually(() => Acked("m-next"), TimeSpan.FromSeconds(3))).Should().BeTrue("the loop resumes when the abandoned handler ends");
        }
        finally
        {
            hold.TrySetResult();
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Abandonment_IsCountedOnTheClientMeter()
    {
        long abandoned = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == Highway.Client.Observability.HighwayClientMetrics.MeterName && inst.Name == "highway.handlers.abandoned")
                    l.EnableMeasurementEvents(inst);
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "kind" && Equals(t.Value, "queue")) Interlocked.Add(ref abandoned, value);
        });
        listener.Start();

        var hold = new TaskCompletionSource();
        HangableProcessor.Handler = async (m, ct) => await hold.Task;
        Claims(Msg("m-1", "hang"), None);
        var wake = new LoopWake();
        var loop = QueueLoop(wake, cap: TimeSpan.FromMilliseconds(40), grace: TimeSpan.FromMilliseconds(60));

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        try
        {
            (await Eventually(() => Interlocked.Read(ref abandoned) >= 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
        finally
        {
            hold.TrySetResult();
            await stop.CancelAsync();
            await run;
        }
    }

    // ---- lease, shutdown, limits ----------------------------------------------------------------------

    [Fact]
    public async Task TheLease_IsRenewedPastTheCap_UntilTheHandlerStops()
    {
        HangableProcessor.Handler = async (m, ct) => await Task.Delay(480, CancellationToken.None);   // ignores ct, inside the grace
        Claims(Msg("m-1", "slow"), None);
        var wake = new LoopWake();
        var loop = QueueLoop(wake, renewal: TimeSpan.FromMilliseconds(25), cap: TimeSpan.FromMilliseconds(80), grace: TimeSpan.FromSeconds(2));

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        (await Eventually(() => Acked("m-1"), TimeSpan.FromSeconds(5))).Should().BeTrue("it finishes inside the grace and is acked");
        await stop.CancelAsync();
        await run;

        Count(nameof(IHighwayConnection.TouchAsync)).Should().BeGreaterThanOrEqualTo(8,
            "~480 ms at 25 ms renewals is ~19 touches; if renewal stopped at the 80 ms cap there would be at most ~3");
    }

    [Fact]
    public async Task Shutdown_CancelsHandlers_WithoutAbandoningThem_AndWaitsForThemToUnwind()
    {
        var started = new TaskCompletionSource();
        var unwound = false;
        HangableProcessor.Handler = async (m, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            finally { await Task.Delay(50, CancellationToken.None); unwound = true; }
        };
        Claims(Msg("m-1", "x"), None);
        var logger = new CapturingLogger();
        var wake = new LoopWake();
        var loop = QueueLoop(wake, logger, renewal: TimeSpan.FromMilliseconds(50), cap: TimeSpan.FromMinutes(5), grace: TimeSpan.FromSeconds(30));

        using var stop = new CancellationTokenSource();
        using var work = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, work.Token);
        wake.Signal();
        (await Task.WhenAny(started.Task, Task.Delay(5000))).Should().Be(started.Task);

        await stop.CancelAsync();
        await work.CancelAsync();
        await run;

        unwound.Should().BeTrue("the loop waits for a cancelled handler to unwind before it reports stopped");
        logger.Entries.Should().NotContain(e => e.Text.Contains("abandoned"), "shutdown is not abandonment");
        await _connection.DidNotReceiveWithAnyArgs().FailAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task AHugeMaxProcessingTime_StillProcessesMessages()
    {
        Claims(Msg("m-1", "ok"), Msg("m-2", "ok"), None);
        var wake = new LoopWake();
        var loop = QueueLoop(wake, renewal: TimeSpan.FromMinutes(1), cap: TimeSpan.FromDays(60), grace: TimeSpan.FromDays(60));

        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        (await Eventually(() => Acked("m-1") && Acked("m-2"), TimeSpan.FromSeconds(5))).Should().BeTrue(
            "with one slot, the second message proves the first released it");
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ProcessedMessages_LeaveNoTimersBehind()
    {
        const int messages = 300;
        var produced = 0;
        _connection.ClaimAsync(WorkKind.Queue, "test.queue", "test-node", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var n = Interlocked.Increment(ref produced);
            return Task.FromResult<ClaimedWork?>(n <= messages ? Msg($"m-{n}", "ok") : null);
        });
        var wake = new LoopWake();
        var loop = QueueLoop(wake, concurrency: 4, renewal: TimeSpan.FromMinutes(1), cap: TimeSpan.FromMinutes(15), grace: TimeSpan.FromSeconds(30));

        var before = Timer.ActiveCount;
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(20), stop.Token, CancellationToken.None);
        wake.Signal();
        (await Eventually(() => Count(nameof(IHighwayConnection.AckAsync)) == messages, TimeSpan.FromSeconds(10))).Should().BeTrue();
        var after = Timer.ActiveCount;
        await stop.CancelAsync();
        await run;

        (after - before).Should().BeLessThan(messages / 3, "each message's cap and abandon timers end with it");
    }

    [Fact]
    public void Options_AbandonGraceMustBePositive_AndMaxAbandonedHandlersNonNegative()
    {
        var options = new HighwayOptions { Server = "127.0.0.1:6500", AbandonGrace = TimeSpan.Zero };
        FluentActions.Invoking(() => HighwayOptionsValidator.Validate(options)).Should().Throw<InvalidOperationException>().WithMessage("*AbandonGrace*");

        options.AbandonGrace = TimeSpan.FromSeconds(30);
        options.MaxAbandonedHandlers = -1;
        FluentActions.Invoking(() => HighwayOptionsValidator.Validate(options)).Should().Throw<InvalidOperationException>().WithMessage("*MaxAbandonedHandlers*");

        options.MaxAbandonedHandlers = 0;
        FluentActions.Invoking(() => HighwayOptionsValidator.Validate(options)).Should().NotThrow();
    }
}
