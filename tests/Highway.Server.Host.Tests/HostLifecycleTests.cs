using FluentAssertions;
using Highway.Server.Host.Configuration;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Host.Tests;

/// <summary>
/// Feature 031 T3 — the Generic Host runs the broker: one binary, started from a
/// configuration, reachable over real TCP, stopped through the host's graceful path.
/// These tests drive the exact host the executable builds (HostFactory), so a break
/// in service-lifetime wiring fails here, not only on a deployed machine.
/// </summary>
public class HostLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static HostConfiguration Load(string json, string? dir = null)
    {
        var path = Path.Combine(dir ?? Path.GetTempPath(), $"highway-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            return ConfigurationLoader.Load(path, environment: new System.Collections.Hashtable()).Configuration;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task TheHost_StartsABrokerFromConfiguration_AndStopsCleanly()
    {
        var port = FreePort.Find();
        var configuration = Load($$"""
            { "server": { "port": {{port}}, "ephemeral": true } }
            """);

        var app = HostFactory.Create(configuration).Build();

        using var cts = new CancellationTokenSource(TestTimeout);
        await app.StartAsync(cts.Token);
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync($"localhost:{port}");
            mux.IsConnected.Should().BeTrue("the packaged broker must accept RESP connections");

            var pong = (string?)await mux.GetDatabase().ExecuteAsync("PING");
            pong.Should().Be("PONG");
        }
        finally
        {
            await app.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task TheHost_ServesTheDashboard_WhenConfigured()
    {
        var brokerPort = FreePort.Find();
        var dashboardPort = FreePort.Find();
        var configuration = Load($$"""
            {
              "server": { "port": {{brokerPort}}, "ephemeral": true },
              "dashboard": { "enabled": true, "port": {{dashboardPort}} }
            }
            """);

        var app = HostFactory.Create(configuration).Build();

        using var cts = new CancellationTokenSource(TestTimeout);
        await app.StartAsync(cts.Token);
        try
        {
            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/", cts.Token);

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
                "the dashboard ships inside the packaged broker");

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            body.Should().NotBeEmpty();
        }
        finally
        {
            await app.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task StoppingTheHost_DisposesTheServer_SoThePortIsReleased()
    {
        var port = FreePort.Find();
        var configuration = Load($$"""
            { "server": { "port": {{port}}, "ephemeral": true } }
            """);

        var app = HostFactory.Create(configuration).Build();

        using var cts = new CancellationTokenSource(TestTimeout);
        await app.StartAsync(cts.Token);
        await app.StopAsync(cts.Token);

        // A disposed Garnet releases its listener: a new server can bind the same port.
        var second = HostFactory.Create(configuration).Build();
        await second.StartAsync(cts.Token);
        await second.StopAsync(cts.Token);
    }

    [Fact]
    public void ConfigDiscovery_FindsTheFileInWorkingDirectoryOrConf()
    {
        // Console-capturing tests force serial execution assembly-wide, so changing
        // the process-wide CWD here cannot race another test.
        var original = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "highways-discover-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(dir);
            Directory.SetCurrentDirectory(dir);

            Program.DiscoverConfigFile().Should().BeNull("nothing exists yet");

            var confDir = Path.Combine(dir, "conf");
            Directory.CreateDirectory(confDir);
            var confPath = Path.Combine(confDir, "highway.json");
            File.WriteAllText(confPath, "{}");

            Program.DiscoverConfigFile().Should().Be(confPath, "conf/highway.json is the distribution layout");

            var cwdPath = Path.Combine(dir, "highway.json");
            File.WriteAllText(cwdPath, "{}");

            Program.DiscoverConfigFile().Should().Be(cwdPath,
                "a file in the working directory beats conf/ — the operator's copy wins");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
