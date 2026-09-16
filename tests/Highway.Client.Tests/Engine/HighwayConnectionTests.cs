using FluentAssertions;
using Highway.Client.Engine;
using Highway.Client.Wire;
using StackExchange.Redis;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 005 Task 4 — the 004.1 error-classification rule that the whole
/// retry policy rests on, plus fail-fast connect behavior.
///
/// <para>The wire shapes themselves (command names, argument orders) are
/// asserted end-to-end against a real server in the integration suite; what
/// matters here is that a client can tell retryable from permanent.</para>
/// </summary>
public class HighwayConnectionTests
{
    [Fact]
    public void IsTransient_BareTransactionFailed_IsTrue()
        => HighwayConnection.IsTransient("ERR Transaction failed.").Should().BeTrue(
            "the bare Garnet abort is a watch conflict — the command did no work and is safe to retry");

    [Theory]
    [InlineData("ERR HW_INVALID_ARG service is blank")]
    [InlineData("ERR HW_PAYLOAD_TOO_LARGE 2097152 > 1048576")]
    [InlineData("ERR HW_INVALID_COUNT count must be 1..500")]
    [InlineData("ERR HW_INTERNAL something broke")]
    [InlineData("ERR wrong number of arguments")]
    [InlineData("ERR Transaction failed")]      // no trailing period — not the sentinel
    [InlineData("err transaction failed.")]     // case matters — not the sentinel
    public void IsTransient_EverythingElse_IsFalse(string message)
        => HighwayConnection.IsTransient(message).Should().BeFalse(
            "only the exact bare abort message is retryable; retrying a permanent error would spin forever");

    [Fact]
    public void Classify_TransientAbort_YieldsTransientException()
        => HighwayConnection.Classify(new RedisServerException("ERR Transaction failed."))
            .Should().BeOfType<HighwayTransientException>();

    [Theory]
    [InlineData("ERR HW_INVALID_ARG blank service")]
    [InlineData("ERR HW_PAYLOAD_TOO_LARGE 99 > 10")]
    [InlineData("ERR HW_INTERNAL boom")]
    public void Classify_HighwayErrors_YieldPermanentTransportException(string message)
        => HighwayConnection.Classify(new RedisServerException(message))
            .Should().BeOfType<HighwayTransportException>();

    [Fact]
    public void Classify_ConnectionException_IsPermanentTransport()
        => HighwayConnection.Classify(new RedisConnectionException(ConnectionFailureType.SocketFailure, "gone"))
            .Should().BeOfType<HighwayTransportException>();

    [Fact]
    public async Task ConnectAsync_InvalidConfigurationString_ThrowsDescriptiveArgumentException()
    {
        var act = () => HighwayConnection.ConnectAsync("   ");

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("not a valid Highway server configuration");
    }

    [Fact]
    public void TryParseNotPrimary_ReadsEndpointAndEpoch()
    {
        HighwayConnection.TryParseNotPrimary("NOTPRIMARY 127.0.0.1:6501 4", out var ep, out var epoch)
            .Should().BeTrue();
        ep.Should().Be("127.0.0.1:6501");
        epoch.Should().Be(4UL);
    }

    [Fact]
    public void ConnectionString_HostAndOptionParsing_SplitsCorrectly()
    {
        HighwayConnectionSource.HostsOf("localhost:6500,password=secret")
            .Should().Equal("localhost:6500");
        HighwayConnectionSource.OptionsOf("localhost:6500,password=secret")
            .Should().Be(",password=secret");

        // 042 R6.1: a multi-endpoint string names every host before the first option.
        HighwayConnectionSource.HostsOf("hostA:6500,hostB:6500,password=secret,ssl=true")
            .Should().Equal("hostA:6500", "hostB:6500");
        HighwayConnectionSource.OptionsOf("hostA:6500,hostB:6500,password=secret,ssl=true")
            .Should().Be(",password=secret,ssl=true");

        HighwayConnectionSource.HostsOf("hostA:6500,hostB:6500").Should().Equal("hostA:6500", "hostB:6500");
        HighwayConnectionSource.OptionsOf("hostA:6500,hostB:6500").Should().BeEmpty();
    }

    /// <summary>042-1b B-T2: the successor order — roster priority ascending, priority-0 skipped, current last, bootstrap fallback.</summary>
    [Fact]
    public void CandidateEndpoints_RosterOrder_SkipsZero_ExcludesCurrent_FallsBackToBootstrap()
    {
        var source = new HighwayConnectionSource(new HighwayOptions
        {
            Server = "boot1:6500,boot2:6500,password=secret",
        });

        // Before any roster: the bootstrap list, current (boot1) last.
        source.CandidateEndpoints().Should().Equal("boot2:6500", "boot1:6500");

        source.SetRosterForTests(
        [
            new HighwayConnectionSource.RosterEntry("never", 0, "never:6500"),
            new HighwayConnectionSource.RosterEntry("third", 30, "c:6500"),
            new HighwayConnectionSource.RosterEntry("first", 1, "a:6500"),
            new HighwayConnectionSource.RosterEntry("second", 2, "b:6500"),
        ], version: 3);

        // The live roster supersedes the bootstrap string entirely (dynamic membership):
        // priority ascending, the priority-0 node never a candidate.
        source.CandidateEndpoints().Should().Equal("a:6500", "b:6500", "c:6500");

        // GOODBYE shape: the incumbent is excluded even though it still answers.
        source.SetRosterForTests(
        [
            new HighwayConnectionSource.RosterEntry("first", 1, "boot1:6500"),
            new HighwayConnectionSource.RosterEntry("second", 2, "b:6500"),
        ], version: 4);
        source.CandidateEndpoints(excludeCurrent: true).Should().Equal(new[] { "b:6500" },
            "the departing master is not a successor candidate");
        source.CandidateEndpoints().Should().Equal(new[] { "b:6500", "boot1:6500" },
            "without exclusion the current host is merely ordered last");
    }

    [Fact]
    public void NoteObservedEpoch_IsMonotonic()
    {
        var source = new HighwayConnectionSource(new HighwayOptions { Server = "h:6500" });
        source.NoteObservedEpoch(3);
        source.NoteObservedEpoch(2);
        source.LastSeenEpoch.Should().Be(3, "a client never forgets a higher epoch it has seen");
    }

    [Fact]
    public async Task ConnectAsync_UnreachableEndpoint_ThrowsServerUnreachableNamingTheEndpoint()
    {
        // Port 1 on loopback: nothing listens, and connect fails fast.
        const string endpoint = "127.0.0.1:1,connectTimeout=250,connectRetry=1,abortConnect=true";

        var act = () => HighwayConnection.ConnectAsync(endpoint);

        (await act.Should().ThrowAsync<HighwayServerUnreachableException>())
            .Which.Message.Should().Contain("127.0.0.1:1", "the operator must see which endpoint failed");
    }
}
