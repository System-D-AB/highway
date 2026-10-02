using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Highway.Server.Dashboard;
using Highway.Server.Host.Configuration;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Host.Tests;

public class DashboardDeadLetterTests
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
    public async Task DeadLetter_Get_WhenNoApiKey_ReturnsReadOnlyTrue_AndItems()
    {
        var brokerPort = FreePort.Find();
        var dashboardPort = FreePort.Find();
        var configuration = Load($$"""
            {
              "server": {
                "port": {{brokerPort}},
                "ephemeral": true,
                "lease": "00:00:00.050",
                "maxDeliveryAttempts": 1
              },
              "dashboard": { "enabled": true, "port": {{dashboardPort}} }
            }
            """);

        var app = HostFactory.Create(configuration).Build();
        using var cts = new CancellationTokenSource(TestTimeout);
        await app.StartAsync(cts.Token);
        try
        {
            // Seed a dead letter message into queue "test.dlq.orders"
            using var mux = await ConnectionMultiplexer.ConnectAsync($"localhost:{brokerPort}");
            var db = mux.GetDatabase();

            await db.ExecuteAsync("HW.SEND", "test.dlq.orders", "msg-dead-1", "dead-payload-data");
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.orders", "worker-1");
            await Task.Delay(100);
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.orders", "worker-1");
            await Task.Delay(100);
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.orders", "worker-1");

            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters?kind=queue&name=test.dlq.orders", cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadFromJsonAsync<DeadLettersResponseDto>(cancellationToken: cts.Token);
            content.Should().NotBeNull();
            content!.ReadOnly.Should().BeTrue("no api key is configured");
            content.ReadOnlyReason.Should().NotBeNullOrEmpty();
            content.Items.Should().HaveCount(1);

            var item = content.Items[0];
            item.MessageId.Should().Be("msg-dead-1");
            item.Attempts.Should().Be(2);
            item.Requeues.Should().Be(0);
            item.Reason.Should().Be("MAX_ATTEMPTS");
            item.DeadId.Should().HaveLength(24);

            // Mutation without API key configured must return 403 Forbidden
            var requeueRes = await http.PostAsJsonAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters/requeue", new DeadLetterRequeueRequest(
                Kind: "queue",
                Name: "test.dlq.orders",
                DeadLetterId: item.DeadId,
                Count: null,
                AfterMs: null
            ), cts.Token);
            requeueRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var purgeRes = await http.PostAsJsonAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters/purge", new DeadLetterPurgeRequest(
                Kind: "queue",
                Name: "test.dlq.orders",
                DeadLetterId: item.DeadId,
                Count: null
            ), cts.Token);
            purgeRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await app.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task DeadLetter_Mutations_WithApiKey_SucceedAndModifyState()
    {
        var brokerPort = FreePort.Find();
        var dashboardPort = FreePort.Find();
        const string apiKey = "test-dashboard-api-key-12345";

        var configuration = Load($$"""
            {
              "server": {
                "port": {{brokerPort}},
                "ephemeral": true,
                "lease": "00:00:00.050",
                "maxDeliveryAttempts": 1
              },
              "dashboard": {
                "enabled": true,
                "port": {{dashboardPort}},
                "apiKey": "{{apiKey}}"
              }
            }
            """);

        var app = HostFactory.Create(configuration).Build();
        using var cts = new CancellationTokenSource(TestTimeout);
        await app.StartAsync(cts.Token);
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync($"localhost:{brokerPort}");
            var db = mux.GetDatabase();

            await db.ExecuteAsync("HW.SEND", "test.dlq.mut", "msg-mut-1", "payload-1");
            await db.ExecuteAsync("HW.SEND", "test.dlq.mut", "msg-mut-2", "payload-2");
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.mut", "worker-1", "COUNT", "10");
            await Task.Delay(100);
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.mut", "worker-1", "COUNT", "10");
            await Task.Delay(100);
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.mut", "worker-1", "COUNT", "10");

            using var http = new HttpClient();

            // 1. Unauthenticated request to /api/deadletters should return 401
            var unauthRes = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters?kind=queue&name=test.dlq.mut", cts.Token);
            unauthRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // 2. Authenticated request with header X-Highway-Key
            http.DefaultRequestHeaders.Add("X-Highway-Key", apiKey);
            var authRes = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters?kind=queue&name=test.dlq.mut", cts.Token);
            authRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var list = await authRes.Content.ReadFromJsonAsync<DeadLettersResponseDto>(cancellationToken: cts.Token);
            list!.ReadOnly.Should().BeFalse();
            list.ReadOnlyReason.Should().BeNull();
            list.Items.Should().HaveCount(2);

            var first = list.Items.First(i => i.MessageId == "msg-mut-1");
            var second = list.Items.First(i => i.MessageId == "msg-mut-2");

            // 3. Point Requeue on first item
            var requeueRes = await http.PostAsJsonAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters/requeue", new DeadLetterRequeueRequest(
                Kind: "queue",
                Name: "test.dlq.mut",
                DeadLetterId: first.DeadId,
                Count: null,
                AfterMs: null
            ), cts.Token);
            requeueRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var requeueBody = await requeueRes.Content.ReadFromJsonAsync<DeadLetterOperationResponse>(cancellationToken: cts.Token);
            requeueBody!.Success.Should().BeTrue();
            requeueBody.AffectedCount.Should().Be(1);

            // Verify DLQ now only has second item
            var afterReqRes = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters?kind=queue&name=test.dlq.mut", cts.Token);
            var afterList = await afterReqRes.Content.ReadFromJsonAsync<DeadLettersResponseDto>(cancellationToken: cts.Token);
            afterList!.Items.Should().HaveCount(1);
            afterList.Items[0].DeadId.Should().Be(second.DeadId);

            // 4. Point Purge on second item
            var purgeRes = await http.PostAsJsonAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters/purge", new DeadLetterPurgeRequest(
                Kind: "queue",
                Name: "test.dlq.mut",
                DeadLetterId: second.DeadId,
                Count: null
            ), cts.Token);
            purgeRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var purgeBody = await purgeRes.Content.ReadFromJsonAsync<DeadLetterOperationResponse>(cancellationToken: cts.Token);
            purgeBody!.Success.Should().BeTrue();
            purgeBody.AffectedCount.Should().Be(1);

            // DLQ is now empty
            var emptyRes = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters?kind=queue&name=test.dlq.mut", cts.Token);
            var emptyList = await emptyRes.Content.ReadFromJsonAsync<DeadLettersResponseDto>(cancellationToken: cts.Token);
            emptyList!.Items.Should().BeEmpty();
        }
        finally
        {
            await app.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task DeadLetter_BatchRequeueAndBatchPurge_WithApiKey_Succeeds()
    {
        var brokerPort = FreePort.Find();
        var dashboardPort = FreePort.Find();
        const string apiKey = "test-dashboard-api-key-batch";

        var configuration = Load($$"""
            {
              "server": {
                "port": {{brokerPort}},
                "ephemeral": true,
                "lease": "00:00:00.050",
                "maxDeliveryAttempts": 1
              },
              "dashboard": {
                "enabled": true,
                "port": {{dashboardPort}},
                "apiKey": "{{apiKey}}"
              }
            }
            """);

        var app = HostFactory.Create(configuration).Build();
        using var cts = new CancellationTokenSource(TestTimeout);
        await app.StartAsync(cts.Token);
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync($"localhost:{brokerPort}");
            var db = mux.GetDatabase();

            await db.ExecuteAsync("HW.SEND", "test.dlq.batch", "msg-b1", "p1");
            await db.ExecuteAsync("HW.SEND", "test.dlq.batch", "msg-b2", "p2");
            await db.ExecuteAsync("HW.SEND", "test.dlq.batch", "msg-b3", "p3");
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.batch", "worker-1", "COUNT", "10");
            await Task.Delay(100);
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.batch", "worker-1", "COUNT", "10");
            await Task.Delay(100);
            await db.ExecuteAsync("HW.CLAIM", "Q", "test.dlq.batch", "worker-1", "COUNT", "10");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Highway-Key", apiKey);

            var initialRes = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters?kind=queue&name=test.dlq.batch", cts.Token);
            var initialList = await initialRes.Content.ReadFromJsonAsync<DeadLettersResponseDto>(cancellationToken: cts.Token);
            initialList!.Items.Should().HaveCount(3);

            // Batch requeue 2 items
            var reqRes = await http.PostAsJsonAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters/requeue", new DeadLetterRequeueRequest(
                Kind: "queue",
                Name: "test.dlq.batch",
                DeadLetterId: null,
                Count: 2,
                AfterMs: null
            ), cts.Token);
            reqRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var reqBody = await reqRes.Content.ReadFromJsonAsync<DeadLetterOperationResponse>(cancellationToken: cts.Token);
            reqBody!.Success.Should().BeTrue();
            reqBody.AffectedCount.Should().Be(2);

            // 1 remains
            var midRes = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters?kind=queue&name=test.dlq.batch", cts.Token);
            var midList = await midRes.Content.ReadFromJsonAsync<DeadLettersResponseDto>(cancellationToken: cts.Token);
            midList!.Items.Should().HaveCount(1);

            // Batch purge remaining 1 item
            var purgeRes = await http.PostAsJsonAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters/purge", new DeadLetterPurgeRequest(
                Kind: "queue",
                Name: "test.dlq.batch",
                DeadLetterId: null,
                Count: 10
            ), cts.Token);
            purgeRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var purgeBody = await purgeRes.Content.ReadFromJsonAsync<DeadLetterOperationResponse>(cancellationToken: cts.Token);
            purgeBody!.Success.Should().BeTrue();
            purgeBody.AffectedCount.Should().Be(1);

            // DLQ is empty
            var finalRes = await http.GetAsync($"http://127.0.0.1:{dashboardPort}/api/deadletters?kind=queue&name=test.dlq.batch", cts.Token);
            var finalList = await finalRes.Content.ReadFromJsonAsync<DeadLettersResponseDto>(cancellationToken: cts.Token);
            finalList!.Items.Should().BeEmpty();
        }
        finally
        {
            await app.StopAsync(cts.Token);
        }
    }
}
