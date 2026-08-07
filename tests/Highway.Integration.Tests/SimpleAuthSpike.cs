using Garnet.server.Auth.Settings;
using Highway.Server;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 012, T2b — SPIKE. Delete or convert before the feature closes.
///
/// <para>Both ACL spikes used an ACL <b>file</b>, because roles need one. But the
/// common case an operator actually wants is "set a password at startup, hand it to
/// the dev team" — no file anywhere. Garnet supports that two ways, and which one
/// Highway makes primary depends on what the client has to send. Measure it.</para>
/// </summary>
public class SimpleAuthSpike(ITestOutputHelper output)
{
    private static (HighwayServer Server, int Port) Start(IAuthenticationSettings auth)
    {
        var port = Highway.Server.Internal.EphemeralPort.Probe();
        var opts = new HighwayServerOptions { Port = port, DataDir = null };
        var garnetOpts = HighwayServerBuilder.BuildGarnetOptions(opts);
        garnetOpts.AuthSettings = auth;
        garnetOpts.AclStrictCustomCommands = false;   // T1 finding 3
        var server = new HighwayServer(new HighwayGarnetServer(garnetOpts), opts);
        server.Start();
        return (server, port);
    }

    private async Task<string> TryAsync(int port, string? user, string? password)
    {
        try
        {
            var config = ConfigurationOptions.Parse($"localhost:{port}");
            config.User = user;
            config.Password = password;
            config.AbortOnConnectFail = true;
            await using var mux = await ConnectionMultiplexer.ConnectAsync(config);
            var db = mux.GetDatabase();
            // ACL WHOAMI is meaningless in password mode; tolerate its absence
            // rather than letting it decide the verdict.
            string who;
            try { who = (await db.ExecuteAsync("ACL", "WHOAMI")).ToString()!; }
            catch (RedisException) { who = "n/a"; }

            await db.ExecuteAsync("HW.CALL", "spike.svc", $"r-{Guid.NewGuid():N}", "p"u8.ToArray());
            return $"OK (whoami={who})";
        }
        catch (Exception ex)
        {
            return $"REFUSED ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Password mode — Garnet's `requirepass` equivalent. No usernames exist.
    /// </summary>
    [Fact]
    public async Task PasswordMode_NoFile()
    {
        var (server, port) = Start(new PasswordAuthenticationSettings("s3cret"));
        using (server)
        {
            output.WriteLine("== PasswordAuthenticationSettings(\"s3cret\") — no file ==");
            output.WriteLine($"  password only            {await TryAsync(port, null, "s3cret")}");
            output.WriteLine($"  user 'default' + pwd     {await TryAsync(port, "default", "s3cret")}");
            output.WriteLine($"  wrong password           {await TryAsync(port, null, "wrong")}");
            output.WriteLine($"  no credentials           {await TryAsync(port, null, null)}");
        }
    }

    /// <summary>
    /// ACL mode with a default password and <b>no ACL file</b> — one user, named
    /// `default`, with a password. This is the "startup user/password" path.
    /// </summary>
    [Fact]
    public async Task AclMode_DefaultPasswordOnly_NoFile()
    {
        var (server, port) = Start(new AclAuthenticationPasswordSettings(aclConfigurationFile: null, defaultPassword: "s3cret"));
        using (server)
        {
            output.WriteLine("== AclAuthenticationPasswordSettings(null, \"s3cret\") — no file ==");
            output.WriteLine($"  password only            {await TryAsync(port, null, "s3cret")}");
            output.WriteLine($"  user 'default' + pwd     {await TryAsync(port, "default", "s3cret")}");
            output.WriteLine($"  made-up user + pwd       {await TryAsync(port, "devteam", "s3cret")}");
            output.WriteLine($"  wrong password           {await TryAsync(port, null, "wrong")}");
            output.WriteLine($"  no credentials           {await TryAsync(port, null, null)}");
        }
    }
}
