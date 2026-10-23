using FluentAssertions;
using Highway.Client.Engine;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 078 — a wave of failing operations runs one failover walk, not one each. A client with hundreds of calls in
/// flight when its primary went away used to queue a walk per call behind the failover gate; the waiters convoyed on the
/// gate's lock and the client never recovered.
/// </summary>
public class FailoverStormTests
{
    private static HighwayConnectionSource Source(int probeMs)
        => new(new HighwayOptions { Server = "192.0.2.1:6500,192.0.2.2:6500" })
        {
            // Every endpoint is unreachable, and each probe takes a while, so the callers overlap one walk.
            EndpointReachableOverride = _ => { Thread.Sleep(probeMs); return false; },
        };

    [Fact]
    public async Task FiveHundredConcurrentFailures_RunOneWalk_AndAllGetItsResult()
    {
        var source = Source(probeMs: 200);

        var callers = Enumerable.Range(0, 500).Select(_ => source.TryFailoverAsync()).ToArray();
        var results = await Task.WhenAll(callers);

        results.Should().AllSatisfy(r => r.Should().BeFalse("nothing answers"));
        source.WalksStarted.Should().Be(1, "every caller in the wave awaits the same walk");

        (await source.TryFailoverAsync()).Should().BeFalse();
        source.WalksStarted.Should().Be(2, "the next failure, after the wave, starts a fresh walk");
    }

    [Fact]
    public async Task AGoodbyeWalk_IsNeverServedByAPlainOne()
    {
        var source = Source(probeMs: 200);

        var plain = source.TryFailoverAsync();
        var departures = Enumerable.Range(0, 50).Select(_ => source.TryFailoverAsync(excludeCurrent: true)).ToArray();
        await Task.WhenAll(departures.Append(plain));

        source.WalksStarted.Should().Be(2, "one plain walk and one GOODBYE walk, each shared by its own callers");
    }

    [Fact]
    public async Task ACallerThatGivesUp_EndsOnlyItsOwnWait()
    {
        var source = Source(probeMs: 300);
        using var cts = new CancellationTokenSource();

        var patient = source.TryFailoverAsync();
        var impatient = source.TryFailoverAsync(cts.Token);
        await cts.CancelAsync();

        var cancelled = () => impatient;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await patient).Should().BeFalse("the shared walk ran to its end for the caller still waiting");
        source.WalksStarted.Should().Be(1);
    }
}
