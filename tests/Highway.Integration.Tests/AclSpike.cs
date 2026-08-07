using Garnet.server.Auth.Settings;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 012, T1 — SPIKE. Delete or convert before the feature closes.
///
/// <para>Requirement 3 (roles) rests entirely on a claim read out of the pinned
/// Garnet source: that ACL rules apply <b>per custom command name</b>, so
/// <c>+hw.call -hw.replay</c> is a meaningful grant. Reading
/// <c>CheckACLPermissionsForCustomCommand</c> is not the same as watching it
/// refuse a command. This spike watches.</para>
///
/// <para>It also records the exact refusal strings, which T8's client-side error
/// mapping needs and which must not be guessed.</para>
/// </summary>
public class AclSpike : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _aclFile;
    private readonly int _port;
    private readonly HighwayServer _server;

    public AclSpike(ITestOutputHelper output)
    {
        _out = output;

        // Two users with deliberately disjoint HW.* grants, plus the stock
        // commands each one needs. ~* is required: Garnet's key patterns are
        // all-or-nothing, so there is no narrower option to test.
        _aclFile = Path.Combine(Path.GetTempPath(), $"highway-spike-{Guid.NewGuid():N}.acl");
        // FINDING (T1): an allow-list narrow enough to be a useful role is too
        // narrow for StackExchange.Redis to *connect* — its handshake issues
        // commands Highway never does. Isolate the per-name mechanism first with
        // deny rules over +@all; the minimum connect set is measured separately
        // by MinimumConnectSet_ForAnAllowListedUser.
        File.WriteAllText(_aclFile, string.Join("\n",
            "user caller on >callerpw ~* +@all -hw.replay",
            "user observer on >observerpw ~* +@all -hw.call",
            "user default on nopass ~* +@all"));

        _port = Highway.Server.Internal.EphemeralPort.Probe();

        var opts = new HighwayServerOptions { Port = _port, DataDir = null };
        var garnetOpts = HighwayServerBuilder.BuildGarnetOptions(opts);
        garnetOpts.AuthSettings = new AclAuthenticationPasswordSettings(_aclFile);

        // FINDING (T1): Garnet validates per-name custom-command ACL rules inside
        // GarnetServer's constructor, against commands registered by *modules*.
        // Highway registers its commands after construction (required for AOF
        // replay), so every hw.* rule is unresolved at validation time and strict
        // mode refuses to start. Highway must turn strict mode off and validate
        // hw.* rules itself.
        garnetOpts.AclStrictCustomCommands = false;

        var garnet = new HighwayGarnetServer(garnetOpts);
        _server = new HighwayServer(garnet, opts);
        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
        try { File.Delete(_aclFile); } catch { /* spike cleanup */ }
    }

    private static async Task<IDatabase> ConnectAsync(int port, string user, string password)
    {
        var config = ConfigurationOptions.Parse($"localhost:{port}");
        config.User = user;
        config.Password = password;
        config.AbortOnConnectFail = true;
        var mux = await ConnectionMultiplexer.ConnectAsync(config);
        return mux.GetDatabase();
    }

    /// <summary>
    /// The claim, in both directions: a grant works and an omission is refused,
    /// for the same user on the same connection.
    /// </summary>
    [Fact]
    public async Task PerNameAcl_GrantsAndRefuses_CustomCommands()
    {
        var caller = await ConnectAsync(_port, "caller", "callerpw");

        // Granted: +hw.call
        var call = await caller.ExecuteAsync("HW.CALL", "spike.svc", "req-1", "payload"u8.ToArray());
        _out.WriteLine($"caller HW.CALL      -> {call}");

        // Not granted: no +hw.replay
        var replay = await Record(() => caller.ExecuteAsync("HW.REPLAY", "spike.svc"));
        _out.WriteLine($"caller HW.REPLAY    -> {replay}");

        var observer = await ConnectAsync(_port, "observer", "observerpw");

        // Granted: +hw.replay
        var obsReplay = await observer.ExecuteAsync("HW.REPLAY", "spike.svc");
        _out.WriteLine($"observer HW.REPLAY  -> {obsReplay}");

        // Not granted: no +hw.call
        var obsCall = await Record(() => observer.ExecuteAsync("HW.CALL", "spike.svc", "req-2", "payload"u8.ToArray()));
        _out.WriteLine($"observer HW.CALL    -> {obsCall}");

        replay.Should().NotBeNull("HW.REPLAY must be refused for a user without +hw.replay");
        obsCall.Should().NotBeNull("HW.CALL must be refused for a user without +hw.call");
    }

    /// <summary>
    /// T1, part two. A role is an allow-list, and the first attempt at one was too
    /// narrow for StackExchange.Redis to even connect — its handshake issues
    /// commands Highway never sends. Requirement 3 AC2 says a role must grant what
    /// the participant actually needs; this measures what that is instead of
    /// guessing.
    /// </summary>
    [Fact]
    public async Task MinimumConnectSet_ForAnAllowListedUser()
    {
        // Candidate rule sets, narrowest first. The Highway grants are constant;
        // only the stock surface varies.
        var candidates = new (string Name, string Rules)[]
        {
            ("hw-only",      "~* +hw.call +get +del +subscribe +ping"),
            ("connection",   "~* +hw.call +get +del +subscribe +@connection"),
            // Which category do custom commands fall under? +@all -@dangerous is a
            // common hardening idiom; if it silently disables every HW.* command
            // that must be documented, not discovered in production.
            ("no-admin",     "~* +@all -@admin"),
            ("no-dangerous", "~* +@all -@dangerous"),
            ("custom-cat",   "~* +@connection +@custom +get +del +subscribe"),
        };

        var file = Path.Combine(Path.GetTempPath(), $"highway-spike-min-{Guid.NewGuid():N}.acl");
        File.WriteAllText(file, string.Join("\n",
            candidates.Select(c => $"user {c.Name} on >pw {c.Rules}")
                      .Append("user default on nopass ~* +@all")));

        var port = Highway.Server.Internal.EphemeralPort.Probe();
        var opts = new HighwayServerOptions { Port = port, DataDir = null };
        var garnetOpts = HighwayServerBuilder.BuildGarnetOptions(opts);
        garnetOpts.AuthSettings = new AclAuthenticationPasswordSettings(file);
        garnetOpts.AclStrictCustomCommands = false;

        // Not `using` on the Garnet server: HighwayServer.Dispose disposes it.
        var garnet = new HighwayGarnetServer(garnetOpts);
        using var server = new HighwayServer(garnet, opts);
        server.Start();

        try
        {
            foreach (var (name, rules) in candidates)
            {
                string verdict;
                try
                {
                    var db = await ConnectAsync(port, name, "pw");
                    var call = await Record(() => db.ExecuteAsync("HW.CALL", "spike.svc", $"r-{name}", "p"u8.ToArray()));
                    verdict = call is null ? "CONNECTED, HW.CALL ok" : $"CONNECTED, HW.CALL -> {call}";
                }
                catch (Exception ex)
                {
                    verdict = $"NO CONNECT ({ex.GetType().Name})";
                }

                _out.WriteLine($"{name,-14} {verdict}");
                _out.WriteLine($"{"",-14} rules: {rules}");
            }
        }
        finally
        {
            try { File.Delete(file); } catch { /* spike cleanup */ }
        }
    }

    /// <summary>An unauthenticated connection should reach nothing at all.</summary>
    [Fact]
    public async Task Unauthenticated_IsRefused()
    {
        var config = ConfigurationOptions.Parse($"localhost:{_port}");
        config.AbortOnConnectFail = true;

        string? failure = null;
        try
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(config);
            var result = await Record(() => mux.GetDatabase().ExecuteAsync("HW.CALL", "spike.svc", "r", "p"u8.ToArray()));
            failure = result ?? "(no error — command succeeded unauthenticated)";
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }

        _out.WriteLine($"unauthenticated     -> {failure}");
        failure.Should().NotBeNull();
    }

    /// <summary>Records the server's error message, or null when the call succeeded.</summary>
    private static async Task<string?> Record(Func<Task<RedisResult>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (RedisException ex)
        {
            return ex.Message;
        }
    }
}
