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
    public async Task ConnectAsync_UnreachableEndpoint_ThrowsServerUnreachableNamingTheEndpoint()
    {
        // Port 1 on loopback: nothing listens, and connect fails fast.
        const string endpoint = "127.0.0.1:1,connectTimeout=250,connectRetry=1,abortConnect=true";

        var act = () => HighwayConnection.ConnectAsync(endpoint);

        (await act.Should().ThrowAsync<HighwayServerUnreachableException>())
            .Which.Message.Should().Contain("127.0.0.1:1", "the operator must see which endpoint failed");
    }
}
