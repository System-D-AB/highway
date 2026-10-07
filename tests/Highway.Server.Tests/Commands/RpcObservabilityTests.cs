using FluentAssertions;
using Highway.Server.Commands.Ported;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 074 Part A, T5/T11: a worker's first claim makes its service "served" (a node without heartbeats declares no
/// catalog); <c>HW.STATS</c> shows a service's oldest waiting request against its deadline and how its requests
/// ended without an answer.
/// </summary>
public class RpcObservabilityTests
{
    private static readonly long T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static long At(double seconds) => T0 + TimeSpan.FromSeconds(seconds).Ticks;
    private static string Text(byte[] b) => CommandHarness.AsText(b);

    [Fact]
    public void AWorkersFirstClaim_MakesItsServiceServed_SoCallsAreNotRefused()
    {
        using var h = new CommandHarness(rpcFailFast: true);
        Text(h.Run(new HwCallCommand(), At(0), "prices", "r-1", "p")).Should().StartWith("-ERR HW_NO_WORKERS");

        h.Run(new HwClaimCommand(), At(1), "SVC", "prices", "worker-without-heartbeat");   // nothing queued, but it serves

        Text(h.Run(new HwCallCommand(), At(2), "prices", "r-1", "p")).Should().Be("+OK\r\n");
    }

    [Fact]
    public void ServiceStats_ShowTheOldestRequestAndHowRequestsEnded()
    {
        using var h = new CommandHarness(options: new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5) }, rpcFailFast: true);
        Text(h.Run(new HwSessionCommand(), At(0), "w-1", """{"catalog":{"services":[{"name":"prices"}]}}""")).Should().Be("+OK\r\n");
        h.Run(new HwCallCommand(), At(0), "prices", "old", "p", "TIMEOUT", "1000");
        h.Run(new HwCallCommand(), At(0), "prices", "gone", "p", "TIMEOUT", "60000");
        h.Run(new HwCallCommand(), At(1), "prices", "waiting", "p", "TIMEOUT", "60000");
        h.Run(new HwCancelCommand(), At(1), "prices", "gone");
        h.Run(new HwClaimCommand(), At(2), "SVC", "prices", "w-1");   // drops "old" (expired), claims "waiting"
        h.Run(new HwCallCommand(), At(3), "prices", "next", "p", "TIMEOUT", "30000");

        var stats = h.Run(new HwStatsCommand(), At(13), "prices");

        DuplicateSafetyTests.StatField(stats, "kind").Should().Be("service");
        DuplicateSafetyTests.StatField(stats, "queueDepth").Should().Be("1");
        DuplicateSafetyTests.StatField(stats, "oldestAgeMs").Should().Be("10000");
        DuplicateSafetyTests.StatField(stats, "oldestRemainingMs").Should().Be("20000");
        DuplicateSafetyTests.StatField(stats, "expired").Should().Be("1");
        DuplicateSafetyTests.StatField(stats, "cancelled").Should().Be("1");
        DuplicateSafetyTests.StatField(stats, "failedFinal").Should().Be("0");
    }

    [Fact]
    public void AServedServiceWithEveryHostGone_StillReportsAsAService()
    {
        using var h = new CommandHarness();
        Text(h.Run(new HwSessionCommand(), At(0), "w-1", """{"catalog":{"services":[{"name":"prices"}]}}""")).Should().Be("+OK\r\n");
        h.Run(new HwHeartbeatCommand(), At(1), "w-1", "BYE");

        DuplicateSafetyTests.StatField(h.Run(new HwStatsCommand(), At(2), "prices"), "kind").Should().Be("service",
            "its queued requests are still waiting on their deadlines — exactly when an operator looks");
    }

    [Fact]
    public void ServerStats_AdvertiseTheBrokersRpcTimeouts()
    {
        using var h = new CommandHarness(options: new HighwayServerOptions { DefaultRpcTimeout = TimeSpan.FromSeconds(45) });

        var stats = h.Run(new HwStatsCommand(), At(0));

        DuplicateSafetyTests.StatField(stats, "defaultRpcTimeoutMs").Should().Be("45000");
        DuplicateSafetyTests.StatField(stats, "maxRpcTimeoutMs").Should().Be("3600000");
    }
}
