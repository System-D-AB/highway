using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Observability;
using Highway.Server.Resp;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// The live view of which processes stand behind each node name — feature 048's observed address,
/// grown by 072 into a count of the processes (instances) behind a name, and a warning when it is more
/// than one.
/// </summary>
public class InstanceRegistryTests
{
    private const string A = "aaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbb";

    // ---- 048: the observed address ----------------------------------------------------------

    [Fact]
    public void Record_ThenAddressOf_ReturnsTheEndpoint()
    {
        var reg = new InstanceRegistry();
        reg.Record("conn-1", "nodeA", "192.0.2.9:6500");

        reg.AddressOf("nodeA").Should().Be("192.0.2.9:6500");
        reg.AddressOf("unknown").Should().BeNull("a node with no named connection is 'not connected'");
    }

    [Fact]
    public void Remove_ClearsTheEntry()
    {
        var reg = new InstanceRegistry();
        reg.Record("conn-1", "nodeA", "192.0.2.9:6500");

        reg.Remove("conn-1");

        reg.AddressOf("nodeA").Should().BeNull("the observed address is gone with the connection");
    }

    [Fact]
    public void EmptyInputs_AreIgnored()
    {
        var reg = new InstanceRegistry();
        reg.Record("", "nodeA", "x");
        reg.Record("c", "", "x");
        reg.Record("c", "nodeA", "");

        reg.AddressOf("nodeA").Should().BeNull();
    }

    // ---- 072: parsing --------------------------------------------------------------------------

    [Theory]
    [InlineData("shop@aaaaaaaaaaaa", "shop", A)]
    [InlineData("shop", "shop", InstanceRegistry.Unidentified)]
    [InlineData("shop@AAAAAAAAAAAA", "shop@AAAAAAAAAAAA", InstanceRegistry.Unidentified)]   // not lowercase hex
    [InlineData("shop@aaaaaaaaaaa", "shop@aaaaaaaaaaa", InstanceRegistry.Unidentified)]     // 11 characters
    [InlineData("@aaaaaaaaaaaa", "@aaaaaaaaaaaa", InstanceRegistry.Unidentified)]           // no node before it
    public void Parse_SplitsOnTheLastAt_OnlyForAnInstanceId(string clientName, string node, string instance)
        => InstanceRegistry.Parse(clientName).Should().Be((node, instance));

    [Fact]
    public void AddressOf_MatchesTheInstanceSuffixedName()
    {
        var reg = new InstanceRegistry();
        reg.Record("c1", $"nodeA@{A}", "192.0.2.9:6500");

        reg.AddressOf("nodeA").Should().Be("192.0.2.9:6500");
    }

    [Fact]
    public void ANodeNameWithASpace_MatchesItsSanitisedConnectionName()
    {
        // The client replaces whitespace with '_' (CLIENT SETNAME rejects spaces); before 072 the broker
        // compared the raw name and such a node always showed "not connected".
        var reg = new InstanceRegistry();
        reg.Record("c1", $"web shop_1@{A}".Replace(' ', '_'), "192.0.2.9:6500");

        reg.AddressOf("web shop 1").Should().Be("192.0.2.9:6500");
        reg.InstancesOf("web shop 1").Should().ContainSingle();
    }

    // ---- 072: counting -------------------------------------------------------------------------

    [Fact]
    public void SeveralConnectionsOfOneProcess_CountOnce()
    {
        var reg = new InstanceRegistry();
        reg.Record("interactive", $"nodeA@{A}", "192.0.2.9:50001");
        reg.Record("subscription", $"nodeA@{A}", "192.0.2.9:50002");
        reg.Record("purge", $"nodeA@{A}", "192.0.2.9:50003");

        reg.InstancesOf("nodeA").Should().ContainSingle().Which.Id.Should().Be(A);
    }

    [Fact]
    public void TwoProcesses_OneName_CountTwice()
    {
        var reg = new InstanceRegistry();
        reg.Record("c1", $"nodeA@{A}", "192.0.2.9:50001");
        reg.Record("c2", $"nodeA@{B}", "192.0.2.7:50001");

        reg.InstancesOf("nodeA").Select(i => (i.Id, i.SeenFrom)).Should().Equal((A, "192.0.2.9:50001"), (B, "192.0.2.7:50001"));
    }

    [Fact]
    public void ConnectionsWithoutAnInstanceId_AreOneUnidentifiedInstance()
    {
        // An older client's two SE.Redis connections must not read as two processes.
        var reg = new InstanceRegistry();
        reg.Record("c1", "nodeA", "192.0.2.9:50001");
        reg.Record("c2", "nodeA", "192.0.2.9:50002");

        reg.InstancesOf("nodeA").Should().ContainSingle().Which.Id.Should().Be(InstanceRegistry.Unidentified);
    }

    [Fact]
    public void AConnectionRenamedToAnotherNode_LeavesTheFirst()
    {
        var reg = new InstanceRegistry();
        reg.Record("c1", $"nodeA@{A}", "192.0.2.9:50001");
        reg.Record("c1", $"nodeB@{A}", "192.0.2.9:50001");

        reg.InstancesOf("nodeA").Should().BeEmpty();
        reg.InstancesOf("nodeB").Should().ContainSingle();
    }

    // ---- 072: what HW.SESSION said ---------------------------------------------------------------

