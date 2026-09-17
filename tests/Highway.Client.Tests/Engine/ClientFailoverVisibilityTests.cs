using FluentAssertions;
using Highway.Client;
using Highway.Client.Engine;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 050 T5 (R7.4) / T6 (F5) — client-side visibility of a leadership change. The client raises
/// a <c>MasterChanged</c> event (and logs) when it adopts a new master, and warns once at startup when
/// it is pointed at a replica set through a single-endpoint connection string (the no-failover footgun).
/// </summary>
public class ClientFailoverVisibilityTests
{
    private sealed class CapturingLoggerFactory(List<string> sink) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
                => sink.Add(formatter(state, ex));
        }
    }

    [Fact]
    public void SwitchTo_ANewMaster_RaisesMasterChanged_AndLogs()
    {
        var logs = new List<string>();
        var source = new HighwayConnectionSource(
            new HighwayOptions { Server = "10.0.0.1:6500,10.0.0.2:6500" },
            new CapturingLoggerFactory(logs));
        try
        {
            HighwayConnectionSource.MasterChange? seen = null;
            source.MasterChanged += mc => seen = mc;

            source.SwitchTo("10.0.0.2:6500");   // the initial active endpoint is the first host

            seen.Should().NotBeNull("adopting a different master raises the event");
            seen!.Value.Endpoint.Should().Be("10.0.0.2:6500");
            logs.Should().Contain(l => l.Contains("master changed to 10.0.0.2:6500"));
        }
        finally { source.Dispose(); }
    }

    [Fact]
    public void SwitchTo_TheSameMaster_DoesNotRaiseMasterChanged()
    {
        var source = new HighwayConnectionSource(new HighwayOptions { Server = "10.0.0.1:6500,10.0.0.2:6500" });
        try
        {
            var fired = 0;
            source.MasterChanged += _ => fired++;

            source.SwitchTo("10.0.0.1:6500");   // already the active endpoint

            fired.Should().Be(0, "no change of master means no event");
        }
        finally { source.Dispose(); }
    }

    [Fact]
    public void SingleEndpointConnectionString_AgainstAReplicaSet_WarnsOnce()
    {
        var logs = new List<string>();
        var source = new HighwayConnectionSource(
            new HighwayOptions { Server = "10.0.0.1:6500" },   // ONE endpoint, no failover target
            new CapturingLoggerFactory(logs));

        source.SetRosterForTests(
            [
                new HighwayConnectionSource.RosterEntry("node-a", 50, "10.0.0.1:6500"),
                new HighwayConnectionSource.RosterEntry("node-b", 100, "10.0.0.2:6500"),
            ],
            version: 1);

        source.MaybeWarnSingleEndpoint(2);
        source.MaybeWarnSingleEndpoint(2);   // a second learn must not warn again

        logs.Should().ContainSingle(l => l.Contains("NO failover"))
            .Which.Should().Contain("10.0.0.2:6500", "the warning names the endpoint the client is missing");
    }

    [Fact]
    public void MultiEndpointConnectionString_DoesNotWarn()
    {
        var logs = new List<string>();
        var source = new HighwayConnectionSource(
            new HighwayOptions { Server = "10.0.0.1:6500,10.0.0.2:6500" },
            new CapturingLoggerFactory(logs));

        source.MaybeWarnSingleEndpoint(2);

        logs.Should().NotContain(l => l.Contains("NO failover"));
    }
}
