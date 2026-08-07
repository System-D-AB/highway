using FluentAssertions;
using Highway.Client;
using Highway.Client.Engine;
using Highway.Client.Wire;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 012 — authentication end to end, against a real secured server.
/// </summary>
public class AuthenticationTests : IDisposable
{
    private const string Password = "test-password-4417";

    private readonly HighwayTestServer _server = new(o => o.Authentication.Password = Password);

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task CorrectPassword_Connects()
    {
        await using var connection = await HighwayConnection.ConnectAsync(
            _server.ConnectionString, new HighwayOptions { Password = Password });

        connection.Should().NotBeNull();
    }

    /// <summary>
    /// The bare endpoint, without the password <c>HighwayTestServer.ConnectionString</c>
    /// now carries by default (feature 012 T11).
    /// </summary>
    private string EndpointOnly => $"localhost:{_server.Port}";

    [Fact]
    public async Task NoCredentials_AreRefused_Legibly()
    {
        var connect = async () => await HighwayConnection.ConnectAsync(EndpointOnly);

        (await connect.Should().ThrowAsync<HighwayAuthenticationException>())
            .WithMessage("*rejected the supplied credentials*")
            .And.Message.Should().Contain("WithPassword",
                "the message should name the remedy, not merely report a refusal");
    }

    [Fact]
    public async Task WrongPassword_IsRefused()
    {
        var connect = async () => await HighwayConnection.ConnectAsync(
            EndpointOnly, new HighwayOptions { Password = "not-the-password" });

        await connect.Should().ThrowAsync<HighwayAuthenticationException>();
    }

    /// <summary>
    /// Distinct from an unreachable server, because the remedies are opposite: one is
    /// "check the network", the other is "check the password".
    /// </summary>
    [Fact]
    public async Task UnreachableServer_IsADifferentExceptionFromABadPassword()
    {
        var dead = $"localhost:{Highway.Server.Internal.EphemeralPort.Probe()}";

        var connect = async () => await HighwayConnection.ConnectAsync(
            dead, new HighwayOptions { Password = Password });

        await connect.Should().ThrowAsync<HighwayServerUnreachableException>();
    }

    /// <summary>
    /// T7's real assertion: the password must not survive into the exception a failed
    /// connection produces.
    /// </summary>
    [Fact]
    public async Task Credentials_DoNotAppearInExceptions()
    {
        const string secret = "hunter2-should-never-be-logged";
        var dead = $"localhost:{Highway.Server.Internal.EphemeralPort.Probe()},password={secret}";

        var connect = async () => await HighwayConnection.ConnectAsync(dead);
        var ex = (await connect.Should().ThrowAsync<HighwayServerUnreachableException>()).Which;

        ex.ToString().Should().NotContain(secret,
            "an exception message is not a safe place for a password");
    }

    /// <summary>
    /// A malformed configuration must not leak either — whichever way it fails.
    ///
    /// <para>The assertion is deliberately on the <i>property</i> rather than the exception
    /// type: StackExchange.Redis parses very leniently, so a string that looks malformed
    /// may reach the connect path instead of the parse path. Pinning the type would make
    /// this test about which code path ran, when what matters is that neither path emits
    /// the secret.</para>
    /// </summary>
    [Fact]
    public async Task MalformedConfiguration_DoesNotLeakTheSecretEither()
    {
        const string secret = "hunter3-should-never-be-logged";

        Exception? thrown = null;
        try
        {
            await HighwayConnection.ConnectAsync($"::::not-valid::::,password={secret}");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.Should().NotBeNull("a malformed configuration must fail");
        thrown!.ToString().Should().NotContain(secret);
    }

    /// <summary>
    /// Everything the client does — heartbeats, doorbells, RPC, pub/sub — must work
    /// unchanged against a secured server.
    /// </summary>
    [Fact]
    public async Task FullClientBehaviour_WorksAgainstASecuredServer()
    {
        await using var host = await EngineNode.StartAsync(
            _server.ConnectionString, "auth-host", o => o.Password = Password);
        await using var caller = await EngineNode.StartAsync(
            _server.ConnectionString, "auth-caller", o => o.Password = Password);

        var response = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "secured" });

        response.StatusCode.Should().Be(200);
        response.Value.Should().Be("secured");
    }

    /// <summary>
    /// StackExchange.Redis re-issues AUTH from its stored configuration on reconnect. That
    /// is an assumption about someone else's library, so it is tested rather than trusted.
    /// </summary>
    [Fact]
    public async Task Reconnect_ReauthenticatesAutomatically()
    {
        await using var host = await EngineNode.StartAsync(
            _server.ConnectionString, "auth-reconnect-host", o => o.Password = Password);
        await using var caller = await EngineNode.StartAsync(
            _server.ConnectionString, "auth-reconnect-caller", o => o.Password = Password);

        (await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "before" })).Value.Should().Be("before");

        _server.Restart();
        await Task.Delay(1500);

        var after = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "after" });
        after.Value.Should().Be("after", "the client must re-authenticate without being told to");
    }
}