    [Fact]
    public void Describe_AttachesTheProcessDetails()
    {
        var reg = new InstanceRegistry();
        reg.Record("c1", $"nodeA@{A}", "192.0.2.9:50001");
        reg.Describe("nodeA", new InstanceMeta(A, "WEB1", 4120, "shop2", DateTimeOffset.UnixEpoch, "3.0.0"));

        reg.InstancesOf("nodeA").Should().ContainSingle().Which.Should().Be(
            new InstanceView(A, "192.0.2.9:50001", "WEB1", 4120, "shop2", DateTimeOffset.UnixEpoch, "3.0.0"));
    }

    [Fact]
    public void Describe_WithAnInvalidId_IsIgnored()
    {
        var reg = new InstanceRegistry();
        reg.Record("c1", $"nodeA@{A}", "192.0.2.9:50001");
        reg.Describe("nodeA", new InstanceMeta("not-an-id", "WEB1", 1, null, null, null));

        reg.InstancesOf("nodeA").Single().Host.Should().BeNull();
    }

    [Fact]
    public void Describe_ClipsOverlongFields()
    {
        var reg = new InstanceRegistry();
        reg.Record("c1", $"nodeA@{A}", "192.0.2.9:50001");
        reg.Describe("nodeA", new InstanceMeta(A, new string('h', 10_000), 1, null, null, null));

        reg.InstancesOf("nodeA").Single().Host.Should().HaveLength(256);
    }

    [Fact]
    public void Details_SurviveABriefDisconnect_AndExpireAfterTheRetention()
    {
        var now = DateTime.UtcNow.Ticks;
        var reg = new InstanceRegistry(clock: () => now);
        reg.Record("c1", $"nodeA@{A}", "192.0.2.9:50001");
        reg.Describe("nodeA", new InstanceMeta(A, "WEB1", 1, null, null, null));
        reg.Remove("c1");

        now += TimeSpan.FromMinutes(5).Ticks;
        reg.Record("c2", $"nodeA@{A}", "192.0.2.9:50009");   // reconnect, no new HW.SESSION
        reg.InstancesOf("nodeA").Single().Host.Should().Be("WEB1", "a reconnect must not blank the view");

        reg.Remove("c2");
        now += InstanceRegistry.Retention.Ticks + 1;
        reg.Record("c3", $"nodeA@{A}", "192.0.2.9:50010");
        reg.InstancesOf("nodeA").Single().Host.Should().BeNull("details of an instance gone longer than the retention are dropped");
    }

    [Fact]
    public void TheDescriptionCap_EvictsTheOldestDisconnected_NeverALiveOne()
    {
        var now = 1_000L;
        var reg = new InstanceRegistry(clock: () => now);
        reg.Record("live", $"live@{A}", "192.0.2.9:1");
        reg.Describe("live", new InstanceMeta(A, "LIVE", 1, null, null, null));

        for (var i = 0; i < InstanceRegistry.MaxDescribed; i++)
        {
            now++;
            reg.Describe($"gone-{i}", new InstanceMeta(i.ToString("x12"), "GONE", i, null, null, null));
        }

        reg.InstancesOf("live").Single().Host.Should().Be("LIVE");
    }

    // ---- 072: the warning ------------------------------------------------------------------------

    private static (InstanceRegistry Registry, FlightRecorder Recorder) WithRecorder()
    {
        var recorder = new FlightRecorder(new ObservabilityOptions());
        return (new InstanceRegistry(recorder), recorder);
    }

    private static List<HighwayEvent> Shared(FlightRecorder recorder, string node)
        => [.. recorder.Read(node, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100)
            .Where(e => e.EventType == HighwayEventType.NodeNameShared)];

    [Fact]
    public void ASecondProcess_RaisesOneSharedEvent_AndItsDepartureOneMore()
    {
        var (reg, recorder) = WithRecorder();
        reg.Record("c1", $"nodeA@{A}", "192.0.2.9:50001");
        reg.Record("c1b", $"nodeA@{A}", "192.0.2.9:50002");
        Shared(recorder, "nodeA").Should().BeEmpty("one process, however many connections");

        reg.Record("c2", $"nodeA@{B}", "192.0.2.7:50001");
        reg.Record("c2b", $"nodeA@{B}", "192.0.2.7:50002");
        var shared = Shared(recorder, "nodeA");
        shared.Should().ContainSingle("the count crossed one once").Which.Count.Should().Be(2);
        shared[0].ErrorCode.Should().Contain(A).And.Contain(B);

        reg.Remove("c2");
        Shared(recorder, "nodeA").Should().HaveCount(1, "B still has a connection");
        reg.Remove("c2b");
        Shared(recorder, "nodeA").Should().HaveCount(2).And.Contain(e => e.Count == 1, "back to one process");
    }

    [Fact]
    public void ConcurrentNaming_OfOneNewProcess_RaisesOnce()
    {
        for (var round = 0; round < 200; round++)
        {
            var (reg, recorder) = WithRecorder();
            reg.Record("c1", $"nodeA@{A}", "192.0.2.9:50001");

            using var start = new Barrier(4);
            var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                reg.Record($"b{i}", $"nodeA@{B}", $"192.0.2.7:{50000 + i}");
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Shared(recorder, "nodeA").Should().ContainSingle($"round {round}: four connections of one process are one transition");
        }
    }

    [Fact]
    public void AThrowingRecorderLogger_DoesNotEscape()
    {
        var recorder = new FlightRecorder(new ObservabilityOptions()) { Logger = new ThrowingLogger() };
        var reg = new InstanceRegistry(recorder);
        reg.Record("c1", $"nodeA@{A}", "x:1");

        var second = () => reg.Record("c2", $"nodeA@{B}", "x:2");

        second.Should().NotThrow("a diagnostic must never fail CLIENT SETNAME");
        reg.InstancesOf("nodeA").Should().HaveCount(2);
    }

    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("log sink down");
    }
}
