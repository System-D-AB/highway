namespace Highway.Integration.Tests;

using System.Text.RegularExpressions;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Client.Caching;
using Highway.Client.Engine;
using Highway.Server;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

public class AclProfileTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Highway.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the repository root (containing Highway.slnx) must be locatable from the test binary");
        return dir!.FullName;
    }

    private static string ReadShippedAclFile()
    {
        var root = FindRepoRoot();
        var aclPath = Path.Combine(root, "config", "users.acl");
        File.Exists(aclPath).Should().BeTrue($"the shipped ACL file must exist at {aclPath}");
        return File.ReadAllText(aclPath);
    }

    private static string ReadProtocolFile()
    {
        var root = FindRepoRoot();
        var protocolPath = Path.Combine(root, "docs", "HIGHWAY-PROTOCOL.md");
        File.Exists(protocolPath).Should().BeTrue($"the protocol file must exist at {protocolPath}");
        return File.ReadAllText(protocolPath);
    }

    [Service("acl.echo")]
    public sealed class AclEchoRequest : IReturn<AclEchoResponse>
    {
        public string Message { get; set; } = "";
    }

    public sealed class AclEchoResponse : Output
    {
        public string Reply { get; set; } = "";
    }

    public sealed class AclEchoService : AsyncService<AclEchoRequest, AclEchoResponse>
    {
        public override Task<AclEchoResponse> ExecuteAsync(AclEchoRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new AclEchoResponse
            {
                StatusCode = 200,
                Reply = $"Echo: {request.Message}"
            });
        }
    }

    [Fact]
    public void ShippedAcl_ContainsEveryProtocolAndCacheCommand()
    {
        var protocolMarkdown = ReadProtocolFile();
        var aclText = ReadShippedAclFile();

        // 1. Extract HW.* commands from protocol Command Index
        var commandIndexStart = protocolMarkdown.IndexOf("## Command Index", StringComparison.Ordinal);
        commandIndexStart.Should().BeGreaterThan(-1);
        var commandIndexEnd = protocolMarkdown.IndexOf("\n## ", commandIndexStart + 1, StringComparison.Ordinal);
        var section = commandIndexEnd > commandIndexStart
            ? protocolMarkdown[commandIndexStart..commandIndexEnd]
            : protocolMarkdown[commandIndexStart..];

        var hwCommands = new List<string>();
        foreach (var line in section.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"^\|\s*`(HW\.[A-Z]+)`\s*\|\s*(-?\d+)\s*\|");
            if (match.Success)
                hwCommands.Add(match.Groups[1].Value.ToLowerInvariant());
        }

        hwCommands.Should().NotBeEmpty();

        // 2. Extract allowed tokens from the default user rule in config/users.acl
        var defaultLine = aclText.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("user default", StringComparison.OrdinalIgnoreCase));
        defaultLine.Should().NotBeNull("config/users.acl must define a rule for 'user default'");

        var tokens = defaultLine!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowedCommands = tokens
            .Where(t => t.StartsWith('+') && !t.StartsWith("+@"))
            .Select(t => t[1..].ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Assert every HW.* command is explicitly granted
        foreach (var cmd in hwCommands)
        {
            allowedCommands.Should().Contain(cmd, $"HW command {cmd} must be explicitly allowlisted in config/users.acl");
        }

        // Assert every HighwayCache string command is explicitly granted
        foreach (var cacheCmd in HighwayCache.SupportedCommands)
        {
            allowedCommands.Should().Contain(cacheCmd.ToLowerInvariant(),
                $"Cache command {cacheCmd} must be explicitly allowlisted in config/users.acl");
        }

        // Assert essential broker / pubsub / liveness commands are granted
        allowedCommands.Should().Contain("ping");
        allowedCommands.Should().Contain("subscribe");
        allowedCommands.Should().Contain("unsubscribe");
        allowedCommands.Should().Contain("scan");
    }

    [Fact]
    public async Task ShippedAcl_AllowsHighwayClient_AndCache_AndStartup()
    {
        var root = FindRepoRoot();
        var aclPath = Path.Combine(root, "config", "users.acl");
        var port = Highway.Server.Internal.EphemeralPort.Probe();

        var server = new HighwayServerBuilder()
            .WithPort(port)
            .WithAclFile(aclPath)
            .Build();

        server.Start();

        try
        {
            var services = new ServiceCollection();
            services.AddHighway(o =>
            {
                o.Server = server.Endpoint;
                o.NodeName = "acl-test-node";
                o.ExcludedAssemblies.Add(a => a == typeof(AclProfileTests).Assembly && false);
            });

            await using var sp = services.BuildServiceProvider();
            var engine = sp.GetRequiredService<IHighwayEngine>();
            await engine.StartAsync();

            try
            {
                var client = sp.GetRequiredService<IHighwayClient>();

                // 1. RPC Call
                var rpcResult = await client.ExecuteAsync(new AclEchoRequest { Message = "hello acl" });
                rpcResult.StatusCode.Should().Be(200);
                rpcResult.Reply.Should().Be("Echo: hello acl");

                // 2. Cache operation
                var cache = sp.GetRequiredService<IDistributedCache>();
                var bytes = "cached-acl-val"u8.ToArray();
                await cache.SetAsync("acl-key-1", bytes, new DistributedCacheEntryOptions());
                var retrieved = await cache.GetAsync("acl-key-1");
                retrieved.Should().BeEquivalentTo(bytes);
            }
            finally
            {
                await engine.StopAsync();
            }
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public void ShippedAcl_RefusesDangerousCommands()
    {
        var root = FindRepoRoot();
        var aclPath = Path.Combine(root, "config", "users.acl");
        var port = Highway.Server.Internal.EphemeralPort.Probe();

        var server = new HighwayServerBuilder()
            .WithPort(port)
            .WithAclFile(aclPath)
            .Build();

        server.Start();

        try
        {
            var muxer = ConnectionMultiplexer.Connect($"localhost:{port}");
            var db = muxer.GetDatabase();

            // 1. FLUSHALL must be refused
            var actFlush = () => db.Execute("FLUSHALL");
            actFlush.Should().Throw<RedisServerException>()
                .WithMessage("*NOPERM*");

            // 2. CONFIG must be refused
            var actConfig = () => db.Execute("CONFIG", "GET", "*");
            actConfig.Should().Throw<RedisServerException>()
                .WithMessage("*NOPERM*");

            // 3. KEYS must be refused
            var actKeys = () => db.Execute("KEYS", "*");
            actKeys.Should().Throw<RedisServerException>()
                .WithMessage("*NOPERM*");
        }
        finally
        {
            server.Dispose();
        }
    }
}
