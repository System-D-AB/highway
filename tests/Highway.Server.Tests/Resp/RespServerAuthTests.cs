using System.Net;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Resp;
using Highway.Server.Security;
using Highway.Server.Storage;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// 040 T6 — auth end-to-end (037 R11): both AUTH forms against config users (PBKDF2), the
/// -NOAUTH gate, the loopback exemption, and that a wrong password over the wire surfaces the
/// classification the client turns into <c>HighwayAuthenticationException</c> (the substrings
/// -NOAUTH / -WRONGPASS). Tests force <c>exemptLoopback: false</c> so AUTH runs on loopback,
/// exactly as the embedded test server will.
/// </summary>
public class RespServerAuthTests
{
    private static async Task<RespServer> StartWithUser(string name, string password, bool exemptLoopback = false)
    {
        var options = new HighwayServerOptions();
        options.Authentication.Users.Add(new HighwayUser(name, PasswordHash.Hash(password)));
        var auth = new PasswordAuthenticator(options.Authentication, exemptLoopback);
        return await RespServer.StartAsync(new InMemoryStore(), options, auth, IPAddress.Loopback, 0);
    }

    private static ConfigurationOptions Config(int port, string? user = null, string? password = null)
    {
        var config = ConfigurationOptions.Parse($"localhost:{port}");
        config.AbortOnConnectFail = true;
        config.ConnectTimeout = 5000;
        if (user is not null) config.User = user;
        if (password is not null) config.Password = password;
        return config;
    }

    // ---- unit: authenticator -------------------------------------------------

    [Fact]
    public void Authenticator_DefaultUser_OneArgAuth_Verifies()
    {
        var options = new HighwayServerOptions();
        options.Authentication.Users.Add(new HighwayUser("default", PasswordHash.Hash("pw")));
        var auth = new PasswordAuthenticator(options.Authentication, exemptLoopback: false);

        auth.TryAuthenticate(null, "pw").Should().BeTrue("AUTH <password> matches the default user");
        auth.TryAuthenticate("default", "pw").Should().BeTrue("AUTH default <password> also works");
        auth.TryAuthenticate(null, "wrong").Should().BeFalse();
        auth.TryAuthenticate("ghost", "pw").Should().BeFalse("an unknown user is refused");
    }

    [Fact]
    public void Authenticator_NoConfig_IsPreAuthorized()
        => new PasswordAuthenticator(new HighwayServerOptions().Authentication)
            .IsPreAuthorized(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1)).Should().BeTrue();

    [Fact]
    public void Authenticator_LoopbackExempt_WhenEnabled()
    {
        var options = new HighwayServerOptions();
        options.Authentication.Users.Add(new HighwayUser("default", PasswordHash.Hash("pw")));
        new PasswordAuthenticator(options.Authentication, exemptLoopback: true)
            .IsPreAuthorized(new IPEndPoint(IPAddress.Loopback, 1)).Should().BeTrue();
        new PasswordAuthenticator(options.Authentication, exemptLoopback: false)
            .IsPreAuthorized(new IPEndPoint(IPAddress.Loopback, 1)).Should().BeFalse("the test-server posture requires AUTH on loopback");
    }

    // ---- wire: both AUTH forms -----------------------------------------------

    [Fact]
    public async Task Wire_AuthWithPassword_Then_HwCommand_Succeeds()
    {
        await using var server = await StartWithUser("default", "s3cr3t");
        using var mux = await ConnectionMultiplexer.ConnectAsync(Config(server.Port, password: "s3cr3t"));
        mux.IsConnected.Should().BeTrue();
        (await mux.GetDatabase().ExecuteAsync("HW.QSEND", "q", "m", "b")).ToString().Should().Be("OK");
    }

    [Fact]
    public async Task Wire_AuthWithUserAndPassword_Succeeds()
    {
        await using var server = await StartWithUser("worker", "hunter2");
        using var mux = await ConnectionMultiplexer.ConnectAsync(Config(server.Port, user: "worker", password: "hunter2"));
        mux.IsConnected.Should().BeTrue("AUTH <user> <password> is honoured");
        (await mux.GetDatabase().ExecuteAsync("HW.STATS", "server")).IsNull.Should().BeFalse();
    }

    // ---- wire: refusal paths (the client's HighwayAuthenticationException substrings) ----

    [Fact]
    public async Task Wire_WrongPassword_IsRefused_WithWrongPass()
    {
        await using var server = await StartWithUser("default", "correct");

        // SE.Redis answers the connect-time probes pre-auth, so the failure surfaces when a real
        // command runs (or on connect, depending on timing). Either way the message carries the
        // substring the client classifies into HighwayAuthenticationException.
        var act = async () =>
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync(Config(server.Port, password: "wrong"));
            await mux.GetDatabase().ExecuteAsync("HW.STATS", "server");
        };
        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.ToString().Should().MatchRegex("WRONGPASS|NOAUTH|Authentication",
            "the wire error must carry the substring the client classifies into HighwayAuthenticationException");
    }

    [Fact]
    public async Task Wire_NoAuth_BeforeAuthenticating_IsRefused()
    {
        // No credentials supplied: a command runs unauthenticated and gets -NOAUTH.
        await using var server = await StartWithUser("default", "correct");
        var act = async () =>
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync(Config(server.Port)); // no password
            await mux.GetDatabase().ExecuteAsync("HW.STATS", "server");
        };
        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.ToString().Should().MatchRegex("WRONGPASS|NOAUTH|Authentication");
    }
}
