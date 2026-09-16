using System.Net;
using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Runtime;
using Highway.Server.Observability;
using Highway.Server.Resp;
using Highway.Server.Storage;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// 040 T4 — the connection state machine, table-driven (design §"The connection state machine").
/// Every state × command class: what is accepted, what errors, and the transitions.
/// </summary>
public class RespSessionTests
{
    private static CommandDispatcher Dispatcher(out IHighwayStore store)
    {
        store = new InMemoryStore();
        var options = new HighwayServerOptions();
        return new CommandDispatcher(store, new StripedLock(), new NullDoorbell(),
            new FlightRecorder(options.Observability), options);
    }

    private static RespSession Session(bool preAuthorized, ISubscriptionSink? sink = null)
    {
        var dispatcher = Dispatcher(out _);
        return new RespSession(dispatcher, new FakeAuth(preAuthorized), sink ?? new FakeSink(),
            new IPEndPoint(IPAddress.Loopback, 5000));
    }

    private static byte[][] F(params string[] tokens) => [.. tokens.Select(Encoding.UTF8.GetBytes)];
    private static string Text(RespSession.SessionResult r) => Encoding.UTF8.GetString(r.Replies[0]);

    // ---- initial state -------------------------------------------------------

    [Fact]
    public void PreAuthorized_StartsAuthenticated()
        => Session(preAuthorized: true).State.Should().Be(ConnectionState.Authenticated);

    [Fact]
    public void NotPreAuthorized_StartsUnauthenticated()
        => Session(preAuthorized: false).State.Should().Be(ConnectionState.Unauthenticated);

    // ---- 048: CLIENT SETNAME records the observed address --------------------

    [Fact]
    public void ClientSetName_RecordsTheObservedAddress()
    {
        var observed = new Highway.Server.Resp.ObservedAddressRegistry();
        var session = new RespSession(Dispatcher(out _), new FakeAuth(preAuthorized: true), new FakeSink(),
            new IPEndPoint(IPAddress.Parse("10.0.0.9"), 6500), connectionId: "c1", observed: observed);

        session.Handle(F("CLIENT", "SETNAME", "nodeA"));

        observed.AddressOf("nodeA").Should().Be("10.0.0.9:6500",
            "CLIENT SETNAME names the connection; the broker records where it sees it from");
    }

    [Fact]
    public void OnConnectionClosed_ClearsTheObservedAddress()
    {
        var observed = new Highway.Server.Resp.ObservedAddressRegistry();
        var session = new RespSession(Dispatcher(out _), new FakeAuth(preAuthorized: true), new FakeSink(),
            new IPEndPoint(IPAddress.Loopback, 5000), connectionId: "c1", observed: observed);
        session.Handle(F("CLIENT", "SETNAME", "nodeA"));
        observed.AddressOf("nodeA").Should().NotBeNull();

        session.OnConnectionClosed();

        observed.AddressOf("nodeA").Should().BeNull("the entry goes when the connection does");
    }

    // ---- unauthenticated -----------------------------------------------------

    [Fact]
    public void Unauthenticated_HwCommand_IsNoAuth()
    {
        var session = Session(preAuthorized: false);
        var result = session.Handle(F("HW.STATS", "server"));
        Text(result).Should().StartWith("-NOAUTH");
    }

    [Fact]
    public void Unauthenticated_Ping_IsNoAuth()
    {
        // PING is SE.Redis's connect handshake: a pre-auth PONG would let a credential-less
        // connect succeed and break the client's HighwayAuthenticationException contract
        // (AuthenticationTests, 012/C6.3). Redis refuses pre-auth PING the same way.
        // 037 R11.3's original "AUTH and PING" wording was amended 2026-09-15 for this reason.
        var session = Session(preAuthorized: false);
        Text(session.Handle(F("PING"))).Should().StartWith("-NOAUTH");
    }

    [Fact]
    public void Unauthenticated_Auth_Wrong_IsWrongPass_StaysUnauthenticated()
    {
        var session = Session(preAuthorized: false);
        Text(session.Handle(F("AUTH", "nope"))).Should().StartWith("-WRONGPASS");
        session.State.Should().Be(ConnectionState.Unauthenticated);
    }

