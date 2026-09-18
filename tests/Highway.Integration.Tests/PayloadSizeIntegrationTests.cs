using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Wire;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>A queue message with a large body, for the message-size tests (feature 057). No handler
/// is defined, so a sent message simply waits in the queue.</summary>
[Queue("payload.big")]
public sealed record BigNote : ISend
{
    public string Body { get; init; } = "";
}

/// <summary>
/// Feature 057 T4/T7 — message size end to end: the client learns the server's configured limit and
/// sends above its own default (057-b), a client still fails fast at the effective limit, and the
/// server refuses an oversize payload as a permanent error (the authority, R2.3).
/// </summary>
public class PayloadSizeIntegrationTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task Client_LearnsRaisedServerLimit_AndSendsAboveItsOwnDefault()
    {
        using var server = new HighwayTestServer(o => o.MaxPayloadBytes = 10 * MiB);
        await using var node = await EngineNode.StartAsync(server.ConnectionString, "big-sender");

        // 6 MiB — above the client's 5 MiB default, below the server's raised 10 MiB. It sends only if
        // the client learned the server's limit at connect (057-b); otherwise it fails fast at 5 MiB.
        var id = await node.Client.SendAsync(new BigNote { Body = new string('x', 6 * MiB) });

        id.Should().NotBeNullOrWhiteSpace("the client learned the server's 10 MiB limit and did not reject at 5 MiB");
    }

    [Fact]
    public async Task Client_FailsFast_AtTheEffectiveLimit_WhenTheServerIsNotRaised()
    {
        using var server = new HighwayTestServer();   // default 5 MiB
        await using var node = await EngineNode.StartAsync(server.ConnectionString, "big-sender-2");

        var act = () => node.Client.SendAsync(new BigNote { Body = new string('x', 6 * MiB) });

        await act.Should().ThrowAsync<PayloadTooLargeException>("6 MiB exceeds the 5 MiB effective limit");
    }

    [Fact]
    public void Server_RefusesAnOversizePayload_AsAPermanentError()
    {
        using var server = new HighwayTestServer();   // default 5 MiB
        using var mux = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = mux.GetDatabase();

        // Raw wire, bypassing the client fail-fast: the server is the authority. A single bulk string
        // past the limit is refused at the RESP frame reader (before the whole payload is buffered);
        // the command-level HW_PAYLOAD_TOO_LARGE is the backstop for a payload that fits the frame.
        var act = () => db.Execute("HW.QSEND", "payload.big", "id-1", new string('x', 6 * MiB));

        act.Should().Throw<RedisServerException>("an oversize message is refused permanently")
            .WithMessage("*exceeding*limit*");
    }
}
