using Garnet.server.Auth.Settings;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 012, T2 — SPIKE. Delete or convert before the feature closes.
///
/// <para>`AclAuthenticationPasswordSettings` takes an ACL <b>file</b> and a default
/// password, and the `GarnetServerOptions` constructor Highway uses cannot accept an
/// `AccessControlList`. So users beyond the default come from either (a) a generated
/// ACL file or (b) `ACL SETUSER` over a loopback connection after start.</para>
///
/// <para>Route (a) is already proven — <c>AclSpike</c> uses it. This spike measures
/// route (b), and answers the question that decides between them: <b>does a password
/// set through `ACL SETUSER` reach the AOF?</b> A credential replayed from an
/// append-only log on every restart would be a leak this feature created.</para>
/// </summary>
public class AclUserCreationSpike : IDisposable
{
    private const string SecretPassword = "spike-secret-pw-8827";
    private const string SpikeUser = "spike-user";

    private readonly ITestOutputHelper _out;
    private readonly string _dataDir;
    private readonly string _aclFile;
    private readonly int _port;
    private readonly HighwayServerOptions _opts;
    private HighwayServer _server;

    public AclUserCreationSpike(ITestOutputHelper output)
    {
        _out = output;
        _dataDir = Path.Combine(Path.GetTempPath(), $"highway-spike-aof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _aclFile = Path.Combine(_dataDir, "users.acl");
        File.WriteAllText(_aclFile, "user default on nopass ~* +@all");

        _port = Highway.Server.Internal.EphemeralPort.Probe();
        _opts = new HighwayServerOptions { Port = _port, DataDir = _dataDir };
        _server = Create();
        _server.Start();
    }

    private HighwayServer Create()
    {
        var garnetOpts = HighwayServerBuilder.BuildGarnetOptions(_opts);
        garnetOpts.AuthSettings = new AclAuthenticationPasswordSettings(_aclFile);
        garnetOpts.AclStrictCustomCommands = false;   // T1 finding 3
        var garnet = new HighwayGarnetServer(garnetOpts);
        return new HighwayServer(garnet, _opts);
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* spike cleanup */ }
    }

    private static async Task<ConnectionMultiplexer> ConnectAsync(int port, string? user, string? password)
    {
        var config = ConfigurationOptions.Parse($"localhost:{port}");
        config.User = user;
        config.Password = password;
        config.AbortOnConnectFail = true;
        config.AllowAdmin = true;
        return await ConnectionMultiplexer.ConnectAsync(config);
    }

    [Fact]
    public async Task AclSetUser_Works_AndTellsUsWhetherThePasswordReachesTheAof()
    {
        // --- Route (b): create a user at runtime, as an operator would.
        var admin = await ConnectAsync(_port, null, null);
        var setUser = await admin.GetDatabase().ExecuteAsync(
            "ACL", "SETUSER", SpikeUser, "on", $">{SecretPassword}",
            "~*", "+@connection", "+@custom", "+get", "+del", "+subscribe");
        _out.WriteLine($"ACL SETUSER            -> {setUser}");

        // Does the new user actually work?
        string verdict;
        try
        {
            var mux = await ConnectAsync(_port, SpikeUser, SecretPassword);
            var call = await mux.GetDatabase().ExecuteAsync("HW.CALL", "spike.svc", "r-1", "p"u8.ToArray());
            verdict = $"CONNECTED, HW.CALL -> {call}";
            await mux.DisposeAsync();
        }
        catch (Exception ex)
        {
            verdict = $"FAILED ({ex.GetType().Name}: {ex.Message})";
        }
        _out.WriteLine($"connect as new user    -> {verdict}");

        await admin.DisposeAsync();

        // --- The question that decides route (a) vs (b): is the secret on disk?
        _server.Dispose();

        // Garnet stores ACL passwords as SHA-256 hex, so look for the hash as well
        // as the plaintext — and for the username, which is stored either way.
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(SecretPassword))).ToLowerInvariant();

        foreach (var file in Directory.EnumerateFiles(_dataDir, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); } catch { continue; }

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var found = new List<string>();
            if (text.Contains(SecretPassword, StringComparison.Ordinal)) found.Add("PLAINTEXT PASSWORD");
            if (text.Contains(hash, StringComparison.OrdinalIgnoreCase)) found.Add("password hash");
            if (text.Contains(SpikeUser, StringComparison.OrdinalIgnoreCase)) found.Add("username");
            if (text.Contains("SETUSER", StringComparison.OrdinalIgnoreCase)) found.Add("SETUSER");

            _out.WriteLine($"  file {Path.GetRelativePath(_dataDir, file),-46} {bytes.Length,10} bytes  "
                         + (found.Count == 0 ? "-" : string.Join(", ", found)));
        }

        _out.WriteLine($"  acl file now: {File.ReadAllText(_aclFile).ReplaceLineEndings(" | ")}");

        // --- Does the user survive a restart? If not, route (b) is a non-starter:
        //     every restart would silently drop every user but the default.
        _server = Create();
        _server.Start();

        var admin2 = await ConnectAsync(_port, null, null);
        var aclList = await admin2.GetDatabase().ExecuteAsync("ACL", "LIST");
        foreach (var line in (RedisResult[])aclList!)
            _out.WriteLine($"  ACL LIST after restart: {line}");
        await admin2.DisposeAsync();

        string survival;
        try
        {
            var mux = await ConnectAsync(_port, SpikeUser, SecretPassword);
            var whoami = await mux.GetDatabase().ExecuteAsync("ACL", "WHOAMI");
            survival = $"SURVIVED restart (WHOAMI = {whoami})";
            await mux.DisposeAsync();
        }
        catch (Exception ex)
        {
            survival = $"GONE after restart ({ex.GetType().Name})";
        }
        _out.WriteLine($"after restart          -> {survival}");

        // WHOAMI came back as `default` for a user that no longer exists. If a
        // `nopass` default user accepts ANY credential and silently promotes the
        // caller to a +@all account, then an ACL file Highway generates with a
        // nopass default has no authentication at all. That is a design rule, so
        // prove it rather than infer it.
        string garbage;
        try
        {
            var mux = await ConnectAsync(_port, $"no-such-user-{Guid.NewGuid():N}", "totally-wrong-password");
            var who = await mux.GetDatabase().ExecuteAsync("ACL", "WHOAMI");
            var call = await Record(() => mux.GetDatabase().ExecuteAsync("HW.CALL", "spike.svc", "r-x", "p"u8.ToArray()));
            garbage = $"ACCEPTED as '{who}'; HW.CALL -> {(call is null ? "ok" : call)}";
            await mux.DisposeAsync();
        }
        catch (Exception ex)
        {
            garbage = $"refused ({ex.GetType().Name})";
        }
        _out.WriteLine($"garbage credentials    -> {garbage}");

        verdict.Should().StartWith("CONNECTED", "ACL SETUSER should create a usable user");
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
