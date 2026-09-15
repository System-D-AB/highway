using System.Net;
using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Resp;
using Highway.Server.Storage;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// 040 T7 — doorbell pub/sub end-to-end (037 R7). A real SE.Redis subscriber on a
/// <c>hw:door:*</c> channel receives the push a command rings post-commit; the ring is
/// server-internal (no client ever <c>PUBLISH</c>es); and nothing doorbell-shaped is ever written
/// to the store — the doorbell is a latency optimisation, correctness rides the backstop sweep.
/// </summary>
public class RespServerDoorbellTests
{
    private static async Task<(RespServer Server, InMemoryStore Store)> StartAsync()
    {
        var store = new InMemoryStore();
        var server = await RespServer.StartAsync(
            store, new HighwayServerOptions(),
            new PasswordAuthenticator(password: null, authDisabled: true),
            IPAddress.Loopback, 0);
        return (server, store);
    }

    private static ConfigurationOptions Config(int port)
    {
        var config = ConfigurationOptions.Parse($"localhost:{port}");
        config.AbortOnConnectFail = true;
        config.ConnectTimeout = 5000;
        return config;
    }

    [Fact]
    public async Task Subscriber_ReceivesDoorbell_WhenAQueueSendRings()
    {
        var (server, _) = await StartAsync();
        await using var _server = server;
        using var mux = await ConnectionMultiplexer.ConnectAsync(Config(server.Port));

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = mux.GetSubscriber();
        await subscriber.SubscribeAsync(
            RedisChannel.Literal("hw:door:q:invoices"),
            (_, value) => received.TrySetResult(value.ToString()));

        // A separate command path enqueues — HW.QSEND rings hw:door:q:invoices post-commit.
        await mux.GetDatabase().ExecuteAsync("HW.QSEND", "invoices", "m-42", "body");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(received.Task, "the subscriber must receive the doorbell within the timeout");
        (await received.Task).Should().Be("m-42", "the doorbell payload is the message id the command rang with");
    }

    [Fact]
    public async Task Subscriber_ReceivesReplyDoorbell_WhenAReplyRings()
    {
        var (server, _) = await StartAsync();
        await using var _server = server;
        using var mux = await ConnectionMultiplexer.ConnectAsync(Config(server.Port));

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await mux.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal("hw:door:rep"),
            (_, value) => received.TrySetResult(value.ToString()));

        await mux.GetDatabase().ExecuteAsync("HW.REPLY", "req-7", "the-answer");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(received.Task, "HW.REPLY rings the reply doorbell");
    }

    [Fact]
    public async Task NoSubscriber_QueueSendStillSucceeds()
    {
        // Ringing a channel nobody is subscribed to costs nothing and never fails the command.
        var (server, _) = await StartAsync();
        await using var _server = server;
        using var mux = await ConnectionMultiplexer.ConnectAsync(Config(server.Port));
        (await mux.GetDatabase().ExecuteAsync("HW.QSEND", "lonely", "m1", "b")).ToString().Should().Be("OK");
    }

    [Fact]
    public async Task Doorbell_IsNeverWrittenToTheStore()
    {
        var (server, store) = await StartAsync();
        await using var _server = server;
        using var mux = await ConnectionMultiplexer.ConnectAsync(Config(server.Port));

        await mux.GetDatabase().ExecuteAsync("HW.QSEND", "invoices", "m1", "body");
        await mux.GetDatabase().ExecuteAsync("HW.CALL", "orders", "req-1", "payload");
        await mux.GetDatabase().ExecuteAsync("HW.REPLY", "req-1", "answer");

        // Not one key in the whole keyspace mentions "door" — the doorbell touches no storage
        // (037 R7 / physical-layout §3: hw:door:* never reaches the store).
        var dump = store.DumpData();
        foreach (var (key, _) in dump)
            Encoding.UTF8.GetString(key).Should().NotContain("door",
                "a doorbell is a signal, never a stored row");
    }

    [Fact]
    public async Task DisconnectMidPublish_LosesOnlyThatDelivery()
    {
        // A subscriber that vanishes must not fail the ring for anyone — the ring is best-effort.
        var (server, _) = await StartAsync();
        await using var _server = server;

        // Subscriber A stays; subscriber B disconnects. A ring must still reach A.
        using var muxA = await ConnectionMultiplexer.ConnectAsync(Config(server.Port));
        var receivedA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await muxA.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal("hw:door:q:shared"),
            (_, value) => receivedA.TrySetResult(value.ToString()));

        var muxB = await ConnectionMultiplexer.ConnectAsync(Config(server.Port));
        await muxB.GetSubscriber().SubscribeAsync(RedisChannel.Literal("hw:door:q:shared"), (_, _) => { });
        muxB.Dispose(); // B vanishes

        using var producer = await ConnectionMultiplexer.ConnectAsync(Config(server.Port));
        await producer.GetDatabase().ExecuteAsync("HW.QSEND", "shared", "m-1", "b");

        var completed = await Task.WhenAny(receivedA.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(receivedA.Task, "A still receives the doorbell even though B disconnected");
    }
}