    [Fact]
    public void Unauthenticated_Auth_Right_Transitions()
    {
        // FakeAuth accepts password "s3cret".
        var session = new RespSession(Dispatcher(out _), new FakeAuth(preAuthorized: false, password: "s3cret"),
            new FakeSink(), new IPEndPoint(IPAddress.Loopback, 1));
        Text(session.Handle(F("AUTH", "s3cret"))).Should().Be("+OK\r\n");
        session.State.Should().Be(ConnectionState.Authenticated);
    }

    // ---- authenticated -------------------------------------------------------

    [Fact]
    public void Authenticated_HwCommand_Dispatches()
    {
        var session = Session(preAuthorized: true);
        Text(session.Handle(F("HW.QSEND", "q", "m1", "body"))).Should().Be("+OK\r\n");
    }

    [Fact]
    public void Authenticated_UnknownCommand_ErrorsNamingSubset()
    {
        var session = Session(preAuthorized: true);
        var reply = Text(session.Handle(F("FLUSHALL")));
        reply.Should().StartWith("-ERR HW_INVALID_ARG unknown command 'FLUSHALL'");
        reply.Should().Contain("HW.");
    }

    [Fact]
    public void Authenticated_Subscribe_Transitions_And_Confirms()
    {
        var sink = new FakeSink();
        var session = Session(preAuthorized: true, sink);
        var result = session.Handle(F("SUBSCRIBE", "hw:door:rep"));
        session.State.Should().Be(ConnectionState.Subscribed);
        // *3 subscribe <channel> :1
        Encoding.UTF8.GetString(result.Replies[0]).Should().Be("*3\r\n$9\r\nsubscribe\r\n$11\r\nhw:door:rep\r\n:1\r\n");
    }

    // ---- subscribed (restricted mode) ----------------------------------------

    [Fact]
    public void Subscribed_HwCommand_IsRejected()
    {
        var session = Session(preAuthorized: true);
        session.Handle(F("SUBSCRIBE", "ch"));
        var reply = Text(session.Handle(F("HW.STATS", "server")));
        reply.Should().Contain("subscribe mode");
    }

    [Fact]
    public void Subscribed_Unsubscribe_AllChannels_ReturnsToAuthenticated()
    {
        var sink = new FakeSink();
        var session = Session(preAuthorized: true, sink);
        session.Handle(F("SUBSCRIBE", "ch"));
        session.Handle(F("UNSUBSCRIBE", "ch"));
        session.State.Should().Be(ConnectionState.Authenticated);
    }

    // ---- handshake probes ----------------------------------------------------

    [Fact]
    public void Echo_ReturnsBulkOfArg()
        => Text(Session(preAuthorized: true).Handle(F("ECHO", "hi"))).Should().Be("$2\r\nhi\r\n");

    [Fact]
    public void ConfigGet_ReturnsEmptyArray()
        => Text(Session(preAuthorized: true).Handle(F("CONFIG", "GET", "timeout"))).Should().Be("*0\r\n");

    [Fact]
    public void TiebreakerGet_ReturnsNullBulk()
        => Text(Session(preAuthorized: true).Handle(F("GET", "__Booksleeve_TieBreak"))).Should().Be("$-1\r\n");

    [Fact]
    public void Client_ReturnsOk()
        => Text(Session(preAuthorized: true).Handle(F("CLIENT", "SETNAME", "node-1"))).Should().Be("+OK\r\n");

    // ---- fakes ---------------------------------------------------------------

    private sealed class FakeAuth(bool preAuthorized, string? password = null) : IConnectionAuthenticator
    {
        public bool IsPreAuthorized(EndPoint? remote) => preAuthorized;
        public bool TryAuthenticate(string? username, string pw) => password is not null && pw == password;
    }

    private sealed class FakeSink : ISubscriptionSink
    {
        private readonly HashSet<string> _channels = [];
        public int Subscribe(string channel) { _channels.Add(channel); return _channels.Count; }
        public int Unsubscribe(string channel) { _channels.Remove(channel); return _channels.Count; }
        public int Count => _channels.Count;
    }

    private sealed class NullDoorbell : IDoorbell
    {
        public int Ring(string channel, ReadOnlySpan<byte> payload) => 0;
    }
}
