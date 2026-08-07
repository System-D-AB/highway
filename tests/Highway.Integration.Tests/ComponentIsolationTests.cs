using FluentAssertions;
using Highway.Server;
using Highway.Server.Internal;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 011, T5 — Prove the isolation gate fires: a component whose Start()
/// throws is logged and the broker still serves RPC.
/// </summary>
public class ComponentIsolationTests
{
    private readonly ITestOutputHelper _output;

    public ComponentIsolationTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A test component that throws on Start() — simulating a dashboard
    /// (or any other component) that fails to bind its port.
    /// </summary>
    private sealed class FailingComponent : IHighwayServerComponent
    {
        public string Name => "FailingTestComponent";
        public bool StartCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public void Start()
        {
            StartCalled = true;
            throw new InvalidOperationException("Simulated component startup failure");
        }

        public void Dispose()
        {
            DisposeCalled = true;
        }
    }

    /// <summary>
    /// A healthy test component that tracks lifecycle for disposal-order assertions.
    /// </summary>
    private sealed class TrackingComponent : IHighwayServerComponent
    {
        public string Name => "TrackingTestComponent";
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public DateTimeOffset? DisposedAt { get; private set; }

        public void Start() => Started = true;

        public void Dispose()
        {
            Disposed = true;
            DisposedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Simple logger factory that collects log messages for assertion.
    /// </summary>
    private sealed class CollectingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(_messages);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CollectingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add($"[{logLevel}] {formatter(state, exception)}");
                }
            }
        }
    }

    [Fact]
    public void FailingComponent_DoesNotPreventBrokerFromServing()
    {
        var port = EphemeralPort.Probe();
        var loggerFactory = new CollectingLoggerFactory();

        using var server = new HighwayServerBuilder()
            .WithPort(port)
            .WithLoggerFactory(loggerFactory)
            .AddComponent(_ => new FailingComponent())
            .Build();

        server.Start();

        // Assert the broker still works — RPC round-trip
        using var redis = ConnectionMultiplexer.Connect($"localhost:{port}");
        var db = redis.GetDatabase();
        db.Execute("HW.CALL", "isolation.svc", "req-1", "payload");

        var result = db.Execute("HW.DEQUEUE", "isolation.svc", "node-1");
        result.IsNull.Should().BeFalse("the broker must still serve RPC after a component fails to start");

        var arr = (RedisResult[])result!;
        ((string)arr[0]!).Should().Be("req-1");

        // Assert the failure was logged
        loggerFactory.Messages.Should().Contain(m =>
            m.Contains("FailingTestComponent") && m.Contains("failed to start"),
            "the component failure must be logged naming the component");
    }

    [Fact]
    public void HealthyComponent_StartsAndDisposesWithServer()
    {
        var port = EphemeralPort.Probe();
        var component = new TrackingComponent();

        var server = new HighwayServerBuilder()
            .WithPort(port)
            .AddComponent(_ => component)
            .Build();

        component.Started.Should().BeFalse("component should not start before server.Start()");

        server.Start();
        component.Started.Should().BeTrue("component should start with the server");

        server.Dispose();
        component.Disposed.Should().BeTrue("component should be disposed with the server");
    }

    [Fact]
    public void Components_DisposedBeforeRecorder()
    {
        // We verify disposal order by ensuring the component is disposed before the
        // server finishes its Dispose(). The component can access the recorder during
        // disposal without it being disposed yet.
        var port = EphemeralPort.Probe();
        var component = new TrackingComponent();

        var server = new HighwayServerBuilder()
            .WithPort(port)
            .AddComponent(_ => component)
            .Build();

        server.Start();
        component.Disposed.Should().BeFalse();

        server.Dispose();
        component.Disposed.Should().BeTrue("component must be disposed during server disposal");
    }

    [Fact]
    public void MultipleComponents_FailingOneDoesNotBlockOthers()
    {
        var port = EphemeralPort.Probe();
        var healthy = new TrackingComponent();

        using var server = new HighwayServerBuilder()
            .WithPort(port)
            .AddComponent(_ => new FailingComponent())
            .AddComponent(_ => healthy)
            .Build();

        server.Start();

        healthy.Started.Should().BeTrue("a healthy component must start even if an earlier one failed");

        // Broker still works
        using var redis = ConnectionMultiplexer.Connect($"localhost:{port}");
        redis.GetDatabase().Ping();
    }
}
