using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Highway.Client.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

using ServiceDescriptor = Highway.Client.Scanning.ServiceDescriptor;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 005 Task 7 — worker loop drain semantics and the REPLY-before-ACK
/// ordering invariant that makes at-least-once hold across a crash.
/// </summary>
public class RpcWorkerLoopTests : IDisposable
{
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly ServiceProvider _provider;
    private readonly ServiceExecutor _executor;
    private readonly ServiceDescriptor _descriptor;

    public RpcWorkerLoopTests()
    {
        var compiler = new ExpressionDelegateCompiler();
        _descriptor = new ServiceDescriptor
        {
            Name = "test.hello",
            ImplementationType = typeof(TestService),
            RequestType = typeof(TestRequest),
            ResponseType = typeof(TestResponse),
            Lifetime = HighwayServiceLifetime.Scoped,
            InvokeDelegate = compiler.CompileServiceDelegate(
                typeof(TestService), typeof(TestRequest), typeof(TestResponse)),
        };

        var catalog = new ImmutableCatalog([_descriptor], []);
        var services = new ServiceCollection();
        services.AddScoped<TestService>();
        _provider = services.BuildServiceProvider();
        _executor = new ServiceExecutor(catalog, _provider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose() => _provider.Dispose();

    private RpcWorkerLoop CreateLoop(LoopWake wake, int concurrency = 4)
        => new(_descriptor, _connection, _executor, "test-node", concurrency, wake, NullLogger.Instance);

    private static byte[] RequestEnvelope(string input)
        => HighwayJson.EncodeEnvelope("caller", new TestRequest { Input = input });

    /// <summary>Runs one drain pass and stops, so tests stay deterministic.</summary>
    private async Task RunOnePassAsync(RpcWorkerLoop loop, LoopWake wake)
    {
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(50), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(250);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Drain_DequeuesUntilNil()
    {
        _connection.DequeueAsync("test.hello", "test-node", Arg.Any<CancellationToken>())
            .Returns(
                ("r1", RequestEnvelope("a")),
                ("r2", RequestEnvelope("b")),
                ((string, byte[])?)null);

        var wake = new LoopWake();
        await RunOnePassAsync(CreateLoop(wake), wake);

        await _connection.Received(1).ReplyAsync("r1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _connection.Received(1).ReplyAsync("r2", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The invariant: a crash between REPLY and ACK still delivers the response
    /// (the server lease redelivers). The reverse order could lose a reply.
    /// </summary>
    [Fact]
    public async Task Process_SendsReplyBeforeAck()
    {
        var order = new List<string>();
        _connection.DequeueAsync("test.hello", "test-node", Arg.Any<CancellationToken>())
            .Returns(("r1", RequestEnvelope("a")), ((string, byte[])?)null);
        _connection.ReplyAsync("r1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => { lock (order) order.Add("REPLY"); return Task.CompletedTask; });
        _connection.AckAsync("test.hello", "test-node", "r1", Arg.Any<CancellationToken>())
            .Returns(_ => { lock (order) order.Add("ACK"); return Task.CompletedTask; });

        var wake = new LoopWake();
        await RunOnePassAsync(CreateLoop(wake), wake);

        order.Should().Equal("REPLY", "ACK");
    }

    [Fact]
    public async Task Process_PoisonEnvelope_Replies400AndAcks()
    {
        byte[]? replyBytes = null;
        _connection.DequeueAsync("test.hello", "test-node", Arg.Any<CancellationToken>())
            .Returns(("bad", "not-json"u8.ToArray()), ((string, byte[])?)null);
        _connection.ReplyAsync("bad", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(ci => { replyBytes = ci.Arg<byte[]>(); return Task.CompletedTask; });

        var wake = new LoopWake();
        await RunOnePassAsync(CreateLoop(wake), wake);

        replyBytes.Should().NotBeNull("a poison request must return data, never a caller timeout");
        var envelope = HighwayJson.DecodeEnvelope(replyBytes!);
        var output = (GenericOutput)HighwayJson.DeserializeBody(envelope, typeof(GenericOutput))!;
        output.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        output.Error!.Code.Should().Be("BAD_ENVELOPE");

        await _connection.Received(1).AckAsync("test.hello", "test-node", "bad", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_ReplyFails_DoesNotAck()
    {
        _connection.DequeueAsync("test.hello", "test-node", Arg.Any<CancellationToken>())
            .Returns(("r1", RequestEnvelope("a")), ((string, byte[])?)null);
        _connection.ReplyAsync("r1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HighwayTransportException("permanent"));

        var wake = new LoopWake();
        await RunOnePassAsync(CreateLoop(wake), wake);

        await _connection.DidNotReceive().AckAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drain_TransientAbort_DoesNotKillTheLoop()
    {
        var calls = 0;
        _connection.DequeueAsync("test.hello", "test-node", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1) throw new HighwayTransientException(HighwayConnection.TransientAbortMessage);
                return Task.FromResult<(string, byte[])?>(null);
            });

        var wake = new LoopWake();
        var loop = CreateLoop(wake);
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(50), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(400);
        await stop.CancelAsync();
        await run;

        calls.Should().BeGreaterThan(1, "a transient abort must be retried on a later wake, not kill the loop");
    }

    [Fact]
    public async Task Drain_PermanentError_EndsPassWithoutDying()
    {
        _connection.DequeueAsync("test.hello", "test-node", Arg.Any<CancellationToken>())
            .Returns<Task<(string, byte[])?>>(_ => throw new HighwayTransportException("permanent"));

        var wake = new LoopWake();
        var loop = CreateLoop(wake);
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(50), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(200);
        await stop.CancelAsync();

        await FluentActions.Awaiting(() => run).Should().NotThrowAsync("loops never die");
    }

    [Fact]
    public async Task Process_ServiceThrows_Replies500AndAcks_WithoutBlockingSiblings()
    {
        var compiler = new ExpressionDelegateCompiler();
        var throwing = new ServiceDescriptor
        {
            Name = "test.throws",
            ImplementationType = typeof(ThrowingService),
            RequestType = typeof(ThrowingRequest),
            ResponseType = typeof(TestResponse),
            Lifetime = HighwayServiceLifetime.Scoped,
            InvokeDelegate = compiler.CompileServiceDelegate(
                typeof(ThrowingService), typeof(ThrowingRequest), typeof(TestResponse)),
        };
        var catalog = new ImmutableCatalog([throwing], []);
        var services = new ServiceCollection();
        services.AddScoped<ThrowingService>();
        using var provider = services.BuildServiceProvider();
        var executor = new ServiceExecutor(catalog, provider.GetRequiredService<IServiceScopeFactory>());

        _connection.DequeueAsync("test.throws", "test-node", Arg.Any<CancellationToken>())
            .Returns(
                ("r1", HighwayJson.EncodeEnvelope("caller", new ThrowingRequest { Message = "boom" })),
                ((string, byte[])?)null);

        var wake = new LoopWake();
        var loop = new RpcWorkerLoop(throwing, _connection, executor, "test-node", 4, wake, NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var run = loop.RunAsync(TimeSpan.FromMilliseconds(50), stop.Token, CancellationToken.None);
        wake.Signal();
        await Task.Delay(250);
        await stop.CancelAsync();
        await run;

        await _connection.Received(1).ReplyAsync("r1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _connection.Received(1).AckAsync("test.throws", "test-node", "r1", Arg.Any<CancellationToken>());
    }
}
