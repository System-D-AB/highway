using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Highway.Server.Host;
using Highway.Server.Host.Configuration;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Host.Tests;

/// <summary>
/// 074 (R9 AC3): a service page shows its requests against their deadlines — what waits, and how requests ended
/// without an answer — since RPC has no dead letters to show.
/// </summary>
public class DashboardServiceRequestsTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ServiceRequests_ReportTheOldestWaitingRequest_AndExpiredAndCancelledCounts()
    {
        var brokerPort = FreePort.Find();
        var dashboardPort = FreePort.Find();
        var path = Path.Combine(Path.GetTempPath(), $"highway-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "server": { "port": {{brokerPort}}, "ephemeral": true },
              "dashboard": { "enabled": true, "port": {{dashboardPort}} }
            }
            """);
        HostConfiguration configuration;
        try { configuration = ConfigurationLoader.Load(path, environment: new System.Collections.Hashtable()).Configuration; }
        finally { File.Delete(path); }

        var app = HostFactory.Create(configuration).Build();
        using var cts = new CancellationTokenSource(TestTimeout);
        await app.StartAsync(cts.Token);
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync($"localhost:{brokerPort}");
            var db = mux.GetDatabase();
            await db.ExecuteAsync("HW.SESSION", "w-1", """{"catalog":{"services":[{"name":"prices"}]}}""");
            await db.ExecuteAsync("HW.CALL", "prices", "soon-gone", "p", "TIMEOUT", "1");
            await db.ExecuteAsync("HW.CALL", "prices", "withdrawn", "p", "TIMEOUT", "60000");
            await db.ExecuteAsync("HW.CANCEL", "prices", "withdrawn");
            await Task.Delay(50, cts.Token);
            await db.ExecuteAsync("HW.CLAIM", "SVC", "prices", "w-1");   // drops the expired one; nothing else is due
            await db.ExecuteAsync("HW.CALL", "prices", "waiting", "p", "TIMEOUT", "60000");

            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/service/prices/requests", cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            json.GetProperty("service").GetBoolean().Should().BeTrue();
            json.GetProperty("queueDepth").GetInt64().Should().Be(1);
            json.GetProperty("oldestRemainingMs").GetInt64().Should().BeInRange(50_000, 60_000);
            json.GetProperty("expired").GetInt64().Should().Be(1);
            json.GetProperty("cancelled").GetInt64().Should().Be(1);

            var unknown = await http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{dashboardPort}/api/service/nope/requests", cts.Token);
            unknown.GetProperty("service").GetBoolean().Should().BeFalse();
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }
}
