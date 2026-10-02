using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Resp;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// Feature 072: <c>HW.SESSION</c>'s optional <c>instance</c> object — what a process says about itself,
/// kept in memory for display. It must never be able to fail a session.
/// </summary>
public class SessionInstanceTests
{
    private const string Id = "3f9c0a17b2e4";
    private static readonly long T0 = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc).Ticks;

    private static (CommandHarness H, InstanceRegistry Reg) Harness()
    {
        var reg = new InstanceRegistry();
        reg.Record("c1", $"node-1@{Id}", "10.0.0.9:50001");
        return (new CommandHarness { Instances = reg }, reg);
    }

    private static string Session(CommandHarness h, string json)
        => CommandHarness.AsText(h.Run(new HwSessionCommand(), T0, "node-1", json));

    [Fact]
    public void AValidInstance_ReachesTheRegistry()
    {
        var (h, reg) = Harness();

        Session(h, $$$"""{"subscriptions":[],"instance":{"id":"{{{Id}}}","host":"WEB1","pid":4120,"hosting":"shop2","started":"2026-10-01T08:59:00Z","client":"3.0.0"}}""")
            .Should().Be("+OK\r\n");

        reg.InstancesOf("node-1").Should().ContainSingle().Which.Should().Be(new InstanceView(
            Id, "10.0.0.9:50001", "WEB1", 4120, "shop2", new DateTimeOffset(2026, 10, 1, 8, 59, 0, TimeSpan.Zero), "3.0.0"));
    }

    [Theory]
    [InlineData("""{"subscriptions":[]}""")]                                                   // absent
    [InlineData("""{"subscriptions":[],"instance":"WEB1"}""")]                                 // not an object
    [InlineData("""{"subscriptions":[],"instance":{"host":"WEB1"}}""")]                        // no id
    [InlineData("""{"subscriptions":[],"instance":{"id":"nope","host":"WEB1"}}""")]            // not an instance id
    [InlineData("""{"subscriptions":[],"instance":{"id":42}}""")]                              // mistyped id
    public void AMissingOrMalformedInstance_NeverFailsTheSession_AndDescribesNothing(string json)
    {
        var (h, reg) = Harness();

        Session(h, json).Should().Be("+OK\r\n");

        reg.InstancesOf("node-1").Single().Host.Should().BeNull();
    }

    [Fact]
    public void MistypedFields_AreDroppedOneByOne()
    {
        var (h, reg) = Harness();

        Session(h, $$$"""{"subscriptions":[],"instance":{"id":"{{{Id}}}","host":7,"pid":"x","started":"yesterday","client":"3.0.0"}}""")
            .Should().Be("+OK\r\n");

        reg.InstancesOf("node-1").Single().Should().Be(new InstanceView(Id, "10.0.0.9:50001", null, null, null, null, "3.0.0"));
    }

    [Fact]
    public void WithoutARegistry_TheInstanceIsIgnored()
    {
        using var h = new CommandHarness();   // in-process tests wire no registry

        Session(h, $$$"""{"subscriptions":[],"instance":{"id":"{{{Id}}}"}}""").Should().Be("+OK\r\n");
    }
}
